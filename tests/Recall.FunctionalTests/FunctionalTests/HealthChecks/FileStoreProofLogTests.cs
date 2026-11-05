namespace Recall.FunctionalTests.FunctionalTests.HealthChecks;

using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.Utilities;
using Serilog.Events;
using Shouldly;

public class FileStoreProofLogTests : TestBase
{
    [Test]
    public async Task a_root_that_stays_broken_logs_one_error_that_names_the_root_and_the_cause_and_a_repair_logs_it()
    {
        // Arrange
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        using var folder = new TemporaryFileRoot();
        Directory.CreateDirectory(folder.Path);
        var blocker = Path.Combine(folder.Path, "a-file");
        await File.WriteAllBytesAsync(blocker, [1]);
        var root = Path.Combine(blocker, "files");
        var store = new FileStore(Options.Create(new FileStoreOptions { Root = root }));

        // Act
        var cause = store.Prepare().NotWritableCause.ShouldNotBeNull();
        store.ProveWritable().ShouldNotBeNull();
        store.ProveWritable().ShouldNotBeNull();
        File.Delete(blocker);
        store.ProveWritable().ShouldBeNull();

        // Assert
        var messages = logs.Holding(root).Select(e => (e.Level, Message: e.RenderMessage(CultureInfo.InvariantCulture))).ToList();
        messages.Count.ShouldBe(2);
        messages[0].Level.ShouldBe(LogEventLevel.Error);
        messages[0].Message.ShouldContain(root);
        messages[0].Message.ShouldContain(cause);
        messages[1].Level.ShouldBe(LogEventLevel.Information);
    }

    [Test]
    public async Task a_proof_that_hangs_gives_degraded_at_the_time_limit_with_one_error_and_no_second_proof()
    {
        // Arrange
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        var root = $"/hanging-root-{Guid.NewGuid():N}";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var release = new ManualResetEventSlim();
        var proofs = 0;
        var check = new FileStoreHealthCheck(
            () =>
            {
                Interlocked.Increment(ref proofs);
                release.Wait();
                return null;
            },
            root,
            clock);
        var context = new HealthCheckContext { Registration = new("file-store", check, HealthStatus.Degraded, null) };

        // Act
        var firstProbe = check.CheckHealthAsync(context);
        var secondProbe = check.CheckHealthAsync(context);
        clock.Advance(FileStoreHealthCheck.TimeBetweenProofs);
        var whileHung = await Task.WhenAll(firstProbe, secondProbe);
        var pastTheInterval = check.CheckHealthAsync(context);
        release.Set();
        var whenFinished = await pastTheInterval;

        // Assert
        whileHung.ShouldAllBe(result => result.Status == HealthStatus.Degraded);
        whenFinished.Status.ShouldBe(HealthStatus.Healthy);
        proofs.ShouldBe(1);
        logs.Holding(root).Count.ShouldBe(1);
        logs.Holding(root)[0].Level.ShouldBe(LogEventLevel.Error);
    }
}
