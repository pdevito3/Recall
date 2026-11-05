namespace Recall.Server.Resources.Storage;

public sealed record ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    public string BucketName { get; init; } = default!;
}
