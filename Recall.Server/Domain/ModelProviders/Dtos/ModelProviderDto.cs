namespace Recall.Server.Domain.ModelProviders.Dtos;

/// <summary>A model provider. No answer holds the key.</summary>
public sealed record ModelProviderDto
{
    public string Id { get; init; } = default!;
    [EnumNames<ModelProviderKind>]
    public string Kind { get; init; } = default!;
    public string Name { get; init; } = default!;
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public bool IsActive { get; init; }

    /// <summary><c>Unreadable</c> means that the key ring of this server cannot read the key, so the Admin must enter it again.</summary>
    [EnumNames<ModelProviderKeyState>]
    public string KeyState { get; init; } = default!;
    public uint Version { get; init; }
}
