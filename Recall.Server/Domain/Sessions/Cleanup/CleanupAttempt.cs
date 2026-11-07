namespace Recall.Server.Domain.Sessions.Cleanup;

using ModelProviders.Clients;

public abstract record CleanupStep
{
    private CleanupStep() { }

    /// <summary>This run ended, or a newer run took the Session. A job that runs again after its commit gets here.</summary>
    public sealed record Skip : CleanupStep;

    public sealed record CallProvider : CleanupStep;

    public sealed record TryAgainLater : CleanupStep;

    public sealed record Fail(CleanupFailure Failure) : CleanupStep;
}

/// <summary>The rules of one attempt of a Cleanup job. They read no clock, no database, and no network.</summary>
public static class CleanupAttempt
{
    /// <summary>
    /// The number of calls to a provider that one Cleanup can make. Each claim counts an attempt, also a stop of the host and a
    /// kill. BackWave would run the job up to 10 times, and a Cleanup that makes the server exit would restart it each time.
    /// </summary>
    public const int MaxProviderCalls = 6;

    public static CleanupStep Before(CleanupRunState state, string? storedRunId, string runId, int attempt)
    {
        if (state != CleanupRunState.Running || storedRunId != runId)
            return new CleanupStep.Skip();

        return attempt > MaxProviderCalls ? new CleanupStep.Fail(CleanupFailure.Interrupted) : new CleanupStep.CallProvider();
    }

    public static CleanupStep AfterFailure(ProviderFailure failure, int attempt) => failure.Kind switch
    {
        ProviderFailureKind.Refused => new CleanupStep.Fail(CleanupFailure.ProviderRefused),
        ProviderFailureKind.BadOutput => new CleanupStep.Fail(CleanupFailure.BadModelOutput),
        ProviderFailureKind.NoWords => new CleanupStep.Fail(CleanupFailure.ProviderUnavailable),
        ProviderFailureKind.Unavailable when attempt >= MaxProviderCalls => new CleanupStep.Fail(CleanupFailure.ProviderUnavailable),
        ProviderFailureKind.Unavailable => new CleanupStep.TryAgainLater(),
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };
}
