namespace Recall.Server.Domain.ModelProviders.Clients;

/// <summary>
/// The way to reach one model provider. It holds the key, so it prints nothing.
/// </summary>
public sealed class ModelEndpoint(ModelProviderKind kind, string? baseUrl, string? apiKey)
{
    public ModelProviderKind Kind { get; } = kind;

    public string? BaseUrl { get; } = baseUrl;

    public string? ApiKey { get; } = apiKey;

    public override string ToString() => nameof(ModelEndpoint);
}

public sealed record ProviderModel(string Id, string DisplayName);

public enum ProviderFailureKind
{
    /// <summary>The provider answers no for this request, so the same request gets the same answer.</summary>
    Refused = 0,

    /// <summary>A later try can work: a limit, a fault of the provider, the network, or a timeout.</summary>
    Unavailable = 1,

    /// <summary>The provider answered, and the answer is not one that the app can store.</summary>
    BadOutput = 2,
}

/// <summary>
/// <see cref="DisplayMessage"/> holds the words of the provider, with the key removed. A provider can echo the text of a
/// request here. Only the test of a provider shows it, because that call sends no journal text. Never log it, and never store it.
/// </summary>
public sealed record ProviderFailure(ProviderFailureKind Kind, int? StatusCode, string DisplayMessage)
{
    public override string ToString() => $"{Kind} {StatusCode}";
}

public abstract record ProviderReply<T>
{
    private ProviderReply() { }

    public sealed record Answered(T Value) : ProviderReply<T>
    {
        public override string ToString() => nameof(Answered);
    }

    public sealed record Failed(ProviderFailure Failure) : ProviderReply<T>;
}

public interface IModelProviderClient
{
    Task<ProviderReply<IReadOnlyList<ProviderModel>>> ListModelsAsync(ModelEndpoint endpoint, CancellationToken cancellationToken);

    /// <summary>Sends one request. A retry is the work of the job, so no adapter retries.</summary>
    Task<ProviderReply<string>> CompleteAsync(ModelEndpoint endpoint, string model, string instructions, string text, CancellationToken cancellationToken);
}
