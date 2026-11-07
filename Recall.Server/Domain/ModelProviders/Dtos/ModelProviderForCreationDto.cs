namespace Recall.Server.Domain.ModelProviders.Dtos;

using Resources;

public sealed record ModelProviderForCreationDto
{
    [RequiredField]
    [EnumNames<ModelProviderKind>]
    public string Kind { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(ModelProvider), nameof(ModelProvider.NameMaxLength))]
    public string Name { get; init; } = default!;

    /// <summary>The address that comes before <c>/models</c>, for example <c>http://localhost:11434/v1</c>. It is required for <c>OpenAiCompatible</c>.</summary>
    [SchemaMaxLength(typeof(ModelProvider), nameof(ModelProvider.BaseUrlMaxLength))]
    public string? BaseUrl { get; init; }

    /// <summary>It is required for <c>Anthropic</c>. A local <c>OpenAiCompatible</c> endpoint can work with no key.</summary>
    [SchemaMaxLength(typeof(ModelProvider), nameof(ModelProvider.ApiKeyMaxLength))]
    public string? ApiKey { get; init; }
}
