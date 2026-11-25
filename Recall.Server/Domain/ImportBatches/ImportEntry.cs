namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using Sessions;
using TimeZones;

/// <summary>
/// One entry of a batch, by the Client session ID that its Session gets at the accept. It holds two layers: the agent
/// writes <see cref="Agent"/> with a put, and the person writes <see cref="Review"/> with a review (IMP-5, IMP-6).
/// </summary>
public class ImportEntry : BaseEntity, IJournalOwned
{
    public const string IdPrefix = "impent";

    [NotSensitive] public string BatchId { get; private set; } = default!;

    public ImportBatch Batch { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [NotSensitive] public string ClientSessionId { get; private set; } = default!;

    [Sensitive] public ImportAgentLayer Agent { get; private set; } = default!;

    [Sensitive] public ImportReview Review { get; private set; } = ImportReview.None;

    /// <summary>True when a put changed Raw after the last edit of the words by the person (IMP-12).</summary>
    public bool WordsChangedAfterEdit { get; private set; }

    /// <summary>True when a put changed the time after the last edit of the time by the person (IMP-12).</summary>
    public bool TimeChangedAfterEdit { get; private set; }

    public const string FutureMessage = "The time of an entry cannot be in the future.";

    /// <summary>The zone of an entry: its own, else the zone of the batch (IMP-10). A zone that this host no longer knows gives UTC.</summary>
    public static TimeZoneId ZoneOf(ImportAgentLayer agent, ImportBatch batch) => TimeZoneId.TryOf(agent.TimeZone) ?? TimeZoneId.OrUtc(batch.TimeZone);

    public static DateTimeOffset StartOf(ImportAgentLayer agent, ImportReview review, ImportBatch batch)
        => ImportStartTime.Of(agent.Date, agent.Position, agent.Time, review.Time, ZoneOf(agent, batch));

    /// <summary>An entry is rejected by its own review or by its day (IMP-6).</summary>
    public static bool IsRejected(ImportAgentLayer agent, ImportReview review, ImportBatch batch) => review.Rejected || batch.IsDayRejected(agent.Date);

    public DateTimeOffset StartIn(ImportBatch batch) => StartOf(Agent, Review, batch);

    public bool IsRejectedIn(ImportBatch batch) => IsRejected(Agent, Review, batch);

    /// <summary>The words of the person win over the Raw of the agent (IMP-11).</summary>
    public string RawAtAccept() => Review.Words ?? Agent.ToDocument();

    internal static ImportEntry Add(ImportBatch batch, string clientSessionId, ImportAgentLayer agent)
        => new() { BatchId = batch.Id, Batch = batch, UserId = batch.UserId, ClientSessionId = clientSessionId, Agent = agent };

    /// <summary>Replaces the agent layer, and gives false when it holds these values already (IMP-3).</summary>
    public bool PutFromAgent(ImportAgentLayer agent)
    {
        if (agent == Agent)
            return false;

        WordsChangedAfterEdit |= Review.Words is not null && agent.Raw != Agent.Raw;
        TimeChangedAfterEdit |= Review.Time is not null && agent.Time != Agent.Time;
        Agent = agent;
        return true;
    }

    /// <summary>The last review wins (IMP-6). An edit of the words or the time clears its flag (IMP-12).</summary>
    /// <exception cref="ValidationException">The review puts the start of the entry after now (IMP-9).</exception>
    public bool ChangeReview(ImportReview review, ImportBatch batch, DateTimeOffset now)
    {
        if (review == Review)
            return false;

        ValidationException.MustNot(StartOf(Agent, review, batch) > now, "time", FutureMessage);
        WordsChangedAfterEdit &= review.Words == Review.Words;
        TimeChangedAfterEdit &= review.Time == Review.Time;
        Review = review;
        return true;
    }

    internal Session ToSession(ImportBatch batch, DateTimeOffset now)
        => Session.Import(batch.TenantId, UserId, ClientSessionId, StartIn(batch), ZoneOf(Agent, batch), RawAtAccept(), Agent.Moods,
            Agent.Topics, batch.Id, now);

    protected ImportEntry() : base(IdPrefix) { }
}
