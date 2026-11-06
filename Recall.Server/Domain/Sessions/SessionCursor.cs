namespace Recall.Server.Domain.Sessions;

using System.Globalization;
using Exceptions;

public readonly record struct SessionCursor(DateTimeOffset StartedAt, string Id)
{
    private static readonly long MinMicroseconds = MicrosecondsOf(DateTimeOffset.MinValue);
    private static readonly long MaxMicroseconds = MicrosecondsOf(DateTimeOffset.MaxValue);

    public static SessionCursor Parse(string text)
    {
        var separator = text.IndexOf('_');
        if (separator > 0
            && long.TryParse(text[..separator], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var microseconds)
            && microseconds >= MinMicroseconds && microseconds <= MaxMicroseconds
            && EntityId.IsValid(text[(separator + 1)..], Session.IdPrefix))
            return new SessionCursor(DateTimeOffset.UnixEpoch.AddTicks(microseconds * TimeSpan.TicksPerMicrosecond), text[(separator + 1)..]);

        throw new QueryInputException("cursor", "The cursor is not valid. Send the nextCursor of the page before.");
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{MicrosecondsOf(StartedAt)}_{Id}");

    private static long MicrosecondsOf(DateTimeOffset time)
        => (time - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond;
}
