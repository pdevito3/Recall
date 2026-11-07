namespace Recall.Server.Domain.ModelProviders.Dtos;

using Clients;

/// <summary>The answer of the provider to a request for its list of models.</summary>
public sealed record ModelProviderTestResultDto
{
    public bool Ok { get; init; }
    public IReadOnlyList<ProviderModelDto> Models { get; init; } = [];
    public ModelProviderFailureDto? Failure { get; init; }
}

public sealed record ProviderModelDto
{
    public string Id { get; init; } = default!;
    public string DisplayName { get; init; } = default!;
}

public sealed record ModelProviderFailureDto
{
    [EnumNames<ProviderFailureKind>]
    public string Kind { get; init; } = default!;
    public int? StatusCode { get; init; }

    /// <summary>The words of the provider, with the key removed.</summary>
    public string Message { get; init; } = default!;
}
