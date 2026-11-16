namespace Recall.Server.Domain.Sessions.Jobs;

using BackWave.Jobs;
using BackWave.Storage;
using Cleanup;
using Content;
using Databases;
using Features;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ModelProviders;
using ModelProviders.Clients;
using Recall.Server.Resources.Jobs;
using Resources;
using Serilog;
using Tenants;

public sealed class CleanupJob(
    IServiceScopeFactory scopeFactory,
    IModelProviderClient client,
    ApiKeyProtector keys,
    RunningCleanups running,
    IJobStore store,
    TimeProvider timeProvider,
    ProviderLimits limits)
{
    public const string WireName = "session-cleanup";
    private const string TryAgainText = "The model provider did not answer. The job tries again.";

    private readonly ILogger _logger = Log.ForContext<CleanupJob>();

    /// <summary>
    /// Reads the Raw of the Session, calls the provider with no transaction open, and stores the result. The payload holds IDs
    /// only. A provider can echo the text of a request in an error, so no words of a failure leave this method.
    /// </summary>
    [Job(WireName, Queue = CleanupQueues.First)]
    public async Task CleanupSessionAsync(string sessionId, string userId, string tenantId, string runId, JobContext context, CancellationToken cancellationToken)
    {
        using var run = running.Watch(runId, timeProvider, cancellationToken);
        var work = await ReadWorkAsync(sessionId, userId, tenantId, runId, context.Attempt, cancellationToken);
        var step = work.Step;
        if (step is CleanupStep.CallProvider)
        {
            var timeLeft = limits.Call - await TimeInRunAsync(context, cancellationToken);
            if (timeLeft <= TimeSpan.Zero)
            {
                await FailAsync(sessionId, userId, tenantId, runId, CleanupFailure.ProviderUnavailable, cancellationToken);
                return;
            }

            run.EndAfter(timeLeft);
            ProviderReply<string> reply;
            try
            {
                reply = await client.CompleteAsync(work.Endpoint!, work.Model!, work.Instructions!, work.Text!, run.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A person stopped the run, or the time of the whole run ended. After a stop, this changes nothing.
                await FailAsync(sessionId, userId, tenantId, runId, CleanupFailure.ProviderUnavailable, cancellationToken);
                _logger.Here().Information("Cleanup {CleanupRunId} of session {SessionId}: attempt {Attempt} ended at a stop or at the limit of the run", runId, sessionId, context.Attempt);
                return;
            }

            step = reply switch
            {
                ProviderReply<string>.Answered answered when CleanupOutput.Parse(answered.Value) is { } output
                    => await CompleteAsync(sessionId, userId, tenantId, runId, work.RawRevision, output, cancellationToken),
                ProviderReply<string>.Answered => new CleanupStep.Fail(CleanupFailure.BadModelOutput),
                ProviderReply<string>.Failed failed => CleanupAttempt.AfterFailure(failed.Failure, context.Attempt),
                _ => throw new InvalidOperationException(),
            };
            _logger.Here().Information("Cleanup {CleanupRunId} of session {SessionId}: attempt {Attempt}, reply {ModelProviderReply}", runId, sessionId, context.Attempt, reply);
        }

        switch (step)
        {
            case CleanupStep.Fail fail:
                await FailAsync(sessionId, userId, tenantId, runId, fail.Failure, cancellationToken);
                break;
            case CleanupStep.TryAgainLater:
                throw new JobSafeTextException(TryAgainText);
        }
    }

    private async Task FailAsync(string sessionId, string userId, string tenantId, string runId, CleanupFailure failure, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(new JournalOwner(userId, tenantId));
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new FailCleanupRun.Command(sessionId, runId, failure), cancellationToken);
    }

    /// <summary>The time since the first claim. A wait before that claim does not count, because a run can wait behind other work. A wait between tries and a stop of the server do count.</summary>
    private async Task<TimeSpan> TimeInRunAsync(JobContext context, CancellationToken cancellationToken)
    {
        if (context.Attempt <= 1)
            return TimeSpan.Zero;

        var history = await store.GetJobHistoryAsync(context.JobId, cancellationToken);
        return history.FirstOrDefault(transition => transition.Attempt >= 1) is { } first ? timeProvider.GetUtcNow() - first.Timestamp : TimeSpan.Zero;
    }

    private async Task<CleanupStep> CompleteAsync(string sessionId, string userId, string tenantId, string runId, int rawRevision, CleanupOutput output, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(new JournalOwner(userId, tenantId));
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CompleteCleanupRun.Command(sessionId, runId, rawRevision, output), cancellationToken);
        return new CleanupStep.Skip();
    }

    private async Task<Work> ReadWorkAsync(string sessionId, string userId, string tenantId, string runId, int attempt, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(new JournalOwner(userId, tenantId));
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = await dbContext.Sessions.AsNoTracking()
            .TagWithOperationCallSite("sessions.cleanup_job.session")
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
            return Work.Without(new CleanupStep.Skip());

        var step = CleanupAttempt.Before(session.CleanupRun, session.CleanupRunId, runId, attempt);
        if (step is not CleanupStep.CallProvider)
            return Work.Without(step);

        var provider = await dbContext.ModelProviders.AsNoTracking()
            .TagWithOperationCallSite("sessions.cleanup_job.provider")
            .ActiveFirst()
            .FirstOrDefaultAsync(cancellationToken);
        if (provider is null)
            return Work.Without(new CleanupStep.Fail(CleanupFailure.NoModelProvider));

        var read = keys.Read(provider.ProtectedApiKey);
        if (read is ProtectedKeyRead.Unreadable)
            return Work.Without(new CleanupStep.Fail(CleanupFailure.KeyUnreadable));

        var corrections = await dbContext.Corrections.AsNoTracking()
            .TagWithOperationCallSite("sessions.cleanup_job.corrections")
            .ToListAsync(cancellationToken);
        var removesFillerWords = await dbContext.UserSettings.AsNoTracking()
            .TagWithOperationCallSite("sessions.cleanup_job.settings")
            .Select(s => (bool?)s.RemoveFillerWords)
            .FirstOrDefaultAsync(cancellationToken) ?? true;

        return new Work(
            step,
            new ModelEndpoint(provider.Kind, provider.BaseUrl, (read as ProtectedKeyRead.Readable)?.ApiKey),
            provider.Model,
            CleanupPrompt.InstructionsWith(corrections, removesFillerWords),
            CleanupPrompt.TextOf(ProseMirrorToPlainText.Render(session.Raw.Text)),
            session.Raw.DraftRevision);
    }

    private sealed class Work(CleanupStep step, ModelEndpoint? endpoint, string? model, string? instructions, string? text, int rawRevision)
    {
        public CleanupStep Step { get; } = step;
        public ModelEndpoint? Endpoint { get; } = endpoint;
        public string? Model { get; } = model;
        public string? Instructions { get; } = instructions;
        public string? Text { get; } = text;
        public int RawRevision { get; } = rawRevision;

        public static Work Without(CleanupStep step) => new(step, null, null, null, null, 0);
    }
}
