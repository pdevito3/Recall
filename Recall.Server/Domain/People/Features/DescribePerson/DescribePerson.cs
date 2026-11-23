namespace Recall.Server.Domain.People.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Resources;
using Serilog;

/// <summary>Sets the details of a Person. No mention shows them, so no Session changes.</summary>
public static class DescribePerson
{
    public sealed record Command(string Id, PersonDetailsDto Dto) : IRequest<PersonDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, PersonDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<PersonDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.person_id", request.Id);

            var person = await dbContext.People
                .TagWithOperationCallSite("people.describe.load")
                .GetById(request.Id, cancellationToken);
            person.Describe(request.Dto.FullName, request.Dto.Notes, request.Dto.Version);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.Here().Information("Described person {PersonId}", person.Id);
            return person.ToPersonDto();
        }
    }
}
