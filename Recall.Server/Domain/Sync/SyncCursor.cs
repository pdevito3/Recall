namespace Recall.Server.Domain.Sync;

using System.Globalization;
using Exceptions;

public static class SyncCursor
{
    public const long Start = 0;

    public static long Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Start;

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : throw new QueryInputException("cursor", "The cursor is not valid. Send the cursor of the page before.");
    }

    public static string Of(long sequence) => sequence.ToString(CultureInfo.InvariantCulture);
}
