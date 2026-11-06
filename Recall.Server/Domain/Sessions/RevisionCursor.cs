namespace Recall.Server.Domain.Sessions;

using System.Globalization;
using Exceptions;

public static class RevisionCursor
{
    public static int Parse(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new QueryInputException("cursor", "The cursor is not valid. Send the nextCursor of the page before.");

    public static string Of(int number) => number.ToString(CultureInfo.InvariantCulture);
}
