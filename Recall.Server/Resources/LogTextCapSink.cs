namespace Recall.Server.Resources;

using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

/// <summary>
/// Gives each sink inside it a log event with each string under <see cref="TelemetryText.MaxLength"/>. The event is a copy,
/// because two sinks can read one event at the same time. A scalar that is not a string stays as it is, so a long
/// <see cref="Uri"/> stays long. docs/telemetry.md names what is outside the cap.
/// </summary>
public sealed class LogTextCapSink(ILogEventSink next) : ILogEventSink
{
    public void Emit(LogEvent logEvent) => next.Emit(new LogEvent(
        logEvent.Timestamp,
        logEvent.Level,
        Cap(logEvent.Exception),
        Cap(logEvent.MessageTemplate),
        logEvent.Properties.Select(property => new LogEventProperty(property.Key, Cap(property.Value))),
        logEvent.TraceId ?? default,
        logEvent.SpanId ?? default));

    private static Exception? Cap(Exception? exception)
    {
        if (exception is null)
            return null;

        var text = exception.ToString();
        return text.Length <= TelemetryText.MaxLength && exception.Message.Length <= TelemetryText.MaxLength
            ? exception
            : new CutException(exception.Message, text);
    }

    private static MessageTemplate Cap(MessageTemplate template) =>
        template.Text.Length <= TelemetryText.MaxLength
            ? template
            : new MessageTemplate([new TextToken(TelemetryText.Cap(template.Text))]);

    private static LogEventPropertyValue Cap(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string { Length: > TelemetryText.MaxLength } text } => new ScalarValue(TelemetryText.Cap(text)),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(Cap)),
        StructureValue structure => new StructureValue(
            structure.Properties.Select(property => new LogEventProperty(property.Name, Cap(property.Value))),
            structure.TypeTag),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(element =>
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(element.Key, Cap(element.Value)))),
        _ => value,
    };

    /// <summary>Takes the place of an exception whose text is too long. The first words of its text still name the real type.</summary>
    private sealed class CutException(string message, string text) : Exception(TelemetryText.Cap(message))
    {
        private readonly string _text = TelemetryText.Cap(text);

        public override string ToString() => _text;
    }
}

public static class LogTextCapSinkExtensions
{
    /// <summary>The settings also call this, by the name of this method, to put the console sink inside it.</summary>
    public static LoggerConfiguration CappedText(this LoggerSinkConfiguration writeTo, Action<LoggerSinkConfiguration> configure) =>
        writeTo.Sink(LoggerSinkConfiguration.Wrap(sink => new LogTextCapSink(sink), configure));
}
