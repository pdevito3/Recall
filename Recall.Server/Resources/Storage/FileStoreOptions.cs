namespace Recall.Server.Resources.Storage;

public sealed record FileStoreOptions
{
    public const string SectionName = "FileStore";

    public string Root { get; init; } = default!;
}
