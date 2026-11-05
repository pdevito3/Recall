using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Recall.Server;
using Recall.Server.Databases;
using Recall.Server.Resources;
using Recall.Server.Resources.Entrypoints;
using Recall.Server.Resources.Entrypoints.Http.OpenApi;
using Recall.Server.Resources.Extensions;
using Serilog;
using Serilog.Events;

OwnerOnlyFiles.ApplyUmask();

// Bootstrap logger to catch startup errors before host is built
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Recall.Server");

    var builder = WebApplication.CreateBuilder(OpenApiBuildTime.WithHostArgs(args));
    CSharpReplGuard.ThrowIfLoadedOutsideDevelopment(builder.Environment);

    builder.AddServiceDefaults(); // OpenTelemetry, Serilog, health checks, resilience, service discovery
    builder.Services.AddHostedService<MigrationHostedService<AppDbContext>>();
    builder.ConfigureServices();
    if (OwnerOnlyFiles.KeysDirectory(builder.Configuration) is { } keys)
        builder.Services.AddDataProtection().SetApplicationName("Recall").PersistKeysToFileSystem(new DirectoryInfo(keys));
    builder.AddEntrypoints();
    OpenApiBuildTime.RemoveHostedServices(builder.Services);

    var app = builder.Build();
    OwnerOnlyFiles.TightenAtStartup(app.Configuration);

    var forwardedHeaders = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
    forwardedHeaders.KnownIPNetworks.Clear();
    forwardedHeaders.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeaders);

    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = HostExtensions.RequestLogLevel;
        // The Serilog default, with the path from the browser cleaned of CR, LF, and the other control characters
        options.GetMessageTemplateProperties = (httpContext, requestPath, elapsed, statusCode) =>
        [
            new LogEventProperty("RequestMethod", new ScalarValue(httpContext.Request.Method)),
            new LogEventProperty("RequestPath", new ScalarValue(LogValue.Clean(requestPath))),
            new LogEventProperty("StatusCode", new ScalarValue(statusCode)),
            new LogEventProperty("Elapsed", new ScalarValue(elapsed)),
        ];
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", LogValue.Clean(httpContext.Request.Host.Value));
            diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
            diagnosticContext.Set("UserAgent", LogValue.Clean(httpContext.Request.Headers.UserAgent.ToString()));
        };
    });

    app.UseEntrypoints();
    app.MapEntrypoints();

    app.MapDefaultEndpoints();

    app.Run();
}
// The build-time OpenAPI generator stops the host on purpose with a HostAbortedException.
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    // A startup failure, such as a CSharpReplGuard stop, must not look like a clean exit to Aspire or CI.
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

