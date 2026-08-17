namespace NetAudit.Core.Models;

public record SystemSnapshot(
    double RxMBps,
    double TxMBps,
    float  CpuPercent,
    float  GpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    int    BatteryPercent,  // -1 = нет батареи / неизвестно
    bool   IsCharging,
    DateTimeOffset Timestamp,
    // Кадров в секунду. NaN — счётчик выключен, нет прав администратора
    // или на экране нет ничего рисующего
    double Fps = double.NaN
);
