namespace Recall.Server.Domain.ImportBatches.Features;

using System.ComponentModel;
using Dtos;
using MediatR;
using ModelContextProtocol.Server;
using Resources.Entrypoints.Mcp;

/// <summary>
/// The SDK resolves <see cref="IMediator"/> from the request services, so it is not in the input schema.
/// </summary>
[McpServerToolType]
public static class PutImportEntriesTool
{
    // A put replaces the agent layer of an entry that it names, so the tool is not additive: the MCP spec says destructive.
    [McpServerTool(Name = "put_import_entries", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [ToolAccess(ToolAccessKind.JournalWrite)]
    [Description("""
        Adds or changes entries of an open import batch by clientSessionId. The entries are not in the journal until the batch is accepted. The caller needs journal write.
        Send at most 500 entries in one call, each clientSessionId once, and at most 16 MB.
        Each entry: clientSessionId (1 to 100 characters, stable for the source entry), date (YYYY-MM-DD, the day of the source), position (0 to 719, the order in its day, unique in each date of the batch), time (HH:mm, only when the source gives one), timeZone (IANA, or leave it out for the zone of the batch), raw (the words of the source as Markdown, with no change to the words of the person), moods, and topics (each at most 50 values of at most 100 characters).
        An entry with no time starts at 12:00 plus position minutes on its date.
        Each entry gets one outcome: Invalid (with field and reason), InJournal (the ID is a Session), Deleted (the person deleted that Session), InOtherBatch, InTheFuture, PositionTaken (with positionTakenBy), Rejected (the person rejected it in the review), Unchanged, Changed, or Added. Changed and Added write the entry. Rejected writes it too, except when the put moves the entry off a rejected day, which writes nothing. Unchanged means that the batch already holds the same entry.
        The same call again changes nothing, so a retry or a second run of a whole import is safe. A put never changes the review of the person and removes no entry.
        Before a put, call list_journal_days to find the days that the journal already has.
        """)]
    public static Task<ImportPutResultDto> Handle(
        IMediator mediator,
        [Description("The ID of the batch, from open_import_batch.")] string id,
        [Description("The entries, at most 500.")] IReadOnlyList<ImportEntryPutDto> entries,
        CancellationToken cancellationToken)
        => mediator.Send(new PutImportEntries.Command(id, new ImportEntriesPutDto { Entries = entries }), cancellationToken);
}
