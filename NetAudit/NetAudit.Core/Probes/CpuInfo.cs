namespace NetAudit.Core.Probes;

/// <summary>Один уровень кэша процессора.</summary>
public readonly record struct CpuCacheInfo(
    int Level,
    string Kind,
    long SizeBytes,
    int LineSize,
    int Instances,
    int SharedByThreads)
{
    /// <summary>Суммарный объём всех блоков этого уровня.</summary>
    public long TotalBytes => SizeBytes * Instances;
}

/// <summary>Паспорт процессора: всё, что известно без нагрузки.</summary>
public sealed class CpuInfo
{
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";

    /// <summary>Поколение по семейству и модели: «Zen+ (2018)», «Raptor Lake (2022–2023)».</summary>
    public string Architecture { get; init; } = "";

    public int Family { get; init; }
    public int Model { get; init; }
    public int Stepping { get; init; }

    /// <summary>Версия микрокода. Обновляется через BIOS и заплатки Windows.</summary>
    public string Microcode { get; init; } = "";

    /// <summary>Физические ядра. Ноль — опросить раскладку ядер не удалось.</summary>
    public int PhysicalCores { get; init; }

    public int LogicalCores { get; init; }

    /// <summary>Удалось ли вообще узнать число физических ядер.</summary>
    public bool CoresKnown => PhysicalCores > 0;

    /// <summary>
    /// Многопоточность на ядре: у AMD это SMT, у Intel — Hyper-Threading.
    /// При неизвестном числе физических ядер — <c>false</c>, и судить по этому
    /// значению нельзя: сначала <see cref="CoresKnown"/>.
    /// </summary>
    public bool SmtEnabled => PhysicalCores > 0 && LogicalCores > PhysicalCores;

    /// <summary>Быстрые ядра у гибридных процессоров Intel. Ноль, если процессор обычный.</summary>
    public int PerformanceCores { get; init; }

    /// <summary>Энергоэффективные ядра у гибридных процессоров Intel.</summary>
    public int EfficiencyCores { get; init; }

    public bool IsHybrid => EfficiencyCores > 0;

    /// <summary>Базовая частота по паспорту, МГц.</summary>
    public int BaseMhz { get; init; }

    public IReadOnlyList<CpuCacheInfo> Caches { get; init; } = [];

    /// <summary>
    /// Группы логических процессоров, делящих общий кэш последнего уровня. У AMD это
    /// кластеры ядер (CCX): обмен внутри группы быстрый, между группами идёт через
    /// внутреннюю шину и стоит втрое дороже.
    /// </summary>
    public IReadOnlyList<ulong> LastLevelCacheGroups { get; init; } = [];

    /// <summary>Наборы инструкций, которые процессор поддерживает.</summary>
    public IReadOnlyList<string> InstructionSets { get; init; } = [];

    /// <summary>Работает ли Windows поверх гипервизора: это стоит нескольких процентов скорости.</summary>
    public bool HypervisorPresent { get; init; }

    /// <summary>
    /// Включена ли защита на основе виртуализации — она тоже забирает производительность.
    /// <c>null</c> означает «узнать не удалось»: пространство имён DeviceGuard в WMI
    /// открыто только администратору. Раньше на его месте стояло <c>false</c>,
    /// и отчёт уверенно сообщал «защита выключена» там, где она работает.
    /// </summary>
    public bool? VbsEnabled { get; init; }

    public string Socket { get; init; } = "";

    public bool IsAmd => Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
    public bool IsIntel => Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    public CpuCacheInfo? Cache(int level, string kind = "данные") =>
        Caches.FirstOrDefault(c => c.Level == level && (level >= 2 || c.Kind == kind)) is { Level: > 0 } c ? c : null;
}

/// <summary>Что с процессором происходит прямо сейчас.</summary>
public sealed class CpuState
{
    /// <summary>Загрузка всех ядер, %.</summary>
    public double LoadPercent { get; init; }

    /// <summary>
    /// Текущая частота, МГц. Считается как базовая × производительность в процентах:
    /// значение выше базовой означает разгон в бусте, ниже — сброс частоты.
    /// NaN — счётчики не ответили.
    /// </summary>
    public double CurrentMhz { get; init; }

    public double PerformancePercent { get; init; }

    /// <summary>
    /// Ответили ли счётчики производительности хоть одной строкой. При <c>false</c>
    /// загрузка, производительность и частота — не «ноль», а «неизвестно»:
    /// повреждённые счётчики нельзя показывать как простаивающий процессор.
    /// </summary>
    public bool CountersAvailable { get; init; }

    public double TemperatureC { get; init; } = double.NaN;

    public string PowerScheme { get; init; } = "";

    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
}
