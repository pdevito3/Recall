namespace Recall.Server.Domain.Recordings.Transcription;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

public sealed class LocalParakeetEngine(ModelStore modelStore, IOptions<TranscriptionOptions> options) : ITranscriptionEngine
{
    private const string WorkAudioName = "work.wav";
    private const int BytesPerSample = 2;
    private const int SigkillExitCode = 137;

    private static readonly JsonSerializerOptions WorkerLines = new(JsonSerializerDefaults.Web);

    public TranscriptionEngineKind Kind => TranscriptionEngineKind.Local;

    public bool IsReady => modelStore.IsReady;

    public async IAsyncEnumerable<TranscriptChunk> RunAsync(
        TranscriptionInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wav = await WorkAudioAsync(input, cancellationToken);
        var settings = options.Value;

        using var process = Start(WorkerCommand(settings, wav, input.StartSample), settings.Threads);
        using var killOnCancel = cancellationToken.Register(() => KillQuietly(process));
        var discardErrors = DiscardAsync(process.StandardError);
        try
        {
            while (true)
            {
                var line = await ReadLineBeforeStallAsync(process, settings, cancellationToken);
                if (line is null)
                    break;

                yield return ParseChunk(line);
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new TranscriptionEngineException(ProblemOfExit(process.ExitCode));
        }
        finally
        {
            KillQuietly(process);
            await discardErrors;
        }
    }

    public static RecordingProblem ProblemOfExit(int exitCode)
        => exitCode == SigkillExitCode ? RecordingProblem.OutOfMemory : RecordingProblem.EngineCrashed;

    private async Task<string> WorkAudioAsync(TranscriptionInput input, CancellationToken cancellationToken)
    {
        var wav = Path.Combine(input.WorkFolder, WorkAudioName);
        if (!File.Exists(wav))
            await DecodeAsync(input.AudioPath, wav, cancellationToken);

        var samples = new FileInfo(wav).Length / BytesPerSample;
        if (samples > Recording.MaxDuration.TotalSeconds * Recording.SamplesPerSecond)
            throw new TranscriptionEngineException(RecordingProblem.TooLong);

        return wav;
    }

    private async Task DecodeAsync(string audio, string wav, CancellationToken cancellationToken)
    {
        var partial = wav + ".part";
        var longest = ((int)Recording.MaxDuration.TotalSeconds + 1).ToString(CultureInfo.InvariantCulture);
        string[] command =
        [
            options.Value.FfmpegPath, "-nostdin", "-y", "-v", "error", "-threads", "1", "-i", audio, "-t", longest,
            "-map_metadata", "-1", "-bitexact", "-ar", Recording.SamplesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", partial,
        ];

        using var process = Start(command, threads: 1);
        using var killOnCancel = cancellationToken.Register(() => KillQuietly(process));
        process.StandardInput.Close();
        var discardOutput = DiscardAsync(process.StandardOutput);
        await DiscardAsync(process.StandardError);
        await discardOutput;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            File.Delete(partial);
            throw new TranscriptionEngineException(process.ExitCode == SigkillExitCode
                ? RecordingProblem.OutOfMemory
                : RecordingProblem.AudioUnreadable);
        }

        File.Move(partial, wav, overwrite: true);
    }

    private string[] WorkerCommand(TranscriptionOptions settings, string wav, long startSample)
    {
        string[] arguments =
        [
            wav, modelStore.Folder, settings.Threads.ToString(CultureInfo.InvariantCulture),
            startSample.ToString(CultureInfo.InvariantCulture),
        ];
        return RunsUnderDotnet(settings.WorkerPath)
            ? [Environment.ProcessPath!, settings.WorkerPath, .. arguments]
            : [settings.WorkerPath, .. arguments];
    }

    private static bool RunsUnderDotnet(string workerPath) => workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

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

    private static TranscriptChunk ParseChunk(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<TranscriptChunk>(line, WorkerLines)
                ?? throw new TranscriptionEngineException(RecordingProblem.EngineCrashed);
        }
        catch (JsonException)
        {
            throw new TranscriptionEngineException(RecordingProblem.EngineCrashed);
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
