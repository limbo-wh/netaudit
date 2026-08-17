using System.Diagnostics;
using System.Net.NetworkInformation;
using NetAudit.Core.Models;

namespace NetAudit.Core.Probes;

public sealed class IcmpProbe : IDisposable
{
    private readonly Ping _ping = new();
    private readonly string _host;

    public IcmpProbe(string host) => _host = host;

    public async Task<PingResult> SendAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await _ping.SendPingAsync(_host, 1000).WaitAsync(ct);
            sw.Stop();
            bool ok = reply.Status == IPStatus.Success;
            double rtt = sw.Elapsed.TotalMilliseconds;
            return new PingResult(_host, ok ? rtt : null, ok, DateTimeOffset.UtcNow);
        }
        catch
        {
            return new PingResult(_host, null, false, DateTimeOffset.UtcNow);
        }
    }

    public void Dispose() => _ping.Dispose();
}
