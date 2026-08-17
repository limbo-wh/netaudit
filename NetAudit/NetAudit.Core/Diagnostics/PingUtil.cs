using System.Net;
using System.Net.NetworkInformation;

namespace NetAudit.Core.Diagnostics;

/// <summary>Результат разведки якорного хоста в интернете.</summary>
/// <param name="Address">IP, который отвечает.</param>
/// <param name="RttMs">Задержка первого успешного ответа.</param>
/// <param name="Ttl">TTL ответа — по нему видно, сколько хопов прошёл пакет.</param>
/// <param name="LooksIntercepted">
/// Ответ пришёл слишком «близко»: TTL около 128 при задержке меньше миллисекунды.
/// Так выглядит перехват адреса внутри локальной сети — отвечает не тот, к кому обращались.
/// </param>
public sealed record Anchor(IPAddress Address, double RttMs, int Ttl, bool LooksIntercepted);

public static class PingUtil
{
    /// <summary>Публичные адреса, по которым удобно мерить интернет.</summary>
    public static readonly string[] Anchors = ["8.8.8.8", "1.1.1.1", "9.9.9.9", "77.88.8.8"];

    /// <summary>
    /// Найти якорь, который действительно находится в интернете.
    ///
    /// Проверка TTL нужна не для красоты: у ответа настоящего 1.1.1.1 начальный
    /// TTL уменьшается на числе хопов и приходит примерно 50–60. Если пришло 128
    /// и задержка меньше миллисекунды — отвечает соседняя машина или роутер,
    /// перехвативший адрес, и все замеры «до интернета» станут враньём.
    /// </summary>
    public static async Task<Anchor?> PickAnchorAsync(CancellationToken ct)
    {
        Anchor? fallback = null;

        foreach (var host in Anchors)
        {
            ct.ThrowIfCancellationRequested();
            var a = await ProbeAsync(host, ct).ConfigureAwait(false);
            if (a is null) continue;
            if (!a.LooksIntercepted) return a;
            fallback ??= a;
        }
        return fallback;
    }

    public static async Task<Anchor?> ProbeAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2),
                                                 cancellationToken: ct).ConfigureAwait(false);
            if (reply.Status != IPStatus.Success) return null;

            int ttl = reply.Options?.Ttl ?? 0;
            bool intercepted = ttl >= 120 && reply.RoundtripTime <= 1;
            return new Anchor(reply.Address, reply.RoundtripTime, ttl, intercepted);
        }
        catch { return null; }
    }

    /// <summary>
    /// Серия пингов подряд. Возвращает все успешные задержки — считать медиану,
    /// среднее или разброс уже решает вызывающий.
    /// </summary>
    public static async Task<List<double>> SampleRttAsync(
        IPAddress target, int count, int intervalMs, CancellationToken ct)
    {
        var result = new List<double>(count);
        using var ping = new Ping();

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await ping.SendPingAsync(target, TimeSpan.FromSeconds(2),
                                                 cancellationToken: ct).ConfigureAwait(false);
                if (r.Status == IPStatus.Success) result.Add(r.RoundtripTime);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (i < count - 1)
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>Пинговать без остановки, пока не отменят. Используется для замера под нагрузкой.</summary>
    public static async Task PingLoopAsync(
        IPAddress target, List<double> sink, int intervalMs, CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await ping.SendPingAsync(target, TimeSpan.FromSeconds(2),
                                                 cancellationToken: ct).ConfigureAwait(false);
                if (r.Status == IPStatus.Success)
                    lock (sink) sink.Add(r.RoundtripTime);
            }
            catch (OperationCanceledException) { return; }
            catch { }

            try { await Task.Delay(intervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Значение, ниже которого лежит доля <paramref name="p"/> выборки (0..1).</summary>
    public static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
