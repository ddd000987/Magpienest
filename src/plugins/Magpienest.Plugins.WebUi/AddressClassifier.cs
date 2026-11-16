using System.Net;
using System.Net.Sockets;

namespace Magpienest.Plugins.WebUi;

/// <summary>Where a request comes from.</summary>
internal enum AddressClass
{
    /// <summary>This computer.</summary>
    Loopback,

    /// <summary>The home network (private, link-local, and optionally Tailscale addresses).</summary>
    Home,

    /// <summary>Anything else.</summary>
    Internet,
}

/// <summary>Classifies the socket's remote address. Headers are never consulted.</summary>
internal static class AddressClassifier
{
    /// <summary>Classifies an address.</summary>
    /// <param name="address">The remote address of the connection.</param>
    /// <param name="tailscaleIsHome">Whether 100.64.0.0/10 counts as home network.</param>
    public static AddressClass Classify(IPAddress address, bool tailscaleIsHome)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return AddressClass.Loopback;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            var home = b[0] == 10
                       || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                       || (b[0] == 192 && b[1] == 168)
                       || (b[0] == 169 && b[1] == 254)
                       || (tailscaleIsHome && b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return home ? AddressClass.Home : AddressClass.Internet;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            var uniqueLocal = (b[0] & 0xFE) == 0xFC; // fc00::/7
            var linkLocal = b[0] == 0xFE && (b[1] & 0xC0) == 0x80; // fe80::/10
            return uniqueLocal || linkLocal ? AddressClass.Home : AddressClass.Internet;
        }

        return AddressClass.Internet;
    }
}
