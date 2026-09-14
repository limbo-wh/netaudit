using LibreHardwareMonitor.Hardware;

namespace NetAudit.Core.Probes;

/// <summary>
/// Температура CPU/GPU через LibreHardwareMonitorLib. Библиотека сама грузит вспомогательный
/// драйвер ядра (WinRing0) для доступа к MSR/SMBus — без прав администратора он не встаёт,
/// и датчики остаются пустыми. Тот же класс требования, что у счётчика FPS (см. FpsProbe),
/// не отдельная проблема: без элевации строки температуры в оверлее показывают прочерк,
/// а не мешают остальному приложению.
///
/// Важно: процессор и видеокарта читаются разными путями. Процессору нужен драйвер ядра,
/// а видеокарту NVIDIA библиотека опрашивает через NVAPI, драйвера не требующий. Поэтому
/// доступность считается раздельно: на машине с заблокированным драйвером температура
/// процессора недоступна, а видеокарты — читается полностью.
/// </summary>
public sealed class TemperatureProbe : IDisposable
{
    private Computer? _computer;
    private bool _available;
    private bool _cpuAvailable;
    private bool _gpuAvailable;
    private bool _initialized;

    /// <summary>Есть хоть один живой датчик температуры — процессора или видеокарты.</summary>
    public bool Available => _available;

    /// <summary>Температура процессора читается. Требует работающего драйвера ядра.</summary>
    public bool CpuAvailable => _cpuAvailable;

    /// <summary>Температура видеокарты читается. У NVIDIA идёт мимо драйвера ядра.</summary>
    public bool GpuAvailable => _gpuAvailable;

    /// <summary>
    /// Почему датчики недоступны — человеческим языком. Пусто, если всё в порядке.
    ///
    /// Нужно, чтобы не сваливать все причины в одно «нужны права администратора».
    /// На этой машине программа работает с правами администратора, а датчиков всё
    /// равно нет: Windows отказывается запускать драйвер чтения (WinRing0) —
    /// «файл содержит вирус или потенциально нежелательное ПО». Этот драйвер входит
    /// в список уязвимых драйверов Microsoft и блокируется, когда включена защита
    /// от них (по умолчанию она включена). Пользователю важно понимать, что это
    /// не поломка железа и не сбой программы.
    /// </summary>
    public string Unavailable { get; private set; } = "";

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // Без прав администратора драйвер ядра не встанет и процессор останется без
        // температуры — но видеокарту NVIDIA библиотека читает через NVAPI, которому
        // прав не нужно. Поэтому вместо раннего выхода поднимаем только ту половину,
        // которая всё равно заработает: проверено, обычный пользователь получает
        // и ядро, и горячую точку, и память карты
        bool elevated = FpsProbe.IsElevated;

        // Коротко: кто выводит эту причину, сам говорит, что именно недоступно
        if (!elevated)
            Unavailable = "нужны права администратора";

        try
        {
            _computer = new Computer { IsCpuEnabled = elevated, IsGpuEnabled = true };
            _computer.Open();

            // Драйвер мог не запуститься — тогда библиотека поднимется, но датчиков
            // температуры не найдёт ни одного. Отличаем это от настоящей работы
            (_cpuAvailable, _gpuAvailable) = ProbeAvailability(_computer);
            _available = _cpuAvailable || _gpuAvailable;

            // Причину про права уже назвали выше — она точнее общей
            if (!_cpuAvailable && elevated)
            {
                Unavailable = DriverBlocked()
                    ? "Windows заблокировала драйвер чтения датчиков как уязвимый"
                    : "датчики температуры не найдены";
            }

            if (!_available)
            {
                try { _computer.Close(); } catch { }
                _computer = null;
            }
        }
        catch
        {
            _computer = null;
            _available = false;
            _cpuAvailable = false;
            _gpuAvailable = false;
            Unavailable = "драйвер чтения датчиков не запустился";
        }
    }

    /// <summary>
    /// Живы ли датчики процессора и видеокарты по отдельности.
    ///
    /// Ноль — не показание, а молчащий датчик. Заблокированный драйвер не убирает
    /// датчик «Core (Tctl/Tdie)» из списка, а отдаёт его со значением 0,0 — и без
    /// этой проверки приложение показывало бы процессор с температурой 0 °C вместо
    /// честного «узнать не удалось». Проверено на машине с включённой защитой от
    /// уязвимых драйверов.
    /// </summary>
    private static (bool Cpu, bool Gpu) ProbeAvailability(Computer computer)
    {
        bool cpu = false, gpu = false;

        try
        {
            foreach (var hw in computer.Hardware)
            {
                hw.Update();

                bool live = false;
                foreach (var sensor in hw.Sensors)
                    if (sensor.SensorType == SensorType.Temperature && IsLive(sensor.Value))
                        live = true;

                if (!live) continue;

                if (hw.HardwareType == HardwareType.Cpu) cpu = true;
                else if (IsGpu(hw.HardwareType)) gpu = true;
            }
        }
        catch { }

        return (cpu, gpu);
    }

    private static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>
    /// Показание, которому можно верить. Ноль и отрицательные значения означают,
    /// что датчик не отвечает: настоящий кремний холоднее нуля не бывает.
    /// </summary>
    private static bool IsLive(float? value) => value is float v && v > 0 && !float.IsNaN(v);

    /// <summary>
    /// Включена ли в Windows защита от уязвимых драйверов. Именно она не даёт
    /// запуститься WinRing0, которым пользуется библиотека чтения датчиков.
    /// </summary>
    private static bool DriverBlocked()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\CI\Config");

            return key?.GetValue("VulnerableDriverBlocklistEnable") is int v && v != 0;
        }
        catch { return false; }
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
                    cpu = CpuTemperature(hw);
                else if (IsGpu(hw.HardwareType))
                    gpu = HottestTemperature(hw);
            }

            return (cpu, gpu);
        }
        catch
        {
            return (double.NaN, double.NaN);
        }
    }

    /// <summary>Температура видеокарты в разбивке по датчикам. Любое поле может быть NaN.</summary>
    public readonly record struct GpuTemperatures(double CoreC, double HotSpotC, double MemoryC)
    {
        /// <summary>Хоть один датчик ответил.</summary>
        public bool Any => !double.IsNaN(CoreC) || !double.IsNaN(HotSpotC) || !double.IsNaN(MemoryC);

        /// <summary>
        /// Насколько горячая точка обгоняет ядро. У исправной карты со свежим термоинтерфейсом
        /// под нагрузкой это 10–15 °C; разрыв за 25 °C означает, что термопаста высохла.
        /// </summary>
        public double HotSpotDeltaC =>
            double.IsNaN(HotSpotC) || double.IsNaN(CoreC) ? double.NaN : HotSpotC - CoreC;
    }

    /// <summary>
    /// Раздельные температуры видеокарты: ядро, горячая точка кристалла и память.
    ///
    /// Зачем нужно. Ядро — самый холодный из трёх датчиков, и именно его отдаёт
    /// <c>nvidia-smi</c>. Виснет же карта обычно не от него: горячая точка и память
    /// идут на 10–30 °C выше, а аварийная защита следит за ядром и потому молчит.
    /// Классический симптом такой перегретой памяти — чёрный экран с вентиляторами
    /// на максимум под долгой игровой нагрузкой, при спокойных цифрах в мониторинге.
    /// </summary>
    public GpuTemperatures SampleGpu()
    {
        if (!_gpuAvailable || _computer is null)
            return new GpuTemperatures(double.NaN, double.NaN, double.NaN);

        try
        {
            foreach (var hw in _computer.Hardware)
            {
                if (!IsGpu(hw.HardwareType)) continue;

                hw.Update();

                double core = double.NaN, hot = double.NaN, mem = double.NaN;

                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || !IsLive(sensor.Value))
                        continue;

                    double v = sensor.Value!.Value;
                    string name = sensor.Name;

                    // Имена датчиков различаются у производителей: у NVIDIA «GPU Hot Spot»
                    // и «GPU Memory Junction», у AMD «GPU Hot Spot» и «GPU Memory»
                    if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase))
                        hot = v;
                    else if (name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                        mem = v;
                    else if (name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        core = v;
                }

                return new GpuTemperatures(core, hot, mem);
            }
        }
        catch { }

        return new GpuTemperatures(double.NaN, double.NaN, double.NaN);
    }

    /// <summary>
    /// Сводный датчик процессора: «Package» у Intel, «Core (Tctl/Tdie)» у AMD. Если
    /// сводного нет — максимум по ядрам.
    /// </summary>
    private static double CpuTemperature(IHardware hw)
    {
        double best = double.NaN;

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || !IsLive(sensor.Value))
                continue;

            double v = sensor.Value!.Value;

            if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
             || sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                return v;

            if (double.IsNaN(best) || v > best) best = v;
        }

        return best;
    }

    /// <summary>
    /// Самый горячий датчик устройства. Для видеокарты берём именно максимум, а не
    /// ядро: раньше здесь стоял выход по первому же совпадению с «GPU Core», и до
    /// горячей точки с памятью расчёт не доходил никогда — мониторинг и аварийный
    /// порог работали по самому холодному из трёх датчиков.
    /// </summary>
    private static double HottestTemperature(IHardware hw)
    {
        double best = double.NaN;

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || !IsLive(sensor.Value))
                continue;

            double v = sensor.Value!.Value;
            if (double.IsNaN(best) || v > best) best = v;
        }

        return best;
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;
        _available = false;
        _cpuAvailable = false;
        _gpuAvailable = false;
    }
}
