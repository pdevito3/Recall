namespace Recall.Server.Domain.UserSettings.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public static class ChangeUserSetting
{
    public sealed record Command(UserSettingChangeDto Dto) : IRequest<UserSettingDto>;

    public sealed class Handler(AppDbContext dbContext) : IRequestHandler<Command, UserSettingDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<UserSettingDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();
            var dto = request.Dto;
            Activity.Current?.SetTag("app.user_id", owner.UserId);

            var applied = false;
            var setting = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                var stored = await dbContext.UserSettings
                    .TagWithOperationCallSite("user_settings.change.load")
                    .FirstOrDefaultAsync(ct);
                var row = stored ?? UserSetting.DefaultsFor(owner.TenantId, owner.UserId);

                applied = row.Change(dto.TimeZone, dto.LocationCaptureEnabled, dto.RequirePeopleProposalApproval, dto.Version);
                if (!applied)
                    return row;

                if (stored is null)
                    await dbContext.UserSettings.AddAsync(row, ct);
                else
                    dbContext.MoveVersionOf(row);

                await dbContext.SaveChangesAsync(ct);
                return row;
            }, cancellationToken);

            _logger.Here().Information("Took a settings change of user {UserId}, applied {Applied}", owner.UserId, applied);
            return setting.ToUserSettingDto();
        }
    }
}
