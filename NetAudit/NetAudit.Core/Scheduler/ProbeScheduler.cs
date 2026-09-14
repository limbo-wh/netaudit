using NetAudit.Core.Logging;
using NetAudit.Core.Models;
using NetAudit.Core.Probes;

namespace NetAudit.Core.Scheduler;

public sealed class ProbeScheduler : IAsyncDisposable
{
    /// <summary>
    /// Проба шлюза. Ноль, когда шлюз не определился: в сети без шлюза (только VPN,
    /// мобильный модем, отключённый кабель) прежний планировщик всё равно слал
    /// пакеты в пустоту и рисовал 100% потерь на исправной машине.
    /// </summary>
    private readonly IcmpProbe? _gatewayProbe;
    private readonly IcmpProbe _cloudflareProbe;
    private readonly TimeSpan _interval;
    private readonly PingLogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<PingResult>? GatewayResult;
    public event Action<PingResult>? CloudflareResult;

    public string GatewayAddress { get; }
    public string LogPath { get; }

    /// <summary>Есть ли вообще шлюз, за которым имеет смысл следить.</summary>
    public bool GatewayKnown => _gatewayProbe is not null;

    public ProbeScheduler(string gatewayAddress, TimeSpan interval)
    {
        GatewayAddress = gatewayAddress;
        _interval = interval;
        _gatewayProbe = string.IsNullOrWhiteSpace(gatewayAddress) ? null : new IcmpProbe(gatewayAddress);
        _cloudflareProbe = new IcmpProbe("1.1.1.1");

        LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetAudit", $"ping_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        _logger = new PingLogger(LogPath);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_gatewayProbe is null)
            {
                // Шлюза нет — следим только за интернетом, а не шлём пакеты в пустоту
                var internetOnly = await _cloudflareProbe.SendAsync(ct);
                _logger.Log(internetOnly);
                CloudflareResult?.Invoke(internetOnly);
                continue;
            }

            var tasks = await Task.WhenAll(
                _gatewayProbe.SendAsync(ct),
                _cloudflareProbe.SendAsync(ct)
            );
            _logger.Log(tasks[0]);
            _logger.Log(tasks[1]);
            GatewayResult?.Invoke(tasks[0]);
            CloudflareResult?.Invoke(tasks[1]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_loop is not null)
                try { await _loop; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
        _gatewayProbe?.Dispose();
        _cloudflareProbe.Dispose();
        _logger.Dispose();
    }
}
