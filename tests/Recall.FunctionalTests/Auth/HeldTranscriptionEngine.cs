namespace Recall.FunctionalTests.Auth;

using System.Runtime.CompilerServices;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Transcription;

/// <summary>A transcription that stays in its run until the test lets it go, so the worker of its group has no free slot.</summary>
public sealed class HeldTranscriptionEngine : ITranscriptionEngine
{
    public const string Words = "words of the held transcription";
    private const long Samples = 16_000;

    private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TranscriptionEngineKind Kind => TranscriptionEngineKind.Local;

    public bool IsReady => true;

    public Task Entered => _entered.Task;

    public bool IsReleased => _released.Task.IsCompleted;

    public void Hold()
    {
        _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release() => _released.TrySetResult();

    public async IAsyncEnumerable<TranscriptChunk> RunAsync(TranscriptionInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
        yield return new TranscriptChunk(Samples, Samples, Words);
    }
}
