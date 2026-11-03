using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.InternalCalls;

namespace AssembleAI.Platform;

public static class PlatformApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Console logging works in unprivileged containers and Windows developer sessions.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
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
        builder.AddPlatformBff();
        builder.AddPlatformInternalCalls();
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseBffSessionBoundary();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIdentityEndpoints();
        app.MapBffEndpoints();
        app.MapInternalCallEndpoints();
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
