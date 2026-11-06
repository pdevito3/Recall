namespace Recall.Server.Resources.FileTelemetry;

using Serilog.Core;
using Serilog.Events;

/// <summary>
/// A file sink cannot report its own failure to a file, so the failure goes to the console. Each failed line calls this,
/// so a fault gives one console line in each <see cref="Interval"/>, and each file has its own notice.
/// </summary>
public sealed class FileSinkFailureNotice(string file, TimeProvider clock, TextWriter console) : ILoggingFailureListener
{
    public const string Text = "A file sink failed";

    // Serilog.Sinks.File 7.0.0 opens a file again 30 minutes after a failed open, so a longer fault gets a new line at each try
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly Lock _lock = new();
    private DateTimeOffset _nextLineAt = DateTimeOffset.MinValue;

    public static FileSinkFailureNotice OnTheConsole(string file) => new(file, TimeProvider.System, Console.Out);

    public void OnLoggingFailed(object sender, LoggingFailureKind kind, string message, IReadOnlyCollection<LogEvent>? events, Exception? exception)
    {
        // A temporary failure is a step of a retry, and it loses no line
        if (kind == LoggingFailureKind.Temporary)
            return;

        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (now < _nextLineAt)
                return;

            _nextLineAt = now + Interval;
        }

        WriteLine(now, Text + " for {File}. The console keeps each log, and the file can lose lines. The cause: {Cause}", exception?.Message ?? message);
    }

    /// <summary>Each file closes once, so this line has no interval.</summary>
    public void OnCloseFailed(Exception exception) => WriteLine(
        clock.GetUtcNow(),
        "The file {File} did not close at the stop. The lines that it still held are lost, and the console keeps each log. The cause: {Cause}",
        exception.Message);

    private void WriteLine(DateTimeOffset now, string template, string cause) => console.WriteLine(JsonLine.Of(json =>
    {
        json.WriteString("@t", now.UtcDateTime);
        json.WriteString("@mt", template);
        json.WriteString("@l", nameof(LogEventLevel.Warning));
        json.WriteString("File", file);
        json.WriteString("Cause", TelemetryText.Cap(cause));
    }));
}
