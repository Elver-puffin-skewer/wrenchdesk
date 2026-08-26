using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WrenchDesk.Services;

/// <summary>
/// Works out the addresses this PC can be reached at, so both the startup banner and the help
/// page can tell someone the exact URL to type into a phone rather than describing one.
/// </summary>
public static class NetworkInfo
{
    public static IEnumerable<string> LocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;

                yield return addr.Address.ToString();
            }
        }
    }

    /// <summary>
    /// The address most likely to be the shop's wifi, listed first. Virtual adapters from Docker,
    /// WSL and VPNs are pushed down — they are real addresses but useless from a phone.
    /// </summary>
    public static List<string> LanUrls(int port) =>
        LocalAddresses()
            .OrderBy(ip => ip.StartsWith("192.168.") ? 0 : ip.StartsWith("10.") ? 1 : 2)
            .Select(ip => $"http://{ip}:{port}")
            .ToList();
}
