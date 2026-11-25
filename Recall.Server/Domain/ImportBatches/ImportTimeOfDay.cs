namespace Recall.Server.Domain.ImportBatches;

using System.Globalization;
using Exceptions;

/// <summary>A time of day of an entry, from the agent or the person, as <c>HH:mm</c> on the clock of the entry zone.</summary>
public static class ImportTimeOfDay
{
    public const string Format = "HH:mm";
    public const int TextLength = 5;

    public static string? Text(TimeOnly? time) => time?.ToString(Format, CultureInfo.InvariantCulture);

    /// <exception cref="ValidationException">The value is not a time of day as <c>HH:mm</c>.</exception>
    public static TimeOnly? Parse(string? value, string field)
    {
        if (value is null)
            return null;

        return TimeOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new ValidationException(field, "Give the time as HH:mm on a 24-hour clock, for example 21:30.");
    }
}
