namespace Recall.Server.Domain.ImportBatches;

using Exceptions;

/// <summary>
/// The draft area of one import (ADR 0026). Its entries and days are outside the journal until the accept, which makes a
/// Session of each kept entry and deletes the entries and days. The row stays, with its counts, as the mark of its Sessions.
/// </summary>
public class ImportBatch : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "imp";
    public const int NameMaxLength = 200;
    public const string NotFoundMessage = "This import batch does not exist.";
    public const string AcceptedMessage = "This batch is in your journal, so it cannot change.";

    private const string NameField = "name";
    private const string TimeZoneField = "timeZone";

    private readonly List<ImportDay> _days = [];

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [Sensitive] public string Name { get; private set; } = default!;

    /// <summary>The IANA zone of each entry that has no zone of its own (IMP-10).</summary>
    [Sensitive] public string TimeZone { get; private set; } = default!;

    public ImportBatchState State { get; private set; }

    [NotSensitive] public DateTimeOffset OpenedAt { get; private set; }

    [NotSensitive] public DateTimeOffset? AcceptedAt { get; private set; }

    [NotSensitive] public ImportAcceptResult Result { get; private set; } = ImportAcceptResult.None;

    public IReadOnlyList<ImportDay> Days => _days;

    public static readonly string NameMessage = $"Give the batch a name of 1 to {NameMaxLength} characters.";

    /// <exception cref="ValidationException">The name is blank or too long, or the zone is not a zone of a place that this host knows.</exception>
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

    /// <exception cref="ConflictException">The batch is accepted (IMP-23).</exception>
    public void EnsureOpen()
    {
        if (State != ImportBatchState.Open)
            throw new ConflictException(string.Empty, AcceptedMessage);
    }

    public bool IsDayRejected(DateOnly date) => _days.Any(day => day.Date == date && day.Rejected);

    /// <summary>The last review of a day wins (IMP-6). It gives false when the day already has this review.</summary>
    /// <exception cref="ConflictException">The batch is accepted (IMP-23).</exception>
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

    protected ImportBatch() : base(IdPrefix) { }
}
