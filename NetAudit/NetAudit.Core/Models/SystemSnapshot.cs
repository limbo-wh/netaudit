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
    double Fps = double.NaN,
    // Температура CPU/GPU, °C. NaN — нет прав администратора или датчик не найден
    double CpuTempC = double.NaN,
    double GpuTempC = double.NaN,
    // Разбивка температуры видеокарты: ядро и горячая точка кристалла. GpuTempC
    // выше — самый горячий из датчиков, его сторожат пороги; эти два — чтобы
    // оверлей показывал «79/94» и не выглядел ошибкой рядом с чужим мониторингом,
    // который показывает только ядро (скриншот владельца 14.09: у нас 94, у
    // FurMark 79 — и это одна и та же карта в одну и ту же секунду)
    double GpuCoreTempC = double.NaN,
    double GpuHotSpotC  = double.NaN
);
