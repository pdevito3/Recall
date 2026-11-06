namespace Recall.Server.Domain.ModelProviders;

using Exceptions;

public static class ModelProviderAddress
{
    private const string BaseUrlField = "baseUrl";

    /// <summary>
    /// A client span and a log can hold the URL of a call, so the address holds no credential and no query. It is null
    /// when the caller sends none.
    /// </summary>
    public static string? Normalized(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var trimmed = baseUrl.Trim();
        ValidationException.ThrowWhenLongerThan(trimmed, ModelProvider.BaseUrlMaxLength, BaseUrlField, $"The base URL must not have more than {ModelProvider.BaseUrlMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(trimmed, BaseUrlField, "The base URL has a character that is not allowed.");
        var isUsable = Uri.TryCreate(trimmed, UriKind.Absolute, out var address)
            && address.Scheme is "http" or "https"
            && address.UserInfo.Length == 0
            && address.Query.Length == 0
            && address.Fragment.Length == 0;
        ValidationException.Must(isUsable, BaseUrlField, "The base URL must be an http or https address with no user name, no query, and no fragment.");
        return trimmed.TrimEnd('/');
    }
}
