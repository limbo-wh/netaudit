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
            var g = new GpuGameBenchmark(seconds: 12);
            await g.RunAsync(log, ct).ConfigureAwait(false);
            game = g.Result;
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
            bool full = info.PcieWidthCurrent >= info.PcieWidthMax && info.PcieGenCurrent >= info.PcieGenMax;
            log.Report(new TestLine(
                Fmt.Row("Шина PCI Express",
                        $"поколение {info.PcieGenCurrent} из {info.PcieGenMax}, " +
                        $"{info.PcieWidthCurrent} линий из {info.PcieWidthMax}"),
                full ? TestLevel.Good : TestLevel.Warn));

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
            >= 40 => "4K с запасом, включая трассировку лучей",
            >= 25 => "4K в большинстве игр, 1440p с запасом",
            >= 15 => "1440p на высоких настройках, 4K в нетребовательных играх",
            >= 8  => "1440p на высоких, 1080p с большим запасом",
            >= 4  => "1080p на высоких настройках",
            _     => "1080p на средних и низких настройках",
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
        double bandwidth = bench?.PeakMemoryGbs ?? 0;

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

        // Часть видеопамяти всегда занята рабочим столом и самим движком
        double usable = Math.Max(0, vramGb - 1.0);
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
