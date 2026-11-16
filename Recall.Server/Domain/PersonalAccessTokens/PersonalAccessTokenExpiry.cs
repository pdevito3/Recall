namespace Recall.Server.Domain.PersonalAccessTokens;

using Auth;
using Exceptions;

public static class PersonalAccessTokenExpiry
{
    public const string Field = "expiresAt";
    public const string Message = "Pick an expiry date in the future, or pick Never.";

    /// <summary>
    /// The expiry of a new token, or null for a token that never expires. A body must pick one of the two, so a client
    /// that forgets the field does not get a token that never expires.
    /// </summary>
    /// <exception cref="ValidationException">
    /// The body sends both, neither, a date that is not in the future, or a date at or after the stored "never", which
    /// would read back as never.
    /// </exception>
    public static DateTimeOffset? Of(DateTimeOffset? expiresAt, bool neverExpires, DateTimeOffset now) => (expiresAt, neverExpires) switch
    {
        (null, true) => null,
        ({ } at, false) when at > now && at < SessionPolicy.PersonalAccessTokenNever => at,
        _ => throw new ValidationException(Field, Message),
    };
}
