namespace Recall.UnitTests.Resources.FileTelemetry;

using System.Globalization;
using Recall.Server.Resources.FileTelemetry;
using Shouldly;

public class RollingFileLimitsTests
{
    private static string NewFolder() => Directory.CreateTempSubdirectory("recall_rolling_").FullName;

    private static string Line(int number) => $"{{\"n\":{number},\"pad\":\"{new string('x', 80)}\"}}";

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
