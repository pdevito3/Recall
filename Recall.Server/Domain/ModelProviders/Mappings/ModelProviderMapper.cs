namespace Recall.Server.Domain.ModelProviders.Mappings;

using Dtos;

public static class ModelProviderMapper
{
    public static ModelProviderDto ToDto(this ModelProvider provider, ApiKeyProtector keys, bool isActive) => new()
    {
        Id = provider.Id,
        Kind = provider.Kind.ToString(),
        Name = provider.Name,
        BaseUrl = provider.BaseUrl,
        Model = provider.Model,
        IsActive = isActive,
        KeyState = ApiKeyProtector.StateOf(keys.Read(provider.ProtectedApiKey)).ToString(),
        Version = provider.Version,
    };
}
