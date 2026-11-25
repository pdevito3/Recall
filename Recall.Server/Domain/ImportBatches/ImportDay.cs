namespace Recall.Server.Domain.ImportBatches;

/// <summary>The review of one date of a batch. A row exists only for a date that the person reviewed (IMP-6).</summary>
public class ImportDay : BaseEntity, IJournalOwned
{
    public const string IdPrefix = "impday";

    [NotSensitive] public string BatchId { get; private set; } = default!;

    public ImportBatch Batch { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [Sensitive] public DateOnly Date { get; private set; }

    public bool Rejected { get; private set; }

    internal static ImportDay Of(ImportBatch batch, DateOnly date, bool rejected)
        => new() { BatchId = batch.Id, Batch = batch, UserId = batch.UserId, Date = date, Rejected = rejected };

    internal void Review(bool rejected) => Rejected = rejected;

    protected ImportDay() : base(IdPrefix) { }
}
