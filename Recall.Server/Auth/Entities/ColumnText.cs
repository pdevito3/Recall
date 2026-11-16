namespace Recall.Server.Auth.Entities;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public static class SessionKindText
{
    public const string Web = "web";
    public const string Native = "native";
    public const string Pat = "pat";
    public const int MaxLength = 10;

    public static IReadOnlyList<string> All { get; } = [Web, Native, Pat];

    public static ValueConverter<SessionKind, string> Converter { get; } = new(
        kind => kind == SessionKind.Web ? Web : kind == SessionKind.Pat ? Pat : Native,
        text => text == Web ? SessionKind.Web : text == Pat ? SessionKind.Pat : SessionKind.Native);

    public static string Of(SessionKind kind) => kind switch
    {
        SessionKind.Web => Web,
        SessionKind.Pat => Pat,
        _ => Native,
    };
}

public static class RevokeReasonText
{
    public const string Logout = "logout";
    public const string RefreshReplay = "refresh_replay";
    public const string RevokeOthers = "revoke_others";
    public const string TokenRevoked = "token_revoked";
    public const int MaxLength = 20;

    public static IReadOnlyList<string> All { get; } = [Logout, RefreshReplay, RevokeOthers, TokenRevoked];

    public static ValueConverter<RevokeReason, string> Converter { get; } = new(
        reason => reason == RevokeReason.Logout ? Logout
            : reason == RevokeReason.RefreshReplay ? RefreshReplay
            : reason == RevokeReason.TokenRevoked ? TokenRevoked
            : RevokeOthers,
        text => text == Logout ? RevokeReason.Logout
            : text == RefreshReplay ? RevokeReason.RefreshReplay
            : text == TokenRevoked ? RevokeReason.TokenRevoked
            : RevokeReason.RevokeOthers);
}
