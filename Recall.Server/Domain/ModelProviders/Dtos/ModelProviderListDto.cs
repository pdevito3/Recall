namespace Recall.Server.Domain.ModelProviders.Dtos;

public sealed record ModelProviderListDto
{
    public IReadOnlyList<ModelProviderDto> Items { get; init; } = [];

    /// <summary><c>Changed</c>: a key is not readable, so it was saved under another key ring. <c>NotWritable</c>: the key folder takes no write, so the next start makes a new ring.</summary>
    [EnumNames<KeyRingState>]
    public string KeyRing { get; init; } = default!;
}
