namespace Recall.SharedTestHelpers.Utilities;

public sealed class TemporaryFileRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"recall_test_files_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
