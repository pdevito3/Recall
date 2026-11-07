namespace Recall.Server.Domain.Recordings.Transcription;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

public sealed class LocalParakeetEngine(ModelStore modelStore, IOptions<TranscriptionOptions> options) : ITranscriptionEngine
{
    private const int BytesPerSample = 2;
    private const int SigkillExitCode = 137;
    private const int ErrorTailLength = 4096;
    private const string OomKillCounter = "oom_kill ";

    // The words of ffmpeg 7.1.3 on its error stream, under the C locale of the clean environment of Start. The exit code
    // is not stable across ffmpeg versions, and 134 is both SIGABRT and the quota error, so only these words name a cause.
    // Bad audio is named last and only by the words that were seen for it, so a new wording reads EngineCrashed and never
    // asks a person to record again.
    private static readonly (string Words, RecordingProblem Problem)[] DecodeErrors =
    [
        ("No space left on device", RecordingProblem.DiskFull),
        ("Disk quota exceeded", RecordingProblem.DiskFull),
        ("Read-only file system", RecordingProblem.DiskRefused),
        ("Permission denied", RecordingProblem.DiskRefused),
        ("Error opening input files: Invalid data found when processing input", RecordingProblem.AudioUnreadable),
        ("Error opening input files: End of file", RecordingProblem.AudioUnreadable),
        ("no decoder found for", RecordingProblem.AudioUnreadable),
        ("does not contain any stream", RecordingProblem.AudioUnreadable),
    ];

    private static readonly JsonSerializerOptions WorkerLines = new(JsonSerializerDefaults.Web);

    public TranscriptionEngineKind Kind => TranscriptionEngineKind.Local;

    public bool IsReady => modelStore.IsReady;

    public async IAsyncEnumerable<TranscriptChunk> RunAsync(
        TranscriptionInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wav = await WorkAudioAsync(input, cancellationToken);
        var settings = options.Value;

        var oomKillsBefore = ReadOomKills();
        using var process = Start(WorkerCommand(settings, wav, input.StartSample), settings.Threads);
        using var killOnCancel = cancellationToken.Register(() => KillQuietly(process));
        var discardErrors = DiscardAsync(process.StandardError);
        try
        {
            var endedOnBrokenLine = false;
            while (await ReadLineBeforeStallAsync(process, settings, cancellationToken) is { } line)
            {
                if (ParseChunk(line) is { } chunk)
                {
                    yield return chunk;
                    continue;
                }

                // A kill can cut the last line, and then the exit code says what happened. A broken line with more after it
                // comes from a worker that still runs, so it is a crash with no need to wait.
                if (await ReadLineBeforeStallAsync(process, settings, cancellationToken) is not null)
                    throw new TranscriptionEngineException(RecordingProblem.EngineCrashed);

                endedOnBrokenLine = true;
                break;
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == SigkillExitCode)
                throw new TranscriptionEngineException(ProblemOfKill(oomKillsBefore));
            if (process.ExitCode != 0 || endedOnBrokenLine)
                throw new TranscriptionEngineException(RecordingProblem.EngineCrashed);
        }
        finally
        {
            KillQuietly(process);
            await discardErrors;
        }
    }

    private RecordingProblem ProblemOfKill(long? oomKillsBefore)
        => ReadOomKills() > oomKillsBefore ? RecordingProblem.OutOfMemory : RecordingProblem.EngineKilled;

    // The kernel counts an OOM kill before it sends the signal, so the count is true by the time the exit code is.
    private long? ReadOomKills()
    {
        try
        {
            foreach (var line in File.ReadLines(options.Value.MemoryEventsPath))
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

    private async Task<string> WorkAudioAsync(TranscriptionInput input, CancellationToken cancellationToken)
    {
        var wav = Path.Combine(input.WorkFolder, RecordingFiles.WorkAudioName);
        if (!File.Exists(wav))
            await DecodeAsync(input.AudioPath, wav, Path.Combine(input.WorkFolder, RecordingFiles.PartialWorkAudioName), cancellationToken);

        var samples = new FileInfo(wav).Length / BytesPerSample;
        if (samples > Recording.MaxDuration.TotalSeconds * Recording.SamplesPerSecond)
            throw new TranscriptionEngineException(RecordingProblem.TooLong);

        return wav;
    }

    private async Task DecodeAsync(string audio, string wav, string partial, CancellationToken cancellationToken)
    {
        var longest = ((int)Recording.MaxDuration.TotalSeconds + 1).ToString(CultureInfo.InvariantCulture);
        string[] command =
        [
            options.Value.FfmpegPath, "-nostdin", "-y", "-v", "error", "-threads", "1", "-i", audio, "-t", longest,
            "-map_metadata", "-1", "-bitexact", "-ar", Recording.SamplesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", partial,
        ];

        var oomKillsBefore = ReadOomKills();
        using var process = Start(command, threads: 1);
        using var killOnCancel = cancellationToken.Register(() => KillQuietly(process));
        process.StandardInput.Close();
        var discardOutput = DiscardAsync(process.StandardOutput);
        var errorTail = await ReadTailAsync(process.StandardError);
        await discardOutput;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            File.Delete(partial);
            throw new TranscriptionEngineException(process.ExitCode == SigkillExitCode
                ? ProblemOfKill(oomKillsBefore)
                : ProblemOfDecodeError(errorTail));
        }

        File.Move(partial, wav, overwrite: true);
    }

    private static RecordingProblem ProblemOfDecodeError(string errorTail)
    {
        foreach (var (words, problem) in DecodeErrors)
        {
            if (errorTail.Contains(words, StringComparison.Ordinal))
                return problem;
        }

        return RecordingProblem.EngineCrashed;
    }

    private string[] WorkerCommand(TranscriptionOptions settings, string wav, long startSample)
    {
        if (!File.Exists(settings.WorkerPath))
            throw new TranscriptionEngineException(RecordingProblem.EngineNotInstalled);

        string[] arguments =
        [
            wav, modelStore.Folder, settings.Threads.ToString(CultureInfo.InvariantCulture),
            startSample.ToString(CultureInfo.InvariantCulture),
        ];
        return RunsUnderDotnet(settings.WorkerPath)
            ? [DotnetHost(), settings.WorkerPath, .. arguments]
            : [settings.WorkerPath, .. arguments];
    }

    private static bool RunsUnderDotnet(string workerPath) => workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static string DotnetHost()
        => Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath! : "dotnet";

    private static Process Start(string[] command, int threads)
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

    private static async Task<string?> ReadLineBeforeStallAsync(
        Process process, TranscriptionOptions settings, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(TimeSpan.FromMinutes(settings.StallMinutes));
        try
        {
            return await process.StandardOutput.ReadLineAsync(stall.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionEngineException(RecordingProblem.EngineStalled);
        }
    }

    private static TranscriptChunk? ParseChunk(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<TranscriptChunk>(line, WorkerLines);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task DiscardAsync(StreamReader output)
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

    private static async Task<string> ReadTailAsync(StreamReader output)
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

    private static void KillQuietly(Process process)
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
