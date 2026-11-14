namespace Recall.IntegrationTests.Databases;

using System.Collections.Concurrent;
using System.Globalization;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using ILogger = Microsoft.Extensions.Logging.ILogger;

public sealed class MigrationHostedServiceTests : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Test]
    public async Task startup_migration_on_a_missing_database_creates_it_and_logs_no_error()
    {
        // Arrange
        var logs = new ErrorLogs();
        await using var services = TestFixture.BuildServiceProvider(
            s => s.AddLogging(logging => logging.AddProvider(logs)), _database.ConnectionString);
        var migrator = new MigrationHostedService<AppDbContext>(
            services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IConfiguration>(), services.GetRequiredService<WalScrub>());

        // Act
        await migrator.StartAsync(CancellationToken.None);

        // Assert
        logs.Errors.ShouldBeEmpty();
        await using var scope = services.CreateAsyncScope();
        var pending = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetPendingMigrationsAsync();
        pending.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-27")]
    [NotInParallel(nameof(MigrationHostedServiceTests))]
    public async Task a_startup_scrub_that_a_reader_blocks_warns_and_names_no_word()
    {
        // Arrange
        const string word = "marker-word";
        await using var services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);
        await Migrator(services, Logger.None).StartAsync(CancellationToken.None);
        await using var writer = await OpenAsync();
        await RunAsync(writer, "CREATE TABLE words (text TEXT NOT NULL);");
        await using var reader = await OpenAsync();
        await RunAsync(reader, "BEGIN; SELECT count(*) FROM words;");
        await RunAsync(writer, $"INSERT INTO words VALUES ('{word}'); DELETE FROM words;");
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        // Act
        await Migrator(services, logger).StartAsync(CancellationToken.None);

        // Assert
        var warning = sink.OfTheMigrator(LogEventLevel.Warning).ShouldHaveSingleItem();
        warning.RenderMessage(CultureInfo.InvariantCulture).ShouldContain("deleted words can stay in the WAL");
        sink.OfTheMigrator(LogEventLevel.Information).ShouldNotBeEmpty();
        sink.Events.ShouldAllBe(e => !e.RenderMessage(CultureInfo.InvariantCulture).Contains(word));
    }

    [Test]
    [Property("Rule", "SES-27")]
    [NotInParallel(nameof(MigrationHostedServiceTests))]
    public async Task a_startup_scrub_with_no_reader_gives_no_warning()
    {
        // Arrange
        await using var services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        // Act
        await Migrator(services, logger).StartAsync(CancellationToken.None);

        // Assert
        sink.OfTheMigrator(LogEventLevel.Information).ShouldNotBeEmpty();
        sink.OfTheMigrator(LogEventLevel.Warning).ShouldBeEmpty();
    }

    // The migrator takes its logger from Log.Logger when it is built, so only that moment needs the swap.
    private static MigrationHostedService<AppDbContext> Migrator(ServiceProvider services, Serilog.ILogger logger)
    {
        var main = Log.Logger;
        Log.Logger = logger;
        try
        {
            return new MigrationHostedService<AppDbContext>(
                services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IConfiguration>(), services.GetRequiredService<WalScrub>());
        }
        finally
        {
            Log.Logger = main;
        }
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task RunAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

        public List<LogEvent> OfTheMigrator(LogEventLevel level) =>
            [.. _events.Where(e => e.Level == level && e.Properties[Constants.SourceContextPropertyName].ToString().Contains("MigrationHostedService"))];
    }

    private sealed class ErrorLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _errors);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    errors.Enqueue($"{category} {eventId.Name}: {formatter(state, exception)}");
            }
        }
    }
}
