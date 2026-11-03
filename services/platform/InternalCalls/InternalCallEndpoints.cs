using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.InternalCalls;

public static class InternalCallEndpoints
{
    public static WebApplicationBuilder AddPlatformInternalCalls(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(InternalCallOptions.SectionName).Get<InternalCallOptions>() ?? new();
        options.Validate(builder.Environment.IsDevelopment());
        builder.Services.AddSingleton(options);
        if (!options.Enabled) return builder;
        // Load immediately so a configured signing service cannot start with invalid keys.
        var keys = new InternalCallKeys(options);
        builder.Services.AddSingleton(_ => keys);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<InternalCallTokenService>();
        if (options.ValidationEndpointEnabled)
            builder.Services.TryAddSingleton<IApplicationCallStore, PostgresApplicationCallStore>();
        return builder;
    }

    public static WebApplication MapInternalCallEndpoints(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<InternalCallOptions>().Enabled) return app;
        var keys = app.Services.GetRequiredService<InternalCallKeys>();
        app.MapGet("/.well-known/assembleai-jwks.json", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Text(keys.GetJwksJson(), "application/json");
        }).AllowAnonymous();
        if (app.Services.GetRequiredService<InternalCallOptions>().ValidationEndpointEnabled)
            app.MapPost("/internal/calls/validate", ApplicationCallValidation.Handle).AllowAnonymous();
        return app;
    }
}
