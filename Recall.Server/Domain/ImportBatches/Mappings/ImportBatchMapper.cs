namespace Recall.Server.Domain.ImportBatches.Mappings;

using Dtos;

public static class ImportBatchMapper
{
    public static ImportBatchDto ToImportBatchDto(this ImportBatch batch, int entryCount) => new()
    {
        Id = batch.Id,
        Name = batch.Name,
        TimeZone = batch.TimeZone,
        State = batch.State.ToString(),
        OpenedAt = batch.OpenedAt,
        AcceptedAt = batch.AcceptedAt,
        EntryCount = entryCount,
        Result = batch.State == ImportBatchState.Accepted ? batch.Result.ToImportAcceptResultDto() : null,
        Version = batch.Version,
    };

    public static ImportEntryDto ToImportEntryDto(this ImportEntry entry, ImportBatch batch) => new()
    {
        ClientSessionId = entry.ClientSessionId,
        Date = entry.Agent.Date,
        Position = entry.Agent.Position,
        Time = ImportTimeOfDay.Text(entry.Agent.Time),
        TimeZone = entry.Agent.TimeZone,
        Raw = entry.Agent.Raw,
        Document = entry.RawAtAccept(),
        Moods = entry.Agent.Moods,
        Topics = entry.Agent.Topics,
        Review = new ImportReviewDto { Rejected = entry.Review.Rejected, Words = entry.Review.Words, Time = ImportTimeOfDay.Text(entry.Review.Time) },
        IsRejected = entry.IsRejectedIn(batch),
        StartsAt = entry.StartIn(batch),
        WordsChangedAfterEdit = entry.WordsChangedAfterEdit,
        TimeChangedAfterEdit = entry.TimeChangedAfterEdit,
    };

    public static ImportPutResultDto ToImportPutResultDto(this ImportPut put, ImportBatch batch) => new()
    {
        Version = batch.Version,
        Outcomes = put.Results.Select(r => new ImportPutOutcomeDto
        {
            ClientSessionId = r.ClientSessionId,
            Outcome = r.Outcome.ToString(),
            Field = r.Field,
            Reason = r.Reason,
            PositionTakenBy = r.PositionTakenBy,
        }).ToList(),
    };

    public static ImportRemoveResultDto ToImportRemoveResultDto(this ImportRemove remove, ImportBatch batch) => new()
    {
        Version = batch.Version,
        Outcomes = remove.Results.Select(r => new ImportRemoveOutcomeDto { ClientSessionId = r.ClientSessionId, Outcome = r.Outcome.ToString() }).ToList(),
    };

    private static ImportAcceptResultDto ToImportAcceptResultDto(this ImportAcceptResult result) => new()
    {
        SessionsMade = result.SessionsMade,
        EntriesRejected = result.EntriesRejected,
        DaysRejected = result.DaysRejected,
        SkippedInJournal = result.SkippedInJournal,
        SkippedDeleted = result.SkippedDeleted,
    };
}
