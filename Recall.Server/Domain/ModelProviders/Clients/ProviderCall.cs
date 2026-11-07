namespace Recall.Server.Domain.ModelProviders.Clients;

using System.Net.Sockets;
using System.Text.Json;
using Anthropic.Exceptions;

/// <summary>The provider sent no byte in the idle limit, or its stream ended before its end.</summary>
public sealed class ProviderSilentException : Exception;

/// <summary>The answer is above a limit of <see cref="ProviderLimits"/>. The same request gets the same answer.</summary>
public sealed class ProviderAnswerTooLargeException : Exception;

/// <summary>The address of the provider is one that <see cref="ProviderAddressRule"/> refuses.</summary>
public sealed class ProviderAddressRefusedException : Exception;

/// <summary>The one way that an adapter runs a call: each fault of the provider becomes a failure, and no exception leaves.</summary>
public static class ProviderCall
{
    public static async Task<ProviderReply<T>> RunAsync<T>(TimeSpan limit, Func<CancellationToken, Task<ProviderReply<T>>> call, CancellationToken cancellationToken)
    {
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        whole.CancelAfter(limit);
        try
        {
            return await call(whole.Token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A stop of the host returns the lease, so this is no failure of the provider, and an SDK can wrap it.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception fault)
        {
            return new ProviderReply<T>.Failed(whole.IsCancellationRequested ? ProviderFailureText.NoAnswer() : FailureFor(fault));
        }
    }

    /// <summary>
    /// A later try can help only when the network or the provider is at fault. Any other fault means that the bytes of the
    /// answer are not ones that the app can read, and the same bytes come again. The first known type in the chain decides,
    /// because the HTTP stack and an SDK wrap what they catch.
    /// </summary>
    public static ProviderFailure FailureFor(Exception fault)
    {
        var chain = ChainOf(fault).ToList();
        if (chain.Any(link => link is ProviderAddressRefusedException))
            return ProviderFailureText.AddressRefused();
        if (chain.Any(link => link is ProviderAnswerTooLargeException or JsonException or AnthropicInvalidDataException))
            return ProviderFailureText.Unusable();
        if (chain.Any(link => link is HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse }))
            return ProviderFailureText.Unusable();
        return chain.Any(IsTransport) ? ProviderFailureText.NoAnswer() : ProviderFailureText.Unusable();
    }

    private static bool IsTransport(Exception link)
        => link is ProviderSilentException or HttpRequestException or IOException or SocketException or TimeoutException or OperationCanceledException or AnthropicException;

    private static IEnumerable<Exception> ChainOf(Exception fault)
    {
        for (var link = fault; link is not null; link = link.InnerException)
            yield return link;
    }
}
