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
        .WithHttpHealthCheck("/health")
        .WithExternalHttpEndpoints()
        // The endpoints come from Kestrel:Endpoints in the appsettings of the Server, with the HTTP/2-only "grpc"
        // endpoint. Aspire makes one endpoint for each name and sets its Url.
        .WithCSharpRepl();

    // MCP clients call the server directly, so the resource of its protected resource metadata is the URL they call.
    server.WithEnvironment("Mcp__PublicOrigin", server.GetEndpoint("https"));

    var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
        .WithEndpoint("http", endpoint => endpoint.Port = 24710)
        .WithPnpm();

    var bff = builder.AddProject<Projects.Recall_Bff>("bff")
        .WithReference(server)
        .WithHttpHealthCheck("/health")
        .WithExternalHttpEndpoints()
        .WithParentRelationship(webfrontend);
    // services:webfrontend tells the BFF that the Vite dev server serves the SPA. A published BFF serves the SPA
    // from wwwroot, so it must not get the reference.
    if (builder.ExecutionContext.IsRunMode)
    {
        var dataDirectory = builder.DataDirectory();
        server.WithEnvironment("ConnectionStrings__appdb", $"Data Source={Path.Combine(dataDirectory, "recall.db")}");
        server.WithEnvironment("FileStore__Root", Path.Combine(dataDirectory, "files"));
        bff.WithEnvironment("ConnectionStrings__bffdb", $"Data Source={Path.Combine(dataDirectory, "bff.db")}");
        if (builder.Configuration["SpaBuild"] is { Length: > 0 } spaBuild)
            bff.WithEnvironment("ASPNETCORE_WEBROOT", Path.GetFullPath(spaBuild));
        else
            bff.WithReference(webfrontend);
    }
    // The identity provider containers call /bff/backchannel through this endpoint. The Aspire container tunnel
    // gives it its own host and port. With the tunnel off, the host is host.docker.internal.
    var bffForContainers = bff.GetEndpoint("http", KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
    // The BFF builds its redirect URIs from its own URL, because the Vite proxy changes the origin.
    bff.WithEnvironment("Bff__PublicOrigin", bff.GetEndpoint("https"));

    var authClientUrls = new AuthClientUrls(
        webfrontend.GetEndpoint("http"), server.GetEndpoint("https"), server.GetEndpoint("http"), bff.GetEndpoint("https"),
        bffForContainers);
    // The AuthProvider setting selects the provider. appsettings.json gives the default, and the environment
    // variable AuthProvider=Keycloak overrides it. The e2e tests find the provider from the running stack.
    var selectedProvider = AuthProviderCatalog.Select(builder.Configuration);
    var authProvider = selectedProvider.AddTo(builder, authClientUrls);
    // In a local run, the host filter also lets in the identity provider containers, through the container endpoint
    // above or host.docker.internal, and the health probes, which call localhost. A published BFF does not trust
    // these hosts. Aspire refuses a container endpoint in an app with no container, which is a run with DuendeDemo.
    if (builder.ExecutionContext.IsRunMode)
        bff.WithEnvironment("Bff__AdditionalAllowedHosts", builder.Resources.OfType<ContainerResource>().Any()
            ? ReferenceExpression.Create($"{bffForContainers.Property(EndpointProperty.Host)};host.docker.internal;localhost")
            : ReferenceExpression.Create($"localhost"));
    // DPoP=true runs the stack with DPoP (RFC 9449): the BFF binds the tokens to its key. The setting is a flag like
    // AuthProvider, for example the environment variable DPoP=true.
    if (AuthProviderDPoP.IsOn(builder))
        authProvider = authProvider.WithDPoP(selectedProvider, builder);
    server.WithServerAuth(authProvider);
    bff.WithBffAuth(authProvider);
    authProvider.ConfigureBff?.Invoke(bff);

    // Only the e2e runs set it. A margin longer than the access token lifetime makes each API call refresh, so
    // that the e2e tests prove that the BFF refreshes and stores the rotated tokens.
    if (builder.Configuration["BffRefreshBeforeExpirationSeconds"] is { Length: > 0 } refreshMargin)
    {
        bff.WithEnvironment("Auth__RefreshBeforeExpirationSeconds", refreshMargin);
    }

    // The UI starts only when a login and an API call can work. The BFF waits for the auth provider.
    webfrontend.WithReference(bff)
        .WaitFor(bff)
        .WaitFor(server);

    // The BFF serves the SPA in production. The server is an internal API only.
    bff.PublishWithContainerFiles(webfrontend, "wwwroot");

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
