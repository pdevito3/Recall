namespace Recall.Server.Resources.FileTelemetry;

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

/// <summary>
/// One line of JSON for each span or metric point, in rotating files. Serilog.Sinks.File keeps the limits, so an exporter
/// holds no rotation code. This logger is its own: it writes no other event, and the app logger does not see its lines.
/// </summary>
public sealed class JsonLinesFile(string path, RollingFileLimits limits) : IDisposable
{
    private const string LineProperty = "Line";

    private readonly Logger _lines = new LoggerConfiguration()
        .WriteTo.RollingFilesWithNoQueue(new LineOnly(), path, limits)
        .CreateLogger();

    public void Write(string line) => _lines.Write(new LogEvent(
        DateTimeOffset.UtcNow,
        LogEventLevel.Information,
        exception: null,
        MessageTemplate.Empty,
        [new LogEventProperty(LineProperty, new ScalarValue(line))]));

    public void Dispose() => _lines.Dispose();

    private sealed class LineOnly : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            output.Write(((ScalarValue)logEvent.Properties[LineProperty]).Value);
            output.Write('\n');
        }
    }
}
