namespace NetAudit.Core.Probes;

/// <summary>Один модуль памяти — то, что стоит в слоте.</summary>
public sealed class RamModule
{
    /// <summary>Обозначение слота на плате, как его называет BIOS: «DIMM 1», «DIMM_A2».</summary>
    public string Slot { get; init; } = "";

    /// <summary>Банк/канал в терминах BIOS: «P0 CHANNEL A». Отсюда же определяется канал.</summary>
    public string Bank { get; init; } = "";

    public string Manufacturer { get; init; } = "";
    public string PartNumber { get; init; } = "";
    public string SerialNumber { get; init; } = "";

    public long CapacityBytes { get; init; }

    /// <summary>Паспортная скорость модуля из SMBIOS, МТ/с. Это не всегда потолок SPD: BIOS
    /// без включённого профиля XMP/DOCP часто пишет сюда ту же цифру, что и рабочую.</summary>
    public int SpeedMts { get; init; }

    /// <summary>Реальная рабочая скорость, МТ/с.</summary>
    public int ConfiguredMts { get; init; }

    /// <summary>Рабочее напряжение, мВ. 1200 — штатное DDR4, 1350 — типичный XMP.</summary>
    public int VoltageMv { get; init; }

    /// <summary>Число рангов: 1Rx8, 2Rx8. Два ранга на модуль дают немного больше скорости.</summary>
    public int Ranks { get; init; }

    public int DataWidthBits { get; init; }
    public int TotalWidthBits { get; init; }

    /// <summary>DDR3/DDR4/DDR5 — расшифровка кода типа из SMBIOS.</summary>
    public string TypeName { get; init; } = "";

    public string FormFactor { get; init; } = "";

    /// <summary>Канал, вычисленный по названию банка: «A», «B» или пусто, если не разобрали.</summary>
    public string Channel { get; init; } = "";

    /// <summary>Разгонен ли модуль относительно того, что о нём говорит SMBIOS.</summary>
    public bool RunsBelowRating => SpeedMts > 0 && ConfiguredMts > 0 && ConfiguredMts < SpeedMts - 1;
}

/// <summary>Паспорт оперативной памяти: модули, слоты, возможности платы.</summary>
public sealed class RamInfo
{
    public IReadOnlyList<RamModule> Modules { get; init; } = [];

    /// <summary>Сколько слотов под память на плате всего.</summary>
    public int SlotsTotal { get; init; }

    /// <summary>Максимум, который плата берёт по паспорту, байт.</summary>
    public long MaxCapacityBytes { get; init; }

    /// <summary>Коррекция ошибок: «нет», «ECC» и т.п. — из SMBIOS Type 16.</summary>
    public string ErrorCorrection { get; init; } = "";

    /// <summary>Сумма объёмов модулей, байт.</summary>
    public long InstalledBytes => Modules.Sum(m => m.CapacityBytes);

    /// <summary>Сколько памяти видит Windows. Разница с установленной — забрана железом.</summary>
    public long VisibleBytes { get; init; }

    /// <summary>Отдано железу (встроенной графике, служебным областям), байт.</summary>
    public long HardwareReservedBytes => Math.Max(0, InstalledBytes - VisibleBytes);

    public int SlotsUsed => Modules.Count;

    public string TypeName => Modules.Count > 0 ? Modules[0].TypeName : "";

    /// <summary>Рабочая частота, МТ/с: берём по самому медленному модулю — так работает контроллер.</summary>
    public int ConfiguredMts => Modules.Count == 0 ? 0 : Modules.Min(m => m.ConfiguredMts);

    /// <summary>Сколько разных каналов задействовано. Два и больше — двухканальный режим.</summary>
    public int ChannelsUsed => Modules
        .Where(m => m.Channel.Length > 0)
        .Select(m => m.Channel)
        .Distinct()
        .Count();

    /// <summary>Модули из разных комплектов: разные партномера или объёмы. Частая причина нестабильности.</summary>
    public bool MixedModules =>
        Modules.Count > 1 &&
        (Modules.Select(m => m.PartNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ||
         Modules.Select(m => m.CapacityBytes).Distinct().Count() > 1);

    /// <summary>
    /// Теоретический предел пропускной способности, ГБ/с: частота × 8 байт на канал.
    /// Столько отдаёт контроллер памяти в идеале, реальные замеры всегда ниже.
    /// </summary>
    public double TheoreticalGbs
    {
        get
        {
            int channels = Math.Max(1, ChannelsUsed);
            return ConfiguredMts * 8.0 * channels / 1000.0;
        }
    }
}

/// <summary>Мгновенный срез: сколько памяти занято и чем именно.</summary>
public sealed class RamUsage
{
    public long TotalBytes { get; init; }
    public long AvailableBytes { get; init; }

    /// <summary>Обещано программам (commit charge) — включая то, что ушло в файл подкачки.</summary>
    public long CommittedBytes { get; init; }

    /// <summary>Потолок обещаний: физическая память плюс файл подкачки.</summary>
    public long CommitLimitBytes { get; init; }

    /// <summary>Кэш файлов, который Windows держит про запас и отдаст по первому требованию.</summary>
    public long StandbyBytes { get; init; }

    /// <summary>Изменённые страницы: ждут записи на диск, освободить их сразу нельзя.</summary>
    public long ModifiedBytes { get; init; }

    /// <summary>Совсем свободные страницы.</summary>
    public long FreeBytes { get; init; }

    public long PoolPagedBytes { get; init; }
    public long PoolNonpagedBytes { get; init; }

    /// <summary>Сжатая память: то, что Windows ужала вместо выгрузки на диск.</summary>
    public long CompressedBytes { get; init; }

    /// <summary>Чтений с диска из-за нехватки памяти в секунду. Больше сотни подряд — память кончилась.</summary>
    public double HardFaultsPerSec { get; init; }

    public double PageFaultsPerSec { get; init; }

    public string PageFilePath { get; init; } = "";
    public long PageFileAllocatedBytes { get; init; }
    public long PageFileUsedBytes { get; init; }
    public long PageFilePeakBytes { get; init; }

    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);

    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;

    /// <summary>
    /// Обещано против физической памяти. Выше 100% — программам обещано больше, чем есть
    /// планок, и разницу держит файл подкачки.
    /// </summary>
    public double CommitPercentOfPhysical => TotalBytes > 0 ? CommittedBytes * 100.0 / TotalBytes : 0;
}

/// <summary>Процесс и его аппетит к памяти — для строки «кто съел».</summary>
public readonly record struct RamProcess(string Name, int Count, long WorkingSetBytes, long PrivateBytes);
