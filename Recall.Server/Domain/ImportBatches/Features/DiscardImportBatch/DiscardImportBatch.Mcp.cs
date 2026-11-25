namespace Recall.Server.Domain.ImportBatches.Features;

using System.ComponentModel;
using MediatR;
using ModelContextProtocol.Server;
using Resources.Entrypoints.Mcp;

/// <summary>
/// The SDK resolves <see cref="IMediator"/> from the request services, so it is not in the input schema.
/// </summary>
[McpServerToolType]
public static class DiscardImportBatchTool
{
    [McpServerTool(Name = "discard_import_batch", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [ToolAccess(ToolAccessKind.JournalWrite)]
    [Description("Deletes an open import batch with each of its entries. Nothing of it was in the journal, and each clientSessionId of the batch is free for a later import. An accepted batch cannot be discarded, because its Sessions are in the journal. The caller needs journal write.")]
    public static Task Handle(
        IMediator mediator,
        [Description("The ID of the batch.")] string id,
        CancellationToken cancellationToken)
        => mediator.Send(new DiscardImportBatch.Command(id), cancellationToken);
}
