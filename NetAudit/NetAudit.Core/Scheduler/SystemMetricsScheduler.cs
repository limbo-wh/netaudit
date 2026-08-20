using NetAudit.Core.Models;
using NetAudit.Core.Probes;

namespace NetAudit.Core.Scheduler;

public sealed class SystemMetricsScheduler : IAsyncDisposable
{
    private readonly NetworkSpeedProbe       _speedProbe = new();
    private readonly SystemMetricsProbe      _sysProbe   = new();
    private readonly WifiProbe               _wifiProbe  = new();
    private readonly GpuProbe                _gpuProbe   = new();
    private readonly FpsProbe                _fpsProbe   = new();
    private readonly TemperatureProbe        _tempProbe  = new();
    private readonly CancellationTokenSource _cts        = new();
    private Task? _metricsTask;
    private Task? _wifiTask;

    public event Action<SystemSnapshot>? SnapshotReady;
    public event Action<WifiInfo?>?      WifiReady;

    private static readonly TimeSpan MetricsFast = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MetricsSlow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WifiFast    = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WifiSlow    = TimeSpan.FromSeconds(30);

    private volatile bool _slowMode;

    /// <summary>
    /// Экономный режим на время игры: метрики раз в 2 секунды вместо секунды,
    /// Wi-Fi раз в полминуты вместо пяти секунд. Оверлей обновляется реже, но
    /// цифры в нём и так меняются медленно, а пинг это не затрагивает вовсе.
    /// </summary>
    public bool SlowMode
    {
        get => _slowMode;
        set => _slowMode = value;
    }

    /// <summary>Работает ли счётчик кадров и почему, если нет.</summary>
    public bool   FpsAvailable => _fpsProbe.Available;
    public string FpsStatus    => _fpsProbe.Status;

    /// <summary>Сырые события ETW и сколько из них опознано как кадр — для самодиагностики.</summary>
    public long FpsEventsSeen      => _fpsProbe.EventsSeen;
    public long FpsPresentsMatched => _fpsProbe.PresentsMatched;

    /// <summary>Сеанс поднят, но событий нет — признак поломки, а не отсутствия игры.</summary>
    public bool FpsLooksDead => _fpsProbe.LooksDead;

    /// <summary>
    /// Включить или выключить счётчик FPS. Сеанс ETW поднимается только по запросу:
    /// держать его ради выключенной строки в оверлее незачем.
    /// </summary>
    public void SetFpsEnabled(bool on)
    {
        if (on) _fpsProbe.Start();
        else    _fpsProbe.Stop();
    }

    public void Start()
    {
        _speedProbe.Sample();
        _sysProbe.Sample();

        _metricsTask = RunMetricsAsync(_cts.Token);
        _wifiTask    = RunWifiAsync(_cts.Token);
    }

    private async Task RunMetricsAsync(CancellationToken ct)
    {
        bool gpuReady = false;
        bool tempReady = false;
        using var timer = new PeriodicTimer(MetricsFast);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var wanted = _slowMode ? MetricsSlow : MetricsFast;
                if (timer.Period != wanted) timer.Period = wanted;

                // Инициализируем GPU-счётчики и датчики температуры в фоне при первом тике
                if (!gpuReady)
                {
                    await Task.Run(_gpuProbe.Initialize, ct).ConfigureAwait(false);
                    gpuReady = true;
                }
                if (!tempReady)
                {
                    await Task.Run(_tempProbe.Initialize, ct).ConfigureAwait(false);
                    tempReady = true;
                }

                var (rx, tx)              = _speedProbe.Sample();
                var (cpu, ramUsed, total) = _sysProbe.Sample();
                var (bat, charging, _)    = _sysProbe.GetBattery();
                float gpu                 = _gpuProbe.Sample();
                var (cpuTemp, gpuTemp)    = _tempProbe.Sample();

                // Кадры считаем у процесса на переднем плане — им и является игра
                double fps = _fpsProbe.Available
                    ? _fpsProbe.Sample(GameMode.GameModeDetector.ForegroundPid())
                    : double.NaN;

                SnapshotReady?.Invoke(new SystemSnapshot(
                    rx, tx, cpu, gpu, ramUsed, total, bat, charging, DateTimeOffset.UtcNow,
                    fps, cpuTemp, gpuTemp));
            }
            catch { }
        }
    }

    private async Task RunWifiAsync(CancellationToken ct)
    {
        try { WifiReady?.Invoke(await _wifiProbe.SampleAsync()); } catch { }

        using var timer = new PeriodicTimer(WifiFast);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var wanted = _slowMode ? WifiSlow : WifiFast;
            if (timer.Period != wanted) timer.Period = wanted;

            try { WifiReady?.Invoke(await _wifiProbe.SampleAsync()); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        var tasks = new[] { _metricsTask, _wifiTask }.OfType<Task>();
        await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _gpuProbe.Dispose();
        _fpsProbe.Dispose();
        _tempProbe.Dispose();
        _cts.Dispose();
    }
}
