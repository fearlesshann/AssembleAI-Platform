using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.SmokeTests;

public static class ModelDelegationTests
{
    public static async Task RunAsync()
    {
        Configuration();
        await ScopeAndDisposal();
        await CancellationAndExpiry();
        await ConcurrentDisposal();
        await Capacity();
        Console.WriteLine("PASS: model delegation: exact deployment allowlist, opaque capabilities, fixed caller, expiry/cancellation/disposal, concurrent revocation and bounded capacity.");
    }

    private static void Configuration()
    {
        var operation = new GatewayOperation("sample", "1.0.0", "POST", "/model", "sample:use");
        Assert(operation.ModelIds.Length == 0, "Ordinary operations cannot implicitly delegate to models.");
        new GatewayOptions { Enabled = true, Operations = [operation with { ModelIds = ["assistant", "embed.v1"] }] }.Validate();
        foreach (var ids in new string[][] { ["assistant", "assistant"], ["UPPER"], ["../model"], [""], new string[101] })
        {
            try { new GatewayOptions { Enabled = true, Operations = [operation with { ModelIds = ids }] }.Validate(); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("Malformed, duplicated or unbounded model allowlists must fail configuration.");
        }
        try { new GatewayOptions { Enabled = true, Operations = [operation with { ModelIds = null! }] }.Validate(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Null model allowlist must fail configuration.");
    }

    private static async Task ScopeAndDisposal()
    {
        var clock = new Clock();
        var registry = new ModelDelegationRegistry(clock);
        var checks = 0;
        var source = new ModelCallSource("sample", "1.0.0", Guid.NewGuid(), new string('a', 32));
        var caller = Caller(clock, _ => { checks++; return Task.FromResult(true); }) with { Source = source };
        Assert(registry.Register(caller, []) is null, "No model allowlist means no delegation capability.");
        string[] aliases = ["assistant"];
        var lease = registry.Register(caller, aliases) ?? throw new InvalidOperationException("Expected registration.");
        using var second = registry.Register(caller, ["assistant"]) ?? throw new InvalidOperationException("Expected second registration.");
        Assert(lease.Capability.Length == 64 && lease.Capability.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9')
            && lease.Capability != second.Capability, "Each active call requires a separate 256-bit opaque capability.");
        aliases[0] = "forbidden";
        Assert(registry.Resolve(lease.Capability, "forbidden") is null && registry.Resolve(lease.Capability, "Assistant") is null,
            "The exact original model allowlist cannot be widened by caller mutation or case changes.");
        foreach (var invalid in new[] { "", "Bearer " + lease.Capability, new string('g', 64), new string('a', 65), "header.payload.signature" })
            Assert(registry.Resolve(invalid, "assistant") is null, "Unknown or JWT-shaped credentials cannot become model capabilities.");
        var resolved = registry.Resolve(lease.Capability, "assistant") ?? throw new InvalidOperationException("Expected authorized alias.");
        Assert(resolved.Subject == caller.Subject && resolved.UserId == caller.UserId && resolved.TenantId == caller.TenantId
            && resolved.Source == source && resolved.ExpiresAt == caller.ExpiresAt, "Delegation identity/source must come solely from the captured caller.");
        Assert(await resolved.CheckAsync(default) && checks == 1, "Each use must call the original live application check.");
        clock.Utc = clock.Utc.AddSeconds(31);
        Assert(registry.Resolve(lease.Capability, "assistant") is not null && await resolved.CheckAsync(default),
            "An accepted live gateway call can delegate past the original JWT admission window without extending its own lifetime.");
        lease.Dispose(); lease.Dispose();
        Assert(resolved.Lifetime.IsCancellationRequested && registry.Resolve(lease.Capability, "assistant") is null,
            "Normal outer completion must immediately cancel existing holders and reject late lookup.");
        Assert(!await Check(resolved), "A holder resolved before disposal cannot execute after disposal.");
        Assert(registry.Resolve(second.Capability, "assistant") is not null, "Disposal must not revoke a different invocation.");
    }

    private static async Task CancellationAndExpiry()
    {
        var clock = new Clock();
        var registry = new ModelDelegationRegistry(clock);
        using var outer = new CancellationTokenSource();
        using var lease = registry.Register(Caller(clock) with { Lifetime = outer.Token }, ["assistant"])!;
        var holder = registry.Resolve(lease.Capability, "assistant")!;
        outer.Cancel();
        Assert(holder.Lifetime.IsCancellationRequested && registry.Resolve(lease.Capability, "assistant") is null && !await Check(holder),
            "Outer disconnect must cancel delegated work and forbid credential reuse.");
        using var expiry = registry.Register(Caller(clock) with { ExpiresAt = clock.GetUtcNow().AddSeconds(1) }, ["assistant"])!;
        var expiring = registry.Resolve(expiry.Capability, "assistant")!;
        clock.Utc = clock.Utc.AddSeconds(2);
        Assert(registry.Resolve(expiry.Capability, "assistant") is null && !await Check(expiring), "Expired capability cannot resolve or pass live recheck.");
        clock.Utc = clock.Utc.AddSeconds(-10);
        Assert(registry.Resolve(expiry.Capability, "assistant") is null && !await Check(expiring), "Observed expiry must not revive after clock rollback.");
        using var revoked = registry.Register(Caller(clock, _ => Task.FromResult(false)), ["assistant"])!;
        Assert(!await Check(registry.Resolve(revoked.Capability, "assistant")!), "Current session/application revocation must deny use.");
    }

    private static async Task ConcurrentDisposal()
    {
        var clock = new Clock(); var registry = new ModelDelegationRegistry(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lease = registry.Register(Caller(clock, async _ => { entered.TrySetResult(); await release.Task; return true; }), ["assistant"])!;
        var holder = registry.Resolve(lease.Capability, "assistant")!;
        var pending = Check(holder);
        await entered.Task;
        lease.Dispose(); release.TrySetResult();
        Assert(!await pending, "A check admitted before outer completion must fail if completion wins while authorization awaits.");
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => {
            lease.Dispose();
            Assert(registry.Resolve(lease.Capability, "assistant") is null, "Concurrent disposal cannot restore a capability.");
        })));
    }

    private static async Task Capacity()
    {
        var clock = new Clock(); var registry = new ModelDelegationRegistry(clock, maximumEntries: 2);
        using var first = registry.Register(Caller(clock), ["assistant"])!;
        using var second = registry.Register(Caller(clock), ["assistant"])!;
        Assert(registry.Register(Caller(clock), ["assistant"]) is null, "Registry must reject excess active entries.");
        first.Dispose();
        using var replacement = registry.Register(Caller(clock), ["assistant"]);
        Assert(replacement is not null, "Normal completion must release registry capacity.");
        var one = new ModelDelegationRegistry(clock, maximumEntries: 1);
        using var outer = new CancellationTokenSource();
        using var cancelled = one.Register(Caller(clock) with { Lifetime = outer.Token }, ["assistant"])!;
        outer.Cancel();
        using var afterCancel = one.Register(Caller(clock), ["assistant"]);
        Assert(afterCancel is not null, "Cancelled outer calls must release capacity even before lease Dispose.");
        await Task.CompletedTask;
    }

    private static ModelCaller Caller(Clock clock, Func<CancellationToken, Task<bool>>? check = null) => new(
        new SubjectIdentity("https://issuer.example", "operator"), Guid.NewGuid(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5),
        check ?? (_ => Task.FromResult(true)), default, new("sample", "1.0.0", Guid.NewGuid(), new string('b', 32)));
    private static async Task<bool> Check(ModelCaller caller)
    {
        try { return await caller.CheckAsync(default); }
        catch (OperationCanceledException) { return false; }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
