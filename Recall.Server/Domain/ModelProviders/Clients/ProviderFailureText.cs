namespace Recall.Server.Domain.ModelProviders.Clients;

using System.Text.Json;

public static class ProviderFailureText
{
    private const int MaxLength = 300;
    private const string Removed = "[removed]";

    /// <summary>
    /// The message of an error body. Both wire formats keep it at <c>error.message</c>. A body in any other form is not
    /// shown, because it can be a page of a proxy.
    /// </summary>
    public static string? MessageIn(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
            return null;

        try
        {
            using var document = JsonDocument.Parse(errorBody);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Shown(string? message, string? apiKey, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
            return fallback;

        var scrubbed = string.IsNullOrEmpty(apiKey) ? message : string.Join(Removed, message.Split(apiKey));
        return scrubbed.Length <= MaxLength ? scrubbed : scrubbed[..MaxLength];
    }

    public static ProviderFailure ForStatus(int status, string? errorBody, string? apiKey)
    {
        var kind = status is 408 or 409 or 429 or >= 500 ? ProviderFailureKind.Unavailable : ProviderFailureKind.Refused;
        return new ProviderFailure(kind, status, Shown(MessageIn(errorBody), apiKey, $"The model provider answered with the status {status}."));
    }

    public static ProviderFailure NoAnswer()
        => new(ProviderFailureKind.Unavailable, null, "The model provider did not answer.");

    public static ProviderFailure Unusable()
        => new(ProviderFailureKind.BadOutput, null, "The answer of the model provider is not one that the app can use.");
}
