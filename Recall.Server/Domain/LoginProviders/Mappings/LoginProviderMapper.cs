namespace Recall.Server.Domain.LoginProviders.Mappings;

using Auth;
using Dtos;

public static class LoginProviderMapper
{
    public static LoginProviderDto ToDto(this LoginProvider provider, IReadOnlyList<OidcProviderOptions> environment, LoginProviderSecret secrets)
    {
        var readable = secrets.Read(provider.ProtectedClientSecret) is not null;
        return new LoginProviderDto
        {
            Id = provider.Id,
            DisplayName = provider.DisplayName,
            Issuer = provider.Issuer,
            ClientId = provider.ClientId,
            AllowHttp = provider.AllowHttp,
            CaCertificatePath = provider.CaCertificatePath,
            State = provider.StateOf(environment, readable).ToString(),
            SecretState = (readable ? LoginProviderSecretState.Readable : LoginProviderSecretState.Unreadable).ToString(),
            CreatedAt = provider.CreatedAt,
            Version = provider.Version,
        };
    }

    public static LoginProviderForm ToForm(this LoginProviderForCreationDto dto)
        => new(dto.DisplayName, dto.Issuer, dto.ClientId, dto.AllowHttp, dto.CaCertificatePath);

    public static LoginProviderForm ToForm(this LoginProviderForUpdateDto dto)
        => new(dto.DisplayName, dto.Issuer, dto.ClientId, dto.AllowHttp, dto.CaCertificatePath);
}
