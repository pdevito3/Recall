namespace Recall.Server.Domain.Recordings.Transcription;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

public sealed class LocalParakeetEngine(ModelStore modelStore, WorkAudio workAudio, IOptions<TranscriptionOptions> options) : ITranscriptionEngine
{
    private static readonly JsonSerializerOptions WorkerLines = new(JsonSerializerDefaults.Web);

    public TranscriptionEngineKind Kind => TranscriptionEngineKind.Local;

    public bool IsReady => modelStore.IsReady;

    public async IAsyncEnumerable<TranscriptChunk> RunAsync(
        TranscriptionInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wav = await workAudio.PrepareAsync(input, cancellationToken);
        var settings = options.Value;

        var oomKillsBefore = ChildProcess.ReadOomKills(settings.MemoryEventsPath);
        using var process = ChildProcess.Start(WorkerCommand(settings, wav, input.StartSample), settings.Threads);
        using var killOnCancel = cancellationToken.Register(() => ChildProcess.KillQuietly(process));
        var discardErrors = ChildProcess.DiscardAsync(process.StandardError);
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
            if (process.ExitCode == ChildProcess.SigkillExitCode)
                throw new TranscriptionEngineException(ChildProcess.ProblemOfKill(settings.MemoryEventsPath, oomKillsBefore));
            if (process.ExitCode != 0 || endedOnBrokenLine)
                throw new TranscriptionEngineException(RecordingProblem.EngineCrashed);
        }
        finally
        {
            ChildProcess.KillQuietly(process);
            await discardErrors;
        }
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
}
