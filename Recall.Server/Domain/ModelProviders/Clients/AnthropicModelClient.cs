namespace Recall.Server.Domain.ModelProviders.Clients;

using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;

/// <summary>
/// The official SDK. An SDK exception can hold the body of an error and the text of a request, so no exception leaves
/// this class, and no message of one goes anywhere but the failure that the test of a provider shows.
/// </summary>
public sealed class AnthropicModelClient(IHttpClientFactory httpClientFactory, ProviderLimits limits)
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>The room for a request whose size the model does not limit, in tokens. Headings and the synopsis use it.</summary>
    private const int OutputTokensAboveInput = 4_000;

    private const int CharactersPerTokenAtMost = 3;

    public async Task<ProviderReply<IReadOnlyList<ProviderModel>>> ListModelsAsync(ModelEndpoint endpoint, CancellationToken cancellationToken)
        => await GuardedAsync<IReadOnlyList<ProviderModel>>(endpoint, limits.Listing, async (client, limit) =>
        {
            // "has_more" comes from the provider, so a page limit ends a list that never ends.
            var models = new List<ProviderModel>();
            string? after = null;
            for (var page = 0; page < limits.ModelPages && models.Count < limits.Models; page++)
            {
                var part = await client.Models.List(new ModelListParams { Limit = limits.ModelsInAPage, AfterID = after }, limit);
                models.AddRange(part.Items.Select(model => new ProviderModel(model.ID, model.DisplayName)));
                if (!part.HasNext() || part.Items.Count == 0)
                    break;
                after = part.Items[^1].ID;
            }

            return models;
        }, cancellationToken);

    public async Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken)
        => await GuardedAsync<string>(endpoint, limits.Call, async (client, limit) =>
        {
            var info = await client.Models.Retrieve(new ModelRetrieveParams { ModelID = model }, limit);
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = OutputTokensFor(text, info.MaxTokens),
                System = instructions,
                Messages = [new UserMessageParam { Content = text }],
            };

            return await WordWatch.RunAsync(limits.NoWords, watch => TextOfStreamAsync(client.Messages.CreateStreaming(parameters, watch.Token), watch), limit);
        }, cancellationToken);

    /// <summary>A Cleaned copy has about the length of its Raw. A cap above the size of the model gets a 400.</summary>
    public static long OutputTokensFor(string text, long? modelLimit)
    {
        long wanted = text.Length / CharactersPerTokenAtMost + OutputTokensAboveInput;
        return modelLimit is { } limit ? Math.Min(wanted, limit) : wanted;
    }

    // The aggregator of the SDK holds each event in memory, so this keeps only the words. A cut answer ("max_tokens") or a
    // refusal ("refusal") comes with a 200, so the stop reason decides. A stream that ends with no stop reason was cut on the
    // way, and a later try can work. A 200 with no event at all is some other service, and the same bytes come again.
    private async Task<string?> TextOfStreamAsync(IAsyncEnumerable<RawMessageStreamEvent> stream, WordWatch watch)
    {
        var words = new StringBuilder();
        var hasEvent = false;
        StopReason? stopReason = null;
        await foreach (var part in stream.WithCancellation(watch.Token))
        {
            hasEvent = true;
            if (part.TryPickContentBlockDelta(out var block))
            {
                if (block.Delta.TryPickThinking(out var thought))
                    watch.Saw(thought.Thinking);
                else if (block.Delta.TryPickSignature(out var signature))
                    watch.Saw(signature.Signature);
                else if (block.Delta.TryPickText(out var delta))
                {
                    words.Append(delta.Text);
                    watch.Saw(delta.Text);
                    if (words.Length > limits.AnswerCharacters)
                        throw new ProviderAnswerTooLargeException();
                }
            }
            else if (part.TryPickDelta(out var end) && end.Delta.StopReason is { } reason)
            {
                stopReason = reason.Value();
            }
        }

        if (stopReason is null && hasEvent)
            throw new ProviderSilentException();

        return stopReason == StopReason.EndTurn && words.Length > 0 ? words.ToString() : null;
    }

    private async Task<ProviderReply<T>> GuardedAsync<T>(ModelEndpoint endpoint, TimeSpan callLimit, Func<AnthropicClient, CancellationToken, Task<T?>> call, CancellationToken cancellationToken)
        where T : class
        => await ProviderCall.RunAsync<T>(callLimit, async limit =>
        {
            using var client = ClientFor(endpoint, callLimit);
            try
            {
                return await call(client, limit) is { } value
                    ? new ProviderReply<T>.Answered(value)
                    : new ProviderReply<T>.Failed(ProviderFailureText.Unusable());
            }
            catch (AnthropicApiException fault) when (!limit.IsCancellationRequested)
            {
                return new ProviderReply<T>.Failed(ProviderFailureText.ForStatus(ModelProviderKind.Anthropic, (int)fault.StatusCode, fault.ResponseBody, endpoint.ApiKey));
            }
        }, cancellationToken);

    private AnthropicClient ClientFor(ModelEndpoint endpoint, TimeSpan callLimit) => new()
    {
        ApiKey = endpoint.ApiKey,
        AuthToken = null,
        BaseUrl = endpoint.BaseUrl ?? DefaultBaseUrl,
        MaxRetries = 0,
        OpenTelemetry = false,
        Timeout = callLimit,
        HttpClient = httpClientFactory.CreateClient(ModelProviderHttp.ClientName),
    };
}
