namespace Recall.Server.Domain.Corrections.Features;

using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;

public static class GetCorrectionList
{
    public sealed record Query(CorrectionParametersDto Parameters) : IRequest<PagedList<CorrectionDto>>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, PagedList<CorrectionDto>>
    {
        public async Task<PagedList<CorrectionDto>> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();

            var corrections = dbContext.Corrections
                .TagWithOperationCallSite("corrections.list")
                .AsNoTracking()
                .ToCorrectionDtoQueryable()
                .OrderBy(c => c.CanonicalTerm).ThenBy(c => c.Id);
            return await PagedList.CreateAsync(corrections, request.Parameters.PageNumber, request.Parameters.PageSize, cancellationToken);
        }
    }
}
