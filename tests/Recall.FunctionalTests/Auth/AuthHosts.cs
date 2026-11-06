namespace Recall.FunctionalTests.Auth;

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

    public static AuthFlowFactory OwnerDemotion { get; private set; } = null!;

    public static OtlpCollector BundleExport { get; private set; } = null!;

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

            // The most that an operator can turn on, so the privacy tests of this host see each line. Hosting.Diagnostics stays as
            // shipped because it writes each query string at Information, and docs/telemetry.md says so.
            ["Serilog:MinimumLevel:Default"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Routing.EndpointMiddleware"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:System"] = "Debug",
        });
        _ = Bundle.Services;
        var bundleLogger = Log.Logger;
        Log.Logger = main;
        OwnerDemotion = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
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
        // A handler writes through the static logger, and one process has one. Without this, the files and the OTLP export of the
        // Bundle host hold only the logs of the framework.
        Log.Logger = new TeeLogger(new TeeLogger(main, bundleLogger), Log.Logger);
    }

    public static async Task StopAsync()
    {
        foreach (var host in new[] { Open, Closed, Limited, Failing, SecretInFormProvider, RollingKeyProvider, TwoProviders, TwoTenants, Bundle, OwnerDemotion, Sweep })
        {
            if (host is not null)
                await host.DisposeAsync();
        }

        if (BundleExport is not null)
            await BundleExport.DisposeAsync();
    }

    private static AuthFlowFactory Start(Dictionary<string, string?> settings, StubOidcProvider? provider = null) => Start(new AuthFlowFactory(settings, provider));

    private static AuthFlowFactory Start(AuthFlowFactory host)
    {
        var main = Log.Logger;
        _ = host.Services;
        Log.Logger = main;
        return host;
    }
}
