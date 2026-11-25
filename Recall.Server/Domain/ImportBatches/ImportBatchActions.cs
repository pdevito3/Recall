namespace Recall.Server.Domain.ImportBatches;

using AuditEntries;

[AuditActionName("import_batch_accepted")]
public sealed record ImportBatchAccepted(
    AuditSubject Subject,
    int SessionsMade,
    int EntriesRejected,
    int DaysRejected,
    int SkippedInJournal,
    int SkippedDeleted) : AuditAction(Subject);
