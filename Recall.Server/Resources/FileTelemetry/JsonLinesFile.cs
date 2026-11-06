namespace Recall.Server.Resources.FileTelemetry;

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

public sealed class JsonLinesFile(string path, RollingFileLimits limits, FileSinkFailureNotice notice) : IDisposable
{
    private const string LineProperty = "Line";

    private readonly Logger _lines = new LoggerConfiguration()
        .WriteTo.RollingFilesWithNoQueue(new LineOnly(), path, limits, notice)
        .CreateLogger();

    public JsonLinesFile(string path, RollingFileLimits limits)
        : this(path, limits, FileSinkFailureNotice.OnTheConsole(path))
    {
    }

    public void Write(string line) => _lines.Write(new LogEvent(
        DateTimeOffset.UtcNow,
        LogEventLevel.Information,
        exception: null,
        MessageTemplate.Empty,
        [new LogEventProperty(LineProperty, new ScalarValue(line))]));

    /// <summary>
    /// A throw from here would end the process on the thread of a span export, and would lose the batch on the thread of a
    /// metric export. The heap can be full when this runs, and then the notice can throw too.
    /// </summary>
    public void ReportLostLine(Exception exception)
    {
        try
        {
            notice.OnLoggingFailed(this, LoggingFailureKind.Permanent, "An export lost a line.", null, exception);
        }
        catch (Exception)
        {
            // Nothing is left to report to
        }
    }

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
