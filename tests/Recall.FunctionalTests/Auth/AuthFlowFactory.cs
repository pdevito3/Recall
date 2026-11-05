namespace Recall.FunctionalTests.Auth;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Auth;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.SharedTestHelpers.Utilities;
using Serilog.Core;

/// <summary>
/// The real app with a stub provider, the real scheme selector, and a database of its own, because the first-owner rule
/// reads whether any tenant exists. <see cref="TestingWebApplicationFactory"/> swaps the default scheme for a fake one,
/// so it cannot test sign-in.
/// </summary>
public sealed class AuthFlowFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
{
    public const string ModeId = "stub";

    private readonly SqliteTestDatabase _database = new();
    private readonly Lock _clientLock = new();

    public StubOidcProvider Provider { get; } = new();

    public CapturedLogs Logs { get; } = new();

    public HttpClient NewClient()
    {
        lock (_clientLock)
            return CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    }

    public async Task<T> WithStoreAsync<T>(Func<SessionStore, Task<T>> use)
    {
        await using var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        return await use(scope.ServiceProvider.GetRequiredService<SessionStore>());
    }

    public async Task<T> WithDatabaseAsync<T>(Func<AppDbContext, Task<T>> use)
    {
        await using var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        return await use(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestingConsts.FunctionalTestingEnvName);
        builder.UseSetting($"ConnectionStrings:{DatabaseConsts.DatabaseName}", _database.ConnectionString);
        builder.UseSetting("Auth:Providers:0:Id", ModeId);
        builder.UseSetting("Auth:Providers:0:DisplayName", "Stub");
        builder.UseSetting("Auth:Providers:0:Issuer", StubOidcProvider.Issuer);
        builder.UseSetting("Auth:Providers:0:ClientId", StubOidcProvider.ClientId);
        builder.UseSetting("Auth:Providers:0:ClientSecret", Provider.ClientSecret);
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<OidcHandlerFactory>(_ => Provider);
            services.AddSingleton<ILogEventSink>(Logs);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(Provider.JwtConfiguration()));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
    }
}
