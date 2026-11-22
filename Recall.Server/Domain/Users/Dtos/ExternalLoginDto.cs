namespace Recall.Server.Domain.Users.Dtos;

using Auth.Entities;
using Resources;

/// <summary>A link of the user to an identity at a provider. A person signs in with it at that provider.</summary>
public sealed record ExternalLoginDto
{
    public string Issuer { get; init; } = default!;

    /// <summary>The subject that the provider gave the identity. A removal names it with the issuer.</summary>
    public string Subject { get; init; } = default!;

    /// <summary>The email, else the name, that the provider gave at the link. Null for a link from before Recall kept it.</summary>
    public string? DisplayLabel { get; init; }

    /// <summary>The ID of the sign-in method with this issuer, or null when no provider on the login page has it, so the link signs no one in.</summary>
    public string? ProviderId { get; init; }

    /// <summary>The name of that provider on the login page, or null.</summary>
    public string? ProviderName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastLoginAt { get; init; }
}

/// <summary>The link to remove, by its issuer and subject (IDP-12).</summary>
public sealed record ExternalLoginForRemovalDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(ExternalLogin), nameof(ExternalLogin.IssuerMaxLength))]
    public string Issuer { get; init; } = default!;

    [RequiredField]
    [SchemaMaxLength(typeof(ExternalLogin), nameof(ExternalLogin.SubjectMaxLength))]
    public string Subject { get; init; } = default!;
}
