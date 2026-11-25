namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recall.Server.Databases;
using Recall.Server.Domain.ImportBatches;
using Recall.Server.Domain.ImportBatches.Dtos;
using Recall.Server.Domain.ImportBatches.Features;
using Shouldly;
using static ImportScenario;

public class ImportAcceptCostBenchmark : TestBase
{
    private const int RawLength = 1_500;
    private const int EntriesPerDay = 10;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(20);

    [Test]
    [Explicit]
    public async Task measure_the_accept_of_the_largest_batch_that_the_limit_allows()
    {
        // Arrange
        var entries = int.Parse(Environment.GetEnvironmentVariable("IMPORT_ACCEPT_BENCHMARK_ENTRIES")
            ?? ImportBatch.AcceptMaxEntries.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var clock = new AcceptClock();
        // Production logs Entity Framework at Warning, and a log line for each of the 60,000 commands would be the cost
        await using var services = TestFixture.BuildServiceProvider(s => s
            .AddLogging(logging => logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning))
            .ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(clock)));
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        var raw = RawOfLength(RawLength);
        uint version = 0;
        foreach (var call in Enumerable.Range(0, entries).Chunk(ImportBatch.PutMaxEntries))
        {
            var put = await PutAsync(ScopeOf(owner, services), batch.Id, call.Select(i => Entry(
                $"bench-{i}", i % EntriesPerDay, raw, new DateOnly(2020, 1, 1).AddDays(i / EntriesPerDay))).ToArray());
            put.Outcomes.ShouldAllBe(o => o.Outcome == nameof(ImportPutOutcome.Added));
            version = put.Version;
        }

        var scope = ScopeOf(owner, services);
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var sampling = new CancellationTokenSource();
        var peakHeap = SamplePeakHeapAsync(sampling.Token);

        // Act
        clock.Start();
        var accepted = await scope.SendAsync(new AcceptImportBatch.Command(batch.Id, new ImportBatchAcceptDto { Version = version }));
        clock.Stop();

        // Assert
        await sampling.CancelAsync();
        var peak = await peakHeap;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        accepted.Result!.SessionsMade.ShouldBe(entries);
        var written = await ScopeOf(owner).ExecuteDbContextAsync(async db => (
            Sessions: await db.Sessions.CountAsync(s => s.ImportBatchId == batch.Id),
            Entries: await db.ImportEntries.CountAsync(e => e.BatchId == batch.Id),
            Audit: await db.AuditEntries.IgnoreQueryFilters().CountAsync(),
            Sync: await db.SyncChanges.IgnoreQueryFilters().CountAsync()));
        written.Sessions.ShouldBe(entries);
        written.Entries.ShouldBe(0);
        Console.WriteLine(
            $"entries={entries} raw_chars={RawLength} load_ms={clock.Load.TotalMilliseconds:F0} build_ms={clock.Build.TotalMilliseconds:F0} "
            + $"save_ms={clock.Save.TotalMilliseconds:F0} commit_ms={clock.Commit.TotalMilliseconds:F0} total_ms={clock.Total.TotalMilliseconds:F0} "
            + $"commands_in_save={clock.CommandsInSave} heap_before_mb={heapBefore / 1048576} peak_heap_mb={peak / 1048576} "
            + $"heap_growth_mb={(peak - heapBefore) / 1048576} allocated_mb={allocated / 1048576} "
            + $"audit_rows={written.Audit} sync_rows={written.Sync}");
    }

    private static string RawOfLength(int length)
    {
        var text = new StringBuilder();
        for (var word = 0; text.Length < length; word++)
            text.Append(word % 60 == 59 ? "end of a thought.\n\n" : $"word{word} ");
        return text.ToString(0, length).TrimEnd();
    }

    private static async Task<long> SamplePeakHeapAsync(CancellationToken stop)
    {
        var peak = 0L;
        while (!stop.IsCancellationRequested)
        {
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
            await Task.Delay(SampleInterval, CancellationToken.None);
        }
        return peak;
    }

    /// <summary>
    /// Splits an accept at the start of the EF save and at the commit. Load is the reads up to the last reader that
    /// closes before the save, build is the rest until the save, and save is the write up to the end of the commit.
    /// </summary>
    private sealed class AcceptClock : ISaveChangesInterceptor, IDbCommandInterceptor, IDbTransactionInterceptor
    {
        private readonly Stopwatch _watch = new();
        private TimeSpan _lastLoad;
        private TimeSpan? _saving;
        private TimeSpan? _commitStarted;
        private TimeSpan? _committed;
        private TimeSpan _stopped;

        public int CommandsInSave { get; private set; }

        public TimeSpan Load => _lastLoad;
        public TimeSpan Build => (_saving ?? _stopped) - _lastLoad;
        public TimeSpan Save => (_committed ?? _stopped) - (_saving ?? _stopped);
        public TimeSpan Commit => (_committed ?? _stopped) - (_commitStarted ?? _stopped);
        public TimeSpan Total => _stopped;

        public void Start() => _watch.Start();

        public void Stop()
        {
            _stopped = _watch.Elapsed;
            _watch.Stop();
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_watch.IsRunning)
                _saving ??= _watch.Elapsed;
            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            if (_saving is null && _watch.IsRunning)
                _lastLoad = _watch.Elapsed;
            return ValueTask.FromResult(result);
        }

        public ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (_watch.IsRunning && _saving is not null)
                CommandsInSave++;
            return ValueTask.FromResult(result);
        }

        public ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (_watch.IsRunning && _saving is not null)
                CommandsInSave++;
            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (_watch.IsRunning)
                _commitStarted ??= _watch.Elapsed;
            return ValueTask.FromResult(result);
        }

        public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (_watch.IsRunning)
                _committed ??= _watch.Elapsed;
            return Task.CompletedTask;
        }
    }
}
