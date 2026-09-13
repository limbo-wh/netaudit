namespace NetAudit.Core.Diagnostics;

/// <summary>На что в первую очередь указывает сбой.</summary>
public enum CrashSuspect
{
    Unknown,
    Memory,
    Cpu,
    Disk,
    Gpu,
    Driver,
    Power,
    Software,
}

/// <summary>Расшифровка кода синего экрана.</summary>
public readonly record struct BugCheckInfo(uint Code, string Name, string Meaning, CrashSuspect Suspect)
{
    public string Hex => $"0x{Code:X8}";

    public string SuspectText => Suspect switch
    {
        CrashSuspect.Memory   => "оперативная память",
        CrashSuspect.Cpu      => "процессор или его питание",
        CrashSuspect.Disk     => "накопитель или кабель к нему",
        CrashSuspect.Gpu      => "видеокарта или её драйвер",
        CrashSuspect.Driver   => "драйвер",
        CrashSuspect.Power    => "питание или перегрев",
        CrashSuspect.Software => "программа или система",
        _                     => "точной привязки нет",
    };
}

/// <summary>
/// Коды синего экрана, которые реально встречаются на домашних машинах,
/// с пояснением на русском и первым подозреваемым.
///
/// Список намеренно не полный — их несколько сотен, и подавляющее большинство
/// относится к экзотике вроде отладчиков ядра. Незнакомый код отчёт показывает
/// как есть, с честной пометкой «расшифровки нет», а не выдумывает объяснение.
/// </summary>
public static class BugCheckCodes
{
    private static readonly Dictionary<uint, BugCheckInfo> Table = BuildTable();

    public static BugCheckInfo? Lookup(uint code) =>
        Table.TryGetValue(code, out var info) ? info : null;

    /// <summary>Разбирает и «0x00000124», и «124», и десятичное значение из EventData.</summary>
    public static uint? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        try
        {
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return System.Convert.ToUInt32(raw[2..], 16);

            // Kernel-Power 41 кладёт BugcheckCode десятичным числом
            if (uint.TryParse(raw, out uint dec)) return dec;

            return System.Convert.ToUInt32(raw, 16);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<uint, BugCheckInfo> BuildTable()
    {
        var d = new Dictionary<uint, BugCheckInfo>();

        void Add(uint code, string name, string meaning, CrashSuspect suspect) =>
            d[code] = new BugCheckInfo(code, name, meaning, suspect);

        // ── Память ────────────────────────────────────────────────────────
        Add(0x0000001A, "MEMORY_MANAGEMENT",
            "Диспетчер памяти нашёл неисправимую ошибку. Классика битой планки или слишком агрессивного профиля XMP/DOCP.",
            CrashSuspect.Memory);
        Add(0x00000050, "PAGE_FAULT_IN_NONPAGED_AREA",
            "Обращение к несуществующему адресу памяти.",
            CrashSuspect.Memory);
        Add(0x0000004E, "PFN_LIST_CORRUPT",
            "Повреждён список страниц памяти — почти всегда неисправная или нестабильная память.",
            CrashSuspect.Memory);
        Add(0x00000109, "CRITICAL_STRUCTURE_CORRUPTION",
            "Кто-то испортил структуры ядра. Обычно это разгон, память или драйвер-нарушитель.",
            CrashSuspect.Memory);
        Add(0x0000012B, "FAULTY_HARDWARE_CORRUPTED_PAGE",
            "Windows поймала одиночную ошибку бита в памяти. Прямое указание на неисправную планку.",
            CrashSuspect.Memory);
        Add(0x00000019, "BAD_POOL_HEADER",
            "Испорчен заголовок блока памяти ядра.",
            CrashSuspect.Memory);
        Add(0x000000BE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY",
            "Драйвер попытался писать в память только для чтения.",
            CrashSuspect.Driver);

        // ── Процессор и питание ───────────────────────────────────────────
        Add(0x00000124, "WHEA_UNCORRECTABLE_ERROR",
            "Железо само сообщило о неисправимой ошибке. Самый «аппаратный» из всех кодов: процессор, память, питание или шина PCIe.",
            CrashSuspect.Cpu);
        Add(0x0000009C, "MACHINE_CHECK_EXCEPTION",
            "Процессор доложил о внутренней ошибке. Разгон, недостаточное или нестабильное питание, перегрев.",
            CrashSuspect.Cpu);
        Add(0x00000101, "CLOCK_WATCHDOG_TIMEOUT",
            "Одно из ядер перестало отвечать. Часто — заниженное напряжение или нестабильный разгон.",
            CrashSuspect.Cpu);
        Add(0x0000007F, "UNEXPECTED_KERNEL_MODE_TRAP",
            "Процессор получил исключение, которое ядро не умеет обрабатывать. Обычно разгон или память.",
            CrashSuspect.Cpu);
        Add(0x00000133, "DPC_WATCHDOG_VIOLATION",
            "Драйвер слишком долго держал процессор. Частый виновник — старая прошивка SSD или драйвер контроллера.",
            CrashSuspect.Driver);

        // ── Диск и файловая система ───────────────────────────────────────
        Add(0x0000007A, "KERNEL_DATA_INPAGE_ERROR",
            "Не удалось прочитать страницу памяти с диска. Смотрите накопитель, его кабель и SMART.",
            CrashSuspect.Disk);
        Add(0x00000024, "NTFS_FILE_SYSTEM",
            "Ошибка в драйвере файловой системы NTFS — обычно повреждение данных на диске.",
            CrashSuspect.Disk);
        Add(0x000000F4, "CRITICAL_OBJECT_TERMINATION",
            "Критический системный процесс неожиданно завершился. Нередко из-за нечитаемого диска.",
            CrashSuspect.Disk);
        Add(0x000000EF, "CRITICAL_PROCESS_DIED",
            "Критический системный процесс умер. Причины те же: диск, память, повреждение системных файлов.",
            CrashSuspect.Disk);
        Add(0x00000154, "UNEXPECTED_STORE_EXCEPTION",
            "Сбой при работе со сжатой памятью. Встречается и при проблемах с диском, и с памятью.",
            CrashSuspect.Disk);
        Add(0x000000C4, "DRIVER_VERIFIER_DETECTED_VIOLATION",
            "Проверка драйверов (Driver Verifier) поймала нарушение. Это не поломка железа, а включённый режим проверки.",
            CrashSuspect.Driver);

        // ── Видео ─────────────────────────────────────────────────────────
        Add(0x00000116, "VIDEO_TDR_ERROR",
            "Видеодрайвер не смог восстановиться после зависания. Перегрев видеокарты, нестабильная память видеокарты или разгон.",
            CrashSuspect.Gpu);
        Add(0x00000117, "VIDEO_TDR_TIMEOUT_DETECTED",
            "Видеодрайвер перестал отвечать и не успел восстановиться вовремя.",
            CrashSuspect.Gpu);
        Add(0x00000119, "VIDEO_SCHEDULER_INTERNAL_ERROR",
            "Внутренняя ошибка планировщика видео. Обычно драйвер или неисправная видеопамять.",
            CrashSuspect.Gpu);
        Add(0x0000010E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL",
            "Ошибка управления видеопамятью.",
            CrashSuspect.Gpu);

        // ── Драйверы ──────────────────────────────────────────────────────
        Add(0x0000000A, "IRQL_NOT_LESS_OR_EQUAL",
            "Драйвер обратился к памяти на недопустимом уровне прерывания. Драйвер либо неисправная память.",
            CrashSuspect.Driver);
        Add(0x000000D1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
            "То же самое, но виновный драйвер Windows назвала явно — его имя есть в дампе.",
            CrashSuspect.Driver);
        Add(0x0000001E, "KMODE_EXCEPTION_NOT_HANDLED",
            "Необработанное исключение в режиме ядра.",
            CrashSuspect.Driver);
        Add(0x0000003B, "SYSTEM_SERVICE_EXCEPTION",
            "Исключение при переходе из программы в ядро. Часто виноват драйвер или античит.",
            CrashSuspect.Driver);
        Add(0x0000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
            "Системный поток словил исключение и не обработал его.",
            CrashSuspect.Driver);
        Add(0x00000139, "KERNEL_SECURITY_CHECK_FAILURE",
            "Проверка целостности структур ядра не прошла. Драйвер, память или повреждённые системные файлы.",
            CrashSuspect.Driver);
        Add(0x000000C2, "BAD_POOL_CALLER",
            "Драйвер неправильно работал с памятью ядра.",
            CrashSuspect.Driver);
        Add(0x000000C5, "DRIVER_CORRUPTED_EXPOOL",
            "Драйвер испортил системный пул памяти.",
            CrashSuspect.Driver);
        Add(0x00000018, "REFERENCE_BY_POINTER",
            "Счётчик ссылок на объект ядра ушёл в минус — ошибка драйвера.",
            CrashSuspect.Driver);
        Add(0x00000001, "APC_INDEX_MISMATCH",
            "Рассогласование внутренних счётчиков ядра.",
            CrashSuspect.Driver);
        Add(0x000000D6, "DRIVER_PAGE_FAULT_BEYOND_END_OF_ALLOCATION",
            "Драйвер вышел за границы выделенной памяти.",
            CrashSuspect.Driver);

        // ── Загрузка и система ────────────────────────────────────────────
        Add(0x0000007B, "INACCESSIBLE_BOOT_DEVICE",
            "Windows не нашла загрузочный диск. Смена режима SATA в BIOS, отвалившийся кабель или умерший накопитель.",
            CrashSuspect.Disk);
        Add(0x000000ED, "UNMOUNTABLE_BOOT_VOLUME",
            "Загрузочный том не монтируется — повреждение файловой системы или диска.",
            CrashSuspect.Disk);
        Add(0x0000005C, "HAL_INITIALIZATION_FAILED",
            "Не поднялся слой аппаратных абстракций. Обычно несовместимость или сбой оборудования при загрузке.",
            CrashSuspect.Unknown);
        Add(0x000000C000021A, "WINLOGON_FATAL_ERROR",
            "Критическая ошибка подсистемы входа в систему.",
            CrashSuspect.Software);

        return d;
    }
}
