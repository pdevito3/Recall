namespace Recall.Server.Domain.Summaries.Jobs;

using BackWave.Jobs;
using Databases;
using Features;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ModelProviders;
using ModelProviders.Clients;
using Recall.Server.Resources.Jobs;
using Resources;
using Serilog;
using Sessions.Content;
using Tenants;
using TimeZones;
using UserSettings;

/// <summary>
/// The jobs of Summaries. Both run in the <see cref="JobHosting.SummaryQueue"/> group of one worker, so two runs of one Summary
/// never call a provider at the same time. A payload holds IDs only. A provider can echo the text of a request in an error, so
/// no words of a failure leave this class.
/// </summary>
public sealed class SummaryJobs(IServiceScopeFactory scopeFactory, IModelProviderClient client, ApiKeyProtector keys, TimeProvider timeProvider)
{
    public const string RunWireName = "summary-run";
    public const string RefreshWireName = "summaries-refresh";

    /// <summary>
    /// The claims that one job can have. Each claim counts, also a stop of the host and a kill, so a job that makes the server
    /// exit ends here before the group limit of 10. A run that a later claim takes over keeps the words of the runs before it.
    /// </summary>
    public const int MaxClaims = 6;

    private const string TryAgainText = "The model provider did not answer. The job tries again.";

    private readonly ILogger _logger = Log.ForContext<SummaryJobs>();

    /// <summary>Generates one Summary that a person opened, and each missing or stale source Summary below it.</summary>
    [Job(RunWireName, Queue = JobHosting.SummaryQueue)]
    public async Task RunSummaryAsync(string summaryId, string userId, string tenantId, string runId, JobContext context, CancellationToken cancellationToken)
    {
        var owner = new JournalOwner(userId, tenantId);
        var run = await ReadRequestedRunAsync(owner, summaryId, runId, cancellationToken);
        if (run is null)
            return;

        if (context.Attempt > MaxClaims)
        {
            await SendAsync(owner, new FailSummaryRun.Command(summaryId, runId, SummaryFailure.Interrupted), cancellationToken);
            await SendAsync(owner, new EndSummaryRunsOfJob.Command(context.JobId), cancellationToken);
            return;
        }

        var generation = await StartAsync(owner, run.Value.Key, run.Value.Key, context, cancellationToken);
        if (generation.Endpoint is not { } endpoint)
        {
            await SendAsync(owner, new FailSummaryRun.Command(summaryId, runId, generation.NoEndpointFailure), cancellationToken);
            return;
        }

        await generation.EnsureAsync(endpoint, run.Value.Key, runId, cancellationToken);
        await SendAsync(owner, new EndSummaryRunsOfJob.Command(context.JobId), cancellationToken);
    }

    /// <summary>
    /// The nightly job of one User. It regenerates each stale or failed Summary, and makes the Day that just ended, and calls
    /// no provider for a Summary whose sources did not change. With no model provider it changes nothing.
    /// </summary>
    [Job(RefreshWireName, Queue = JobHosting.SummaryQueue)]
    public async Task RefreshSummariesAsync(string userId, string tenantId, JobContext context, CancellationToken cancellationToken)
    {
        var owner = new JournalOwner(userId, tenantId);
        if (context.Attempt > MaxClaims)
        {
            await SendAsync(owner, new EndSummaryRunsOfJob.Command(context.JobId), cancellationToken);
            return;
        }

        var plan = await ReadRefreshPlanAsync(owner, context.JobId, cancellationToken);
        if (plan.Targets.Count == 0)
        {
            _logger.Here().Information("The summary refresh of user {UserId} found no stale summary", userId);
            return;
        }

        var generation = await StartAsync(owner, plan.First, plan.End, context, cancellationToken);
        if (generation.Endpoint is not { } endpoint)
        {
            _logger.Here().Information("The summary refresh of user {UserId} skipped {SummaryCount} summaries, reason {SummaryFailure}", userId, plan.Targets.Count, generation.NoEndpointFailure);
            return;
        }

        foreach (var key in plan.Targets)
            await generation.EnsureAsync(endpoint, key, requestedRunId: null, cancellationToken);
        await SendAsync(owner, new EndSummaryRunsOfJob.Command(context.JobId), cancellationToken);
        _logger.Here().Information("The summary refresh of user {UserId} checked {SummaryCount} summaries and called the provider {ProviderCallCount} times", userId, plan.Targets.Count, generation.ProviderCalls);
    }

    private async Task<(PeriodKey Key, string RunId)?> ReadRequestedRunAsync(JournalOwner owner, string summaryId, string runId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        var summary = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Summaries.AsNoTracking()
            .TagWithOperationCallSite("summaries.run_job.summary")
            .FirstOrDefaultAsync(s => s.Id == summaryId, cancellationToken);

        // A second claim after the end of the run, or a run that a newer one took over, does nothing.
        return summary is not null && summary.IsRunning(runId) ? (summary.Key, runId) : null;
    }

    private async Task<RefreshPlan> ReadRefreshPlanAsync(JournalOwner owner, Guid jobId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var zone = await dbContext.LoadJournalZoneAsync(cancellationToken);
        var yesterday = PeriodKey.Holding(SummaryPeriod.Day, ZonedDate.TodayAt(zone, timeProvider.GetUtcNow()).Date.AddDays(-1));

        var stored = await dbContext.Summaries.AsNoTracking()
            .TagWithOperationCallSite("summaries.refresh_job.summaries")
            .Select(s => new { s.Period, s.StartDate, s.RunState, s.SourceFingerprint, s.RunJobId })
            .ToListAsync(cancellationToken);
        var keys = stored.Select(s => new PeriodKey(s.Period, s.StartDate)).Append(yesterday).ToList();
        var first = keys.Min(k => k.Start);
        var end = keys.Max(k => k.End);
        var sources = await dbContext.LoadSourcesAsync(zone, first, end, cancellationToken);

        var targets = stored
            .Where(s => s.RunState == SummaryRunState.Failed
                || (s.RunState == SummaryRunState.Generating && s.RunJobId == jobId)
                || (s.RunState == SummaryRunState.Ready && s.SourceFingerprint != sources.StateOf(new PeriodKey(s.Period, s.StartDate)).Fingerprint))
            .Select(s => new PeriodKey(s.Period, s.StartDate))
            .ToList();
        if (!stored.Any(s => s.Period == SummaryPeriod.Day && s.StartDate == yesterday.Start) && sources.StateOf(yesterday).HasSources)
            targets.Add(yesterday);

        return new RefreshPlan(targets.OrderBy(k => k.Period).ThenBy(k => k.Start).ToList(), first, end);
    }

    private async Task<SummaryGeneration> StartAsync(JournalOwner owner, PeriodKey from, PeriodKey to, JobContext context, CancellationToken cancellationToken)
        => await StartAsync(owner, from.Start, to.End, context, cancellationToken);

    private async Task<SummaryGeneration> StartAsync(JournalOwner owner, DateOnly first, DateOnly end, JobContext context, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var zone = await dbContext.LoadJournalZoneAsync(cancellationToken);
        var sources = await dbContext.LoadSourcesAsync(zone, first, end, cancellationToken);

        var provider = await dbContext.ModelProviders.AsNoTracking()
            .TagWithOperationCallSite("summaries.job.provider")
            .ActiveFirst()
            .FirstOrDefaultAsync(cancellationToken);
        var generation = new SummaryGeneration(this, owner, sources, context);
        if (provider is null)
            return generation.WithNoEndpoint(SummaryFailure.NoModelProvider);

        return keys.Read(provider.ProtectedApiKey) switch
        {
            ProtectedKeyRead.Unreadable => generation.WithNoEndpoint(SummaryFailure.KeyUnreadable),
            var read => generation.WithEndpoint(new ModelEndpoint(provider.Kind, provider.BaseUrl, (read as ProtectedKeyRead.Readable)?.ApiKey), provider.Model!),
        };
    }

    private async Task<T> SendAsync<T>(JournalOwner owner, IRequest<T> request, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request, cancellationToken);
    }

    private async Task SendAsync(JournalOwner owner, IRequest request, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateJournalScope(owner);
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request, cancellationToken);
    }

    private AsyncServiceScope JournalScope(JournalOwner owner) => scopeFactory.CreateJournalScope(owner);

    private Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken)
        => client.CompleteAsync(endpoint, model, instructions, text, cancellationToken);

    private sealed record RefreshPlan(IReadOnlyList<PeriodKey> Targets, DateOnly First, DateOnly End);

    /// <summary>
    /// The work of one claim on the Summaries of one User. A Summary whose sources did not change since its words calls no
    /// provider. A roll-up first makes each source Summary that is missing, stale, or failed.
    /// </summary>
    private sealed class SummaryGeneration(SummaryJobs jobs, JournalOwner owner, SummarySources sources, JobContext context)
    {
        private readonly Dictionary<PeriodKey, bool> _ready = [];

        public ModelEndpoint? Endpoint { get; private set; }

        public string? Model { get; private set; }

        public SummaryFailure NoEndpointFailure { get; private set; }

        public int ProviderCalls { get; private set; }

        public SummaryGeneration WithEndpoint(ModelEndpoint endpoint, string model)
        {
            Endpoint = endpoint;
            Model = model;
            return this;
        }

        public SummaryGeneration WithNoEndpoint(SummaryFailure failure)
        {
            NoEndpointFailure = failure;
            return this;
        }

        /// <summary>True when the Summary of the period is ready with words of its sources now.</summary>
        public async Task<bool> EnsureAsync(ModelEndpoint endpoint, PeriodKey key, string? requestedRunId, CancellationToken cancellationToken)
        {
            if (_ready.TryGetValue(key, out var known))
                return known;

            var ready = await GenerateAsync(endpoint, key, requestedRunId, cancellationToken);
            _ready[key] = ready;
            return ready;
        }

        private async Task<bool> GenerateAsync(ModelEndpoint endpoint, PeriodKey key, string? requestedRunId, CancellationToken cancellationToken)
        {
            var state = sources.StateOf(key);
            if (!state.HasSources)
            {
                await jobs.SendAsync(owner, new RemoveSummaryWithNoSources.Command(key), cancellationToken);
                return false;
            }

            if (await IsReadyAsync(key, state, cancellationToken))
                return true;

            var run = await jobs.SendAsync(owner, new BeginSummaryRun.Command(key, requestedRunId, context.JobId), cancellationToken);
            IReadOnlyList<SummarySourceText> texts;
            if (key.SourcePeriod is null)
            {
                texts = await ReadSessionTextsAsync(key, cancellationToken);
            }
            else
            {
                var sourceKeys = sources.SourcePeriodsWithSources(key);
                var allReady = true;
                foreach (var source in sourceKeys)
                    allReady &= await EnsureAsync(endpoint, source, requestedRunId: null, cancellationToken);
                if (!allReady)
                {
                    await jobs.SendAsync(owner, new FailSummaryRun.Command(run.SummaryId, run.RunId, SummaryFailure.SourceFailed), cancellationToken);
                    return false;
                }

                texts = await ReadSummaryTextsAsync(sourceKeys, cancellationToken);
            }

            ProviderCalls++;
            var reply = await jobs.CompleteAsync(endpoint, Model!, SummaryPrompt.Instructions, SummaryPrompt.TextOf(key, texts), cancellationToken);
            jobs._logger.Here().Information("Summary run {SummaryRunId} of summary {SummaryId}: attempt {Attempt}, reply {ModelProviderReply}", run.RunId, run.SummaryId, context.Attempt, reply);

            var failure = reply switch
            {
                ProviderReply<string>.Answered answered when SummaryPrompt.ContentOf(answered.Value) is { } content
                    => await jobs.SendAsync(owner, new CompleteSummaryRun.Command(run.SummaryId, run.RunId, content, state), cancellationToken) ? (SummaryFailure?)null : SummaryFailure.Interrupted,
                ProviderReply<string>.Answered => SummaryFailure.BadModelOutput,
                ProviderReply<string>.Failed failed => FailureOf(failed.Failure),
                _ => throw new InvalidOperationException(),
            };
            if (failure is null)
                return true;

            await jobs.SendAsync(owner, new FailSummaryRun.Command(run.SummaryId, run.RunId, failure.Value), cancellationToken);
            return false;
        }

        /// <summary>A provider that a later try can reach makes the job try again, and the Summaries that it made stay made.</summary>
        private SummaryFailure FailureOf(ProviderFailure failure) => failure.Kind switch
        {
            ProviderFailureKind.Refused => SummaryFailure.ProviderRefused,
            ProviderFailureKind.BadOutput => SummaryFailure.BadModelOutput,
            ProviderFailureKind.NoWords => SummaryFailure.ProviderUnavailable,
            ProviderFailureKind.Unavailable when context.Attempt >= MaxClaims => SummaryFailure.ProviderUnavailable,
            ProviderFailureKind.Unavailable => throw new JobSafeTextException(TryAgainText),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        private async Task<bool> IsReadyAsync(PeriodKey key, SourceState state, CancellationToken cancellationToken)
        {
            await using var scope = jobs.JournalScope(owner);
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Summaries.AsNoTracking()
                .TagWithOperationCallSite("summaries.job.ready")
                .AnyAsync(s => s.Period == key.Period && s.StartDate == key.Start && s.RunState == SummaryRunState.Ready && s.SourceFingerprint == state.Fingerprint, cancellationToken);
        }

        /// <summary>Each Session reads Cleaned when it has words, and Raw when it has not.</summary>
        private async Task<IReadOnlyList<SummarySourceText>> ReadSessionTextsAsync(PeriodKey key, CancellationToken cancellationToken)
        {
            var stubs = sources.SessionsIn(key);
            var ids = stubs.Select(s => s.Id).ToList();
            await using var scope = jobs.JournalScope(owner);
            var texts = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
                .TagWithOperationCallSite("summaries.job.session_texts")
                .Where(s => ids.Contains(s.Id))
                .Select(s => new { s.Id, Raw = s.Raw.Text, Cleaned = s.Cleaned.Text })
                .ToDictionaryAsync(s => s.Id, cancellationToken);

            return stubs
                .Where(stub => texts.ContainsKey(stub.Id))
                .Select(stub => new SummarySourceText(stub.JournalingDay, WordsOf(texts[stub.Id].Cleaned) is { Length: > 0 } cleaned ? cleaned : WordsOf(texts[stub.Id].Raw)))
                .Where(source => source.Text.Length > 0)
                .ToList();
        }

        private async Task<IReadOnlyList<SummarySourceText>> ReadSummaryTextsAsync(IReadOnlyList<PeriodKey> keys, CancellationToken cancellationToken)
        {
            var period = keys[0].Period;
            var starts = keys.Select(k => k.Start).ToList();
            await using var scope = jobs.JournalScope(owner);
            var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Summaries.AsNoTracking()
                .TagWithOperationCallSite("summaries.job.source_summaries")
                .Where(s => s.Period == period && starts.Contains(s.StartDate) && s.RunState == SummaryRunState.Ready)
                .Select(s => new { s.StartDate, s.Content })
                .ToListAsync(cancellationToken);
            return rows.OrderBy(r => r.StartDate).Select(r => new SummarySourceText(r.StartDate, r.Content ?? string.Empty)).ToList();
        }

        private static string WordsOf(string draft) => ProseMirrorToPlainText.Render(draft).Trim();
    }
}
