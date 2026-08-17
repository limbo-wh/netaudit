using System.Net;
using System.Net.NetworkInformation;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Поиск наибольшего пакета, который проходит по маршруту без дробления.
///
/// Зачем это в утилите про игры: если MTU по пути меньше, чем настроено у вас,
/// крупные пакеты приходится дробить, а при неправильно настроенном оборудовании —
/// молча выбрасывать. Наружу это выглядит как случайные вылеты из матча и зависания
/// при загрузке карты, притом что пинг и потери в норме. Такое не ловится ничем,
/// кроме прямой проверки.
///
/// Метод: пинг с запретом фрагментации и двоичным поиском по размеру полезной нагрузки.
/// MTU = нагрузка + 28 байт (20 заголовок IP + 8 заголовок ICMP).
/// </summary>
public sealed class MtuTest : IDiagnosticTest
{
    public string Title => "Проверка MTU";

    private const int Overhead = 28;
    private const int MinLoad  = 500;
    private const int MaxLoad  = 1472;   // 1472 + 28 = 1500, обычный Ethernet
    private const int TimeoutMs = 1500;

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Проверка MTU (размер пакета без дробления)"));
        log.Report(TestLine.Dim("Двоичный поиск пингом с запретом фрагментации"));
        log.Report(TestLine.Empty);

        string? gw = Probes.NetworkUtils.GetDefaultGateway();
        int gwMtu = 0;

        if (gw is not null)
        {
            log.Report(TestLine.Dim($"Ищу MTU до шлюза {gw}…"));
            gwMtu = await FindMtuAsync(IPAddress.Parse(gw), ct).ConfigureAwait(false);
            Report(log, $"MTU до шлюза ({gw})", gwMtu);
        }
        else
        {
            log.Report(TestLine.Warn("Шлюз не определён, проверка локального участка пропущена"));
        }

        var anchor = await PingUtil.PickAnchorAsync(ct).ConfigureAwait(false);
        int netMtu = 0;

        if (anchor is null)
        {
            log.Report(TestLine.Warn("Публичные адреса не отвечают на ICMP — MTU до интернета не проверить"));
        }
        else if (anchor.LooksIntercepted)
        {
            log.Report(TestLine.Warn(
                $"⚠ {anchor.Address} перехвачен локально (TTL {anchor.Ttl}) — это не интернет-хост."));
            log.Report(TestLine.Warn("   Результат ниже относится к локальной сети, а не к каналу провайдера."));
            log.Report(TestLine.Dim($"Ищу MTU до {anchor.Address}…"));
            netMtu = await FindMtuAsync(anchor.Address, ct).ConfigureAwait(false);
            Report(log, $"MTU до {anchor.Address}", netMtu);
        }
        else
        {
            log.Report(TestLine.Dim($"Ищу MTU до {anchor.Address}…"));
            netMtu = await FindMtuAsync(anchor.Address, ct).ConfigureAwait(false);
            Report(log, $"MTU до интернета ({anchor.Address})", netMtu);
        }

        // ── Что настроено на самом адаптере ─────────────────────────────────
        log.Report(TestLine.Empty);
        log.Report(TestLine.Head("Настройки адаптеров"));
        foreach (var line in AdapterMtus())
            log.Report(line);

        // ── Вывод ───────────────────────────────────────────────────────────
        log.Report(TestLine.Empty);
        log.Report(TestLine.Head("Итог"));

        int effective = netMtu > 0 ? netMtu : gwMtu > 0 ? gwMtu : 0;
        if (effective == 0)
        {
            log.Report(TestLine.Warn("Измерить не удалось: узлы не отвечают на пинг с запретом фрагментации."));
            log.Report(TestLine.Dim("Часто это просто фильтр ICMP на оборудовании, а не поломка."));
            return;
        }

        if (effective >= 1500)
            log.Report(TestLine.Good("MTU 1500 — полный Ethernet, дробить пакеты не приходится."));
        else if (effective >= 1492)
            log.Report(TestLine.Good($"MTU {effective} — типично для подключения через PPPoE. Это норма."));
        else if (effective >= 1400)
            log.Report(TestLine.Warn($"MTU {effective} — занижен. Обычно так делают VPN или туннель."));
        else
            log.Report(TestLine.Bad($"MTU {effective} — сильно занижен. Вероятен туннель или неверная настройка."));

        if (gwMtu > 0 && netMtu > 0 && netMtu < gwMtu)
        {
            log.Report(TestLine.Warn(
                $"До шлюза проходит {gwMtu}, до интернета только {netMtu}: узкое место за роутером, у провайдера."));
            log.Report(TestLine.Dim("Если в играх бывают беспричинные вылеты — выставьте MTU адаптера равным " +
                                    $"{netMtu} и проверьте, повторится ли."));
        }
    }

    private static void Report(IProgress<TestLine> log, string label, int mtu)
    {
        if (mtu == 0)
        {
            log.Report(TestLine.Warn(Fmt.Row(label, "не отвечает")));
            return;
        }
        if (mtu < 0)
        {
            // Узел отвечает, но не пропускает даже нижнюю границу поиска
            log.Report(TestLine.Bad(Fmt.Row(label, $"меньше {MinLoad + Overhead} байт")));
            return;
        }
        var level = mtu >= 1492 ? TestLevel.Good : mtu >= 1400 ? TestLevel.Warn : TestLevel.Bad;
        log.Report(new TestLine(Fmt.Row(label, $"{mtu} байт"), level));
    }

    /// <summary>Двоичный поиск: наибольшая нагрузка, которая доходит целиком. 0 — узел не отвечает.</summary>
    private static async Task<int> FindMtuAsync(IPAddress target, CancellationToken ct)
    {
        using var ping = new Ping();
        var noFrag = new PingOptions(64, true);

        async Task<bool> Fits(int load)
        {
            // Два шанса: одиночная потеря не должна выглядеть как «пакет не пролез»
            for (int i = 0; i < 2; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(TimeoutMs),
                                                     new byte[load], noFrag, ct).ConfigureAwait(false);
                    if (r.Status == IPStatus.Success) return true;
                    if (r.Status == IPStatus.PacketTooBig) return false;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return false;
        }

        // Сначала убеждаемся, что узел вообще отвечает — иначе поиск найдёт «MTU = 0»
        if (!await Fits(MinLoad).ConfigureAwait(false))
        {
            bool alive = false;
            try
            {
                var r = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(TimeoutMs),
                                                 cancellationToken: ct).ConfigureAwait(false);
                alive = r.Status == IPStatus.Success;
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            // Отвечает, но 528 байт не пролезают — MTU ниже нижней границы поиска
            return alive ? -1 : 0;
        }

        int lo = MinLoad, hi = MaxLoad;
        if (await Fits(hi).ConfigureAwait(false)) return hi + Overhead;

        while (hi - lo > 1)
        {
            ct.ThrowIfCancellationRequested();
            int mid = (lo + hi) / 2;
            if (await Fits(mid).ConfigureAwait(false)) lo = mid;
            else hi = mid;
        }
        return lo + Overhead;
    }

    /// <summary>MTU, прописанный в свойствах активных адаптеров.</summary>
    private static IEnumerable<TestLine> AdapterMtus()
    {
        var lines = new List<TestLine>();

        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex)
        {
            return [TestLine.Warn($"  Не удалось получить список адаптеров: {ex.Message}")];
        }

        foreach (var ni in all)
        {
            // Ловим на каждом адаптере отдельно: у адаптера без IPv4 обращение к его
            // свойствам бросает исключение, и общий try обрывал перебор на середине —
            // часть адаптеров просто не показывалась
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                var props   = ipProps.GetIPv4Properties();
                if (props is null) continue;

                bool hasGateway = ipProps.GatewayAddresses.Count > 0;
                string mark = hasGateway ? "  ← активный" : "";
                lines.Add(TestLine.Info(Fmt.Row("  " + Trim(ni.Name, 24), $"{props.Mtu} байт{mark}")));
            }
            catch { /* адаптер без IPv4 — просто пропускаем */ }
        }

        if (lines.Count == 0) lines.Add(TestLine.Dim("  Активных адаптеров не найдено"));
        return lines;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
