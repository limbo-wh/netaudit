using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>Результаты замеров процессора.</summary>
public sealed class CpuBenchmarkResult
{
    /// <summary>Целочисленные операции одним ядром, млн/с.</summary>
    public double SingleThreadMops { get; init; }

    /// <summary>То же всеми потоками сразу.</summary>
    public double MultiThreadMops { get; init; }

    /// <summary>Вычисления с плавающей точкой одним ядром, млрд операций в секунду.</summary>
    public double SingleThreadGflops { get; init; }

    public double MultiThreadGflops { get; init; }

    /// <summary>Шифрование AES, ГБ/с — упирается в отдельный блок процессора.</summary>
    public double AesGbs { get; init; }

    /// <summary>Как растёт результат с числом потоков.</summary>
    public IReadOnlyList<(int Threads, double Mops)> Scaling { get; init; } = [];

    /// <summary>Частота под нагрузкой одного ядра и при загрузке всех ядер, МГц.</summary>
    public double SingleCoreMhz { get; init; }
    public double AllCoreMhz { get; init; }

    /// <summary>Падение производительности за время длительной нагрузки, %.</summary>
    public double ThrottlePercent { get; init; }

    public double TempStartC { get; init; } = double.NaN;
    public double TempPeakC { get; init; } = double.NaN;

    public bool HasFma { get; init; }
    public bool HasAes { get; init; }

    /// <summary>Почему температуру не удалось прочитать. Пусто, если удалось.</summary>
    public string TemperatureProblem { get; init; } = "";

    /// <summary>Прирост от всех потоков против одного, в разах.</summary>
    public double MultiThreadGain => SingleThreadMops > 0 ? MultiThreadMops / SingleThreadMops : 0;
}

/// <summary>
/// Замеры процессора: целые числа, плавающая точка, шифрование, масштабирование по
/// потокам, частота под нагрузкой и устойчивость к нагреву.
///
/// Абсолютные «попугаи» сравнивать не с чем, и цель не в них. Цель — четыре вещи,
/// которые видны только под нагрузкой и объясняют поведение машины:
///
///   1. **Разница между одним ядром и всеми.** Игры и старые программы упираются в
///      одно ядро, рендер и компиляция — во все. Слабый прирост при исправном
///      охлаждении означает, что частота сбрасывается сразу под многопоточной нагрузкой.
///   2. **Частота в бусте.** Процессор держит высокую частоту на одном ядре и
///      снижает на всех — это норма. Насколько снижает, паспорт не говорит.
///   3. **Троттлинг.** Если через полминуты полной нагрузки скорость падает, система
///      перегревается или упирается в лимит питания. В играх это выглядит как
///      просадки через несколько минут после начала матча.
///   4. **Специальные блоки.** Плавающая точка через FMA и шифрование через AES-NI
///      считаются отдельными блоками процессора; их скорость не выводится из
///      целочисленной и важна для нейросетей и архиваторов соответственно.
/// </summary>
public sealed class CpuBenchmark(int soakSeconds = 15) : IDiagnosticTest
{
    public string Title => "Замеры процессора";

    public CpuBenchmarkResult? Result { get; private set; }

    /// <summary>Длительность коротких фаз.</summary>
    private static readonly TimeSpan Phase = TimeSpan.FromSeconds(2.5);

    /// <summary>Сюда стекают результаты циклов, чтобы оптимизатор их не выбросил.</summary>
    private static long _sink;
    private static float _sinkF;

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Замеры процессора"));
        log.Report(TestLine.Dim("Во время замеров процессор загружен полностью — закройте игры "
                              + "и тяжёлые программы."));

        int threads = Environment.ProcessorCount;
        var temp = new TemperatureProbe();
        temp.Initialize();

        double tempStart = temp.CpuAvailable ? temp.Sample().CpuTempC : double.NaN;

        try
        {
            log.Report(TestLine.Dim("Прогрев…"));
            await Task.Run(() => IntegerBurn(TimeSpan.FromSeconds(1), ct), ct).ConfigureAwait(false);

            // ── Частота одного ядра ────────────────────────────────────────
            // Меряется первой и намеренно: наибольшую частоту процессор держит на
            // одном ядре, пока остальные простаивают и кристалл не прогрет. После
            // серии тяжёлых фаз тот же замер давал на 8% меньше — и «частота на одном
            // ядре» выходила равной частоте на всех, что неправда
            log.Report(TestLine.Dim("Частота по ядрам…"));
            double singleMhz = await Task.Run(() => FrequencyGhz(1, ct), ct).ConfigureAwait(false) * 1000;

            // ── Целые числа ────────────────────────────────────────────────
            log.Report(TestLine.Dim("Целые числа, одно ядро…"));
            double singleMops = await Task.Run(() => IntegerRun(1, ct), ct).ConfigureAwait(false);

            log.Report(TestLine.Dim("Целые числа, все ядра…"));
            double multiMops = await Task.Run(() => IntegerRun(threads, ct), ct).ConfigureAwait(false);

            // ── Частота всех ядер ──────────────────────────────────────────
            // А эта — сразу после многопоточной фазы, на прогретом процессоре: именно
            // такую частоту он держит в долгой работе, а не в первые секунды
            log.Report(TestLine.Dim("Частота всех ядер…"));
            double allMhz = await Task.Run(() => FrequencyGhz(0, ct), ct).ConfigureAwait(false) * 1000;

            // ── Плавающая точка ────────────────────────────────────────────
            double singleGflops = 0, multiGflops = 0;
            if (Fma.IsSupported)
            {
                log.Report(TestLine.Dim("Плавающая точка…"));
                singleGflops = await Task.Run(() => FloatRun(1, ct), ct).ConfigureAwait(false);
                multiGflops  = await Task.Run(() => FloatRun(threads, ct), ct).ConfigureAwait(false);
            }

            // ── Шифрование ─────────────────────────────────────────────────
            double aes = 0;
            if (Aes.IsSupported)
            {
                log.Report(TestLine.Dim("Шифрование…"));
                aes = await Task.Run(() => AesRun(threads, ct), ct).ConfigureAwait(false);
            }

            // ── Масштабирование ────────────────────────────────────────────
            log.Report(TestLine.Dim("Прирост по числу потоков…"));
            var scaling = await ScalingAsync(threads, ct).ConfigureAwait(false);

            // ── Троттлинг ──────────────────────────────────────────────────
            log.Report(TestLine.Dim($"Длительная нагрузка, {soakSeconds} с — проверка троттлинга…"));
            var (drop, peakTemp) = await SoakAsync(threads, temp, ct).ConfigureAwait(false);

            Result = new CpuBenchmarkResult
            {
                SingleThreadMops   = singleMops,
                MultiThreadMops    = multiMops,
                SingleThreadGflops = singleGflops,
                MultiThreadGflops  = multiGflops,
                AesGbs             = aes,
                Scaling            = scaling,
                SingleCoreMhz      = singleMhz,
                AllCoreMhz         = allMhz,
                ThrottlePercent    = drop,
                TempStartC         = tempStart,
                TempPeakC          = peakTemp,
                HasFma             = Fma.IsSupported,
                HasAes             = Aes.IsSupported,
                TemperatureProblem = temp.Unavailable,
            };

            Report(log, Result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Замеры не выполнились: {ex.Message}"));
        }
        finally
        {
            temp.Dispose();
        }

        log.Report(TestLine.Empty);
    }

    // ── Отчёт ─────────────────────────────────────────────────────────────

    private static void Report(IProgress<TestLine> log, CpuBenchmarkResult r)
    {
        log.Report(TestLine.Empty);
        log.Report(TestLine.Info(Fmt.Row("Целые числа, одно ядро", $"{r.SingleThreadMops,8:F0} млн оп/с")));
        log.Report(TestLine.Info(Fmt.Row("Целые числа, все ядра",  $"{r.MultiThreadMops,8:F0} млн оп/с")));

        int cores = Environment.ProcessorCount;
        double gainPercent = cores > 0 ? r.MultiThreadGain / cores * 100 : 0;

        // У гибридных процессоров Intel экономичные ядра слабее быстрых в полтора-два
        // раза, и низкая отдача «на поток» там заложена устройством, а не поломкой.
        // Прежний вердикт объявлял исправный i7-13700K сбрасывающим частоту
        var info = Probes.CpuInfoProbe.Collect();
        bool hybrid = info.IsHybrid;

        var gainLevel = hybrid
            ? TestLevel.Info
            : gainPercent >= 70 ? TestLevel.Good : gainPercent >= 45 ? TestLevel.Warn : TestLevel.Bad;

        log.Report(new TestLine(Fmt.Row("Прирост от всех потоков",
            $"×{r.MultiThreadGain:F1} из ×{cores}   ({gainPercent:F0}%)"), gainLevel));

        if (hybrid)
        {
            log.Report(TestLine.Dim($"   У процессора {info.PerformanceCores} быстрых и "
                                  + $"{info.EfficiencyCores} экономичных ядер — сравнивать прирост"));
            log.Report(TestLine.Dim("   с числом потоков здесь нельзя: экономичные ядра слабее быстрых,"));
            log.Report(TestLine.Dim("   и отдача ниже по устройству процессора, а не из-за перегрева."));
        }
        else
        {
            log.Report(TestLine.Dim(gainPercent switch
            {
                >= 70 => "   Хороший прирост: частота под многопоточной нагрузкой держится.",
                >= 45 => "   Типично для процессора с многопоточностью на ядре: второй поток "
                       + "на том же ядре даёт не полный прирост.",
                _     => "   Низкий прирост. Обычно это сброс частоты под нагрузкой всех ядер.",
            }));
        }

        if (r.HasFma)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Info(Fmt.Row("Плавающая точка, одно ядро", $"{r.SingleThreadGflops,8:F1} GFLOPS")));
            log.Report(TestLine.Info(Fmt.Row("Плавающая точка, все ядра",  $"{r.MultiThreadGflops,8:F1} GFLOPS")));
            log.Report(TestLine.Dim("   Считается векторными инструкциями FMA — теми самыми, на которых"));
            log.Report(TestLine.Dim("   работают нейросети и обработка видео."));
        }
        else
        {
            log.Report(TestLine.Warn("Инструкций FMA у процессора нет — замер плавающей точки пропущен."));
        }

        if (r.HasAes)
        {
            log.Report(TestLine.Info(Fmt.Row("Шифрование AES", $"{r.AesGbs,8:F1} ГБ/с")));
            log.Report(TestLine.Dim("   Отдельный блок процессора: от него зависит скорость шифрования "
                                  + "диска и защищённых соединений."));
        }

        // ── Частоты ───────────────────────────────────────────────────────
        if (r.SingleCoreMhz > 0 || r.AllCoreMhz > 0)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Info(Fmt.Row("Частота лучшего ядра",   $"{r.SingleCoreMhz,8:F0} МГц")));
            log.Report(TestLine.Info(Fmt.Row("Частота на всех ядрах",  $"{r.AllCoreMhz,8:F0} МГц")));

            if (r.SingleCoreMhz > 0 && r.AllCoreMhz > 0)
            {
                double dropPercent = (1 - r.AllCoreMhz / r.SingleCoreMhz) * 100;

                // Точность метода — около трёх процентов, и меньшую разницу выдавать
                // за вывод нельзя: на прогретом процессоре замеры регулярно менялись
                // местами, и «частота не падает» соседствовала с «падает на 2%»
                log.Report(TestLine.Dim(dropPercent switch
                {
                    >= 10 => $"   На всех ядрах частота ниже на {dropPercent:F0}% — так работает буст: "
                           + "чем больше ядер занято, тем скромнее прибавка.",
                    >= 3  => $"   На всех ядрах частота ниже на {dropPercent:F0}% — процессор почти "
                           + "полностью сохраняет скорость под полной нагрузкой.",
                    _     => "   Разница в пределах точности замера: под полной нагрузкой процессор "
                           + "держит ту же частоту.",
                }));

                log.Report(TestLine.Dim("   Частота посчитана по числу выполненных тактов с точностью около"));
                log.Report(TestLine.Dim("   трёх процентов, а не взята из счётчиков Windows: на процессорах"));
                log.Report(TestLine.Dim("   AMD они показывают политику питания, а не действительную частоту."));
            }
        }

        // ── Масштабирование ───────────────────────────────────────────────
        if (r.Scaling.Count > 0)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Dim("Прирост по числу потоков:"));

            double baseline = r.Scaling[0].Mops;
            foreach (var (n, mops) in r.Scaling)
            {
                double gain = baseline > 0 ? mops / baseline : 0;
                double perThread = n > 0 ? gain / n * 100 : 0;
                log.Report(TestLine.Info($"   {n,3} {Plural(n, "поток", "потока", "потоков"),-8} "
                                       + $"{mops,8:F0} млн оп/с   ×{gain,4:F1}   отдача {perThread,3:F0}%"));
            }

            log.Report(TestLine.Dim("   Отдача падает, когда потоки начинают делить одно физическое ядро"));
            log.Report(TestLine.Dim("   и когда процессор снижает частоту под общей нагрузкой."));
        }

        // ── Троттлинг ─────────────────────────────────────────────────────
        log.Report(TestLine.Empty);
        var throttleLevel = r.ThrottlePercent switch
        {
            < 5  => TestLevel.Good,
            < 15 => TestLevel.Warn,
            _    => TestLevel.Bad,
        };
        log.Report(new TestLine(Fmt.Row("Падение за время нагрузки", $"{r.ThrottlePercent,8:F1}%"), throttleLevel));

        log.Report(TestLine.Dim(r.ThrottlePercent switch
        {
            < 5  => "   Частота держится, охлаждение справляется.",
            < 15 => "   Небольшое снижение. Для ноутбука это норма, для настольного — "
                  + "повод посмотреть охлаждение.",
            _    => "   Заметный троттлинг: система перегревается или упирается в лимит питания.",
        }));

        if (!double.IsNaN(r.TempPeakC))
        {
            var tempLevel = r.TempPeakC switch
            {
                < ThermalLimits.CpuComfortableC => TestLevel.Good,
                < ThermalLimits.CpuWarnC        => TestLevel.Info,
                _                               => TestLevel.Warn,
            };
            string start = double.IsNaN(r.TempStartC) ? "" : $"   (в начале {r.TempStartC:F0} °C)";
            log.Report(new TestLine(Fmt.Row("Температура под нагрузкой", $"{r.TempPeakC,8:F0} °C{start}"), tempLevel));

            if (r.TempPeakC >= ThermalLimits.CpuWarnC)
            {
                log.Report(TestLine.Warn("   Горячо. Если при этом падает частота (строка «Падение за время"));
                log.Report(TestLine.Warn("   нагрузки» выше) — стоит посмотреть кулер и термопасту."));
                log.Report(TestLine.Dim("   " + ThermalLimits.ModernHardwareNote));
            }
            else if (r.TempPeakC >= ThermalLimits.CpuComfortableC)
            {
                log.Report(TestLine.Dim("   Тепло, но в пределах нормального рабочего режима."));
            }
        }
        else
        {
            // Причина бывает разная, и «нужны права администратора» — не единственная:
            // на машине с включённой защитой от уязвимых драйверов Windows не даёт
            // запуститься драйверу чтения датчиков даже администратору
            log.Report(TestLine.Dim($"Температура не читается: {r.TemperatureProblem}."));

            if (r.TemperatureProblem.Contains("уязвим", StringComparison.OrdinalIgnoreCase))
            {
                log.Report(TestLine.Dim("   Это защита Windows, а не поломка: библиотека чтения датчиков"));
                log.Report(TestLine.Dim("   пользуется драйвером WinRing0, который Microsoft внесла в список"));
                log.Report(TestLine.Dim("   уязвимых. Температуру видеокарты это не затрагивает — её отдаёт"));
                log.Report(TestLine.Dim("   сама карта."));
            }
        }
    }

    // ── Целочисленный замер ───────────────────────────────────────────────

    /// <summary>
    /// Считаем операции за отведённое время, а не время за фиксированное число
    /// операций: так одинаково меряются и короткие фазы, и длительная нагрузка,
    /// и результаты сравнимы между собой.
    /// </summary>
    private static long IntegerBurn(TimeSpan duration, CancellationToken ct)
    {
        const long Block = 2_000_000;
        var sw = Stopwatch.StartNew();
        long done = 0;
        ulong h = 1469598103934665603UL;

        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            for (long i = 0; i < Block; i++)
            {
                h ^= (ulong)i;
                h *= 1099511628211UL;
                h ^= h >> 29;
            }
            done += Block;
        }

        Interlocked.Exchange(ref _sink, (long)h);
        return done;
    }

    /// <summary>Целочисленная нагрузка на N потоках, млн операций в секунду.</summary>
    private static double IntegerRun(int threads, CancellationToken ct)
    {
        var tasks = new Task<long>[threads];
        for (int i = 0; i < threads; i++) tasks[i] = Task.Run(() => IntegerBurn(Phase, ct), ct);

        Task.WaitAll(tasks, ct);
        return tasks.Sum(t => t.Result) / Phase.TotalSeconds / 1e6;
    }

    /// <summary>
    /// Действительная частота под нагрузкой, ГГц. Ноль потоков означает «по одному на
    /// каждое физическое ядро» — это и есть режим полной загрузки процессора.
    ///
    /// Потоки привязываются к ядрам через один: у процессора с многопоточностью
    /// логические процессоры 0 и 1 живут на одном физическом ядре и делят его блоки
    /// исполнения. Посадить замер на оба — значит заставить цепочку умножений одного
    /// потока ждать другого, и частота выйдет вдвое ниже настоящей.
    /// </summary>
    private static double FrequencyGhz(int threads, CancellationToken ct)
    {
        var info = CpuInfoProbe.Collect();
        int logical = Math.Max(1, info.LogicalCores);
        int physical = Math.Max(1, info.PhysicalCores);

        // Шаг привязки: у процессора с многопоточностью логические процессоры 0 и 1
        // живут на одном физическом ядре и делят его блоки исполнения. Посадить замер
        // на оба — значит заставить цепочку умножений одного потока ждать другого,
        // и частота выйдет вдвое ниже настоящей
        int step = logical > physical ? logical / physical : 1;

        if (threads == 1) return BestCoreGhz(physical, step, logical, ct);

        // Если физические ядра не определились, их окажется одно — и «частота на всех
        // ядрах» замерялась бы на единственном потоке, совпадая с частотой одного ядра.
        // В таком случае честнее занять все логические
        int cores = physical > 1 ? physical : logical;

        // Все ядра сразу: по одному потоку на каждое, среднее по ним
        var window = TimeSpan.FromSeconds(2);
        var tasks = new Task<double>[cores];

        for (int i = 0; i < cores; i++)
        {
            int cpu = (i * step) % logical;
            tasks[i] = Task.Run(() =>
            {
                CpuAffinity.Pin(cpu);
                try { return CpuAffinity.MeasureGhz(window, ct); }
                finally { CpuAffinity.Unpin(); }
            }, ct);
        }

        Task.WaitAll(tasks, ct);
        return tasks.Average(t => t.Result);
    }

    /// <summary>
    /// Частота лучшего ядра. Обходим ядра по очереди и берём максимум, а не меряем
    /// нулевое: разброс между ядрами доходит до 13% (замерено на шестиядерном Zen+ —
    /// от 3,25 до 3,68 ГГц). Нулевое ядро систематически медленнее прочих, на нём
    /// обрабатываются прерывания; вдобавок у AMD часть ядер отмечена как
    /// предпочтительные и поднимается выше остальных. Однопоточная нагрузка попадает
    /// именно на такое ядро, поэтому максимум и есть честный ответ.
    /// </summary>
    private static double BestCoreGhz(int physical, int step, int logical, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(0.6);
        double best = 0;

        for (int core = 0; core < physical; core++)
        {
            ct.ThrowIfCancellationRequested();

            int cpu = (core * step) % logical;
            double ghz = 0;

            // Отдельный поток, а не задача из пула: привязка к процессору живёт на
            // потоке, а пул волен переиспользовать его под чужую работу
            var thread = new Thread(() =>
            {
                CpuAffinity.Pin(cpu);
                try { ghz = CpuAffinity.MeasureGhz(window, ct); }
                finally { CpuAffinity.Unpin(); }
            }) { IsBackground = true };

            thread.Start();
            thread.Join();

            if (ghz > best) best = ghz;
        }

        return best;
    }

    // ── Плавающая точка ───────────────────────────────────────────────────

    /// <summary>
    /// Умножение со сложением векторами по восемь чисел. Восемь независимых
    /// накопителей — чтобы конвейер не простаивал в ожидании предыдущего результата:
    /// у инструкции задержка около четырёх тактов, а выдавать их процессор может
    /// по две за такт.
    /// </summary>
    private static double FloatRun(int threads, CancellationToken ct)
    {
        var tasks = new Task<double>[threads];
        for (int i = 0; i < threads; i++) tasks[i] = Task.Run(() => FloatBurn(ct), ct);

        Task.WaitAll(tasks, ct);
        return tasks.Sum(t => t.Result);
    }

    private static double FloatBurn(CancellationToken ct)
    {
        var a = Vector256.Create(1.0000001f);
        var b = Vector256.Create(0.9999999f);

        var acc0 = Vector256.Create(1f); var acc1 = Vector256.Create(2f);
        var acc2 = Vector256.Create(3f); var acc3 = Vector256.Create(4f);
        var acc4 = Vector256.Create(5f); var acc5 = Vector256.Create(6f);
        var acc6 = Vector256.Create(7f); var acc7 = Vector256.Create(8f);

        const long Block = 1_000_000;
        long iterations = 0;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < Phase && !ct.IsCancellationRequested)
        {
            for (long i = 0; i < Block; i++)
            {
                acc0 = Fma.MultiplyAdd(a, b, acc0);
                acc1 = Fma.MultiplyAdd(a, b, acc1);
                acc2 = Fma.MultiplyAdd(a, b, acc2);
                acc3 = Fma.MultiplyAdd(a, b, acc3);
                acc4 = Fma.MultiplyAdd(a, b, acc4);
                acc5 = Fma.MultiplyAdd(a, b, acc5);
                acc6 = Fma.MultiplyAdd(a, b, acc6);
                acc7 = Fma.MultiplyAdd(a, b, acc7);
            }
            iterations += Block;
        }
        sw.Stop();

        var sum = acc0 + acc1 + acc2 + acc3 + acc4 + acc5 + acc6 + acc7;
        _sinkF = sum.GetElement(0);

        // Каждая инструкция — восемь чисел, умножение и сложение за раз: 16 операций
        double flops = iterations * 8.0 * 16.0;
        return flops / sw.Elapsed.TotalSeconds / 1e9;
    }

    // ── Шифрование ────────────────────────────────────────────────────────

    private static double AesRun(int threads, CancellationToken ct)
    {
        var tasks = new Task<double>[threads];
        for (int i = 0; i < threads; i++) tasks[i] = Task.Run(() => AesBurn(ct), ct);

        Task.WaitAll(tasks, ct);
        return tasks.Sum(t => t.Result);
    }

    /// <summary>
    /// Десять раундов AES-128 на блок — столько же, сколько в настоящем шифровании.
    /// Четыре независимых блока за виток: у инструкции задержка в несколько тактов,
    /// и одна цепочка заняла бы блок процессора на четверть.
    /// </summary>
    private static double AesBurn(CancellationToken ct)
    {
        var key = Vector128.Create((byte)0x2B, 0x7E, 0x15, 0x16, 0x28, 0xAE, 0xD2, 0xA6,
                                          0xAB, 0xF7, 0x15, 0x88, 0x09, 0xCF, 0x4F, 0x3C);

        var b0 = Vector128.Create((byte)1); var b1 = Vector128.Create((byte)2);
        var b2 = Vector128.Create((byte)3); var b3 = Vector128.Create((byte)4);

        const long Block = 200_000;
        long blocks = 0;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < Phase && !ct.IsCancellationRequested)
        {
            for (long i = 0; i < Block; i++)
            {
                for (int round = 0; round < 10; round++)
                {
                    b0 = Aes.Encrypt(b0, key);
                    b1 = Aes.Encrypt(b1, key);
                    b2 = Aes.Encrypt(b2, key);
                    b3 = Aes.Encrypt(b3, key);
                }
            }
            blocks += Block * 4;
        }
        sw.Stop();

        _sinkF = b0.AsSingle().GetElement(0) + b1.AsSingle().GetElement(0)
               + b2.AsSingle().GetElement(0) + b3.AsSingle().GetElement(0);

        return blocks * 16.0 / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024);
    }

    // ── Масштабирование ───────────────────────────────────────────────────

    private static async Task<List<(int, double)>> ScalingAsync(int maxThreads, CancellationToken ct)
    {
        var result = new List<(int, double)>();
        var window = TimeSpan.FromSeconds(1);

        for (int n = 1; n <= maxThreads; n *= 2)
        {
            ct.ThrowIfCancellationRequested();

            var tasks = new Task<long>[n];
            for (int i = 0; i < n; i++) tasks[i] = Task.Run(() => IntegerBurn(window, ct), ct);

            var done = await Task.WhenAll(tasks).ConfigureAwait(false);
            result.Add((n, done.Sum() / window.TotalSeconds / 1e6));
        }

        // Полное число потоков не всегда степень двойки: у шестиядерного процессора
        // с многопоточностью их двенадцать, и без этой строки таблица кончалась бы на восьми
        if (result.Count > 0 && result[^1].Item1 != maxThreads)
        {
            var tasks = new Task<long>[maxThreads];
            for (int i = 0; i < maxThreads; i++) tasks[i] = Task.Run(() => IntegerBurn(window, ct), ct);

            var done = await Task.WhenAll(tasks).ConfigureAwait(false);
            result.Add((maxThreads, done.Sum() / window.TotalSeconds / 1e6));
        }

        return result;
    }

    // ── Длительная нагрузка ───────────────────────────────────────────────

    /// <summary>
    /// Держит полную нагрузку и сравнивает скорость в начале и в конце. Попутно следит
    /// за температурой, если она доступна.
    /// </summary>
    private async Task<(double DropPercent, double PeakTemp)> SoakAsync(
        int threads, TemperatureProbe temp, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(3);
        long counter = 0;
        double peak = double.NaN;

        using var soakCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = new Task[threads];

        for (int i = 0; i < threads; i++)
            workers[i] = Task.Run(() =>
            {
                const long Block = 500_000;
                ulong h = 1469598103934665603UL;
                while (!soakCts.Token.IsCancellationRequested)
                {
                    for (long k = 0; k < Block; k++)
                    {
                        h ^= (ulong)k;
                        h *= 1099511628211UL;
                        h ^= h >> 29;
                    }
                    Interlocked.Add(ref counter, Block);
                }
                Interlocked.Exchange(ref _sink, (long)h);
            }, soakCts.Token);

        // Температуру снимаем всё время нагрузки: пик важнее значения в конкретный миг
        var watcher = Task.Run(async () =>
        {
            while (!soakCts.Token.IsCancellationRequested)
            {
                if (temp.CpuAvailable)
                {
                    double t = temp.Sample().CpuTempC;
                    if (!double.IsNaN(t) && (double.IsNaN(peak) || t > peak)) peak = t;
                }
                try { await Task.Delay(1000, soakCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, soakCts.Token);

        long a0 = Interlocked.Read(ref counter);
        var sw = Stopwatch.StartNew();
        await Task.Delay(window, ct).ConfigureAwait(false);
        long a1 = Interlocked.Read(ref counter);
        double first = (a1 - a0) / sw.Elapsed.TotalSeconds;

        var middle = TimeSpan.FromSeconds(Math.Max(1, soakSeconds - window.TotalSeconds * 2));
        await Task.Delay(middle, ct).ConfigureAwait(false);

        long b0 = Interlocked.Read(ref counter);
        sw.Restart();
        await Task.Delay(window, ct).ConfigureAwait(false);
        long b1 = Interlocked.Read(ref counter);
        double last = (b1 - b0) / sw.Elapsed.TotalSeconds;

        await soakCts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
        try { await watcher.ConfigureAwait(false); } catch { }

        double drop = first > 0 ? (1 - last / first) * 100 : 0;
        return (Math.Max(0, drop), peak);
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int last2 = Math.Abs(n) % 100;
        int last = Math.Abs(n) % 10;
        if (last2 is >= 11 and <= 14) return many;
        return last switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
