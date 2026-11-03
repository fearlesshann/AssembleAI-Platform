using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>
/// Real PostgreSQL contracts. Only the explicit integration dispatcher may call
/// this after validating its isolated database marker. The launcher owns cleanup.
/// </summary>
public static class PostgresSessionTests
{
    public static async Task RunAsync(string connectionString, string runDirectory)
    {
        var directory = CreateKeyDirectory(runDirectory);
        var pfxPath = Path.Combine(directory, "primary.pfx");
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using (var generated = Certificate())
            File.WriteAllBytes(pfxPath, generated.Export(X509ContentType.Pfx, password));
        using var certificate = LoadCertificate(pfxPath, password);
        var keyRing = Path.Combine(directory, "keyring");
        var configuration = Configuration(connectionString);
        var clock = new SessionClock();
        var subject = "postgres-subject-" + Guid.NewGuid().ToString("N");
        var accessToken = "postgres-access-" + Guid.NewGuid().ToString("N");
        var refreshToken = "postgres-refresh-" + Guid.NewGuid().ToString("N");
        var ticket = Ticket(clock, subject, accessToken, refreshToken);
        string reference;
        byte[] protectedBytes;

        using (var firstServices = Services(keyRing, certificate))
        {
            var first = Store(configuration, firstServices, clock);
            reference = await first.StoreAsync(ticket);
            var stored = await ReadRow(connectionString, reference);
            protectedBytes = stored.Bytes;
            Assert(reference.Length == 43, "PostgreSQL session references must retain the 32-byte random key format.");
            Assert(stored.Expires == ticket.Properties.ExpiresUtc && stored.Expires.Offset == TimeSpan.Zero,
                "Stored timestamptz expiry must match the ticket's UTC deadline.");
            foreach (var secret in new[] { subject, accessToken, refreshToken })
                Assert(protectedBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0,
                    "PostgreSQL must store encrypted ticket contents, without plaintext identity or OIDC tokens.");
            ticket.Properties.UpdateTokenValue("access_token", "changed-after-write");
            Assert((await first.RetrieveAsync(reference))?.Properties.GetTokenValue("access_token") == accessToken,
                "Store must retain a serialized copy of its caller's ticket.");
        }

        // The first provider is disposed. Recover through a newly loaded PFX,
        // independently constructed provider and fresh database connection.
        using var reloadedCertificate = LoadCertificate(pfxPath, password);
        using var restartedServices = Services(keyRing, reloadedCertificate);
        var restarted = Store(configuration, restartedServices, clock);
        var recovered = await restarted.RetrieveAsync(reference);
        Assert(recovered?.Principal.FindFirst("sub")?.Value == subject
            && recovered.Properties.GetTokenValue("access_token") == accessToken
            && recovered.Properties.GetTokenValue("refresh_token") == refreshToken,
            "A new store and persisted key provider must recover the original server-side identity and tokens.");
        recovered!.Properties.UpdateTokenValue("access_token", "changed-after-read");
        Assert((await restarted.RetrieveAsync(reference))?.Properties.GetTokenValue("access_token") == accessToken,
            "Reading a ticket must not expose mutable stored state.");

        await VerifyCiphertextBinding(connectionString, configuration, clock, restarted, reference,
            protectedBytes, directory, keyRing, certificate);
        await VerifyExpiryAndRenewal(connectionString, configuration, restartedServices);
        await VerifyConcurrentRevocation(configuration, restartedServices);
        await VerifyCancellation(restarted, clock);
        await VerifyConnectionFailure(connectionString, restartedServices, clock);
        Console.WriteLine("PASS: PostgreSQL BFF sessions: encrypted tickets, independent key-provider recovery, tamper rejection, UTC expiry, revoke/renew concurrency, cancellation and safe connection failure.");
    }

    private static async Task VerifyCiphertextBinding(string connectionString, IConfiguration configuration,
        SessionClock clock, PostgresTicketStore store, string reference, byte[] original,
        string directory, string keyRing, X509Certificate2 certificate)
    {
        using (var unrelated = Services(Path.Combine(directory, "unrelated-keyring"), certificate))
            Assert(await Store(configuration, unrelated, clock).RetrieveAsync(reference) is null,
                "A provider without the original key ring must reject the stored ticket.");
        var copiedRing = Path.Combine(directory, "wrong-certificate-keyring");
        Directory.CreateDirectory(copiedRing);
        foreach (var source in Directory.GetFiles(keyRing, "key-*.xml"))
            File.Copy(source, Path.Combine(copiedRing, Path.GetFileName(source)));
        using (var wrongCertificate = Certificate())
        using (var wrongServices = Services(copiedRing, wrongCertificate))
            Assert(await Store(configuration, wrongServices, clock).RetrieveAsync(reference) is null,
                "A different private certificate must not recover the original encrypted key ring.");

        var otherReference = await store.StoreAsync(Ticket(clock, "different-subject"));
        var other = await ReadRow(connectionString, otherReference);
        await WriteBytes(connectionString, reference, other.Bytes);
        Assert(await store.RetrieveAsync(reference) is null,
            "Copying a valid ciphertext into a different session reference must fail authentication.");
        var damaged = original.ToArray();
        damaged[^1] ^= 1;
        await WriteBytes(connectionString, reference, damaged);
        Assert(await store.RetrieveAsync(reference) is null, "A modified ticket ciphertext must be rejected.");
        await WriteBytes(connectionString, reference, original);
        Assert(await store.RetrieveAsync(reference) is not null, "Restoring the original ciphertext must recover the original ticket.");
        await store.RemoveAsync(reference);
        await store.RenewAsync(reference, Ticket(clock));
        using var anotherServices = Services(keyRing, certificate);
        Assert(await Store(configuration, anotherServices, clock).RetrieveAsync(reference) is null,
            "A new independent store must observe revocation, and Renew must not recreate the deleted row.");
    }

    private static async Task VerifyExpiryAndRenewal(string connectionString, IConfiguration configuration, ServiceProvider services)
    {
        var clock = new SessionClock();
        var store = Store(configuration, services, clock);
        var input = Ticket(clock);
        input.Properties.ExpiresUtc = null;
        var key = await store.StoreAsync(input);
        var deadline = clock.GetUtcNow().AddHours(8).AddMilliseconds(-750);
        Assert((await ReadRow(connectionString, key)).Expires == deadline
            && (await store.RetrieveAsync(key))?.Properties.ExpiresUtc == deadline,
            "Missing expiry must be capped at eight hours and use the serialized whole-second deadline.");
        var excessive = await store.StoreAsync(Ticket(clock, expires: clock.GetUtcNow().AddDays(30)));
        Assert((await ReadRow(connectionString, excessive)).Expires == deadline,
            "PostgreSQL must cap excessive ticket lifetimes at eight hours.");
        clock.Advance(TimeSpan.FromHours(7));
        var renewed = Ticket(clock, accessToken: "renewed-server-token", expires: clock.GetUtcNow().AddHours(8));
        await store.RenewAsync(key, renewed);
        Assert((await ReadRow(connectionString, key)).Expires == deadline
            && (await store.RetrieveAsync(key))?.Properties.GetTokenValue("access_token") == "renewed-server-token",
            "Renew must update the encrypted ticket while preserving the original database deadline.");
        clock.Advance(deadline - clock.GetUtcNow());
        Assert(await store.RetrieveAsync(key) is null && await store.RetrieveAsync(excessive) is null,
            "A persisted session must stop authenticating exactly at its serialized deadline.");
        await store.RenewAsync(key, Ticket(clock));
        Assert(await store.RetrieveAsync(key) is null, "Renew cannot resurrect a database-expired session.");

        var forcedExpiry = await store.StoreAsync(Ticket(clock));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE public.platform_bff_sessions SET expires_at = $2 WHERE session_key = $1", connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, forcedExpiry);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, clock.GetUtcNow().AddSeconds(-1));
        await command.ExecuteNonQueryAsync();
        Assert(await store.RetrieveAsync(forcedExpiry) is null,
            "An earlier database expiry must override a later deadline inside a protected ticket.");
        await store.RenewAsync(forcedExpiry, Ticket(clock));
        Assert(await store.RetrieveAsync(forcedExpiry) is null, "Renew must not undo an explicit database expiry.");
    }

    private static async Task VerifyConcurrentRevocation(IConfiguration configuration, ServiceProvider services)
    {
        var clock = new SessionClock();
        var first = Store(configuration, services, clock);
        var second = Store(configuration, services, clock);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var key = await first.StoreAsync(Ticket(clock));
            await Task.WhenAll(first.RemoveAsync(key), second.RenewAsync(key, Ticket(clock)));
            await second.RenewAsync(key, Ticket(clock));
            Assert(await first.RetrieveAsync(key) is null && await second.RetrieveAsync(key) is null,
                "Independent database connections must not renew a concurrently revoked session back into existence.");
        }
    }

    private static async Task VerifyCancellation(PostgresTicketStore store, SessionClock clock)
    {
        var key = await store.StoreAsync(Ticket(clock));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext();
        ITicketStore contract = store;
        foreach (var action in new Func<Task>[]
        {
            () => contract.StoreAsync(Ticket(clock), context, cancellation.Token),
            () => contract.RetrieveAsync(key, context, cancellation.Token),
            () => contract.RenewAsync(key, Ticket(clock), context, cancellation.Token),
            () => contract.RemoveAsync(key, context, cancellation.Token)
        })
        {
            var cancelled = false;
            try { await action(); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "PostgreSQL ticket operations must preserve caller cancellation.");
        }
        Assert(await store.RetrieveAsync(key) is not null, "Cancelled removal must leave the existing database session intact.");
    }

    private static async Task VerifyConnectionFailure(string connectionString, ServiceProvider services, SessionClock clock)
    {
        // Reserve, but do not listen on, a loopback endpoint. This is a genuine
        // refused connection without changing or stopping the running database.
        using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var secret = "must-not-leak-" + Guid.NewGuid().ToString("N");
        var invalid = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)unavailable.LocalEndPoint!).Port,
            Password = secret, Pooling = false
        };
        var store = Store(Configuration(invalid.ConnectionString), services, clock);
        try { await store.StoreAsync(Ticket(clock)); }
        catch (BffSessionStoreException exception)
        {
            Assert(exception.InnerException is null && !exception.ToString().Contains(secret, StringComparison.Ordinal)
                && !exception.ToString().Contains(invalid.ConnectionString, StringComparison.Ordinal),
                "Real connection failures must not disclose database credentials or provider exceptions.");
            return;
        }
        throw new InvalidOperationException("A refused database connection must fail closed with BffSessionStoreException.");
    }

    private static PostgresTicketStore Store(IConfiguration configuration, ServiceProvider services, TimeProvider clock) =>
        new(configuration, services.GetRequiredService<IDataProtectionProvider>(), clock);

    private static IConfiguration Configuration(string connectionString) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Platform"] = connectionString }).Build();

    private static ServiceProvider Services(string keyRing, X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("AssembleAI.Platform")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRing)).ProtectKeysWithCertificate(certificate);
        return services.BuildServiceProvider();
    }

    private static AuthenticationTicket Ticket(SessionClock clock, string subject = "session-fixture",
        string accessToken = "access-fixture", string refreshToken = "refresh-fixture", DateTimeOffset? expires = null)
    {
        var properties = new AuthenticationProperties { IssuedUtc = clock.GetUtcNow(), ExpiresUtc = expires ?? clock.GetUtcNow().AddHours(1) };
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = accessToken },
            new AuthenticationToken { Name = "refresh_token", Value = refreshToken }]);
        return new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", "https://postgres-session.example.test"), new Claim("sub", subject)
        ], "test-only")), properties, "test-only");
    }

    private static async Task<(byte[] Bytes, DateTimeOffset Expires)> ReadRow(string connectionString, string reference)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT protected_ticket, expires_at FROM public.platform_bff_sessions WHERE session_key = $1", connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, reference);
        await using var reader = await command.ExecuteReaderAsync();
        Assert(await reader.ReadAsync(), "The newly stored session must exist in the real database.");
        return (reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static async Task WriteBytes(string connectionString, string reference, byte[] bytes)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE public.platform_bff_sessions SET protected_ticket = $2 WHERE session_key = $1", connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, reference);
        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, bytes);
        Assert(await command.ExecuteNonQueryAsync() == 1, "Tampering tests must change exactly their own session row.");
    }

    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=AssembleAI PostgreSQL session test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 LoadCertificate(string path, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);

    private static string CreateKeyDirectory(string runDirectory)
    {
        var run = Path.GetFullPath(runDirectory);
        DirectoryInfo? workspace = new(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AssembleAI.slnx"))) workspace = workspace.Parent;
        Assert(workspace is not null, "PostgreSQL integration tests must run within the workspace.");
        var allowed = Path.Combine(workspace!.FullName, ".local-data") + Path.DirectorySeparatorChar;
        Assert(run.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(run),
            "Test key material must stay inside the launcher's existing local run directory.");
        for (var item = new DirectoryInfo(run); item is not null; item = item.Parent)
        {
            Assert((item.Attributes & FileAttributes.ReparsePoint) == 0, "Test key paths must not contain symbolic links or junctions.");
            if (string.Equals(item.FullName, workspace.FullName, StringComparison.OrdinalIgnoreCase)) break;
        }
        var directory = Path.Combine(run, "session-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SessionClock : TimeProvider
    {
        private DateTimeOffset now = new(2030, 1, 1, 12, 0, 0, 750, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
