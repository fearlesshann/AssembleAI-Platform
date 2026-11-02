using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AssembleAI.Platform.Identity;

namespace AssembleAI.Platform;

public static class PlatformApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Console logging works in unprivileged containers and Windows developer sessions.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        if (builder.Environment.IsDevelopment())
        {
            // T2 has no login/session state. Do not read a developer's unrelated DPAPI key ring.
            // T3 must replace this with explicit shared key persistence for authenticated sessions.
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = new DevelopmentKeyRepository());
        }
        builder.Services.AddProblemDetails();
        builder.Services.AddPlatformAuthorization();
        builder.Services.AddHttpClient("readiness", client => client.Timeout = TimeSpan.FromSeconds(3))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddHealthChecks()
            .AddCheck<DependencyReadinessCheck>("dependencies", tags: ["ready"]);

        var sampleEnabled = builder.Environment.IsDevelopment()
            && builder.Configuration.GetValue<bool>("DevelopmentSample:Enabled");
        if (sampleEnabled)
        {
            DevelopmentSampleProxy.Register(builder.Services, builder.Configuration);
        }

        configure?.Invoke(builder);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIdentityEndpoints();
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealthResponse
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthResponse
        });
        if (sampleEnabled)
        {
            app.MapReverseProxy();
        }

        return app;
    }

    private static Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.ToDictionary(
                item => item.Key,
                item => new
                {
                    status = item.Value.Status.ToString().ToLowerInvariant(),
                    dependencies = item.Value.Data
                })
        });
    }
}
