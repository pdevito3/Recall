namespace Recall.Server.Resources.FileTelemetry;

using Serilog;
using Serilog.Configuration;
using Serilog.Formatting;

public static class RollingFileSinks
{
    private const int LinesThatWaitForTheDisk = 10_000;

    /// <summary>When the disk hangs and the queue is full, the line goes and the request continues.</summary>
    public static LoggerConfiguration RollingFiles(this LoggerSinkConfiguration writeTo, ITextFormatter formatter, string path, RollingFileLimits limits, FileSinkFailureNotice? notice = null)
    {
        var fileNotice = notice ?? FileSinkFailureNotice.OnTheConsole(path);
        return writeTo.Fallible(
            queued => queued.Async(
                to => to.OneRollingFile(formatter, path, limits, fileNotice),
                bufferSize: LinesThatWaitForTheDisk,
                blockWhenFull: false),
            fileNotice);
    }

    public static LoggerConfiguration RollingFilesWithNoQueue(this LoggerSinkConfiguration writeTo, ITextFormatter formatter, string path, RollingFileLimits limits)
        => writeTo.OneRollingFile(formatter, path, limits, FileSinkFailureNotice.OnTheConsole(path));

    /// <summary>
    /// The age limit acts when a file opens, so at each start and at each roll. The roll of each day thus removes an old
    /// file on a server with few lines.
    /// </summary>
    private static LoggerConfiguration OneRollingFile(this LoggerSinkConfiguration writeTo, ITextFormatter formatter, string path, RollingFileLimits limits, FileSinkFailureNotice notice)
        => writeTo.Fallible(
            to => to.File(
                formatter,
                path,
                fileSizeLimitBytes: limits.FileSizeBytes,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: limits.FilesKept,
                retainedFileTimeLimit: TimeSpan.FromDays(limits.DaysKept)),
            notice);
}
