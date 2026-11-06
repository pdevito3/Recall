namespace Recall.FunctionalTests.Auth;

using System.Diagnostics.CodeAnalysis;
using Serilog;
using Serilog.Core;
using Serilog.Events;

internal sealed class TeeLogger(ILogger first, ILogger second) : ILogger
{
    public ILogger ForContext(ILogEventEnricher enricher)
        => new TeeLogger(first.ForContext(enricher), second.ForContext(enricher));

    public ILogger ForContext(IEnumerable<ILogEventEnricher> enrichers)
        => new TeeLogger(first.ForContext(enrichers), second.ForContext(enrichers));

    public ILogger ForContext(string propertyName, object? value, bool destructureObjects = false)
        => new TeeLogger(first.ForContext(propertyName, value, destructureObjects), second.ForContext(propertyName, value, destructureObjects));

    public ILogger ForContext<TSource>() => new TeeLogger(first.ForContext<TSource>(), second.ForContext<TSource>());

    public ILogger ForContext(Type source) => new TeeLogger(first.ForContext(source), second.ForContext(source));

    public bool IsEnabled(LogEventLevel level) => first.IsEnabled(level) || second.IsEnabled(level);

    public void Write(LogEvent logEvent)
    {
        first.Write(logEvent);
        second.Write(logEvent);
    }

    public bool BindMessageTemplate(
        string messageTemplate,
        object?[]? propertyValues,
        [NotNullWhen(true)] out MessageTemplate? parsedTemplate,
        [NotNullWhen(true)] out IEnumerable<LogEventProperty>? boundProperties)
        => (first.IsEnabled(LogEventLevel.Fatal) ? first : second).BindMessageTemplate(messageTemplate, propertyValues, out parsedTemplate, out boundProperties);

    public bool BindProperty(string? propertyName, object? value, bool destructureObjects, [NotNullWhen(true)] out LogEventProperty? property)
        => first.BindProperty(propertyName, value, destructureObjects, out property);
}
