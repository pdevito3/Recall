namespace Recall.Server.Auth;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// One bucket for each connection address, so that a stranger who floods a login route blocks only that stranger. It never
/// reads a forwarded header, because a client can write any value there and so get a new bucket for each request.
/// </summary>
public static class ConnectionAddress
{
    private const int NetworkBytesOfIpv6 = 8;

    public static string BucketOf(IPAddress? address)
    {
        if (address is null)
            return "no-address";
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4().ToString();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        // One person gets at least a /64, and can use each address in it.
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, NetworkBytesOfIpv6, bytes.Length - NetworkBytesOfIpv6);
        return new IPAddress(bytes).ToString();
    }
}
