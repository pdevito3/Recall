namespace Recall.Server.Auth.Entities;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>The text of <see cref="SessionKind"/> in a column. The check constraints of the tables name these values.</summary>
public static class SessionKindText
{
    public const string Web = "web";
    public const string Native = "native";
    public const int MaxLength = 10;

    public static IReadOnlyList<string> All { get; } = [Web, Native];

    public static ValueConverter<SessionKind, string> Converter { get; } = new(
        kind => kind == SessionKind.Web ? Web : Native,
        text => text == Web ? SessionKind.Web : SessionKind.Native);

    public static string Of(SessionKind kind) => kind == SessionKind.Web ? Web : Native;
}

/// <summary>The text of <see cref="RevokeReason"/> in a column.</summary>
public static class RevokeReasonText
{
    public const string Logout = "logout";
    public const string RefreshReplay = "refresh_replay";
    public const string RevokeOthers = "revoke_others";
    public const int MaxLength = 20;

    public static IReadOnlyList<string> All { get; } = [Logout, RefreshReplay, RevokeOthers];

    public static ValueConverter<RevokeReason, string> Converter { get; } = new(
        reason => reason == RevokeReason.Logout ? Logout : reason == RevokeReason.RefreshReplay ? RefreshReplay : RevokeOthers,
        text => text == Logout ? RevokeReason.Logout : text == RefreshReplay ? RevokeReason.RefreshReplay : RevokeReason.RevokeOthers);
}
