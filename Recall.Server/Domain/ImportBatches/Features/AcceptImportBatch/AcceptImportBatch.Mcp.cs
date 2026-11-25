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
public static class AcceptImportBatchTool
{
    // An accept removes the entries and the days of the batch, so the tool is not additive: the MCP spec says destructive.
    [McpServerTool(Name = "accept_import_batch", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [ToolAccess(ToolAccessKind.JournalWrite)]
    [Description("""
        Accepts an open import batch: makes one journal Session of each entry that the person did not reject, in one save, and removes the entries of the batch. The caller needs journal write.
        With the import review on, which is the default, only the person accepts, in Recall in a browser, and this tool gives permission_denied. Then tell the person to review and accept the batch in Recall.
        An accepted batch gives its stored counts again. An accept of more than 20,000 entries gives invalid_argument: split the import into smaller batches, for example one for each year.
        """)]
    public static Task<ImportBatchDto> Handle(
        IMediator mediator,
        [Description("The ID of the batch.")] string id,
        [Description("The version of the batch from list_import_entries or the last write. If the batch changed after that, the tool gives the aborted error.")] uint version,
        CancellationToken cancellationToken)
        => mediator.Send(new AcceptImportBatch.Command(id, new ImportBatchAcceptDto { Version = version }), cancellationToken);
}
