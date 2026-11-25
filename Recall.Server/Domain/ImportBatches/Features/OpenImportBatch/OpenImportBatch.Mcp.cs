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
public static class OpenImportBatchTool
{
    [McpServerTool(Name = "open_import_batch", Destructive = false, UseStructuredContent = true)]
    [ToolAccess(ToolAccessKind.JournalWrite)]
    [Description("Opens an import batch: a draft area outside the journal for the entries of another app. No entry of the batch is in the journal until the batch is accepted. Then add the entries with put_import_entries. The caller needs journal write.")]
    public static Task<ImportBatchDto> Handle(
        IMediator mediator,
        [Description("The name of the batch, 1 to 200 characters, for example: Day One 2019 to 2023.")] string name,
        [Description("The IANA time zone of the source, for example America/Chicago. Each entry with no zone of its own takes it.")] string timeZone,
        CancellationToken cancellationToken)
        => mediator.Send(
            new OpenImportBatch.Command(new ImportBatchForCreationDto { Name = name, TimeZone = timeZone }),
            cancellationToken);
}
