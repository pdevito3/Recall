namespace Recall.Server.Domain.ImportBatches;

using Exceptions;

public class ImportBatch : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "imp";
    public const int NameMaxLength = 200;
    public const int PutMaxEntries = 500;

    public const int AcceptMaxEntries = 20_000;
    public const string NotFoundMessage = "This import batch does not exist.";
    public const string AcceptedMessage = "This batch is in your journal, so it cannot change.";
    public const string ChangedMessage = "This batch changed after you read it. Read it again, then accept it.";

    private const string NameField = "name";
    private const string TimeZoneField = "timeZone";

    private readonly List<ImportDay> _days = [];

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [Sensitive] public string Name { get; private set; } = default!;

    [Sensitive] public string TimeZone { get; private set; } = default!;

    public ImportBatchState State { get; private set; }

    [NotSensitive] public DateTimeOffset OpenedAt { get; private set; }

    [NotSensitive] public DateTimeOffset? AcceptedAt { get; private set; }

    [NotSensitive] public ImportAcceptResult Result { get; private set; } = ImportAcceptResult.None;

    public IReadOnlyList<ImportDay> Days => _days;

    public static readonly string NameMessage = $"Give the batch a name of 1 to {NameMaxLength} characters.";

    public static readonly string PutCallMessage = $"Send each entry once, and at most {PutMaxEntries} entries in one call.";

    public static readonly string TooManyToAcceptMessage =
        $"This batch has more than {AcceptMaxEntries:N0} entries to accept. Split it into smaller batches, for example one for each year.";

    public static ImportBatch Open(string tenantId, string userId, string name, string timeZone, DateTimeOffset now)
    {
        var trimmed = name.Trim();
        ValidationException.Must(trimmed.Length > 0, NameField, NameMessage);
        ValidationException.ThrowWhenLongerThan(trimmed, NameMaxLength, NameField, NameMessage);
        ValidationException.ThrowWhenHasCharacterNotStorable(trimmed, NameField, "The name of the batch has a character that is not allowed.");

        return new ImportBatch
        {
            TenantId = tenantId,
            UserId = userId,
            Name = trimmed,
            TimeZone = ImportZone.Parse(timeZone, TimeZoneField).Value,
            State = ImportBatchState.Open,
            OpenedAt = now,
        };
    }

    public void EnsureOpen()
    {
        if (State != ImportBatchState.Open)
            throw new ConflictException(string.Empty, AcceptedMessage);
    }

    public static void CheckPutCall(IReadOnlyCollection<string> clientSessionIds)
        => ValidationException.Must(
            clientSessionIds.Count <= PutMaxEntries && clientSessionIds.Distinct(StringComparer.Ordinal).Count() == clientSessionIds.Count,
            "entries", PutCallMessage);

    public bool IsDayRejected(DateOnly date) => _days.Any(day => day.Date == date && day.Rejected);

    public bool ReviewDay(DateOnly date, bool rejected)
    {
        EnsureOpen();
        var day = _days.SingleOrDefault(d => d.Date == date);
        if ((day?.Rejected ?? false) == rejected)
            return false;

        if (day is null)
            _days.Add(ImportDay.Of(this, date, rejected));
        else
            day.Review(rejected);
        return true;
    }

    public ImportPut Put(IReadOnlyList<ImportPutEntry> entries, ImportPutFacts facts, DateTimeOffset now)
    {
        EnsureOpen();
        var byId = facts.Stored.ToDictionary(entry => entry.ClientSessionId, StringComparer.Ordinal);
        var results = new Dictionary<string, ImportPutResult>(StringComparer.Ordinal);
        var candidates = new List<(string Id, ImportAgentLayer Agent, ImportIdentity Identity, ImportEntry? Known)>();
        foreach (var entry in entries)
        {
            var id = entry.ClientSessionId;
            if (entry.Agent is not { } agent)
            {
                results[id] = new ImportPutResult(id, ImportPutOutcome.Invalid, entry.Field, entry.Reason);
                continue;
            }

            var other = byId.GetValueOrDefault(id);
            var identity = ImportIdentities.Classify(facts.SessionIds.Contains(id), facts.TombstoneIds.Contains(id), other?.BatchId, Id);
            var known = identity == ImportIdentity.InThisBatch ? other : null;
            var inTheFuture = ImportEntry.StartOf(agent, known?.Review ?? ImportReview.None, this) > now;
            if (inTheFuture || identity is ImportIdentity.InJournal or ImportIdentity.Deleted or ImportIdentity.InOtherBatch)
                results[id] = new ImportPutResult(id, ImportPutOutcomes.Of(identity, inTheFuture, false, false, false));
            else
                candidates.Add((id, agent, identity, known));
        }

        var takenBy = ImportPositions.Check(
            facts.Stored.Where(entry => entry.BatchId == Id)
                .ToDictionary(entry => entry.ClientSessionId, entry => new ImportSlot(entry.Agent.Date, entry.Agent.Position), StringComparer.Ordinal),
            candidates.ToDictionary(c => c.Id, c => new ImportSlot(c.Agent.Date, c.Agent.Position), StringComparer.Ordinal));
        var added = new List<ImportEntry>();
        foreach (var (id, agent, identity, known) in candidates)
        {
            var other = takenBy.GetValueOrDefault(id);
            var rejected = ImportEntry.IsRejected(agent, known?.Review ?? ImportReview.None, this);
            var outcome = ImportPutOutcomes.Of(identity, false, other is not null, rejected, known?.Agent == agent);
            if (outcome.Stores())
            {
                if (known is null)
                    added.Add(ImportEntry.Add(this, id, agent));
                else
                    known.PutFromAgent(agent);
            }

            results[id] = new ImportPutResult(id, outcome, PositionTakenBy: other);
        }

        return new ImportPut(entries.Select(entry => results[entry.ClientSessionId]).ToList(), added);
    }

    public ImportRemove Remove(IReadOnlyList<string> clientSessionIds, IReadOnlyCollection<ImportEntry> stored)
    {
        EnsureOpen();
        var byId = stored.Where(entry => entry.BatchId == Id).ToDictionary(entry => entry.ClientSessionId, StringComparer.Ordinal);
        var results = clientSessionIds
            .Select(id => new ImportRemoveResult(id, byId.GetValueOrDefault(id) switch
            {
                null => ImportRemoveOutcome.NotFound,
                var entry when entry.IsRejectedIn(this) => ImportRemoveOutcome.Rejected,
                _ => ImportRemoveOutcome.Removed,
            }))
            .ToList();
        return new ImportRemove(results, results.Where(r => r.Outcome == ImportRemoveOutcome.Removed).Select(r => byId[r.ClientSessionId]).ToList());
    }

    public ImportAccept Accept(
        uint version, IReadOnlyList<ImportEntry> entries, IReadOnlySet<string> sessionIds, IReadOnlySet<string> tombstoneIds, DateTimeOffset now)
    {
        EnsureOpen();
        var kept = entries.Where(entry => !entry.IsRejectedIn(this)).ToList();
        ValidationException.Must(kept.Count <= AcceptMaxEntries, string.Empty, TooManyToAcceptMessage);
        if (version != Version)
            throw new StaleVersionException(ChangedMessage);

        var identities = kept.ToLookup(entry => ImportIdentities.Classify(
            sessionIds.Contains(entry.ClientSessionId), tombstoneIds.Contains(entry.ClientSessionId), Id, Id));
        var sessions = identities[ImportIdentity.InThisBatch].Select(entry => entry.ToSession(this, now)).ToList();

        State = ImportBatchState.Accepted;
        AcceptedAt = now;
        Result = new ImportAcceptResult(
            sessions.Count, entries.Count - kept.Count, _days.Count(day => day.Rejected),
            identities[ImportIdentity.InJournal].Count(), identities[ImportIdentity.Deleted].Count());
        _days.Clear();
        return new ImportAccept(sessions, entries);
    }

    protected ImportBatch() : base(IdPrefix) { }
}
