namespace Recall.Server.Resources.Storage;

using System.Runtime.InteropServices;
using System.Text;
using Databases.Sqlite;
using Exceptions;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using Serilog;

public sealed record FileStorePreparation(int RemovedItems, string? NotWritableCause);

/// <summary>
/// Keeps files on the local disk, under one root folder, which must be one file system. A reader of a key sees a whole
/// file or no file only when the rename is atomic, so a mount below the root, where a rename becomes a copy, can show a
/// part of a file. A write that returned stays after a power failure.
/// </summary>
public sealed class FileStore(IOptions<FileStoreOptions> options)
{
    private const FileShare ReadersDoNotBlockARename = FileShare.ReadWrite | FileShare.Delete;

    private static ILogger Logger => Log.ForContext<FileStore>();

    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Root));

    private bool _lastProofFailed;

    private string TemporaryFolder => Path.Combine(_root, FileKey.TemporaryFolder);

    public string? MountRefusal(IEnumerable<MountEntry> mounts)
    {
        try
        {
            return NetworkMountCheck.FileRootRefusal(NetworkMountCheck.RealPathOf(_root), mounts);
        }
        catch (IOException)
        {
            return NetworkMountCheck.FileRootRefusal(_root, mounts);
        }
    }

    /// <summary>
    /// Removes what an interrupted process left in the temporary folder, makes the root, and proves that it takes a
    /// write. Call it only at startup, because it also removes the temporary file of a write that runs. A root that cannot
    /// take a write does not throw, because the rest of the app can still serve reads.
    /// </summary>
    public FileStorePreparation Prepare()
    {
        var (removed, removeFailure) = TryRemoveLeftovers();
        var notWritableCause = ProveWritable();
        if (removeFailure is not null && notWritableCause is null)
            Logger.Here().Warning(removeFailure, "Could not remove the temporary items of interrupted file store work in {Root}", _root);

        return new(removed, notWritableCause);
    }

    /// <summary>
    /// Writes a temporary file, renames it into the root, and deletes it, as a real write does. It can run at any time.
    /// It logs one error when the root stops taking writes, and one message when it takes them again.
    /// </summary>
    /// <returns>The reason that the root cannot take a write, or null when it can.</returns>
    public string? ProveWritable()
    {
        var cause = WriteProofFile();
        var lastProofFailed = Interlocked.Exchange(ref _lastProofFailed, cause is not null);
        if (cause is not null && !lastProofFailed)
            Logger.Here().Error("The file store root {Root} cannot take a write, so a new file fails until this is corrected. The cause: {Cause}", _root, cause);
        else if (cause is null && lastProofFailed)
            Logger.Here().Information("The file store root {Root} takes writes again", _root);

        return cause;
    }

    /// <summary>Writes all of the stream to the key, in place of a file that the key has.</summary>
    public async Task WriteAsync(FileKey key, Stream content, CancellationToken cancellationToken)
    {
        var path = ResolveWithNoSymlink(key);
        var temporary = NewTemporaryPath();
        try
        {
            await using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous,
                BufferSize = 0,
            }))
            {
                await content.CopyToAsync(file, cancellationToken);
                file.Flush(flushToDisk: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporary);
            throw;
        }

        // A new folder needs the flush of its parent too, so each folder up to the root gets one.
        var folder = path;
        foreach (var _ in key.Segments)
        {
            folder = Path.GetDirectoryName(folder)!;
            FlushFolder(folder);
        }
    }

    /// <summary>Opens the file of the key. The caller disposes the stream.</summary>
    /// <exception cref="NotFoundException">The key has no file.</exception>
    public Stream OpenRead(FileKey key)
    {
        var path = ResolveWithNoSymlink(key);
        if (Directory.Exists(path))
            throw NoFile();

        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, ReadersDoNotBlockARename,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
        {
            throw NoFile(ex);
        }
    }

    /// <summary>Deletes the file of the key. A second delete is a no-op.</summary>
    public void Delete(FileKey key)
    {
        var path = ResolveWithNoSymlink(key);
        if (!File.Exists(path))
            return;

        File.Delete(path);
        FlushFolder(Path.GetDirectoryName(path)!);
    }

    /// <summary>Deletes the folder of the prefix and all that is in it. A second delete is a no-op.</summary>
    public void DeleteUnder(FileKey prefix)
    {
        var path = ResolveWithNoSymlink(prefix);
        if (!Directory.Exists(path))
            return;

        var moved = RemoveAllKeysAtOneTime(path);
        Directory.Delete(moved, recursive: true);
    }

    private (int Removed, Exception? Failure) TryRemoveLeftovers()
    {
        try
        {
            // Directory.Exists follows a link, and the entries of its target are not temporary items.
            if (new FileInfo(TemporaryFolder).LinkTarget is not null)
            {
                File.Delete(TemporaryFolder);
                return (1, null);
            }

            if (!Directory.Exists(TemporaryFolder))
                return (0, null);

            var entries = Directory.GetFileSystemEntries(TemporaryFolder).Length;
            Directory.Delete(TemporaryFolder, recursive: true);
            return (entries, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, ex);
        }
    }

    private string? WriteProofFile()
    {
        string? temporary = null;
        try
        {
            var rootIsNew = !Directory.Exists(_root);
            temporary = NewTemporaryPath();
            if (rootIsNew)
                FlushFolder(Path.GetDirectoryName(_root)!);

            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                file.WriteByte(0);
                file.Flush(flushToDisk: true);
            }

            var proof = Path.Combine(_root, FileKey.ReservedProofFileName);
            File.Move(temporary, proof, overwrite: true);
            File.Delete(proof);
            FlushFolder(_root);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (temporary is not null)
                TryDeleteTemporaryFile(temporary);

            return ex.Message;
        }
    }

    private string ResolveWithNoSymlink(FileKey key)
    {
        var path = _root;
        foreach (var segment in key.Segments)
        {
            path = Path.Combine(path, segment);
            RefuseSymlink(path);
        }

        return path;
    }

    private string RemoveAllKeysAtOneTime(string folder)
    {
        var moved = NewTemporaryPath();
        Directory.Move(folder, moved);
        FlushFolder(Path.GetDirectoryName(folder)!);
        return moved;
    }

    private string NewTemporaryPath()
    {
        Directory.CreateDirectory(TemporaryFolder);
        RefuseSymlink(TemporaryFolder);
        return Path.Combine(TemporaryFolder, Guid.NewGuid().ToString("N"));
    }

    private static void RefuseSymlink(string path)
    {
        if (new FileInfo(path).LinkTarget is { } target)
            throw new IOException($"The file store refuses the path {path}, because it is a symlink to {target}.");
    }

    private static NotFoundException NoFile(Exception? inner = null)
        => inner is null ? new("The file was not found.") : new("The file was not found.", inner);

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Here().Warning(ex, "Could not delete the temporary file {Path}", path);
        }
    }

    // .NET does not open a folder as a file, so it needs the call to open(2). Windows has no flush of a folder.
    private static void FlushFolder(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        var descriptor = Open(Encoding.UTF8.GetBytes(path + '\0'), flags: 0);
        if (descriptor < 0)
            throw new IOException($"The file store cannot open the folder {path}: {Marshal.GetLastPInvokeErrorMessage()}.");

        using var folder = new SafeFileHandle(descriptor, ownsHandle: true);
        RandomAccess.FlushToDisk(folder);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte[] nullTerminatedPath, int flags);
}
