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
            .Select(s => new { s.UserId, s.TimeZone })
            .FirstOrDefaultAsync(cancellationToken);
        return setting is null ? UserSetting.ZoneOf(null) : JournalZoneOf(setting.UserId, setting.TimeZone);
    }

    public static TimeZoneId JournalZoneOf(string userId, string? timeZone)
    {
        if (!UserSetting.IsKnownZone(timeZone) && WarnedOwners.TryAdd(userId, true))
            Log.ForContext(typeof(UserSettingQueries)).Warning("User {UserId} has a stored time zone that this host does not know, so the Journaling day comes from UTC", userId);

        return UserSetting.ZoneOf(timeZone);
    }
}
