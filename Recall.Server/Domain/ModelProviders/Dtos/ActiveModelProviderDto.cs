namespace Recall.Server.Domain.ModelProviders.Dtos;

public sealed record ActiveModelProviderDto
{
    /// <summary>The name of the model provider that gets the words of the next Cleanup. Null when no Cleanup can start yet.</summary>
    public string? Name { get; init; }
}
