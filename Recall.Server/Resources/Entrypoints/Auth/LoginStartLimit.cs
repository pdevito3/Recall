namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>
/// One bucket for each connection address, so that a stranger who floods the start route blocks only that stranger. It never
/// reads a forwarded header, because a client can write any value there and so get a new bucket for each request.
/// </summary>
public static class LoginStartLimit
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

    public static async ValueTask RefuseAsync(OnRejectedContext context, TimeSpan window)
    {
        var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var left) ? left : window;
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        var http = context.HttpContext;
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await AuthPages.TooManyStarts(seconds, http.Request.Path + http.Request.QueryString).ExecuteAsync(http);
    }
}
