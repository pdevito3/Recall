using System.Globalization;
using Recall.AppHost;
using Recall.AppHost.AuthProviders;
using Serilog;
using Serilog.Events;

// Bootstrap logger to catch startup errors before host is built
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Aspire.Hosting.Dcp", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Recall.AppHost");

    var builder = DistributedApplication.CreateBuilder(args);

    builder.Services.AddSerilog((_, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "Recall.AppHost"));

    // A normal run uses the fixed ports of the port table in README.md. `aspire run --isolated` gives each proxied
    // endpoint a random port and skips the data volumes, so that worktrees can run the full stack at the same time.
    // See Isolation.cs.
    var server = builder.AddProject<Projects.Recall_Server>("server")
        .WithHttpHealthCheck("/health", endpointName: "http")
        .WithExternalHttpEndpoints()
        // The endpoints come from Kestrel:Endpoints in the appsettings of the Server, with the HTTP/2-only "grpc"
        // endpoint. Aspire makes one endpoint for each name and sets its Url.
        .WithCSharpRepl();

    // MCP clients call the server directly, so the resource of its protected resource metadata is the URL they call.
    server.WithEnvironment("Mcp__PublicOrigin", server.GetEndpoint("https"));

    var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
        .WithEndpoint("http", endpoint => endpoint.Port = 24710)
        .WithPnpm();

    if (builder.ExecutionContext.IsRunMode)
    {
        var dataDirectory = builder.DataDirectory();
        server.WithEnvironment("ConnectionStrings__appdb", $"Data Source={Path.Combine(dataDirectory, "recall.db")}");
        server.WithEnvironment("FileStore__Root", Path.Combine(dataDirectory, "files"));
        if (builder.Configuration["SpaBuild"] is { Length: > 0 } spaBuild)
            server.WithEnvironment("ASPNETCORE_WEBROOT", Path.GetFullPath(spaBuild));
    }

    var authClientUrls = new AuthClientUrls(webfrontend.GetEndpoint("http"), server.GetEndpoint("https"), server.GetEndpoint("http"));
    // The AuthProvider setting selects the provider. appsettings.json gives the default, and the environment
    // variable AuthProvider=Keycloak overrides it. The e2e tests find the provider from the running stack.
    var selectedProvider = AuthProviderCatalog.Select(builder.Configuration);
    if (selectedProvider.AddTo(builder, authClientUrls) is { } authProvider)
        server.WithServerAuth(authProvider, selectedProvider);
    else
        server.WithForwardedAuth(builder.Configuration);

    webfrontend.WithReference(server)
        .WaitFor(server);

    server.PublishWithContainerFiles(webfrontend, "wwwroot");

    // Labcoat finds the test projects of the solution and shows a web UI to run them. Aspire tests and publish skip it.
    builder.AddLabcoat("tests")
        .WithHostPort(24705);

    builder.Build().Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AppHost terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
