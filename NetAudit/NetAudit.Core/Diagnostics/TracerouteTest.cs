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
                    var reply = await ping.SendPingAsync(dest, TimeSpan.FromMilliseconds(TimeoutMs),
                                                         payload, options, ct).ConfigureAwait(false);

                    if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
                    {
                        if (reply.Address is not null && !reply.Address.Equals(IPAddress.Any))
                            addr = reply.Address.ToString();
                        times.Add(reply.RoundtripTime);
                        if (reply.Status == IPStatus.Success) arrived = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            double median = PingUtil.Median(times);
            string timeText = times.Count == 0
                ? "нет ответа"
                : $"{median:F0} мс  (из {times.Count}/{Probes})";

            // Скачок относительно предыдущего узла — то, ради чего трассировка и делается
            double jump = times.Count > 0 && prevMedian > 0 ? median - prevMedian : 0;
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
}
