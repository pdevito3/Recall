namespace Recall.Server.Databases.Sqlite;

using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public sealed class UtcTextConverter() : ValueConverter<DateTimeOffset, string>(
    value => ToText(value),
    text => FromText(text))
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public static string ToText(DateTimeOffset value)
        => value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset StoredInstantOf(DateTimeOffset instant)
        => new(instant.UtcTicks - instant.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    public static DateTimeOffset FromText(string text)
        => DateTimeOffset.ParseExact(text, Format, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
