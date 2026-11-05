namespace Recall.Server.Databases.Sqlite;

public sealed record MountEntry(string MountPoint, string FileSystemType, string Source);

/// <summary>
/// SQLite locks a WAL file through shared memory, which works only on one machine. So the data file must not be on a
/// network file system, where two machines can open it and the file can corrupt. A FUSE file system, a union of disks
/// such as Unraid <c>/mnt/user</c>, and a shared folder of a hypervisor are also refused, because WAL on them has no proof.
/// </summary>
public static class NetworkMountCheck
{
    private static readonly HashSet<string> RefusedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "9p", "afs", "afpfs", "beegfs", "ceph", "cifs", "coda", "davfs", "fuse", "glusterfs", "gpfs", "lustre", "ncpfs",
        "nfs", "nfs3", "nfs4", "ocfs2", "shfs", "smb3", "smbfs", "vboxsf", "virtiofs", "webdav",
    };

    public static string? Refusal(string dataFilePath, IEnumerable<MountEntry> mounts)
    {
        var mount = mounts
            .Where(m => IsUnder(dataFilePath, m.MountPoint))
            .OrderBy(m => m.MountPoint.TrimEnd('/').Length)
            .LastOrDefault();

        return mount is not null && IsRefused(mount.FileSystemType)
            ? $"The data file {dataFilePath} is on the mount {mount.MountPoint} ({mount.FileSystemType} from {mount.Source}). " +
              "SQLite WAL is safe only on a local disk, and this type is a network, FUSE, or shared folder file system. " +
              "Move the data directory to a local disk. On Unraid, use a path such as /mnt/cache, not /mnt/user."
            : null;
    }

    /// <summary><c>fuseblk</c> has no dot, so it passes: it is a local block device such as NTFS on a USB disk.</summary>
    private static bool IsRefused(string fileSystemType)
        => RefusedTypes.Contains(fileSystemType) || fileSystemType.StartsWith("fuse.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the text of <c>/proc/self/mountinfo</c> of Linux, whose lines are
    /// <c>36 35 98:0 /root /mount/point rw,noatime master:1 - ext4 /dev/sda1 rw</c>.
    /// </summary>
    public static IReadOnlyList<MountEntry> ParseMountInfo(string text)
    {
        var entries = new List<MountEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (separator < 5 || fields.Length < separator + 3)
                continue;

            var superOptions = fields.Length > separator + 3 ? fields[separator + 3] : "";
            entries.Add(new MountEntry(
                Unescape(fields[4]), fields[separator + 1], WithServer(Unescape(fields[separator + 2]), superOptions)));
        }

        return entries;
    }

    /// <summary>
    /// Reads the output of <c>mount</c> on macOS, whose lines are <c>//me@nas/share on /Volumes/share (smbfs, nodev)</c>.
    /// </summary>
    public static IReadOnlyList<MountEntry> ParseMountOutput(string text)
    {
        const string on = " on ";
        var entries = new List<MountEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var options = line.LastIndexOf(" (", StringComparison.Ordinal);
            var point = line.IndexOf(on, StringComparison.Ordinal);
            if (options < 0 || point < 0 || point + on.Length > options)
                continue;

            var type = line[(options + 2)..].TrimEnd(')').Split(',')[0].Trim();
            entries.Add(new MountEntry(line[(point + on.Length)..options], type, line[..point]));
        }

        return entries;
    }

    /// <summary>
    /// The Docker volume driver mounts NFS with a source such as <c>:/export</c>, and only <c>addr=</c> of the super
    /// options names the server.
    /// </summary>
    private static string WithServer(string source, string superOptions)
    {
        const string key = "addr=";
        var server = superOptions.Split(',').FirstOrDefault(option => option.StartsWith(key, StringComparison.Ordinal));
        return source.StartsWith(':') && server is not null ? server[key.Length..] + source : source;
    }

    private static bool IsUnder(string path, string mountPoint)
    {
        var root = mountPoint.TrimEnd('/');
        return root.Length == 0
            || path.Equals(root, StringComparison.Ordinal)
            || path.StartsWith(root + "/", StringComparison.Ordinal);
    }

    // The kernel writes a space, a tab, a newline, and a backslash of a path as an octal escape such as \040.
    private static string Unescape(string field)
    {
        var result = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length
                && field.AsSpan(i + 1, 3) is var octal && octal.IndexOfAnyExceptInRange('0', '7') < 0)
            {
                result.Append((char)Convert.ToInt32(octal.ToString(), 8));
                i += 3;
            }
            else
                result.Append(field[i]);
        }

        return result.ToString();
    }
}
