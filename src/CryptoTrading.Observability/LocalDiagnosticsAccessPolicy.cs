using System.Net;

namespace CryptoTrading.Observability;

/// <summary>Restricts local-only diagnostics when the host is accidentally bound beyond loopback.</summary>
public static class LocalDiagnosticsAccessPolicy
{
    public static bool IsLoopback(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        var normalized = remoteAddress.IsIPv4MappedToIPv6
            ? remoteAddress.MapToIPv4()
            : remoteAddress;

        return IPAddress.IsLoopback(normalized) ||
               normalized.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               normalized.GetAddressBytes() is [127, _, _, _];
    }
}
