namespace Recall.UnitTests.Resources.FileTelemetry;

using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Recall.Server.Resources.FileTelemetry;
using Serilog.Core;
using Shouldly;

public class FileSinkFailureNoticeTests
{
    private static string[] LinesOf(StringWriter console) => console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void Fail(FileSinkFailureNotice notice, Exception? exception = null) =>
        notice.OnLoggingFailed(notice, LoggingFailureKind.Permanent, "the target file could not be opened or created", events: null, exception);

    [Test]
    public void a_fault_with_many_failed_lines_gives_one_warning_that_names_the_file_and_the_cause()
    {
        // Arrange
        using var console = new StringWriter();
        var notice = new FileSinkFailureNotice("/data/logs/recall-.jsonl", new FakeTimeProvider(), console);

        // Act
        Fail(notice, new IOException("No space left on device"));
        for (var failure = 0; failure < 1000; failure++)
            Fail(notice);

        // Assert
        var line = JsonDocument.Parse(LinesOf(console).ShouldHaveSingleItem()).RootElement;
        line.GetProperty("@l").GetString().ShouldBe("Warning");
        line.GetProperty("@mt").GetString().ShouldStartWith(FileSinkFailureNotice.Text);
        line.GetProperty("File").GetString().ShouldBe("/data/logs/recall-.jsonl");
        line.GetProperty("Cause").GetString().ShouldBe("No space left on device");
    }

    [Test]
    public void a_step_of_a_retry_that_loses_no_line_gives_no_warning()
    {
        // Arrange
        using var console = new StringWriter();
        var notice = new FileSinkFailureNotice("/data/logs/recall-.jsonl", new FakeTimeProvider(), console);

        // Act
        notice.OnLoggingFailed(notice, LoggingFailureKind.Temporary, "the file was locked, so the next one opens", events: null, exception: null);

        // Assert
        console.ToString().ShouldBeEmpty();
    }

    [Test]
    public void a_fault_that_lasts_gives_one_more_warning_after_the_interval()
    {
        // Arrange
        using var console = new StringWriter();
        var clock = new FakeTimeProvider();
        var notice = new FileSinkFailureNotice("/data/logs/recall-.jsonl", clock, console);
        Fail(notice);

        // Act
        clock.Advance(FileSinkFailureNotice.Interval - TimeSpan.FromSeconds(1));
        Fail(notice);
        var beforeTheInterval = LinesOf(console).Length;
        clock.Advance(TimeSpan.FromSeconds(1));
        Fail(notice);

        // Assert
        beforeTheInterval.ShouldBe(1);
        LinesOf(console).Length.ShouldBe(2);
    }

    [Test]
    public void a_file_that_cannot_close_gives_no_throw_when_the_console_also_fails()
    {
        // Arrange
        var console = new StringWriter();
        console.Dispose();
        var notice = new FileSinkFailureNotice("/data/logs/recall-.jsonl", new FakeTimeProvider(), console);
        var file = Substitute.For<ILogEventSink, IDisposable>();
        ((IDisposable)file).When(sink => sink.Dispose()).Throw(new IOException("No space left on device"));
        var closes = new ClosesWithNoThrow(file, notice);

        // Act
        var close = () => closes.Dispose();

        // Assert
        close.ShouldNotThrow();
    }

    [Test]
    public void a_file_that_cannot_close_gives_a_line_on_the_console_and_no_throw()
    {
        // Arrange
        using var console = new StringWriter();
        var notice = new FileSinkFailureNotice("/data/logs/recall-.jsonl", new FakeTimeProvider(), console);
        Fail(notice);
        var file = Substitute.For<ILogEventSink, IDisposable>();
        ((IDisposable)file).When(sink => sink.Dispose()).Throw(new IOException("No space left on device"));
        var closes = new ClosesWithNoThrow(file, notice);

        // Act
        var close = () => closes.Dispose();

        // Assert
        close.ShouldNotThrow();
        var lines = LinesOf(console);
        lines.Length.ShouldBe(2);
        var line = JsonDocument.Parse(lines[1]).RootElement;
        line.GetProperty("@mt").GetString().ShouldNotBeNull().ShouldContain("The lines that it still held are lost");
        line.GetProperty("Cause").GetString().ShouldBe("No space left on device");
    }
}
