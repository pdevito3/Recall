namespace Recall.UnitTests.Resources.FileTelemetry;

using System.Globalization;
using Recall.Server.Resources.FileTelemetry;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Shouldly;

public class RollingFileLimitsTests
{
    private static string NewFolder() => Directory.CreateTempSubdirectory("recall_rolling_").FullName;

    private static string Line(int number) => $"{{\"n\":{number},\"pad\":\"{new string('x', 80)}\"}}";

    private sealed class FormatterOnADiskThatBlocks(ManualResetEventSlim diskIsBack) : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            diskIsBack.Wait();
            output.Write("line\n");
        }
    }

    [Test]
    public async Task a_disk_that_blocks_does_not_hold_the_caller_and_a_full_queue_drops_the_line()
    {
        // Arrange
        var folder = NewFolder();
        using var diskIsBack = new ManualResetEventSlim();
        const int linesSent = 11_000;
        var limits = new RollingFileLimits { FileSizeBytes = 1_048_576, FilesKept = 3, DaysKept = 14 };
        var logger = new LoggerConfiguration()
            .WriteTo.RollingFiles(new FormatterOnADiskThatBlocks(diskIsBack), Path.Combine(folder, "recall-.jsonl"), limits)
            .CreateLogger();

        try
        {
            // Act
            var calls = Task.Run(() =>
            {
                for (var number = 0; number < linesSent; number++)
                    logger.Information("A line");
            });
            var first = await Task.WhenAny(calls, Task.Delay(TimeSpan.FromSeconds(30)));

            // Assert
            first.ShouldBe(calls, "A logging call waited for the disk.");
            diskIsBack.Set();
            await logger.DisposeAsync();
            var kept = Directory.GetFiles(folder).SelectMany(File.ReadLines).Count();
            kept.ShouldBeGreaterThan(0);
            kept.ShouldBeLessThan(linesSent);
        }
        finally
        {
            diskIsBack.Set();
            await logger.DisposeAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void a_days_kept_of_zero_keeps_only_the_file_that_just_opened()
    {
        // Arrange
        var folder = NewFolder();
        var limits = new RollingFileLimits { FileSizeBytes = 1024, FilesKept = 20, DaysKept = 0 };

        try
        {
            // Act
            using (var file = new JsonLinesFile(Path.Combine(folder, "traces-.jsonl"), limits))
            {
                for (var number = 1; number <= 200; number++)
                    file.Write(Line(number));
            }

            // Assert
            File.ReadLines(Directory.GetFiles(folder).ShouldHaveSingleItem()).ShouldContain(Line(200));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void a_file_at_its_size_limit_rolls_and_the_folder_keeps_the_count_limit_and_the_newest_lines()
    {
        // Arrange
        var folder = NewFolder();
        const int lastLine = 2000;
        var limits = new RollingFileLimits { FileSizeBytes = 1024, FilesKept = 3, DaysKept = 14 };

        try
        {
            // Act
            using (var file = new JsonLinesFile(Path.Combine(folder, "traces-.jsonl"), limits))
            {
                for (var number = 1; number <= lastLine; number++)
                    file.Write(Line(number));
            }

            // Assert
            var files = Directory.GetFiles(folder);
            files.Length.ShouldBe(3);
            var longestLine = Line(lastLine).Length + 1;
            files.ShouldAllBe(path => new FileInfo(path).Length <= limits.FileSizeBytes + longestLine);
            files.SelectMany(File.ReadLines).ShouldContain(Line(lastLine));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void a_file_older_than_the_age_limit_goes_when_a_new_file_opens()
    {
        // Arrange
        var folder = NewFolder();
        var limits = new RollingFileLimits { FileSizeBytes = 1024, FilesKept = 3, DaysKept = 14 };
        // The sink reads the real clock, and a file name holds a local date
        var today = TimeProvider.System.GetLocalNow();
        var old = Path.Combine(folder, $"traces-{today.AddDays(-15).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl");
        var recent = Path.Combine(folder, $"traces-{today.AddDays(-13).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl");
        File.WriteAllText(old, "old\n");
        File.WriteAllText(recent, "recent\n");

        try
        {
            // Act
            using (var file = new JsonLinesFile(Path.Combine(folder, "traces-.jsonl"), limits))
                file.Write(Line(1));

            // Assert
            File.Exists(old).ShouldBeFalse();
            File.Exists(recent).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void a_line_is_the_text_and_one_newline_with_nothing_around_it()
    {
        // Arrange
        var folder = NewFolder();
        var limits = new RollingFileLimits { FileSizeBytes = 1024, FilesKept = 3, DaysKept = 14 };

        try
        {
            // Act
            using (var file = new JsonLinesFile(Path.Combine(folder, "traces-.jsonl"), limits))
                file.Write("""{"a":"{Not} a {0} template"}""");

            // Assert
            File.ReadAllText(Directory.GetFiles(folder).ShouldHaveSingleItem()).ShouldBe("""{"a":"{Not} a {0} template"}""" + "\n");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void the_default_kind_of_limits_has_no_refusal_and_a_days_kept_of_zero_is_allowed()
    {
        // Arrange
        var limits = new RollingFileLimits { FileSizeBytes = 1, FilesKept = 1, DaysKept = 0 };
        var options = new FileTelemetryOptions { Logs = limits, Traces = limits, Metrics = limits, MetricExportSeconds = 1 };

        // Act
        var refusal = options.Refusal();

        // Assert
        refusal.ShouldBeNull();
    }

    [Test]
    public void each_limit_below_its_least_value_gets_a_refusal_that_names_the_setting()
    {
        // Arrange
        var good = new RollingFileLimits { FileSizeBytes = 1024, FilesKept = 3, DaysKept = 14 };
        var options = new FileTelemetryOptions { Logs = good, Traces = good, Metrics = good, MetricExportSeconds = 60 };

        // Act
        var refusals = new[]
        {
            options with { Logs = good with { DaysKept = -1 } },
            options with { Traces = good with { FilesKept = 0 } },
            options with { Metrics = good with { FileSizeBytes = 0 } },
            options with { MetricExportSeconds = 0 },
        }.Select(bad => bad.Refusal()).ToList();

        // Assert
        refusals[0].ShouldNotBeNull().ShouldContain("FileTelemetry:Logs:DaysKept is -1");
        refusals[1].ShouldNotBeNull().ShouldContain("FileTelemetry:Traces:FilesKept is 0");
        refusals[2].ShouldNotBeNull().ShouldContain("FileTelemetry:Metrics:FileSizeBytes is 0");
        refusals[3].ShouldNotBeNull().ShouldContain("FileTelemetry:MetricExportSeconds is 0");
    }

    [Test]
    public void a_folder_that_takes_no_file_does_not_stop_the_caller()
    {
        // Arrange
        var blocker = Path.GetTempFileName();
        var limits = new RollingFileLimits { FileSizeBytes = 1024, FilesKept = 3, DaysKept = 14 };

        try
        {
            // Act
            using var file = new JsonLinesFile(Path.Combine(blocker, "traces-.jsonl"), limits);
            var write = () => file.Write(Line(1));

            // Assert
            write.ShouldNotThrow();
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
