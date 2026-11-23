namespace Recall.FunctionalTests.Auth;

using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Auth;
using Recall.Server.Databases;
using Recall.Server.Domain.Recordings.Transcription;
using Recall.Server.Domain.Tenants;
using Recall.SharedTestHelpers.Utilities;
using Serilog.Core;

public sealed class AuthFlowFactory(IReadOnlyDictionary<string, string?> settings, StubOidcProvider? provider = null) : WebApplicationFactory<Program>
{
    public const string ModeId = "stub";
    public const string OtherModeId = "other";
    public const string OtherIssuer = "https://other-idp.test";
    public const string RowIssuer = "https://row-idp.test";
    public const string ConnectionAddressHeader = "X-Test-Connection-Address";

    private readonly SqliteTestDatabase _database = new();
    private readonly Lock _clientLock = new();
    private readonly AsyncLocal<bool> _inRequest = new();

    public StubOidcProvider Provider { get; } = provider ?? new();

    public CapturedLogs Logs { get; } = new();

    /// <summary>The database of this host. A host whose settings name it boots on the rows of this one.</summary>
    public string ConnectionString => _database.ConnectionString;

    /// <summary>The hasher of the passwords of this host, which counts each check and can hold one.</summary>
    public WatchedHasher Hasher { get; } = new();

    public ITranscriptionEngine? TranscriptionEngine { get; init; }

    /// <summary>The provider of each row of <c>auth_oidc_providers</c> whose issuer is <see cref="RowIssuer"/>.</summary>
    public StubOidcProvider RowProvider { get; } = new(RowIssuer);

    /// <summary>A second provider with the mode ID <see cref="OtherModeId"/>, for a test of one provider that fails next to one that works.</summary>
    public StubOidcProvider? OtherProvider { get; init; }

    /// <summary>A place for the claim file outside the folder of the database, so a test can refuse a rename there and the database still works.</summary>
    public string? ClaimFilePath { get; init; }

    /// <summary>True for a host with no OIDC provider, as a fresh install with no setting has.</summary>
    public bool NoProvider { get; init; }

    /// <summary>The clock of the server, for a test of a value that the server takes from the clock.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>
    /// The pause of a race test on this host, which holds the first command with its tag. A test sets it before the request under test,
    /// and clears it after, so the tests of one host that use it run one at a time.
    /// </summary>
    public PauseAtQuery? Pause { get; set; }

    /// <summary>True on the flow of a request to this host. Each host of the process logs through one static logger, so this tells their lines apart.</summary>
    public bool IsInRequest => _inRequest.Value;

    /// <summary>Runs work that a request did not start, such as a job, so that its lines count as lines of this host.</summary>
    public async Task InRequestFlowAsync(Func<Task> work)
    {
        _inRequest.Value = true;
        try
        {
            await work();
        }
        finally
        {
            _inRequest.Value = false;
        }
    }

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
        if (!NoProvider)
        {
            builder.UseSetting("Auth:Providers:0:Id", ModeId);
            builder.UseSetting("Auth:Providers:0:DisplayName", "Stub");
            builder.UseSetting("Auth:Providers:0:Issuer", StubOidcProvider.Issuer);
            builder.UseSetting("Auth:Providers:0:ClientId", StubOidcProvider.ClientId);
            builder.UseSetting("Auth:Providers:0:ClientSecret", Provider.ClientSecret);
        }

        if (OtherProvider is not null)
        {
            builder.UseSetting("Auth:Providers:1:Id", OtherModeId);
            builder.UseSetting("Auth:Providers:1:DisplayName", "Other");
            builder.UseSetting("Auth:Providers:1:Issuer", OtherIssuer);
            builder.UseSetting("Auth:Providers:1:ClientId", StubOidcProvider.ClientId);
            builder.UseSetting("Auth:Providers:1:ClientSecret", OtherProvider.ClientSecret);
        }

        builder.UseSetting("Jobs:RunWorkers", "false");
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<OidcHandlerFactory>(options => options.Id == OtherModeId ? OtherProvider!
                : options.Issuer?.StartsWith(RowIssuer, StringComparison.Ordinal) == true ? RowProvider
                // A real IdP on this machine, so a test sees the client that the Server gives a provider of the app.
                : options.Issuer?.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) == true ? OidcLoginMode.DefaultHandler(options)
                : Provider);
            // The tests of each host share its two hash slots, so a test waits longer for one than a person does.
            services.AddSingleton(new PasswordHashing(Hasher, TimeSpan.FromMinutes(2)));
            if (ClaimFilePath is { } claimFile)
                services.AddSingleton(provider => new OwnerClaim(claimFile, provider.GetRequiredService<TimeProvider>()));
            if (Clock is { } clock)
                services.AddSingleton(clock);
            if (TranscriptionEngine is { } engine)
            {
                services.RemoveAll<ITranscriptionEngine>();
                services.AddSingleton(engine);
            }

            services.ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(new PauseSlot(this)));
            services.AddSingleton<ILogEventSink>(Logs);
            services.AddSingleton<IStartupFilter, ConnectionAddressFilter>();
            services.AddSingleton<IStartupFilter>(new RequestFlowFilter(_inRequest));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(Provider.JwtConfiguration()));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        HostBoots.ThrowIfClosed();
        return base.CreateHost(builder);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
        if (ClaimFilePath is { } claimFile)
            Directory.Delete(Path.GetDirectoryName(claimFile)!, recursive: true);
    }

    /// <summary>Hands each command to the <see cref="Pause"/> of the host, when a test set one.</summary>
    private sealed class PauseSlot(AuthFlowFactory host) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            => host.Pause?.ReaderExecutingAsync(command, eventData, result, cancellationToken) ?? ValueTask.FromResult(result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => host.Pause?.NonQueryExecutingAsync(command, eventData, result, cancellationToken) ?? ValueTask.FromResult(result);

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
            => host.Pause?.ScalarExecutingAsync(command, eventData, result, cancellationToken) ?? ValueTask.FromResult(result);

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
            => host.Pause?.DataReaderClosingAsync(command, eventData, result) ?? ValueTask.FromResult(result);

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
            => host.Pause?.NonQueryExecutedAsync(command, eventData, result, cancellationToken) ?? ValueTask.FromResult(result);

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
            => host.Pause?.ScalarExecutedAsync(command, eventData, result, cancellationToken) ?? ValueTask.FromResult(result);
    }

    private sealed class RequestFlowFilter(AsyncLocal<bool> inRequest) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextStep) =>
            {
                inRequest.Value = true;
                return nextStep(context);
            });
            next(app);
        };
    }

    /// <summary>The test server has no socket, so this is how a test stands at a connection address.</summary>
    private sealed class ConnectionAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextStep) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[ConnectionAddressHeader], out var address))
                    context.Connection.RemoteIpAddress = address;
                return nextStep(context);
            });
            next(app);
        };
    }
}
