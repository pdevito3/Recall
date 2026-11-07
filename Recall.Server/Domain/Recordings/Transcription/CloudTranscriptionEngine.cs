namespace Recall.Server.Domain.Recordings.Transcription;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using ModelProviders.Clients;

/// <summary>
/// Sends the audio to the model provider that an Admin chose, one part at a time, in the wire format of
/// <c>POST {base}/audio/transcriptions</c>. Each part is the decoded WAV, so each format that a client sends reaches the
/// provider in a format that it takes, and each part ends on a sample that the next attempt can start from.
/// </summary>
public sealed class CloudTranscriptionEngine(WorkAudio workAudio, OpenAiCompatibleClient client, IOptions<TranscriptionOptions> options)
    : ITranscriptionEngine
{
    /// <summary>A cut answer of a shorter part is no speech that a model can hear, so the part is not split again.</summary>
    public const int SmallestPartToSplit = 4 * Recording.SamplesPerSecond;

    public TranscriptionEngineKind Kind => TranscriptionEngineKind.Cloud;

    public bool IsReady => true;

    public async IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var provider = input.Provider ?? throw new InvalidOperationException("A cloud transcription needs its provider.");
        var wav = await workAudio.PrepareAsync(input, cancellationToken);
        var maxSamples = Math.Min(AudioParts.MaxSamplesIn(options.Value.CloudPartBytes), options.Value.CloudPartSeconds * (long)Recording.SamplesPerSecond);

        using var audio = PcmAudio.Open(wav);
        var start = input.StartSample;
        var limit = maxSamples;
        while (start < audio.Samples)
        {
            var end = audio.EndOfPart(start, limit);
            var part = AudioParts.WavOf(audio.ReadBytes(start, end));
            var heard = await client.TranscribeAsync(provider.Endpoint, provider.Model, part, cancellationToken) switch
            {
                ProviderReply<HeardAudio>.Answered answered => answered.Value,
                ProviderReply<HeardAudio>.Failed failed => throw FailureOf(failed.Failure),
                _ => throw new InvalidOperationException(),
            };

            if (heard.Cut)
            {
                // The words of a cut part are missing, so none of them is saved, and the part goes again at half its length.
                // Speech that fills one part tends to go on, so the rest of the run keeps the shorter length.
                limit = end - start >= SmallestPartToSplit
                    ? (end - start) / 2
                    : throw new TranscriptionEngineException(RecordingProblem.ProviderBadAnswer);
                continue;
            }

            yield return new TranscriptChunk(end, audio.Samples, heard.Text);
            start = end;
        }
    }

    private static TranscriptionEngineException FailureOf(ProviderFailure failure) => failure.Kind switch
    {
        ProviderFailureKind.Refused => new TranscriptionEngineException(RecordingProblem.ProviderRefused),
        ProviderFailureKind.BadOutput => new TranscriptionEngineException(RecordingProblem.ProviderBadAnswer),
        _ => new TranscriptionEngineException(RecordingProblem.ProviderUnavailable, tryAgain: true),
    };
}
