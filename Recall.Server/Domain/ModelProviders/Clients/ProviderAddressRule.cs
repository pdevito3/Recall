namespace Recall.Server.Domain.ModelProviders.Clients;

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

/// <summary>
/// The addresses that a call to a provider can reach. A private address stays allowed, because a local model on a home
/// network is a real use. <c>docs/backend/model-providers.md</c> names each rule.
/// </summary>
public static class ProviderAddressRule
{
    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("fd00:ec2::254"), // AWS over IPv6.
        IPAddress.Parse("fd20:ce::254"), // Google Cloud over IPv6.
        IPAddress.Parse("100.100.100.200"), // Alibaba Cloud.
        IPAddress.Parse("168.63.129.16"), // Azure.
        IPAddress.Parse("192.0.0.192"), // Oracle Cloud.
    ];

    /// <summary>In a container, a loopback address is the app itself, and a local model is on another address.</summary>
    public static bool LoopbackIsTheApp { get; } = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    private static readonly byte[] LocalNat64Prefix = [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01];

    public static bool Refuses(IPAddress address) => Refuses(address, LoopbackIsTheApp);

    public static bool Refuses(IPAddress address, bool loopbackIsTheApp)
    {
        if (IsLocalNat64(address))
            return true;

        var plain = Unwrapped(address);
        if (IPAddress.IsLoopback(plain))
            return loopbackIsTheApp;
        if (MetadataAddresses.Contains(plain))
            return true;

        if (plain.AddressFamily == AddressFamily.InterNetworkV6)
            return plain.IsIPv6LinkLocal || plain.IsIPv6Multicast || plain.Equals(IPAddress.IPv6None);

        var bytes = plain.GetAddressBytes();
        return bytes[0] == 0 // "This network", which reaches the app itself on Linux.
            || (bytes[0] == 169 && bytes[1] == 254) // Link-local, with the metadata address of most clouds.
            || bytes[0] >= 224; // Multicast, reserved, and broadcast.
    }

    /// <summary>The refusal of an address that a person types, before a name can resolve. The check at each connect still runs.</summary>
    public static bool RefusesLiteral(string host)
    {
        var bare = host.Trim('[', ']');
        return bare.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? LoopbackIsTheApp
            : IPAddress.TryParse(bare, out var address) && Refuses(address);
    }

    /// <summary>
    /// The name resolves here, and the socket connects to the address that passed, so a name cannot move to a refused
    /// address after the check. A connection that the pool keeps was checked when it opened.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(address => !Refuses(address)).ToList();
        if (allowed.Count == 0 && addresses.Length > 0)
            throw new ProviderAddressRefusedException();

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception fault) when (fault is SocketException)
            {
                socket.Dispose();
                last = fault;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    // Each site picks where the IPv4 address sits in this prefix, so no check can read it.
    private static bool IsLocalNat64(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 && address.GetAddressBytes().AsSpan(0, 6).SequenceEqual(LocalNat64Prefix);

    // An IPv6 form that a router or a stack turns into an IPv4 address reaches the same place, so the IPv4 rule decides.
    private static IPAddress Unwrapped(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address;
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        var isNat64 = bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4..12].All(b => b == 0);
        var isTranslated = bytes[..8].All(b => b == 0) && bytes[8] == 0xff && bytes[9] == 0xff && bytes[10] == 0 && bytes[11] == 0;
        var isCompatible = bytes[..12].All(b => b == 0) && (bytes[12] | bytes[13]) != 0;
        if (isNat64 || isTranslated || isCompatible)
            return new IPAddress(bytes[12..]);
        return bytes[0] == 0x20 && bytes[1] == 0x02 ? new IPAddress(bytes[2..6]) : address; // 6to4.
    }
}
