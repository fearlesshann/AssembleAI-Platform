using System.Security.Claims;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace AssembleAI.Platform.SmokeTests;

public static class TicketStoreTests
{
    public static async Task RunAsync()
    {
        await LiveBffSessionTests.RunAsync();
        await VerifyCopiesAndRandomReferences();
        await VerifyExpirationAndRevocation();
        await VerifyRenewalDoesNotExtendLifetime();
        await VerifySerializedExpiryBoundary();
        await VerifyCapacityAndConcurrentRevocation();
        await VerifyCancellation();
        await VerifyPostgresConfigurationFailures();
        Console.WriteLine("PASS: BFF ticket stores: isolated copies, bounded expiry/capacity, revoke/renew races, cancellation and safe configuration failures.");
    }

    private static async Task VerifyCopiesAndRandomReferences()
    {
        var clock = new SessionClock();
        ITicketStore store = new MemoryTicketStore(clock);
        var input = Ticket(clock.GetUtcNow().AddHours(1));
        var key = await store.StoreAsync(input);
        Assert(key.Length == 43 && WebEncoders.Base64UrlDecode(key).Length == 32,
            "Session references must contain 32 random bytes encoded as base64url.");
        Assert(key != await store.StoreAsync(input), "Every session must get an independent reference.");
        input.Properties.Items["marker"] = "changed-after-store";
        ((ClaimsIdentity)input.Principal.Identity!).AddClaim(new Claim("mutated", "input"));
        var first = await store.RetrieveAsync(key);
        Assert(first is not null && first.Properties.Items["marker"] == "original"
            && !first.Principal.HasClaim(c => c.Type == "mutated"), "Store must retain a serialized copy of its input.");
        first!.Properties.Items["marker"] = "changed-after-read";
        ((ClaimsIdentity)first.Principal.Identity!).AddClaim(new Claim("mutated", "read"));
        var second = await store.RetrieveAsync(key);
        Assert(second is not null && second.Properties.Items["marker"] == "original"
            && !second.Principal.HasClaim(c => c.Type == "mutated"), "Each retrieval must produce an isolated ticket copy.");
    }

    private static async Task VerifyExpirationAndRevocation()
    {
        var clock = new SessionClock();
        ITicketStore store = new MemoryTicketStore(clock);
        var key = await store.StoreAsync(Ticket(clock.GetUtcNow().AddMinutes(1)));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert(await store.RetrieveAsync(key) is null, "A session expires exactly at its deadline, even without a cleanup pass.");
        await store.RenewAsync(key, Ticket(clock.GetUtcNow().AddHours(1)));
        Assert(await store.RetrieveAsync(key) is null, "Renew cannot resurrect an expired session.");

        var revoked = await store.StoreAsync(Ticket(clock.GetUtcNow().AddHours(1)));
        await store.RemoveAsync(revoked);
        await store.RenewAsync(revoked, Ticket(clock.GetUtcNow().AddHours(2)));
        Assert(await store.RetrieveAsync(revoked) is null, "Renew cannot recreate a removed session.");
        await store.RemoveAsync(revoked);
        await store.RenewAsync("unknown-reference", Ticket(clock.GetUtcNow().AddHours(1)));
        Assert(await store.RetrieveAsync("unknown-reference") is null, "Unknown references must not create sessions.");
        var alreadyExpired = await store.StoreAsync(Ticket(clock.GetUtcNow().AddSeconds(-1)));
        Assert(await store.RetrieveAsync(alreadyExpired) is null, "An already expired ticket must never authenticate.");
    }

    private static async Task VerifyRenewalDoesNotExtendLifetime()
    {
        var clock = new SessionClock();
        ITicketStore store = new MemoryTicketStore(clock);
        var expectedDeadline = clock.GetUtcNow().AddHours(8);
        var input = Ticket();
        var defaultKey = await store.StoreAsync(input);
        var excessiveKey = await store.StoreAsync(Ticket(clock.GetUtcNow().AddDays(30)));
        Assert(input.Properties.ExpiresUtc is null, "Normalizing expiry must not modify the caller's ticket.");
        Assert((await store.RetrieveAsync(defaultKey))!.Properties.ExpiresUtc == expectedDeadline,
            "Missing expiry must default to eight hours.");
        Assert((await store.RetrieveAsync(excessiveKey))!.Properties.ExpiresUtc == expectedDeadline,
            "Requested expiry beyond eight hours must be capped.");
        clock.Advance(TimeSpan.FromHours(7));
        var renewed = Ticket(clock.GetUtcNow().AddHours(8));
        renewed.Properties.Items["marker"] = "renewed";
        await store.RenewAsync(defaultKey, renewed);
        renewed.Properties.Items["marker"] = "changed-after-renew";
        var result = await store.RetrieveAsync(defaultKey);
        Assert(result!.Properties.Items["marker"] == "renewed" && result.Properties.ExpiresUtc == expectedDeadline,
            "Renew must update a copy while preserving the original absolute deadline.");
        clock.Advance(TimeSpan.FromHours(1));
        Assert(await store.RetrieveAsync(defaultKey) is null && await store.RetrieveAsync(excessiveKey) is null,
            "Renewed and excessive-expiry sessions must expire at the original eight-hour boundary.");

        var shortenedKey = await store.StoreAsync(Ticket(clock.GetUtcNow().AddHours(1)));
        await store.RenewAsync(shortenedKey, Ticket(clock.GetUtcNow().AddMinutes(1)));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert(await store.RetrieveAsync(shortenedKey) is null, "Renew must honor an earlier expiration requested by the server.");
    }

    private static async Task VerifyCapacityAndConcurrentRevocation()
    {
        var clock = new SessionClock();
        ITicketStore store = new MemoryTicketStore(clock);
        var attempted = await Task.WhenAll(Enumerable.Range(0, 1100).Select(_ => Task.Run(async () =>
        {
            try { return await store.StoreAsync(Ticket(clock.GetUtcNow().AddMinutes(1))); }
            catch (BffSessionStoreException) { return null; }
        })));
        var keys = attempted.OfType<string>().ToArray();
        Assert(keys.Length == 1024 && keys.Distinct(StringComparer.Ordinal).Count() == 1024,
            "Concurrent stores must enforce the 1,024-session cap and unique references.");
        Assert(await store.RetrieveAsync(keys[0]) is not null, "Reaching capacity must preserve existing live sessions.");
        clock.Advance(TimeSpan.FromMinutes(1));
        var replacement = await store.StoreAsync(Ticket(clock.GetUtcNow().AddHours(1)));
        Assert(await store.RetrieveAsync(replacement) is not null && await store.RetrieveAsync(keys[0]) is null,
            "Expired sessions must be reclaimed to admit a new session.");
        await Task.WhenAll(Enumerable.Range(0, 128).Select(_ => Task.Run(async () =>
        {
            await store.RenewAsync(replacement, Ticket(clock.GetUtcNow().AddHours(1)));
            await store.RemoveAsync(replacement);
        })));
        Assert(await store.RetrieveAsync(replacement) is null, "Concurrent renewals must never undo session revocation.");
    }

    private static async Task VerifySerializedExpiryBoundary()
    {
        var clock = new SessionClock();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        ITicketStore store = new MemoryTicketStore(clock);
        var key = await store.StoreAsync(Ticket());
        var expires = (await store.RetrieveAsync(key))!.Properties.ExpiresUtc!.Value;
        clock.Advance(expires - clock.GetUtcNow());
        Assert(await store.RetrieveAsync(key) is null,
            "Stored metadata must honor the serialized ticket's expiry, including fractional-second normalization.");
    }

    private static async Task VerifyCancellation()
    {
        var clock = new SessionClock();
        ITicketStore store = new MemoryTicketStore(clock);
        var key = await store.StoreAsync(Ticket());
        using var source = new CancellationTokenSource();
        source.Cancel();
        var context = new DefaultHttpContext();
        await ExpectCancellation(() => store.StoreAsync(Ticket(), context, source.Token));
        await ExpectCancellation(() => store.RetrieveAsync(key, context, source.Token));
        await ExpectCancellation(() => store.RenewAsync(key, Ticket(), context, source.Token));
        await ExpectCancellation(() => store.RemoveAsync(key, context, source.Token));
        Assert(await store.RetrieveAsync(key) is not null, "A cancelled removal must leave the session intact.");

        ITicketStore postgres = new PostgresTicketStore(new ConfigurationBuilder().Build(), new EphemeralDataProtectionProvider(), clock);
        await ExpectCancellation(() => postgres.StoreAsync(Ticket(), context, source.Token));
        await ExpectCancellation(() => postgres.RetrieveAsync(key, context, source.Token));
        await ExpectCancellation(() => postgres.RenewAsync(key, Ticket(), context, source.Token));
        await ExpectCancellation(() => postgres.RemoveAsync(key, context, source.Token));
    }

    private static async Task VerifyPostgresConfigurationFailures()
    {
        foreach (var connectionString in new[] { "", "Password=do-not-leak;UnsupportedKey=value" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = connectionString
            }).Build();
            ITicketStore store = new PostgresTicketStore(configuration, new EphemeralDataProtectionProvider(), new SessionClock());
            const string key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            await ExpectSafeStoreFailure(() => store.StoreAsync(Ticket()));
            await ExpectSafeStoreFailure(() => store.RetrieveAsync(key));
            await ExpectSafeStoreFailure(() => store.RenewAsync(key, Ticket()));
            await ExpectSafeStoreFailure(() => store.RemoveAsync(key));
        }
    }

    private static AuthenticationTicket Ticket(DateTimeOffset? expires = null) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "https://identity.example.test"), new Claim("sub", "alice")], "test-only")),
        new AuthenticationProperties(new Dictionary<string, string?> { ["marker"] = "original" }) { ExpiresUtc = expires },
        "test-only");

    private static async Task ExpectSafeStoreFailure(Func<Task> action)
    {
        try { await action(); }
        catch (BffSessionStoreException exception)
        {
            Assert(!exception.ToString().Contains("do-not-leak", StringComparison.Ordinal),
                "Store exceptions must not expose credentials, including through inner exceptions.");
            return;
        }
        throw new InvalidOperationException("Missing or malformed PostgreSQL configuration must fail closed with BffSessionStoreException.");
    }

    private static async Task ExpectCancellation(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Ticket stores must preserve request cancellation.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SessionClock : TimeProvider
    {
        private DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
