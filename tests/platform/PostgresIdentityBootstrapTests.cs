using System.Diagnostics;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

public static class PostgresIdentityBootstrapTests
{
    public static async Task RunAsync(string connectionString)
    {
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        var issuer = "https://identity.example.test/bootstrap-" + Guid.NewGuid().ToString("N");
        const string subject = "operator'quote";
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
        try
        {
            await Bootstrap(connectionString, id, issuer, subject, "首次管理员", 0);
            var first = await IdentityManagementFixture.Scalar<long>(db, "SELECT identity_revision FROM public.platform_users WHERE user_id=$1", id);
            IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(db, "SELECT count(*) FROM public.platform_identity_audit WHERE actor_user_id=$1 AND action='bootstrap_admin'", id) == 1, "Bootstrap must persist a correlated audit.");
            await Bootstrap(connectionString, id, issuer, subject, "不得覆盖名称", 0);
            IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(db, "SELECT identity_revision FROM public.platform_users WHERE user_id=$1", id) == first,
                "Exact bootstrap replay cannot increment revision.");
            IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<string>(db, "SELECT display_name FROM public.platform_users WHERE user_id=$1", id) == "首次管理员",
                "Exact bootstrap replay cannot overwrite a profile.");
            IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(db, "SELECT count(*) FROM public.platform_identity_audit WHERE actor_user_id=$1", id) == 1,
                "Exact bootstrap replay cannot duplicate audit.");
            await Bootstrap(connectionString, other, issuer, "another", "不得新建管理员", 1);
            IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(db, "SELECT count(*) FROM public.platform_users WHERE user_id=$1", other) == 0,
                "Existing enabled administrator blocks bootstrap of another identity atomically.");
            await Bootstrap(connectionString, id, issuer, "wrong-subject", "错误绑定", 1);
            await Bootstrap(connectionString, other, issuer, subject, "冲突绑定", 1);
            await IdentityManagementFixture.Execute(db, "UPDATE public.platform_users SET enabled=false WHERE user_id=$1", id);
            await Bootstrap(connectionString, id, issuer, subject, "不得恢复", 1);
            IdentityManagementFixture.Assert(!await IdentityManagementFixture.Scalar<bool>(db, "SELECT enabled FROM public.platform_users WHERE user_id=$1", id),
                "Bootstrap cannot silently re-enable a disabled user.");
        }
        finally
        {
            await IdentityManagementFixture.Execute(db, "DELETE FROM public.platform_identity_audit WHERE actor_user_id=ANY($1)", new[] { id, other });
            await IdentityManagementFixture.Execute(db, "DELETE FROM public.platform_users WHERE user_id=ANY($1)", new[] { id, other });
        }
        Console.WriteLine("PASS: real psql first-administrator bootstrap, quoted inputs, exact replay, existing-admin guard, binding conflict and disabled-user protection.");
    }

    private static async Task Bootstrap(string connectionString, Guid id, string issuer, string subject, string name, int expected)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        var root = IdentityManagementFixture.Workspace();
        var psql = Environment.GetEnvironmentVariable("ASSEMBLEAI_POSTGRES_PSQL") ?? Path.Combine(root, ".local-data/native-postgres/postgresql/pgsql/bin/psql.exe");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("PG", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ASSEMBLEAI_POSTGRES", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["PGPASSWORD"] = settings.Password;
        foreach (var argument in new[] { Path.Combine(root, "tools/bootstrap-identity-admin.mjs"), "--psql", psql,
            "--host", settings.Host!, "--port", settings.Port.ToString(), "--username", settings.Username!,
            "--user-id", id.ToString(), "--issuer", issuer, "--subject", subject, "--display-name", name }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not launch bootstrap psql.");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await child.WaitForExitAsync(budget.Token); }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            throw new InvalidOperationException("Bootstrap psql exceeded time budget.");
        }
        await output;
        await error; // The wrapper keeps raw database diagnostics out of user output.
        IdentityManagementFixture.Assert(expected == 0 ? child.ExitCode == 0 : child.ExitCode != 0,
            $"Bootstrap expected {(expected == 0 ? "success" : "rejection")}, received exit {child.ExitCode}.");
    }
}
