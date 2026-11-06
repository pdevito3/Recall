namespace Recall.FunctionalTests.Auth;

using System.Net;
using Microsoft.Data.Sqlite;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public partial class NoProviderBootTests
{
    private const string JobsStopped = "The background jobs stopped";

    [Test]
    [Arguments("DROP TABLE backwave_jobs")]
    [Arguments("DROP TABLE backwave_jobs; CREATE VIEW backwave_jobs AS SELECT 1 AS job_id; DELETE FROM backwave_schema_version")]
    public async Task a_fault_in_the_job_store_at_the_boot_leaves_the_journal_up_and_health_says_so(string fault)
    {
        // Arrange
        await using var first = await ServerProcess.StartAsync();
        await WaitUntilTheJobTablesExistAsync(first.Database);
        var root = first.Root;
        await first.StopAsync();
        await RunOnDatabaseAsync(first.Database, fault);

        // Act
        await using var server = await ServerProcess.StartAsync(root);
        using var health = await HealthThatStoppedAsync(server.Client);
        using var journal = await server.Client.GetAsync(AuthRoutes.Status);
        await WaitForOutputLineAsync(server, JobsStopped);

        // Assert
        health.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await health.Content.ReadAsStringAsync()).ShouldContain(JobsStopped);
        journal.StatusCode.ShouldBe(HttpStatusCode.OK);
        server.HasExited.ShouldBeFalse();
        var fatal = server.Output.Split('\n').Where(line => line.Contains(JobsStopped, StringComparison.Ordinal) && line.Contains("\"@l\":\"Fatal\"", StringComparison.Ordinal)).ToList();
        fatal.Count.ShouldBe(1, server.Output);
    }

    [Test]
    [Arguments("UPDATE backwave_schema_version SET version = 1")]
    [Arguments("UPDATE backwave_schema_version SET version = 99")]
    [Arguments("UPDATE backwave_schema_version SET version = 'not a version'")]
    public async Task a_version_that_is_not_the_version_of_the_adapter_is_no_fault(string skew)
    {
        // Arrange
        await using var first = await ServerProcess.StartAsync();
        await WaitUntilTheJobTablesExistAsync(first.Database);
        var root = first.Root;
        await first.StopAsync();
        await RunOnDatabaseAsync(first.Database, skew);

        // Act
        await using var server = await ServerProcess.StartAsync(root);
        await Task.Delay(TimeSpan.FromSeconds(3));
        using var health = await server.Client.GetAsync("/health");

        // Assert
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        server.Output.ShouldNotContain(JobsStopped);
    }

    [Test]
    public async Task the_steps_in_the_troubleshooting_doc_make_a_lost_job_table_again_and_keep_the_journal()
    {
        // Arrange
        await using var first = await ServerProcess.StartAsync();
        await WaitUntilTheJobTablesExistAsync(first.Database);
        var root = first.Root;
        await first.StopAsync();
        await RunOnDatabaseAsync(first.Database, "DROP TABLE backwave_jobs");

        // Act
        await RunOnDatabaseAsync(first.Database, "DELETE FROM backwave_schema_version");
        await using var server = await ServerProcess.StartAsync(root);
        await WaitUntilTheJobTablesExistAsync(first.Database);
        await Task.Delay(TimeSpan.FromSeconds(1));
        using var health = await server.Client.GetAsync("/health");

        // Assert
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        server.Output.ShouldNotContain(JobsStopped);
    }

    private static async Task WaitUntilTheJobTablesExistAsync(string database)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (await CountJobsAsync(database) > 0)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException("The first boot made no job row.");
    }

    private static async Task<long> CountJobsAsync(string database)
    {
        try
        {
            await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = new SqliteCommand("SELECT count(*) FROM backwave_jobs", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static async Task RunOnDatabaseAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HttpResponseMessage> HealthThatStoppedAsync(HttpClient client)
    {
        for (var attempt = 0; ; attempt++)
        {
            var health = await client.GetAsync("/health");
            if (health.StatusCode != HttpStatusCode.OK || attempt == 300)
                return health;

            health.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private static async Task WaitForOutputLineAsync(ServerProcess server, string text)
    {
        for (var attempt = 0; attempt < 300 && !server.Output.Contains(text, StringComparison.Ordinal); attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(100));
    }
}
