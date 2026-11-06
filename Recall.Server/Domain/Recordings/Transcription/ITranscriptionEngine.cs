namespace Recall.Server.Domain.Recordings.Transcription;

/// <summary>
/// The seam between the flow of a <see cref="Recording"/> and the thing that makes text. <see cref="LocalParakeetEngine"/>
/// is the one engine today. A cloud engine implements this interface and adds a value to
/// <see cref="TranscriptionEngineKind"/>, and the job, the progress pointer, and the status stay as they are.
/// </summary>
public interface ITranscriptionEngine
{
    TranscriptionEngineKind Kind { get; }

    /// <summary>False when the engine must get something first, so the Recording waits with a true status.</summary>
    bool IsReady { get; }

    /// <summary>Gives the text in order, from the sample that <paramref name="input"/> names.</summary>
    /// <exception cref="TranscriptionEngineException">The engine could not go on, for a reason that the person can read.</exception>
    IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, CancellationToken cancellationToken);
}

/// <param name="AudioPath">The whole audio file, which the engine only reads.</param>
/// <param name="WorkFolder">A folder for work files that a later attempt can use. The job removes them at the end.</param>
/// <param name="StartSample">The first sample at 16 kHz that has no text yet.</param>
public sealed record TranscriptionInput(string AudioPath, string WorkFolder, long StartSample);

/// <param name="EndSample">The sample after the last one of this chunk, at 16 kHz.</param>
/// <param name="TotalSamples">The length of the audio, at 16 kHz.</param>
/// <param name="Text">The words of this chunk, which are empty for silence.</param>
public sealed record TranscriptChunk(long EndSample, long TotalSamples, string Text);

public sealed class TranscriptionEngineException(RecordingProblem problem)
    : Exception("The transcription engine stopped.")
{
    public RecordingProblem Problem { get; } = problem;
}
