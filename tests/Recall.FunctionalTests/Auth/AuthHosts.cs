namespace Recall.FunctionalTests.Auth;

using Microsoft.Extensions.Time.Testing;
using Recall.Server.Auth;
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

    /// <summary>A fresh host with no provider, which only the setup test uses.</summary>
    public static AuthFlowFactory Setup { get; private set; } = null!;

    /// <summary>A host with a provider and a person with a password, <see cref="MixedPerson"/>, so its login page offers both.</summary>
    public static AuthFlowFactory Mixed { get; private set; } = null!;

    public static PasswordPerson MixedPerson { get; private set; } = null!;

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

    public static OtlpCollector BundleExport { get; private set; } = null!;

    public static AuthFlowFactory NoProviderLogout { get; private set; } = null!;

    public static AuthFlowFactory DownAtLogout { get; private set; } = null!;

    public static AuthFlowFactory LostJobTable { get; private set; } = null!;

    public static AuthFlowFactory FrozenClock { get; private set; } = null!;

    /// <summary>A host whose writer line only the tests of <see cref="WriterLineTests"/> hold, with a clock that they move.</summary>
    public static AuthFlowFactory WriterLine { get; private set; } = null!;

    public static FakeTimeProvider WriterLineClock { get; } = new(DateTimeOffset.UtcNow);

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
        Mixed = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts });
        MixedPerson = PasswordPeople.AddAsync(Mixed).GetAwaiter().GetResult();
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
        // A handler and a framework category write through the static logger, and one process has one. Without this, the files and
        // the OTLP export of the Bundle host hold only the lines of its start. The filter keeps out the lines of the other hosts.
        var bundleRequests = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByIncludingOnly(_ => Bundle.IsInRequest)
            .WriteTo.Logger(bundleLogger)
            .CreateLogger();
        Log.Logger = new TeeLogger(new TeeLogger(new TeeLogger(main, bundleRequests), passwordRequests), sweepLogger);
    }

    public static async Task StopAsync()
    {
        foreach (var host in new[] { Password, Setup, Mixed, LostJobTable, FrozenClock, WriterLine, KeepAlive, Open, Closed, Limited, Failing, SecretInFormProvider, RollingKeyProvider, TwoProviders, TwoTenants, FirstPartyClient, Bundle, OwnerDemotion, Sweep, Jobs, Cleanups, FailingJobs, HaltedJobs, DemotedLiveStream, NoProviderLogout, DownAtLogout })
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

    private static AuthFlowFactory Start(AuthFlowFactory host)
    {
        var main = Log.Logger;
        _ = host.Services;
        Log.Logger = main;
        return host;
    }
}
