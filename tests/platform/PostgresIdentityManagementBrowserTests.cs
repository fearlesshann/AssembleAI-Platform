using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

public static class PostgresIdentityManagementBrowserTests
{
    public static async Task RunAsync(string connectionString, string runDirectory)
    {
        var f = new IdentityManagementFixture();
        await using var oidc = await OidcFixture.StartAsync(new Dictionary<string, string>
        {
            [f.Root.ToString()] = "平台管理员", [f.Admin.ToString()] = "租户管理员", [f.Member.ToString()] = "普通成员"
        });
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
        try
        {
            await f.Seed(db, oidc.Authority);
            var root = IdentityManagementFixture.Workspace(); var origin = IdentityManagementFixture.FreeOrigin();
            await using var app = IdentityManagementFixture.Application(connectionString, oidc, origin);
            using var files = new PhysicalFileProvider(Path.Combine(root, "apps/portal/dist"));
            var index = Path.Combine(files.Root, "index.html");
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            app.MapGet("/", () => Results.File(index, "text/html"));
            app.MapGet("/manage/{**path}", () => Results.File(index, "text/html"));
            await app.StartAsync();
            try
            {
                var configuration = Path.Combine(runDirectory, "identity-browser.json");
                await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
                {
                    origin, tenantId = f.Tenant, otherTenantId = f.OtherTenant, rootId = f.Root,
                    adminId = f.Admin, memberId = f.Member, issuer = oidc.Authority
                }));
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node")
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add(Path.Combine(root, "tests/frontend/identity-postgres-browser.mjs")); start.ArgumentList.Add(configuration);
                foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ASSEMBLEAI_POSTGRES", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch identity browser verification.");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                try { await process.WaitForExitAsync(budget.Token); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw new InvalidOperationException("Identity browser verification exceeded its time budget.");
                }
                var transcript = await output + await error;
                Console.Write(transcript.Length > 20000 ? transcript[..20000] : transcript);
                IdentityManagementFixture.Assert(process.ExitCode == 0, "Identity real browser verification failed.");
                IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(db,
                    "SELECT count(*) FROM public.platform_identity_audit WHERE actor_user_id=$1 AND tenant_id=$2 AND result='success'", f.Admin, f.Tenant) >= 3,
                    "Real tenant UI writes must persist attributable audits.");
                IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(db,
                    "SELECT count(*) FROM public.platform_identity_audit WHERE actor_user_id=$1 AND result='success'", f.Root) >= 2,
                    "Real global UI writes must persist attributable audits.");
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            // This fixture owns all rows identified by its issuer, including users created by the browser.
            await using var command = new NpgsqlCommand("SELECT user_id FROM public.platform_users WHERE issuer=$1", db);
            command.Parameters.AddWithValue(oidc.Authority);
            await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) f.AdditionalUsers.Add(reader.GetGuid(0));
            await f.Cleanup(db);
        }
        Console.WriteLine("PASS: identity management built portal, real OIDC/BFF/PostgreSQL writes, scoped roles/members, audit, themes and narrow layout.");
    }
}
