namespace Recall.Server.Databases.Sqlite;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Serilog;

public static class SqliteFile
{
    private static readonly ILogger Logger = Log.ForContext(typeof(SqliteFile));

    /// <summary>
    /// Makes the directory, refuses a network mount, sets WAL, and removes a migration lock that a killed process left.
    /// EF takes that lock to protect the migrations from another process, and each process of a data file is the only
    /// one, so a lock that exists at startup is stale and the migrations would wait for it forever.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is on a network mount.</exception>
    [SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs",
        Justification = "This setup runs before the context can open the file, and it writes no audited table.")]
    public static async Task PrepareAsync(string connectionString, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (await ReadMountsAsync(cancellationToken) is { } mounts
            && NetworkMountCheck.Refusal(RealPathOf(path), mounts) is { } refusal)
            throw new InvalidOperationException(refusal);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; DROP TABLE IF EXISTS __EFMigrationsLock;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string RealPathOf(string path)
    {
        var real = Path.GetPathRoot(path)!;
        foreach (var part in path[real.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(real, part);
            real = Path.Exists(next) && new FileInfo(next).ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? RealPathOf(target.FullName)
                : next;
        }

        return real;
    }

    private static async Task<IReadOnlyList<MountEntry>?> ReadMountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return NetworkMountCheck.ParseMountInfo(
                    await File.ReadAllTextAsync("/proc/self/mountinfo", cancellationToken));

            if (OperatingSystem.IsMacOS())
            {
                using var mount = Process.Start(new ProcessStartInfo("/sbin/mount") { RedirectStandardOutput = true })!;
                var output = await mount.StandardOutput.ReadToEndAsync(cancellationToken);
                await mount.WaitForExitAsync(cancellationToken);
                return NetworkMountCheck.ParseMountOutput(output);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Logger.Warning(exception, "Could not read the mount table, so the network mount check of the data file did not run");
        }

        return null;
    }
}
