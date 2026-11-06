namespace Recall.ContractTests;

using Recall.FunctionalTests;

public static class AssemblyHooks
{
    private static TestingWebApplicationFactory? _factory;

    [Before(Assembly)]
    public static async Task StartAsync()
    {
        _factory = new TestingWebApplicationFactory();
        await _factory.InitializeAsync();
    }

    [After(Assembly)]
    public static async Task StopAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
    }
}
