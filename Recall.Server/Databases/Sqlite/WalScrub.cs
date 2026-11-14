namespace Recall.Server.Databases.Sqlite;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Resources;
using Serilog;

/// <summary>
/// The truncate of the WAL after a delete. A truncate that a reader stops is owed, and this service tries it again each
/// <see cref="RetryInterval"/> until it passes. The debt lives in memory, because each start of the server truncates
/// before it takes a request.
/// </summary>
public sealed class WalScrub(TimeProvider timeProvider) : BackgroundService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private const string ShortWait = "PRAGMA busy_timeout = 250;";
    private const string Truncate = "PRAGMA wal_checkpoint(TRUNCATE);";
    private const int BusyColumn = 0;

    private readonly ILogger _logger = Log.ForContext<WalScrub>();
    private readonly SemaphoreSlim _owed = new(0, 1);
    private volatile string? _owedConnectionString;

    /// <summary>The delete has committed, so this takes no token. False when a reader stopped the truncate, which is then owed.</summary>
    public async Task<bool> TruncateAsync(string connectionString)
    {
        var truncated = await TryTruncateAsync(connectionString);
        if (!truncated)
            Owe(connectionString);
        return truncated;
    }

    /// <summary>A client that goes away must not leave the words on the disk. It goes through the locks of SQLite, so another process that holds the WAL makes it say false.</summary>
    [SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs",
        Justification = "A checkpoint is outside each transaction of the context, so it uses a connection of its own. The SQL is a constant.")]
    public static async Task<bool> TryTruncateAsync(string connectionString)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var wait = connection.CreateCommand();
            wait.CommandText = ShortWait;
            await wait.ExecuteNonQueryAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = Truncate;
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() && reader.GetInt32(BusyColumn) == 0;
        }
        catch (SqliteException exception)
        {
            Log.ForContext<WalScrub>().Warning(exception, "Could not truncate the WAL");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            await _owed.WaitAsync(stoppingToken);
            var tries = 0;
            do
            {
                await Task.Delay(RetryInterval, timeProvider, stoppingToken);
                tries++;
            }
            while (!await RetryAsync(_owedConnectionString!));

            _logger.Here().Information("Truncated the WAL that a reader held, after {Tries} tries", tries);
        }
    }

    // A fault of a retry must not stop the host, so it counts as a truncate that did not pass.
    private async Task<bool> RetryAsync(string connectionString)
    {
        try
        {
            return await TryTruncateAsync(connectionString);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Here().Warning("A retry of the WAL truncate failed ({FaultType})", exception.GetType().FullName);
            return false;
        }
    }

    private void Owe(string connectionString)
    {
        _owedConnectionString = connectionString;
        try
        {
            _owed.Release();
        }
        catch (SemaphoreFullException)
        {
            // The truncate is owed already, and one retry covers each delete before it.
        }
    }
}
