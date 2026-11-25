namespace Recall.Server.Domain.Sessions.Features;

using System.ComponentModel;
using Dtos;
using MediatR;
using ModelContextProtocol.Server;
using Resources.Entrypoints.Mcp;

/// <summary>
/// The SDK resolves <see cref="IMediator"/> from the request services, so it is not in the input schema.
/// </summary>
[McpServerToolType]
public static class GetJournalDaysTool
{
    [McpServerTool(Name = "list_journal_days", ReadOnly = true, UseStructuredContent = true)]
    [ToolAccess(ToolAccessKind.JournalRead)]
    [Description("Gets each day from `from` to `to`, both included, that already has a Session in the journal, with the count of its Sessions. Use it before an import to find the days of the source that overlap the journal. The range is at most 366 days, with dates from 1000-01-01 to 9998-12-31. The caller needs a journal.")]
    public static Task<McpList<JournalDayDto>> Handle(
        IMediator mediator,
        [Description("The first day, as YYYY-MM-DD.")] DateOnly from,
        [Description("The last day, as YYYY-MM-DD, on or after the first.")] DateOnly to,
        CancellationToken cancellationToken)
        => McpList.From(mediator.Send(new GetJournalDays.Query(from, to), cancellationToken));
}
