using LibreHardwareMonitor.Hardware;

namespace NetAudit.Core.Probes;

/// <summary>
/// Температура CPU/GPU через LibreHardwareMonitorLib. Библиотека сама грузит вспомогательный
/// драйвер ядра (WinRing0) для доступа к MSR/SMBus — без прав администратора он не встаёт,
/// и датчики остаются пустыми. Тот же класс требования, что у счётчика FPS (см. FpsProbe),
/// не отдельная проблема: без элевации строки температуры в оверлее показывают прочерк,
/// а не мешают остальному приложению.
/// </summary>
public sealed class TemperatureProbe : IDisposable
{
    private Computer? _computer;
    private bool _available;
    private bool _initialized;

    public bool Available => _available;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // Без прав администратора Computer.Open() либо бросит, либо тихо не найдёт
        // ни одного датчика — не пытаемся, чтобы не платить временем на инициализацию впустую
        if (!FpsProbe.IsElevated) return;

        try
        {
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _computer.Open();
            _available = true;
        }
        catch
        {
            _computer = null;
            _available = false;
        }
    }

    public (double CpuTempC, double GpuTempC) Sample()
    {
        if (!_available || _computer is null) return (double.NaN, double.NaN);

        try
        {
            double cpu = double.NaN;
            double gpu = double.NaN;

            foreach (var hw in _computer.Hardware)
            {
                hw.Update();

                if (hw.HardwareType == HardwareType.Cpu)
                    cpu = BestTemperature(hw);
                else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    gpu = BestTemperature(hw);
            }

            return (cpu, gpu);
        }
        catch
        {
            return (double.NaN, double.NaN);
        }
    }

    /// <summary>
    /// «Package»/«Core (Tctl…»/«GPU Core» — сводный датчик приоритетно, иначе максимум
    /// по всем датчикам температуры этого устройства (у многодатчиковых GPU это горячее
    /// ядро, а не усреднённое число).
    /// </summary>
    private static double BestTemperature(IHardware hw)
    {
        double best = double.NaN;

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float v)
                continue;

            bool isPrimary = sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                           || sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                           || sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase);

            if (isPrimary) return v;
            if (double.IsNaN(best) || v > best) best = v;
        }

        return best;
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;
        _available = false;
    }
}
