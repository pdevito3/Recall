namespace Recall.UnitTests.Resources;

using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Recall.Server.Resources;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;

public class TelemetryTextTests
{
    private static readonly string TooLong = new('<', TelemetryText.MaxLength + 1);

    private sealed class Recording(List<Activity> spans) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => spans.Add(data);
    }

    private sealed class Captured : ILogEventSink, IDisposable
    {
        public List<LogEvent> Events { get; } = [];

        public bool Disposed { get; private set; }

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);

        public void Dispose() => Disposed = true;
    }

    private static LogEvent Through(LogEvent logEvent)
    {
        var captured = new Captured();
        new LogTextCapSink(captured).Emit(logEvent);
        return captured.Events.ShouldHaveSingleItem();
    }

    private static LogEvent EventWith(Exception? exception = null, string template = "text", params LogEventProperty[] properties) => new(
        DateTimeOffset.UnixEpoch,
        LogEventLevel.Warning,
        exception,
        new MessageTemplate([new TextToken(template)]),
        properties,
        ActivityTraceId.CreateRandom(),
        ActivitySpanId.CreateRandom());

    private static string ScalarOf(LogEventPropertyValue value) => ((ScalarValue)value).Value.ShouldBeOfType<string>();

    [Test]
    public void a_string_at_the_limit_stays_as_it_is()
    {
        // Arrange
        var atTheLimit = new string('x', TelemetryText.MaxLength);

        // Act
        var capped = TelemetryText.Cap(atTheLimit);

        // Assert
        capped.ShouldBeSameAs(atTheLimit);
    }

    [Test]
    public void a_longer_string_keeps_its_start_within_the_limit_and_the_marker_gives_its_length()
    {
        // Arrange
        var text = new string('x', 20_000_000);

        // Act
        var capped = TelemetryText.Cap(text);

        // Assert
        capped.Length.ShouldBeLessThanOrEqualTo(TelemetryText.MaxLength);
        capped.ShouldStartWith("xxx");
        capped.ShouldEndWith($"{TelemetryText.CutMarker}20000000]");
    }

    [Test]
    public void a_cut_never_splits_a_surrogate_pair()
    {
        // Arrange
        var pairs = string.Concat(Enumerable.Repeat("\U0001F600", TelemetryText.MaxLength));

        // Act
        var cuts = new[] { pairs, "x" + pairs }.Select(TelemetryText.Cap).ToList();

        // Assert
        cuts.ShouldAllBe(cut => !cut.EnumerateRunes().Any(rune => rune == Rune.ReplacementChar));
    }

    [Test]
    public void a_value_that_is_not_a_string_stays_as_it_is()
    {
        // Arrange
        var value = new object();

        // Act
        var capped = TelemetryText.Cap(value);

        // Assert
        capped.ShouldBeSameAs(value);
    }

    [Test]
    public void an_exporter_gets_a_span_with_each_string_cut()
    {
        // Arrange
        var exported = new List<Activity>();
        var sourceName = $"recall.test.{Guid.NewGuid():N}";
        using var source = new ActivitySource(sourceName);
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddProcessor(new SpanTextCapProcessor())
            .AddProcessor(new Recording(exported))
            .Build();

        // Act
        using (var span = source.StartActivity(TooLong)!)
        {
            span.SetTag("app.long", TooLong);
            span.SetTag("app.short", "kept");
            span.SetStatus(ActivityStatusCode.Error, TooLong);
            span.AddException(new InvalidOperationException(TooLong));
        }

        // Assert
        var line = exported.ShouldHaveSingleItem();
        line.DisplayName.Length.ShouldBeLessThanOrEqualTo(TelemetryText.MaxLength);
        line.StatusDescription.ShouldNotBeNull().ShouldContain(TelemetryText.CutMarker);
        line.GetTagItem("app.long").ShouldBeOfType<string>().ShouldContain(TelemetryText.CutMarker);
        line.GetTagItem("app.short").ShouldBe("kept");
        var tags = line.Events.ShouldHaveSingleItem().Tags.ToDictionary(tag => tag.Key, tag => tag.Value);
        tags["exception.message"].ShouldBeOfType<string>().ShouldContain(TelemetryText.CutMarker);
        tags["exception.stacktrace"].ShouldBeOfType<string>().ShouldContain(TelemetryText.CutMarker);
        tags["exception.stacktrace"].ShouldBeOfType<string>().Length.ShouldBeLessThanOrEqualTo(TelemetryText.MaxLength);
        tags["exception.type"].ShouldBe(typeof(InvalidOperationException).FullName);
    }

    [Test]
    public void a_short_exception_keeps_each_tag_and_a_tag_of_the_caller_wins()
    {
        // Arrange
        var exported = new List<Activity>();
        var sourceName = $"recall.test.{Guid.NewGuid():N}";
        using var source = new ActivitySource(sourceName);
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddProcessor(new SpanTextCapProcessor())
            .AddProcessor(new Recording(exported))
            .Build();

        // Act
        using (var span = source.StartActivity("span")!)
        {
            span.AddException(new InvalidOperationException("short"));
            span.AddException(new InvalidOperationException(TooLong), new TagList { { "exception.message", "from the caller" } });
        }

        // Assert
        var events = exported.ShouldHaveSingleItem().Events.ToList();
        events[0].Tags.Single(tag => tag.Key == "exception.message").Value.ShouldBe("short");
        events[0].Tags.Single(tag => tag.Key == "exception.stacktrace").Value.ShouldBeOfType<string>().ShouldNotContain(TelemetryText.CutMarker);
        events[1].Tags.Single(tag => tag.Key == "exception.message").Value.ShouldBe("from the caller");
    }

    [Test]
    public void no_tostring_of_a_tag_runs_on_the_thread_that_ends_a_span()
    {
        // Arrange
        using var processor = new SpanTextCapProcessor();
        using var span = new Activity("span");
        span.SetTag("app.bad", new FileTelemetry.ArmedTag(() => throw new InvalidOperationException("broken ToString")));

        // Act
        var end = () => processor.OnEnd(span);

        // Assert
        end.ShouldNotThrow();
    }

    [Test]
    public void a_log_event_has_each_long_string_cut_at_each_depth()
    {
        // Arrange
        var logEvent = EventWith(
            properties:
            [
                new LogEventProperty("Scalar", new ScalarValue(TooLong)),
                new LogEventProperty("Sequence", new SequenceValue([new ScalarValue(TooLong)])),
                new LogEventProperty("Structure", new StructureValue([new LogEventProperty("Inner", new ScalarValue(TooLong))], "Tag")),
                new LogEventProperty("Dictionary", new DictionaryValue([new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("key"), new ScalarValue(TooLong))])),
                new LogEventProperty("Short", new ScalarValue("kept")),
                new LogEventProperty("Number", new ScalarValue(7)),
            ]);

        // Act
        var capped = Through(logEvent);

        // Assert
        ScalarOf(capped.Properties["Scalar"]).ShouldContain(TelemetryText.CutMarker);
        ScalarOf(((SequenceValue)capped.Properties["Sequence"]).Elements.ShouldHaveSingleItem()).ShouldContain(TelemetryText.CutMarker);
        var structure = (StructureValue)capped.Properties["Structure"];
        structure.TypeTag.ShouldBe("Tag");
        ScalarOf(structure.Properties.ShouldHaveSingleItem().Value).ShouldContain(TelemetryText.CutMarker);
        ScalarOf(((DictionaryValue)capped.Properties["Dictionary"]).Elements.ShouldHaveSingleItem().Value).ShouldContain(TelemetryText.CutMarker);
        ScalarOf(capped.Properties["Short"]).ShouldBe("kept");
        ((ScalarValue)capped.Properties["Number"]).Value.ShouldBe(7);
    }

    [Test]
    public void a_log_event_keeps_its_level_time_trace_and_a_short_exception()
    {
        // Arrange
        var exception = new InvalidOperationException("short");
        var logEvent = EventWith(exception);

        // Act
        var capped = Through(logEvent);

        // Assert
        capped.Level.ShouldBe(logEvent.Level);
        capped.Timestamp.ShouldBe(logEvent.Timestamp);
        capped.TraceId.ShouldBe(logEvent.TraceId);
        capped.SpanId.ShouldBe(logEvent.SpanId);
        capped.Exception.ShouldBeSameAs(exception);
        capped.MessageTemplate.ShouldBeSameAs(logEvent.MessageTemplate);
    }

    [Test]
    public void a_long_exception_or_template_is_cut_and_the_text_still_names_the_type()
    {
        // Arrange
        var logEvent = EventWith(new InvalidOperationException(TooLong), TooLong);

        // Act
        var capped = Through(logEvent);

        // Assert
        var exception = capped.Exception.ShouldNotBeNull();
        exception.ToString().Length.ShouldBeLessThanOrEqualTo(TelemetryText.MaxLength);
        exception.ToString().ShouldStartWith(typeof(InvalidOperationException).FullName!);
        exception.ToString().ShouldContain(TelemetryText.CutMarker);
        exception.Message.Length.ShouldBeLessThanOrEqualTo(TelemetryText.MaxLength);
        capped.MessageTemplate.Text.Length.ShouldBeLessThanOrEqualTo(TelemetryText.MaxLength);
    }

    [Test]
    public void the_wrapper_of_the_settings_cuts_and_closes_the_sink_inside_it()
    {
        // Arrange
        var captured = new Captured();
        var logger = new LoggerConfiguration()
            .WriteTo.CappedText(to => to.Sink(captured))
            .CreateLogger();

        // Act
        logger.Information("{Value:l}", TooLong);
        logger.Dispose();

        // Assert
        ScalarOf(captured.Events.ShouldHaveSingleItem().Properties["Value"]).ShouldContain(TelemetryText.CutMarker);
        captured.Disposed.ShouldBeTrue();
    }
}
