using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class RegistryBoundaryTests
{
    public static async Task RunAsync()
    {
        await using var identity = await OidcFixture.StartAsync();
        var origin = FreeOrigin();
        var state = new State();
        var store = new Commands();
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", origin,
            "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
            "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
            "--Authentication:Bff:PublicOrigin", origin
        ], builder => {
            builder.Logging.ClearProviders(); builder.Services.AddSingleton<IRegistryStore>(store);
            builder.Services.AddSingleton<IStartupFilter>(new BodyGateFilter(state));
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(12) };
            async Task<string> Login()
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                using var code = await client.GetAsync(challenge.Headers.Location);
                using var callback = await client.GetAsync(code.Headers.Location);
                Check(callback.StatusCode == HttpStatusCode.Redirect, "Fixture must exchange real OIDC code.");
                using var response = await client.GetAsync("/bff/csrf");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return json.RootElement.GetProperty("requestToken").GetString()!;
            }
            async Task<HttpResponseMessage> Write(string csrf, string path = "/api/platform/v1/registry/apps/production-reports/releases/1.0.0", bool logout = false)
            {
                using var request = new HttpRequestMessage(logout ? HttpMethod.Post : HttpMethod.Put, path);
                request.Headers.Add("Origin", origin); request.Headers.Add("X-CSRF-TOKEN", csrf);
                if (!logout) request.Content = new StringContent(JsonSerializer.Serialize(new {
                    expectedRevision = 0, manifest = ManifestTests.Valid()
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
                return await client.SendAsync(request);
            }
            var csrf = await Login();
            using (var saved = await Write(csrf))
            {
                Check(saved.StatusCode == HttpStatusCode.OK && store.Writes == 1,
                    "Authenticated global commands can work without a selected tenant.");
                Check(saved.Headers.CacheControl?.NoStore == true && saved.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff",
                    "Registry boundary must prohibit caching and sniffing.");
            }
            var gate = state.Next = new();
            var pending = Write(csrf);
            await gate.Reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using (var loggedOut = await Write(csrf, "/bff/logout", logout: true))
                Check(loggedOut.IsSuccessStatusCode, "Logout must commit while command parsing waits.");
            gate.Release.TrySetResult();
            using (var rejected = await pending)
                Check(rejected.StatusCode == HttpStatusCode.Forbidden && store.Writes == 1,
                    "A cached Cookie identity must not write after logout during body parsing.");
            csrf = await Login();
            var deadline = state.Next = new();
            var slow = Write(csrf);
            await deadline.Reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using (var unavailable = await slow)
                Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable && store.Writes == 1,
                    "A stalled command body must exhaust its budget before entering the store.");
            deadline.Release.TrySetResult();
            using (var malformed = await Write(csrf, "/api/platform/v1/registry/apps/production-reports/releases/1.0.0?override=true"))
                Check(malformed.StatusCode == HttpStatusCode.BadRequest && store.Writes == 1, "Undeclared command query cannot be accepted.");
            using (var saved = await Write(csrf)) Check(saved.StatusCode == HttpStatusCode.OK && store.Writes == 2,
                "A failed request does not poison subsequent valid commands.");
            Console.WriteLine("PASS: registry live-session boundary: global without tenant, logout during body parsing, shared deadline, safe headers and recovery.");
        }
        finally { state.Next?.Release.TrySetResult(); await app.StopAsync(); }
    }
    private static string FreeOrigin()
    { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Gate
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class State { public Gate? Next; }
    private sealed class BodyGateFilter(State state) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use(async (context, continuation) => {
                var body = context.Request.Body;
                if (context.Request.Method == "PUT" && state.Next is { } gate)
                { state.Next = null; context.Request.Body = new GateStream(body, gate); }
                try { await continuation(context); } finally { context.Request.Body = body; }
            });
            next(app);
        };
    }
    private sealed class GateStream(Stream body, Gate gate) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { gate.Reading.TrySetResult(); await gate.Release.Task.WaitAsync(token); return await body.ReadAsync(buffer, token); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    // This HTTP boundary test counts command admission only. SQL authorization,
    // transaction and audit behavior is independently exercised by real PG tests.
    private sealed class Commands : IRegistryStore
    {
        public int Writes;
        public Task<RegistryResult<ApplicationReleaseView>> SaveDraftAsync(SubjectIdentity actor, ApplicationManifest manifest, long expectedRevision, string requestId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Writes++; return Task.FromResult(RegistryResult<ApplicationReleaseView>.Success(new(manifest, 1, Guid.NewGuid(), null, null))); }
        public Task<RegistryResult<ApplicationReleaseView>> TransitionAsync(SubjectIdentity actor, string appId, string version, string action, long expectedRevision, string requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RegistryResult<ApplicationReleaseView>> ReadReleaseAsync(SubjectIdentity actor, string appId, string version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RegistryResult<IReadOnlyList<ApplicationReleaseView>>> ListReleasesAsync(SubjectIdentity actor, string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RegistryResult<TenantApplicationView>> ReadTenantApplicationAsync(SubjectIdentity actor, Guid tenantId, string appId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RegistryResult<TenantApplicationView>> SetTenantApplicationAsync(SubjectIdentity actor, Guid tenantId, string appId, string releaseVersion, bool enabled, long expectedRevision, string requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
