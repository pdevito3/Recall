namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Domain.ImportBatches;
using Recall.Server.Domain.ImportBatches.Dtos;
using Recall.Server.Domain.ImportBatches.Features;
using Recall.Server.Exceptions;
using Recall.IntegrationTests.Databases;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;
using static ImportScenario;

public class ImportBatchRaceTests : TestBase
{
    [Test]
    [Property("Rule", "IMP-17")]
    public async Task a_put_that_an_accept_beat_after_its_load_runs_again_and_gets_409()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        var pause = PauseAtQuery.After("import_batches.put.entries");
        await using var services = pause.BuildServices();

        // Act
        var put = PutAsync(WebSessionOf(owner, services), batch.Id, Entry("late", 1));
        await pause.ReachedAsync();
        var accepted = await AcceptAsync(owner, batch.Id);
        pause.Release();
        var failure = await put.ContinueWith(t => t.Exception?.InnerException, TaskScheduler.Default);

        // Assert
        failure.ShouldBeOfType<ConflictException>().Message.ShouldBe(ImportBatch.AcceptedMessage);
        accepted.Result!.SessionsMade.ShouldBe(1);
        var rows = await WebSessionOf(owner).ExecuteDbContextAsync(async db =>
            (await db.Sessions.Select(s => s.ClientSessionId).ToListAsync(), await db.ImportEntries.CountAsync()));
        rows.Item1.ShouldBe(["a"]);
        rows.Item2.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IMP-17")]
    public async Task an_accept_that_a_put_beat_after_its_load_gets_409_and_makes_no_session()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        var version = (await EntriesAsync(owner, batch.Id)).Version;
        var pause = PauseAtQuery.After("import_batches.accept.entries");
        await using var services = pause.BuildServices();

        // Act
        var accept = WebSessionOf(owner, services).SendAsync(new AcceptImportBatch.Command(batch.Id, new ImportBatchAcceptDto { Version = version }));
        await pause.ReachedAsync();
        var put = await PutAsync(owner, batch.Id, Entry("late", 1));
        pause.Release();
        var failure = await accept.ContinueWith(t => t.Exception?.InnerException, TaskScheduler.Default);

        // Assert
        failure.ShouldBeOfType<StaleVersionException>().Message.ShouldBe(ImportBatch.ChangedMessage);
        OutcomesOf(put).ShouldBe([("late", "Added")]);
        var stored = await EntriesAsync(owner, batch.Id);
        stored.Entries.Select(e => e.ClientSessionId).ShouldBe(["a", "late"]);
        stored.Version.ShouldBe(put.Version);
        (await WebSessionOf(owner).ExecuteDbContextAsync(db => db.Sessions.CountAsync())).ShouldBe(0);
    }
}
