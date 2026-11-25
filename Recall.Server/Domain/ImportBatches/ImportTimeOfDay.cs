namespace Recall.Server.Domain.ImportBatches;

using System.Globalization;
using Exceptions;

public static class ImportTimeOfDay
{
    public const string Format = "HH:mm";
    public const int TextLength = 5;

    public static string? Text(TimeOnly? time) => time?.ToString(Format, CultureInfo.InvariantCulture);

    public static TimeOnly? Parse(string? value, string field)
    {
        if (value is null)
            return null;

        return TimeOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new ValidationException(field, "Give the time as HH:mm on a 24-hour clock, for example 21:30.");
    }
}
