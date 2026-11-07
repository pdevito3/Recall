namespace Recall.Server.Domain.Summaries;

/// <summary>The stored state of a run. <c>Stale</c> and <c>Missing</c> are never stored: they come from the sources of the period.</summary>
public enum SummaryRunState
{
    Generating = 0,
    Ready = 1,
    Failed = 2,
}

/// <summary>Why a Summary failed. It is a closed list, because the words of a provider can hold the text of a request.</summary>
public enum SummaryFailure
{
    None = 0,
    NoModelProvider = 1,
    KeyUnreadable = 2,
    ProviderRefused = 3,
    ProviderUnavailable = 4,
    BadModelOutput = 5,
    Interrupted = 6,

    /// <summary>A roll-up whose source Summary failed. Its own run made no call.</summary>
    SourceFailed = 7,
}

/// <summary>
/// An AI narrative of one User over one period. A row exists only for a period that had sources when a run began. A run sets
/// <see cref="SummaryRunState.Generating"/>, and only <see cref="Complete"/> or <see cref="Fail"/> of the same run end it.
/// </summary>
public class Summary : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "sum";

    /// <summary>A Summary is a few paragraphs. A longer answer is no Summary, so the run fails as <see cref="SummaryFailure.BadModelOutput"/>.</summary>
    public const int ContentMaxLength = 20_000;

    public const int FingerprintLength = 64;

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [NotSensitive] public SummaryPeriod Period { get; private set; }

    [Sensitive] public DateOnly StartDate { get; private set; }

    public SummaryRunState RunState { get; private set; }

    public SummaryFailure Failure { get; private set; }

    /// <summary>The words of the last run that ended <see cref="SummaryRunState.Ready"/>. A later run keeps them until it ends.</summary>
    [Sensitive] public string? Content { get; private set; }

    /// <summary>The sources that <see cref="Content"/> read: Sessions for a Day or a Week, and source Summaries for a roll-up.</summary>
    public int SourceCount { get; private set; }

    /// <summary>The <see cref="SourceState.Fingerprint"/> that <see cref="Content"/> read. A different one now makes the Summary stale.</summary>
    [NotSensitive] public string? SourceFingerprint { get; private set; }

    public DateTimeOffset? GeneratedAt { get; private set; }

    /// <summary>The current run. The job payload holds it, so a job of an older run cannot end this one.</summary>
    [NotSensitive] public string? RunId { get; private set; }

    /// <summary>The job that holds the run, so a watch can end the run of a job that ended with no result.</summary>
    [NotSensitive] public Guid? RunJobId { get; private set; }

    public PeriodKey Key => new(Period, StartDate);

    /// <summary>The row of a period that has no row yet, with its first run begun.</summary>
    public static Summary WithFirstRun(string tenantId, string userId, PeriodKey key, Guid? jobId)
    {
        var summary = new Summary { TenantId = tenantId, UserId = userId, Period = key.Period, StartDate = key.Start };
        summary.BeginRun(jobId);
        return summary;
    }

    /// <summary>The status that a person sees, from the stored run and the sources of the period now.</summary>
    public SummaryStatus StatusFor(SourceState sources) => RunState switch
    {
        _ when !sources.HasSources => SummaryStatus.Missing,
        SummaryRunState.Generating => SummaryStatus.Generating,
        SummaryRunState.Failed => SummaryStatus.Failed,
        SummaryRunState.Ready when SourceFingerprint == sources.Fingerprint => SummaryStatus.Ready,
        _ => SummaryStatus.Stale,
    };

    /// <summary>Starts a new run. It takes the place of each earlier run, which can then end nothing.</summary>
    public string BeginRun(Guid? jobId)
    {
        RunId = EntityId.New("run");
        RunJobId = jobId;
        RunState = SummaryRunState.Generating;
        Failure = SummaryFailure.None;
        return RunId;
    }

    /// <summary>A job claims a run that a request began, so the watch can end it when the job ends with no result.</summary>
    public bool TakeRun(string runId, Guid jobId)
    {
        if (!IsRunning(runId))
            return false;

        RunJobId = jobId;
        return true;
    }

    public bool IsRunning(string runId) => RunState == SummaryRunState.Generating && RunId == runId;

    public bool Complete(string runId, string content, SourceState sources, DateTimeOffset now)
    {
        if (!IsRunning(runId))
            return false;

        Content = content;
        SourceCount = sources.Count;
        SourceFingerprint = sources.Fingerprint;
        GeneratedAt = now;
        RunState = SummaryRunState.Ready;
        Failure = SummaryFailure.None;
        RunJobId = null;
        return true;
    }

    public bool Fail(string runId, SummaryFailure failure)
    {
        if (!IsRunning(runId))
            return false;

        RunState = SummaryRunState.Failed;
        Failure = failure;
        RunJobId = null;
        return true;
    }

    protected Summary() : base(IdPrefix) { }
}

/// <summary>Where a Summary stands for a person. <see cref="Missing"/> and <see cref="Stale"/> come from the sources and are never stored.</summary>
public enum SummaryStatus
{
    Missing,
    Generating,
    Ready,
    Stale,
    Failed,
}
