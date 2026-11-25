namespace Recall.Server.Resources.Entrypoints.Mcp;

public sealed record McpList<T>(IReadOnlyList<T> Items);

public static class McpList
{
    public static async Task<McpList<T>> From<T>(Task<IReadOnlyList<T>> list) => new(await list);
}
