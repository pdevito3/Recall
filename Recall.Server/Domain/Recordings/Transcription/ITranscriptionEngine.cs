namespace Recall.Server.Domain.Recordings.Transcription;

public interface ITranscriptionEngine
{
    TranscriptionEngineKind Kind { get; }

    bool IsReady { get; }

    IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, CancellationToken cancellationToken);
}

public sealed record TranscriptionInput(string AudioPath, string WorkFolder, long StartSample);

public sealed record TranscriptChunk(long EndSample, long TotalSamples, string Text);

public sealed class TranscriptionEngineException(RecordingProblem problem)
    : Exception("The transcription engine stopped.")
{
    public RecordingProblem Problem { get; } = problem;
}
