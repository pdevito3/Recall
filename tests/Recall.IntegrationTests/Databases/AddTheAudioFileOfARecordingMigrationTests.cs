namespace Recall.IntegrationTests.Databases;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Tenants;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class AddTheAudioFileOfARecordingMigrationTests : IAsyncDisposable
{
    private const string MigrationBefore = "20261111002850_AddTheLostAudioOfARecording";

    private readonly SqliteTestDatabase _database = new();
    private readonly ServiceProvider _services;

    public AddTheAudioFileOfARecordingMigrationTests()
        => _services = TestFixture.BuildServiceProvider(_ => { }, _database.ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Test]
    [Property("Rule", "REC-24")]
    [Arguments("Transcribed", null, "None", "Webm", "audio.webm")]
    [Arguments("Received", null, "None", "Mp4", "audio.mp4")]
    [Arguments("Receiving", 1, "None", "Webm", "audio.webm")]
    [Arguments("Receiving", null, "None", "Webm", null)]
    [Arguments("Failed", null, "NoAudioReceived", "Webm", null)]
    public async Task up_names_the_audio_that_a_recording_joined_before_the_column(
        string status, int? quietEndThroughSlice, string problem, string format, string? audioFile)
    {
        // Arrange
        await MigrateAsync(target: null);
        var recordingId = await AddRecordingAsync(Enum.Parse<RecordingFormat>(format));
        await MigrateAsync(MigrationBefore);
        await ExecuteAsync(
            "UPDATE recordings SET status = $status, quiet_end_through_slice = $quiet, problem = $problem WHERE id = $id",
            ("$status", status), ("$quiet", quietEndThroughSlice), ("$problem", problem), ("$id", recordingId));

        // Act
        await MigrateAsync(target: null);

        // Assert
        (await AudioFileAsync(recordingId)).ShouldBe(audioFile);
    }

    private async Task MigrateAsync(string? target)
    {
        await using var system = _services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await system.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<string> AddRecordingAsync(RecordingFormat format)
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner)
            .Under(tenant);
        var factory = _services.GetRequiredService<IServiceScopeFactory>();
        await using (var system = factory.CreateSystemScope())
        {
            var dbContext = system.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Tenants.Add(tenant);
            dbContext.Users.Add(owner);
            await dbContext.SaveChangesAsync();
        }

        var session = Session.Create(tenant.Id, owner.Id, $"migration-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch);
        var recording = Recording.Create(tenant.Id, owner.Id, session.Id, $"migration-{Guid.NewGuid():N}", format);
        await using (var journal = factory.CreateJournalScope(new JournalOwner(owner.Id, owner.TenantId)))
        {
            var dbContext = journal.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Sessions.Add(session);
            dbContext.Recordings.Add(recording);
            await dbContext.SaveChangesAsync();
        }

        return recording.Id;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> AudioFileAsync(string recordingId)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand("SELECT audio_file FROM recordings WHERE id = $id", connection);
        command.Parameters.AddWithValue("$id", recordingId);
        return await command.ExecuteScalarAsync() as string;
    }
}
