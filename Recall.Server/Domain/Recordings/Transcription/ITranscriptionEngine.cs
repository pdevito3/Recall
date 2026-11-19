namespace Recall.Server.Domain.Recordings.Transcription;

using ModelProviders.Clients;

public interface ITranscriptionEngine
{
    TranscriptionEngineKind Kind { get; }

    bool IsReady { get; }

    IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, CancellationToken cancellationToken);
}

/// <summary>
/// <paramref name="Provider"/> and <paramref name="Vocabulary"/> are set only for <see cref="TranscriptionEngineKind.Cloud"/>.
/// <paramref name="Vocabulary"/> is the <see cref="TranscriptionVocabulary"/> of the owner, null when the owner keeps no term.
/// </summary>
public sealed record TranscriptionInput(string AudioPath, string WorkAudioPath, long StartSample, AudioProvider? Provider = null, string? Vocabulary = null)
{
    /// <summary><see cref="Vocabulary"/> holds the terms of a person, so it prints nothing.</summary>
    public override string ToString() => nameof(TranscriptionInput);
}

/// <summary>The provider that takes the audio. The endpoint holds the key, so it prints nothing.</summary>
public sealed record AudioProvider(ModelEndpoint Endpoint, string Model)
{
    public override string ToString() => nameof(AudioProvider);
}

/// <summary>
/// The text of the samples from the pointer to <paramref name="EndSample"/>. <paramref name="WordTimes"/> is one span for each
/// word of <paramref name="Text"/>, in order, where a word is a run of characters with no whitespace. An engine that cannot
/// say when each word was spoken leaves it null. <see cref="ParagraphBreaks"/> checks the spans, so a wrong span costs a
/// precise paragraph break, never a word.
/// </summary>
public sealed record TranscriptChunk(long EndSample, long TotalSamples, string Text, IReadOnlyList<WordTime>? WordTimes = null);

/// <summary>When one word was spoken, in samples of the 16 kHz work audio from its start, not from the chunk.</summary>
public readonly record struct WordTime(long Start, long End);

/// <summary><paramref name="tryAgain"/> means that a later try can work, so the job keeps the audio waiting and runs again.</summary>
public sealed class TranscriptionEngineException(RecordingProblem problem, bool tryAgain = false)
    : Exception("The transcription engine stopped.")
{
    public RecordingProblem Problem { get; } = problem;

    public bool TryAgain { get; } = tryAgain;
}
