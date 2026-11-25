namespace Recall.Server.Domain.ImportBatches;

using TimeZones;

/// <summary>
/// The start of an entry (IMP-7): the time of the person, else the time of the agent, else 12:00 plus one minute for each
/// position, on the date of the entry in its zone. No row stores it, so a change of one entry never moves another.
/// </summary>
public static class ImportStartTime
{
    public static readonly TimeOnly FirstPosition = new(12, 0);

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
