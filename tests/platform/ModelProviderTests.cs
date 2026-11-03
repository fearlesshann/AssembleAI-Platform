using System.Net;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.SmokeTests;

public static class ModelProviderTests
{
    private static readonly Uri Endpoint = new("https://provider.invalid/private/chat/completions?deployment=test");
    private static readonly ModelChatRequest Request = new([new("system", "Be concise."), new("user", "你好")], 128, 0.25, false);
    private const string Completion = """{"id":"local-response","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"你好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":2,"total_tokens":11}}""";
    private const string Delta = """{"choices":[{"index":0,"delta":{"content":"你好🌏"},"finish_reason":null}],"usage":null}""";
    private const string Finish = """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}""";
    private const string Usage = """{"choices":[],"usage":{"prompt_tokens":9,"completion_tokens":2,"total_tokens":11}}""";

    public static async Task RunAsync()
    {
        await VerifyRequestMappingAndCredentialIsolation();
        await VerifyOptionalUsage();
        await VerifySafeCompletionFailures();
        await VerifyIncrementalStreaming();
        await VerifyStreamingWithoutUsage();
        await VerifyMalformedAndOversizedStreams();
        await VerifyCancellationAndDisposal();
        Console.WriteLine("PASS: model provider: exact endpoint, isolated credentials, optional usage, incremental UTF-8 SSE, bounded parsing, safe failures and cancellation.");
    }

    private static async Task VerifyRequestMappingAndCredentialIsolation()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert(request.Method == HttpMethod.Post && request.RequestUri == Endpoint, "The configured full completion URL must be used verbatim.");
            Assert(!request.Headers.Contains("Cookie") && !request.Headers.Contains("X-Internal-User")
                && !request.Headers.Contains("X-Tenant-Id"), "Provider requests must not include browser identity headers.");
            Assert(request.Headers.Authorization?.ToString() == (calls++ == 0 ? "Bearer provider-secret" : null), "Only the configured provider credential may be sent; it must not persist across calls.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            Assert(root.GetProperty("model").GetString() == "local-test-model", "The configured provider model must be mapped.");
            Assert(root.GetProperty("max_tokens").GetInt32() == 128 && root.GetProperty("temperature").GetDouble() == 0.25
                && !root.GetProperty("stream").GetBoolean(), "Generation limits must be mapped to OpenAI-compatible fields.");
            Assert(root.GetProperty("messages")[0].GetProperty("role").GetString() == "system"
                && root.GetProperty("messages")[1].GetProperty("content").GetString() == "你好", "Messages must retain their roles and Unicode content.");
            Assert(!root.TryGetProperty("stream_options", out _), "Non-streaming requests must omit streaming options.");
            return JsonResponse(Completion);
        }));
        var client = new OpenAiModelClient(http);
        var result = await client.CompleteAsync(Endpoint, "local-test-model", "provider-secret", Request, CancellationToken.None);
        Assert(result == new ModelChatResult("你好", "stop", new(9, 2, 11)), "Text, finish reason and measured usage must be mapped.");
        await client.CompleteAsync(Endpoint, "local-test-model", null, Request, CancellationToken.None);
        Assert(calls == 2, "Each completion must make exactly one upstream request.");
    }

    private static async Task VerifyOptionalUsage()
    {
        foreach (var suffix in new[] { "", ",\"usage\":null" })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]" + suffix + "}"))));
            var result = await new OpenAiModelClient(http).CompleteAsync(Endpoint, "local", null, Request with { Temperature = null }, CancellationToken.None);
            Assert(result.Content == "ok" && result.Usage is null && result.FinishReason is null, "Absent provider measurements must remain unknown, including nullable finish reason.");
        }
        using var measuredZero = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse(Completion.Replace("\"prompt_tokens\":9,\"completion_tokens\":2,\"total_tokens\":11", "\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0", StringComparison.Ordinal)))));
        var zero = await new OpenAiModelClient(measuredZero).CompleteAsync(Endpoint, "local", null, Request, CancellationToken.None);
        Assert(zero.Usage == new ModelUsage(0, 0, 0), "Actual provider-reported zero usage must remain distinguishable from unknown usage.");
    }

    private static async Task VerifySafeCompletionFailures()
    {
        foreach (var body in new[]
        {
            "provider-secret private prompt is not JSON", "{}", "{\"choices\":[]}",
            "{\"choices\":[{\"message\":{\"content\":\"\\uD800\"}}]}",
            "{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"\\uD800\":1}",
            "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"\\uD800\":1}]}",
            """{"choices":[{"message":{"content":42},"finish_reason":"stop"}]}""",
            """{"choices":[{"message":{"content":"ok"},"finish_reason":42}]}""",
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":1}}""",
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":-1,"completion_tokens":2,"total_tokens":1}}"""
        })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse(body))));
            await ExpectSafeFailure(() => new OpenAiModelClient(http).CompleteAsync(Endpoint, "local", "provider-secret", Request, CancellationToken.None), "upstream_invalid_response");
        }
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse(new string('x', 1024 * 1024 + 1))))))
            await ExpectSafeFailure(() => new OpenAiModelClient(http).CompleteAsync(Endpoint, "local", null, Request, CancellationToken.None), "upstream_response_too_large");
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.TemporaryRedirect })
        {
            var calls = 0;
            using var http = new HttpClient(new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("provider-secret private prompt") });
            }));
            await ExpectSafeFailure(() => new OpenAiModelClient(http).CompleteAsync(Endpoint, "local", null, Request, CancellationToken.None), "upstream_http_error");
            Assert(calls == 1, "Provider failures must not be retried.");
        }
        using (var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("provider-secret at " + Endpoint))))
            await ExpectSafeFailure(() => new OpenAiModelClient(http).CompleteAsync(Endpoint, "local", null, Request, CancellationToken.None), "upstream_unavailable");
    }

    private static async Task VerifyIncrementalStreaming()
    {
        var prefix = "\uFEFF: heartbeat\r\nid: ignored\r\ndata: " + Delta + "\r\n\r\n";
        var suffix = "data: " + Finish + "\n\n" + "data: " + Usage + "\r\rdata: [DONE]\n\n";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new SegmentedStream(Encoding.UTF8.GetBytes(prefix + suffix), 1, Encoding.UTF8.GetByteCount(prefix), release.Task);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert(body.RootElement.GetProperty("stream").GetBoolean()
                && body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(), "Streaming requests must request measured final usage.");
            Assert(request.Headers.Accept.Any(value => value.MediaType == "text/event-stream"), "Streaming must request SSE.");
            return SseResponse(stream);
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = new OpenAiModelClient(http).StreamAsync(Endpoint, "local", null, Request with { Stream = true }, timeout.Token).GetAsyncEnumerator();
        Assert(await events.MoveNextAsync() && events.Current == new ModelStreamEvent("delta", "你好🌏"), "Split UTF-8 content must reach the caller before the provider finishes.");
        Assert(!release.Task.IsCompleted, "The first delta must be available while the remaining provider bytes are gated.");
        release.SetResult();
        Assert(await events.MoveNextAsync() && events.Current == new ModelStreamEvent("usage", Usage: new(9, 2, 11)), "Usage-only chunks must preserve actual token counts.");
        Assert(await events.MoveNextAsync() && events.Current == new ModelStreamEvent("complete", FinishReason: "stop"), "A complete event is emitted only after [DONE].");
        Assert(!await events.MoveNextAsync(), "Exactly one completion event must terminate the stream.");
        Assert(stream.Disposed, "Provider response content must be disposed when streaming ends.");
    }

    private static async Task VerifyStreamingWithoutUsage()
    {
        // Multiple data fields form one JSON event; null usage is not a zero-token measurement.
        var wire = "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}],\n"
            + "data: \"usage\":null}\n\n" + "data: " + Delta + "\n\ndata: " + Finish + "\n\ndata: [DONE]\n\n";
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(SseResponse(new SegmentedStream(Encoding.UTF8.GetBytes(wire), 7)))));
        var events = await Collect(new OpenAiModelClient(http), CancellationToken.None);
        Assert(events.Count == 2 && events[0].Type == "delta" && events[1].Type == "complete"
            && events.All(value => value.Usage is null), "Streams without usage must not invent usage events or numbers.");
    }

    private static async Task VerifyMalformedAndOversizedStreams()
    {
        var fixtures = new (string Body, string Code)[]
        {
            ("data: provider-secret private prompt\n\n", "upstream_invalid_response"),
            ("data: {}\n\n", "upstream_invalid_response"),
            ("data: {\"choices\":[]}\n\n", "upstream_invalid_response"),
            ("data: {\"choices\":[{\"delta\":{\"content\":42}}]}\n\n", "upstream_invalid_response"),
            ("data: {\"choices\":[{\"delta\":{\"content\":\"\\uD800\"}}]}\n\n", "upstream_invalid_response"),
            ("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}],\"\\uD800\":1}\n\n", "upstream_invalid_response"),
            ("data: {\"choices\":[{\"delta\":{\"content\":\"ok\",\"\\uD800\":1}}]}\n\n", "upstream_invalid_response"),
            ("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1}}\n\n", "upstream_invalid_response"),
            ("data: " + Delta + "\n\ndata: " + Finish + "\n\n", "upstream_incomplete_stream"),
            ("data: [DONE]", "upstream_incomplete_stream"),
            ("data: " + new string('x', 70_000) + "\n\n", "upstream_response_too_large"),
            (string.Concat(Enumerable.Repeat("data: " + new string('x', 60_000) + "\n", 5)) + "\n", "upstream_response_too_large")
        };
        foreach (var fixture in fixtures)
        {
            var stream = new SegmentedStream(Encoding.UTF8.GetBytes(fixture.Body), 17);
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(SseResponse(stream))));
            await ExpectSafeFailure(() => Collect(new OpenAiModelClient(http), CancellationToken.None), fixture.Code);
            Assert(stream.Disposed, "Malformed or interrupted provider streams must be disposed.");
        }
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(SseResponse(new SegmentedStream([100, 97, 116, 97, 58, 32, 0xff, 10, 10], 1))))))
            await ExpectSafeFailure(() => Collect(new OpenAiModelClient(http), CancellationToken.None), "upstream_invalid_response");
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse(Completion)))))
            await ExpectSafeFailure(() => Collect(new OpenAiModelClient(http), CancellationToken.None), "upstream_invalid_response");
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("provider-secret private prompt") }))))
            await ExpectSafeFailure(() => Collect(new OpenAiModelClient(http), CancellationToken.None), "upstream_http_error");
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(SseResponse(new FaultingStream())))))
            await ExpectSafeFailure(() => Collect(new OpenAiModelClient(http), CancellationToken.None), "upstream_unavailable");
    }

    private static async Task VerifyCancellationAndDisposal()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using (var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Canceled calls must not reach this branch.");
        })))
            await ExpectCanceled(() => new OpenAiModelClient(http).CompleteAsync(Endpoint, "local", null, Request, canceled.Token));

        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new SegmentedStream(Encoding.UTF8.GetBytes("data: " + Delta + "\n\n"), 4, 0, blocked.Task);
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(SseResponse(stream)))))
        using (var cancellation = new CancellationTokenSource())
        {
            var result = Collect(new OpenAiModelClient(http), cancellation.Token);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await ExpectCanceled(() => result);
            Assert(stream.Disposed, "Cancellation must close the provider response promptly.");
        }

        var earlyStream = new SegmentedStream(Encoding.UTF8.GetBytes("data: " + Delta + "\n\ndata: [DONE]\n\n"), 3);
        using var earlyHttp = new HttpClient(new Handler((_, _) => Task.FromResult(SseResponse(earlyStream))));
        await using (var events = new OpenAiModelClient(earlyHttp).StreamAsync(Endpoint, "local", null, Request with { Stream = true }, CancellationToken.None).GetAsyncEnumerator())
            Assert(await events.MoveNextAsync(), "Early-disposal fixture must first produce a delta.");
        Assert(earlyStream.Disposed, "A consumer ending enumeration early must close the upstream stream.");
    }

    private static async Task<List<ModelStreamEvent>> Collect(OpenAiModelClient client, CancellationToken token)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in client.StreamAsync(Endpoint, "local", "provider-secret", Request with { Stream = true }, token)) events.Add(item);
        return events;
    }

    private static async Task ExpectSafeFailure(Func<Task> action, string code)
    {
        try { await action(); }
        catch (ModelUpstreamException error)
        {
            Assert(error.Code == code, $"Expected safe failure {code}; got {error.Code}.");
            Assert(!error.ToString().Contains("provider-secret", StringComparison.Ordinal)
                && !error.ToString().Contains("private prompt", StringComparison.Ordinal)
                && !error.ToString().Contains("provider.invalid", StringComparison.Ordinal)
                && error.InnerException is null, "Provider errors must not expose body text, endpoint, credentials or raw nested errors.");
            return;
        }
        throw new InvalidOperationException("An invalid provider response was accepted.");
    }

    private static async Task ExpectCanceled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancellation must remain cancellation rather than an upstream error.");
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage SseResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private sealed class FaultingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("provider-secret private prompt at https://provider.invalid");
    }
    private sealed class SegmentedStream(byte[] bytes, int maxRead, int? gateOffset = null, Task? release = null) : Stream
    {
        private int position;
        public bool Disposed { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            if (gateOffset is { } gate && position >= gate && release is not null) await release.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(maxRead, buffer.Length), bytes.Length - position);
            if (gateOffset is { } boundary && position < boundary) count = Math.Min(count, boundary - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
