namespace Recall.Server.Domain.Recordings.Transcription;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>A process that an engine starts, with no secret of the server, and the reads of its exit that each engine needs.</summary>
internal static class ChildProcess
{
    public const int SigkillExitCode = 137;

    private const int ErrorTailLength = 4096;
    private const string OomKillCounter = "oom_kill ";

    public static Process Start(string[] command, int threads)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1))
            start.ArgumentList.Add(argument);

        GiveNoSecretOfTheServer(start, threads);

        try
        {
            return Process.Start(start)!;
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new TranscriptionEngineException(RecordingProblem.EngineNotInstalled);
        }
    }

    private static void GiveNoSecretOfTheServer(ProcessStartInfo start, int threads)
    {
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["DOTNET_PROCESSOR_COUNT"] = threads.ToString(CultureInfo.InvariantCulture);
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_gcConcurrent"] = "0";
    }

    public static RecordingProblem ProblemOfKill(string memoryEventsPath, long? oomKillsBefore)
        => ReadOomKills(memoryEventsPath) > oomKillsBefore ? RecordingProblem.OutOfMemory : RecordingProblem.EngineKilled;

    // The kernel counts an OOM kill before it sends the signal, so the count is true by the time the exit code is.
    public static long? ReadOomKills(string memoryEventsPath)
    {
        try
        {
            foreach (var line in File.ReadLines(memoryEventsPath))
            {
                if (line.StartsWith(OomKillCounter, StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(OomKillCounter.Length), CultureInfo.InvariantCulture, out var kills))
                    return kills;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public static async Task DiscardAsync(StreamReader output)
    {
        var buffer = new char[4096];
        try
        {
            while (await output.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
    }

    public static async Task<string> ReadTailAsync(StreamReader output)
    {
        var tail = new StringBuilder();
        var buffer = new char[ErrorTailLength];
        try
        {
            int read;
            while ((read = await output.ReadAsync(buffer)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > ErrorTailLength)
                    tail.Remove(0, tail.Length - ErrorTailLength);
            }
        }
        catch (IOException)
        {
        }

        return tail.ToString();
    }

    public static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException)
        {
        }
    }
}
