namespace Recall.Server.Domain.ModelProviders.Clients;

using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;

/// <summary>
/// The official SDK. An SDK exception can hold the body of an error and the text of a request, so no exception leaves
/// this class, and no message of one goes anywhere but the failure that the test of a provider shows.
/// </summary>
public sealed class AnthropicModelClient(IHttpClientFactory httpClientFactory)
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>The room for a request whose size the model does not limit, in tokens. Headings and the synopsis use it.</summary>
    private const int OutputTokensAboveInput = 4_000;

    private const int CharactersPerTokenAtMost = 3;

    /// <summary>The limit of one call with its whole stream. The timeout of the SDK ends at the headers.</summary>
    public TimeSpan CallLimit { get; init; } = ModelProviderHttp.CallLimit;

    public async Task<ProviderReply<IReadOnlyList<ProviderModel>>> ListModelsAsync(ModelEndpoint endpoint, CancellationToken cancellationToken)
        => await GuardedAsync<IReadOnlyList<ProviderModel>>(endpoint, async (client, limit) =>
        {
            var models = new List<ProviderModel>();
            var page = await client.Models.List(cancellationToken: limit);
            await foreach (var model in page.Paginate(limit))
                models.Add(new ProviderModel(model.ID, model.DisplayName));
            return models;
        }, cancellationToken);

    public async Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken)
        => await GuardedAsync<string>(endpoint, async (client, limit) =>
        {
            var info = await client.Models.Retrieve(new ModelRetrieveParams { ModelID = model }, limit);
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = OutputTokensFor(text, info.MaxTokens),
                System = instructions,
                Messages = [new UserMessageParam { Content = text }],
            };

            var message = await client.Messages.CreateStreaming(parameters, limit).Aggregate();
            return TextOf(message);
        }, cancellationToken);

    /// <summary>A Cleaned copy has about the length of its Raw. A cap above the size of the model gets a 400.</summary>
    public static long OutputTokensFor(string text, long? modelLimit)
    {
        long wanted = text.Length / CharactersPerTokenAtMost + OutputTokensAboveInput;
        return modelLimit is { } limit ? Math.Min(wanted, limit) : wanted;
    }

    // A cut answer ("max_tokens") or a refusal ("refusal") comes with a 200, so the stop reason decides before the text.
    private static string? TextOf(Message message)
    {
        if (message.StopReason is null || message.StopReason.Value() != StopReason.EndTurn)
            return null;

        var text = string.Concat(message.Content.Select(block => block.TryPickText(out var part) ? part.Text : string.Empty));
        return text.Length > 0 ? text : null;
    }

    private async Task<ProviderReply<T>> GuardedAsync<T>(ModelEndpoint endpoint, Func<AnthropicClient, CancellationToken, Task<T?>> call, CancellationToken cancellationToken)
        where T : class
    {
        using var client = ClientFor(endpoint);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(CallLimit);
        try
        {
            return await call(client, limit.Token) is { } value
                ? new ProviderReply<T>.Answered(value)
                : new ProviderReply<T>.Failed(ProviderFailureText.Unusable());
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A stop of the host returns the lease, so this is no failure of the provider, and the SDK can wrap it.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (limit.IsCancellationRequested)
        {
            return new ProviderReply<T>.Failed(ProviderFailureText.NoAnswer());
        }
        catch (AnthropicApiException fault)
        {
            return new ProviderReply<T>.Failed(ProviderFailureText.ForStatus((int)fault.StatusCode, fault.ResponseBody, endpoint.ApiKey));
        }
        catch (AnthropicInvalidDataException)
        {
            return new ProviderReply<T>.Failed(ProviderFailureText.Unusable());
        }
        catch (Exception fault) when (fault is AnthropicException or HttpRequestException or TaskCanceledException or IOException)
        {
            return new ProviderReply<T>.Failed(ProviderFailureText.NoAnswer());
        }
    }

    private AnthropicClient ClientFor(ModelEndpoint endpoint) => new()
    {
        ApiKey = endpoint.ApiKey,
        AuthToken = null,
        BaseUrl = endpoint.BaseUrl ?? DefaultBaseUrl,
        MaxRetries = 0,
        OpenTelemetry = false,
        Timeout = ModelProviderHttp.CallLimit,
        HttpClient = httpClientFactory.CreateClient(ModelProviderHttp.ClientName),
    };
}
