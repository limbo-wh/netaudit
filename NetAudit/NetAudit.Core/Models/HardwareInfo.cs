namespace NetAudit.Core.Models;

public record HardwareInfo(
    // Процессор
    string CpuName,
    int    CpuPhysicalCores,
    int    CpuLogicalCores,
    int    CpuMaxMhz,

    // Оперативная память
    double RamTotalGb,
    string RamType,
    int    RamSpeedMhz,
    int    RamModules,

    // Видеокарта
    string GpuName,
    double GpuVramGb,

    // Материнская плата
    string BoardVendor,
    string BoardModel,

    // Операционная система
    string OsCaption,
    string OsBuild,
    string OsDisplayVersion,

    // Накопители
    IReadOnlyList<DriveEntry> Drives,

    // Сетевые адаптеры
    IReadOnlyList<AdapterEntry> Adapters
);

public record DriveEntry(
    string Name,
    string Label,
    string MediaType,
    string FileSystem,
    long   TotalBytes,
    long   FreeBytes
);

public record AdapterEntry(
    string Name,
    string Description,
    string MacAddress,
    IReadOnlyList<string> IpAddresses,
    string AdapterType,
    long   SpeedMbps,
    bool   IsConnected
);
