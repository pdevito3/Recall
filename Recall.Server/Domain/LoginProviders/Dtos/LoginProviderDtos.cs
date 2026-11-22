namespace Recall.Server.Domain.LoginProviders.Dtos;

using Resources;

/// <summary>A login provider of the table. No answer holds the client secret (IDP-4).</summary>
public sealed record LoginProviderDto
{
    public string Id { get; init; } = default!;
    public string DisplayName { get; init; } = default!;
    public string Issuer { get; init; } = default!;
    public string ClientId { get; init; } = default!;
    public bool AllowHttp { get; init; }
    public string? CaCertificatePath { get; init; }

    /// <summary><c>ReplacedByEnvironment</c>: an entry of <c>Auth:Providers</c> has its ID or its issuer, and that entry wins (IDP-3).</summary>
    [EnumNames<LoginProviderState>]
    public string State { get; init; } = default!;

    /// <summary><c>Unreadable</c>: the key ring of this server cannot read the secret, so an Admin must enter it again (IDP-5).</summary>
    [EnumNames<LoginProviderSecretState>]
    public string SecretState { get; init; } = default!;

    public DateTimeOffset CreatedAt { get; init; }
    public uint Version { get; init; }
}

/// <summary>A provider from <c>Auth:Providers</c>. The app cannot change it.</summary>
public sealed record EnvironmentLoginProviderDto
{
    public string Id { get; init; } = default!;
    public string DisplayName { get; init; } = default!;
    public string Issuer { get; init; } = default!;
}

public sealed record LoginProviderListDto
{
    public IReadOnlyList<LoginProviderDto> Items { get; init; } = [];
    public IReadOnlyList<EnvironmentLoginProviderDto> EnvironmentProviders { get; init; } = [];

    /// <summary>The callback URL to register at the provider, from the origin of this request, as <c>/auth/status</c> gives it (IDP-8).</summary>
    public string CallbackUrl { get; init; } = default!;
}

public sealed record LoginProviderForCreationDto
{
    /// <summary>The name of the provider in the sign-in URLs: letters, digits, dashes, or underscores.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.IdMaxLength))]
    public string Id { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.DisplayNameMaxLength))]
    public string DisplayName { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.IssuerMaxLength))]
    public string Issuer { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.ClientIdMaxLength))]
    public string ClientId { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.ClientSecretMaxLength))]
    public string ClientSecret { get; init; } = default!;
    public required bool AllowHttp { get; init; }
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.CaCertificatePathMaxLength))]
    public string? CaCertificatePath { get; init; }
}

public sealed record LoginProviderForUpdateDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.DisplayNameMaxLength))]
    public string DisplayName { get; init; } = default!;

    /// <summary>The issuer cannot change (IDP-6). A value other than the stored issuer gets 422, and no value keeps it.</summary>
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.IssuerMaxLength))]
    public string? Issuer { get; init; }
    [RequiredField]
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.ClientIdMaxLength))]
    public string ClientId { get; init; } = default!;

    /// <summary>A blank value keeps the stored secret (IDP-4).</summary>
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.ClientSecretMaxLength))]
    public string? ClientSecret { get; init; }
    public required bool AllowHttp { get; init; }
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.CaCertificatePathMaxLength))]
    public string? CaCertificatePath { get; init; }
    public required uint Version { get; init; }
}

/// <summary>The fields of the form that discovery needs (IDP-7).</summary>
public sealed record LoginProviderForTestDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.IssuerMaxLength))]
    public string Issuer { get; init; } = default!;
    public required bool AllowHttp { get; init; }
    [SchemaMaxLength(typeof(LoginProvider), nameof(LoginProvider.CaCertificatePathMaxLength))]
    public string? CaCertificatePath { get; init; }
}

public sealed record LoginProviderTestResultDto
{
    public bool Ok { get; init; }

    /// <summary>The issuer that the discovery document names.</summary>
    public string? Issuer { get; init; }

    public LoginProviderTestFailureDto? Failure { get; init; }
}

public sealed record LoginProviderTestFailureDto
{
    [EnumNames<LoginProviderTestFailure>]
    public string Kind { get; init; } = default!;

    /// <summary>The words that the settings page shows for the failure.</summary>
    public string Message { get; init; } = default!;
}
