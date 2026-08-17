using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetAudit.Core.Probes;

public static class NetworkUtils
{
    public static string? GetDefaultGateway()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                      && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(ni => ni.GetIPProperties().GatewayAddresses)
            .Select(ga => ga.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .OrderBy(IsVirtualAdapter)
            .FirstOrDefault()
            ?.ToString();
    }

    // Docker (172.17-31.x.x) and Hyper-V/WSL (172.x or 192.168.x where x > 200) tend to be virtual
    private static int IsVirtualAdapter(System.Net.IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 1;
        return 0;
    }
}
