using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>Мгновенное состояние машины во время нагрузки.</summary>
public readonly record struct StressSample(
    double CpuPercent,
    double GpuPercent,
    double CpuTempC,
    double GpuTempC,
    double RamUsedGb);

/// <summary>Сводка за весь прогон.</summary>
public readonly record struct StressStats(
    double MaxCpuTempC,
    double AvgCpuTempC,
    double MaxGpuTempC,
    double AvgGpuTempC,
    double MaxCpuPercent,
    double FirstOps,
    double LastOps);

/// <summary>
/// Наблюдатель за машиной на время стресс-теста: раз в секунду снимает температуры,
/// загрузку и текущую скорость вычислений.
///
/// Живёт в собственном потоке, а не в пуле задач, по простой причине: все потоки
/// пула во время теста заняты нагрузкой, и замер, поставленный в очередь, приходил
/// бы с непредсказуемой задержкой — именно тогда, когда важна секундная точность.
///
/// Скорость вычислений берётся из счётчика самого теста: это и есть детектор
/// троттлинга. Частоту процессора напрямую спрашивать бесполезно — WMI отдаёт
/// её с задержкой и округлением, а падение реальной производительности видно сразу.
/// </summary>
public sealed class StressMonitor
{
    private readonly TemperatureProbe   _temp = new();
    private readonly GpuProbe           _gpu  = new();
    private readonly SystemMetricsProbe _sys  = new();

    private readonly object _gate = new();
    private readonly List<(double Seconds, long Counter)> _counters = [];

    private Thread? _thread;
    private Func<long>? _counterSource;

    private StressSample _current = new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
    private double _opsPerSecond;

    private double _maxCpuTemp = double.NaN, _sumCpuTemp, _maxGpuTemp = double.NaN, _sumGpuTemp;
    private int _cpuTempCount, _gpuTempCount;
    private double _maxCpuPercent = double.NaN;

    public bool TemperatureAvailable { get; private set; }

    public StressSample Current
    {
        get { lock (_gate) return _current; }
    }

    public void Initialize()
    {
        try { _temp.Initialize(); } catch { }
        try { _gpu.Initialize(); } catch { }
        try { _sys.Sample(); } catch { }   // первый замер счётчика CPU всегда нулевой — засеваем заранее

        TemperatureAvailable = _temp.Available;
    }

    public void Start(CancellationToken ct, Func<long>? counterSource = null)
    {
        _counterSource = counterSource;

        _thread = new Thread(() => Loop(ct))
        {
            Name = "NetAudit-stress-monitor",
            IsBackground = true,
            // Выше обычного: замерять надо вовремя, а все ядра заняты нагрузкой
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        try { _thread?.Join(TimeSpan.FromSeconds(3)); } catch { }
        _temp.Dispose();
        _gpu.Dispose();
    }

    /// <summary>Последняя измеренная скорость вычислений, операций в секунду.</summary>
    public double OpsPerSecond(long _ = 0)
    {
        lock (_gate) return _opsPerSecond;
    }

    public StressStats Stats
    {
        get
        {
            lock (_gate)
            {
                var (first, last) = OpsEdges();
                return new StressStats(
                    _maxCpuTemp,
                    _cpuTempCount > 0 ? _sumCpuTemp / _cpuTempCount : double.NaN,
                    _maxGpuTemp,
                    _gpuTempCount > 0 ? _sumGpuTemp / _gpuTempCount : double.NaN,
                    _maxCpuPercent,
                    first,
                    last);
            }
        }
    }

    private void Loop(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        long prevCounter = _counterSource?.Invoke() ?? 0;
        var prevTime = started;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(1000);
                if (ct.IsCancellationRequested) break;

                var (cpu, ramUsed, _) = _sys.Sample();
                float gpu = _gpu.Sample();
                var (cpuTemp, gpuTemp) = _temp.Sample();

                var now = DateTime.UtcNow;
                double ops = 0;

                if (_counterSource is not null)
                {
                    long counter = _counterSource();
                    double dt = (now - prevTime).TotalSeconds;
                    if (dt > 0) ops = (counter - prevCounter) / dt;
                    prevCounter = counter;
                    prevTime = now;
                }

                lock (_gate)
                {
                    _current = new StressSample(cpu, gpu, cpuTemp, gpuTemp, ramUsed);
                    _opsPerSecond = ops;

                    if (!double.IsNaN(cpuTemp))
                    {
                        _maxCpuTemp = double.IsNaN(_maxCpuTemp) ? cpuTemp : Math.Max(_maxCpuTemp, cpuTemp);
                        _sumCpuTemp += cpuTemp;
                        _cpuTempCount++;
                    }
                    if (!double.IsNaN(gpuTemp))
                    {
                        _maxGpuTemp = double.IsNaN(_maxGpuTemp) ? gpuTemp : Math.Max(_maxGpuTemp, gpuTemp);
                        _sumGpuTemp += gpuTemp;
                        _gpuTempCount++;
                    }
                    if (!double.IsNaN(cpu))
                        _maxCpuPercent = double.IsNaN(_maxCpuPercent) ? cpu : Math.Max(_maxCpuPercent, cpu);

                    if (_counterSource is not null)
                        _counters.Add(((now - started).TotalSeconds, prevCounter));
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* один сбойный замер не должен ронять наблюдение */ }
        }
    }

    /// <summary>
    /// Скорость в начале и в конце прогона. Первые 10 секунд отбрасываются —
    /// там ещё идёт разгон частоты и прогрев, и сравнение с ними завышало бы
    /// падение на ровном месте.
    /// </summary>
    private (double First, double Last) OpsEdges()
    {
        if (_counters.Count < 6) return (0, 0);

        var points = _counters.Where(p => p.Seconds >= 10).ToList();
        if (points.Count < 4) points = _counters;

        int window = Math.Max(2, Math.Min(20, points.Count / 4));

        double Rate(int from, int to)
        {
            double dt = points[to].Seconds - points[from].Seconds;
            return dt > 0 ? (points[to].Counter - points[from].Counter) / dt : 0;
        }

        double first = Rate(0, window);
        double last  = Rate(points.Count - 1 - window, points.Count - 1);

        return (first, last);
    }
}
