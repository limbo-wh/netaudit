using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>Что именно включать в диагностику оперативной памяти.</summary>
[Flags]
public enum RamDiagnosticParts
{
    /// <summary>Модули, слоты, частота, ранги — всё, что известно без нагрузки.</summary>
    Passport = 1,

    /// <summary>Что происходит с памятью прямо сейчас: занято, кэш, подкачка, кто съел.</summary>
    Usage = 2,

    /// <summary>Следы прошлых бед: синие экраны по памяти, аппаратные ошибки, средство проверки Windows.</summary>
    Health = 4,

    /// <summary>Скорость и задержка.</summary>
    Benchmark = 8,

    /// <summary>Проверка шаблонами.</summary>
    Integrity = 16,

    All = Passport | Usage | Health | Benchmark | Integrity,
}

/// <summary>
/// Полная диагностика оперативной памяти: паспорт, текущее состояние, следы
/// прошлых сбоев, замеры скорости, проверка на ошибки и выводы.
///
/// Память — единственный узел, о котором Windows показывает пользователю ровно
/// одну цифру: сколько её всего. Между тем именно от неё зависят три вещи, которые
/// владелец машины чувствует каждый день: подтормаживания из-за нехватки объёма,
/// кадры в играх (через частоту и задержку) и стабильность системы. Причём типовая
/// беда — не поломка, а недонастройка: комплект памяти, купленный как 3200 МГц,
/// без включённого в BIOS профиля работает на 2133–2400 и отдаёт заметно меньше,
/// чем мог бы, никак об этом не сообщая.
///
/// Отчёт устроен так, чтобы отделить одно от другого: сколько её, как она настроена,
/// исправна ли она и что даст вмешательство.
/// </summary>
public sealed class RamDiagnosticTest(
    RamDiagnosticParts parts = RamDiagnosticParts.Passport | RamDiagnosticParts.Usage
                             | RamDiagnosticParts.Health | RamDiagnosticParts.Benchmark)
    : IDiagnosticTest
{
    public string Title => "Диагностика оперативной памяти";

    /// <summary>За сколько суток смотреть журналы Windows.</summary>
    private const int HealthDays = 60;

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("ДИАГНОСТИКА ОПЕРАТИВНОЙ ПАМЯТИ"));
        log.Report(TestLine.Empty);

        RamInfo? info = null;
        RamUsage? usage = null;
        RamBenchmarkResult? bench = null;

        if (parts.HasFlag(RamDiagnosticParts.Passport))
        {
            info = await RamInfoProbe.CollectAsync().ConfigureAwait(false);
            ReportPassport(log, info);
            ReportTuning(log, info);
        }

        if (parts.HasFlag(RamDiagnosticParts.Usage))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            usage = await RamInfoProbe.SampleUsageAsync().ConfigureAwait(false);
            ReportUsage(log, usage);
        }

        if (parts.HasFlag(RamDiagnosticParts.Health))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            ReportHealth(log, ct);
        }

        if (parts.HasFlag(RamDiagnosticParts.Benchmark))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            var b = new RamBenchmark();
            await b.RunAsync(log, ct).ConfigureAwait(false);
            bench = b.Result;

            if (bench is not null && info is not null)
                ReportEfficiency(log, info, bench);
        }

        if (parts.HasFlag(RamDiagnosticParts.Integrity))
        {
            log.Report(TestLine.Dim(new string('─', 72)));
            await new RamIntegrityTest(passes: 1, sharePercent: 45).RunAsync(log, ct).ConfigureAwait(false);
        }

        log.Report(TestLine.Dim(new string('═', 72)));
        ReportVerdict(log, info, usage, bench);
    }

    // ── Паспорт ───────────────────────────────────────────────────────────

    private static void ReportPassport(IProgress<TestLine> log, RamInfo info)
    {
        log.Report(TestLine.Head("Паспорт"));

        if (info.Modules.Count == 0)
        {
            log.Report(TestLine.Bad("Модули памяти не определились."));
            log.Report(TestLine.Empty);
            return;
        }

        string type = info.TypeName.Length > 0 ? info.TypeName + " " : "";
        log.Report(TestLine.Info(Fmt.Row("Установлено",
            $"{Fmt.Bytes(info.InstalledBytes)} {type}— {info.SlotsUsed} "
          + $"{Plural(info.SlotsUsed, "модуль", "модуля", "модулей")}")));

        log.Report(TestLine.Info(Fmt.Row("Рабочая частота", $"{info.ConfiguredMts} МТ/с")));

        if (!info.ChannelsKnown)
        {
            // Каналы вычитываются из названия банка, а называет их BIOS как хочет.
            // Не разобрали — так и говорим: угадать «одноканальный» значило бы
            // вдвое занизить теоретический предел и переврать все выводы ниже
            log.Report(TestLine.Dim(Fmt.Row("Режим работы",
                "BIOS не назвал каналы — определить не удалось")));
        }
        else
        {
            var channelLevel = info.ChannelsUsed >= 2 ? TestLevel.Good : TestLevel.Warn;
            log.Report(new TestLine(Fmt.Row("Режим работы", info.ChannelsUsed switch
            {
                >= 4 => "четырёхканальный",
                3    => "трёхканальный",
                2    => "двухканальный",
                _    => "одноканальный",
            }), channelLevel));

            if (info.TheoreticalGbs > 0)
                log.Report(TestLine.Dim($"   Теоретический предел контроллера: {info.TheoreticalGbs:F1} ГБ/с"));
        }

        ReportSlots(log, info);

        if (info.ErrorCorrection.Length > 0)
            log.Report(TestLine.Info(Fmt.Row("Коррекция ошибок", info.ErrorCorrection)));

        if (info.HardwareReservedBytes > 64L * 1024 * 1024)
        {
            var level = info.HardwareReservedBytes > 1024L * 1024 * 1024 ? TestLevel.Warn : TestLevel.Info;
            log.Report(new TestLine(
                Fmt.Row("Забрано железом", Fmt.Bytes(info.HardwareReservedBytes)), level));
            log.Report(TestLine.Dim("   Обычно это встроенная графика и служебные области. Если цифра"));
            log.Report(TestLine.Dim("   велика при отдельной видеокарте — посмотрите в BIOS объём памяти,"));
            log.Report(TestLine.Dim("   отданный встроенному видео."));
        }

        log.Report(TestLine.Empty);

        foreach (var m in info.Modules)
        {
            string head = m.Slot.Length > 0 ? m.Slot : "модуль";
            if (m.Channel.Length > 0) head += $" (канал {m.Channel})";

            log.Report(TestLine.Info($"   {head}"));

            string name = m.PartNumber.Length > 0 ? m.PartNumber : "модель не указана";
            if (m.Manufacturer.Length > 0) name = $"{m.Manufacturer} {name}";
            log.Report(TestLine.Dim($"      {name}"));

            var bits = new List<string> { Fmt.Bytes(m.CapacityBytes) };
            if (m.TypeName.Length > 0) bits.Add(m.TypeName);
            bits.Add($"{m.ConfiguredMts} МТ/с");
            if (m.RunsBelowRating) bits.Add($"паспорт {m.SpeedMts}");
            if (m.Ranks > 0) bits.Add($"{m.Ranks} {Plural(m.Ranks, "ранг", "ранга", "рангов")}");
            if (m.VoltageMv > 0) bits.Add($"{m.VoltageMv / 1000.0:F2} В");

            log.Report(TestLine.Dim($"      {string.Join(", ", bits)}"));

            if (m.SerialNumber.Length > 0)
                log.Report(TestLine.Dim($"      серийный номер {m.SerialNumber}"));
        }

        log.Report(TestLine.Empty);

        if (info.MixedModules)
        {
            log.Report(TestLine.Warn("Модули разные — это не один комплект."));
            log.Report(TestLine.Dim("   Работать так можно, но контроллер выставит режим по слабейшему,"));
            log.Report(TestLine.Dim("   а совместимость разных планок на высоких частотах — известный"));
            log.Report(TestLine.Dim("   источник редких сбоев. Если система нестабильна, это первый подозреваемый."));
            log.Report(TestLine.Empty);
        }
    }

    /// <summary>
    /// Слоты — единственная часть паспорта, где BIOS систематически врёт, поэтому
    /// она вынесена отдельно и подаётся как слова прошивки, а не как факт.
    ///
    /// Проверено на Gigabyte B450M S2H: SMBIOS сообщает четыре слота и потолок в
    /// 128 ГБ, тогда как на плате их физически два, а официальный потолок — 32 ГБ.
    /// Прошивка описывает возможности контроллера памяти процессора, и на компактных
    /// платах это расходится с реальностью. Совет «доставьте ещё две планки»,
    /// выданный по такой цифре, стоил бы владельцу лишней покупки.
    /// </summary>
    private static void ReportSlots(IProgress<TestLine> log, RamInfo info)
    {
        if (info.SlotsTotal > info.SlotsUsed)
        {
            log.Report(TestLine.Info(Fmt.Row("Слотов, по данным BIOS",
                $"{info.SlotsTotal}, занято {info.SlotsUsed}")));

            if (info.EmptySlots.Count > 0)
                log.Report(TestLine.Dim($"   Свободными названы: {string.Join(", ", info.EmptySlots)}"));

            log.Report(TestLine.Warn("   Этой цифре верить нельзя без проверки: прошивка часто описывает"));
            log.Report(TestLine.Warn("   возможности контроллера памяти процессора, а не разведённые на плате"));
            log.Report(TestLine.Warn("   слоты. Перед покупкой планок посмотрите на саму плату или её"));
            log.Report(TestLine.Warn("   спецификацию — модель платы показана в карточке железа."));
        }
        else if (info.SlotsTotal > 0)
        {
            log.Report(TestLine.Info(Fmt.Row("Слотов, по данным BIOS",
                $"{info.SlotsTotal}, все заняты")));
        }

        if (info.MaxCapacityBytes > 0)
        {
            log.Report(TestLine.Dim(Fmt.Row("   потолок по данным BIOS", Fmt.Bytes(info.MaxCapacityBytes))));
            log.Report(TestLine.Dim("   Та же оговорка: это потолок процессора, у платы он обычно ниже."));
        }
    }

    // ── Настройка ─────────────────────────────────────────────────────────

    /// <summary>
    /// Включён ли профиль XMP/EXPO. Прямого ответа не существует: SMBIOS сообщает
    /// только текущую скорость, а на что способны планки — знает лишь микросхема SPD
    /// на самой планке, читаемая по шине SMBus (это уже драйвер уровня ядра).
    /// Поэтому судим по трём косвенным признакам, и говорим об этом прямо.
    /// </summary>
    private static void ReportTuning(IProgress<TestLine> log, RamInfo info)
    {
        if (info.Modules.Count == 0) return;

        log.Report(TestLine.Head("Настройка"));

        int mts = info.ConfiguredMts;
        bool ddr5 = info.TypeName == "DDR5";
        var module = info.Modules[0];

        // Штатные (JEDEC) частоты — то, на чём память работает без профиля разгона
        int[] jedec = ddr5 ? [3600, 4000, 4400, 4800, 5200, 5600, 6000, 6400]
                           : [1600, 1866, 2133, 2400, 2666, 2933, 3200];

        bool looksStock = jedec.Contains(mts) && mts <= (ddr5 ? 5600 : 2666);
        bool lowVoltage = module.VoltageMv is > 0 and <= 1250 && !ddr5;
        bool belowRating = info.Modules.Any(m => m.RunsBelowRating);

        if (belowRating)
        {
            int rated = info.Modules.Max(m => m.SpeedMts);
            log.Report(TestLine.Bad(Fmt.Row("Профиль XMP/EXPO",
                $"выключен — память работает на {mts} вместо {rated} МТ/с")));
        }
        else if (looksStock)
        {
            log.Report(TestLine.Warn(Fmt.Row("Профиль XMP/EXPO", "скорее всего выключен")));
            log.Report(TestLine.Dim($"   {mts} МТ/с — это штатный режим по стандарту JEDEC. Игровые комплекты"));
            log.Report(TestLine.Dim("   продаются с более быстрым профилем внутри, но BIOS по умолчанию его"));
            log.Report(TestLine.Dim("   не включает: без ручного включения планки работают на штатной частоте."));
        }
        else
        {
            log.Report(TestLine.Good(Fmt.Row("Профиль XMP/EXPO", $"похоже, включён — {mts} МТ/с выше штатных")));
        }

        if (lowVoltage && (looksStock || belowRating))
            log.Report(TestLine.Dim($"   Косвенное подтверждение: напряжение {module.VoltageMv / 1000.0:F2} В — "
                                  + "штатное для DDR4. Профили разгона обычно требуют 1,35 В."));

        log.Report(TestLine.Dim("   Точно узнать, на что способны планки, можно только по маркировке на"));
        log.Report(TestLine.Dim("   наклейке или по их модели: чипа SPD программа не видит без драйвера."));

        log.Report(TestLine.Empty);
    }

    // ── Состояние ─────────────────────────────────────────────────────────

    private static void ReportUsage(IProgress<TestLine> log, RamUsage u)
    {
        log.Report(TestLine.Head("Что с памятью сейчас"));

        var usedLevel = u.UsedPercent switch
        {
            < 70 => TestLevel.Good,
            < 85 => TestLevel.Info,
            < 95 => TestLevel.Warn,
            _    => TestLevel.Bad,
        };
        log.Report(new TestLine(Fmt.Row("Занято",
            $"{Fmt.Bytes(u.UsedBytes)} из {Fmt.Bytes(u.TotalBytes)}   ({u.UsedPercent:F0}%)"), usedLevel));

        log.Report(TestLine.Info(Fmt.Row("Доступно", Fmt.Bytes(u.AvailableBytes))));

        if (u.StandbyBytes > 0)
        {
            log.Report(TestLine.Dim(Fmt.Row("   из них кэш про запас", Fmt.Bytes(u.StandbyBytes))));
            log.Report(TestLine.Dim("   Это файлы, которые Windows держит в памяти на случай повторного"));
            log.Report(TestLine.Dim("   обращения. Считается доступной: понадобится программе — отдаст сразу."));
        }

        if (u.FreeBytes > 0)
            log.Report(TestLine.Dim(Fmt.Row("   совсем свободно", Fmt.Bytes(u.FreeBytes))));

        log.Report(TestLine.Empty);

        // Обещано программам — это и есть настоящая потребность машины в памяти
        var commitLevel = u.CommitPercentOfPhysical switch
        {
            < 80  => TestLevel.Good,
            < 100 => TestLevel.Info,
            < 130 => TestLevel.Warn,
            _     => TestLevel.Bad,
        };
        log.Report(new TestLine(Fmt.Row("Обещано программам",
            $"{Fmt.Bytes(u.CommittedBytes)} из {Fmt.Bytes(u.CommitLimitBytes)}   "
          + $"({u.CommitPercentOfPhysical:F0}% от объёма планок)"), commitLevel));

        if (u.CommitPercentOfPhysical >= 100)
        {
            log.Report(TestLine.Warn("   Программам обещано больше памяти, чем есть физически. Разницу"));
            log.Report(TestLine.Warn("   держит файл подкачки на диске — это и есть причина подтормаживаний"));
            log.Report(TestLine.Warn("   при переключении между тяжёлыми окнами."));
        }

        if (u.CompressedBytes > 0)
        {
            var level = u.CompressedBytes > u.TotalBytes / 10 ? TestLevel.Warn : TestLevel.Info;
            log.Report(new TestLine(Fmt.Row("Сжато", Fmt.Bytes(u.CompressedBytes)), level));
            log.Report(TestLine.Dim("   Windows ужимает редко используемые страницы вместо выгрузки на диск."));
            log.Report(TestLine.Dim("   Это быстрее подкачки, но стоит процессорного времени — и сам факт"));
            log.Report(TestLine.Dim("   говорит, что памяти впритык."));
        }

        if (u.ModifiedBytes > 0)
            log.Report(TestLine.Dim(Fmt.Row("Ждёт записи на диск", Fmt.Bytes(u.ModifiedBytes))));

        log.Report(TestLine.Empty);

        var faultLevel = u.HardFaultsPerSec switch
        {
            < 20  => TestLevel.Good,
            < 200 => TestLevel.Warn,
            _     => TestLevel.Bad,
        };
        log.Report(new TestLine(Fmt.Row("Чтений с диска из-за нехватки", $"{u.HardFaultsPerSec:F0}/с"), faultLevel));
        if (u.HardFaultsPerSec >= 20)
            log.Report(TestLine.Dim("   Система лезет на диск за страницами, вытесненными из памяти."));

        if (u.PageFileAllocatedBytes > 0)
        {
            log.Report(TestLine.Info(Fmt.Row("Файл подкачки",
                $"{Fmt.Bytes(u.PageFileUsedBytes)} из {Fmt.Bytes(u.PageFileAllocatedBytes)}"
              + (u.PageFilePath.Length > 0 ? $"   {u.PageFilePath}" : ""))));

            if (u.PageFilePeakBytes > u.PageFileUsedBytes)
                log.Report(TestLine.Dim($"   Максимум с момента загрузки: {Fmt.Bytes(u.PageFilePeakBytes)}"));
        }

        if (u.PoolNonpagedBytes > 0)
        {
            // Невыгружаемый пул растёт при утечках в драйверах — редкая, но злая беда
            var level = u.PoolNonpagedBytes > 2L * 1024 * 1024 * 1024 ? TestLevel.Warn : TestLevel.Info;
            log.Report(new TestLine(Fmt.Row("Память ядра (невыгружаемая)",
                Fmt.Bytes(u.PoolNonpagedBytes)), level));

            if (u.PoolNonpagedBytes > 2L * 1024 * 1024 * 1024)
                log.Report(TestLine.Warn("   Много. Обычно это утечка в драйвере: она копится с момента загрузки"));
        }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Кто занял память (процессы одного имени сложены вместе):"));

        foreach (var p in RamInfoProbe.TopProcesses(8))
        {
            string count = p.Count > 1 ? $" ×{p.Count}" : "";
            log.Report(TestLine.Info($"   {p.Name + count,-28} {Fmt.Bytes(p.WorkingSetBytes),10}"));
        }

        log.Report(TestLine.Empty);
    }

    // ── Здоровье ──────────────────────────────────────────────────────────

    private static void ReportHealth(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head($"Следы сбоев за {HealthDays} суток"));

        // Синие экраны, чей код указывает именно на память
        int memoryCrashes = 0;
        DateTime? lastCrash = null;
        try
        {
            var wer = WindowsEventQuery.Query(
                "System", ["Microsoft-Windows-WER-SystemErrorReporting"], [1001], HealthDays, 40, ct);

            foreach (var ev in wer)
            {
                uint? code = BugCheckCodes.Parse(ev.Get("param1")) ?? BugCheckCodes.Parse(ev.Get("BugcheckCode"));
                if (code is null) continue;

                var info = BugCheckCodes.Lookup(code.Value);
                if (info?.Suspect != CrashSuspect.Memory) continue;

                memoryCrashes++;
                lastCrash ??= ev.Time;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        log.Report(new TestLine(
            Fmt.Row("Синие экраны по памяти", memoryCrashes == 0 ? "нет" : memoryCrashes.ToString()),
            memoryCrashes == 0 ? TestLevel.Good : TestLevel.Bad));

        if (lastCrash is { } t)
            log.Report(TestLine.Warn($"   Последний: {t:dd.MM.yyyy HH:mm}. Подробный разбор — кнопка «Отчёт о сбоях ПК»."));

        // WHEA: аппаратные ошибки, о которых сообщило само железо
        try
        {
            var whea = WindowsEventQuery.Query(
                "System", ["Microsoft-Windows-WHEA-Logger"], [], HealthDays, 60, ct);

            log.Report(new TestLine(
                Fmt.Row("Аппаратные ошибки (WHEA)", whea.Count == 0 ? "нет" : whea.Count.ToString()),
                whea.Count == 0 ? TestLevel.Good : TestLevel.Warn));

            if (whea.Count > 0)
                log.Report(TestLine.Warn($"   Последняя: {whea.Max(e => e.Time):dd.MM.yyyy HH:mm}. "
                                       + "Что именно сбоило — в отчёте о сбоях ПК."));
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Средство проверки памяти Windows — если владелец его когда-либо запускал
        try
        {
            var diag = WindowsEventQuery.Query(
                "System", ["Microsoft-Windows-MemoryDiagnostics-Results"], [], 3650, 5, ct);

            if (diag.Count == 0)
            {
                log.Report(TestLine.Dim(Fmt.Row("Средство проверки Windows", "не запускалось")));
            }
            else
            {
                var last = diag.OrderByDescending(e => e.Time).First();
                // Код 1201 — ошибки найдены, 1101 — память исправна
                bool bad = last.Id == 1201 || last.Message.Contains("обнаруж", StringComparison.OrdinalIgnoreCase);
                log.Report(new TestLine(
                    Fmt.Row("Средство проверки Windows",
                            $"{(bad ? "нашло ошибки" : "ошибок не нашло")}, {last.Time:dd.MM.yyyy}"),
                    bad ? TestLevel.Bad : TestLevel.Good));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        log.Report(TestLine.Empty);
    }

    // ── Насколько замеры близки к пределу ─────────────────────────────────

    private static void ReportEfficiency(IProgress<TestLine> log, RamInfo info, RamBenchmarkResult bench)
    {
        if (info.TheoreticalGbs <= 0) return;

        double best = Math.Max(bench.ReadGbs, bench.WriteGbs);
        double efficiency = best / info.TheoreticalGbs * 100;

        var level = efficiency switch
        {
            >= 70 => TestLevel.Good,
            >= 50 => TestLevel.Info,
            _     => TestLevel.Warn,
        };

        log.Report(new TestLine(Fmt.Row("Достигнуто от предела",
            $"{efficiency:F0}%   ({best:F1} из {info.TheoreticalGbs:F1} ГБ/с)"), level));

        log.Report(TestLine.Dim("   Предел считается по частоте и числу каналов; 70–90% для настольной"));
        log.Report(TestLine.Dim("   машины — норма, остальное съедают накладные расходы контроллера."));

        if (efficiency < 50 && info.ChannelsKnown && info.ChannelsUsed >= 2)
            log.Report(TestLine.Warn("   Заметно ниже ожидаемого. Проверьте, не занят ли компьютер чем-то ещё."));

        log.Report(TestLine.Empty);
    }

    // ── Выводы ────────────────────────────────────────────────────────────

    private static void ReportVerdict(
        IProgress<TestLine> log, RamInfo? info, RamUsage? usage, RamBenchmarkResult? bench)
    {
        log.Report(TestLine.Head("Выводы"));

        if (info is null && usage is null)
        {
            log.Report(TestLine.Dim("Данных для выводов не собрано."));
            return;
        }

        double totalGb = (info?.InstalledBytes ?? usage?.TotalBytes ?? 0) / 1024.0 / 1024 / 1024;

        // ── Объём ─────────────────────────────────────────────────────────
        if (totalGb > 0)
        {
            double nominal = Math.Round(totalGb);
            var (level, verdict) = nominal switch
            {
                >= 64 => (TestLevel.Good, "с запасом под что угодно, включая виртуальные машины и большие модели"),
                >= 32 => (TestLevel.Good, "комфортно: игры, десятки вкладок и тяжёлые программы одновременно"),
                >= 16 => (TestLevel.Info, "рабочий минимум на сегодня: игры идут, но вместе с браузером и "
                                        + "фоновыми программами память кончается"),
                >= 8  => (TestLevel.Warn, "мало для новых игр и современного браузера — подтормаживания неизбежны"),
                _     => (TestLevel.Bad,  "критически мало для нынешней Windows"),
            };
            log.Report(new TestLine(Fmt.Row($"Объём {nominal:F0} ГБ", verdict), level));
        }

        // ── Хватает ли на деле ────────────────────────────────────────────
        if (usage is not null)
        {
            bool tight = usage.CommitPercentOfPhysical >= 100
                      || usage.HardFaultsPerSec >= 50
                      || usage.CompressedBytes > usage.TotalBytes / 10;

            if (tight)
            {
                log.Report(TestLine.Empty);
                // Признаков три, но показываем только сработавшие: обещать «три» и
                // напечатать два — верный способ подорвать доверие к остальному отчёту
                log.Report(TestLine.Warn("Памяти сейчас не хватает, и вот по чему это видно:"));

                if (usage.CommitPercentOfPhysical >= 100)
                    log.Report(TestLine.Warn($"   • программам обещано {usage.CommitPercentOfPhysical:F0}% "
                                           + "от объёма планок — остальное держит диск;"));
                if (usage.CompressedBytes > 0)
                    log.Report(TestLine.Warn($"   • {Fmt.Bytes(usage.CompressedBytes)} пришлось сжать, "
                                           + "чтобы не выгружать на диск;"));
                if (usage.HardFaultsPerSec >= 20)
                    log.Report(TestLine.Warn($"   • {usage.HardFaultsPerSec:F0} чтений с диска в секунду "
                                           + "из-за вытесненных страниц."));

                log.Report(TestLine.Dim("   На глаз это ощущается как задержка при переключении на давно"));
                log.Report(TestLine.Dim("   не тронутое окно и просадки при заходе в игру со свёрнутым браузером."));

                if (info is { } i && i.Modules.Count > 0)
                {
                    double moduleGb = i.Modules[0].CapacityBytes / 1024.0 / 1024 / 1024;

                    log.Report(TestLine.Dim("   Расширение зависит от того, сколько слотов на плате на самом"));
                    log.Report(TestLine.Dim("   деле — BIOS в этом вопросе ненадёжен (см. раздел «Паспорт»)."));
                    log.Report(TestLine.Dim($"   Если слоты свободны: две планки по {moduleGb:F0} ГБ доведут объём"));
                    log.Report(TestLine.Dim($"   до {totalGb + moduleGb * 2:F0} ГБ. Если все заняты — только замена комплекта,"));
                    log.Report(TestLine.Dim($"   например на {totalGb * 2:F0} ГБ двумя планками."));
                    log.Report(TestLine.Dim("   Брать желательно один комплект: разные планки контроллер сводит"));
                    log.Report(TestLine.Dim("   по слабейшей, а на высоких частотах они ещё и не всегда уживаются."));
                }
            }
            else
            {
                log.Report(TestLine.Good("Памяти сейчас хватает: подкачка почти не задействована."));
            }
        }

        // ── Настройка и скорость ──────────────────────────────────────────
        if (info is not null && info.Modules.Count > 0)
        {
            log.Report(TestLine.Empty);

            bool stock = info.Modules.Any(m => m.RunsBelowRating)
                      || (info.TypeName == "DDR4" && info.ConfiguredMts <= 2666)
                      || (info.TypeName == "DDR5" && info.ConfiguredMts <= 5200);

            if (stock)
            {
                int target = info.TypeName == "DDR5" ? 6000 : 3200;
                double gain = (double)target / Math.Max(1, info.ConfiguredMts);

                log.Report(TestLine.Warn($"Память работает на {info.ConfiguredMts} МТ/с. Если планки "
                                       + $"рассчитаны на {target}, включение профиля"));
                log.Report(TestLine.Warn($"XMP/EXPO в BIOS даст примерно +{(gain - 1) * 100:F0}% "
                                       + "пропускной способности и заметно меньшую задержку."));
                log.Report(TestLine.Dim("   Делается это одним переключателем в BIOS (у Gigabyte он называется"));
                log.Report(TestLine.Dim("   XMP или EXPO, у ASUS — D.O.C.P., у MSI — A-XMP). Если после включения"));
                log.Report(TestLine.Dim("   система перестанет стабильно загружаться — вернуть обратно тем же"));
                log.Report(TestLine.Dim("   переключателем, вреда не будет."));

                if (IsAmdCpu())
                {
                    log.Report(TestLine.Warn("   На процессорах AMD Ryzen это важнее, чем на прочих: от частоты"));
                    log.Report(TestLine.Warn("   памяти напрямую зависит частота внутренней шины между блоками"));
                    log.Report(TestLine.Warn("   процессора, а через неё — задержка обращения к памяти целиком."));
                    log.Report(TestLine.Dim("   Отсюда и прибавка в играх: она обычно больше, чем даёт сам рост"));
                    log.Report(TestLine.Dim("   пропускной способности."));
                }
            }

            if (info.ChannelsKnown && info.ChannelsUsed == 1 && info.SlotsUsed == 1)
            {
                log.Report(TestLine.Empty);
                log.Report(TestLine.Warn("Одна планка — одноканальный режим. Вторая такая же почти удваивает"));
                log.Report(TestLine.Warn("пропускную способность; в играх и во встроенной графике это самый"));
                log.Report(TestLine.Warn("дешёвый способ прибавить кадров."));
            }
            else if (info.ChannelsKnown && info.ChannelsUsed == 1 && info.SlotsUsed > 1)
            {
                log.Report(TestLine.Empty);
                log.Report(TestLine.Bad("Планок несколько, но канал один — они стоят в слотах одного канала."));
                log.Report(TestLine.Bad("Переставьте через слот (обычно это слоты 2 и 4) — станет двухканальный"));
                log.Report(TestLine.Bad("режим, и пропускная способность удвоится бесплатно."));
            }
        }

        // ── Что это даёт в играх и в языковых моделях ─────────────────────
        if (bench is not null)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Head("Что это даёт на практике"));

            log.Report(TestLine.Dim("В играх память важна не пропускной способностью, а задержкой: игровой"));
            log.Report(TestLine.Dim("движок постоянно ходит за мелкими объектами по случайным адресам."));
            log.Report(new TestLine(Fmt.Row("Задержка", $"{bench.LatencyNs:F1} нс — " + bench.LatencyNs switch
            {
                < 70  => "хорошо, память не тормозит процессор",
                < 90  => "нормально",
                < 110 => "заметно выше хорошей; на слабых кадрах это чувствуется",
                _     => "высокая: процессор часто ждёт память",
            }), bench.LatencyNs < 90 ? TestLevel.Good : TestLevel.Warn));

            // Языковые модели в оперативной памяти: та же логика, что для видеокарты,
            // только полоса в разы уже — поэтому и скорость выдачи совсем другая
            if (bench.ReadGbs > 0)
            {
                log.Report(TestLine.Empty);
                log.Report(TestLine.Dim("Языковая модель, не поместившаяся в видеопамять, считается на процессоре"));
                log.Report(TestLine.Dim("и упирается ровно в эту пропускную способность — чтобы выдать одно слово,"));
                log.Report(TestLine.Dim("нужно прочитать все веса модели целиком:"));

                // Влезет ли модель — считаем по свободной памяти, а не по общему объёму:
                // операционная система и открытые программы своё уже забрали
                double freeGb = (usage?.AvailableBytes ?? 0) / 1024.0 / 1024 / 1024;

                foreach (var billions in new[] { 7.0, 14.0, 32.0 })
                {
                    double sizeGb = billions * 0.6;   // 4 бита на параметр плюс накладные
                    double tokens = bench.ReadGbs / sizeGb;
                    bool fits = freeGb <= 0 || sizeGb <= freeGb;

                    log.Report(new TestLine(
                        $"   модель {billions,4:F0} млрд (4 бита), {sizeGb,4:F1} ГБ   "
                      + (fits ? $"предел {tokens,4:F1} слов/с" : "сейчас не помещается в свободную память"),
                        fits ? TestLevel.Info : TestLevel.Warn));
                }

                log.Report(TestLine.Dim("   На практике выходит вдвое-втрое меньше. Для сравнения: видеопамять"));
                log.Report(TestLine.Dim("   игровой карты быстрее оперативной в 10–20 раз."));
            }
        }
    }

    /// <summary>
    /// Процессор AMD? Для памяти это не праздный вопрос: у Ryzen частота шины между
    /// блоками процессора привязана к частоте памяти, и цена невключённого профиля
    /// XMP там выше, чем на процессорах Intel.
    /// </summary>
    private static bool IsAmdCpu()
    {
        try
        {
            string? vendor = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")
                ?.GetValue("VendorIdentifier")?.ToString();

            return vendor is not null &&
                   vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Русское склонение после числа: 1 модуль, 2 модуля, 5 модулей.</summary>
    private static string Plural(int n, string one, string few, string many)
    {
        int last2 = Math.Abs(n) % 100;
        int last = Math.Abs(n) % 10;
        if (last2 is >= 11 and <= 14) return many;
        return last switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
