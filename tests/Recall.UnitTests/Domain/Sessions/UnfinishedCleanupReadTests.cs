namespace Recall.UnitTests.Domain.Sessions;

using System.Text;
using System.Text.Json;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.Sessions.Jobs;
using Shouldly;

/// <summary>SES-44: a start reads each Cleanup that has not ended before it picks a queue.</summary>
public class UnfinishedCleanupReadTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task EnqueueAsync(InMemoryJobStore store, string queue, byte[] payload)
        => await store.EnqueueAsync(new NewJob(Guid.NewGuid(), CleanupJob.WireName, payload, queue, Noon.AddDays(1)), Noon, null!, CancellationToken.None);

    private static byte[] Payload(string tenantId, int number)
        => JsonSerializer.SerializeToUtf8Bytes(new CleanupSession($"ses_{number}", "usr_1", tenantId, $"run_{number}"));

    [Test]
    [Property("Rule", "SES-44")]
    public async Task the_read_finds_a_cleanup_behind_more_than_a_page_of_others()
    {
        // Arrange
        var store = new InMemoryJobStore(new StoreBounds(), JobHistoryPolicy.Off);
        for (var i = 0; i < 201; i++)
            await EnqueueAsync(store, "cleanup-0", Payload("ten_crowd", i));
        await EnqueueAsync(store, "cleanup-5", Payload("ten_late", 999));

        // Act
        var unfinished = await StartCleanup.UnfinishedCleanupsAsync(store, CancellationToken.None);

        // Assert
        unfinished.Count.ShouldBe(202);
        unfinished.ShouldContain(("cleanup-5", "ten_late"));
    }

    [Test]
    [Property("Rule", "SES-44")]
    public async Task a_payload_that_does_not_read_is_left_out_and_the_others_are_read()
    {
        // Arrange
        var store = new InMemoryJobStore(new StoreBounds(), JobHistoryPolicy.Off);
        await EnqueueAsync(store, "cleanup-1", Encoding.UTF8.GetBytes("null"));
        await EnqueueAsync(store, "cleanup-2", Encoding.UTF8.GetBytes("{not json"));
        await EnqueueAsync(store, "cleanup-3", Payload("ten_ok", 1));

        // Act
        var unfinished = await StartCleanup.UnfinishedCleanupsAsync(store, CancellationToken.None);

        // Assert
        unfinished.ShouldBe([("cleanup-3", "ten_ok")]);
    }
}
