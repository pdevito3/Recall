namespace Recall.Server.Domain.Summaries.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using UserSettings;

public static class GetSummaryById
{
    public sealed record Query(string Id) : IRequest<SummaryDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, SummaryDto>
    {
        public async Task<SummaryDto> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.summary_id", request.Id);

            var summary = await dbContext.Summaries
                .TagWithOperationCallSite("summaries.get_by_id")
                .AsNoTracking()
                .GetById(request.Id, cancellationToken);
            var zone = await dbContext.LoadJournalZoneAsync(cancellationToken);
            var sources = await dbContext.LoadSourcesAsync(zone, summary.Key, cancellationToken);
            return summary.ToSummaryDto(summary.Key, sources.StateOf(summary.Key));
        }
    }
}
