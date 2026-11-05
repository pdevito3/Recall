namespace Recall.Server.Auth;

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

public readonly record struct TokenHash(string Hex)
{
    public static TokenHash Of(string token) => new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));

    public override string ToString() => "[token hash]";
}

public static class Pkce
{
    public const int VerifierMinLength = 43;
    public const int VerifierMaxLength = 128;

    public static string ChallengeOf(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static bool Matches(string verifier, string challenge)
        => verifier.Length is >= VerifierMinLength and <= VerifierMaxLength
            && Ascii.IsValid(verifier)
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ChallengeOf(verifier)), Encoding.ASCII.GetBytes(challenge));
}

public static class Secrets
{
    public const int ByteLength = 32;

    public const int EncodedLength = ((ByteLength * 8) + 5) / 6;

    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

    internal static bool IsBase64Url(string value)
        => value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static bool IsExactlyOneSecret(string value)
        => value.Length == EncodedLength && IsBase64Url(value) && Base64Url.IsValid(value, out var length) && length == ByteLength;
}

public sealed record RefreshToken(string Family, string Secret)
{
    public const string Prefix = "rcl_rt_";

    public static RefreshToken New() => new(Secrets.New(), Secrets.New());

    public static bool TryParse(string? raw, [NotNullWhen(true)] out RefreshToken? token)
    {
        token = null;
        if (raw is null || !raw.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = raw[Prefix.Length..].Split('.');
        if (parts.Length != 2 || !Secrets.IsExactlyOneSecret(parts[0]) || !Secrets.IsExactlyOneSecret(parts[1]))
            return false;

        token = new RefreshToken(parts[0], parts[1]);
        return true;
    }

    public string Encode() => $"{Prefix}{Family}.{Secret}";

    public override string ToString() => "[refresh token]";
}

public readonly record struct RetryKey
{
    public const int MinLength = Secrets.EncodedLength;
    public const int MaxLength = 256;

    public string Value { get; }

    private RetryKey(string value) => Value = value;

    public static bool TryParse(string? raw, out RetryKey? key)
    {
        key = null;
        if (raw is null)
            return true;
        if (raw.Length is < MinLength or > MaxLength || !Secrets.IsBase64Url(raw))
            return false;

        key = new RetryKey(raw);
        return true;
    }

    public override string ToString() => "[retry key]";
}

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt)
{
    public const string AccessPrefix = "rcl_at_";

    public override string ToString() => "[token pair]";
}

public static class TokenDerivation
{
    private const string AccessLabel = "recall access token";
    private const string RefreshLabel = "recall refresh token";

    public static TokenPair Derive(RefreshToken presented, RetryKey key, DateTimeOffset accessExpiresAt)
        => new(
            TokenPair.AccessPrefix + Mac(presented.Secret, AccessLabel, key),
            new RefreshToken(presented.Family, Mac(presented.Secret, RefreshLabel, key)).Encode(),
            accessExpiresAt);

    public static TokenPair Random(string family, DateTimeOffset accessExpiresAt)
        => new(TokenPair.AccessPrefix + Secrets.New(), new RefreshToken(family, Secrets.New()).Encode(), accessExpiresAt);

    private static string Mac(string secret, string label, RetryKey key)
    {
        var message = Encoding.UTF8.GetBytes($"{label}\n{key.Value}");
        return Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }
}

public sealed record RefreshState(string RefreshHash, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt)
{
    public override string ToString() => "[refresh state]";
}

public enum RefreshDecision
{
    Rotate,
    RotateRetry,
    Replay,
    Expired,
    Revoked,
}

public static class RefreshRule
{
    public static RefreshDecision Classify(RefreshState row, RefreshToken token, RetryKey? retryKey, DateTimeOffset now)
    {
        if (row.RevokedAt is not null)
            return RefreshDecision.Revoked;
        if (row.ExpiresAt <= now)
            return RefreshDecision.Expired;
        if (SameHash(TokenHash.Of(token.Encode()), row.RefreshHash))
            return RefreshDecision.Rotate;
        if (retryKey is { } key
            && SameHash(TokenHash.Of(TokenDerivation.Derive(token, key, now).RefreshToken), row.RefreshHash))
            return RefreshDecision.RotateRetry;
        return RefreshDecision.Replay;
    }

    private static bool SameHash(TokenHash presented, string stored)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(presented.Hex), Encoding.ASCII.GetBytes(stored));
}

public static class SessionPolicy
{
    public static readonly TimeSpan BearerLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshIdle = TimeSpan.FromDays(90);
    public static readonly TimeSpan WebIdle = TimeSpan.FromDays(30);
    public static readonly TimeSpan WebAbsolute = TimeSpan.FromDays(180);
    public static readonly TimeSpan WebSlideEvery = TimeSpan.FromDays(1);
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AppCodeLifetime = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan KeepEndedSession = TimeSpan.FromDays(30);
}
