namespace Recall.Server.Domain.ModelProviders.Clients;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>The wire format of <c>GET {base}/models</c> and <c>POST {base}/chat/completions</c>.</summary>
public sealed class OpenAiCompatibleClient(IHttpClientFactory httpClientFactory)
{
    public async Task<ProviderReply<IReadOnlyList<ProviderModel>>> ListModelsAsync(ModelEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var request = RequestTo(endpoint, HttpMethod.Get, "models");
        return await SendAsync<ModelList, IReadOnlyList<ProviderModel>>(endpoint, request, list => list.Data is { } data
            ? data.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => new ProviderModel(m.Id!, m.Id!)).ToList()
            : null, cancellationToken);
    }

    public async Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken)
    {
        using var request = RequestTo(endpoint, HttpMethod.Post, "chat/completions");
        request.Content = JsonContent.Create(new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = instructions },
                new { role = "user", content = text },
            },
        });

        return await SendAsync<ChatCompletion, string>(endpoint, request, TextOf, cancellationToken);
    }

    // Only a natural stop holds the whole answer. "length" is a cut answer, and "content_filter" left words out.
    private static string? TextOf(ChatCompletion completion)
        => completion.Choices is [{ FinishReason: "stop", Message.Content: { Length: > 0 } content }, ..] ? content : null;

    private static HttpRequestMessage RequestTo(ModelEndpoint endpoint, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{endpoint.BaseUrl}/{path}");
        if (endpoint.ApiKey is { } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private async Task<ProviderReply<TValue>> SendAsync<TWire, TValue>(
        ModelEndpoint endpoint, HttpRequestMessage request, Func<TWire, TValue?> read, CancellationToken cancellationToken)
        where TValue : class
    {
        var http = httpClientFactory.CreateClient(ModelProviderHttp.ClientName);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new ProviderReply<TValue>.Failed(ProviderFailureText.ForStatus((int)response.StatusCode, body, endpoint.ApiKey));

            return JsonSerializer.Deserialize<TWire>(body) is { } wire && read(wire) is { } value
                ? new ProviderReply<TValue>.Answered(value)
                : new ProviderReply<TValue>.Failed(ProviderFailureText.Unusable());
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A stop of the host returns the lease, so this is no failure of the provider.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (JsonException)
        {
            return new ProviderReply<TValue>.Failed(ProviderFailureText.Unusable());
        }
        catch (Exception fault) when (fault is HttpRequestException or TaskCanceledException or IOException)
        {
            return new ProviderReply<TValue>.Failed(ProviderFailureText.NoAnswer());
        }
    }

    private sealed record ModelList([property: JsonPropertyName("data")] IReadOnlyList<ModelRow>? Data);

    private sealed record ModelRow([property: JsonPropertyName("id")] string? Id);

    private sealed record ChatCompletion([property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices);

    private sealed record Choice(
        [property: JsonPropertyName("message")] ChoiceMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChoiceMessage([property: JsonPropertyName("content")] string? Content);
}
