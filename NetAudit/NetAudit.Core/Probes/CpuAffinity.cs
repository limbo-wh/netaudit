using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetAudit.Core.Probes;

/// <summary>
/// Привязка потока к конкретному логическому процессору и измерение реальной частоты.
///
/// Зачем нужна своя мерка частоты: счётчик Windows «% Processor Performance» на
/// процессорах AMD показывает состояние политики питания, а не факт. Проверено на
/// шестиядерном Zen+ — под нагрузкой одного потока он отдавал одинаковые 110% для всех
/// двенадцати логических процессоров, включая простаивающие, и «частота на одном
/// ядре» выходила равной «частоте на всех».
///
/// Поэтому частота измеряется напрямую: цепочка зависимых умножений. У 64-битного
/// умножения задержка ровно три такта на всех x86 последних поколений, и следующее
/// не может начаться, пока не готово предыдущее — сколько таких операций уложилось
/// в секунду, столько и тактов было. Сверено с независимым замером на векторных
/// инструкциях FMA: 3,64 против 3,72 ГГц, расхождение в пределах двух процентов.
/// </summary>
public static class CpuAffinity
{
    [DllImport("kernel32.dll")]
    private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    /// <summary>Задержка 64-битного умножения в тактах — основа расчёта частоты.</summary>
    private const double MultiplyLatencyCycles = 3.0;

    /// <summary>Маска, которая была у потока до привязки. Ноль — привязки не было.</summary>
    [ThreadStatic]
    private static nuint _previousMask;

    /// <summary>
    /// Привязывает текущий поток к одному логическому процессору. Без привязки
    /// планировщик Windows переносит поток между ядрами, и замер мерит его решения.
    ///
    /// Возвращает <c>false</c>, если привязать не удалось — тогда замер по этому
    /// логическому процессору проводить нельзя. Номера от 64 и выше отвергаются
    /// сразу: маска в Windows описывает только текущую группу процессоров, а сдвиг
    /// <c>1UL &lt;&lt; 64</c> в C# берётся по модулю 64 и молча привязал бы поток
    /// к нулевому ядру, выдав чужой результат за верный.
    /// </summary>
    public static bool Pin(int cpu)
    {
        if (cpu < 0 || cpu >= 64) return false;

        try
        {
            Thread.BeginThreadAffinity();
            nuint previous = SetThreadAffinityMask(GetCurrentThread(), (nuint)(1UL << cpu));

            if (previous == 0)
            {
                // Ядра нет в текущей группе процессоров либо вызов отвергнут
                Thread.EndThreadAffinity();
                return false;
            }

            _previousMask = previous;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Возвращает потоку прежнюю маску — ту, которую отдала <c>SetThreadAffinityMask</c>
    /// при привязке. Раньше здесь строилась маска <c>(1 &lt;&lt; ProcessorCount) - 1</c>,
    /// и ровно при 64 логических процессорах она вырождалась в ноль: вызов с нулевой
    /// маской не делает ничего, и поток оставался привязанным к одному ядру навсегда.
    ///
    /// Если привязки не было (в том числе когда <see cref="Pin"/> вернула
    /// <c>false</c>), делать нечего: <c>EndThreadAffinity</c> без парного
    /// <c>BeginThreadAffinity</c> вызывать нельзя.
    /// </summary>
    public static void Unpin()
    {
        nuint restore = _previousMask;
        if (restore == 0) return;

        try
        {
            SetThreadAffinityMask(GetCurrentThread(), restore);
        }
        catch { }

        _previousMask = 0;
        try { Thread.EndThreadAffinity(); } catch { }
    }

    /// <summary>
    /// Частота этого ядра, ГГц. Вызывается из потока, который уже привязан
    /// и уже нагружен: измерение само является нагрузкой.
    /// </summary>
    public static double MeasureGhz(TimeSpan duration, CancellationToken ct)
    {
        long a = 1;
        long iterations = 0;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            // Константа нечётная: произведение по модулю 2^64 никогда не обнулится.
            // Разворачивать цикл нельзя: восемь умножений на одну и ту же константу
            // подряд компилятор сворачивает в одно, и замер показывал 29 ГГц —
            // проверено. Одно умножение на виток цикла он свернуть не может
            for (int i = 0; i < 1_000_000; i++)
                a *= 6364136223846793005L;

            iterations += 1_000_000;
        }
        sw.Stop();

        Sink = a;
        return iterations * MultiplyLatencyCycles / sw.Elapsed.TotalSeconds / 1e9;
    }

    /// <summary>Результат обязан утечь наружу, иначе цикл имеет право исчезнуть при оптимизации.</summary>
    public static long Sink;
}
