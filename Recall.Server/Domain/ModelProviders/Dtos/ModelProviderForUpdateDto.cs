namespace Recall.Server.Domain.ModelProviders.Dtos;

using Resources;

public sealed record ModelProviderForUpdateDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(ModelProvider), nameof(ModelProvider.NameMaxLength))]
    public string Name { get; init; } = default!;
    [SchemaMaxLength(typeof(ModelProvider), nameof(ModelProvider.BaseUrlMaxLength))]
    public string? BaseUrl { get; init; }

    /// <summary>A blank value keeps the stored key. A new base URL needs the key again.</summary>
    [SchemaMaxLength(typeof(ModelProvider), nameof(ModelProvider.ApiKeyMaxLength))]
    public string? ApiKey { get; init; }

    /// <summary>An ID from the list that the test of this provider gives.</summary>
    [SchemaMaxLength(typeof(ModelProvider), nameof(ModelProvider.ModelMaxLength))]
    public string? Model { get; init; }

    /// <summary>True makes this one the active provider. False changes nothing.</summary>
    public bool MakeActive { get; init; }
    public required uint Version { get; init; }
}
