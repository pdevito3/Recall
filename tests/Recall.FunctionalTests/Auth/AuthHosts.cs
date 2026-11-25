namespace Recall.FunctionalTests.Auth;

using Microsoft.Extensions.Time.Testing;
using Recall.Server.Auth;
using Recall.Server.Databases;
using Serilog;

public static class AuthHosts
{
    private const string ManyStarts = "100000";

    public static AuthFlowFactory Open { get; private set; } = null!;

    public static AuthFlowFactory Closed { get; private set; } = null!;

    public static AuthFlowFactory Limited { get; private set; } = null!;

    public static AuthFlowFactory Failing { get; private set; } = null!;

    public static AuthFlowFactory Sweep { get; private set; } = null!;

    /// <summary>A host with no OIDC provider, so it has only the password login. Its <see cref="AuthFlowFactory.Logs"/> hold the lines of its requests.</summary>
    public static AuthFlowFactory Password { get; private set; } = null!;

    /// <summary>A host that only <see cref="LoginProviderTests"/> uses, because a provider of the app is install-wide.</summary>
    public static AuthFlowFactory LoginProviders { get; private set; } = null!;

    /// <summary>A host with no entry of <c>Auth:Providers</c> and with <c>Auth:AllowLoopbackProviders</c>, which only <see cref="LoginProviderTests"/> uses.</summary>
    public static AuthFlowFactory LoginProvidersNoEnvironment { get; private set; } = null!;

    /// <summary>A host with no provider whose database holds a Usable row before <see cref="RowAtBoot"/> boots on it.</summary>
    public static AuthFlowFactory RowAtBootDatabase { get; private set; } = null!;

    /// <summary>A fresh host with no provider that boots on the database of <see cref="RowAtBootDatabase"/>, which only <see cref="LoginProviderTests"/> uses.</summary>
    public static AuthFlowFactory RowAtBoot { get; private set; } = null!;

    /// <summary>A host with closed signup that only <see cref="ExternalLoginLinkTests"/> uses, so a link is never the work of the provisioner.</summary>
    public static AuthFlowFactory Links { get; private set; } = null!;

    /// <summary>A host whose provider lists <c>prompt_values_supported</c> without <c>login</c>, which only <see cref="ExternalLoginLinkTests"/> uses.</summary>
    public static AuthFlowFactory NoPromptLogin { get; private set; } = null!;

    /// <summary>
    /// A host whose password login only <see cref="PasswordsOffTests"/> turns, because the setting is install-wide. It has a claim file of
    /// its own. Each tenant that a test adds keeps an Admin with a link to its provider, so a later test can turn passwords off.
    /// </summary>
    public static AuthFlowFactory PasswordsOff { get; private set; } = null!;

    /// <summary>A host with no provider whose password login only <see cref="PasswordsOffTests"/> turns Off, so it has no login method.</summary>
    public static AuthFlowFactory NoLoginMethod { get; private set; } = null!;

    /// <summary>A fresh host with no provider, which only the setup test uses.</summary>
    public static AuthFlowFactory Setup { get; private set; } = null!;

    /// <summary>A fresh host with no provider, which only the privacy test of the setup uses. Its <see cref="AuthFlowFactory.Logs"/> hold the lines of its requests.</summary>
    public static AuthFlowFactory SetupLogs { get; private set; } = null!;

    /// <summary>A fresh host with a provider, which only the test of its first login uses, so it has no tenant before that test.</summary>
    public static AuthFlowFactory SetupWithProvider { get; private set; } = null!;

    /// <summary>A host with a provider and a person with a password, <see cref="MixedPerson"/>, so its login page offers both.</summary>
    public static AuthFlowFactory Mixed { get; private set; } = null!;

    public static PasswordPerson MixedPerson { get; private set; } = null!;

    /// <summary>A host with no provider and a claim file of its own. <see cref="ClaimOwner"/> is the Admin of its oldest tenant, so each claim picks it.</summary>
    public static AuthFlowFactory PasswordClaim { get; private set; } = null!;

    public static PasswordPerson ClaimOwner { get; private set; } = null!;

    public static AuthFlowFactory SecretInFormProvider { get; private set; } = null!;

    public static AuthFlowFactory RollingKeyProvider { get; private set; } = null!;

    public static AuthFlowFactory TwoProviders { get; private set; } = null!;

    public static AuthFlowFactory TwoTenants { get; private set; } = null!;

    /// <summary>A host whose <c>Auth:ClientId</c> is <see cref="FirstPartyClientId"/>, so a provider token of that client acts as its user.</summary>
    public static AuthFlowFactory FirstPartyClient { get; private set; } = null!;

    public const string FirstPartyClientId = "swagger-ui";

    public static AuthFlowFactory Bundle { get; private set; } = null!;

    public static AuthFlowFactory Jobs { get; private set; } = null!;

    public static AuthFlowFactory Cleanups { get; private set; } = null!;

    public static HeldTranscriptionEngine HeldTranscription { get; } = new();

    public static AuthFlowFactory FailingJobs { get; private set; } = null!;

    public static AuthFlowFactory HaltedJobs { get; private set; } = null!;

    public static AuthFlowFactory DemotedLiveStream { get; private set; } = null!;

    public static AuthFlowFactory OwnerDemotion { get; private set; } = null!;

    /// <summary>A host whose Owner only the demotion test of <see cref="Recall.FunctionalTests.FunctionalTests.Jobs.JobMcpTests"/> demotes.</summary>
    public static AuthFlowFactory JobToolsDemotion { get; private set; } = null!;

    public static OtlpCollector BundleExport { get; private set; } = null!;

    public static AuthFlowFactory NoProviderLogout { get; private set; } = null!;

    public static AuthFlowFactory DownAtLogout { get; private set; } = null!;

    public static AuthFlowFactory LostJobTable { get; private set; } = null!;

    public static AuthFlowFactory FrozenClock { get; private set; } = null!;

    /// <summary>A host whose writer line only the tests of <see cref="WriterLineTests"/> hold, with a clock that they move.</summary>
    public static AuthFlowFactory WriterLine { get; private set; } = null!;

    public static FakeTimeProvider WriterLineClock { get; } = new(DateTimeOffset.UtcNow);

    public static AuthFlowFactory FileStoreRestart { get; private set; } = null!;

    /// <summary>A host on Kestrel, so a test can send two requests on one keep-alive connection. Only <see cref="PersonalAccessTokenTests"/> holds its writer line.</summary>
    public static AuthFlowFactory KeepAlive { get; private set; } = null!;

    public static void Start()
    {
        var main = Log.Logger;
        Open = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        Closed = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts });
        Limited = Start(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" });
        Failing = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        SecretInFormProvider = Start(
            new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts },
            new StubOidcProvider { AuthMethods = ["client_secret_post"], ListsIssParameter = false });
        RollingKeyProvider = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        TwoTenants = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        FirstPartyClient = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts, ["Auth:ClientId"] = FirstPartyClientId });
        BundleExport = OtlpCollector.Start();
        Setup = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts }) { NoProvider = true });
        SetupWithProvider = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts });
        Mixed = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts });
        LoginProviders = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts });
        Links = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts });
        PasswordsOff = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts })
        {
            ClaimFilePath = Path.Combine(Directory.CreateTempSubdirectory("recall-claim-").FullName, OwnerClaim.FileName),
        });
        NoLoginMethod = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts }) { NoProvider = true });
        NoPromptLogin = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts }, new StubOidcProvider { PromptValues = ["none", "consent"] });
        LoginProvidersNoEnvironment = Start(new AuthFlowFactory(new Dictionary<string, string?>
        {
            ["Auth:StartPermitsPerWindow"] = ManyStarts,
            ["Auth:AllowLoopbackProviders"] = "true",
        }) { NoProvider = true });
        RowAtBootDatabase = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts }) { NoProvider = true });
        LoginProviderTests.AddUsableRowAsync(RowAtBootDatabase).GetAwaiter().GetResult();
        RowAtBoot = Start(new AuthFlowFactory(new Dictionary<string, string?>
        {
            ["Auth:StartPermitsPerWindow"] = ManyStarts,
            [$"ConnectionStrings:{DatabaseConsts.DatabaseName}"] = RowAtBootDatabase.ConnectionString,
        }) { NoProvider = true });
        MixedPerson = PasswordPeople.AddAsync(Mixed).GetAwaiter().GetResult();
        PasswordClaim = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts })
        {
            NoProvider = true,
            ClaimFilePath = Path.Combine(Directory.CreateTempSubdirectory("recall-claim-").FullName, OwnerClaim.FileName),
        });
        ClaimOwner = PasswordPeople.AddAsync(PasswordClaim).GetAwaiter().GetResult();
        Bundle = new AuthFlowFactory(new Dictionary<string, string?>
        {
            ["Auth:OpenSignup"] = "true",
            ["Auth:StartPermitsPerWindow"] = ManyStarts,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = BundleExport.Endpoint,

            // Debug in each category, so the privacy tests of this host see more than a shipped server writes. Hosting.Diagnostics
            // stays as shipped, because it writes each query string at Information, and docs/telemetry.md says so.
            ["Serilog:MinimumLevel:Default"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Routing.EndpointMiddleware"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.Extensions.Diagnostics.HealthChecks"] = "Debug",
            ["Serilog:MinimumLevel:Override:System"] = "Debug",
            ["Serilog:MinimumLevel:Override:ZiggyCreatures.Caching.Fusion"] = "Debug",
        });
        _ = Bundle.Services;
        var bundleLogger = Log.Logger;
        Log.Logger = main;
        Jobs = StartWithWorker();
        Cleanups = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts, ["Jobs:RunWorkers"] = "true" })
        {
            TranscriptionEngine = HeldTranscription,
        });
        FailingJobs = Start(new Dictionary<string, string?>
        {
            ["Auth:OpenSignup"] = "true",
            ["Auth:StartPermitsPerWindow"] = ManyStarts,
            ["Jobs:RunWorkers"] = "true",
            ["Jobs:HealthJob:FailOnPurpose"] = "true",
        });
        HaltedJobs = StartWithWorker();
        DemotedLiveStream = StartWithWorker();
        OwnerDemotion = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        JobToolsDemotion = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        NoProviderLogout = Start(
            new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts },
            new StubOidcProvider { EndSessionEndpoint = null });
        DownAtLogout = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        LostJobTable = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        FrozenClock = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts })
        {
            Clock = new FakeTimeProvider(DateTimeOffset.UtcNow),
        });
        WriterLine = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts })
        {
            Clock = WriterLineClock,
        });
        FileStoreRestart = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts }) { NoProvider = true });
        KeepAlive = new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts });
        KeepAlive.UseKestrel(0);
        KeepAlive.StartServer();
        Log.Logger = main;
        TwoProviders = Start(new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts })
        {
            OtherProvider = new StubOidcProvider(AuthFlowFactory.OtherIssuer),
        });

        Sweep = new AuthFlowFactory(new Dictionary<string, string?>
        {
            ["Auth:OpenSignup"] = "true",
            ["Auth:StartPermitsPerWindow"] = ManyStarts,
            ["Serilog:MinimumLevel:Default"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Routing.EndpointMiddleware"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:System"] = "Debug",
        })
        {
            ClaimFilePath = Path.Combine(Directory.CreateTempSubdirectory("recall-claim-").FullName, OwnerClaim.FileName),
        };
        _ = Sweep.Services;
        var sweepLogger = Log.Logger;
        Password = new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts }) { NoProvider = true };
        _ = Password.Services;
        var passwordLogger = Log.Logger;
        var passwordRequests = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByIncludingOnly(_ => Password.IsInRequest)
            .WriteTo.Logger(passwordLogger)
            .CreateLogger();
        SetupLogs = new AuthFlowFactory(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts }) { NoProvider = true };
        _ = SetupLogs.Services;
        var setupLogger = Log.Logger;
        var setupRequests = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByIncludingOnly(_ => SetupLogs.IsInRequest)
            .WriteTo.Logger(setupLogger)
            .CreateLogger();
        // A handler and a framework category write through the static logger, and one process has one. Without this, the files and
        // the OTLP export of the Bundle host hold only the lines of its start. The filter keeps out the lines of the other hosts.
        var bundleRequests = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByIncludingOnly(_ => Bundle.IsInRequest)
            .WriteTo.Logger(bundleLogger)
            .CreateLogger();
        Log.Logger = new TeeLogger(new TeeLogger(new TeeLogger(new TeeLogger(main, bundleRequests), passwordRequests), setupRequests), sweepLogger);
    }

    public static async Task StopAsync()
    {
        // RowAtBoot runs on the database of RowAtBootDatabase, so it stops first.
        foreach (var host in new[] { Password, LoginProviders, Links, PasswordsOff, NoLoginMethod, NoPromptLogin, LoginProvidersNoEnvironment, RowAtBoot, RowAtBootDatabase, SetupLogs, Setup, SetupWithProvider, Mixed, PasswordClaim, LostJobTable, FrozenClock, WriterLine, FileStoreRestart, KeepAlive, Open, Closed, Limited, Failing, SecretInFormProvider, RollingKeyProvider, TwoProviders, TwoTenants, FirstPartyClient, Bundle, OwnerDemotion, JobToolsDemotion, Sweep, Jobs, Cleanups, FailingJobs, HaltedJobs, DemotedLiveStream, NoProviderLogout, DownAtLogout })
        {
            if (host is not null)
                await host.DisposeAsync();
        }

        if (BundleExport is not null)
            await BundleExport.DisposeAsync();
    }

    private static AuthFlowFactory StartWithWorker() =>
        Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts, ["Jobs:RunWorkers"] = "true" });

    private static AuthFlowFactory Start(Dictionary<string, string?> settings, StubOidcProvider? provider = null) => Start(new AuthFlowFactory(settings, provider));

    /// <remarks>
    /// Only <see cref="Start()"/> calls this, before the first test. The boot of a host sets the static logger of the process to its own
    /// logger until this puts the tee back, so a host that a test boots takes the static lines of each other host for that time. The
    /// stop of a host closes the static logger, so each host also runs until the end of the run.
    /// </remarks>
    private static AuthFlowFactory Start(AuthFlowFactory host)
    {
        var main = Log.Logger;
        _ = host.Services;
        Log.Logger = main;
        return host;
    }
}
