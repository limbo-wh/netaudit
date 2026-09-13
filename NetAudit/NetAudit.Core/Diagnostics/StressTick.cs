namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Числовой срез хода стресс-теста, раз в секунду. Отдельно от текстовых
/// <see cref="TestLine"/>: строки читает человек, а эти числа идут на графики,
/// и разбирать их обратно из текста было бы нелепо.
/// </summary>
public readonly record struct StressTick(
    TimeSpan Elapsed,
    TimeSpan Remaining,
    double CpuPercent,
    double GpuPercent,
    double CpuTempC,
    double GpuTempC,
    double RamUsedGb,
    /// <summary>Скорость вычислений процессора, млн операций в секунду.</summary>
    double CpuMops,
    /// <summary>Скорость вычислений видеокарты, млрд операций в секунду.</summary>
    double GpuGops,
    long CpuErrors,
    long MemErrors,
    long DiskErrors,
    long GpuErrors,
    long MemBytesChecked)
{
    public long TotalErrors => CpuErrors + MemErrors + DiskErrors + GpuErrors;

    /// <summary>Доля выполненного, 0…1. Для полосы прогресса.</summary>
    public double Progress
    {
        get
        {
            double total = (Elapsed + Remaining).TotalSeconds;
            return total > 0 ? Math.Clamp(Elapsed.TotalSeconds / total, 0, 1) : 0;
        }
    }
}
