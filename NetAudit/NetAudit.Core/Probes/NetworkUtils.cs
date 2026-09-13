using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetAudit.Core.Probes;

/// <summary>Выбранный шлюз и адаптер, через который он найден.</summary>
public readonly record struct GatewayInfo(
    string Address,
    string AdapterName,
    string AdapterDescription,
    bool IsTunnel)
{
    public bool IsEmpty => string.IsNullOrEmpty(Address);
}

public static class NetworkUtils
{
    public static string? GetDefaultGateway()
    {
        var info = GetDefaultGatewayInfo();
        return info.IsEmpty ? null : info.Address;
    }

    /// <summary>
    /// Ищет шлюз, до которого осмысленно мерить задержку, — то есть роутер в своей
    /// локальной сети, а не выход виртуального адаптера.
    ///
    /// Найдено эмпирически 2026-09-13: при поднятом VPN (WireGuard/AmneziaVPN)
    /// туннельный адаптер объявляет своим шлюзом <c>0.0.0.0</c>, и прежний выбор
    /// «первый попавшийся» брал именно его. Результат — ровные 100% потерь на
    /// графике «шлюз» при полностью исправной сети: пинг уходил в несуществующий
    /// адрес. Тот же промах давали Hyper-V и WSL — это числилось в техническом долге.
    ///
    /// Поэтому теперь два шага: сначала выбрасываются адреса, до которых пинговать
    /// бессмысленно в принципе, затем оставшиеся выстраиваются по осмысленности —
    /// физический адаптер впереди туннеля, а среди равных побеждает тот, у которого
    /// есть DNS-серверы (у служебных интерфейсов их обычно нет).
    /// </summary>
    public static GatewayInfo GetDefaultGatewayInfo()
    {
        try
        {
            var candidates = new List<(GatewayInfo Info, int Rank, int NoDns)>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                // Адаптер без единого DNS-сервера почти всегда служебный: настоящее
                // подключение их получает вместе с адресом. Различитель грубый,
                // но применяется только между равными по основному признаку
                int noDns = 0;
                try { noDns = props.DnsAddresses.Count > 0 ? 0 : 1; } catch { }

                foreach (var ga in props.GatewayAddresses)
                {
                    var addr = ga.Address;
                    if (addr is null) continue;
                    if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (!IsUsableGateway(addr)) continue;

                    candidates.Add((
                        new GatewayInfo(addr.ToString(), ni.Name, ni.Description, IsTunnel(ni)),
                        Rank(ni, addr),
                        noDns));
                }
            }

            if (candidates.Count == 0) return default;

            return candidates
                .OrderBy(c => c.Rank)
                .ThenBy(c => c.NoDns)
                .First().Info;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Адреса, по которым пинг никогда не даст осмысленного ответа.
    /// 0.0.0.0 объявляют туннельные адаптеры, 169.254.x.x — признак того,
    /// что адрес по DHCP получить не удалось вовсе.
    /// </summary>
    private static bool IsUsableGateway(IPAddress addr)
    {
        var b = addr.GetAddressBytes();

        if (b[0] == 0) return false;                     // 0.0.0.0 — туннель без реального шлюза
        if (b[0] == 127) return false;                   // loopback
        if (b[0] == 169 && b[1] == 254) return false;    // APIPA, адреса нет
        if (b[0] >= 224) return false;                   // multicast и broadcast

        return true;
    }

    /// <summary>Чем меньше — тем правдоподобнее, что это настоящий домашний роутер.</summary>
    private static int Rank(NetworkInterface ni, IPAddress addr)
    {
        if (IsVirtualByName(ni)) return 3;
        if (IsTunnel(ni))        return 2;
        if (IsVirtualByAddress(addr)) return 1;

        return ni.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet        => 0,
            NetworkInterfaceType.GigabitEthernet => 0,
            NetworkInterfaceType.Wireless80211   => 0,
            _                                    => 1,
        };
    }

    private static bool IsTunnel(NetworkInterface ni) =>
        ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp;

    /// <summary>
    /// Виртуальные адаптеры по названию драйвера, а не по типу интерфейса:
    /// WireGuard, TAP и Hyper-V представляются системе обычными сетевыми картами,
    /// и <see cref="NetworkInterfaceType"/> у них честный <c>Ethernet</c>.
    /// </summary>
    private static bool IsVirtualByName(NetworkInterface ni)
    {
        string text = $"{ni.Name} {ni.Description}";

        ReadOnlySpan<string> markers =
        [
            "wireguard", "amnezia", "openvpn", "tap-", "tap ", "wintun", "vpn",
            "hyper-v", "virtualbox", "vmware", "vethernet", "wsl", "docker",
            "loopback", "bluetooth", "zerotier", "tailscale", "radmin", "hamachi",
        ];

        foreach (var m in markers)
            if (text.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>Docker (172.17–31.x.x) и часть виртуальных коммутаторов.</summary>
    private static bool IsVirtualByAddress(IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        return b[0] == 172 && b[1] >= 16 && b[1] <= 31;
    }
}
