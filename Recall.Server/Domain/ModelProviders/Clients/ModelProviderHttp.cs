namespace Recall.Server.Domain.ModelProviders.Clients;

using Databases;

public static class ModelProviderHttp
{
    public const string ClientName = "model-provider";

    public static void AddModelProviders(this IServiceCollection services)
    {
        services.AddSingleton<ApiKeyProtector>();
        services.AddSingleton<KeyRingStatus>();
        services.AddHostedService<KeyRingWatch>();
        services.AddHealthChecks().AddCheck<KeyRingHealthCheck>(KeyRingStatus.HealthCheckName);
        services.AddSingleton<ProviderLimits>();
        services.AddTransient<ProviderReadGuard>();
        services.AddSingleton<OpenAiCompatibleClient>();
        services.AddSingleton<AnthropicModelClient>();
        services.AddSingleton<IModelProviderClient, ModelProviderClient>();

#pragma warning disable EXTEXP0001 // The call is the documented way to take the retries of the defaults off one client.
        services.AddHttpClient(ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan) // Each adapter sets the limit of its call.
            .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
            {
                // A redirect sends the key to another address, so the client does not follow one.
                AllowAutoRedirect = false,
                // A proxy would make the connect, and the address rule would see only the proxy.
                UseProxy = false,
                ConnectTimeout = provider.GetRequiredService<ProviderLimits>().Connect,
                ConnectCallback = ProviderAddressRule.ConnectAsync,
            })
            .AddHttpMessageHandler<ProviderReadGuard>()
            // The logger of a client writes each header at its lowest level, and that level reaches a file and OTLP. A header here holds the key.
            .RemoveAllLoggers()
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }
}

public sealed class ModelProviderClient(AnthropicModelClient anthropic, OpenAiCompatibleClient openAiCompatible, ProviderLimits limits) : IModelProviderClient
{
    public async Task<ProviderReply<IReadOnlyList<ProviderModel>>> ListModelsAsync(ModelEndpoint endpoint, CancellationToken cancellationToken)
    {
        var reply = await (endpoint.Kind switch
        {
            ModelProviderKind.Anthropic => anthropic.ListModelsAsync(endpoint, cancellationToken),
            ModelProviderKind.OpenAiCompatible => openAiCompatible.ListModelsAsync(endpoint, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        });

        return reply is ProviderReply<IReadOnlyList<ProviderModel>>.Answered answered
            ? new ProviderReply<IReadOnlyList<ProviderModel>>.Answered(UsableOf(answered.Value, endpoint.ApiKey))
            : reply;
    }

    // A person picks from this list, and the choice goes to the store. An ID with bad bytes, a hidden character, or the key is no
    // ID of a model. A provider can echo the key in any field, and this list reaches a page.
    private List<ProviderModel> UsableOf(IReadOnlyList<ProviderModel> models, string? apiKey) => models
        .Where(model => CanList(model.Id, apiKey))
        .Select(model => model with { DisplayName = CanList(model.DisplayName, apiKey) ? model.DisplayName : model.Id })
        .Take(limits.Models)
        .ToList();

    private static bool CanList(string value, string? apiKey)
        => value.Length <= ModelProvider.ModelMaxLength && VisibleText.IsClean(value) && !ProviderFailureText.Holds(value, apiKey);

    public Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken)
        => endpoint.Kind switch
        {
            ModelProviderKind.Anthropic => anthropic.CompleteAsync(endpoint, model, instructions, text, cancellationToken),
            ModelProviderKind.OpenAiCompatible => openAiCompatible.CompleteAsync(endpoint, model, instructions, text, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };
}
