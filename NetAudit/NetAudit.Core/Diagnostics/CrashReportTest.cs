using System.IO;
using Microsoft.Win32;
using NetAudit.Core.Logging;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Отчёт о сбоях компьютера: почему машина ушла в синий экран, перезагрузилась
/// сама или зависла.
///
/// Собирает картину из четырёх независимых источников, потому что ни один из них
/// по отдельности полной не бывает:
///   1. журнал Windows — факт сбоя, код синего экрана, аппаратные ошибки WHEA,
///      срывы видеодрайвера, ошибки диска;
///   2. файлы дампов на диске — подтверждение, что синий экран действительно был
///      записан и есть что разбирать;
///   3. собственный чёрный ящик — что происходило с температурами и нагрузкой в
///      последние секунды перед смертью. Журнал Windows об этом не знает ничего;
///   4. настройки записи дампов — если они выключены, следующий синий экран
///      снова не оставит следов, и это надо сказать заранее, а не потом.
///
/// Прав администратора не требует: System и Application читаются обычным
/// пользователем. Список файлов в C:\Windows\Minidump без прав может не читаться —
/// это отмечается в выводе, а не выдаётся за «дампов нет».
/// </summary>
public sealed class CrashReportTest(int days = 30) : IDiagnosticTest
{
    public string Title => "Отчёт о сбоях компьютера";

    private const string SystemLog = "System";

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head($"Отчёт о сбоях компьютера — за последние {days} сут."));
        log.Report(TestLine.Dim("Сведения берутся из журналов Windows, файлов дампов и собственного"));
        log.Report(TestLine.Dim("журнала состояния. Компьютер при этом не нагружается."));
        log.Report(TestLine.Empty);

        await Task.Run(() => Collect(log, ct), ct).ConfigureAwait(false);
    }

    private void Collect(IProgress<TestLine> log, CancellationToken ct)
    {
        var suspects = new List<CrashSuspect>();

        ReportUptime(log);
        ct.ThrowIfCancellationRequested();

        int hardShutdowns = ReportHardShutdowns(log, suspects, ct);
        ct.ThrowIfCancellationRequested();

        ReportMinidumps(log);
        ct.ThrowIfCancellationRequested();

        ReportWhea(log, suspects, ct);
        ct.ThrowIfCancellationRequested();

        ReportGpu(log, suspects, ct);
        ct.ThrowIfCancellationRequested();

        ReportDisk(log, suspects, ct);
        ct.ThrowIfCancellationRequested();

        ReportThermalThrottle(log, suspects, ct);
        ct.ThrowIfCancellationRequested();

        ReportAppCrashes(log, ct);
        ct.ThrowIfCancellationRequested();

        ReportBlackBox(log, suspects);
        ReportDumpSettings(log);

        Verdict(log, suspects, hardShutdowns);
    }

    // ── Время работы ──────────────────────────────────────────────────────

    private void ReportUptime(IProgress<TestLine> log)
    {
        log.Report(TestLine.Head("Общие сведения"));

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        log.Report(TestLine.Info(Fmt.Row("Компьютер работает", HumanTime(uptime))));

        var oldest = JournalStart();
        if (oldest is not null)
        {
            var depth = DateTime.Now - oldest.Value;
            log.Report(TestLine.Info(Fmt.Row("Глубина журнала Windows", HumanTime(depth))));

            if (depth < TimeSpan.FromDays(2))
            {
                log.Report(TestLine.Warn("   Журнал очень короткий — система установлена недавно или журнал очищали."));
                log.Report(TestLine.Dim("   Прошлых сбоев в нём просто нет, и отсутствие записей ничего не доказывает."));
            }
        }
        else
        {
            log.Report(TestLine.Warn(Fmt.Row("Глубина журнала Windows", "не удалось определить")));
        }

        log.Report(TestLine.Empty);
    }

    /// <summary>
    /// С какого момента журнал вообще что-то помнит. Считать события целиком
    /// слишком дорого — журнал бывает на сотни тысяч записей, поэтому берём дату
    /// создания самого файла журнала: она и есть начало его ведения.
    /// </summary>
    private static DateTime? JournalStart()
    {
        try
        {
            string evtx = Path.Combine(Environment.SystemDirectory, "winevt", "Logs", "System.evtx");
            if (File.Exists(evtx)) return File.GetCreationTime(evtx);
        }
        catch { }
        return null;
    }

    // ── Аварийные завершения и синие экраны ───────────────────────────────

    private int ReportHardShutdowns(IProgress<TestLine> log, List<CrashSuspect> suspects, CancellationToken ct)
    {
        log.Report(TestLine.Head("Аварийные завершения работы"));

        // 41 — Windows не успела закрыться штатно: синий экран, зависание, пропажа питания
        var kp41 = WindowsEventQuery.Query(SystemLog, ["Microsoft-Windows-Kernel-Power"], [41], days, 30, ct);
        // 1001 — WER записала отчёт о синем экране, здесь же его код
        var wer = WindowsEventQuery.Query(SystemLog, ["Microsoft-Windows-WER-SystemErrorReporting"], [1001], days, 30, ct);
        // 6008 — «Предыдущее завершение работы было неожиданным»
        var el6008 = WindowsEventQuery.Query(SystemLog, ["EventLog"], [6008], days, 30, ct);

        if (kp41.Count == 0 && wer.Count == 0 && el6008.Count == 0)
        {
            log.Report(TestLine.Good("Аварийных завершений за период не зафиксировано"));
            log.Report(TestLine.Empty);
            return 0;
        }

        log.Report(TestLine.Bad(Fmt.Row("Неожиданных выключений", $"{kp41.Count}")));
        if (wer.Count > 0)
            log.Report(TestLine.Bad(Fmt.Row("Записано синих экранов", $"{wer.Count}")));
        log.Report(TestLine.Empty);

        // Синие экраны с расшифровкой кода
        foreach (var ev in wer.Take(10))
        {
            ct.ThrowIfCancellationRequested();
            uint? code = ExtractBugCheck(ev);
            PrintBugCheck(log, suspects, ev.Time, code, ev.Message);
        }

        // Kernel-Power 41 несёт код синего экрана в своих полях. Нулевой код
        // означает, что синего экрана не было вовсе — питание пропало или машина
        // зависла насмерть. Это принципиально разные диагнозы.
        foreach (var ev in kp41.Take(10))
        {
            ct.ThrowIfCancellationRequested();

            uint? code = BugCheckCodes.Parse(ev.Get("BugcheckCode"));
            bool sameAsWer = wer.Any(w => Math.Abs((w.Time - ev.Time).TotalMinutes) < 5);

            if (code is > 0)
            {
                if (!sameAsWer) PrintBugCheck(log, suspects, ev.Time, code, "");
                continue;
            }

            string reason = ev.Get("PowerButtonTimestamp") is "0" or ""
                ? "питание пропало, машина зависла или была выключена кнопкой удержанием"
                : "выключено кнопкой питания";

            log.Report(new TestLine(
                Fmt.Row($"{ev.Time:dd.MM HH:mm}", $"выключение без синего экрана — {reason}", 18),
                TestLevel.Warn));
            suspects.Add(CrashSuspect.Power);
        }

        log.Report(TestLine.Empty);
        return kp41.Count;
    }

    private static uint? ExtractBugCheck(WinEvent ev)
    {
        // У WER-события параметры лежат в безымянных полях, первое из них — код
        foreach (var kv in ev.Data)
        {
            var parsed = BugCheckCodes.Parse(kv.Value);
            if (parsed is > 0 and not 0xFFFFFFFF) return parsed;
        }

        // Запасной путь: код есть в тексте вида «0x00000124 (0x...)»
        int i = ev.Message.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (i >= 0 && ev.Message.Length >= i + 10)
            return BugCheckCodes.Parse(ev.Message.Substring(i, 10));

        return null;
    }

    private static void PrintBugCheck(
        IProgress<TestLine> log, List<CrashSuspect> suspects, DateTime time, uint? code, string message)
    {
        if (code is null or 0)
        {
            log.Report(new TestLine(Fmt.Row($"{time:dd.MM HH:mm}", "синий экран, код не определился", 18), TestLevel.Bad));
            return;
        }

        var info = BugCheckCodes.Lookup(code.Value);
        if (info is null)
        {
            log.Report(new TestLine(
                Fmt.Row($"{time:dd.MM HH:mm}", $"синий экран 0x{code.Value:X8} — расшифровки нет", 18), TestLevel.Bad));
            log.Report(TestLine.Dim($"   Код редкий. Поищите его номер в сети, а лучше разберите дамп."));
            return;
        }

        log.Report(new TestLine(
            Fmt.Row($"{time:dd.MM HH:mm}", $"{info.Value.Hex}  {info.Value.Name}", 18), TestLevel.Bad));
        log.Report(TestLine.Dim($"   {info.Value.Meaning}"));
        log.Report(TestLine.Warn($"   Первый подозреваемый: {info.Value.SuspectText}"));
        suspects.Add(info.Value.Suspect);
    }

    // ── Дампы ─────────────────────────────────────────────────────────────

    private static void ReportMinidumps(IProgress<TestLine> log)
    {
        log.Report(TestLine.Head("Файлы дампов"));

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
        try
        {
            if (!Directory.Exists(dir))
            {
                log.Report(TestLine.Good($"Папка {dir} пуста или не создавалась — синих экранов не было"));
            }
            else
            {
                var files = new DirectoryInfo(dir).GetFiles("*.dmp")
                    .OrderByDescending(f => f.LastWriteTime).ToList();

                if (files.Count == 0)
                {
                    log.Report(TestLine.Good($"Дампов в {dir} нет"));
                }
                else
                {
                    log.Report(TestLine.Bad(Fmt.Row("Дампов на диске", $"{files.Count}")));
                    foreach (var f in files.Take(8))
                        log.Report(TestLine.Info(Fmt.Row($"   {f.LastWriteTime:dd.MM HH:mm}", $"{f.Name}  ({Fmt.Bytes(f.Length)})", 18)));

                    log.Report(TestLine.Empty);
                    log.Report(TestLine.Dim("   Разобрать дамп можно бесплатной утилитой BlueScreenView или WinDbg —"));
                    log.Report(TestLine.Dim("   они называют конкретный драйвер, с которого начался сбой."));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            log.Report(TestLine.Warn($"Нет доступа к {dir} — запустите NetAudit от администратора, чтобы проверить дампы"));
        }
        catch (Exception ex)
        {
            log.Report(TestLine.Warn($"Не удалось прочитать папку дампов: {ex.Message}"));
        }

        try
        {
            string full = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                log.Report(TestLine.Info(Fmt.Row("Полный дамп", $"{fi.LastWriteTime:dd.MM.yyyy HH:mm}  ({Fmt.Bytes(fi.Length)})")));
            }
        }
        catch { }

        log.Report(TestLine.Empty);
    }

    // ── Аппаратные ошибки WHEA ────────────────────────────────────────────

    private void ReportWhea(IProgress<TestLine> log, List<CrashSuspect> suspects, CancellationToken ct)
    {
        log.Report(TestLine.Head("Аппаратные ошибки (WHEA)"));
        log.Report(TestLine.Dim("Это сообщения самого железа об ошибках — самый весомый сигнал в отчёте."));

        var events = WindowsEventQuery.Query(SystemLog, ["Microsoft-Windows-WHEA-Logger"], [], days, 60, ct);

        if (events.Count == 0)
        {
            log.Report(TestLine.Good("Аппаратных ошибок не зарегистрировано"));
            log.Report(TestLine.Empty);
            return;
        }

        // 17 — исправленная ошибка (машина выжила), 18/19/20/47 — серьёзнее
        var corrected = events.Where(e => e.Id is 17 or 47).ToList();
        var fatal = events.Where(e => e.Id is 18 or 19 or 20 or 46).ToList();

        if (corrected.Count > 0)
        {
            log.Report(TestLine.Warn(Fmt.Row("Исправленных ошибок", $"{corrected.Count}")));
            log.Report(TestLine.Dim("   Железо заметило ошибку и само её исправило. Единичные бывают у всех,"));
            log.Report(TestLine.Dim("   но регулярные — это ранний признак деградации памяти, шины PCIe или питания."));
            foreach (var e in corrected.Take(5))
                log.Report(TestLine.Info(Fmt.Row($"   {e.Time:dd.MM HH:mm}", DescribeWhea(e), 18)));
            suspects.Add(CrashSuspect.Memory);
        }

        if (fatal.Count > 0)
        {
            log.Report(TestLine.Bad(Fmt.Row("Неисправимых ошибок", $"{fatal.Count}")));
            foreach (var e in fatal.Take(5))
                log.Report(TestLine.Bad(Fmt.Row($"   {e.Time:dd.MM HH:mm}", DescribeWhea(e), 18)));
            log.Report(TestLine.Bad("   Это прямое сообщение железа о неисправности. Гадать здесь не о чем:"));
            log.Report(TestLine.Bad("   проверяйте память, питание и температуры целенаправленно."));
            suspects.Add(CrashSuspect.Cpu);
        }

        log.Report(TestLine.Empty);
    }

    private static string DescribeWhea(WinEvent e)
    {
        string type = e.Get("ErrorType");
        string source = e.Get("ErrorSourceType");

        string kind = type switch
        {
            "1" => "ошибка памяти",
            "2" => "ошибка кэша процессора",
            "3" => "ошибка TLB процессора",
            "4" => "ошибка шины или соединения",
            "5" => "ошибка микроархитектуры процессора",
            "8" => "ошибка PCI Express",
            _   => e.Id switch
            {
                17 => "исправленная аппаратная ошибка",
                18 => "неисправимая аппаратная ошибка",
                19 => "исправленная ошибка памяти",
                20 => "ошибка PCI Express",
                47 => "исправленная ошибка памяти (порог превышен)",
                _  => $"событие {e.Id}",
            },
        };

        return string.IsNullOrEmpty(source) ? kind : $"{kind} (источник {source})";
    }

    // ── Видеокарта ────────────────────────────────────────────────────────

    private void ReportGpu(IProgress<TestLine> log, List<CrashSuspect> suspects, CancellationToken ct)
    {
        log.Report(TestLine.Head("Видеокарта и её драйвер"));

        // 4101 — «Видеодрайвер перестал отвечать и был восстановлен» (TDR)
        var tdr = WindowsEventQuery.Query(SystemLog, ["Display"], [4101, 4100], days, 40, ct);
        // Ошибки самих драйверов: у них манифест не зарегистрирован, текст пустой,
        // поэтому ценен сам факт и количество
        var vendor = WindowsEventQuery.Query(
            SystemLog, ["nvlddmkm", "amdkmdag", "amdwddmg", "igfx", "igfxn"], [], days, 200, ct,
            WindowsEventQuery.ErrorsOnly);

        if (tdr.Count == 0 && vendor.Count == 0)
        {
            log.Report(TestLine.Good("Срывов видеодрайвера не зафиксировано"));
            log.Report(TestLine.Empty);
            return;
        }

        if (tdr.Count > 0)
        {
            log.Report(TestLine.Bad(Fmt.Row("Срывов драйвера (TDR)", $"{tdr.Count}")));
            log.Report(TestLine.Dim("   Драйвер перестал отвечать, и Windows его перезапустила — в игре это"));
            log.Report(TestLine.Dim("   выглядит как чёрный экран на секунду или вылет."));
            foreach (var e in tdr.Take(6))
                log.Report(TestLine.Info(Fmt.Row($"   {e.Time:dd.MM HH:mm}", Shorten(e.Message), 18)));
            suspects.Add(CrashSuspect.Gpu);
        }

        if (vendor.Count > 0)
        {
            var byId = vendor.GroupBy(e => new { e.Provider, e.Id })
                             .OrderByDescending(g => g.Count());

            log.Report(TestLine.Warn(Fmt.Row("Ошибок видеодрайвера", $"{vendor.Count}")));
            foreach (var g in byId.Take(5))
            {
                var last = g.Max(e => e.Time);
                log.Report(TestLine.Warn(Fmt.Row(
                    $"   {g.Key.Provider} код {g.Key.Id}",
                    $"{g.Count()} шт., последняя {last:dd.MM HH:mm}", 26)));
                log.Report(TestLine.Dim($"   {DescribeVendorGpuEvent(g.Key.Provider, g.Key.Id)}"));
            }

            // Серия ошибок за минуту — это не фон, а конкретный эпизод
            var burst = FindBurst(vendor);
            if (burst is not null)
            {
                log.Report(TestLine.Bad($"   Серия: {burst.Value.Count} ошибок за {burst.Value.Span.TotalSeconds:F0} с около {burst.Value.At:dd.MM HH:mm:ss}"));
                log.Report(TestLine.Dim("   Такая кучность — признак конкретного эпизода (нагрузка, игра, тест),"));
                log.Report(TestLine.Dim("   а не редких фоновых сообщений драйвера."));
            }

            suspects.Add(CrashSuspect.Gpu);
        }

        log.Report(TestLine.Empty);
    }

    private static string DescribeVendorGpuEvent(string provider, int id) => (provider, id) switch
    {
        ("nvlddmkm", 153) => "Ошибка на стороне видеокарты NVIDIA: драйвер сообщил о сбое в работе GPU. " +
                            "Частые причины — нестабильная видеопамять, разгон, перегрев или нехватка питания.",
        ("nvlddmkm", 13)  => "Сбой команды видеодрайвера NVIDIA.",
        ("nvlddmkm", 14)  => "Видеодрайвер NVIDIA сообщил о неустранимой ошибке.",
        ("nvlddmkm", 0)   => "Общее сообщение об ошибке драйвера NVIDIA.",
        ("amdkmdag", _)   => "Сообщение об ошибке драйвера AMD.",
        _                 => "Сообщение об ошибке видеодрайвера. Текст недоступен: манифест драйвера " +
                            "в системе не зарегистрирован, поэтому важны факт и количество.",
    };

    /// <summary>Наибольшая кучная серия событий: сколько их пришло в пределах минуты.</summary>
    private static (int Count, DateTime At, TimeSpan Span)? FindBurst(List<WinEvent> events)
    {
        if (events.Count < 3) return null;

        var times = events.Select(e => e.Time).OrderBy(t => t).ToList();
        int bestCount = 0;
        DateTime bestAt = default;
        TimeSpan bestSpan = default;

        int start = 0;
        for (int end = 0; end < times.Count; end++)
        {
            while (times[end] - times[start] > TimeSpan.FromMinutes(1)) start++;
            int count = end - start + 1;
            if (count > bestCount)
            {
                bestCount = count;
                bestAt = times[start];
                bestSpan = times[end] - times[start];
            }
        }

        return bestCount >= 3 ? (bestCount, bestAt, bestSpan) : null;
    }

    // ── Накопители ────────────────────────────────────────────────────────

    private void ReportDisk(IProgress<TestLine> log, List<CrashSuspect> suspects, CancellationToken ct)
    {
        log.Report(TestLine.Head("Накопители"));

        // Только ошибки и критические: у этих провайдеров полно информационных
        // сообщений с теми же кодами («том работоспособен»), и без фильтра
        // здоровый диск выглядел бы неисправным
        var disk = WindowsEventQuery.Query(SystemLog, ["disk", "Disk"], [7, 11, 51, 52, 153], days, 50, ct,
                                           WindowsEventQuery.ErrorsOnly);
        var ntfs = WindowsEventQuery.Query(SystemLog, ["Ntfs", "Microsoft-Windows-Ntfs"], [55, 137, 140], days, 50, ct,
                                           WindowsEventQuery.ErrorsOnly);
        var volmgr = WindowsEventQuery.Query(SystemLog, ["volmgr"], [46, 161, 162], days, 20, ct,
                                             WindowsEventQuery.ErrorsOnly);

        if (disk.Count == 0 && ntfs.Count == 0 && volmgr.Count == 0)
        {
            log.Report(TestLine.Good("Ошибок накопителей не зафиксировано"));
            log.Report(TestLine.Empty);
            return;
        }

        foreach (var (list, label) in new[] { (disk, "диск"), (ntfs, "файловая система"), (volmgr, "том") })
        {
            if (list.Count == 0) continue;

            log.Report(TestLine.Bad(Fmt.Row($"Ошибок ({label})", $"{list.Count}")));
            foreach (var g in list.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).Take(4))
            {
                log.Report(TestLine.Warn(Fmt.Row(
                    $"   код {g.Key}", $"{g.Count()} шт., последняя {g.Max(e => e.Time):dd.MM HH:mm}", 26)));
                log.Report(TestLine.Dim($"   {DescribeDiskEvent(g.Key)}"));
            }
            suspects.Add(CrashSuspect.Disk);
        }

        log.Report(TestLine.Dim("   Ошибки ввода-вывода на SSD часто означают проблему кабеля SATA или порта,"));
        log.Report(TestLine.Dim("   а не самой микросхемы. Начните с перестановки кабеля."));
        log.Report(TestLine.Empty);
    }

    private static string DescribeDiskEvent(int id) => id switch
    {
        7   => "Устройство содержит сбойный блок.",
        11  => "Контроллер обнаружил ошибку на диске — самая частая жалоба на кабель, порт или сам накопитель.",
        51  => "Ошибка при записи в страничный файл — сбой ввода-вывода.",
        52  => "Диск предупреждает о скором отказе (предиктивный сбой SMART).",
        153 => "Запрос ввода-вывода был повторён — обычно кратковременный сбой связи с диском.",
        55  => "Повреждение структуры файловой системы; помогает chkdsk.",
        137 => "Файловая система в состоянии, требующем проверки.",
        140 => "Не удалось сбросить данные на диск — возможна потеря записанного.",
        46  => "Не удалось создать файл аварийного дампа.",
        161 => "Не удалось записать аварийный дамп.",
        _   => "Сообщение подсистемы хранения.",
    };

    // ── Троттлинг из журнала ──────────────────────────────────────────────

    private void ReportThermalThrottle(IProgress<TestLine> log, List<CrashSuspect> suspects, CancellationToken ct)
    {
        // 37 — «Скорость процессора ограничена из-за нагрева» — редкое, но однозначное событие
        var ev = WindowsEventQuery.Query(
            SystemLog, ["Microsoft-Windows-Kernel-Processor-Power"], [37, 38], days, 20, ct);

        if (ev.Count == 0) return;

        log.Report(TestLine.Head("Перегрев процессора"));
        log.Report(TestLine.Bad(Fmt.Row("Ограничений частоты из-за нагрева", $"{ev.Count}")));
        foreach (var e in ev.Take(5))
            log.Report(TestLine.Warn(Fmt.Row($"   {e.Time:dd.MM HH:mm}", "частота снижена системой охлаждения", 18)));
        log.Report(TestLine.Dim("   Windows сама зафиксировала троттлинг: кулер, термопаста или продув корпуса."));
        log.Report(TestLine.Empty);
        suspects.Add(CrashSuspect.Power);
    }

    // ── Падения программ ──────────────────────────────────────────────────

    private void ReportAppCrashes(IProgress<TestLine> log, CancellationToken ct)
    {
        var ev = WindowsEventQuery.Query("Application", ["Application Error"], [1000], 7, 40, ct);
        if (ev.Count == 0) return;

        log.Report(TestLine.Head("Падения программ за последние 7 сут."));
        log.Report(TestLine.Dim("Само по себе это не поломка железа, но повальные вылеты разных программ —"));
        log.Report(TestLine.Dim("характерный признак неисправной памяти."));

        var byApp = ev.GroupBy(e => e.Get("Data0") is { Length: > 0 } n ? n : "неизвестно")
                      .OrderByDescending(g => g.Count());

        foreach (var g in byApp.Take(6))
            log.Report(TestLine.Info(Fmt.Row($"   {g.Key}", $"{g.Count()} раз", 34)));

        if (byApp.Count() >= 5)
            log.Report(TestLine.Warn("   Падают сразу многие разные программы — стоит проверить память."));

        log.Report(TestLine.Empty);
    }

    // ── Свой чёрный ящик ──────────────────────────────────────────────────

    private static void ReportBlackBox(IProgress<TestLine> log, List<CrashSuspect> suspects)
    {
        log.Report(TestLine.Head("Журнал состояния NetAudit (чёрный ящик)"));

        var sessions = BlackBoxReader.ScanSessions();
        if (sessions.Count == 0)
        {
            log.Report(TestLine.Warn("Записей пока нет."));
            log.Report(TestLine.Dim("Журнал ведётся, пока NetAudit запущен, и пишется на диск сразу, чтобы"));
            log.Report(TestLine.Dim("пережить синий экран. Он покажет температуру и нагрузку в последние"));
            log.Report(TestLine.Dim("секунды перед сбоем — того, чего в журналах Windows нет."));
            log.Report(TestLine.Empty);
            return;
        }

        var crashed = sessions.Where(s => !s.ClosedCleanly && s.SampleCount > 0).ToList();

        log.Report(TestLine.Info(Fmt.Row("Записанных сеансов", $"{sessions.Count}")));
        log.Report(TestLine.Info(Fmt.Row("Оборванных (без штатного выхода)", $"{crashed.Count}")));

        if (crashed.Count == 0)
        {
            log.Report(TestLine.Good("   Все сеансы завершались штатно."));
            log.Report(TestLine.Empty);
            return;
        }

        // Момент загрузки текущего сеанса Windows. По нему видно главное:
        // перезагружалась ли машина после обрыва. Если нет — умерло только
        // приложение, а компьютер продолжал работать, и это совсем другой диагноз
        var bootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        foreach (var s in crashed.Take(3))
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Bad($"Сеанс оборвался: {s.Started:dd.MM HH:mm} → {s.LastRecord:HH:mm:ss}, длился {HumanTime(s.Duration)}"));
            log.Report(TestLine.Dim($"   Файл: {s.FileName}"));

            if (s.LastRecord > bootTime)
            {
                log.Report(TestLine.Warn("   Windows после этого не перезагружалась — значит завершился только"));
                log.Report(TestLine.Warn("   сам NetAudit, а компьютер продолжал работать. Смотрите crash.log."));
            }
            else
            {
                log.Report(TestLine.Bad("   После обрыва компьютер перезагружался — похоже, умерла вся машина,"));
                log.Report(TestLine.Bad("   а не одно приложение."));
            }

            // Ноль в журнале означает «датчик не читался» (нет прав администратора
            // или заблокирован драйвер чтения), а не «процессор был ледяным». Печатать
            // «максимум 0 °C» — вводить в заблуждение ровно в том отчёте, где ищут перегрев
            if (s.MaxCpuTempC > 0 && !double.IsNaN(s.MaxCpuTempC))
                log.Report(TestLine.Info(Fmt.Row("   Максимум темп. CPU", $"{s.MaxCpuTempC:F0} °C")));
            else
                log.Report(TestLine.Dim(Fmt.Row("   Максимум темп. CPU", "не записывалась")));

            if (s.MaxGpuTempC > 0 && !double.IsNaN(s.MaxGpuTempC))
                log.Report(TestLine.Info(Fmt.Row("   Максимум темп. GPU", $"{s.MaxGpuTempC:F0} °C")));
            else
                log.Report(TestLine.Dim(Fmt.Row("   Максимум темп. GPU", "не записывалась")));

            foreach (var mark in s.TailMarks.TakeLast(3))
                log.Report(TestLine.Dim($"   Пометка: {mark}"));

            if (s.Tail.Count > 0)
            {
                log.Report(TestLine.Dim("   Последние секунды перед обрывом:"));
                log.Report(TestLine.Dim("   время      CPU%   CPU°C   GPU%   GPU°C   ОЗУ ГБ   FPS   режим"));
                foreach (var t in s.Tail)
                {
                    log.Report(TestLine.Info(
                        $"   {t.TimeOfDay:hh\\:mm\\:ss}  {V(t.CpuPercent, 0, 5)}  {V(t.CpuTempC, 0, 6)}  " +
                        $"{V(t.GpuPercent, 0, 5)}  {V(t.GpuTempC, 0, 6)}  {V(t.RamUsedGb, 1, 7)}  {V(t.Fps, 0, 4)}   {t.Mode}"));
                }
            }

            // Перегрев в последних секундах — это уже не догадка, а замер
            double lastCpu = s.Tail.Select(t => t.CpuTempC).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
            double lastGpu = s.Tail.Select(t => t.GpuTempC).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();

            if (lastCpu >= 90)
            {
                log.Report(TestLine.Bad($"   Перед обрывом процессор был {lastCpu:F0} °C — это перегрев."));
                suspects.Add(CrashSuspect.Power);
            }
            if (lastGpu >= 90)
            {
                log.Report(TestLine.Bad($"   Перед обрывом видеокарта была {lastGpu:F0} °C — это перегрев."));
                suspects.Add(CrashSuspect.Gpu);
            }
        }

        log.Report(TestLine.Empty);
    }

    private static string V(double v, int digits, int width) =>
        (double.IsNaN(v) ? "—" : v.ToString("F" + digits)).PadLeft(width);

    // ── Настройки дампов ──────────────────────────────────────────────────

    private static void ReportDumpSettings(IProgress<TestLine> log)
    {
        log.Report(TestLine.Head("Готовность к записи следующего сбоя"));

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
            if (key is null)
            {
                log.Report(TestLine.Warn("Не удалось прочитать настройки записи дампов"));
                log.Report(TestLine.Empty);
                return;
            }

            int mode = ToInt(key.GetValue("CrashDumpEnabled"));
            int autoReboot = ToInt(key.GetValue("AutoReboot"));

            string modeText = mode switch
            {
                0 => "выключена",
                1 => "полный дамп памяти",
                2 => "дамп ядра",
                3 => "малый дамп (минидамп)",
                7 => "автоматический дамп",
                _ => $"режим {mode}",
            };

            var level = mode == 0 ? TestLevel.Bad : TestLevel.Good;
            log.Report(new TestLine(Fmt.Row("Запись дампа при сбое", modeText), level));

            if (mode == 0)
            {
                log.Report(TestLine.Bad("   Запись выключена — следующий синий экран не оставит файла для разбора."));
                log.Report(TestLine.Dim("   Включается так: Win+R → sysdm.cpl → Дополнительно → Загрузка и"));
                log.Report(TestLine.Dim("   восстановление → Параметры → «Запись отладочной информации» →"));
                log.Report(TestLine.Dim("   «Малый дамп памяти». Перезагрузка не требуется."));
            }

            log.Report(TestLine.Info(Fmt.Row("Автоперезагрузка после сбоя", autoReboot == 1 ? "включена" : "выключена")));
            if (autoReboot == 1)
            {
                log.Report(TestLine.Dim("   Windows перезагружается сразу и код с синего экрана прочитать не успеваешь."));
                log.Report(TestLine.Dim("   Отчёт выше показывает этот код из журнала — специально ради этого случая."));
            }
        }
        catch (Exception ex)
        {
            log.Report(TestLine.Warn($"Не удалось прочитать настройки дампов: {ex.Message}"));
        }

        log.Report(TestLine.Empty);
    }

    private static int ToInt(object? v)
    {
        try { return v is null ? -1 : Convert.ToInt32(v); } catch { return -1; }
    }

    // ── Вердикт ───────────────────────────────────────────────────────────

    private void Verdict(IProgress<TestLine> log, List<CrashSuspect> suspects, int hardShutdowns)
    {
        log.Report(TestLine.Head("Итог"));

        if (suspects.Count == 0 && hardShutdowns == 0)
        {
            log.Report(TestLine.Good("За период следов сбоев не найдено."));
            log.Report(TestLine.Dim("Если машина всё-таки падала, а записей нет — журнал мог быть очищен или"));
            log.Report(TestLine.Dim("система переустановлена. Оставьте NetAudit работать: его собственный"));
            log.Report(TestLine.Dim("журнал состояния фиксирует то, что Windows записать не успевает."));
            return;
        }

        var ranked = suspects.GroupBy(s => s)
                             .OrderByDescending(g => g.Count())
                             .ToList();

        log.Report(TestLine.Info("Порядок проверки, от самого вероятного:"));
        log.Report(TestLine.Empty);

        int n = 1;
        foreach (var g in ranked)
        {
            log.Report(new TestLine($"{n}. {Advice(g.Key)}", n == 1 ? TestLevel.Bad : TestLevel.Warn));
            n++;
        }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Проверять по одному изменению за раз, иначе не понять, что именно помогло."));
    }

    private static string Advice(CrashSuspect s) => s switch
    {
        CrashSuspect.Memory =>
            "Оперативная память. Прогнать стресс-тест памяти в NetAudit, затем встроенную проверку Windows " +
            "(Win+R → mdsched.exe) или MemTest86 с флешки. Выключить XMP/DOCP и проверить на штатной частоте.",
        CrashSuspect.Cpu =>
            "Процессор и его питание. Сбросить разгон, проверить температуры под нагрузкой, " +
            "убедиться, что блок питания тянет систему.",
        CrashSuspect.Disk =>
            "Накопитель. Проверить SMART, переставить кабель SATA в другой порт, прогнать chkdsk.",
        CrashSuspect.Gpu =>
            "Видеокарта. Переустановить драйвер начисто (DDU), проверить температуры, сбросить разгон, " +
            "проверить подключение дополнительного питания к карте.",
        CrashSuspect.Driver =>
            "Драйвер. Разобрать минидамп (BlueScreenView) — он назовёт файл драйвера, с которого всё началось.",
        CrashSuspect.Power =>
            "Питание и охлаждение. Почистить от пыли, проверить термопасту и обороты вентиляторов, " +
            "оценить запас блока питания.",
        CrashSuspect.Software =>
            "Система или программа. Проверить целостность файлов Windows: sfc /scannow и DISM.",
        _ =>
            "Однозначного подозреваемого нет — начните со стресс-теста и наблюдения за температурами.",
    };

    private static string HumanTime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} сут. {t.Hours} ч"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} мин"
        : $"{(int)t.TotalSeconds} с";

    private static string Shorten(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "текст события недоступен";
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 90 ? s : s[..90] + "…";
    }
}
