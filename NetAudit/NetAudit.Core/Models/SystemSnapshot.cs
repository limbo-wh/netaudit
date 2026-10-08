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
    // который показывает только ядро (скриншот под FurMark: у нас 94, у
    // FurMark 79 — и это одна и та же карта в одну и ту же секунду)
    double GpuCoreTempC = double.NaN,
    double GpuHotSpotC  = double.NaN,
    // Мощность карты в ваттах и частота ядра в мегагерцах. Нужны в журнале
    // состояния: при разборе сбоев под нагрузкой решающей оказалась мощность
    // карты, а в файле её не было — пришлось сопоставлять три лога по времени.
    // Загрузка в процентах мощность не заменяет: игра на 60% загрузки берёт
    // столько же ватт, сколько тест на 95%
    double GpuWatts = double.NaN,
    double GpuClockMhz = double.NaN,
    // Предел мощности карты. Сами по себе ватты ни о чём не говорят: 120 Вт —
    // это полтора часа спокойной игры для одной карты и потолок для другой.
    // Смысл появляется только в паре «сколько берёт из скольки разрешено»
    double GpuPowerLimitW = double.NaN
);
