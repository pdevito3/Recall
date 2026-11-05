namespace Recall.Server.Resources.Entrypoints.Auth;

using Serilog.Core;
using Serilog.Events;

/// <summary>
/// Drops the framework request lines of the sign-in routes. These lines hold the raw query, and a query on these routes
/// holds a state, a code, or a challenge. The request log of Serilog stays, because it has only the path.
/// </summary>
public sealed class AuthQueryLogFilter : ILogEventFilter
{
    private const string FrameworkRequestLines = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public bool IsEnabled(LogEvent logEvent)
        => !(logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var source)
            && source is ScalarValue { Value: FrameworkRequestLines }
            && logEvent.Properties.TryGetValue("Path", out var path)
            && path is ScalarValue { Value: string value }
            && value.StartsWith("/auth/", StringComparison.Ordinal));
}
