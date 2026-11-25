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
public static class RemoveImportEntriesTool
{
    [McpServerTool(Name = "remove_import_entries", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [ToolAccess(ToolAccessKind.JournalWrite)]
    [Description("Removes the named entries of an open import batch. Each ID gets one outcome: Removed, NotFound (the batch does not hold it), or Rejected (the person rejected it in the review, so it stays with its reject). The caller needs journal write.")]
    public static Task<ImportRemoveResultDto> Handle(
        IMediator mediator,
        [Description("The ID of the batch.")] string id,
        [Description("The clientSessionId of each entry to remove.")] IReadOnlyList<string> clientSessionIds,
        CancellationToken cancellationToken)
        => mediator.Send(
            new RemoveImportEntries.Command(id, new ImportEntriesRemoveDto { ClientSessionIds = clientSessionIds }),
            cancellationToken);
}
