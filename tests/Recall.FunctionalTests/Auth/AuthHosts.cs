namespace Recall.FunctionalTests.Auth;

using Serilog;

/// <summary>
/// The apps of the sign-in tests. They start before each test, in one thread, because each run of Program replaces the
/// static logger, and each <c>Log.ForContext</c> reads it. So the logger of the main factory goes back after each start.
/// </summary>
public static class AuthHosts
{
    private const string ManyStarts = "100000";

    /// <summary>Sign-up is open and each test uses a new subject, so that tests in parallel do not meet.</summary>
    public static AuthFlowFactory Open { get; private set; } = null!;

    /// <summary>Sign-up is closed. The first login of the host is the Owner.</summary>
    public static AuthFlowFactory Closed { get; private set; } = null!;

    /// <summary>The real start limit and a real <c>AllowedHosts</c>.</summary>
    public static AuthFlowFactory Limited { get; private set; } = null!;

    /// <summary>One test turns this provider off, so it does not share it.</summary>
    public static AuthFlowFactory Failing { get; private set; } = null!;

    /// <summary>Logs at Debug for the sweep that looks for raw values.</summary>
    public static AuthFlowFactory Sweep { get; private set; } = null!;

    public static void Start()
    {
        Open = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });
        Closed = Start(new Dictionary<string, string?> { ["Auth:StartPermitsPerWindow"] = ManyStarts });
        Limited = Start(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" });
        Failing = Start(new Dictionary<string, string?> { ["Auth:OpenSignup"] = "true", ["Auth:StartPermitsPerWindow"] = ManyStarts });

        var main = Log.Logger;
        Sweep = new AuthFlowFactory(new Dictionary<string, string?>
        {
            ["Auth:OpenSignup"] = "true",
            ["Serilog:MinimumLevel:Default"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Routing.EndpointMiddleware"] = "Debug",
            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Debug",
            ["Serilog:MinimumLevel:Override:System"] = "Debug",
        });
        _ = Sweep.Services;
        // A static logger of the Server reaches the sink of the sweep at Debug, and the sinks of the main factory as before
        Log.Logger = new TeeLogger(main, Log.Logger);
    }

    public static async Task StopAsync()
    {
        foreach (var host in new[] { Open, Closed, Limited, Failing, Sweep })
        {
            if (host is not null)
                await host.DisposeAsync();
        }
    }

    private static AuthFlowFactory Start(Dictionary<string, string?> settings)
    {
        var main = Log.Logger;
        var host = new AuthFlowFactory(settings);
        _ = host.Services;
        Log.Logger = main;
        return host;
    }
}
