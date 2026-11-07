namespace Recall.Server.Domain.ModelProviders.Clients;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The wire format of <c>GET {base}/models</c> and <c>POST {base}/chat/completions</c>. A completion asks for a stream, because
/// only a stream shows the progress of a model that works for minutes. A server that ignores the request answers with one
/// object, and the idle limit then bounds its whole answer.
/// </summary>
public sealed class OpenAiCompatibleClient(IHttpClientFactory httpClientFactory, ProviderLimits limits)
{
    private const string StreamMediaType = "text/event-stream";
    private const string DataPrefix = "data:";
    private const string EndOfStream = "[DONE]";

    public async Task<ProviderReply<IReadOnlyList<ProviderModel>>> ListModelsAsync(ModelEndpoint endpoint, CancellationToken cancellationToken)
        => await ProviderCall.RunAsync<IReadOnlyList<ProviderModel>>(limits.Listing, async limit =>
        {
            using var request = RequestTo(endpoint, HttpMethod.Get, "models");
            using var response = await SendAsync(request, limit);
            var body = await response.Content.ReadAsStringAsync(limit);
            if (!response.IsSuccessStatusCode)
                return new ProviderReply<IReadOnlyList<ProviderModel>>.Failed(ProviderFailureText.ForStatus(endpoint.Kind, (int)response.StatusCode, body, endpoint.ApiKey));

            return JsonSerializer.Deserialize<ModelList>(body)?.Data is { } data
                ? new ProviderReply<IReadOnlyList<ProviderModel>>.Answered(data.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => new ProviderModel(m.Id!, m.Id!)).ToList())
                : new ProviderReply<IReadOnlyList<ProviderModel>>.Failed(ProviderFailureText.Unusable());
        }, cancellationToken);

    public async Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken)
        => await ProviderCall.RunAsync<string>(limits.Call, limit => WordWatch.RunAsync<ProviderReply<string>>(limits.NoWords, async watch =>
        {
            using var request = RequestTo(endpoint, HttpMethod.Post, "chat/completions");
            request.Content = JsonContent.Create(new
            {
                model,
                stream = true,
                messages = new[]
                {
                    new { role = "system", content = instructions },
                    new { role = "user", content = text },
                },
            });

            using var response = await SendAsync(request, watch.Token);
            if (!response.IsSuccessStatusCode)
                return new ProviderReply<string>.Failed(ProviderFailureText.ForStatus(endpoint.Kind, (int)response.StatusCode, await response.Content.ReadAsStringAsync(watch.Token), endpoint.ApiKey));

            var answer = response.Content.Headers.ContentType?.MediaType == StreamMediaType
                ? await TextOfStreamAsync(response, watch)
                : TextOf(JsonSerializer.Deserialize<ChatCompletion>(await response.Content.ReadAsStringAsync(watch.Token)));
            return answer is not null
                ? new ProviderReply<string>.Answered(answer)
                : new ProviderReply<string>.Failed(ProviderFailureText.Unusable());
        }, limit), cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => await httpClientFactory.CreateClient(ModelProviderHttp.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    // Only a natural stop holds the whole answer. "length" is a cut answer, and "content_filter" left words out.
    private static string? TextOf(ChatCompletion? completion)
        => completion?.Choices is [{ FinishReason: "stop", Message.Content: { Length: > 0 } content }, ..] ? content : null;

    // A comment line is a keep-alive, a chunk can have no choice, and its content can be null. A stream that ends with no
    // finish reason was cut on the way, and a later try can work. A 200 with no event at all is some other service, and the
    // same bytes come again.
    private async Task<string?> TextOfStreamAsync(HttpResponseMessage response, WordWatch watch)
    {
        await using var body = await response.Content.ReadAsStreamAsync(watch.Token);
        using var reader = new StreamReader(body, Encoding.UTF8);
        var words = new StringBuilder();
        var hasEvent = false;
        string? finishReason = null;
        while (await reader.ReadLineAsync(watch.Token) is { } line)
        {
            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
                continue;

            hasEvent = true;
            var data = line[DataPrefix.Length..].Trim();
            if (data == EndOfStream)
                break;

            var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
            if (chunk?.Error is not null)
                throw new ProviderSilentException();
            if (chunk?.Choices is not [var choice, ..])
                continue;

            words.Append(choice.Delta?.Content);
            watch.Saw(choice.Delta?.Content);
            watch.Saw(ThoughtIn(choice.Delta?.ReasoningContent));
            watch.Saw(ThoughtIn(choice.Delta?.Reasoning));
            if (words.Length > limits.AnswerCharacters)
                throw new ProviderAnswerTooLargeException();
            finishReason = choice.FinishReason ?? finishReason;
        }

        if (finishReason is null && hasEvent)
            throw new ProviderSilentException();

        return finishReason == "stop" && words.Length > 0 ? words.ToString() : null;
    }

    // Servers differ on the form of a thought: a string or an object. Only the progress matters, so any form that holds a word counts,
    // and an empty object or a list of empty strings in each keep-alive does not.
    private static string? ThoughtIn(JsonElement? thought) => thought is { } value && HoldsWords(value) ? value.GetRawText() : null;

    private static bool HoldsWords(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Object => value.EnumerateObject().Any(field => HoldsWords(field.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(HoldsWords),
        _ => false,
    };

    private static HttpRequestMessage RequestTo(ModelEndpoint endpoint, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{endpoint.BaseUrl}/{path}");
        if (endpoint.ApiKey is { } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private sealed record ModelList([property: JsonPropertyName("data")] IReadOnlyList<ModelRow>? Data);

    private sealed record ModelRow([property: JsonPropertyName("id")] string? Id);

    private sealed record ChatCompletion([property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices);

    private sealed record Choice(
        [property: JsonPropertyName("message")] ChoiceMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChoiceMessage(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("reasoning_content")] JsonElement? ReasoningContent = null,
        [property: JsonPropertyName("reasoning")] JsonElement? Reasoning = null);

    private sealed record StreamChunk(
        [property: JsonPropertyName("choices")] IReadOnlyList<StreamChoice>? Choices,
        [property: JsonPropertyName("error")] JsonElement? Error);

    private sealed record StreamChoice(
        [property: JsonPropertyName("delta")] ChoiceMessage? Delta,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
}
