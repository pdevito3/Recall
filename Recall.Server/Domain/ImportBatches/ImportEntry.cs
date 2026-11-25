namespace Recall.Server.Domain.ImportBatches;

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

    protected ImportEntry() : base(IdPrefix) { }
}
