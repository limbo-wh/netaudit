using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>Что именно включать в диагностику видеокарты.</summary>
[Flags]
public enum GpuDiagnosticParts
{
    Passport  = 1,
    Benchmark = 2,
    Llm       = 4,
    Memory    = 8,
    Health    = 16,
    Game      = 32,
    All       = Passport | Benchmark | Llm | Memory | Health | Game,
}

/// <summary>
/// Полная диагностика видеокарты: паспорт, замеры, проверка памяти и выводы —
/// на что она способна в играх и в нейросетях.
///
/// Собрано под конкретную задачу: оценить видеокарту, купленную с рук. Поэтому
/// кроме скоростей проверяется здоровье — состояние шины, следы вмешательства
/// в прошивку и жалобы драйвера в журнале Windows.
/// </summary>
public sealed class GpuDiagnosticTest(GpuDiagnosticParts parts = GpuDiagnosticParts.All) : IDiagnosticTest
{
    public string Title => "Диагностика видеокарты";

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("ДИАГНОСТИКА ВИДЕОКАРТЫ"));
        log.Report(TestLine.Empty);

        GpuInfo? info = null;
        GpuBenchmarkResult? bench = null;
        GpuLlmResult? llm = null;
        GpuTensorResult? tensorResult = null;
        GpuGameResult? game = null;

        if (parts.HasFlag(GpuDiagnosticParts.Passport))
        {
            info = await GpuInfoProbe.CollectAsync(ct).ConfigureAwait(false);
            ReportPassport(log, info);
        }

        if (parts.HasFlag(GpuDiagnosticParts.Health))
        {
            ReportHealth(log, info, ct);
            ReportTemperatures(log, underLoad: false);
        }

        if (parts.HasFlag(GpuDiagnosticParts.Benchmark))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            var b = new GpuBenchmark();
            await b.RunAsync(log, ct).ConfigureAwait(false);
            bench = b.Result;
            log.Report(TestLine.Empty);
        }

        if (parts.HasFlag(GpuDiagnosticParts.Game))
        {
            log.Report(TestLine.Dim(new string('─', 72)));

            // Пока идёт игровая сцена, снимаем с карты частоту, мощность и причину,
            // по которой драйвер не поднимает частоту выше. Это единственный способ
            // заметить карту, зажатую режимом питания драйвера: TFLOPS и кадры
            // просто выйдут ниже нормы, и без этих цифр отчёт скажет «карта слабее,
            // чем должна быть», не объяснив почему
            using var live = new NvidiaLiveProbe();
            var watch = LoadWatch.Start(live, ct);

            var g = new GpuGameBenchmark(seconds: 12);
            await g.RunAsync(log, ct).ConfigureAwait(false);
            game = g.Result;

            var seen = await watch.StopAsync().ConfigureAwait(false);
            ReportLoadBehaviour(log, info, seen);

            log.Report(TestLine.Empty);
        }

        if (parts.HasFlag(GpuDiagnosticParts.Llm))
        {
            log.Report(TestLine.Dim(new string('─', 72)));

            // Сначала пробуем тензорные блоки через DirectML — это настоящая
            // производительность для нейросетей. Не поднялся DirectML (старая
            // Windows, нет Direct3D 12) — считаем вычисления обычным шейдером:
            // цифра будет скромнее, но получена честно, а не переписана из справочника
            var tensor = new GpuTensorBenchmark();
            await tensor.RunAsync(log, ct).ConfigureAwait(false);
            tensorResult = tensor.Result;

            if (tensorResult is not { Ok: true })
            {
                log.Report(TestLine.Empty);
                log.Report(TestLine.Dim("Пробую запасным способом, без тензорных блоков…"));
                var l = new GpuLlmBenchmark();
                await l.RunAsync(log, ct).ConfigureAwait(false);
                llm = l.Result;
            }

            log.Report(TestLine.Empty);
        }

        if (parts.HasFlag(GpuDiagnosticParts.Memory))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            await new GpuMemoryTest(passes: 1).RunAsync(log, ct).ConfigureAwait(false);
            log.Report(TestLine.Empty);
        }

        // Температуры сразу после нагрузки: в покое разрыв между ядром и горячей
        // точкой ещё мал и о состоянии термоинтерфейса не говорит ничего
        bool wasLoaded = parts.HasFlag(GpuDiagnosticParts.Benchmark)
                      || parts.HasFlag(GpuDiagnosticParts.Game)
                      || parts.HasFlag(GpuDiagnosticParts.Memory);

        if (wasLoaded && parts.HasFlag(GpuDiagnosticParts.Health))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            ReportTemperatures(log, underLoad: true);
        }

        // Выводы имеют смысл только при замерах: без них это две строки «данных нет»,
        // которые лишь засоряют отчёт о паспорте
        if (parts.HasFlag(GpuDiagnosticParts.Benchmark))
        {
            log.Report(TestLine.Dim(new string('═', 72)));
            ReportGamingVerdict(log, info, bench, game);
            ReportLlmVerdict(log, info, bench, llm, tensorResult);
        }
    }

    // ── Паспорт ───────────────────────────────────────────────────────────

    private static void ReportPassport(IProgress<TestLine> log, GpuInfo? info)
    {
        log.Report(TestLine.Head("Паспорт"));

        if (info is null)
        {
            log.Report(TestLine.Bad("Видеокарта не определилась"));
            log.Report(TestLine.Empty);
            return;
        }

        log.Report(TestLine.Info(Fmt.Row("Модель", info.Name)));

        if (info.Architecture.Length > 0)
            log.Report(TestLine.Info(Fmt.Row("Поколение", info.Architecture)));

        log.Report(TestLine.Info(Fmt.Row("Видеопамять", Fmt.Bytes(info.DedicatedVideoMemory))));

        if (info.MaxMemoryClockMhz > 0)
            log.Report(TestLine.Info(Fmt.Row("Частоты (предел)",
                $"ядро {info.MaxGraphicsClockMhz} МГц, память {info.MaxMemoryClockMhz} МГц")));

        if (info.DriverVersion.Length > 0)
        {
            string date = info.DriverDate is { } d ? $" от {d:dd.MM.yyyy}" : "";
            log.Report(TestLine.Info(Fmt.Row("Драйвер", info.DriverVersion + date)));
        }

        if (info.VbiosVersion.Length > 0)
            log.Report(TestLine.Info(Fmt.Row("Прошивка (VBIOS)", info.VbiosVersion)));

        log.Report(TestLine.Info(Fmt.Row("Уровень Direct3D", info.FeatureLevel)));
        log.Report(TestLine.Info(Fmt.Row("Половинная точность", info.SupportsFp16 ? "есть" : "нет")));

        if (info.IsNvidia && info.ComputeCapability.Length > 0)
            log.Report(TestLine.Info(Fmt.Row("Тензорные блоки",
                info.HasTensorCores ? "есть" : "нет")));

        log.Report(TestLine.Empty);
    }

    // ── Здоровье ──────────────────────────────────────────────────────────

    private static void ReportHealth(IProgress<TestLine> log, GpuInfo? info, CancellationToken ct)
    {
        log.Report(TestLine.Head("Здоровье"));

        if (info is not null && info.PcieWidthMax > 0)
        {
            // Судим только по числу линий. Поколение в простое ничего не значит:
            // NVIDIA штатно опускает линк до первого поколения, когда карта не занята,
            // и прежняя проверка выдавала предупреждение про райзер и майнинг
            // совершенно исправной карте на каждом запуске
            bool full = info.PcieWidthCurrent >= info.PcieWidthMax;

            log.Report(new TestLine(
                Fmt.Row("Шина PCI Express",
                        $"поколение {info.PcieGenCurrent} из {info.PcieGenMax}, " +
                        $"{info.PcieWidthCurrent} линий из {info.PcieWidthMax}"),
                full ? TestLevel.Good : TestLevel.Warn));

            if (full && info.PcieGenCurrent < info.PcieGenMax)
                log.Report(TestLine.Dim("   Поколение шины ниже предельного — это нормально в простое:"));

            if (full && info.PcieGenCurrent < info.PcieGenMax)
                log.Report(TestLine.Dim("   под нагрузкой карта поднимает его сама."));

            if (!full)
            {
                log.Report(TestLine.Warn("   Карта работает на урезанной шине. Причины: не тот слот на плате,"));
                log.Report(TestLine.Warn("   переходник или райзер (типично для карт после майнинга), либо"));
                log.Report(TestLine.Warn("   слот делится с накопителем M.2. В играх это стоит нескольких процентов,"));
                log.Report(TestLine.Warn("   а вот для нейросетей, которым не хватило видеопамяти, — очень много."));
            }
        }

        if (info is not null && !double.IsNaN(info.PowerLimitW) && info.PowerDefaultLimitW > 0)
        {
            bool stock = Math.Abs(info.PowerLimitW - info.PowerDefaultLimitW) < 1;
            log.Report(new TestLine(
                Fmt.Row("Предел мощности", stock
                    ? $"{info.PowerLimitW:F0} Вт (заводской)"
                    : $"{info.PowerLimitW:F0} Вт при заводских {info.PowerDefaultLimitW:F0} Вт"),
                stock ? TestLevel.Good : TestLevel.Warn));

            if (!stock)
                log.Report(TestLine.Warn("   Предел изменён — картой занимались: разгоняли или наоборот "
                                       + "ограничивали (так делают при майнинге)."));
        }

        // Жалобы драйвера в журнале Windows — след прошлых проблем
        try
        {
            var vendor = WindowsEventQuery.Query(
                "System", ["nvlddmkm", "amdkmdag", "amdwddmg"], [], 30, 200, ct,
                WindowsEventQuery.ErrorsOnly);
            var tdr = WindowsEventQuery.Query("System", ["Display"], [4101], 30, 50, ct);

            int total = vendor.Count + tdr.Count;
            log.Report(new TestLine(
                Fmt.Row("Ошибки драйвера за 30 сут.", total == 0 ? "нет" : $"{total}"),
                total == 0 ? TestLevel.Good : TestLevel.Bad));

            if (total > 0)
            {
                var last = vendor.Concat(tdr).Max(e => e.Time);
                log.Report(TestLine.Warn($"   Последняя: {last:dd.MM HH:mm}. Разбор — кнопка «Отчёт о сбоях ПК»."));
            }
        }
        catch { }

        log.Report(TestLine.Empty);
    }

    // ── Поведение под нагрузкой ────────────────────────────────────────────

    /// <summary>
    /// Ниже этой доли от предельной частоты карта считается зажатой — если при этом
    /// и мощность далека от лимита. Нормальный буст лежит в 80–92% от предела
    /// (предел — из vBIOS, буст его не достигает никогда); 75% оставляет запас
    /// на жаркие карты, а зажатая режимом питания сидит около 55%.
    /// </summary>
    private const double ClockRatioLow = 0.75;

    /// <summary>Ниже этой доли лимита мощность не объясняет низкую частоту.</summary>
    private const double PowerRatioLow = 0.85;

    /// <summary>Выборки с загрузкой ниже этой не считаются нагрузкой.</summary>
    private const double LoadedUtilization = 90;

    /// <summary>Сводка показаний карты за время нагрузки.</summary>
    private sealed record LoadSeen(
        int Samples,
        double MedianClockMhz,
        double MedianPowerWatts,
        double PowerLimitWatts,
        double MaxTemperatureC,
        long DominantReason);

    /// <summary>
    /// Копит показания <see cref="NvidiaLiveProbe"/>, пока идёт нагрузка. Берутся
    /// только выборки с настоящей загрузкой: первые секунды сцена ещё грузится,
    /// и их частота — частота простоя.
    /// </summary>
    private sealed class LoadWatch
    {
        private readonly NvidiaLiveProbe _live;
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<NvidiaLiveSample> _seen = [];
        private readonly bool _started;

        private LoadWatch(NvidiaLiveProbe live, CancellationToken ct)
        {
            _live = live;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _started = NvidiaLiveProbe.IsPresent && live.Start();
            _task = _started ? Task.Run(Loop) : Task.CompletedTask;
        }

        public static LoadWatch Start(NvidiaLiveProbe live, CancellationToken ct) => new(live, ct);

        private async Task Loop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var s = _live.Last;
                    if (s.HasData && s.Utilization >= LoadedUtilization)
                        lock (_seen) _seen.Add(s);

                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task<LoadSeen> StopAsync()
        {
            _cts.Cancel();
            try { await _task.ConfigureAwait(false); } catch { }

            List<NvidiaLiveSample> seen;
            lock (_seen) seen = [.. _seen];

            if (seen.Count == 0)
                return new LoadSeen(0, double.NaN, double.NaN, double.NaN, double.NaN, -1);

            // Самая частая маска — а не последняя: карта мигает между причинами,
            // и одна выборка на границе может назвать что угодно
            long dominant = seen.Where(s => s.HasReason)
                .GroupBy(s => s.ReasonMask)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .DefaultIfEmpty(-1)
                .First();

            return new LoadSeen(
                seen.Count,
                Median(seen.Select(s => s.ClockMhz)),
                Median(seen.Select(s => s.PowerWatts)),
                seen.Select(s => s.PowerLimitWatts).LastOrDefault(v => !double.IsNaN(v), double.NaN),
                seen.Select(s => s.TemperatureC).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max(),
                dominant);
        }

        private static double Median(IEnumerable<double> values)
        {
            var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
            if (v.Length == 0) return double.NaN;
            return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
        }
    }

    /// <summary>
    /// Как карта вела себя под нагрузкой: до какой частоты поднялась, сколько взяла
    /// мощности и что назвала причиной, по которой не поднялась выше.
    ///
    /// Откуда взялось. 14.09.2026 RTX 2080 SUPER под любой нагрузкой — своей,
    /// FurMark, в окне и на весь экран — держала 1200 МГц из 2130 при 180 Вт из 250,
    /// без троттлинга, называя причиной «Idle». Три часа ушло на блок питания,
    /// композитор Windows, лок частот и собственный шейдер. Причиной оказался режим
    /// «Оптимальное энергопотребление» в панели NVIDIA — умолчание драйвера, которое
    /// переживает переустановку Windows. После переключения: 1650 МГц, 243 Вт,
    /// причина «Pwr». Замеры TFLOPS и кадров при этом просто выходили на треть ниже
    /// нормы, ничего не объясняя. Этот блок — чтобы отчёт объяснял.
    /// </summary>
    private static void ReportLoadBehaviour(IProgress<TestLine> log, GpuInfo? info, LoadSeen seen)
    {
        if (!NvidiaLiveProbe.IsPresent) return;

        log.Report(TestLine.Empty);
        log.Report(TestLine.Head("Поведение под нагрузкой"));

        if (seen.Samples < 4)
        {
            log.Report(TestLine.Dim("   Показания карты за время сцены не получены — судить не по чему."));
            return;
        }

        int maxClock = info?.MaxGraphicsClockMhz ?? 0;
        double clockRatio = maxClock > 0 && !double.IsNaN(seen.MedianClockMhz)
            ? seen.MedianClockMhz / maxClock
            : double.NaN;
        double powerRatio = seen.PowerLimitWatts > 0 && !double.IsNaN(seen.MedianPowerWatts)
            ? seen.MedianPowerWatts / seen.PowerLimitWatts
            : double.NaN;

        if (!double.IsNaN(seen.MedianClockMhz))
            log.Report(TestLine.Info(Fmt.Row("Частота ядра",
                maxClock > 0
                    ? $"{seen.MedianClockMhz:F0} МГц из {maxClock} предельных ({clockRatio * 100:F0}%)"
                    : $"{seen.MedianClockMhz:F0} МГц")));

        if (!double.IsNaN(seen.MedianPowerWatts))
            log.Report(TestLine.Info(Fmt.Row("Мощность",
                seen.PowerLimitWatts > 0
                    ? $"{seen.MedianPowerWatts:F0} Вт из {seen.PowerLimitWatts:F0} ({powerRatio * 100:F0}% лимита)"
                    : $"{seen.MedianPowerWatts:F0} Вт")));

        bool idle    = (seen.DominantReason & NvidiaLiveSample.ReasonIdle) != 0;
        bool power   = (seen.DominantReason & NvidiaLiveSample.ReasonSoftwarePowerCap) != 0;
        bool thermal = (seen.DominantReason & (NvidiaLiveSample.ReasonSoftwareThermal | NvidiaLiveSample.ReasonHardwareThermal)) != 0;
        bool brake   = (seen.DominantReason & (NvidiaLiveSample.ReasonHardwarePowerBrake | NvidiaLiveSample.ReasonHardwareSlowdown)) != 0;

        if (seen.DominantReason >= 0)
            log.Report(TestLine.Info(Fmt.Row("Что держит частоту", ReasonText(seen.DominantReason))));

        // Низкая частота при далёкой от лимита мощности и без троттлинга — карта
        // не хочет, а не не может. С маской «Idle» под полной загрузкой это
        // режим питания драйвера почти наверняка
        bool clamped = !double.IsNaN(clockRatio) && clockRatio < ClockRatioLow
                    && !double.IsNaN(powerRatio) && powerRatio < PowerRatioLow
                    && !thermal && !brake;

        if (brake)
        {
            log.Report(TestLine.Bad(Fmt.Row("Вердикт", "аварийное ограничение по питанию")));
            log.Report(TestLine.Warn("   Карта сама сбрасывает частоту по сигналу от цепи питания. Проверить"));
            log.Report(TestLine.Warn("   блок питания, кабели к карте (два отдельных, без переходников) и её VRM."));
        }
        else if (thermal)
        {
            log.Report(TestLine.Warn(Fmt.Row("Вердикт", "ограничена температурой")));
            log.Report(TestLine.Warn("   Частота снижена из-за нагрева. Смотреть блок температур ниже:"));
            log.Report(TestLine.Warn("   если горячая точка далеко от ядра — термопаста, если память — прокладки."));
        }
        else if (clamped)
        {
            log.Report(TestLine.Warn(Fmt.Row("Вердикт", idle
                ? "зажата режимом питания драйвера"
                : "частота ниже ожидаемой без явной причины")));
            log.Report(TestLine.Warn("   Карта не упирается ни в мощность, ни в температуру, но и не разгоняется."));
            log.Report(TestLine.Warn("   Самая частая причина — «Оптимальное энергопотребление» в панели NVIDIA:"));
            log.Report(TestLine.Warn("   это умолчание драйвера, оно переживает переустановку Windows и на некоторых"));
            log.Report(TestLine.Warn("   картах режет треть производительности. Панель управления NVIDIA →"));
            log.Report(TestLine.Warn("   Управление параметрами 3D → Режим управления электропитанием →"));
            log.Report(TestLine.Warn("   «Предпочтителен режим максимальной производительности», затем повторить замер."));
        }
        else if (power)
        {
            log.Report(TestLine.Good(Fmt.Row("Вердикт", "упирается в лимит мощности — штатно")));
        }
        else
        {
            log.Report(TestLine.Good(Fmt.Row("Вердикт", "работает в полную силу")));
        }
    }

    /// <summary>Маска причин → слова. Несколько битов сразу — через запятую.</summary>
    private static string ReasonText(long mask)
    {
        if (mask == 0) return "ничего — карта на свободном ходу";

        var parts = new List<string>();
        if ((mask & NvidiaLiveSample.ReasonSoftwarePowerCap) != 0)   parts.Add("лимит мощности");
        if ((mask & NvidiaLiveSample.ReasonSoftwareThermal) != 0)    parts.Add("температура (программно)");
        if ((mask & NvidiaLiveSample.ReasonHardwareThermal) != 0)    parts.Add("температура (аппаратно)");
        if ((mask & NvidiaLiveSample.ReasonHardwarePowerBrake) != 0) parts.Add("аварийный сигнал питания");
        if ((mask & NvidiaLiveSample.ReasonHardwareSlowdown) != 0)   parts.Add("аппаратное замедление");
        if ((mask & NvidiaLiveSample.ReasonApplicationsClocks) != 0) parts.Add("заданные частоты приложения");
        if ((mask & NvidiaLiveSample.ReasonSyncBoost) != 0)          parts.Add("синхронный буст");
        if ((mask & NvidiaLiveSample.ReasonDisplayClockSetting) != 0) parts.Add("частоты под монитор");
        if ((mask & NvidiaLiveSample.ReasonReliabilityOrBoardLimit) != 0)
            parts.Add("потолок буста — предел напряжения по надёжности или платы, это норма");
        if ((mask & NvidiaLiveSample.ReasonIdle) != 0)               parts.Add("«простой» — при полной загрузке это режим питания драйвера");

        return parts.Count > 0 ? string.Join(", ", parts) : $"неизвестная причина 0x{mask:X}";
    }

    /// <summary>
    /// Температуры видеокарты по всем датчикам, а не только по ядру.
    ///
    /// Зачем отдельным блоком. Ядро — самый холодный датчик карты, и именно его
    /// показывают <c>nvidia-smi</c> и большинство мониторингов. Горячая точка
    /// кристалла и память идут на 10–30 °C выше, а аварийная защита следит за ядром
    /// и потому молчит. Классический симптом перегретой памяти — чёрный экран
    /// с вентиляторами на максимум под долгой игровой нагрузкой при совершенно
    /// спокойных цифрах в мониторинге.
    /// </summary>
    private static void ReportTemperatures(IProgress<TestLine> log, bool underLoad)
    {
        using var probe = new TemperatureProbe();
        probe.Initialize();

        var t = probe.SampleGpu();

        log.Report(TestLine.Head(underLoad ? "Температуры после нагрузки" : "Температуры в покое"));

        if (!t.Any)
        {
            log.Report(TestLine.Warn(Fmt.Row("Датчики видеокарты", "недоступны")));

            if (probe.Unavailable.Length > 0)
                log.Report(TestLine.Dim($"   {probe.Unavailable}."));

            log.Report(TestLine.Empty);
            return;
        }

        if (!double.IsNaN(t.CoreC))
            log.Report(new TestLine(
                Fmt.Row("Ядро", $"{t.CoreC:F0} °C"),
                Level(t.CoreC, ThermalLimits.GpuComfortableC, ThermalLimits.GpuWarnC)));

        if (!double.IsNaN(t.HotSpotC))
            log.Report(new TestLine(
                Fmt.Row("Горячая точка", $"{t.HotSpotC:F0} °C"),
                Level(t.HotSpotC, ThermalLimits.GpuComfortableC + 15, ThermalLimits.GpuWarnC + 10)));

        if (!double.IsNaN(t.MemoryC))
            log.Report(new TestLine(
                Fmt.Row("Память", $"{t.MemoryC:F0} °C"),
                Level(t.MemoryC, ThermalLimits.GpuMemoryWarnC - 10, ThermalLimits.GpuMemoryWarnC)));

        double delta = t.HotSpotDeltaC;

        if (!double.IsNaN(delta))
        {
            // В покое разрыв мал у любой карты, даже у запущенной, — судить по нему нельзя
            bool bad = underLoad && delta >= ThermalLimits.GpuHotSpotDeltaWarnC;

            log.Report(new TestLine(
                Fmt.Row("Разрыв с ядром", $"{delta:F0} °C"),
                bad ? TestLevel.Warn : TestLevel.Info));

            if (bad)
            {
                log.Report(TestLine.Warn("   Больше 25 °C под нагрузкой — термопаста под кристаллом высохла"));
                log.Report(TestLine.Warn("   или прижим неравномерный. Обычное дело для карт, которые"));
                log.Report(TestLine.Warn("   несколько лет не разбирали. Лечится заменой термоинтерфейса."));
            }
        }

        if (!double.IsNaN(t.MemoryC) && t.MemoryC >= ThermalLimits.GpuMemoryWarnC)
        {
            log.Report(TestLine.Warn("   Память подошла к пределу: у GDDR6 это 105 °C, после чего карта"));
            log.Report(TestLine.Warn("   сбрасывает частоты или виснет. Виснет она молча — аварийная защита"));
            log.Report(TestLine.Warn("   следит за ядром, а оно в этот момент холодное. Менять термопрокладки."));
        }

        if (!underLoad)
            log.Report(TestLine.Dim("   Это покой. Разрыв между ядром и горячей точкой имеет смысл"));

        if (!underLoad)
            log.Report(TestLine.Dim("   смотреть под нагрузкой — он будет ниже по отчёту."));

        log.Report(TestLine.Empty);
    }

    /// <summary>Уровень строки по двум порогам: до первого — хорошо, после второго — плохо.</summary>
    private static TestLevel Level(double value, double good, double warn) =>
        value >= warn ? TestLevel.Bad
      : value >= good ? TestLevel.Warn
      : TestLevel.Good;

    /// <summary>
    /// Русское склонение после числа: 21 раз, 22 раза, 25 раз. Без этого в отчёте
    /// попадаются «в 34 раз медленнее», и текст читается как машинный перевод.
    /// </summary>
    private static string Plural(double value, string one, string few, string many)
    {
        int n = (int)Math.Round(Math.Abs(value));
        int last2 = n % 100;
        int last = n % 10;

        if (last2 is >= 11 and <= 14) return many;
        return last switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    // ── Выводы: игры ──────────────────────────────────────────────────────

    private static void ReportGamingVerdict(
        IProgress<TestLine> log, GpuInfo? info, GpuBenchmarkResult? bench, GpuGameResult? game)
    {
        log.Report(TestLine.Head("Что это даёт в играх"));

        if (bench is null)
        {
            log.Report(TestLine.Dim("Замеры не выполнялись."));
            log.Report(TestLine.Empty);
            return;
        }

        double vramGb = (info?.DedicatedVideoMemory ?? 0) / 1024.0 / 1024 / 1024;

        log.Report(TestLine.Info(Fmt.Row("Вычислительная мощность", $"{bench.Fp32Tflops:F1} TFLOPS")));
        log.Report(TestLine.Info(Fmt.Row("Скорость видеопамяти", $"{bench.PeakMemoryGbs:F0} ГБ/с")));

        if (game is { Ok: true })
        {
            log.Report(TestLine.Info(Fmt.Row("Кадров на тестовой сцене",
                $"{game.AverageFps:F0}   (худший процент {game.OnePercentLowFps:F0})")));
            log.Report(TestLine.Info(Fmt.Row("Ровность кадров", $"{game.Smoothness * 100:F0}%")));
        }

        log.Report(TestLine.Empty);

        // Грубые ориентиры по разрешению. Намеренно грубые: точную цифру кадров
        // даёт только сама игра, а эти границы отделяют классы карт друг от друга
        string resolution = bench.Fp32Tflops switch
        {
            >= 80 => "4K и выше с запасом, включая трассировку лучей на максимуме",
            >= 40 => "4K с запасом, включая трассировку лучей",
            >= 25 => "4K в большинстве игр, 1440p с запасом",
            >= 15 => "1440p на высоких настройках, 4K в нетребовательных играх",
            >= 8  => "1440p на высоких, 1080p с большим запасом",
            >= 4  => "1080p на высоких настройках",
            >= 2  => "1080p на средних и низких настройках",
            // Прежняя нижняя ветка обещала 1080p и встроенной графике с 0,3 TFLOPS,
            // которая не тянет его вовсе
            _     => "для игр не предназначена: это встроенная графика, хватит "
                   + "лишь на старые и очень простые игры",
        };

        log.Report(TestLine.Good(Fmt.Row("Комфортное разрешение", resolution)));

        if (vramGb > 0)
        {
            // Классифицируем по округлённому объёму: DXGI отдаёт чуть меньше
            // номинала (7,80 ГБ у восьмигигабайтной карты — часть памяти забрана
            // под служебные нужды), и восьмигигабайтная карта попадала в класс
            // шестигигабайтных
            double nominal = Math.Round(vramGb);
            string vramVerdict = nominal switch
            {
                >= 16 => "с запасом на годы вперёд",
                >= 12 => "хватает везде на сегодня",
                >= 8  => "достаточно для 1440p; в отдельных новых играх на максимальных "
                       + "текстурах в 4K уже впритык",
                >= 6  => "хватает для 1080p, в 1440p местами не хватает",
                _     => "мало по нынешним меркам",
            };
            log.Report(TestLine.Info(Fmt.Row($"Видеопамяти {nominal:F0} ГБ", vramVerdict)));
        }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Проверить карту именно под игровой нагрузкой можно вкладкой «Стресс-тест»:"));
        log.Report(TestLine.Dim("она греет видеокарту так же, как тяжёлая игра, и следит за температурой,"));
        log.Report(TestLine.Dim("сбросом частот и ошибками вычислений."));
        log.Report(TestLine.Empty);
    }

    // ── Выводы: нейросети ─────────────────────────────────────────────────

    private static void ReportLlmVerdict(
        IProgress<TestLine> log, GpuInfo? info, GpuBenchmarkResult? bench,
        GpuLlmResult? llm, GpuTensorResult? tensor)
    {
        log.Report(TestLine.Head("Что это даёт для языковых моделей"));

        double vramGb = (info?.DedicatedVideoMemory ?? 0) / 1024.0 / 1024 / 1024;
        double sharedGb = (info?.SharedSystemMemory ?? 0) / 1024.0 / 1024 / 1024;
        double bandwidth = bench?.PeakMemoryGbs ?? 0;

        // У встроенной графики и у процессоров со встроенным видео своей видеопамяти
        // нет вовсе — они работают в системной. Прежний код на этом останавливался
        // и выдавал «видеопамяти 0 ГБ, мало по нынешним меркам», хотя такая система
        // модели считает, просто медленнее
        bool integrated = vramGb < 1 && sharedGb > 0;

        if (integrated)
        {
            log.Report(TestLine.Info(Fmt.Row("Своей видеопамяти", "нет — встроенная графика")));
            log.Report(TestLine.Dim($"   Она берёт системную: доступно до {sharedGb:F0} ГБ, но скорость"));
            log.Report(TestLine.Dim("   там в разы ниже, чем у видеопамяти отдельной карты. Модель"));
            log.Report(TestLine.Dim("   уместится, а вот быстрого ответа ждать не стоит."));
            log.Report(TestLine.Empty);
        }

        if (vramGb <= 0 || bandwidth <= 0)
        {
            log.Report(TestLine.Dim("Недостаточно данных: нужны паспорт и замеры."));
            return;
        }

        if (tensor is { Ok: true })
        {
            log.Report(TestLine.Info(Fmt.Row("Умножение матриц FP16", $"{tensor.Fp16Tflops:F1} TFLOPS")));
            log.Report(TestLine.Dim($"   на тензорных блоках, в {tensor.Speedup:F1} раза быстрее обычных вычислений"));
        }
        else if (llm is not null && llm.Fp16Tflops > 0)
        {
            log.Report(TestLine.Info(Fmt.Row("Вычисления FP16", $"{llm.Fp16Tflops:F1} TFLOPS")));
            log.Report(TestLine.Dim("   без тензорных блоков — реальные движки получат больше"));
        }

        // Часть видеопамяти всегда занята рабочим столом и самим движком. Доля, а не
        // постоянный гигабайт: на карте с 4 ГБ он съедал четверть, а на 24 ГБ был
        // незаметен — при том что рабочему столу нужно примерно одинаково
        double reserve = Math.Clamp(vramGb * 0.12, 0.6, 1.5);
        double usable = Math.Max(0, vramGb - reserve);
        log.Report(TestLine.Info(Fmt.Row("Доступно под модель", $"{usable:F1} ГБ из {vramGb:F0}")));
        log.Report(TestLine.Empty);

        log.Report(TestLine.Dim("Сколько параметров помещается целиком в видеопамять:"));
        log.Report(TestLine.Dim("   формат       размер на 1 млрд    влезает"));

        foreach (var (name, bytesPerB) in new[]
                 {
                     ("4 бита (Q4)",  0.6),
                     ("5 бит  (Q5)",  0.75),
                     ("8 бит  (Q8)",  1.1),
                     ("16 бит (FP16)", 2.1),
                 })
        {
            double billions = usable / bytesPerB;
            log.Report(TestLine.Info($"   {name,-12} {bytesPerB,6:F1} ГБ         до {billions,4:F0} млрд параметров"));
        }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Скорость выдачи слов упирается в скорость видеопамяти: чтобы выдать одно"));
        log.Report(TestLine.Dim("слово, модель читает все свои веса целиком. Отсюда оценка сверху:"));
        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("   модель (4 бита)     вес      предел скорости"));

        foreach (var billions in new[] { 7.0, 8.0, 14.0, 32.0 })
        {
            double sizeGb = billions * 0.6;
            bool fits = sizeGb <= usable;
            double tokensPerSecond = bandwidth / sizeGb;

            string verdict = fits
                ? $"{tokensPerSecond,4:F0} слов/с"
                : "не влезает целиком";

            log.Report(new TestLine(
                $"   {billions,4:F0} млрд          {sizeGb,4:F1} ГБ    {verdict}",
                fits ? TestLevel.Good : TestLevel.Warn));
        }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Это предел сверху: на практике выходит примерно вдвое меньше — часть"));
        log.Report(TestLine.Dim("времени уходит на вычисления, а не только на чтение весов."));

        if (tensor is { Ok: true })
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Dim("Обработка запроса (то, что происходит до первого слова ответа) упирается"));
            log.Report(TestLine.Dim("не в память, а в вычисления — и здесь работают тензорные блоки."));

            // Грубая, но полезная оценка: длинный запрос требует примерно
            // 2 × параметры × токены операций
            double tokensPerSecond7B = tensor.Fp16Tflops * 1e12 / (2.0 * 7e9) * 0.3;
            log.Report(TestLine.Good(
                $"   Для модели на 7 млрд параметров это порядка {tokensPerSecond7B:F0} слов запроса в секунду"));
            log.Report(TestLine.Dim("   (с поправкой на то, что до пиковой скорости реальные движки не дотягивают)"));
        }
        else if (info is not null && info.HasTensorCores)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Good("У карты есть тензорные блоки — движки вроде llama.cpp и ONNX Runtime"));
            log.Report(TestLine.Good("их используют, и обработка длинного запроса идёт заметно быстрее,"));
            log.Report(TestLine.Good("чем показывает замер выше."));
        }

        if (bench is not null && bench.UploadGbs > 0)
        {
            log.Report(TestLine.Empty);
            double ratio = bandwidth / bench.UploadGbs;
            log.Report(TestLine.Warn($"Если модель не влезла в видеопамять, недостающее читается по шине PCI"));
            log.Report(TestLine.Warn($"Express — она в {ratio:F0} {Plural(ratio, "раз", "раза", "раз")} медленнее " +
                                     $"видеопамяти ({bench.UploadGbs:F0} против {bandwidth:F0} ГБ/с)."));
            log.Report(TestLine.Dim("Поэтому модель, не поместившаяся целиком, работает не немного, а в разы медленнее."));
        }
    }
}
