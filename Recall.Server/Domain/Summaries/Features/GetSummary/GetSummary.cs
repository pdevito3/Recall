namespace Recall.Server.Domain.Summaries.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>Reads the Summary of a period with no change. A period of no Summary gives <c>Missing</c>.</summary>
public static class GetSummary
{
    public sealed record Query(SummaryPeriod Period, DateOnly Date) : IRequest<SummaryDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, SummaryDto>
    {
        public async Task<SummaryDto> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();
            var key = PeriodKey.Holding(request.Period, request.Date);
            Activity.Current?.SetTag("app.summary.period", key.Period.ToString());

            var sources = await dbContext.LoadSourcesAsync(key, cancellationToken);
            var summary = await dbContext.Summaries
                .TagWithOperationCallSite("summaries.get")
                .AsNoTracking()
                .FindAsync(key, cancellationToken);
            return summary.ToSummaryDto(key, sources.StateOf(key));
        }
    }
}
