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
    public sealed record Command(UserSettingChangeDto Dto) : IRequest<UserSettingDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<Command, UserSettingDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<UserSettingDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();
            var dto = request.Dto;
            Activity.Current?.SetTag("app.user_id", owner.UserId);

            var setting = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                var row = await dbContext.UserSettings
                    .TagWithOperationCallSite("user_settings.change.load")
                    .FirstOrDefaultAsync(ct);
                if (row is null)
                {
                    row = UserSetting.DefaultsFor(owner.TenantId, owner.UserId);
                    await dbContext.UserSettings.AddAsync(row, ct);
                }

                row.Change(dto.TimeZone, dto.LocationCaptureEnabled, dto.RequirePeopleTagApproval, dto.SavedAt, timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(ct);
                return row;
            }, cancellationToken);

            _logger.Here().Information("Changed the settings of user {UserId}", owner.UserId);
            return setting.ToUserSettingDto();
        }
    }
}
