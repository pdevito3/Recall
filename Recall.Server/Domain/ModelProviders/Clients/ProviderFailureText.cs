namespace Recall.Server.Domain.ModelProviders.Clients;

using System.Net;
using System.Text;
using System.Text.Json;

public static class ProviderFailureText
{
    private const int MaxLength = 300;
    private const string Removed = "[removed]";

    /// <summary>A shorter run in a message is no proof that it is a cut of the key.</summary>
    private const int ShortestCutOfKey = 8;

    /// <summary>
    /// The message of an error body, when the body has the error format of that provider. A body in any other form is not
    /// shown, because the address can be another service on the network, and its words must not reach the person.
    /// </summary>
    public static string? MessageIn(ModelProviderKind kind, string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
            return null;

        try
        {
            using var document = JsonDocument.Parse(errorBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.String)
                return null;

            var hasFormat = kind == ModelProviderKind.Anthropic
                ? IsString(root, "type", "error") && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                : error.TryGetProperty("type", out _) || error.TryGetProperty("code", out _);
            return hasFormat ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsString(JsonElement element, string name, string value)
        => element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == value;

    public static string Shown(string? message, string? apiKey, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
            return fallback;

        var plain = VisibleText.Without(message);
        if (string.IsNullOrEmpty(apiKey))
            return plain.Length <= MaxLength ? plain : plain[..MaxLength];

        // A cut at the limit can split a form of the key, so the scrub runs on the whole message and the cut comes after it.
        var scrubbed = FormsOf(apiKey).Aggregate(plain, Scrubbed);
        return scrubbed.Length <= MaxLength ? scrubbed : scrubbed[..MaxLength];
    }

    private static IEnumerable<string> FormsOf(string apiKey)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(apiKey));
        yield return apiKey;
        yield return base64;
        yield return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        yield return Uri.EscapeDataString(apiKey);
        yield return WebUtility.HtmlEncode(apiKey);
    }

    /// <summary>Removes the form in any case, and each run of the message that is a cut of the form.</summary>
    private static string Scrubbed(string message, string form)
    {
        var shortest = Math.Min(ShortestCutOfKey, form.Length);
        var result = new StringBuilder(message.Length);
        var at = 0;
        while (at < message.Length)
        {
            var run = LongestRunInForm(message, at, form, shortest);
            if (run == 0)
            {
                result.Append(message[at]);
                at++;
                continue;
            }

            result.Append(Removed);
            at += run;
        }

        return result.ToString();
    }

    private static int LongestRunInForm(string message, int at, string form, int shortest)
    {
        if (at + shortest > message.Length || !form.Contains(message.AsSpan(at, shortest), StringComparison.OrdinalIgnoreCase))
            return 0;

        var run = shortest;
        while (at + run < message.Length && form.Contains(message.AsSpan(at, run + 1), StringComparison.OrdinalIgnoreCase))
            run++;
        return run;
    }

    public static ProviderFailure ForStatus(ModelProviderKind kind, int status, string? errorBody, string? apiKey)
    {
        var failureKind = status is 408 or 409 or 429 or >= 500 ? ProviderFailureKind.Unavailable : ProviderFailureKind.Refused;
        return new ProviderFailure(failureKind, status, Shown(MessageIn(kind, errorBody), apiKey, $"The model provider answered with the status {status}."));
    }

    public static ProviderFailure NoAnswer()
        => new(ProviderFailureKind.Unavailable, null, "The model provider did not answer.");

    public static ProviderFailure Unusable()
        => new(ProviderFailureKind.BadOutput, null, "The answer of the model provider is not one that the app can use.");

    public static ProviderFailure AddressRefused()
        => new(ProviderFailureKind.Refused, null, "The app does not connect to this address. Link-local, metadata, and multicast addresses are refused, and a loopback address in a container is the app itself.");
}
