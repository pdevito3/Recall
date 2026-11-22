namespace Recall.Server.Domain.LoginProviders.Dtos;

using Resources;

/// <summary>The login settings of the install (IDP-13).</summary>
public sealed record LoginSettingsDto
{
    /// <summary><c>Off</c>: the login page has no password form, and a temporary password only links a provider (IDP-15, IDP-20).</summary>
    [EnumNames<PasswordLoginState>]
    public string PasswordLogin { get; init; } = default!;

    public uint Version { get; init; }
}

public sealed record LoginSettingsForUpdateDto
{
    /// <summary>A turn to <c>Off</c> gets 409 unless each tenant has an Admin who can sign in with a provider (IDP-13).</summary>
    [RequiredField]
    [EnumNames<PasswordLoginState>]
    public string PasswordLogin { get; init; } = default!;

    public required uint Version { get; init; }
}
