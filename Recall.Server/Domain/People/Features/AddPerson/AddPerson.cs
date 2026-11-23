namespace Recall.Server.Domain.People.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Resources;
using Serilog;

public static class AddPerson
{
    public sealed record Command(PersonForCreationDto Dto) : IRequest<PersonDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, PersonDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<PersonDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();

            var person = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                var added = Person.Create(owner.TenantId, owner.UserId, request.Dto.Label, request.Dto.FullName, request.Dto.Notes);
                await dbContext.People.AddAsync(added, ct);
                await dbContext.SaveChangesAsync(ct);
                return added;
            }, cancellationToken);

            Activity.Current?.SetTag("app.person_id", person.Id);
            _logger.Here().Information("Added person {PersonId}", person.Id);
            return person.ToPersonDto();
        }
    }
}
