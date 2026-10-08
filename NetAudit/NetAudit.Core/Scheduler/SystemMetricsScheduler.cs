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

    // Загрузку карты NVIDIA спрашиваем у самой карты. Счётчик Windows «GPU Engine»
    // считает долю времени, что планировщик держал пакеты на движке, и на коротких
    // кадрах систематически занижает: на скриншоте под FurMark оверлей
    // показывал 68%, а карта под FurMark — 100%. Для карт без nvidia-smi
    // остаётся счётчик
    private readonly NvidiaLiveProbe         _nvidia     = new();
    private readonly CancellationTokenSource _cts        = new();
    private Thread? _metricsThread;
    private Task? _wifiTask;

    public event Action<SystemSnapshot>? SnapshotReady;
    public event Action<WifiInfo?>?      WifiReady;

    /// <summary>
    /// Тик занял заметно дольше секунды — с разбивкой, кто из проб виноват.
    /// Нужно ради чёрного ящика: под стресс-тестом процессора запись шла
    /// с провалами по 6–19 секунд, и момент сбоя в файл не попадал. Событие
    /// даёт увидеть причину прямо в журнале, без отладчика на живой машине.
    /// </summary>
    public event Action<string>? SlowTick;

    /// <summary>
    /// nvidia-smi отдавал показания и перестал (true), либо снова отдаёт (false).
    /// Это и есть картина «карта отвалилась с шины при живом процессоре»:
    /// загрузка карты за секунду падает со 100% до нуля, температура застывает
    /// на одном значении, а остальная система продолжает работать. Без пометки
    /// такой хвост читается как
    /// «всё было спокойно» — момент срыва виден только по косвенным признакам.
    /// </summary>
    public event Action<bool>? GpuSilent;

    private bool _gpuWasLive;
    private bool _gpuSilentReported;

    /// <summary>Порог, после которого тик считается медленным.</summary>
    private static readonly TimeSpan SlowTickLimit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Пропуск дольше этого — машина спала, а не тормозила. Во сне поток замеров
    /// стоит вместе со всей системой, а видеокарта обесточена: после пробуждения
    /// первый же тик выглядит как «датчики висели девять часов, карта не
    /// отвечает» — ложная тревога о срыве карты там, где компьютер просто
    /// был в спящем режиме.
    ///
    /// Полминуты: настоящая задержка от нагрузки дальше единиц секунд не уходила
    /// даже когда поток замеров вытеснялся полностью.
    /// </summary>
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(30);

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

        // Замеры идут на собственном потоке с высоким приоритетом, а не на пуле
        // через PeriodicTimer. Причина — чёрный ящик: под стресс-тестом процессора
        // (двенадцать потоков на 100%) посекундная запись проваливалась на 6–29
        // секунд, и момент сбоя в файл не попадал. Замер показал:
        // проба скорости сети «занимала» 16 секунд, проба системы — 5, хотя это
        // два вызова kernel32. Поток просто не получал процессорного времени:
        // приложение, поднятое задачей Планировщика, живёт с приоритетом «ниже
        // обычного». Highest даёт потоку +2 к базе процесса — этого хватает, чтобы
        // раз в секунду отобрать сто миллисекунд у любой нагрузки, включая игру
        _metricsThread = new Thread(() => RunMetrics(_cts.Token))
        {
            Name = "NetAudit-metrics",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _metricsThread.Start();
        _wifiTask = RunWifiAsync(_cts.Token);
    }

    private void RunMetrics(CancellationToken ct)
    {
        bool gpuReady = false;
        bool tempReady = false;
        bool nvidiaReady = false;
        bool nvidiaLive = false;

        // Тики по расписанию, а не «период после окончания работы»: иначе секунда
        // растягивалась бы на секунду с хвостиком и метки времени плыли
        var due = DateTime.UtcNow + MetricsFast;
        var lastTickAt = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero && ct.WaitHandle.WaitOne(wait)) break;
            var period = _slowMode ? MetricsSlow : MetricsFast;
            var now = DateTime.UtcNow;

            // Разрыв в разы больше периода — компьютер спал. Такой тик не годится
            // ни для жалобы на медлительность, ни для вывода «карта пропала»
            bool afterSleep = now - lastTickAt > SleepGap;
            lastTickAt = now;
            due = now + period;

            try
            {
                // Счётчики GPU, датчики и nvidia-smi поднимаются на первом тике.
                // Это долгие вызовы, но поток свой — никого не задерживают
                if (!gpuReady)
                {
                    _gpuProbe.Initialize();
                    gpuReady = true;
                }
                if (!tempReady)
                {
                    _tempProbe.Initialize();
                    tempReady = true;
                }
                if (!nvidiaReady)
                {
                    nvidiaLive = NvidiaLiveProbe.IsPresent && _nvidia.Start();
                    nvidiaReady = true;
                }

                var tick = System.Diagnostics.Stopwatch.StartNew();
                long tSpeed, tSys, tGpu, tTemp, tSplit, tFps, tHandlers;

                var (rx, tx)              = _speedProbe.Sample();
                tSpeed = tick.ElapsedMilliseconds;
                var (cpu, ramUsed, total) = _sysProbe.Sample();
                var (bat, charging, _)    = _sysProbe.GetBattery();
                tSys = tick.ElapsedMilliseconds;
                float gpu                 = _gpuProbe.Sample();
                tGpu = tick.ElapsedMilliseconds;
                var (cpuTemp, gpuTemp)    = _tempProbe.Sample();
                tTemp = tick.ElapsedMilliseconds;

                var nv = nvidiaLive ? _nvidia.Last : NvidiaLiveSample.Empty;
                if (nv.HasData) gpu = (float)nv.Utilization;

                // Пустой ответ после живых показаний — карта или её драйвер замолчали.
                // Пустота с самого старта не считается: nvidia-smi ещё поднимается
                if (nv.HasData)
                {
                    _gpuWasLive = true;
                    if (_gpuSilentReported)
                    {
                        _gpuSilentReported = false;
                        GpuSilent?.Invoke(false);
                    }
                }
                else if (_gpuWasLive && !_gpuSilentReported && !afterSleep)
                {
                    _gpuSilentReported = true;
                    GpuSilent?.Invoke(true);
                }

                // Разбивка по датчикам — для строки «ядро/горячая точка» в оверлее.
                // Без прав администратора датчиков через драйвер нет, но nvidia-smi
                // отдаёт ядро и так — лучше одна честная цифра, чем прочерк
                var split = _tempProbe.SampleGpu();
                tSplit = tick.ElapsedMilliseconds;
                double gpuCore = split.CoreC;
                double gpuHot  = split.HotSpotC;
                if (double.IsNaN(gpuCore) && !double.IsNaN(nv.TemperatureC)) gpuCore = nv.TemperatureC;
                if (double.IsNaN(gpuTemp)) gpuTemp = gpuCore;

                // Кадры считаем у процесса на переднем плане — им и является игра
                double fps = _fpsProbe.Available
                    ? _fpsProbe.Sample(GameMode.GameModeDetector.ForegroundPid())
                    : double.NaN;
                tFps = tick.ElapsedMilliseconds;

                SnapshotReady?.Invoke(new SystemSnapshot(
                    rx, tx, cpu, gpu, ramUsed, total, bat, charging, DateTimeOffset.UtcNow,
                    fps, cpuTemp, gpuTemp, gpuCore, gpuHot,
                    nv.PowerWatts, nv.ClockMhz, nv.PowerLimitWatts));
                tHandlers = tick.ElapsedMilliseconds;

                if (tick.Elapsed > SlowTickLimit && !afterSleep)
                {
                    SlowTick?.Invoke(
                        $"тик {tHandlers} мс: сеть {tSpeed}, система {tSys - tSpeed}, " +
                        $"счётчик GPU {tGpu - tSys}, датчики {tTemp - tGpu}, " +
                        $"датчики GPU {tSplit - tTemp}, кадры {tFps - tSplit}, " +
                        $"обработчики {tHandlers - tFps}");
                }
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
        if (_wifiTask is not null)
            await _wifiTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var thread = _metricsThread;
        if (thread is not null)
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        _gpuProbe.Dispose();
        _fpsProbe.Dispose();
        _tempProbe.Dispose();
        _nvidia.Dispose();
        _cts.Dispose();
    }
}
