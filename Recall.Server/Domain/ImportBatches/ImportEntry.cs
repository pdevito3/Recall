namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using Sessions;
using TimeZones;

public class ImportEntry : BaseEntity, IJournalOwned
{
    public const string IdPrefix = "impent";
    public const string FutureMessage = "The time of an entry cannot be in the future.";
    public const string NotInBatchMessage = "This entry is not in the batch.";

    [NotSensitive] public string BatchId { get; private set; } = default!;

    public ImportBatch Batch { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [NotSensitive] public string ClientSessionId { get; private set; } = default!;

    [Sensitive] public ImportAgentLayer Agent { get; private set; } = default!;

    [Sensitive] public ImportReview Review { get; private set; } = ImportReview.None;

    public bool WordsChangedAfterEdit { get; private set; }

    public bool TimeChangedAfterEdit { get; private set; }

    public static TimeZoneId ZoneOf(ImportAgentLayer agent, ImportBatch batch) => TimeZoneId.TryOf(agent.TimeZone) ?? TimeZoneId.OrUtc(batch.TimeZone);

    public static DateTimeOffset StartOf(ImportAgentLayer agent, ImportReview review, ImportBatch batch)
        => ImportStartTime.Of(agent.Date, agent.Position, agent.Time, review.Time, ZoneOf(agent, batch));

    public static bool IsRejected(ImportAgentLayer agent, ImportReview review, ImportBatch batch) => review.Rejected || batch.IsDayRejected(agent.Date);

    public DateTimeOffset StartIn(ImportBatch batch) => StartOf(Agent, Review, batch);

    public bool IsRejectedIn(ImportBatch batch) => IsRejected(Agent, Review, batch);

    public string RawAtAccept() => Review.Words ?? Agent.ToDocument();

    internal static ImportEntry Add(ImportBatch batch, string clientSessionId, ImportAgentLayer agent)
        => new() { BatchId = batch.Id, Batch = batch, UserId = batch.UserId, ClientSessionId = clientSessionId, Agent = agent };

    public bool PutFromAgent(ImportAgentLayer agent)
    {
        if (agent == Agent)
            return false;

        WordsChangedAfterEdit |= Review.Words is not null && agent.Raw != Agent.Raw;
        TimeChangedAfterEdit |= Review.Time is not null && agent.Time != Agent.Time;
        Agent = agent;
        return true;
    }

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
