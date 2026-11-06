namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;

public abstract record LoginClient
{
    public sealed record Web(string ReturnPath) : LoginClient;

    public sealed record Native(NativeRedirect Redirect, string Challenge, string ClientState) : LoginClient
    {
        public override string ToString() => "[native client]";
    }

    public static bool TryParseStart(IQueryCollection query, [NotNullWhen(true)] out LoginClient? client)
    {
        client = null;
        switch (OneValue(query, "client"))
        {
            case "web":
                client = new Web(LocalReturnPath.Of(OneValue(query, "returnUrl")));
                return true;
            case "app":
                if (!NativeRedirect.TryParse(OneValue(query, "redirect_uri"), out var redirect)
                    || OneValue(query, "code_challenge_method") != "S256"
                    || OneValue(query, "code_challenge") is not { } challenge
                    || !Secrets.IsExactlyOneSecret(challenge)
                    || OneValue(query, "state") is not { } state
                    || !IsClientState(state))
                    return false;
                client = new Native(redirect, challenge, state);
                return true;
            default:
                return false;
        }
    }

    public static LoginClient FromAttempt(LoginAttempt attempt)
    {
        if (attempt.ClientKind == SessionKind.Web)
            return new Web(attempt.ReturnPath ?? "/");

        if (!NativeRedirect.TryParse(attempt.NativeRedirect, out var redirect))
            throw new InvalidOperationException("A native login attempt has no valid redirect.");
        return new Native(redirect, attempt.NativeChallenge!, attempt.NativeState!);
    }

    private static string? OneValue(IQueryCollection query, string name)
        => query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;

    // RFC 6749 appendix A.5: VSCHAR. The same rule keeps a NUL or a lone surrogate from the database.
    private static bool IsClientState(string state)
        => state.Length is > 0 and <= LoginAttempt.NativeStateMaxLength && state.All(c => c is >= '\x20' and <= '\x7E');
}

/// <summary>
/// The redirect of a native app. It is exactly <c>recall://auth/callback</c>, or an http loopback address of RFC 8252
/// section 7.3 with a port. It has no user info, query, or fragment.
/// </summary>
public sealed partial record NativeRedirect
{
    public const string AppCallback = "recall://auth/callback";

    private NativeRedirect(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? raw, [NotNullWhen(true)] out NativeRedirect? redirect)
    {
        redirect = raw is { Length: <= LoginAttempt.NativeRedirectMaxLength } && (raw == AppCallback || IsLoopback(raw))
            ? new NativeRedirect(raw)
            : null;
        return redirect is not null;
    }

    public string WithQuery(params (string Name, string Value)[] values)
        => Value + "?" + string.Join('&', values.Select(pair => $"{pair.Name}={Uri.EscapeDataString(pair.Value)}"));

    private static bool IsLoopback(string raw)
    {
        var match = LoopbackPattern().Match(raw);
        return match.Success && int.Parse(match.Groups["port"].ValueSpan, CultureInfo.InvariantCulture) <= ushort.MaxValue;
    }

    [GeneratedRegex(@"\Ahttp://(?:127\.0\.0\.1|\[::1\]):(?<port>[1-9][0-9]{0,4})/[A-Za-z0-9._~/-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex LoopbackPattern();
}
