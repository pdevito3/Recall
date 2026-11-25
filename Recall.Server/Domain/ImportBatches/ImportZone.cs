namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using TimeZones;

/// <summary>The zone of a batch or of an entry (IMP-1, IMP-10): a zone of a place that this host knows.</summary>
public static class ImportZone
{
    public const string UnknownMessage = "This server does not know this time zone. Pick another zone.";

    /// <exception cref="ValidationException">This host does not know the zone, or it is not the zone of a place.</exception>
    public static TimeZoneId Parse(string? value, string field)
        => TimeZoneId.TryOf(value) ?? throw new ValidationException(field, UnknownMessage);
}
