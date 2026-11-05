namespace Recall.Server.Databases.Sqlite;

public sealed record MountEntry(string MountPoint, string FileSystemType, string Source);

/// <summary>
/// SQLite locks a WAL file through shared memory, which works only on one machine. So the data file must not be on a
/// network file system, where two machines can open it and the file can corrupt.
/// </summary>
public static class NetworkMountCheck
{
    private static readonly HashSet<string> NetworkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "9p", "afs", "afpfs", "beegfs", "ceph", "cifs", "coda", "davfs", "fuse.ceph", "fuse.ceph-fuse", "fuse.davfs2",
        "fuse.gcsfuse", "fuse.glusterfs", "fuse.juicefs", "fuse.mfs", "fuse.rclone", "fuse.s3fs", "fuse.sshfs",
        "glusterfs", "gpfs", "lustre", "ncpfs", "nfs", "nfs3", "nfs4", "ocfs2", "smb3", "smbfs", "webdav",
    };

    public static string? Refusal(string dataFilePath, IEnumerable<MountEntry> mounts)
    {
        var mount = mounts
            .Where(m => IsUnder(dataFilePath, m.MountPoint))
            .OrderBy(m => m.MountPoint.TrimEnd('/').Length)
            .LastOrDefault();

        return mount is not null && NetworkTypes.Contains(mount.FileSystemType)
            ? $"The data file {dataFilePath} is on the network mount {mount.MountPoint} ({mount.FileSystemType} from {mount.Source}). " +
              "SQLite needs a file system of this machine. Move the data directory to a local volume."
            : null;
    }

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

            entries.Add(new MountEntry(
                Unescape(fields[4]), fields[separator + 1], Unescape(fields[separator + 2])));
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
