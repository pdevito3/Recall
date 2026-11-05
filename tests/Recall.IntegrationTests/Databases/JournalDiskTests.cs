namespace Recall.IntegrationTests.Databases;

using System.Text;
using Microsoft.Data.Sqlite;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Shouldly;

public class JournalDiskTests : TestBase
{
    private const int MaxCheckpoints = 200;
    private static readonly TimeSpan CheckpointGap = TimeSpan.FromMilliseconds(250);

    [Test]
    [Property("Rule", "SES-27")]
    public async Task after_a_delete_the_words_are_not_in_the_database_file_or_the_wal()
    {
        // Arrange
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        var words = string.Concat(Enumerable.Repeat(marker + " ", 500));
        var scope = new TestingServiceScope();
        var created = await scope.SendAsync(new AddSession.Command(new SessionForCreationDto
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        }));
        await scope.SendAsync(new SaveRawDraft.Command(created.Session.Id, new DraftSaveDto
        {
            Text = words,
            BaseRevision = 0,
            SavedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        }));
        var path = new SqliteConnectionStringBuilder(TestFixture.UnpooledConnectionString).DataSource;
        (await FilesHoldAsync(path, marker)).ShouldBeTrue();

        // Act
        await new TestingServiceScope().SendAsync(new DeleteSession.Command(created.Session.Id));
        await CheckpointUntilFreeAsync();

        // Assert
        (await FilesHoldAsync(path, marker)).ShouldBeFalse();
    }

    private static async Task CheckpointUntilFreeAsync()
    {
        for (var i = 0; i < MaxCheckpoints; i++)
        {
            if (await WalScrub.TryTruncateAsync(TestFixture.UnpooledConnectionString, CancellationToken.None))
                return;
            await Task.Delay(CheckpointGap);
        }

        throw new InvalidOperationException("The WAL stayed busy, so this run proves nothing.");
    }

    private static async Task<bool> FilesHoldAsync(string path, string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        foreach (var file in new[] { path, $"{path}-wal" })
        {
            if (!File.Exists(file))
                continue;

            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            if (buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf(needle) >= 0)
                return true;
        }

        return false;
    }
}
