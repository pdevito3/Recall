namespace Recall.Server.Domain.Sessions.Cleanup;

using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Databases;
using Dtos;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;

/// <summary>
/// Shows the state of a Cleanup job to a person who waits. It only reads, and the job does not depend on it, so a closed
/// connection stops the reads and not the Cleanup.
/// </summary>
public sealed class CleanupWatch(AppDbContext dbContext, TimeProvider timeProvider, IHostApplicationLifetime lifetime)
{
    public static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan WatchLimit = TimeSpan.FromMinutes(15);

    private sealed record RunState(CleanupRunState Run, CleanupFailure Failure);

    public async IAsyncEnumerable<SseItem<CleanupEventDto>> EventsOfAsync(string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Event("run.started", "The Cleanup runs. You can close this page, and the result stays.");

        var end = timeProvider.GetUtcNow() + WatchLimit;
        var nextProgress = timeProvider.GetUtcNow() + ProgressInterval;
        while (true)
        {
            var state = await ReadAsync(sessionId, cancellationToken);
            if (state is { Run: CleanupRunState.Running })
            {
                if (lifetime.ApplicationStopping.IsCancellationRequested || timeProvider.GetUtcNow() >= end)
                {
                    yield return Event("stopped", "The server stopped watching this Cleanup. The Cleanup goes on, so read the Session later.");
                    yield break;
                }

                if (timeProvider.GetUtcNow() >= nextProgress)
                {
                    nextProgress = timeProvider.GetUtcNow() + ProgressInterval;
                    yield return Event("progress", "The Cleanup runs.");
                }

                await WaitAsync(cancellationToken);
                continue;
            }

            yield return state is { Run: CleanupRunState.Clean }
                ? Event("completed", "The Cleanup is done.")
                : Failed(state is null or { Failure: CleanupFailure.None } ? CleanupFailure.Interrupted : state.Failure);
            yield break;
        }
    }

    public static string TextOf(CleanupFailure failure) => failure switch
    {
        CleanupFailure.None => string.Empty,
        CleanupFailure.NoModelProvider => "No model provider is ready. An Admin must connect one, pick a model, and make it active.",
        CleanupFailure.KeyUnreadable => "The server cannot read the API key of the model provider. An Admin must enter the key again.",
        CleanupFailure.ProviderRefused => "The model provider refused the request. An Admin can test the connection on the model provider page.",
        CleanupFailure.ProviderUnavailable => "The model provider did not answer after several tries. Try again later.",
        CleanupFailure.BadModelOutput => "The answer of the model is not one that the app can use, so your Cleaned text did not change. Try again.",
        CleanupFailure.Interrupted => "The Cleanup stopped before it got a result, so your Cleaned text did not change. Try again.",
        CleanupFailure.Stopped => "You stopped the Cleanup, so your Cleaned text did not change. You can run it again.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    private static SseItem<CleanupEventDto> Event(string type, string message) => new(new CleanupEventDto
    {
        Type = type,
        Data = new CleanupEventDataDto { Message = message },
    });

    private static SseItem<CleanupEventDto> Failed(CleanupFailure failure) => new(new CleanupEventDto
    {
        Type = "failed",
        Data = new CleanupEventDataDto { Message = TextOf(failure), StopReason = failure.ToString() },
    });

    // The reads of a watch are not work to trace. A span for each one would fill the telemetry folder.
    private async Task<RunState?> ReadAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var quiet = SuppressInstrumentationScope.Begin();
        return await dbContext.Sessions.AsNoTracking()
            .TagWithOperationCallSite("sessions.cleanup_watch.read")
            .Where(session => session.Id == sessionId)
            .Select(session => new RunState(session.CleanupRun, session.CleanupFailure))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try
        {
            await Task.Delay(ReadInterval, timeProvider, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The server stops. The next read tells the person.
        }
    }
}
