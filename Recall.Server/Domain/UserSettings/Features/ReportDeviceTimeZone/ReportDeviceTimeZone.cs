namespace Recall.Server.Domain.UserSettings.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Jobs;
using Serilog;

public static class ReportDeviceTimeZone
{
    public sealed record Command(DeviceTimeZoneDto Dto) : IRequest<UserSettingDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, SummarySchedules schedules) : IRequestHandler<Command, UserSettingDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<UserSettingDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var owner = dbContext.RequireJournalOwner();
            Activity.Current?.SetTag("app.user_id", owner.UserId);

            var applied = false;
            var setting = await RootLock.Tenant.RunSharedAsync(dbContext, owner.TenantId, async ct =>
            {
                var stored = await dbContext.UserSettings
                    .TagWithOperationCallSite("user_settings.report_zone.load")
                    .FirstOrDefaultAsync(ct);
                var row = stored ?? UserSetting.DefaultsFor(owner.TenantId, owner.UserId);

                applied = row.ReportDeviceZone(request.Dto.TimeZone);
                if (!applied)
                    return row;

                if (stored is null)
                    await dbContext.UserSettings.AddAsync(row, ct);
                else
                    dbContext.MoveVersionOf(row);

                await dbContext.SaveChangesAsync(ct);
                return row;
            }, cancellationToken);

            if (applied)
                await schedules.TryEnsureAsync(owner.UserId, owner.TenantId, setting.JournalZone, cancellationToken);

            _logger.Here().Information("Took a device time zone of user {UserId}, applied {Applied}", owner.UserId, applied);
            return setting.ToUserSettingDto();
        }
    }
}
