using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace NetAudit.Core.Probes;

/// <summary>
/// Паспорт и состояние процессора.
///
/// Источники разные, и ни один не полон:
///   • реестр — имя, семейство, модель, степпинг, версия микрокода (без WMI и мгновенно);
///   • <c>GetLogicalProcessorInformation</c> — кэши всех уровней и то, какие ядра
///     делят общий кэш последнего уровня (у AMD по этому видно кластеры ядер);
///   • <c>GetLogicalProcessorInformationEx</c> — многопоточность на ядре и класс
///     энергоэффективности, по которому различаются быстрые и экономичные ядра
///     гибридных процессоров Intel;
///   • сама среда выполнения — поддержка наборов инструкций;
///   • WMI — сокет и текущая частота через счётчики.
/// </summary>
public static class CpuInfoProbe
{
    // ── Win32 ─────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(byte[]? buffer, ref uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, byte[]? buffer, ref uint returnLength);

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    // ── Паспорт ───────────────────────────────────────────────────────────

    public static Task<CpuInfo> CollectAsync() => Task.Run(Collect);

    public static CpuInfo Collect()
    {
        var (name, vendor, family, model, stepping, microcode, baseMhz) = ReadRegistry();
        var caches = ReadCaches(out var lastLevelGroups);
        var (physical, perf, eff) = ReadCoreLayout();

        return new CpuInfo
        {
            Name                 = name,
            Vendor               = vendor,
            Architecture         = ArchitectureName(vendor, family, model),
            Family               = family,
            Model                = model,
            Stepping             = stepping,
            Microcode            = microcode,
            // Ноль означает «не определено» и так и остаётся нулём. Прежняя подстановка
            // числа логических процессоров превращала неудачный опрос в утверждение
            // «физических ядер столько же, сколько потоков», и на обычном Ryzen
            // с включённым SMT отчёт сообщал, что многопоточность выключена
            PhysicalCores        = physical,
            LogicalCores         = Environment.ProcessorCount,
            PerformanceCores     = eff > 0 ? perf : 0,
            EfficiencyCores      = eff,
            BaseMhz              = baseMhz,
            Caches               = caches,
            LastLevelCacheGroups = lastLevelGroups,
            InstructionSets      = ReadInstructionSets(),
            HypervisorPresent    = ReadHypervisor(),
            VbsEnabled           = ReadVbs(),
            Socket               = ReadSocket(),
        };
    }

    // ── Состояние ─────────────────────────────────────────────────────────

    public static Task<CpuState> SampleStateAsync() => Task.Run(SampleState);

    public static CpuState SampleState()
    {
        double load = 0, performance = 0, frequency = 0;

        // Счётчики производительности Windows умеют разваливаться (повреждённый реестр
        // счётчиков, отключённая служба). Раньше это давало те же нули, что и полный
        // простой машины, и в отчёте появлялась «частота 0 МГц» без единого намёка,
        // что значение просто не получено. Поэтому отдельно храним факт «строка пришла»
        bool countersRead = false;

        try
        {
            // Класс счётчиков «Processor Information» точнее старого «Processor»: он знает
            // про буст. Свойства WMI не локализованы, в отличие от имён самих счётчиков
            using var s = new ManagementObjectSearcher(
                "SELECT PercentProcessorPerformance, PercentProcessorUtility, ProcessorFrequency "
              + "FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name = '_Total'");

            foreach (ManagementObject o in s.Get())
            {
                performance = ToDouble(o["PercentProcessorPerformance"]);
                load        = ToDouble(o["PercentProcessorUtility"]);
                frequency   = ToDouble(o["ProcessorFrequency"]);
                countersRead = true;
                break;
            }
        }
        catch { }

        int processes = 0, threads = 0;
        try
        {
            var all = Process.GetProcesses();
            processes = all.Length;
            foreach (var p in all)
            {
                try { threads += p.Threads.Count; } catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }

        return new CpuState
        {
            LoadPercent        = load,
            PerformancePercent = performance,
            // Частота без счётчиков — не ноль, а «неизвестно»: NaN не проходит ни одну
            // проверку «> 0», и строка о частоте просто не появляется в отчёте
            CurrentMhz         = !countersRead ? double.NaN
                               : frequency > 0 && performance > 0 ? frequency * performance / 100.0
                               : frequency,
            CountersAvailable  = countersRead,
            PowerScheme        = ReadPowerScheme(),
            ProcessCount       = processes,
            ThreadCount        = threads,
        };
    }

    private static double ToDouble(object? value)
    {
        try { return value is null ? 0 : Convert.ToDouble(value); }
        catch { return 0; }
    }

    // ── Реестр ────────────────────────────────────────────────────────────

    private static (string Name, string Vendor, int Family, int Model, int Stepping, string Microcode, int BaseMhz)
        ReadRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

            if (key is null) return ("", "", 0, 0, 0, "", 0);

            string name = (key.GetValue("ProcessorNameString")?.ToString() ?? "").Trim();
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ");

            string vendor = key.GetValue("VendorIdentifier")?.ToString()?.Trim() ?? "";
            if (vendor == "AuthenticAMD") vendor = "AMD";
            if (vendor == "GenuineIntel") vendor = "Intel";

            // «AMD64 Family 23 Model 8 Stepping 2» — разбирать проще, чем звать CPUID
            string identifier = key.GetValue("Identifier")?.ToString() ?? "";
            int family   = ExtractNumber(identifier, "Family");
            int model    = ExtractNumber(identifier, "Model");
            int stepping = ExtractNumber(identifier, "Stepping");

            // Ревизия микрокода лежит двоичным значением, младший байт первым
            string microcode = "";
            if (key.GetValue("Update Revision") is byte[] rev && rev.Length >= 4)
            {
                uint value = BitConverter.ToUInt32(rev, 0);
                if (value == 0 && rev.Length >= 8) value = BitConverter.ToUInt32(rev, 4);
                microcode = $"0x{value:X8}";
            }

            int baseMhz = key.GetValue("~MHz") is int mhz ? mhz : 0;

            return (name, vendor, family, model, stepping, microcode, baseMhz);
        }
        catch { return ("", "", 0, 0, 0, "", 0); }
    }

    private static int ExtractNumber(string text, string word)
    {
        int idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;

        string rest = text[(idx + word.Length)..].TrimStart();
        string digits = new([.. rest.TakeWhile(char.IsDigit)]);
        return int.TryParse(digits, out int value) ? value : 0;
    }

    // ── Кэши ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Кэши всех уровней. Разбор идёт по сырым байтам: запись
    /// <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION</c> занимает ровно 32 байта — маска
    /// логических процессоров, тип связи по смещению 8, дальше описание кэша.
    ///
    /// Старый вариант функции намеренно предпочтён расширенному: у расширенного
    /// размер полей описания кэша менялся между версиями заголовков Windows, и
    /// разбирать его по смещениям рискованно. Плата за надёжность — на машинах
    /// с более чем 64 логическими процессорами будет видна только первая группа;
    /// для домашних машин это не ограничение.
    /// </summary>
    private static List<CpuCacheInfo> ReadCaches(out List<ulong> lastLevelGroups)
    {
        lastLevelGroups = [];
        var result = new List<CpuCacheInfo>();

        try
        {
            uint size = 0;
            GetLogicalProcessorInformation(null, ref size);
            if (size == 0) return result;

            var buf = new byte[size];
            if (!GetLogicalProcessorInformation(buf, ref size)) return result;

            const int RecordSize = 32;

            // Ключ группировки — уровень, тип и размер: одинаковых блоков в процессоре
            // много, а показывать надо «L2: 6 × 512 КБ», а не шесть одинаковых строк
            var groups = new Dictionary<(int Level, string Kind, long Size), (int Count, int Line, int Threads)>();
            int maxLevel = 0;

            for (int pos = 0; pos + RecordSize <= size; pos += RecordSize)
            {
                if (BitConverter.ToInt32(buf, pos + 8) != RelationCache) continue;

                ulong mask  = BitConverter.ToUInt64(buf, pos);
                byte level  = buf[pos + 16];
                int line    = BitConverter.ToUInt16(buf, pos + 18);
                long bytes  = BitConverter.ToUInt32(buf, pos + 20);
                int type    = BitConverter.ToInt32(buf, pos + 24);

                string kind = type switch
                {
                    0 => "объединённый",
                    1 => "инструкции",
                    2 => "данные",
                    3 => "трассировка",
                    _ => "",
                };

                int threads = System.Numerics.BitOperations.PopCount(mask);
                var key = ((int)level, kind, bytes);

                groups.TryGetValue(key, out var acc);
                groups[key] = (acc.Count + 1, line, threads);

                if (level > maxLevel) { maxLevel = level; lastLevelGroups = []; }
                if (level == maxLevel) lastLevelGroups.Add(mask);
            }

            foreach (var ((level, kind, bytes), (count, line, threads)) in groups.OrderBy(g => g.Key.Level))
                result.Add(new CpuCacheInfo(level, kind, bytes, line, count, threads));
        }
        catch { }

        return result;
    }

    // ── Раскладка ядер ────────────────────────────────────────────────────

    /// <summary>
    /// Сколько физических ядер и как они делятся на быстрые и экономичные.
    ///
    /// Здесь уже нужен расширенный вариант функции: класс энергоэффективности
    /// (по нему различаются P- и E-ядра гибридных процессоров Intel) в старом
    /// не сообщается вовсе. Смещения внутри описания ядра между версиями Windows
    /// не менялись: признаки по смещению 8, класс по 9.
    /// </summary>
    private static (int Physical, int Performance, int Efficiency) ReadCoreLayout()
    {
        try
        {
            uint size = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref size);
            if (size == 0) return (0, 0, 0);

            var buf = new byte[size];
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref size)) return (0, 0, 0);

            int physical = 0;
            var byClass = new Dictionary<byte, int>();

            int pos = 0;
            while (pos + 8 <= size)
            {
                int recordSize = BitConverter.ToInt32(buf, pos + 4);
                if (recordSize <= 0 || pos + recordSize > size) break;

                physical++;
                byte efficiencyClass = buf[pos + 9];
                byClass.TryGetValue(efficiencyClass, out int n);
                byClass[efficiencyClass] = n + 1;

                pos += recordSize;
            }

            // Один класс на всех — процессор обычный. Несколько — гибридный, и старший
            // класс означает быстрые ядра
            if (byClass.Count <= 1) return (physical, physical, 0);

            byte best = byClass.Keys.Max();
            int fast = byClass[best];
            return (physical, fast, physical - fast);
        }
        catch { return (0, 0, 0); }
    }

    // ── Наборы инструкций ─────────────────────────────────────────────────

    /// <summary>
    /// Что процессор умеет из того, что заметно ускоряет счёт. Список намеренно
    /// короткий: перечислять все три десятка расширений бессмысленно, важны те,
    /// от которых зависят игры, архиваторы, шифрование и нейросети.
    /// </summary>
    private static List<string> ReadInstructionSets()
    {
        var sets = new List<string>();

        void Add(bool supported, string name) { if (supported) sets.Add(name); }

        Add(Sse42.IsSupported,   "SSE4.2");
        Add(Avx.IsSupported,     "AVX");
        Add(Avx2.IsSupported,    "AVX2");
        Add(Fma.IsSupported,     "FMA");
        Add(Avx512F.IsSupported, "AVX-512");
        Add(AvxVnni.IsSupported, "AVX-VNNI");
        Add(Aes.IsSupported,     "AES-NI");
        Add(Pclmulqdq.IsSupported, "PCLMULQDQ");
        Add(Bmi1.IsSupported && Bmi2.IsSupported, "BMI1/2");
        Add(Popcnt.IsSupported,  "POPCNT");
        Add(HasSha(),            "SHA");

        return sets;
    }

    /// <summary>
    /// Отдельного класса для инструкций SHA в среде выполнения нет, поэтому
    /// спрашиваем процессор напрямую: лист 7, подлист 0, бит 29 в регистре EBX.
    /// </summary>
    private static bool HasSha()
    {
        try
        {
            if (!X86Base.IsSupported) return false;
            var (_, ebx, _, _) = X86Base.CpuId(7, 0);
            return (ebx & (1 << 29)) != 0;
        }
        catch { return false; }
    }

    // ── Прочее окружение ──────────────────────────────────────────────────

    private static bool ReadHypervisor()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
            foreach (ManagementObject o in s.Get())
                return o["HypervisorPresent"] is bool b && b;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Включена ли защита на основе виртуализации. Три исхода, а не два:
    /// <c>null</c> — узнать не удалось.
    ///
    /// Пространство имён DeviceGuard в WMI открывается только администратору, и
    /// прежний <c>catch { } return false</c> у обычного пользователя означал
    /// «защита выключена» — то есть отчёт уверенно врал ровно там, где VBS чаще
    /// всего и включена. Поэтому при отказе WMI пробуем реестр: ветка
    /// <c>Control\DeviceGuard</c> читается без прав администратора.
    /// </summary>
    private static bool? ReadVbs()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                @"\\.\root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");

            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["VirtualizationBasedSecurityStatus"]) == 2;   // 2 — работает
        }
        catch { }

        // Реестр говорит о заданной настройке, а не о том, что VBS действительно
        // поднялась (для этого нужны ещё и возможности железа), но это неизмеримо
        // ближе к правде, чем безусловное «выключена»
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard");

            if (key?.GetValue("EnableVirtualizationBasedSecurity") is int enabled)
                return enabled != 0;
        }
        catch { }

        return null;
    }

    private static string ReadSocket()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SocketDesignation FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
                return o["SocketDesignation"]?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Активная схема электропитания. В экономичной схеме процессор держит низкую
    /// частоту — это первое, что объясняет вдвое худший результат замера на исправной
    /// машине, поэтому строка в отчёте нужна.
    /// </summary>
    private static string ReadPowerScheme()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");

            string? active = key?.GetValue("ActivePowerScheme")?.ToString();
            if (string.IsNullOrEmpty(active)) return "";

            return active.ToLowerInvariant() switch
            {
                "381b4222-f694-41f0-9685-ff5bb260df2e" => "Сбалансированная",
                "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "Высокая производительность",
                "a1841308-3541-4fab-bc81-f71556f20b4a" => "Экономия энергии",
                "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Максимальная производительность",
                _ => "своя схема",
            };
        }
        catch { return ""; }
    }

    // ── Поколения ─────────────────────────────────────────────────────────

    /// <summary>
    /// Поколение по семейству и модели. Таблица неполная и это осознанно: неизвестное
    /// сочетание отдаёт пустую строку, и отчёт просто не показывает поколение — это
    /// честнее, чем назвать процессор чужим именем.
    /// </summary>
    private static string ArchitectureName(string vendor, int family, int model)
    {
        if (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase))
        {
            return (family, model) switch
            {
                (23, 1) or (23, 17) or (23, 32) => "Zen (2017)",
                (23, 8) or (23, 24)             => "Zen+ (2018)",
                (23, _)                         => "Zen 2 (2019–2020)",
                (25, 33) or (25, 80)            => "Zen 3 (2020–2022)",
                (25, 97) or (25, 116) or (25, 120) => "Zen 4 (2022–2023)",
                (25, _)                         => "Zen 3/4",
                (26, _)                         => "Zen 5 (2024)",
                (>= 21 and <= 22, _)            => "Bulldozer/Excavator (2011–2015)",
                _                               => "",
            };
        }

        if (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) && family == 6)
        {
            return model switch
            {
                60 or 69 or 70    => "Haswell (2013–2014)",
                61 or 71 or 79    => "Broadwell (2014–2015)",
                78 or 94          => "Skylake (2015–2016)",
                142 or 158        => "Kaby/Coffee Lake (2016–2019)",
                165 or 166        => "Comet Lake (2020)",
                167 or 151        => "Rocket/Alder Lake (2021)",
                140 or 141        => "Tiger Lake (2020–2021)",
                154               => "Alder Lake (2021–2022)",
                183 or 186 or 191 => "Raptor Lake (2022–2023)",
                170 or 172        => "Meteor Lake (2023–2024)",
                197 or 198        => "Arrow Lake (2024)",
                _                 => "",
            };
        }

        return "";
    }
}
