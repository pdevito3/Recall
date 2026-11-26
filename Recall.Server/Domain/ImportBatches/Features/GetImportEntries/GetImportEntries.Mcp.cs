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
public static class GetImportEntriesTool
{
    [McpServerTool(Name = "list_import_entries", ReadOnly = true, UseStructuredContent = true)]
    [ToolAccess(ToolAccessKind.JournalRead)]
    [Description("Gets the entries of an import batch in order of date and position, with raw exactly as the put sent it, the review of the person, isRejected, and startsAt (the start that the accept gives the entry now). It also gives the version of the batch and the rejected days. The entries are not in the journal until the batch is accepted. The caller needs a journal.")]
    public static Task<ImportEntryPageForAgentDto> Handle(
        IMediator mediator,
        [Description("The ID of the batch.")] string id,
        CancellationToken cancellationToken,
        [Description("The first date to get, as YYYY-MM-DD, included. Leave it out for no lower limit.")] DateOnly? from = null,
        [Description("The last date to get, as YYYY-MM-DD, included. Leave it out for no upper limit.")] DateOnly? to = null)
        => mediator.Send(new GetImportEntries.AgentQuery(id, from, to), cancellationToken);
}
