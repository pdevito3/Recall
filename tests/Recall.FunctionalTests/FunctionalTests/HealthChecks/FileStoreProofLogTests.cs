namespace Recall.FunctionalTests.FunctionalTests.HealthChecks;

using System.Diagnostics;
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
    private static readonly TimeSpan RegressionLimit = TimeSpan.FromSeconds(10);

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
        var context = ContextOf(check);

        try
        {
            // Act
            var firstProbe = check.CheckHealthAsync(context);
            var secondProbe = check.CheckHealthAsync(context);
            await EventuallyAsync(() => Volatile.Read(ref proofs) > 0);
            clock.Advance(FileStoreHealthCheck.LongestWaitForAProof);
            var whileHung = await Task.WhenAll(firstProbe, secondProbe).WaitAsync(RegressionLimit);
            var pastTheLimit = await check.CheckHealthAsync(context).WaitAsync(RegressionLimit);
            clock.Advance(FileStoreHealthCheck.TimeBetweenProofs);
            var pastTheInterval = await check.CheckHealthAsync(context).WaitAsync(RegressionLimit);
            await EventuallyAsync(() => logs.Holding(root).Count > 0);

            // Assert
            whileHung.ShouldAllBe(result => result.Status == HealthStatus.Degraded);
            pastTheLimit.Status.ShouldBe(HealthStatus.Degraded);
            pastTheInterval.Status.ShouldBe(HealthStatus.Degraded);
            proofs.ShouldBe(1);
            logs.Holding(root).Count.ShouldBe(1);
            logs.Holding(root)[0].Level.ShouldBe(LogEventLevel.Error);
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public async Task a_proof_that_hangs_and_then_finishes_its_write_logs_one_recovery_line()
    {
        // Arrange
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        var root = $"/hanging-root-{Guid.NewGuid():N}";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var release = new ManualResetEventSlim();
        var check = new FileStoreHealthCheck(
            () =>
            {
                release.Wait();
                return null;
            },
            root,
            clock);

        try
        {
            // Act
            var probe = check.CheckHealthAsync(ContextOf(check));
            clock.Advance(FileStoreHealthCheck.LongestWaitForAProof);
            await probe.WaitAsync(RegressionLimit);
            await EventuallyAsync(() => logs.Holding(root).Count > 0);
            release.Set();
            await EventuallyAsync(() => logs.Holding(root).Count > 1);

            // Assert
            var lines = logs.Holding(root);
            lines.Count.ShouldBe(2);
            lines[0].Level.ShouldBe(LogEventLevel.Error);
            lines[1].Level.ShouldBe(LogEventLevel.Information);
            lines[1].Exception.ShouldBeNull();
            lines[1].RenderMessage(CultureInfo.InvariantCulture).ShouldContain("takes writes again");
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public async Task a_proof_that_hangs_and_then_fails_logs_no_recovery_line()
    {
        // Arrange
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        var root = $"/hanging-root-{Guid.NewGuid():N}";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var release = new ManualResetEventSlim();
        var proofFinished = new TaskCompletionSource();
        var check = new FileStoreHealthCheck(
            () =>
            {
                release.Wait();
                proofFinished.SetResult();
                return "The disk is full.";
            },
            root,
            clock);

        try
        {
            // Act
            var probe = check.CheckHealthAsync(ContextOf(check));
            clock.Advance(FileStoreHealthCheck.LongestWaitForAProof);
            await probe.WaitAsync(RegressionLimit);
            await EventuallyAsync(() => logs.Holding(root).Count > 0);
            release.Set();
            await proofFinished.Task.WaitAsync(RegressionLimit);
            var afterTheFailure = await check.CheckHealthAsync(ContextOf(check)).WaitAsync(RegressionLimit);

            // Assert
            afterTheFailure.Status.ShouldBe(HealthStatus.Degraded);
            logs.Holding(root).ShouldHaveSingleItem().Level.ShouldBe(LogEventLevel.Error);
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public async Task a_proof_that_hangs_logs_its_error_when_the_prober_leaves_before_the_time_limit()
    {
        // Arrange
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        var root = $"/hanging-root-{Guid.NewGuid():N}";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var release = new ManualResetEventSlim();
        using var prober = new CancellationTokenSource();
        var check = new FileStoreHealthCheck(
            () =>
            {
                release.Wait();
                return null;
            },
            root,
            clock);

        try
        {
            // Act
            var probe = check.CheckHealthAsync(ContextOf(check), prober.Token);
            await prober.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => probe);
            clock.Advance(FileStoreHealthCheck.LongestWaitForAProof);
            await EventuallyAsync(() => logs.Holding(root).Count > 0);

            // Assert
            logs.Holding(root).Count.ShouldBe(1);
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public async Task a_proof_that_finishes_in_time_logs_nothing_and_a_probe_sees_its_result()
    {
        // Arrange
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        var root = $"/quick-root-{Guid.NewGuid():N}";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var check = new FileStoreHealthCheck(() => null, root, clock);

        // Act
        var result = await check.CheckHealthAsync(ContextOf(check));
        clock.Advance(FileStoreHealthCheck.LongestWaitForAProof);
        var pastTheLimit = await check.CheckHealthAsync(ContextOf(check));

        // Assert
        result.Status.ShouldBe(HealthStatus.Healthy);
        pastTheLimit.Status.ShouldBe(HealthStatus.Healthy);
        logs.Holding(root).ShouldBeEmpty();
    }

    private static HealthCheckContext ContextOf(IHealthCheck check)
        => new() { Registration = new("file-store", check, HealthStatus.Degraded, null) };

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var limit = Stopwatch.StartNew();
        while (!condition() && limit.Elapsed < RegressionLimit)
            await Task.Delay(10);
    }
}
