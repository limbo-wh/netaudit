using System.Diagnostics;
using System.Net.NetworkInformation;
using NetAudit.Core.Models;

namespace NetAudit.Core.Probes;

public sealed class IcmpProbe : IDisposable
{
    private readonly Ping _ping = new();
    private readonly string _host;

    public IcmpProbe(string host) => _host = host;

    /// <summary>
    /// Один пинг. Задержка берётся у ядра, а не у своего секундомера.
    ///
    /// Секундомер вокруг <c>SendPingAsync</c> считает и время, пока поток NetAudit
    /// ждал процессор. Когда машина на мгновение подвисает (драйвер Wi-Fi держит
    /// процессор, игра забрала все ядра), он показывает сотни миллисекунд до роутера
    /// при здоровой сети — и оверлей уводит шлюз в красную зону ровно во время фриза,
    /// подтверждая ложный вывод «виновата сеть». Ядро ставит время в момент прихода
    /// ответа, и наш поток на него не влияет.
    ///
    /// Но ядро отдаёт целые миллисекунды, а по кабелю до роутера бывает 0,3 мс.
    /// Поэтому берётся меньшее из секундомера и «ядро + 1 мс»: без подвисания точность
    /// секундомера сохраняется, с подвисанием число не уходит от сетевого дальше
    /// миллисекунды. Разница уходит в <see cref="PingResult.StallMs"/>.
    /// </summary>
    public async Task<PingResult> SendAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await _ping.SendPingAsync(_host, 1000).WaitAsync(ct);
            sw.Stop();
            if (reply.Status != IPStatus.Success)
                return new PingResult(_host, null, false, DateTimeOffset.UtcNow);

            double observed = sw.Elapsed.TotalMilliseconds;
            double rtt      = Math.Min(observed, reply.RoundtripTime + 1.0);
            return new PingResult(_host, rtt, true, DateTimeOffset.UtcNow, observed - rtt);
        }
        catch
        {
            return new PingResult(_host, null, false, DateTimeOffset.UtcNow);
        }
    }

    public void Dispose() => _ping.Dispose();
}
