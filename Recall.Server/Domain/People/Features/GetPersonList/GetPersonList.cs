namespace Recall.Server.Domain.People.Features;

using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;

public static class GetPersonList
{
    public sealed record Query(PersonParametersDto Parameters) : IRequest<PagedList<PersonDto>>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, PagedList<PersonDto>>
    {
        public async Task<PagedList<PersonDto>> Handle(Query request, CancellationToken cancellationToken)
        {
            dbContext.RequireJournalOwner();

            var people = dbContext.People
                .TagWithOperationCallSite("people.list")
                .AsNoTracking()
                .ToPersonDtoQueryable()
                .OrderBy(p => p.Label).ThenBy(p => p.Id);
            return await PagedList.CreateAsync(people, request.Parameters.PageNumber, request.Parameters.PageSize, cancellationToken);
        }
    }
}
