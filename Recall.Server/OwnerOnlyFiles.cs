namespace Recall.Server;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Databases;
using Microsoft.Data.Sqlite;
using Resources.Storage;
using Serilog;

public static partial class OwnerOnlyFiles
{
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const uint OwnerOnlyMask = 0b000_111_111;

    private static readonly string[] DatabaseFileSuffixes = ["", "-wal", "-shm"];

    public sealed record ModeAccess(Func<string, UnixFileMode> Get, Action<string, UnixFileMode> Set)
    {
        [UnsupportedOSPlatform("windows")]
        public static ModeAccess Real { get; } = new(File.GetUnixFileMode, File.SetUnixFileMode);
    }

    public static void ApplyUmask()
    {
        if (!OperatingSystem.IsWindows())
            _ = SetUmask(OwnerOnlyMask);
    }

    public static void TightenAtStartup(IConfiguration configuration)
    {
        if (OperatingSystem.IsWindows())
            return;

        var directories = new List<string>();
        var files = new List<string>();
        if (DatabaseFile(configuration) is { } fullPath)
        {
            directories.Add(Path.GetDirectoryName(fullPath)!);
            files.AddRange(DatabaseFileSuffixes.Select(suffix => fullPath + suffix));
        }

        if (KeysDirectory(configuration) is { } keys && Directory.Exists(keys))
        {
            directories.Add(keys);
            files.AddRange(Directory.GetFiles(keys));
        }

        if (configuration[$"{FileStoreOptions.SectionName}:{nameof(FileStoreOptions.Root)}"] is { Length: > 0 } fileRoot && Path.IsPathFullyQualified(fileRoot))
            directories.Add(fileRoot);

        var refused = Tighten(directories, files, ModeAccess.Real);
        if (refused.Count > 0)
        {
            Log.ForContext(typeof(OwnerOnlyFiles)).Warning(
                "The volume that holds {Paths} does not take owner-only modes, so other accounts on this machine may read these files. Set the owner of the volume, or use a volume that keeps modes",
                refused);
        }
    }

    public static string? KeysDirectory(IConfiguration configuration)
        => DatabaseFile(configuration) is { } file ? Path.Combine(Path.GetDirectoryName(file)!, "keys") : null;

    private static string? DatabaseFile(IConfiguration configuration)
    {
        var dataFile = new SqliteConnectionStringBuilder(configuration.GetConnectionString(DatabaseConsts.DatabaseName)).DataSource;
        return string.IsNullOrWhiteSpace(dataFile) || dataFile == ":memory:" ? null : Path.GetFullPath(dataFile);
    }

    public static IReadOnlyList<string> Tighten(IEnumerable<string> directories, IEnumerable<string> files, ModeAccess modes)
    {
        var refused = new List<string>();
        foreach (var directory in directories)
            TightenOne(new DirectoryInfo(directory), DirectoryMode, modes, refused);
        foreach (var file in files)
            TightenOne(new FileInfo(file), FileMode, modes, refused);
        return refused;
    }

    private static void TightenOne(FileSystemInfo entry, UnixFileMode wanted, ModeAccess modes, List<string> refused)
    {
        if (!entry.Exists || entry.LinkTarget is not null)
            return;

        try
        {
            if (modes.Get(entry.FullName) == wanted)
                return;

            modes.Set(entry.FullName, wanted);
            if (modes.Get(entry.FullName) != wanted)
                refused.Add(entry.FullName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            refused.Add(entry.FullName);
        }
    }

    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint SetUmask(uint mask);
}
