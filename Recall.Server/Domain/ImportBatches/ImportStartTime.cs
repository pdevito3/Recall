namespace Recall.Server.Domain.ImportBatches;

using TimeZones;

public static class ImportStartTime
{
    public const int FirstPositionHour = 12;

    public static readonly TimeOnly FirstPosition = new(FirstPositionHour, 0);

    // The longest gap of the tz database is a whole day, when Pacific/Apia moved across the date line in 2011.
    private const int MostMinutesInAGap = 24 * 60;

    public static DateTimeOffset Of(DateOnly date, int position, TimeOnly? agentTime, TimeOnly? personTime, TimeZoneId zone)
    {
        var local = date.ToDateTime(personTime ?? agentTime ?? FirstPosition.AddMinutes(position));
        for (var minute = 0; minute <= MostMinutesInAGap; minute++)
        {
            var wall = local.AddMinutes(minute);
            if (zone.ToInstant(DateOnly.FromDateTime(wall), TimeOnly.FromDateTime(wall)) is { } instant)
                return instant;
        }

        throw new InvalidOperationException($"The zone {zone} has no time in the day after {local:O}.");
    }
}
