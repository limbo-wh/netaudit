using System.Diagnostics;
using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>Задержки обмена между логическими процессорами.</summary>
public sealed class CoreLatencyResult
{
    /// <summary>Матрица задержек, нс. По диагонали — нули (сам с собой не меряется).</summary>
    public double[,] Matrix { get; init; } = new double[0, 0];

    public int Threads { get; init; }

    /// <summary>Обмен между двумя потоками одного физического ядра.</summary>
    public double SameCoreNs { get; init; } = double.NaN;

    /// <summary>Обмен между ядрами, делящими общий кэш последнего уровня.</summary>
    public double SameGroupNs { get; init; } = double.NaN;

    /// <summary>Обмен между разными кластерами ядер — через внутреннюю шину процессора.</summary>
    public double CrossGroupNs { get; init; } = double.NaN;

    public bool HasGroups => !double.IsNaN(CrossGroupNs);
}

/// <summary>
/// Сколько стоит передать значение от одного ядра другому.
///
/// Это не «попугай», а свойство устройства процессора, которое напрямую объясняет
/// поведение многопоточных программ. Два потока, обменивающиеся данными, платят эту
/// задержку на каждой передаче: у ядер, сидящих на общем кэше, обмен идёт через него
/// и стоит десятки наносекунд, а у ядер из разных кластеров — через внутреннюю шину
/// процессора, и цена вырастает в разы. Именно поэтому игра, разложенная по всем
/// ядрам Ryzen первых поколений, иногда идёт хуже, чем на половине из них.
///
/// Меряется пинг-понгом: поток A ставит флаг и крутится в ожидании ответа, поток B
/// видит флаг и отвечает. Оба потока жёстко привязаны к своим логическим процессорам —
/// без привязки планировщик Windows переставит их куда захочет, и замер будет мерить
/// его решения, а не процессор.
/// </summary>
public sealed class CoreLatencyTest : IDiagnosticTest
{
    public string Title => "Задержки между ядрами";

    public CoreLatencyResult? Result { get; private set; }

    /// <summary>Обменов на пару за один подход.</summary>
    private const int Iterations = 8_000;

    /// <summary>
    /// Подходов на пару, из которых берётся лучший. Шум здесь только односторонний:
    /// прерывание или чужой поток могут задержать обмен, но ускорить его не могут.
    /// Замерено на этой машине: у пары потоков нулевого ядра одиночный подход давал
    /// 105 нс против 25–28 нс у таких же пар на других ядрах — на нулевом ядре
    /// Windows обрабатывает прерывания.
    /// </summary>
    private const int Attempts = 3;

    /// <summary>Первые обмены идут дольше, пока строка кэша не поселится между ядрами.</summary>
    private const int Warmup = 200;

    /// <summary>Выше этого числа потоков матрица перестаёт помещаться на экран.</summary>
    private const int MaxMatrixThreads = 16;

    /// <summary>
    /// Общая ячейка для пинг-понга. Отдельный объект с полем — чтобы значение жило
    /// в своей строке кэша и обмен мерил дорогу между ядрами, а не соседство с
    /// другими данными теста.
    /// </summary>
    private sealed class PingPong
    {
        public long Turn;
    }

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Задержки между ядрами"));

        int threads = Environment.ProcessorCount;
        if (threads < 2)
        {
            log.Report(TestLine.Dim("Ядро одно — мерить нечего."));
            log.Report(TestLine.Empty);
            return;
        }

        log.Report(TestLine.Dim($"Пинг-понг между каждой парой из {threads} логических процессоров…"));

        try
        {
            var info = CpuInfoProbe.Collect();
            var result = await Task.Run(() => Measure(threads, info, ct), ct).ConfigureAwait(false);
            Result = result;
            Report(log, result, info);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Замер не выполнился: {ex.Message}"));
        }

        log.Report(TestLine.Empty);
    }

    // ── Отчёт ─────────────────────────────────────────────────────────────

    private static void Report(IProgress<TestLine> log, CoreLatencyResult r, CpuInfo info)
    {
        if (!double.IsNaN(r.SameCoreNs))
        {
            log.Report(TestLine.Info(Fmt.Row("Внутри одного ядра", $"{r.SameCoreNs,8:F0} нс")));
            log.Report(TestLine.Dim("   Два потока на одном ядре: обмен идёт через кэш первого уровня."));
        }

        if (!double.IsNaN(r.SameGroupNs))
            log.Report(TestLine.Info(Fmt.Row("Между соседними ядрами", $"{r.SameGroupNs,8:F0} нс")));

        if (r.HasGroups)
        {
            log.Report(new TestLine(Fmt.Row("Между кластерами ядер", $"{r.CrossGroupNs,8:F0} нс"),
                                    TestLevel.Warn));

            double ratio = r.SameGroupNs > 0 ? r.CrossGroupNs / r.SameGroupNs : 0;
            log.Report(TestLine.Dim($"   В {ratio:F1} раза дороже, чем внутри кластера. У этого процессора"));
            log.Report(TestLine.Dim($"   ядра разбиты на {info.LastLevelCacheGroups.Count} группы с собственным кэшем "
                                  + "последнего уровня,"));
            log.Report(TestLine.Dim("   и обмен между группами идёт через внутреннюю шину процессора."));
        }

        // Матрица показывается только когда её видно целиком: на 32 потоках это
        // тысяча чисел, из которых читатель не извлечёт ничего
        if (r.Threads <= MaxMatrixThreads)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Dim("Матрица задержек, нс (строка — кто отправил, столбец — кто ответил):"));

            var header = "      " + string.Join("", Enumerable.Range(0, r.Threads).Select(i => $"{i,6}"));
            log.Report(TestLine.Dim(header));

            for (int i = 0; i < r.Threads; i++)
            {
                var cells = new System.Text.StringBuilder($"  {i,3} ");
                for (int j = 0; j < r.Threads; j++)
                    cells.Append(i == j ? "     ·" : $"{r.Matrix[i, j],6:F0}");

                log.Report(TestLine.Info(cells.ToString()));
            }
        }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Чем ниже числа, тем дешевле многопоточным программам обмениваться данными."));
        log.Report(TestLine.Dim("Показан лучший результат из трёх подходов: прерывания и чужие потоки"));
        log.Report(TestLine.Dim("могут обмен только задержать, но не ускорить. Строки нулевого ядра всё"));
        log.Report(TestLine.Dim("равно бывают выше прочих — на нём Windows обрабатывает прерывания."));
    }

    // ── Замер ─────────────────────────────────────────────────────────────

    private static CoreLatencyResult Measure(int threads, CpuInfo info, CancellationToken ct)
    {
        var matrix = new double[threads, threads];

        for (int i = 0; i < threads; i++)
        {
            for (int j = i + 1; j < threads; j++)
            {
                ct.ThrowIfCancellationRequested();

                double best = double.MaxValue;
                for (int attempt = 0; attempt < Attempts; attempt++)
                {
                    double ns = PingPongPair(i, j, ct);
                    if (ns > 0 && ns < best) best = ns;
                }

                double result = best == double.MaxValue ? 0 : best;
                matrix[i, j] = result;
                matrix[j, i] = result;
            }
        }

        // Раскладываем пары по трём корзинам: тот же физический процессор, тот же
        // кластер, разные кластеры. Кто с кем сидит — знает паспорт процессора
        var sameCore = new List<double>();
        var sameGroup = new List<double>();
        var crossGroup = new List<double>();

        var groups = info.LastLevelCacheGroups;
        var coreOf = BuildLayout(info);

        for (int i = 0; i < threads; i++)
        {
            for (int j = i + 1; j < threads; j++)
            {
                double ns = matrix[i, j];
                if (ns <= 0) continue;

                bool sameThreadPair = coreOf.TryGetValue(i, out int ci)
                                   && coreOf.TryGetValue(j, out int cj) && ci == cj;

                if (sameThreadPair) sameCore.Add(ns);
                else if (groups.Count <= 1 || InSameGroup(groups, i, j)) sameGroup.Add(ns);
                else crossGroup.Add(ns);
            }
        }

        return new CoreLatencyResult
        {
            Matrix       = matrix,
            Threads      = threads,
            SameCoreNs   = Median(sameCore),
            SameGroupNs  = Median(sameGroup),
            CrossGroupNs = Median(crossGroup),
        };
    }

    /// <summary>
    /// Медиана, а не среднее: одна пара, которой не повезло с прерыванием, сдвигает
    /// среднее на десятки наносекунд и делает сводку бесполезной.
    /// </summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;

        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    private static bool InSameGroup(IReadOnlyList<ulong> groups, int a, int b)
    {
        foreach (ulong mask in groups)
            if ((mask & (1UL << a)) != 0 && (mask & (1UL << b)) != 0) return true;

        return false;
    }

    /// <summary>
    /// Логический процессор → номер физического ядра.
    ///
    /// Считается по простому правилу: потоков вдвое больше ядер — значит на ядре по
    /// два, и Windows нумерует их подряд (0 и 1 — первое ядро, 2 и 3 — второе). Это
    /// верно для всех нынешних процессоров с многопоточностью; на гибридных Intel
    /// экономичные ядра идут без пары, и правило даёт для них по одному потоку на
    /// ядро — что тоже верно.
    /// </summary>
    private static Dictionary<int, int> BuildLayout(CpuInfo info)
    {
        var map = new Dictionary<int, int>();

        int logical = Math.Max(1, info.LogicalCores);
        int physical = Math.Max(1, info.PhysicalCores);
        int perCore = Math.Max(1, logical / physical);

        for (int i = 0; i < logical; i++) map[i] = i / perCore;
        return map;
    }

    /// <summary>
    /// Обмен между двумя логическими процессорами. Возвращает задержку одной передачи:
    /// полный оборот делится пополам, потому что за оборот значение проходит туда и обратно.
    /// </summary>
    private static double PingPongPair(int cpuA, int cpuB, CancellationToken ct)
    {
        var cell = new PingPong();
        var ready = new ManualResetEventSlim(false);
        double result = 0;

        var responder = new Thread(() =>
        {
            CpuAffinity.Pin(cpuB);
            ready.Set();

            for (int i = 0; i < Iterations; i++)
            {
                // Ждём своей очереди и сразу отдаём ход обратно
                while (Interlocked.Read(ref cell.Turn) != 1)
                {
                    if (ct.IsCancellationRequested) return;
                    Thread.SpinWait(1);
                }
                Interlocked.Exchange(ref cell.Turn, 0);
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest };

        var initiator = new Thread(() =>
        {
            CpuAffinity.Pin(cpuA);
            ready.Wait(ct);

            // Разогрев: первые обмены идут дольше, пока строка кэша не поселится
            for (int i = 0; i < Warmup; i++)
            {
                Interlocked.Exchange(ref cell.Turn, 1);
                while (Interlocked.Read(ref cell.Turn) != 0) Thread.SpinWait(1);
            }

            var sw = Stopwatch.StartNew();
            for (int i = Warmup; i < Iterations; i++)
            {
                Interlocked.Exchange(ref cell.Turn, 1);
                while (Interlocked.Read(ref cell.Turn) != 0)
                {
                    if (ct.IsCancellationRequested) return;
                    Thread.SpinWait(1);
                }
            }
            sw.Stop();

            int done = Iterations - Warmup;
            result = sw.Elapsed.TotalMilliseconds * 1e6 / done / 2;
        }) { IsBackground = true, Priority = ThreadPriority.Highest };

        responder.Start();
        initiator.Start();

        initiator.Join(TimeSpan.FromSeconds(10));
        responder.Join(TimeSpan.FromSeconds(2));

        return result;
    }

}
