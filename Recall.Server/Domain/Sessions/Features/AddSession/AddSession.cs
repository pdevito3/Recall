namespace Recall.Server.Domain.Sessions.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using UserSettings;

public static class AddSession
{
    public sealed record Result(SessionDto Session, bool IsNew);

    public sealed record Command(SessionForCreationDto Dto) : IRequest<Result>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, Result>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();
            var dto = request.Dto;

            var result = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                var setting = await dbContext.UserSettings
                    .TagWithOperationCallSite("sessions.add.settings")
                    .AsNoTracking()
                    .FirstOrDefaultAsync(ct);
                var existing = await dbContext.Sessions
                    .TagWithOperationCallSite("sessions.add.replay")
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.ClientSessionId == dto.ClientSessionId, ct);
                if (existing is not null)
                    return new Result(existing.ToSessionDto(await ProposalPeople.LoadAsync(dbContext, [existing], ct)), false);

                var deleted = await dbContext.SyncChanges
                    .TagWithOperationCallSite("sessions.add.tombstone")
                    .AnyAsync(c => c.IsTombstone && c.ClientSessionId == dto.ClientSessionId, ct);
                if (deleted)
                    throw new ConflictException("clientSessionId", "This Session was deleted, so it cannot come back. Drop the local copy.");

                var location = CapturedLocation.Admit(setting?.LocationCaptureEnabled ?? false, dto.Latitude, dto.Longitude);
                var zone = UserSettingQueries.ZoneOfANewSession(setting, dto.TimeZone);
                var session = Session.Create(owner.TenantId, owner.UserId, dto.ClientSessionId, dto.StartedAt, location, zone, timeProvider.GetUtcNow());
                await dbContext.Sessions.AddAsync(session, ct);
                await dbContext.SaveChangesAsync(ct);
                return new Result(session.ToSessionDto(await ProposalPeople.LoadAsync(dbContext, [session], ct)), true);
            }, cancellationToken);

            Activity.Current?.SetTag("app.session_id", result.Session.Id);
            Activity.Current?.SetTag("app.session.is_new", result.IsNew);
            _logger.Here().Information("Added session {SessionId}, new {IsNew}", result.Session.Id, result.IsNew);
            return result;
        }
    }
}
