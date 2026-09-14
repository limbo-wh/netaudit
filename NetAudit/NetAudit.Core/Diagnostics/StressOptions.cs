namespace NetAudit.Core.Diagnostics;

/// <summary>Что и как долго мучить.</summary>
public sealed record StressOptions
{
    /// <summary>Сколько держать нагрузку. Меньше пяти минут ловит только грубые дефекты.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Процессор: все потоки под полной нагрузкой с проверкой результата вычислений.</summary>
    public bool Cpu { get; init; } = true;

    /// <summary>Память: запись и сверка контрольных шаблонов в большом блоке ОЗУ.</summary>
    public bool Memory { get; init; } = true;

    /// <summary>
    /// Накопитель: запись и сверка. По умолчанию выключен — многочасовая запись
    /// на SSD расходует его ресурс, и для поиска нестабильности системы это
    /// далеко не первый кандидат.
    /// </summary>
    public bool Disk { get; init; }

    /// <summary>
    /// Видеокарта: собственный вычислительный шейдер Direct3D 11 с проверкой
    /// результата. По умолчанию выключена — это самая горячая часть теста,
    /// и включать её стоит осознанно.
    /// </summary>
    public bool Gpu { get; init; }

    /// <summary>
    /// Какую долю СВОБОДНОЙ памяти занять под тест. Половина — безопасный потолок:
    /// система и открытые программы продолжают работать, а своп не начинает
    /// молотить (иначе тест меряет диск, а не память).
    /// </summary>
    public double MemoryFraction { get; init; } = 0.5;

    /// <summary>
    /// Температура процессора, выше которой тест останавливается сам, °C.
    /// Прежние 95 останавливали прогон на исправной новой машине: у Ryzen 7000/9000
    /// и Intel 13/14 поколений 95 °C под полной нагрузкой — проектный режим.
    /// </summary>
    public int CpuTempLimitC { get; init; } = (int)ThermalLimits.CpuStopC;

    /// <summary>
    /// Температура видеокарты, выше которой тест останавливается сам, °C.
    /// Карты, отдающие температуру горячей точки, в норме показывают 90–100 °C.
    /// </summary>
    public int GpuTempLimitC { get; init; } = (int)ThermalLimits.GpuStopC;

    /// <summary>Останавливаться на первой же ошибке вычислений или памяти.</summary>
    public bool StopOnFirstError { get; init; }

    /// <summary>Сколько потоков грузить. 0 — по числу логических ядер.</summary>
    public int Threads { get; init; }

    public static StressOptions Quick => new()
    {
        Duration = TimeSpan.FromMinutes(5),
        Cpu = true,
        Memory = true,
    };

    public static StressOptions Standard => new()
    {
        Duration = TimeSpan.FromMinutes(15),
        Cpu = true,
        Memory = true,
    };

    public static StressOptions Long => new()
    {
        Duration = TimeSpan.FromHours(1),
        Cpu = true,
        Memory = true,
    };

    /// <summary>Короткое описание для подтверждения перед запуском.</summary>
    public string Describe()
    {
        var parts = new List<string>(4);
        if (Cpu) parts.Add("процессор");
        if (Memory) parts.Add("память");
        if (Gpu) parts.Add("видеокарта");
        if (Disk) parts.Add("накопитель");

        string what = parts.Count == 0 ? "ничего" : string.Join(", ", parts);
        string how = Duration.TotalHours >= 1 ? $"{Duration.TotalHours:F0} ч"
                   : Duration.TotalMinutes >= 1 ? $"{Duration.TotalMinutes:F0} мин"
                   : $"{Duration.TotalSeconds:F0} с";

        return $"{what} — {how}";
    }
}
