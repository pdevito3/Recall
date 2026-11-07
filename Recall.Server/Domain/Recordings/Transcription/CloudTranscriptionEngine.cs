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
    public TranscriptionEngineKind Kind => TranscriptionEngineKind.Cloud;

    public bool IsReady => true;

    public async IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var provider = input.Provider ?? throw new InvalidOperationException("A cloud transcription needs its provider.");
        var wav = await workAudio.PrepareAsync(input, cancellationToken);
        var maxSamples = AudioParts.MaxSamplesIn(options.Value.CloudPartBytes);

        using var audio = PcmAudio.Open(wav);
        var start = input.StartSample;
        while (start < audio.Samples)
        {
            var end = audio.EndOfPart(start, maxSamples);
            var part = AudioParts.WavOf(audio.ReadBytes(start, end));
            var reply = await client.TranscribeAsync(provider.Endpoint, provider.Model, part, cancellationToken);
            yield return reply switch
            {
                ProviderReply<string>.Answered answered => new TranscriptChunk(end, audio.Samples, answered.Value),
                ProviderReply<string>.Failed failed => throw FailureOf(failed.Failure),
                _ => throw new InvalidOperationException(),
            };
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
