using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Трассировка маршрута с замером задержки на каждом узле.
///
/// Смысл не в списке хопов, а в том, где именно задержка подскакивает: до первого
/// узла — виноват Wi-Fi или роутер, на втором-третьем — «последняя миля» провайдера,
/// дальше — магистраль, до которой уже никому не дотянуться.
///
/// Оговорка, которая обязательно всплывёт при чтении вывода: транзитные роутеры
/// отвечают на ICMP по остаточному принципу и часто показывают задержку больше,
/// чем узлы за ними. Скачок считается настоящим, только если он держится
/// до самого конца маршрута.
/// </summary>
public sealed class TracerouteTest(string target, int maxHops = 20) : IDiagnosticTest
{
    public string Title => $"Трассировка до {target}";

    private const int Probes    = 3;
    private const int TimeoutMs = 1500;

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head($"Трассировка маршрута до {target}"));
        log.Report(TestLine.Dim($"максимум {maxHops} узлов, по {Probes} пробы на узел"));
        log.Report(TestLine.Empty);
        log.Report(TestLine.Info("№".PadRight(4) + "адрес".PadRight(24) + "задержка".PadLeft(22)));
        log.Report(TestLine.Dim(new string('─', 52)));

        IPAddress? dest;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(target, ct).ConfigureAwait(false);
            dest = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (dest is null) { log.Report(TestLine.Bad($"Не удалось разрешить имя {target}")); return; }
        }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Не удалось разрешить имя {target}: {ex.Message}"));
            return;
        }

        var payload = new byte[32];
        var hops    = new List<(int n, string addr, double median)>();
        double prevMedian = 0;

        using var ping = new Ping();

        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();

            var options = new PingOptions(ttl, true);
            var times   = new List<double>();
            string addr = "*";
            bool arrived = false;

            for (int p = 0; p < Probes; p++)
            {
                try
                {
                    // Время меряется своим секундомером: для TtlExpired .NET на Windows
                    // всегда отдаёт RoundtripTime = 0 и заполняет его только при Success.
                    // С ним все транзитные узлы показывали 0 мс, а вердикт сваливал всю
                    // задержку маршрута на последний узел.
                    var sw    = Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(dest, TimeSpan.FromMilliseconds(TimeoutMs),
                                                         payload, options, ct).ConfigureAwait(false);
                    sw.Stop();

                    if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
                    {
                        if (reply.Address is not null && !reply.Address.Equals(IPAddress.Any))
                            addr = reply.Address.ToString();

                        // У цели время от ядра есть — берём его, чтобы конечный узел
                        // и промежуточные мерились одинаково, без накладных расходов потока
                        double ms = sw.Elapsed.TotalMilliseconds;
                        if (reply.Status == IPStatus.Success)
                        {
                            ms = Math.Min(ms, reply.RoundtripTime + 1.0);
                            arrived = true;
                        }
                        times.Add(ms);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            double median = PingUtil.Median(times);
            string timeText = times.Count == 0
                ? "нет ответа"
                : $"{median:F0} мс  (из {times.Count}/{Probes})";

            // Скачок относительно предыдущего ответившего узла — то, ради чего трассировка
            // и делается. Первый узел скачком не считается: задержку до роутера вердикт
            // оценивает отдельно
            double jump = times.Count > 0 && hops.Count > 0 ? median - prevMedian : 0;
            string jumpText = jump >= 20 ? $"   +{jump:F0}" : "";

            var level = times.Count == 0 ? TestLevel.Muted
                      : jump >= 50 ? TestLevel.Bad
                      : jump >= 20 ? TestLevel.Warn
                      : TestLevel.Info;

            log.Report(new TestLine(
                $"{ttl}".PadRight(4) + addr.PadRight(24) + timeText.PadLeft(22) + jumpText, level));

            if (times.Count > 0)
            {
                hops.Add((ttl, addr, median));
                prevMedian = median;
            }

            if (arrived)
            {
                log.Report(TestLine.Empty);
                log.Report(TestLine.Good($"Цель достигнута за {ttl} узлов"));
                Verdict(log, hops);
                return;
            }
        }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Warn($"Цель не достигнута за {maxHops} узлов"));
        Verdict(log, hops);
    }

    /// <summary>Где именно задержка выросла сильнее всего и что это означает.</summary>
    private static void Verdict(IProgress<TestLine> log, List<(int n, string addr, double median)> hops)
    {
        if (hops.Count < 2) return;

        int worstIdx = -1;
        double worstJump = 0;
        for (int i = 1; i < hops.Count; i++)
        {
            double d = hops[i].median - hops[i - 1].median;
            if (d > worstJump) { worstJump = d; worstIdx = i; }
        }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Head("Где теряется время"));

        double first = hops[0].median;
        log.Report(TestLine.Info(Fmt.Row("До первого узла (роутер)", $"{first:F0} мс")));
        if (first > 15)
            log.Report(TestLine.Warn("   Много для домашней сети. Wi-Fi, кабель или сам роутер."));
        else
            log.Report(TestLine.Good("   Домашняя сеть в порядке."));

        ReportNat(log, hops);

        if (worstIdx < 0 || worstJump < 15)
        {
            log.Report(TestLine.Good("Резких скачков по маршруту нет — задержка набирается равномерно."));
            return;
        }

        var to = hops[worstIdx];
        string zone = worstIdx switch
        {
            1     => "последняя миля провайдера",
            2 or 3 => "сеть провайдера",
            _     => "магистраль или сеть на другом конце",
        };

        log.Report(new TestLine(
            Fmt.Row($"Скачок на узле {to.n} ({to.addr})", $"+{worstJump:F0} мс"),
            worstJump >= 50 ? TestLevel.Bad : TestLevel.Warn));
        log.Report(TestLine.Info($"   Это {zone}."));
        log.Report(TestLine.Dim("   Проверьте, держится ли скачок на всех последующих узлах. Если дальше"));
        log.Report(TestLine.Dim("   задержка снова падает — узел просто медленно отвечает на ICMP, и это не проблема."));
    }

    /// <summary>
    /// Двойной NAT и CGNAT провайдера видны прямо по адресам первых узлов.
    ///
    /// На задержку они не влияют, но объясняют то, что трассировкой не поймать:
    /// строгий тип NAT в играх, проблемы с голосовым чатом и P2P-лобби, невозможность
    /// открыть порт. Пользователь обычно не знает, что у него два роутера подряд.
    /// </summary>
    private static void ReportNat(IProgress<TestLine> log, List<(int n, string addr, double median)> hops)
    {
        var parsed = hops
            .Select(h => IPAddress.TryParse(h.addr, out var ip) ? ip : null)
            .Where(ip => ip is not null)
            .Select(ip => ip!)
            .ToList();

        // Частные адреса подряд с самого начала маршрута — это роутеры в квартире
        int privateRun = parsed.TakeWhile(IsPrivate).Count();
        bool cgnat     = parsed.Any(IsCgnat);

        if (privateRun < 2 && !cgnat) return;

        if (privateRun >= 2)
            log.Report(TestLine.Warn(
                $"Двойной NAT: {privateRun} роутера подряд ({string.Join(" → ", parsed.Take(privateRun))})"));
        if (cgnat)
            log.Report(TestLine.Warn("CGNAT: провайдер выдаёт общий «серый» адрес (100.64.0.0/10)"));

        log.Report(TestLine.Dim("   На пинг это не влияет. Влияет на тип NAT в играх, голосовой чат, P2P"));
        log.Report(TestLine.Dim("   и проброс портов. Двойной NAT убирается переводом одного из роутеров"));
        log.Report(TestLine.Dim("   в режим точки доступа или моста; CGNAT — только «белым» IP у провайдера."));
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10
                              || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                              || (b[0] == 192 && b[1] == 168));
    }

    private static bool IsCgnat(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}
