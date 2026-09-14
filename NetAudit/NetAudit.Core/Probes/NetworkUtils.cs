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
    /// Найдено эмпирически 2026-09-13: при поднятом VPN (WireGuard и подобные)
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
            // Два списка вместо одного: IPv6-шлюз годится для пинга, но берётся
            // только когда IPv4 нет вовсе. На смешанном подключении шлюз IPv4 —
            // тот самый роутер, а адрес IPv6 у него же меняется от префикса
            // провайдера и в отчёте читается хуже
            var candidates   = new List<(GatewayInfo Info, int Rank, int NoDns)>();
            var candidatesV6 = new List<(GatewayInfo Info, int Rank, int NoDns)>();

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
                    if (addr.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                        continue;
                    if (!IsUsableGateway(addr)) continue;

                    // Адрес со ссылочной областью пишется как «fe80::1%12»; без номера
                    // области Ping такой адрес не разберёт, поэтому строку берём
                    // целиком из IPAddress, а не из одних байтов
                    var entry = (
                        new GatewayInfo(addr.ToString(), ni.Name, ni.Description, IsTunnel(ni)),
                        Rank(ni, addr),
                        noDns);

                    if (addr.AddressFamily == AddressFamily.InterNetwork) candidates.Add(entry);
                    else                                                  candidatesV6.Add(entry);
                }
            }

            // На чисто IPv6-подключении список IPv4 пуст — раньше это означало
            // «шлюза нет вовсе», и график задержки до шлюза оставался пустым
            var chosen = candidates.Count > 0 ? candidates : candidatesV6;
            if (chosen.Count == 0) return default;

            return chosen
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
        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Отсев тот же по смыслу, но байтовые правила IPv4 к IPv6 неприменимы:
            // ff00::/8 — многоадресная рассылка, ::/128 — «адреса нет», ::1 — loopback.
            // Ссылочно-локальный fe80::/10 при этом оставляем: именно им почти всегда
            // и представляется настоящий домашний роутер в IPv6
            if (addr.IsIPv6Multicast) return false;
            if (addr.Equals(IPAddress.IPv6Any)) return false;
            if (IPAddress.IsLoopback(addr)) return false;
            return true;
        }

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
        // Признак чисто адресный и осмыслен только для IPv4: в IPv6 те же байты
        // 0xAC 0x1x встречаются в любом обычном глобальном адресе
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = addr.GetAddressBytes();
        return b[0] == 172 && b[1] >= 16 && b[1] <= 31;
    }
}
