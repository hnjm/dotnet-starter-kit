using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace FSH.Modules.Webhooks.Services;

/// <summary>
/// SSRF guard for outbound webhook delivery. A tenant registers the destination URL, so the
/// target is untrusted input: without a guard a tenant can point a subscription at the cloud
/// metadata endpoint, loopback, or an internal RFC1918 host and use the delivery log as a
/// blind-SSRF oracle. <see cref="IsBlockedHost"/> gives fast feedback at the create boundary;
/// <see cref="IsBlockedAddress"/> is the authoritative check applied at connect time (after DNS
/// resolution) so DNS-rebinding cannot slip a public hostname past the create check and then
/// resolve to an internal address at delivery time.
/// </summary>
public static class WebhookUrlGuard
{
    public static bool IsBlockedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        // "localhost" and any *.localhost never leave the box — block by name; a literal IP is
        // checked against the address ranges, real hostnames defer to the connect-time check.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsBlockedAddress(ip);
    }

    public static bool IsBlockedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                10 => true,                                   // 10.0.0.0/8   private
                127 => true,                                  // 127.0.0.0/8  loopback
                169 when b[1] == 254 => true,                 // 169.254.0.0/16 link-local (cloud metadata)
                172 when b[1] >= 16 && b[1] <= 31 => true,    // 172.16.0.0/12 private
                192 when b[1] == 168 => true,                 // 192.168.0.0/16 private
                100 when b[1] >= 64 && b[1] <= 127 => true,   // 100.64.0.0/10 carrier-grade NAT
                0 => true,                                    // 0.0.0.0/8    unspecified
                >= 224 => true,                               // multicast / reserved
                _ => false,
            };
        }

        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || IsIPv6UniqueLocal(address);
    }

    // fc00::/7 — unique local addresses (the IPv6 equivalent of RFC1918).
    private static bool IsIPv6UniqueLocal(IPAddress address) =>
        (address.GetAddressBytes()[0] & 0xFE) == 0xFC;

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the destination host and
    /// refuses to connect to any non-routable/internal address. This is the authoritative SSRF
    /// gate: it runs at delivery time on the real resolved IP, closing the DNS-rebinding window
    /// left open by a create-time hostname check.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var target = Array.Find(addresses, a => !IsBlockedAddress(a));
        if (target is null)
        {
            throw new HttpRequestException(
                $"Blocked webhook target '{host}': it resolves only to non-routable or internal addresses.");
        }

        Socket? socket = null;
        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null; // ownership transferred to the stream
            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }
}
