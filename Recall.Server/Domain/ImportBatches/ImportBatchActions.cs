namespace Recall.Server.Domain.ImportBatches;

using AuditEntries;

/// <summary>A caller accepted a batch (IMP-20). It holds only counts, so no word of an entry reaches the audit log.</summary>
[AuditActionName("import_batch_accepted")]
public sealed record ImportBatchAccepted(
    AuditSubject Subject,
    int SessionsMade,
    int EntriesRejected,
    int DaysRejected,
    int SkippedInJournal,
    int SkippedDeleted) : AuditAction(Subject);
