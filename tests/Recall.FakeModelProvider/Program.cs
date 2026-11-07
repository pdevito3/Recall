namespace Recall.FakeModelProvider;

/// <summary>
/// Usage: dotnet run --project tests/Recall.FakeModelProvider -- [url]. The key comes from the variable
/// FAKE_PROVIDER_KEY, so that it is in no file and no process list.
/// </summary>
internal static class FakeModelProviderHost
{
    private static async Task Main(string[] args)
    {
        var url = args.Length > 0 ? args[0] : "http://0.0.0.0:18099";
        var key = Environment.GetEnvironmentVariable("FAKE_PROVIDER_KEY")
            ?? throw new InvalidOperationException("Set FAKE_PROVIDER_KEY.");

        await using var fake = await FakeModelProvider.StartAsync(key, url);
        await Console.Out.WriteLineAsync($"Fake model provider at {url}. Anthropic base: /, compatible base: /compat/v1. Control: /control");
        await fake.WaitForStopAsync();
    }
}
