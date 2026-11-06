namespace Recall.Server.Domain.ModelProviders.Clients;

public static class ModelProviderHttp
{
    public const string ClientName = "model-provider";

    /// <summary>A model can take minutes on a long Session. The job retries, so no handler retries.</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromMinutes(10);

    // A Cleaned copy of the longest Draft, with an escape for each character and room for the fields around it.
    private const long MaxBufferedResponseBytes = 16 * 1024 * 1024;

    public static void AddModelProviders(this IServiceCollection services)
    {
        services.AddSingleton<ApiKeyProtector>();
        services.AddSingleton<KeyRingStatus>();
        services.AddHostedService<KeyRingWatch>();
        services.AddHealthChecks().AddCheck<KeyRingHealthCheck>(KeyRingStatus.HealthCheckName);
        services.AddSingleton<OpenAiCompatibleClient>();
        services.AddSingleton<AnthropicModelClient>();
        services.AddSingleton<IModelProviderClient, ModelProviderClient>();

#pragma warning disable EXTEXP0001 // The call is the documented way to take the retries of the defaults off one client.
        services.AddHttpClient(ClientName, client =>
            {
                client.Timeout = CallLimit;
                client.MaxResponseContentBufferSize = MaxBufferedResponseBytes;
            })
            // A redirect sends the key to another address, so the client does not follow one.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }
}

public sealed class ModelProviderClient(AnthropicModelClient anthropic, OpenAiCompatibleClient openAiCompatible) : IModelProviderClient
{
    public Task<ProviderReply<IReadOnlyList<ProviderModel>>> ListModelsAsync(ModelEndpoint endpoint, CancellationToken cancellationToken)
        => endpoint.Kind switch
        {
            ModelProviderKind.Anthropic => anthropic.ListModelsAsync(endpoint, cancellationToken),
            ModelProviderKind.OpenAiCompatible => openAiCompatible.ListModelsAsync(endpoint, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };

    public Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken)
        => endpoint.Kind switch
        {
            ModelProviderKind.Anthropic => anthropic.CompleteAsync(endpoint, model, instructions, text, cancellationToken),
            ModelProviderKind.OpenAiCompatible => openAiCompatible.CompleteAsync(endpoint, model, instructions, text, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };
}
