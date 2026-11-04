using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AssembleAI.Platform.Models;

public sealed class OpenAiModelClient
{
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxLineBytes = 64 * 1024;
    private const int MaxEventBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient client;

    public OpenAiModelClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public async Task<ModelChatResult> CompleteAsync(Uri completionsEndpoint, string model, string? credential, ModelChatRequest request, CancellationToken token, string? reasoningEffort = null)
    {
        using var message = CreateRequest(completionsEndpoint, model, credential, request, stream: false, reasoningEffort);
        using var response = await SendAsync(message, token);
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw TooLarge();
        await using var stream = await OpenStreamAsync(response, token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await ReadAsync(stream, chunk, token)) != 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw TooLarge();
            buffer.Write(chunk, 0, count);
        }
        using var document = ParseJson(buffer.ToArray());
        var root = document.RootElement;
        var choices = GetChoices(root);
        if (choices.GetArrayLength() != 1) throw InvalidResponse();
        var choice = choices[0];
        ValidateChoice(choice);
        var resultMessage = RequiredObject(choice, "message");
        var content = Required(resultMessage, "content");
        if (content.ValueKind != JsonValueKind.String) throw InvalidResponse();
        return new(ReadString(content), OptionalString(choice, "finish_reason"), ReadUsage(root));
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(Uri completionsEndpoint, string model, string? credential, ModelChatRequest request, [EnumeratorCancellation] CancellationToken token, string? reasoningEffort = null)
    {
        using var message = CreateRequest(completionsEndpoint, model, credential, request, stream: true, reasoningEffort);
        using var response = await SendAsync(message, token);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) throw InvalidResponse();
        await using var stream = await OpenStreamAsync(response, token);
        string? finishReason = null;
        await foreach (var data in ReadSseDataAsync(stream, token))
        {
            token.ThrowIfCancellationRequested();
            if (data == "[DONE]")
            {
                yield return new("complete", FinishReason: finishReason);
                yield break;
            }

            using var document = ParseJson(StrictUtf8.GetBytes(data));
            var root = document.RootElement;
            var choices = GetChoices(root);
            var usage = ReadUsage(root);
            if (choices.GetArrayLength() == 0)
            {
                if (usage is null) throw InvalidResponse();
            }
            else
            {
                if (choices.GetArrayLength() != 1) throw InvalidResponse();
                var choice = choices[0];
                ValidateChoice(choice);
                var delta = RequiredObject(choice, "delta");
                var content = OptionalString(delta, "content");
                _ = OptionalString(delta, "role");
                var nextFinishReason = OptionalString(choice, "finish_reason");
                if (finishReason is not null && !string.IsNullOrEmpty(content)) throw InvalidResponse();
                finishReason = nextFinishReason ?? finishReason;
                if (!string.IsNullOrEmpty(content)) yield return new("delta", content);
            }
            if (usage is not null) yield return new("usage", Usage: usage);
        }
        // A clean transport EOF is still an interrupted generation without the protocol marker.
        throw new ModelUpstreamException("upstream_incomplete_stream");
    }

    private static async IAsyncEnumerable<string> ReadSseDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        var data = new StringBuilder();
        var hasData = false;
        var eventBytes = 0;
        await foreach (var line in ReadLinesAsync(stream, token))
        {
            if (line.Length == 0)
            {
                if (hasData) yield return data.ToString();
                data.Clear();
                hasData = false;
                eventBytes = 0;
                continue;
            }
            eventBytes += StrictUtf8.GetByteCount(line) + 1;
            if (eventBytes > MaxEventBytes) throw TooLarge();
            if (line[0] == ':') continue;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            if (field != "data") continue;
            var value = separator < 0 ? "" : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (hasData) data.Append('\n');
            data.Append(value);
            hasData = true;
        }
        // SSE dispatches on the blank line, never on an unterminated event at EOF.
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new byte[4096];
        var line = new byte[MaxLineBytes];
        var length = 0;
        var skipLf = false;
        var firstLine = true;
        int count;
        while ((count = await ReadAsync(stream, buffer, token)) != 0)
        {
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var value = buffer[index];
                if (skipLf)
                {
                    skipLf = false;
                    if (value == '\n') continue;
                }
                if (value is (byte)'\r' or (byte)'\n')
                {
                    skipLf = value == '\r';
                    string decoded;
                    try { decoded = StrictUtf8.GetString(line, 0, length); }
                    catch (DecoderFallbackException) { throw InvalidResponse(); }
                    if (firstLine && decoded.StartsWith('\uFEFF')) decoded = decoded[1..];
                    firstLine = false;
                    length = 0;
                    yield return decoded;
                }
                else
                {
                    if (length == line.Length) throw TooLarge();
                    line[length++] = value;
                }
            }
        }
    }

    private static HttpRequestMessage CreateRequest(Uri endpoint, string model, string? credential, ModelChatRequest request, bool stream, string? reasoningEffort)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = request.Messages.Select(message => new { role = message.Role, content = message.Content }),
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = stream
        };
        if (request.Temperature is { } temperature) payload["temperature"] = temperature;
        if (reasoningEffort is not null) payload["reasoning_effort"] = reasoningEffort;
        if (stream) payload["stream_options"] = new { include_usage = true };
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload))
        };
        message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        message.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json"));
        // The dedicated client has no ambient browser headers or cookie store. Credentials
        // belong to this request only so concurrent configurations cannot share a secret.
        if (!string.IsNullOrEmpty(credential))
        {
            try { message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential); }
            catch (FormatException)
            {
                message.Dispose();
                throw new ModelUpstreamException("upstream_unavailable");
            }
        }
        return message;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    {
        HttpResponseMessage response;
        try { response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new ModelUpstreamException("upstream_unavailable");
        }
        if (response.IsSuccessStatusCode) return response;
        // Never consume or attach an upstream error body, URL or exception to a public error.
        response.Dispose();
        throw new ModelUpstreamException("upstream_http_error");
    }

    private static async Task<Stream> OpenStreamAsync(HttpResponseMessage response, CancellationToken token)
    {
        try { return await response.Content.ReadAsStreamAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new ModelUpstreamException("upstream_unavailable");
        }
    }

    private static async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        try { return await stream.ReadAsync(buffer, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new ModelUpstreamException("upstream_unavailable");
        }
    }

    private static JsonDocument ParseJson(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            // Validate all bytes, including unknown properties that JsonDocument might
            // otherwise never decode. Malformed UTF-8 is not repaired silently.
            StrictUtf8.GetCharCount(bytes.Span);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw InvalidResponse(); }
    }

    private static JsonElement GetChoices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || TryGetProperty(root, "error", out _)) throw InvalidResponse();
        var choices = Required(root, "choices");
        if (choices.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        return choices;
    }

    private static void ValidateChoice(JsonElement choice)
    {
        if (choice.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (TryGetProperty(choice, "index", out var index)
            && (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var value) || value != 0)) throw InvalidResponse();
    }

    private static JsonElement Required(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !TryGetProperty(element, name, out var value)) throw InvalidResponse();
        return value;
    }

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        return value;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        return ReadString(value);
    }

    private static string ReadString(JsonElement value)
    {
        // JsonDocument validates escape syntax eagerly but decodes surrogate pairs lazily.
        try { return value.GetString()!; }
        catch (InvalidOperationException) { throw InvalidResponse(); }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        // Escaped property names are also decoded lazily, including names we skip.
        try { return element.TryGetProperty(name, out value); }
        catch (InvalidOperationException) { throw InvalidResponse(); }
    }

    private static ModelUsage? ReadUsage(JsonElement root)
    {
        if (!TryGetProperty(root, "usage", out var usage) || usage.ValueKind == JsonValueKind.Null) return null;
        return new(ReadTokenCount(usage, "prompt_tokens"), ReadTokenCount(usage, "completion_tokens"), ReadTokenCount(usage, "total_tokens"));
    }

    private static long ReadTokenCount(JsonElement usage, string name)
    {
        var value = Required(usage, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0) throw InvalidResponse();
        return count;
    }

    private static ModelUpstreamException InvalidResponse() => new("upstream_invalid_response");
    private static ModelUpstreamException TooLarge() => new("upstream_response_too_large");
}
