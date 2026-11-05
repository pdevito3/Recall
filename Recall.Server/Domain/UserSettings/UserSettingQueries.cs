namespace Recall.Server.Domain.UserSettings;

using Databases;
using Microsoft.EntityFrameworkCore;
using TimeZones;

public static class UserSettingQueries
{
    public static async Task<TimeZoneId> LoadJournalZoneAsync(this AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var timeZone = await dbContext.UserSettings
            .TagWithOperationCallSite("user_settings.zone")
            .AsNoTracking()
            .Select(s => s.TimeZone)
            .FirstOrDefaultAsync(cancellationToken);
        return UserSetting.ZoneOf(timeZone);
    }
}
