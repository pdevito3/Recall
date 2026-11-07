namespace Recall.Server.Domain.Summaries.Features;

using System.Globalization;
using Exceptions;

public static class SummaryRoute
{
    private const string DateField = "date";

    /// <summary>
    /// A period and its zone stay inside the calendar of .NET, so the end of a Year, or the start of a Day in a zone ahead of
    /// UTC, never overflows.
    /// </summary>
    public const int FirstYear = 1900;

    public const int LastYear = 9998;

    /// <summary>Any day of the period, as yyyy-MM-dd.</summary>
    public static DateOnly ParseDate(string value)
    {
        var parsed = DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        ValidationException.Must(parsed, DateField, "The date must be a day as yyyy-MM-dd.");
        ValidationException.Must(date.Year is >= FirstYear and <= LastYear, DateField, $"The date must be in the years {FirstYear} to {LastYear}.");
        return date;
    }
}
