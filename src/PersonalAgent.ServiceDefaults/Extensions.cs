using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>Provides hosting defaults without imposing retries on non-idempotent operations.</summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>Adds privacy-filtered telemetry, health checks, and service discovery.</summary>
    /// <typeparam name="TBuilder">Host builder type.</typeparam>
    /// <param name="builder">Application host builder to configure.</param>
    /// <returns>The same builder for further configuration.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
        return builder;
    }

    /// <summary>Adds local telemetry instrumentation without formatted log messages or scopes.</summary>
    /// <typeparam name="TBuilder">Host builder type.</typeparam>
    /// <param name="builder">Application host builder to configure.</param>
    /// <returns>The same builder for further configuration.</returns>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = false;
            logging.IncludeScopes = false;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                    .AddHttpClientInstrumentation();
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>Registers a non-sensitive process liveness check.</summary>
    /// <typeparam name="TBuilder">Host builder type.</typeparam>
    /// <param name="builder">Application host builder to configure.</param>
    /// <returns>The same builder for further configuration.</returns>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    /// <summary>Maps development-only readiness and liveness endpoints.</summary>
    /// <param name="app">Web application to configure.</param>
    /// <returns>The same application for further configuration.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath);
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = result => result.Tags.Contains("live")
            });
        }

        return app;
    }
}
