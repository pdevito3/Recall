namespace Recall.FunctionalTests.TestUtilities;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Serilog.Core;
using Serilog.Events;

public sealed class CapturedLogs : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public IReadOnlyList<LogEvent> All => _events.ToList();

    public IReadOnlyList<LogEvent> InTrace(ActivityTraceId traceId) =>
        _events.Where(e => e.TraceId == traceId).ToList();

    public IReadOnlyList<LogEvent> Holding(string text) =>
        _events.Where(e => e.RenderMessage(CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal)
            || e.Properties.Values.Any(value => value.ToString().Contains(text, StringComparison.Ordinal))
            || e.Exception?.ToString().Contains(text, StringComparison.Ordinal) == true).ToList();
}
