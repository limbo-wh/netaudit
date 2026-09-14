using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>Что именно включать в диагностику процессора.</summary>
[Flags]
public enum CpuDiagnosticParts
{
    /// <summary>Модель, поколение, ядра, кэши, наборы инструкций.</summary>
    Passport = 1,

    /// <summary>Загрузка, частота, схема питания, что мешает работать в полную силу.</summary>
    State = 2,

    /// <summary>Следы прошлых бед: синие экраны по процессору, аппаратные ошибки, ограничение частоты.</summary>
    Health = 4,

    /// <summary>Скорость: целые, плавающая точка, шифрование, масштабирование, троттлинг.</summary>
    Benchmark = 8,

    /// <summary>Задержки обмена между ядрами.</summary>
    CoreLatency = 16,

    All = Passport | State | Health | Benchmark | CoreLatency,
}

/// <summary>
/// Полная диагностика процессора: паспорт, состояние, следы сбоев, замеры и выводы.
///
/// Отчёт отвечает на четыре вопроса, которые обычно задают о процессоре, и ни на
/// один из них Windows не отвечает сама: что это за процессор и на что он способен;
/// работает ли он сейчас в полную силу; не сбоил ли он в прошлом; и что из его
/// возможностей достанется играм, а что — тяжёлым расчётам.
///
/// Отдельная задача — отделить свойства процессора от того, что ему мешает.
/// Экономичная схема питания, работа поверх гипервизора, включённая защита на
/// основе виртуализации и перегрев дают одинаковый на вид результат: «медленно».
/// Причины у них разные, и каждая проверяется здесь отдельной строкой.
/// </summary>
public sealed class CpuDiagnosticTest(
    CpuDiagnosticParts parts = CpuDiagnosticParts.Passport | CpuDiagnosticParts.State
                             | CpuDiagnosticParts.Health | CpuDiagnosticParts.Benchmark)
    : IDiagnosticTest
{
    public string Title => "Диагностика процессора";

    /// <summary>За сколько суток смотреть журналы Windows.</summary>
    private const int HealthDays = 60;

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("ДИАГНОСТИКА ПРОЦЕССОРА"));
        log.Report(TestLine.Empty);

        CpuInfo? info = null;
        CpuState? state = null;
        CpuBenchmarkResult? bench = null;
        CoreLatencyResult? latency = null;

        if (parts.HasFlag(CpuDiagnosticParts.Passport))
        {
            info = await CpuInfoProbe.CollectAsync().ConfigureAwait(false);
            ReportPassport(log, info);
        }

        if (parts.HasFlag(CpuDiagnosticParts.State))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            state = await CpuInfoProbe.SampleStateAsync().ConfigureAwait(false);
            ReportState(log, state, info);
        }

        if (parts.HasFlag(CpuDiagnosticParts.Health))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            ReportHealth(log, ct);
        }

        if (parts.HasFlag(CpuDiagnosticParts.Benchmark))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            var b = new CpuBenchmark();
            await b.RunAsync(log, ct).ConfigureAwait(false);
            bench = b.Result;
        }

        if (parts.HasFlag(CpuDiagnosticParts.CoreLatency))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            var l = new CoreLatencyTest();
            await l.RunAsync(log, ct).ConfigureAwait(false);
            latency = l.Result;
        }

        log.Report(TestLine.Dim(new string('═', 72)));
        ReportVerdict(log, info, state, bench, latency);
    }

    // ── Паспорт ───────────────────────────────────────────────────────────

    private static void ReportPassport(IProgress<TestLine> log, CpuInfo info)
    {
        log.Report(TestLine.Head("Паспорт"));

        if (info.Name.Length == 0)
        {
            log.Report(TestLine.Bad("Процессор не определился."));
            log.Report(TestLine.Empty);
            return;
        }

        log.Report(TestLine.Info(Fmt.Row("Модель", info.Name)));

        if (info.Architecture.Length > 0)
            log.Report(TestLine.Info(Fmt.Row("Поколение", info.Architecture)));

        // Число физических ядер может остаться неизвестным (виртуальная машина,
        // урезанный доступ к раскладке процессоров) — тогда честнее сказать об этом,
        // чем напечатать «0 ядер»
        string cores = info.CoresKnown
            ? $"{info.PhysicalCores} {Plural(info.PhysicalCores, "ядро", "ядра", "ядер")}, "
            + $"{info.LogicalCores} {Plural(info.LogicalCores, "поток", "потока", "потоков")}"
            : $"{info.LogicalCores} {Plural(info.LogicalCores, "поток", "потока", "потоков")} "
            + "(сколько из них физических ядер — определить не удалось)";

        if (info.IsHybrid)
            cores += $"   (быстрых {info.PerformanceCores}, экономичных {info.EfficiencyCores})";
        else if (info.CoresKnown && !info.SmtEnabled && info.PhysicalCores > 1)
            cores += "   (многопоточность выключена или не поддерживается)";

        log.Report(TestLine.Info(Fmt.Row("Ядра", cores)));

        if (info.BaseMhz > 0)
            log.Report(TestLine.Info(Fmt.Row("Базовая частота", $"{info.BaseMhz} МГц")));

        if (info.Socket.Length > 0)
            log.Report(TestLine.Info(Fmt.Row("Сокет", info.Socket)));

        if (info.Family > 0)
            log.Report(TestLine.Dim(Fmt.Row("Семейство/модель/степпинг",
                $"{info.Family} / {info.Model} / {info.Stepping}")));

        if (info.Microcode.Length > 0)
            log.Report(TestLine.Dim(Fmt.Row("Микрокод", info.Microcode)));

        // ── Кэши ──────────────────────────────────────────────────────────
        if (info.Caches.Count > 0)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Dim("Кэш-память:"));

            foreach (var c in info.Caches)
            {
                string kind = c.Level == 1 ? $" ({c.Kind})" : "";
                string total = c.Instances > 1 ? $"   всего {Fmt.Bytes(c.TotalBytes)}" : "";
                log.Report(TestLine.Info($"   L{c.Level}{kind,-12} {c.Instances} × {Fmt.Bytes(c.SizeBytes),9}{total}"));
            }

            // Кэш последнего уровня, разбитый на группы, — это кластеры ядер: важнейшая
            // особенность процессоров AMD, объясняющая их поведение в играх
            if (info.LastLevelCacheGroups.Count > 1)
            {
                int perGroup = info.LogicalCores / info.LastLevelCacheGroups.Count;
                log.Report(TestLine.Warn($"   Кэш последнего уровня разбит на {info.LastLevelCacheGroups.Count} "
                                       + $"{Plural(info.LastLevelCacheGroups.Count, "блок", "блока", "блоков")} "
                                       + $"по {perGroup} {Plural(perGroup, "потоку", "потока", "потоков")}."));
                log.Report(TestLine.Dim("   Ядру доступен только свой блок: суммарный объём из характеристик"));
                log.Report(TestLine.Dim("   в одиночку получить нельзя. Обмен между блоками идёт через"));
                log.Report(TestLine.Dim("   внутреннюю шину и стоит заметно дороже — это видно в замере"));
                log.Report(TestLine.Dim("   задержек между ядрами."));
            }
        }

        if (info.InstructionSets.Count > 0)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Info(Fmt.Row("Наборы инструкций", string.Join(", ", info.InstructionSets))));
        }

        log.Report(TestLine.Empty);
    }

    // ── Состояние ─────────────────────────────────────────────────────────

    private static void ReportState(IProgress<TestLine> log, CpuState state, CpuInfo? info)
    {
        log.Report(TestLine.Head("Что с процессором сейчас"));

        // Нулевая загрузка при неотвечающих счётчиках — это не «процессор свободен»,
        // а «спросить не удалось»: показывать её как факт нельзя
        if (state.CountersAvailable)
            log.Report(TestLine.Info(Fmt.Row("Загрузка", $"{state.LoadPercent:F0}%")));
        else
            log.Report(TestLine.Warn(Fmt.Row("Загрузка",
                "счётчики производительности Windows не отвечают")));

        if (state.CurrentMhz > 0 && !double.IsNaN(state.CurrentMhz))
        {
            string relative = info is { BaseMhz: > 0 }
                ? $"   ({state.PerformancePercent:F0}% от базовых {info.BaseMhz})"
                : "";

            log.Report(TestLine.Info(Fmt.Row("Частота сейчас", $"{state.CurrentMhz:F0} МГц{relative}")));

            if (state.PerformancePercent is > 0 and < 70 && state.LoadPercent > 50)
            {
                log.Report(TestLine.Warn("   Под нагрузкой частота держится ниже базовой. Так выглядит перегрев,"));
                log.Report(TestLine.Warn("   нехватка питания или ограничение в схеме электропитания."));
            }
        }

        if (state.PowerScheme.Length > 0)
        {
            bool saving = state.PowerScheme.Contains("Экономия", StringComparison.OrdinalIgnoreCase);
            log.Report(new TestLine(Fmt.Row("Схема питания", state.PowerScheme),
                                    saving ? TestLevel.Warn : TestLevel.Info));

            if (saving)
                log.Report(TestLine.Warn("   В этой схеме процессор намеренно держит низкую частоту. "
                                       + "Для игр переключите на сбалансированную."));
        }

        log.Report(TestLine.Dim(Fmt.Row("Процессов / потоков",
            $"{state.ProcessCount} / {state.ThreadCount}")));

        // ── Что забирает производительность ───────────────────────────────
        if (info is not null)
        {
            log.Report(TestLine.Empty);

            if (info.HypervisorPresent)
            {
                log.Report(TestLine.Warn(Fmt.Row("Гипервизор", "работает")));
                log.Report(TestLine.Dim("   Windows запущена поверх слоя виртуализации (Hyper-V, WSL2,"));
                log.Report(TestLine.Dim("   песочница, эмулятор Android). Это стоит нескольких процентов"));
                log.Report(TestLine.Dim("   скорости и заметнее всего бьёт по задержкам в играх."));
            }
            else
            {
                log.Report(TestLine.Good(Fmt.Row("Гипервизор", "выключен")));
            }

            // Трёхзначное значение: сведения о защите на основе виртуализации лежат
            // в разделе WMI, открытом только администратору, и «не удалось узнать»
            // нельзя показывать как «выключена»
            if (info.VbsEnabled is true)
            {
                log.Report(TestLine.Warn(Fmt.Row("Защита на виртуализации", "включена")));
                log.Report(TestLine.Dim("   Изоляция ядра забирает 3–8% производительности. Выключать её"));
                log.Report(TestLine.Dim("   стоит только осознанно: это настоящая защита от вредоносных драйверов."));
            }
            else if (info.VbsEnabled is false)
            {
                log.Report(TestLine.Good(Fmt.Row("Защита на виртуализации", "выключена")));
            }
            else
            {
                log.Report(TestLine.Dim(Fmt.Row("Защита на виртуализации",
                    "неизвестно — нужны права администратора")));
            }
        }

        log.Report(TestLine.Empty);
    }

    // ── Здоровье ──────────────────────────────────────────────────────────

    private static void ReportHealth(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head($"Следы сбоев за {HealthDays} суток"));

        // Синие экраны, чей код указывает на процессор
        int crashes = 0;
        DateTime? last = null;
        try
        {
            var wer = WindowsEventQuery.Query(
                "System", ["Microsoft-Windows-WER-SystemErrorReporting"], [1001], HealthDays, 40, ct);

            foreach (var ev in wer)
            {
                uint? code = BugCheckCodes.Parse(ev.Get("param1")) ?? BugCheckCodes.Parse(ev.Get("BugcheckCode"));
                if (code is null) continue;

                var info = BugCheckCodes.Lookup(code.Value);
                if (info?.Suspect != CrashSuspect.Cpu) continue;

                crashes++;
                last ??= ev.Time;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        log.Report(new TestLine(
            Fmt.Row("Синие экраны по процессору", crashes == 0 ? "нет" : crashes.ToString()),
            crashes == 0 ? TestLevel.Good : TestLevel.Bad));

        if (last is { } t)
            log.Report(TestLine.Warn($"   Последний: {t:dd.MM.yyyy HH:mm}. Разбор — кнопка «Отчёт о сбоях ПК»."));

        // Аппаратные ошибки: для процессора это машинные проверки, которые он сам
        // сообщает системе. Одиночная исправленная — не беда, серия — беда
        try
        {
            var whea = WindowsEventQuery.Query(
                "System", ["Microsoft-Windows-WHEA-Logger"], [], HealthDays, 60, ct);

            log.Report(new TestLine(
                Fmt.Row("Аппаратные ошибки (WHEA)", whea.Count == 0 ? "нет" : whea.Count.ToString()),
                whea.Count == 0 ? TestLevel.Good : TestLevel.Warn));

            if (whea.Count > 0)
                log.Report(TestLine.Warn($"   Последняя: {whea.Max(e => e.Time):dd.MM.yyyy HH:mm}."));
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Событие 37 — прошивка ограничила частоту процессора. Это прямая улика
        // перегрева или нехватки питания, и её не видно никаким другим способом
        try
        {
            var limited = WindowsEventQuery.Query(
                "System", ["Microsoft-Windows-Kernel-Processor-Power"], [37], HealthDays, 100, ct);

            log.Report(new TestLine(
                Fmt.Row("Ограничение частоты прошивкой", limited.Count == 0 ? "нет" : $"{limited.Count} раз"),
                limited.Count == 0 ? TestLevel.Good : TestLevel.Warn));

            if (limited.Count > 0)
            {
                log.Report(TestLine.Warn($"   Последний: {limited.Max(e => e.Time):dd.MM.yyyy HH:mm}."));
                log.Report(TestLine.Dim("   Прошивка снижала частоту сама — обычно из-за перегрева или"));
                log.Report(TestLine.Dim("   недостаточного питания. На ноутбуках бывает при работе от батареи."));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        log.Report(TestLine.Empty);
    }

    // ── Выводы ────────────────────────────────────────────────────────────

    private static void ReportVerdict(
        IProgress<TestLine> log, CpuInfo? info, CpuState? state,
        CpuBenchmarkResult? bench, CoreLatencyResult? latency)
    {
        log.Report(TestLine.Head("Выводы"));

        if (info is null && bench is null)
        {
            log.Report(TestLine.Dim("Данных для выводов не собрано."));
            return;
        }

        // ── Игры ──────────────────────────────────────────────────────────
        if (bench is not null)
        {
            log.Report(TestLine.Head("Для игр"));
            log.Report(TestLine.Dim("Игры упираются в одно-два быстрых ядра: важнее скорость ядра, чем их число."));

            log.Report(TestLine.Info(Fmt.Row("Одно ядро", $"{bench.SingleThreadMops:F0} млн оп/с "
                                                        + $"на {bench.SingleCoreMhz:F0} МГц")));

            int cores = info is { PhysicalCores: > 0 } ? info.PhysicalCores : Environment.ProcessorCount;
            // Диапазоны, а не точные совпадения: у виртуальных машин и процессоров
            // с отключённым ядром бывает 5 или 7, и они проваливались в ветку
            // «мало ядер», хотя строка при этом красилась как хорошая
            var coreVerdict = cores switch
            {
                >= 8 => "ядер с запасом: любая нынешняя игра занимает меньше",
                >= 6 => "шести ядер хватает всем нынешним играм",
                >= 4 => "четыре ядра — нижняя граница: в тяжёлых сценах будут просадки",
                _    => "мало ядер для нынешних игр",
            };
            log.Report(new TestLine(Fmt.Row($"Ядер {cores}", coreVerdict),
                                    cores >= 6 ? TestLevel.Good : cores >= 4 ? TestLevel.Warn : TestLevel.Bad));

            if (bench.ThrottlePercent >= 15)
                log.Report(TestLine.Bad("Под длительной нагрузкой скорость падает — в долгом матче кадры "
                                      + "просядут сильнее, чем в первые минуты."));
        }

        // ── Многопоточные задачи ──────────────────────────────────────────
        if (bench is not null)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Head("Для тяжёлых расчётов"));
            log.Report(TestLine.Dim("Рендер, сборка кода, архивация и обработка видео используют все ядра."));

            log.Report(TestLine.Info(Fmt.Row("Все ядра", $"{bench.MultiThreadMops:F0} млн оп/с, "
                                                       + $"×{bench.MultiThreadGain:F1} к одному ядру")));

            if (bench.HasFma)
                log.Report(TestLine.Info(Fmt.Row("Плавающая точка", $"{bench.MultiThreadGflops:F0} GFLOPS")));

            if (bench.AllCoreMhz > 0 && bench.SingleCoreMhz > 0)
            {
                double keep = bench.AllCoreMhz / bench.SingleCoreMhz * 100;
                log.Report(TestLine.Dim($"   Под полной нагрузкой процессор держит {keep:F0}% от частоты "
                                      + "одного ядра."));
            }
        }

        // ── Языковые модели ───────────────────────────────────────────────
        if (bench is { HasFma: true, MultiThreadGflops: > 0 })
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Head("Для языковых моделей на процессоре"));
            log.Report(TestLine.Dim("Считать модель на процессоре имеет смысл, только когда она не влезла"));
            log.Report(TestLine.Dim("в видеопамять. Упирается это обычно не в вычисления, а в память —"));
            log.Report(TestLine.Dim("оценка скорости выдачи слов есть в диагностике оперативной памяти."));

            log.Report(TestLine.Info(Fmt.Row("Вычислительная мощность", $"{bench.MultiThreadGflops:F0} GFLOPS")));

            bool hasVnni = info?.InstructionSets.Contains("AVX-VNNI") == true;
            bool hasAvx512 = info?.InstructionSets.Contains("AVX-512") == true;

            if (hasAvx512 || hasVnni)
                log.Report(TestLine.Good("   Процессор умеет ускорять целочисленные вычисления нейросетей "
                                       + "(AVX-512/VNNI) — движки этим пользуются."));
            else
                log.Report(TestLine.Dim("   Специальных инструкций для нейросетей у процессора нет: "
                                      + "работать будет, но медленнее новых моделей."));
        }

        // ── Что мешает ────────────────────────────────────────────────────
        var obstacles = new List<string>();

        if (info?.HypervisorPresent == true)
            obstacles.Add("работает гипервизор — несколько процентов скорости и рост задержек");
        if (info?.VbsEnabled is true)
            obstacles.Add("включена защита на основе виртуализации — 3–8% производительности");
        if (state?.PowerScheme.Contains("Экономия", StringComparison.OrdinalIgnoreCase) == true)
            obstacles.Add("выбрана схема питания «Экономия энергии» — частота занижена намеренно");
        if (bench is { ThrottlePercent: >= 15 })
            obstacles.Add($"частота падает на {bench.ThrottlePercent:F0}% под длительной нагрузкой — охлаждение или питание");
        if (bench is { TempPeakC: >= 90 })
            obstacles.Add($"температура доходит до {bench.TempPeakC:F0} °C");

        log.Report(TestLine.Empty);
        if (obstacles.Count == 0)
        {
            log.Report(TestLine.Good("Ничего не мешает: процессор работает в полную силу."));
        }
        else
        {
            log.Report(TestLine.Warn("Что мешает процессору работать в полную силу:"));
            foreach (var o in obstacles)
                log.Report(TestLine.Warn($"   • {o}"));
        }

        // ── Особенность многокластерных процессоров ───────────────────────
        if (latency is { HasGroups: true })
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Dim($"Обмен между кластерами ядер стоит {latency.CrossGroupNs:F0} нс против "
                                  + $"{latency.SameGroupNs:F0} нс внутри кластера."));
            log.Report(TestLine.Dim("Программы, активно обменивающиеся данными между потоками, на таком"));
            log.Report(TestLine.Dim("процессоре иногда работают быстрее на половине ядер, чем на всех:"));
            log.Report(TestLine.Dim("привязка к ядрам одного кластера убирает дорогие переходы."));
        }
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int last2 = Math.Abs(n) % 100;
        int last = Math.Abs(n) % 10;
        if (last2 is >= 11 and <= 14) return many;
        return last switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
