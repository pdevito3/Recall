namespace Recall.UnitTests.Resources.Jobs;

using System.Collections.Concurrent;
using System.Globalization;
using BackWave;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Recall.Server.Resources.Jobs;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

public class JobHostWatchTests
{
    private const string Marker = "text of a person";

    private sealed class Lines : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> All { get; } = new();

        public void Emit(LogEvent logEvent) => All.Enqueue(logEvent);
    }

    private static JobHostWatch WatchWithLinesIn(Lines lines, IJobStore store, TimeProvider clock)
    {
        var registry = new JobModule
        {
            Registrations =
            [
                new JobRegistration
                {
                    WireName = "system-health",
                    JobType = typeof(SystemHealth),
                    Queue = JobHosting.SystemQueue,
                    Deserialize = _ => null!,
                    Serialize = _ => [],
                    Execute = (_, _, _, _) => Task.CompletedTask,
                },
            ],
            Handlers = [],
            ContainingTypes = [],
        }.CreateRegistry();

        // The watch takes the static logger when it is made.
        var main = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(lines).CreateLogger();
        try
        {
            return new JobHostWatch(new BackWaveClient(store, registry, clock), store, new JobHostStatus(new BackWaveHealth()), clock);
        }
        finally
        {
            Log.Logger = main;
        }
    }

    private static int EnqueuesOf(IJobStore store) =>
        store.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IJobStore.EnqueueAsync));

    private static async Task TicksAsync(IJobStore store, FakeTimeProvider clock, int enqueues)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (EnqueuesOf(store) < enqueues && DateTimeOffset.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task a_store_that_stays_locked_gives_one_warning_with_the_type_and_no_message()
    {
        // Arrange
        var store = Substitute.For<IJobStore>();
#pragma warning disable CA2012 // This call only names the method for the substitute. It runs no work.
        store.EnqueueAsync(default!, default, default, default).ReturnsForAnyArgs<ValueTask<EnqueueResult>>(_ => throw new TimeoutException(Marker));
#pragma warning restore CA2012
        var clock = new FakeTimeProvider();
        var lines = new Lines();
        var watch = WatchWithLinesIn(lines, store, clock);

        // Act
        await watch.StartAsync(CancellationToken.None);
        await TicksAsync(store, clock, enqueues: 3);
        await watch.StopAsync(CancellationToken.None);

        // Assert
        EnqueuesOf(store).ShouldBeGreaterThanOrEqualTo(3);
        var warning = lines.All
            .Where(line => line.Level == LogEventLevel.Warning && line.Properties["SourceContext"].ToString().Contains(nameof(JobHostWatch)))
            .ShouldHaveSingleItem()
            .RenderMessage(CultureInfo.InvariantCulture);
        warning.ShouldContain(typeof(TimeoutException).FullName!);
        warning.ShouldNotContain(Marker);
    }
}
