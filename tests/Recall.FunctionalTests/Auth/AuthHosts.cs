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

    public static AuthFlowFactory SecretInFormProvider { get; private set; } = null!;

    public static AuthFlowFactory RollingKeyProvider { get; private set; } = null!;

    public static AuthFlowFactory TwoProviders { get; private set; } = null!;

    public static AuthFlowFactory TwoTenants { get; private set; } = null!;

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
        BundleExport = OtlpCollector.Start();
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
        // A handler and a framework category write through the static logger, and one process has one. Without this, the files and
        // the OTLP export of the Bundle host hold only the lines of its start. The filter keeps out the lines of the other hosts.
        var bundleRequests = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByIncludingOnly(_ => Bundle.IsInRequest)
            .WriteTo.Logger(bundleLogger)
            .CreateLogger();
        Log.Logger = new TeeLogger(new TeeLogger(main, bundleRequests), Log.Logger);
    }

    public static async Task StopAsync()
    {
        foreach (var host in new[] { LostJobTable, FrozenClock, WriterLine, Open, Closed, Limited, Failing, SecretInFormProvider, RollingKeyProvider, TwoProviders, TwoTenants, Bundle, OwnerDemotion, Sweep, Jobs, Cleanups, FailingJobs, HaltedJobs, DemotedLiveStream, NoProviderLogout, DownAtLogout })
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
