using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Magpienest.Plugins.WebUi;

/// <summary>This computer's home-network addresses, for the links and QR codes in settings.</summary>
internal static class NetworkLinks
{
    /// <summary>The IPv4 addresses of active adapters that count as home network.</summary>
    /// <param name="tailscaleIsHome">Whether Tailscale addresses count.</param>
    public static IReadOnlyList<IPAddress> HomeAddresses(bool tailscaleIsHome)
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    IsVirtual(adapter))
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork &&
                        !address.ToString().StartsWith("169.254.", StringComparison.Ordinal) &&
                        AddressClassifier.Classify(address, tailscaleIsHome) == AddressClass.Home &&
                        !result.Contains(address))
                    {
                        result.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No list; settings then shows none.
        }

        // 192.168.x.x first: the usual home router network, the one a phone is most likely on.
        return [.. result.OrderBy(a => a.ToString().StartsWith("192.168.", StringComparison.Ordinal) ? 0 : 1)
                         .ThenBy(a => a.ToString(), StringComparer.Ordinal)];
    }

    /// <summary>
    /// Adapters of virtual machines and WSL (Hyper-V "vEthernet", VirtualBox, VMware): their addresses exist only inside
    /// this computer, so a phone can never reach them. Tailscale and other VPN adapters are kept.
    /// </summary>
    private static bool IsVirtual(NetworkInterface adapter)
    {
        string[] markers = ["vEthernet", "Hyper-V", "VirtualBox", "VMware", "WSL"];
        return markers.Any(m => adapter.Name.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                                adapter.Description.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Names a new automatic certificate should cover.</summary>
    /// <param name="tailscaleIsHome">Whether Tailscale addresses count.</param>
    /// <param name="extraHosts">Extra host names from settings.</param>
    public static IReadOnlyCollection<string> CertificateNames(bool tailscaleIsHome, IEnumerable<string> extraHosts)
    {
        var machine = Environment.MachineName;
        return [.. new[] { "localhost", "127.0.0.1", "::1", machine, machine + ".local" }
            .Concat(HomeAddresses(tailscaleIsHome).Select(a => a.ToString()))
            .Concat(extraHosts)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
