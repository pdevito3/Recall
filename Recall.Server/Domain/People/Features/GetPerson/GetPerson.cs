namespace Recall.Server.Domain.People.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetPerson
{
    public sealed record Query(string Id) : IRequest<PersonDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Query, PersonDto>
    {
        public async Task<PersonDto> Handle(Query request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.person_id", request.Id);

            var person = await dbContext.People
                .TagWithOperationCallSite("people.get")
                .AsNoTracking()
                .GetById(request.Id, cancellationToken);
            return person.ToPersonDto();
        }
    }
}
