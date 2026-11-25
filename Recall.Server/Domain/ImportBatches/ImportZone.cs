namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using TimeZones;

public static class ImportZone
{
    public const string UnknownMessage = "This server does not know this time zone. Pick another zone.";

    public static TimeZoneId Parse(string? value, string field)
        => TimeZoneId.TryOf(value) ?? throw new ValidationException(field, UnknownMessage);
}
