using System.Diagnostics;
using System.Globalization;

namespace NetAudit.Core.Probes;

/// <summary>Показания карты в один момент времени. NaN — значение не получено.</summary>
public readonly record struct NvidiaLiveSample(
    double Utilization,
    double PowerWatts,
    double PowerLimitWatts,
    double TemperatureC,
    double ClockMhz)
{
    public static NvidiaLiveSample Empty =>
        new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

    public bool HasData => !double.IsNaN(Utilization);
}

/// <summary>
/// Живые показания видеокарты NVIDIA: загрузка, мощность, температура, частота.
///
/// Зачем отдельно от <see cref="GpuProbe"/>: счётчик Windows «GPU Engine» считает
/// не то, что принято называть загрузкой видеокарты. Он меряет долю времени, что
/// планировщик WDDM держал на движке пакеты конкретного процесса, и на коротких
/// кадрах систематически занижает. Замерено на этой машине под полной нагрузкой:
/// счётчик показывал 58%, сама карта — 93% при 200 Вт. Для окна нагрузки, которое
/// существует ровно затем, чтобы показать настоящую занятость карты, разница
/// в тридцать пять процентных пунктов недопустима.
///
/// Побочная выгода: nvidia-smi отдаёт температуру без прав администратора,
/// в отличие от датчиков через <c>TemperatureProbe</c>.
///
/// Устройство: один долгоживущий процесс <c>nvidia-smi -l 1</c>, который сам
/// печатает строку раз в секунду. Запускать его заново на каждый опрос нельзя —
/// старт стоит около сотни миллисекунд и создаёт всплеск нагрузки на процессор
/// прямо посреди замера.
/// </summary>
public sealed class NvidiaLiveProbe : IDisposable
{
    private const string Fields =
        "utilization.gpu,power.draw,power.limit,temperature.gpu,clocks.sm";

    private readonly object _lock = new();
    private Process? _process;
    private Thread? _reader;
    private NvidiaLiveSample _last = NvidiaLiveSample.Empty;
    private bool _disposed;

    /// <summary>Есть ли на машине nvidia-smi. Проверка дешёвая, без запуска.</summary>
    public static bool IsPresent =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));

    /// <summary>Последние полученные показания. До первой строки — пусто.</summary>
    public NvidiaLiveSample Last
    {
        get { lock (_lock) return _last; }
    }

    /// <summary>Запускает фоновое чтение. Повторный вызов ничего не делает.</summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (_process is not null || _disposed) return _process is not null;
        }

        string exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(exe)) return false;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = $"--query-gpu={Fields} --format=csv,noheader,nounits -l 1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            if (process is null) return false;

            var reader = new Thread(() => Read(process))
            {
                Name = "NetAudit-nvidia-smi",
                IsBackground = true,
            };

            lock (_lock)
            {
                _process = process;
                _reader = reader;
            }

            reader.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Read(Process process)
    {
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                if (Parse(line) is not { } sample) continue;
                lock (_lock) _last = sample;
            }
        }
        catch
        {
            // Процесс убит при закрытии окна — это штатное завершение чтения
        }
    }

    /// <summary>
    /// Разбирает строку вида «93, 200.18, 250.00, 62, 1920».
    /// Недоступное поле nvidia-smi печатает как «[N/A]» — такое поле станет NaN,
    /// а строка целиком не отбрасывается: остальные значения годны.
    /// </summary>
    private static NvidiaLiveSample? Parse(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 5) return null;

        double util = Number(parts[0]);
        if (double.IsNaN(util)) return null;

        return new NvidiaLiveSample(
            Utilization: util,
            PowerWatts: Number(parts[1]),
            PowerLimitWatts: Number(parts[2]),
            TemperatureC: Number(parts[3]),
            ClockMhz: Number(parts[4]));
    }

    private static double Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : double.NaN;

    public void Dispose()
    {
        Process? process;
        Thread? reader;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            process = _process;
            reader = _reader;
            _process = null;
            _reader = null;
        }

        try { if (process is { HasExited: false }) process.Kill(); } catch { }
        try { reader?.Join(TimeSpan.FromSeconds(1)); } catch { }
        try { process?.Dispose(); } catch { }
    }
}
