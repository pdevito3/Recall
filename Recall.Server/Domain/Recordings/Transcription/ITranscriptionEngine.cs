namespace Recall.Server.Domain.Recordings.Transcription;

using ModelProviders.Clients;

public interface ITranscriptionEngine
{
    TranscriptionEngineKind Kind { get; }

    bool IsReady { get; }

    IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, CancellationToken cancellationToken);
}

/// <summary><paramref name="Provider"/> is set only for <see cref="TranscriptionEngineKind.Cloud"/>.</summary>
public sealed record TranscriptionInput(string AudioPath, string WorkFolder, long StartSample, AudioProvider? Provider = null);

/// <summary>The provider that takes the audio. The endpoint holds the key, so it prints nothing.</summary>
public sealed record AudioProvider(ModelEndpoint Endpoint, string Model)
{
    public override string ToString() => nameof(AudioProvider);
}

public sealed record TranscriptChunk(long EndSample, long TotalSamples, string Text);

/// <summary><paramref name="tryAgain"/> means that a later try can work, so the job keeps the audio waiting and runs again.</summary>
public sealed class TranscriptionEngineException(RecordingProblem problem, bool tryAgain = false)
    : Exception("The transcription engine stopped.")
{
    public RecordingProblem Problem { get; } = problem;

    public bool TryAgain { get; } = tryAgain;
}
