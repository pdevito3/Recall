namespace Recall.Server.Databases.Sqlite;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

public static class SqliteFile
{
    /// <summary>
    /// Refuses a network mount, makes the directory, refuses a directory that cannot take a write, sets WAL, and removes a
    /// migration lock that a killed process left. EF takes that lock to protect the migrations from another process, and
    /// each process of a data file is the only one, so a lock that exists at startup is stale and the migrations would
    /// wait for it forever.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is on a network mount, or its directory is not writable.</exception>
    public static Task PrepareAsync(string connectionString, CancellationToken cancellationToken)
        => PrepareAsync(connectionString, NetworkMountCheck.ReadMountsAsync, cancellationToken);

    [SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs",
        Justification = "This setup runs before the context can open the file, and it writes no audited table.")]
    internal static async Task PrepareAsync(string connectionString, ReadMounts readMounts, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);

        if (await readMounts(cancellationToken) is { } mounts
            && NetworkMountCheck.Refusal(NetworkMountCheck.RealPathOf(path), mounts) is { } refusal)
            throw new InvalidOperationException(refusal);

        MakeWritableDirectory(Path.GetDirectoryName(path)!);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; DROP TABLE IF EXISTS __EFMigrationsLock;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void MakeWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using var proof = new FileStream(Path.Combine(directory, $".write-proof-{Guid.NewGuid():N}"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The data directory {directory} is not writable, so the database cannot open. " +
                $"Mount it read-write, and give the user of this process write access. The cause: {exception.Message}",
                exception);
        }
    }
}
