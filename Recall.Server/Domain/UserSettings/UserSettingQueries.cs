namespace Recall.Server.Domain.UserSettings;

using System.Collections.Concurrent;
using Databases;
using Microsoft.EntityFrameworkCore;
using Serilog;
using TimeZones;

public static class UserSettingQueries
{
    private static readonly ConcurrentDictionary<string, bool> WarnedOwners = new();

    public static async Task<TimeZoneId> LoadJournalZoneAsync(this AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var setting = await dbContext.UserSettings
            .TagWithOperationCallSite("user_settings.zone")
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return JournalZoneOf(setting);
    }

    public static TimeZoneId JournalZoneOf(UserSetting? setting)
    {
        if (setting is { TimeZoneIsKnown: false } && WarnedOwners.TryAdd(setting.UserId, true))
            Log.ForContext(typeof(UserSettingQueries)).Warning("User {UserId} has a stored time zone that this host does not know, so the Journaling day comes from UTC", setting.UserId);

        return setting?.JournalZone ?? TimeZoneId.OrUtc(null);
    }
}
