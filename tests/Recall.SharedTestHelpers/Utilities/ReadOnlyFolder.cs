namespace Recall.SharedTestHelpers.Utilities;

/// <summary>Takes the write permission of a folder away until it is disposed. A root user does not feel it.</summary>
public sealed class ReadOnlyFolder : IDisposable
{
    private readonly string _path;

    public ReadOnlyFolder(string path)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        _path = path;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
