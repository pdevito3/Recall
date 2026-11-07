namespace Recall.Server.Domain.Summaries.Features;

using System.Globalization;
using Exceptions;

public static class SummaryRoute
{
    private const string DateField = "date";

    /// <summary>Any day of the period, as yyyy-MM-dd.</summary>
    public static DateOnly ParseDate(string value)
    {
        var parsed = DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        ValidationException.Must(parsed, DateField, "The date must be a day as yyyy-MM-dd.");
        return date;
    }
}
