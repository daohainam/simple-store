using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string ReadinessEndpointPath = "/ready";

    // v10: tag convention. Aspire-auto-registered dependency probes (Npgsql, Redis, MassTransit
    // bus, etc.) ship without a "ready" tag; we add it post-hoc via HealthCheckServiceOptions
    // so /ready can filter to readiness-only checks. Anything in this set gets the tag.
    private static readonly HashSet<string> AspireDependencyCheckPrefixes =
    [
        "npgsql", "postgres", "redis", "stackexchangeredis", "rabbitmq", "masstransit"
    ];

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // v10: 1.0 by default (AlwaysOn — fine for a learning project so every trace shows up
        // in the Aspire dashboard). Set OTEL_TRACES_SAMPLER_ARG=0.1 to sample 10% in production.
        // ParentBased keeps a trace whole — once the head sampling decision is made, every child
        // span inherits it instead of each service re-deciding and producing fractured traces.
        var samplerArg = builder.Configuration.GetValue<double?>("OTEL_TRACES_SAMPLER_ARG") ?? 1.0;

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // v10: pick up MassTransit's built-in meter (messaging.consumer.duration etc.)
                    // and every per-service Telemetry.Meter via the wildcard.
                    .AddMeter("MassTransit")
                    .AddMeter("SimpleStore.*");
            })
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(samplerArg)))
                    .AddSource(builder.Environment.ApplicationName)
                    // v10: pick up MassTransit's built-in ActivitySource (publish/send/consume
                    // spans across all services that use the bus) and every per-service
                    // Telemetry.Source via the wildcard.
                    .AddSource("MassTransit")
                    .AddSource("SimpleStore.*")
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing — they would otherwise dominate
                        // the trace stream with no signal.
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                            && !context.Request.Path.StartsWithSegments(ReadinessEndpointPath)
                    )
                    .AddHttpClientInstrumentation()
                    // v10: gRPC client instrumentation — covers KurrentDB calls (AppendAsync,
                    // SubscribeAllAsync) in Inventory.API.
                    .AddGrpcClientInstrumentation()
                    // v10: EF Core instrumentation — surfaces every SQL command as a span.
                    // SetDbStatementForText emits the actual SQL text in the span's db.statement
                    // tag. Safe here: passwords are hashed in IdentityService before reaching EF,
                    // and no PII flows through SQL parameters on hot read paths.
                    .AddEntityFrameworkCoreInstrumentation(o =>
                    {
                        o.SetDbStatementForText = true;
                    });
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        // v10: Aspire's Npgsql / Redis / RabbitMQ components and MassTransit register dependency
        // probes for us, but they don't tag them with "ready" because that tag is our convention.
        // Configure-after-the-fact so /ready can filter to dependency probes only. Any registration
        // whose name starts with one of the known dependency prefixes gets the tag.
        builder.Services.PostConfigure<HealthCheckServiceOptions>(o =>
        {
            foreach (var r in o.Registrations)
            {
                if (r.Tags.Contains("ready")) continue;
                if (AspireDependencyCheckPrefixes.Any(p =>
                        r.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    r.Tags.Add("ready");
                }
            }
        });

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // v9: health endpoints are exposed in ALL environments so Aspire / k8s liveness + readiness
        // probes work in production too, not just dev. The endpoints only leak per-dependency
        // up/down state (no PII or stack traces) — auth-gating them is deferred to a later pass.
        //
        // v10 introduced a third endpoint /ready and the "ready" tag convention so we can
        // distinguish liveness from readiness cleanly. The three endpoints serve different
        // orchestrator needs:
        //
        // /alive  — liveness probe. Only "live"-tagged checks (just the trivial self-check).
        //           Returns 200 as long as the process is responsive; never 503 just because
        //           a downstream dependency is unreachable. Used by k8s to decide whether to
        //           restart the container.
        // /ready  — readiness probe (v10). Only "ready"-tagged checks: the dependency probes
        //           auto-registered by Aspire (Postgres / Redis / RabbitMQ) + custom probes
        //           tagged "ready" (e.g. KurrentDB in Inventory.API). 503 when any dependency
        //           is unreachable. Used by k8s to decide whether to route traffic.
        // /health — aggregate. Runs EVERY registered check (live + ready + anything else).
        //           Kept for backward compatibility with Aspire dashboards and v9 consumers
        //           (e.g. YARP's active health-check probes from v10 §8).

        app.MapHealthChecks(HealthEndpointPath);

        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        app.MapHealthChecks(ReadinessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready")
        });

        return app;
    }
}
