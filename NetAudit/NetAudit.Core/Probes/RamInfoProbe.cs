using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace NetAudit.Core.Probes;

/// <summary>
/// Паспорт и состояние оперативной памяти.
///
/// Главный источник — сырые таблицы SMBIOS, которые BIOS оставляет в памяти:
/// <c>GetSystemFirmwareTable('RSMB')</c>. WMI (<c>Win32_PhysicalMemory</c>) читает
/// ровно эти же таблицы, но по дороге теряет часть полей — число рангов, диапазон
/// напряжений, тип коррекции ошибок. Разбор своими руками стоит сотни строк, зато
/// не требует ни прав администратора, ни запуска внешних процессов, и отдаёт всё,
/// что вообще знает о планках сама машина.
///
/// Чего здесь принципиально нет — таймингов (CL-tRCD-tRP-tRAS). Их в SMBIOS не
/// бывает: они лежат в микросхеме SPD на самой планке и читаются по шине SMBus,
/// а это уже драйвер уровня ядра. Косвенная замена — измеренная задержка
/// случайного доступа в <see cref="Diagnostics.RamBenchmark"/>.
/// </summary>
public static class RamInfoProbe
{
    // ── Win32 ─────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(
        uint firmwareTableProviderSignature, uint firmwareTableId,
        byte[]? buffer, uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation info, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public int cb;
        public nint CommitTotal;
        public nint CommitLimit;
        public nint CommitPeak;
        public nint PhysicalTotal;
        public nint PhysicalAvailable;
        public nint SystemCache;
        public nint KernelTotal;
        public nint KernelPaged;
        public nint KernelNonpaged;
        public nint PageSize;
        public int HandleCount;
        public int ProcessCount;
        public int ThreadCount;
    }

    /// <summary>'RSMB' — таблицы SMBIOS в сыром виде.</summary>
    private const uint RawSmbios = 0x52534D42;

    // ── Паспорт ───────────────────────────────────────────────────────────

    public static Task<RamInfo> CollectAsync() => Task.Run(Collect);

    public static RamInfo Collect()
    {
        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);

        long visible = (long)mem.ullTotalPhys;

        // Объём, который видит Windows, знает только сама Windows — в SMBIOS его нет,
        // поэтому подставляем его в разобранный паспорт отдельно
        var fromSmbios = TryReadSmbios();
        if (fromSmbios is not null)
            return new RamInfo
            {
                Modules          = fromSmbios.Modules,
                EmptySlots       = fromSmbios.EmptySlots,
                SlotsTotal       = fromSmbios.SlotsTotal,
                MaxCapacityBytes = fromSmbios.MaxCapacityBytes,
                ErrorCorrection  = fromSmbios.ErrorCorrection,
                VisibleBytes     = visible,
            };

        return CollectFromWmi(visible);
    }

    // ── Состояние ─────────────────────────────────────────────────────────

    /// <summary>
    /// Сколько физической памяти сейчас свободно. Дешёвый вызов без WMI — тесты
    /// спрашивают это, чтобы решить, сколько памяти можно занять под замер.
    /// </summary>
    public static long AvailablePhysicalBytes()
    {
        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref mem) ? (long)mem.ullAvailPhys : 0;
    }

    public static Task<RamUsage> SampleUsageAsync() => Task.Run(SampleUsage);

    public static RamUsage SampleUsage()
    {
        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);

        long commit = 0, commitLimit = 0, poolPaged = 0, poolNonpaged = 0;
        if (GetPerformanceInfo(out var pi, Marshal.SizeOf<PerformanceInformation>()))
        {
            long page = pi.PageSize;
            commit       = pi.CommitTotal * page;
            commitLimit  = pi.CommitLimit * page;
            poolPaged    = pi.KernelPaged * page;
            poolNonpaged = pi.KernelNonpaged * page;
        }

        var perf = ReadMemoryCounters();
        var pf   = ReadPageFile();

        return new RamUsage
        {
            TotalBytes             = (long)mem.ullTotalPhys,
            AvailableBytes         = (long)mem.ullAvailPhys,
            CommittedBytes         = commit,
            CommitLimitBytes       = commitLimit,
            PoolPagedBytes         = poolPaged,
            PoolNonpagedBytes      = poolNonpaged,
            StandbyBytes           = perf.Standby,
            ModifiedBytes          = perf.Modified,
            FreeBytes              = perf.Free,
            HardFaultsPerSec       = perf.HardFaults,
            PageFaultsPerSec       = perf.PageFaults,
            CompressedBytes        = ReadCompressedBytes(),
            PageFilePath           = pf.Path,
            PageFileAllocatedBytes = pf.Allocated,
            PageFileUsedBytes      = pf.Used,
            PageFilePeakBytes      = pf.Peak,
        };
    }

    /// <summary>
    /// Самые прожорливые процессы, сгруппированные по имени: браузер из двадцати
    /// процессов иначе занимает весь список и прячет всё остальное.
    /// </summary>
    public static List<RamProcess> TopProcesses(int count)
    {
        var byName = new Dictionary<string, (int n, long ws, long priv)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                byName.TryGetValue(p.ProcessName, out var acc);
                byName[p.ProcessName] = (acc.n + 1,
                                         acc.ws + p.WorkingSet64,
                                         acc.priv + p.PrivateMemorySize64);
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        return [.. byName
            .Select(kv => new RamProcess(kv.Key, kv.Value.n, kv.Value.ws, kv.Value.priv))
            .OrderByDescending(x => x.WorkingSetBytes)
            .Take(count)];
    }

    // ── Счётчики памяти ───────────────────────────────────────────────────

    /// <summary>
    /// Список ожидания, изменённые страницы и обращения к диску из-за нехватки памяти.
    /// Идём через WMI, а не через <c>PerformanceCounter</c>: имена категорий счётчиков
    /// в Windows локализованы (на русской системе категория называется «Память»), а
    /// свойства WMI-класса всегда английские и одинаковы на любой локали.
    /// </summary>
    private static (long Standby, long Modified, long Free, double HardFaults, double PageFaults) ReadMemoryCounters()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT StandbyCacheCoreBytes, StandbyCacheNormalPriorityBytes, StandbyCacheReserveBytes, "
              + "ModifiedPageListBytes, FreeAndZeroPageListBytes, PagesInputPersec, PageFaultsPersec "
              + "FROM Win32_PerfFormattedData_PerfOS_Memory");

            foreach (ManagementObject o in s.Get())
            {
                long standby = Num(o, "StandbyCacheCoreBytes")
                             + Num(o, "StandbyCacheNormalPriorityBytes")
                             + Num(o, "StandbyCacheReserveBytes");

                return (standby,
                        Num(o, "ModifiedPageListBytes"),
                        Num(o, "FreeAndZeroPageListBytes"),
                        Num(o, "PagesInputPersec"),
                        Num(o, "PageFaultsPersec"));
            }
        }
        catch { }

        return (0, 0, 0, 0, 0);
    }

    private static long Num(ManagementBaseObject o, string name)
    {
        try { return o[name] is null ? 0 : Convert.ToInt64(o[name]); }
        catch { return 0; }
    }

    /// <summary>
    /// Сжатая память живёт в системном процессе «Memory Compression»: Windows ужимает
    /// страницы вместо выгрузки на диск, и весь сжатый объём числится его рабочим набором.
    /// </summary>
    private static long ReadCompressedBytes()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT WorkingSetSize FROM Win32_Process WHERE Name = 'Memory Compression'");
            foreach (ManagementObject o in s.Get())
                return Num(o, "WorkingSetSize");
        }
        catch { }
        return 0;
    }

    private static (string Path, long Allocated, long Used, long Peak) ReadPageFile()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
            foreach (ManagementObject o in s.Get())
                return (o["Name"]?.ToString() ?? "",
                        Num(o, "AllocatedBaseSize") * 1048576,
                        Num(o, "CurrentUsage")      * 1048576,
                        Num(o, "PeakUsage")         * 1048576);
        }
        catch { }
        return ("", 0, 0, 0);
    }

    // ── Разбор SMBIOS ─────────────────────────────────────────────────────

    private static RamInfo? TryReadSmbios()
    {
        try
        {
            uint size = GetSystemFirmwareTable(RawSmbios, 0, null, 0);
            if (size == 0) return null;

            var buf = new byte[size];
            if (GetSystemFirmwareTable(RawSmbios, 0, buf, size) != size) return null;

            // Заголовок RawSMBIOSData: метод вызова, версия SMBIOS (мажор/минор),
            // ревизия DMI, длина таблиц — сами таблицы начинаются с восьмого байта
            if (buf.Length <= 8) return null;
            int tablesLength = BitConverter.ToInt32(buf, 4);
            int end = Math.Min(buf.Length, 8 + tablesLength);

            var modules   = new List<RamModule>();
            var emptySlots = new List<string>();
            int slotsTotal = 0;
            long maxCapacity = 0;
            string ecc = "";

            int pos = 8;
            while (pos + 4 <= end)
            {
                byte type = buf[pos];
                byte length = buf[pos + 1];
                if (length < 4) break;

                int strStart = pos + length;
                var strings = ReadStrings(buf, strStart, end, out int next);

                switch (type)
                {
                    case 16:   // Physical Memory Array — сама подсистема памяти
                        (slotsTotal, maxCapacity, ecc) = ParseArray(buf, pos, length, slotsTotal, maxCapacity, ecc);
                        break;

                    case 17:   // Memory Device — один слот, заполненный или пустой
                        var m = ParseDevice(buf, pos, length, strings);
                        if (m is not null) modules.Add(m);
                        else
                        {
                            string empty = EmptySlotName(buf, pos, length, strings);
                            if (empty.Length > 0) emptySlots.Add(empty);
                        }
                        break;

                    case 127:  // End-of-Table
                        pos = end;
                        continue;
                }

                pos = next;
                if (next <= strStart) break;   // защита от порчи таблицы: не зацикливаться
            }

            if (modules.Count == 0) return null;

            return new RamInfo
            {
                Modules          = modules,
                EmptySlots       = emptySlots,
                SlotsTotal       = slotsTotal > 0 ? slotsTotal : modules.Count + emptySlots.Count,
                MaxCapacityBytes = maxCapacity,
                ErrorCorrection  = ecc,
            };
        }
        catch { return null; }
    }

    private static (int slots, long maxCapacity, string ecc) ParseArray(
        byte[] b, int pos, int length, int slots, long maxCapacity, string ecc)
    {
        if (length >= 0x0F)
        {
            byte correction = b[pos + 0x06];
            ecc = correction switch
            {
                3 => "нет",
                4 => "контроль чётности",
                5 => "ECC, одиночные биты",
                6 => "ECC, многобитовая",
                7 => "CRC",
                _ => ecc,
            };

            uint maxKb = BitConverter.ToUInt32(b, pos + 0x07);
            if (maxKb == 0x8000_0000 && length >= 0x17)
                maxCapacity = BitConverter.ToInt64(b, pos + 0x0F);   // расширенное поле, сразу в байтах
            else if (maxKb > 0)
                maxCapacity = (long)maxKb * 1024;

            slots += BitConverter.ToUInt16(b, pos + 0x0D);
        }
        return (slots, maxCapacity, ecc);
    }

    /// <summary>
    /// Как BIOS называет пустой слот: «DIMM 0 (канал A)». Нужно, чтобы показать
    /// владельцу, куда, по мнению прошивки, можно доставить планку — с оговоркой,
    /// что прошивка тут врёт чаще, чем хотелось бы.
    /// </summary>
    private static string EmptySlotName(byte[] b, int pos, int length, List<string> strings)
    {
        if (length < 0x12) return "";

        string locator = Str(strings, b[pos + 0x10]);
        string bank    = Str(strings, b[pos + 0x11]);
        string channel = GuessChannel(bank, locator);

        if (locator.Length == 0) return "";
        return channel.Length > 0 ? $"{locator} (канал {channel})" : locator;
    }

    private static RamModule? ParseDevice(byte[] b, int pos, int length, List<string> strings)
    {
        if (length < 0x15) return null;

        // Размер: 0 — слот пуст, 0x7FFF — не влезло в два байта, читать расширенное поле
        ushort sizeRaw = BitConverter.ToUInt16(b, pos + 0x0C);
        if (sizeRaw == 0) return null;

        long capacity;
        if (sizeRaw == 0x7FFF && length >= 0x20)
        {
            uint ext = BitConverter.ToUInt32(b, pos + 0x1C) & 0x7FFF_FFFF;
            capacity = (long)ext * 1024 * 1024;
        }
        else
        {
            // Бит 15 задаёт единицу измерения: 0 — мегабайты, 1 — килобайты
            bool inKb = (sizeRaw & 0x8000) != 0;
            long value = sizeRaw & 0x7FFF;
            capacity = inKb ? value * 1024 : value * 1024 * 1024;
        }

        string locator = Str(strings, b[pos + 0x10]);
        string bank    = Str(strings, b[pos + 0x11]);

        int speed = BitConverter.ToUInt16(b, pos + 0x15);
        if (speed == 0xFFFF && length >= 0x58 + 4) speed = (int)BitConverter.ToUInt32(b, pos + 0x54);

        int configured = length >= 0x22 ? BitConverter.ToUInt16(b, pos + 0x20) : 0;
        if (configured == 0xFFFF && length >= 0x5C) configured = (int)BitConverter.ToUInt32(b, pos + 0x58);
        if (configured == 0) configured = speed;

        int ranks = length >= 0x1C ? b[pos + 0x1B] & 0x0F : 0;
        int voltage = length >= 0x28 ? BitConverter.ToUInt16(b, pos + 0x26) : 0;

        return new RamModule
        {
            Slot           = locator,
            Bank           = bank,
            Channel        = GuessChannel(bank, locator),
            Manufacturer   = CleanVendor(Str(strings, b[pos + 0x17])),
            SerialNumber   = length >= 0x19 ? Str(strings, b[pos + 0x18]) : "",
            PartNumber     = length >= 0x1B ? Str(strings, b[pos + 0x1A]).Trim() : "",
            CapacityBytes  = capacity,
            SpeedMts       = speed,
            ConfiguredMts  = configured,
            VoltageMv      = voltage,
            Ranks          = ranks,
            DataWidthBits  = BitConverter.ToUInt16(b, pos + 0x0A),
            TotalWidthBits = BitConverter.ToUInt16(b, pos + 0x08),
            TypeName       = MemoryTypeName(b[pos + 0x12]),
            FormFactor     = FormFactorNameSmbios(b[pos + 0x0E]),
        };
    }

    /// <summary>
    /// Канал по названию банка. BIOS называет их по-разному («P0 CHANNEL A»,
    /// «BANK 0», «ChannelA-DIMM1»), поэтому ищем букву после слова «channel»,
    /// а если его нет — последнюю букву A–D в обозначении слота.
    /// </summary>
    private static string GuessChannel(string bank, string locator)
    {
        foreach (string source in new[] { bank, locator })
        {
            if (source.Length == 0) continue;

            int idx = source.IndexOf("channel", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                for (int i = idx + 7; i < source.Length; i++)
                {
                    char c = char.ToUpperInvariant(source[i]);
                    if (c is >= 'A' and <= 'D') return c.ToString();
                    if (char.IsDigit(c)) return c.ToString();
                }
            }
        }

        // «DIMM_A2» — буква сразу после подчёркивания или дефиса
        foreach (string source in new[] { locator, bank })
        {
            for (int i = 1; i < source.Length; i++)
            {
                if (source[i - 1] is '_' or '-')
                {
                    char c = char.ToUpperInvariant(source[i]);
                    if (c is >= 'A' and <= 'D') return c.ToString();
                }
            }
        }

        return "";
    }

    /// <summary>BIOS часто пишет «Unknown» или код JEDEC вместо имени — такое лучше не показывать.</summary>
    private static string CleanVendor(string raw)
    {
        string v = raw.Trim();
        if (v.Length == 0) return "";
        if (v.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "";
        if (v.Equals("Undefined", StringComparison.OrdinalIgnoreCase)) return "";
        if (v.All(c => c is '0' or 'x' or 'X' or ' ')) return "";
        return v;
    }

    /// <summary>
    /// Тип памяти по коду SMBIOS. WMI в поле <c>SMBIOSMemoryType</c> отдаёт те же
    /// коды, поэтому таблица общая — в отличие от форм-фактора.
    /// </summary>
    private static string MemoryTypeName(byte code) => code switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _    => "",
    };

    /// <summary>
    /// Форм-фактор по нумерации SMBIOS. Она сдвинута на единицу относительно
    /// нумерации WMI (там DIMM — это 8, а здесь 9), и общая таблица на обе тихо
    /// превращала бы DIMM в TSOP, а SODIMM — в RIMM.
    /// </summary>
    private static string FormFactorNameSmbios(byte code) => code switch
    {
        0x05 => "чип, распаян на плате",
        0x09 => "DIMM",
        0x0A => "TSOP",
        0x0B => "чипы, распаяны на плате",
        0x0C => "RIMM",
        0x0D => "SODIMM",
        0x0E => "SRIMM",
        0x0F => "FB-DIMM",
        0x10 => "кристалл, распаян на плате",
        _    => "",
    };

    /// <summary>Форм-фактор по нумерации WMI <c>Win32_PhysicalMemory.FormFactor</c>.</summary>
    private static string FormFactorNameWmi(byte code) => code switch
    {
        8  => "DIMM",
        9  => "TSOP",
        11 => "RIMM",
        12 => "SODIMM",
        13 => "SRIMM",
        14 => "FB-DIMM",
        _  => "",
    };

    /// <summary>
    /// Строки записи SMBIOS лежат после её структурной части подряд, каждая
    /// заканчивается нулём, весь блок — двумя нулями. Нумеруются с единицы.
    /// </summary>
    private static List<string> ReadStrings(byte[] b, int start, int end, out int next)
    {
        var result = new List<string>();
        int i = start;

        // Пустой блок строк — это сразу два нуля
        if (i + 1 < end && b[i] == 0 && b[i + 1] == 0)
        {
            next = i + 2;
            return result;
        }

        var current = new List<byte>();
        while (i < end)
        {
            byte c = b[i++];
            if (c != 0) { current.Add(c); continue; }

            result.Add(System.Text.Encoding.ASCII.GetString(current.ToArray()));
            current.Clear();

            if (i < end && b[i] == 0) { i++; break; }
        }

        next = i;
        return result;
    }

    private static string Str(List<string> strings, byte index) =>
        index >= 1 && index <= strings.Count ? strings[index - 1].Trim() : "";

    // ── Запасной путь: WMI ────────────────────────────────────────────────

    /// <summary>
    /// Если таблицы SMBIOS недоступны (виртуальная машина, урезанный BIOS) — берём
    /// то же самое из WMI. Полей меньше: рангов и напряжений там нет.
    /// </summary>
    private static RamInfo CollectFromWmi(long visibleBytes)
    {
        var modules = new List<RamModule>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT BankLabel, DeviceLocator, Manufacturer, PartNumber, SerialNumber, Capacity, "
              + "Speed, ConfiguredClockSpeed, ConfiguredVoltage, SMBIOSMemoryType, FormFactor, "
              + "DataWidth, TotalWidth FROM Win32_PhysicalMemory");

            foreach (ManagementObject o in s.Get())
            {
                string bank    = o["BankLabel"]?.ToString() ?? "";
                string locator = o["DeviceLocator"]?.ToString() ?? "";
                int speed      = (int)Num(o, "Speed");
                int configured = (int)Num(o, "ConfiguredClockSpeed");

                modules.Add(new RamModule
                {
                    Slot           = locator,
                    Bank           = bank,
                    Channel        = GuessChannel(bank, locator),
                    Manufacturer   = CleanVendor(o["Manufacturer"]?.ToString() ?? ""),
                    PartNumber     = (o["PartNumber"]?.ToString() ?? "").Trim(),
                    SerialNumber   = (o["SerialNumber"]?.ToString() ?? "").Trim(),
                    CapacityBytes  = Num(o, "Capacity"),
                    SpeedMts       = speed,
                    ConfiguredMts  = configured > 0 ? configured : speed,
                    VoltageMv      = (int)Num(o, "ConfiguredVoltage"),
                    DataWidthBits  = (int)Num(o, "DataWidth"),
                    TotalWidthBits = (int)Num(o, "TotalWidth"),
                    TypeName       = MemoryTypeName((byte)Num(o, "SMBIOSMemoryType")),
                    FormFactor     = FormFactorNameWmi((byte)Num(o, "FormFactor")),
                });
            }
        }
        catch { }

        int slots = 0;
        long maxCapacity = 0;
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
            foreach (ManagementObject o in s.Get())
            {
                slots += (int)Num(o, "MemoryDevices");
                maxCapacity += Num(o, "MaxCapacityEx") * 1024;
            }
        }
        catch { }

        return new RamInfo
        {
            Modules          = modules,
            SlotsTotal       = slots > 0 ? slots : modules.Count,
            MaxCapacityBytes = maxCapacity,
            VisibleBytes     = visibleBytes,
        };
    }
}
