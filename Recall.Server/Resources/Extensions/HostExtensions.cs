namespace Recall.Server.Resources.Extensions;

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Domain.ModelProviders;
using Entrypoints.Http.OpenApi;
using FileTelemetry;
using Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

public static class HostExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    // Meters that the instrumentation packages do not add. Polly reports the retries and circuit breaks
    // of the standard resilience handler. The auth meters are new in .NET 10.
    private static readonly string[] FrameworkMeters =
    [
        "Polly",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore.Authentication",
        "Microsoft.AspNetCore.Authorization",
    ];

    // The instrumentation redacts the query values by default, but Aspire turns that off in development.
    private static string RedactedQuery(string query) =>
        "?" + string.Join('&', query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Contains('=') ? $"{pair.Split('=')[0]}=Redacted" : "Redacted"));

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.ConfigureSerilog();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    private static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var serviceName = builder.Environment.ApplicationName;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes(ResourceAttributes(builder)))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(Telemetry.Meter.Name)
                    .AddMeter(FrameworkMeters)
                    .AddBackWaveInstrumentation()
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddProcessor(new SpanTextCapProcessor())
                    .AddSource(Telemetry.Source.Name)
                    .AddSource(serviceName)
                    .AddBackWaveInstrumentation()
                    .AddProcessor(new EmptyClaimSpanFilter())
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        opts.Filter = ctx => !IsHealthProbe(ctx.Request.Path);
                        opts.RecordException = true;
                        opts.EnrichWithHttpRequest = (activity, request) =>
                        {
                            activity.SetTag("user_agent.original", null);
                            if (request.QueryString.HasValue)
                                activity.SetTag("url.query", RedactedQuery(request.QueryString.Value!));
                        };
                    })
                    .AddHttpClientInstrumentation(opts => opts.RecordException = true);
            });

        builder.AddOpenTelemetryExporters();
        builder.AddFileTelemetryExporters();

        return builder;
    }

    /// <remarks>
    /// Each sink must live inside this one <c>AddSerilog</c> callback. A logger assigned to <c>Log.Logger</c> elsewhere
    /// is replaced when the host resolves <c>ILoggerFactory</c>, so its sinks stop there.
    /// </remarks>
    private static TBuilder ConfigureSerilog<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        builder.Services.AddSerilog((services, lc) =>
        {
            lc.ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithThreadId()
                .Enrich.WithProperty("Application", builder.Environment.ApplicationName);

            lc.WriteTo.CappedText(to =>
            {
                if (FilesOutsideTheOpenApiBuild(services) is { } files)
                    to.RollingFiles(new CompactJsonFormatter(), files.LogFilePath, files.Options.Logs);

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    to.OpenTelemetry(options =>
                    {
                        options.Endpoint = otlpEndpoint;
                        options.Protocol = OtlpProtocol.Grpc;
                        options.ResourceAttributes = ResourceAttributes(builder);
                    });
            });
        });

        return builder;
    }

    /// <summary>
    /// The resource attributes for traces, metrics, and logs. Serilog exports logs on its own, not
    /// through the OpenTelemetry SDK, so both read this one source. Then a filter on version or
    /// environment finds all three signals.
    /// <para>
    /// The values of <c>OTEL_SERVICE_NAME</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c> win over the defaults. Aspire
    /// sets them to the resource name (<c>server</c>) and its replica id, and the dashboard finds the telemetry
    /// of a resource by these two values. If a default replaces them, the traces and the metrics go to a
    /// second resource, apart from the logs.
    /// </para>
    /// </summary>
    private static Dictionary<string, object> ResourceAttributes(IHostApplicationBuilder builder)
    {
        Dictionary<string, object> attributes = new()
        {
            ["service.name"] = builder.Environment.ApplicationName,
            ["service.version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0",
            ["service.instance.id"] = Environment.MachineName,
            ["deployment.environment"] = builder.Environment.EnvironmentName,
            ["host.name"] = Environment.MachineName,
        };

        foreach (var (key, value) in ResourceBuilder.CreateEmpty().AddEnvironmentVariableDetector().Build().Attributes)
            attributes[key] = value;

        return attributes;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            // Local dev / Aspire: OTLP endpoint is set automatically
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Production exporters - uncomment and configure as needed:
        // Azure Monitor: requires Azure.Monitor.OpenTelemetry.AspNetCore package
        // if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        // {
        //     builder.Services.AddOpenTelemetry().UseAzureMonitor();
        // }

        // AWS X-Ray: requires AWS.Distro.OpenTelemetry.AspNetCore package
        // builder.Services.AddOpenTelemetry().UseXRayExporter();

        // Jaeger: requires OpenTelemetry.Exporter.Jaeger package
        // builder.Services.AddOpenTelemetry().WithTracing(t => t.AddJaegerExporter());

        return builder;
    }

    /// <summary>
    /// The exporter of a span runs on the thread of its batch, and the exporter of a metric on the thread of its reader,
    /// so a slow disk does not hold a request.
    /// </summary>
    private static void AddFileTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.ConfigureOpenTelemetryTracerProvider((services, tracing) =>
        {
            if (FilesOutsideTheOpenApiBuild(services) is { } files)
                tracing.AddProcessor(new BatchActivityExportProcessor(new SpanFileExporter(files.NewTracesFile())));
        });
        builder.Services.ConfigureOpenTelemetryMeterProvider((services, metrics) =>
        {
            if (FilesOutsideTheOpenApiBuild(services) is { } files)
                metrics.AddReader(new PeriodicExportingMetricReader(
                    new MetricFileExporter(files.NewMetricsFile()),
                    files.Options.MetricExportSeconds * 1000));
        });
    }

    private static FileTelemetryPlan? FilesOutsideTheOpenApiBuild(IServiceProvider services)
        => OpenApiBuildTime.IsActive ? null : FileTelemetryPlan.For(services.GetRequiredService<IConfiguration>());

    private static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        // PostConfigure runs after all Configure calls, so it also wraps the checks that Aspire integrations add later
        builder.Services.PostConfigure<HealthCheckServiceOptions>(CallerCancellationHealthCheck.WrapEachCheck);

        return builder;
    }

    /// <summary>
    /// True for the health and aliveness probes. The orchestrator calls them every few seconds, so
    /// their spans and request logs are noise.
    /// </summary>
    public static bool IsHealthProbe(PathString path) =>
        path.StartsWithSegments(HealthEndpointPath) || path.StartsWithSegments(AlivenessEndpointPath);

    /// <summary>
    /// The request log level: Error for a failure, Verbose for a health probe, and Information for
    /// all other requests. Verbose is below the minimum level, so a healthy probe writes no log.
    /// </summary>
    public static LogEventLevel RequestLogLevel(HttpContext httpContext, double _, Exception? exception) =>
        (exception, httpContext.Response.StatusCode) switch
        {
            (not null, _) or (_, > 499) => LogEventLevel.Error,
            _ when IsHealthProbe(httpContext.Request.Path) => LogEventLevel.Verbose,
            _ => LogEventLevel.Information,
        };

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions { ResponseWriter = WriteHealthBodyAsync });
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        return app;
    }

    // Each entry can hold the text of an exception, so the body names only the stopped jobs and the key ring, whose texts are fixed.
    private static Task WriteHealthBodyAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain";
        var body = report.Status.ToString();
        if (report.Entries.TryGetValue(JobHosting.HealthCheckName, out var jobs) && jobs.Status == HealthStatus.Unhealthy)
            body += $"\n{jobs.Description}";
        if (report.Entries.TryGetValue(KeyRingStatus.HealthCheckName, out var keyRing) && keyRing.Status != HealthStatus.Healthy)
            body += $"\n{keyRing.Description}";

        return context.Response.WriteAsync(body);
    }
}
