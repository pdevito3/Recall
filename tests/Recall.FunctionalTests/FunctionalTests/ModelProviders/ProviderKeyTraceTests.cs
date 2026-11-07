namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recall.FakeModelProvider;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Clients;
using Shouldly;

/// <summary>The real client of the app at its lowest log level and with each trace on. The key of a provider must reach neither.</summary>
public class ProviderKeyTraceTests
{
    private sealed class EachRecord(ConcurrentQueue<string> records) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            records.Enqueue($"{formatter(state, exception)} {exception} {string.Join(' ', values.Select(pair => $"{pair.Key}={Shown(pair.Value)}"))}");
        }

        private static string Shown(object? value) => value is IEnumerable<string> many ? string.Join(',', many) : $"{value}";

        public void Dispose() { }
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task the_key_reaches_no_log_record_and_no_trace(ModelProviderKind kind)
    {
        // Arrange
        var records = new ConcurrentQueue<string>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new EachRecord(records)));
        services.AddDataProtection();
        services.AddModelProviders();
        await using var provider = services.BuildServiceProvider();

        var spans = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => spans.Enqueue($"{span.DisplayName} {string.Join(' ', span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))} {string.Join(' ', span.Events.Select(item => item.Name))}"),
        };
        ActivitySource.AddActivityListener(listener);

        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        var endpoint = new ModelEndpoint(kind, CleanupScenario.BaseUrlOf(fake, kind), key);
        var client = provider.GetRequiredService<IModelProviderClient>();

        // Act
        (await client.ListModelsAsync(endpoint, CancellationToken.None)).ShouldBeOfType<ProviderReply<IReadOnlyList<ProviderModel>>.Answered>();
        (await client.CompleteAsync(endpoint, FakeModelProvider.ModelId, "Clean this up.", "some words", CancellationToken.None)).ShouldBeOfType<ProviderReply<string>.Answered>();

        // Assert
        var bytes = Encoding.UTF8.GetBytes(key);
        string[] forms = [key, Convert.ToBase64String(bytes), Convert.ToHexString(bytes), Uri.EscapeDataString(key)];
        foreach (var record in records.Concat(spans))
        foreach (var form in forms)
            record.ShouldNotContain(form, Case.Insensitive);
    }
}
