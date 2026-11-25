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
public static class GetImportBatchesTool
{
    [McpServerTool(Name = "list_import_batches", ReadOnly = true, UseStructuredContent = true)]
    [ToolAccess(ToolAccessKind.JournalRead)]
    [Description("Gets each import batch of the caller, newest first, with its state (Open or Accepted), its entry count, and its version. An accepted batch stays, with the counts of its accept. The caller needs a journal.")]
    public static Task<McpList<ImportBatchDto>> Handle(IMediator mediator, CancellationToken cancellationToken)
        => McpList.From(mediator.Send(new GetImportBatches.Query(), cancellationToken));
}
