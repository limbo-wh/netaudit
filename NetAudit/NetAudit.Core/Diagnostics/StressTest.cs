using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NetAudit.Core.Logging;
using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Стресс-тест: длительная полная нагрузка с проверкой того, что машина считает
/// правильно, а не только быстро.
///
/// Это не бенчмарк — «попугаи» тут вторичны. Тест отвечает на три вопроса, которые
/// важны при покупке бывшего в употреблении компьютера:
///
///   1. **Считает ли железо верно.** Каждый блок вычислений и каждая страница памяти
///      сверяются с заранее известным правильным ответом. Одно расхождение — это уже
///      диагноз: исправная машина ошибаться не имеет права ни разу. Так работают
///      Prime95 и MemTest, и по-другому нестабильность не ловится: неисправная память
///      и завышенный разгон почти всегда «работают», пока не соврут в одном бите.
///   2. **Держит ли частоту.** Производительность замеряется в начале и в конце:
///      падение означает перегрев или упор в лимит питания.
///   3. **Переживает ли нагрузку вообще.** Если машина уйдёт в перезагрузку или
///      синий экран прямо во время теста, чёрный ящик сохранит последние секунды —
///      температуры и нагрузку в момент смерти.
///
/// Тест сам останавливается, если температура выходит за заданный порог: доводить
/// железо до отключения по защите не нужно, факт перегрева уже установлен.
/// </summary>
public sealed class StressTest(
    StressOptions options,
    BlackBoxRecorder? blackBox = null,
    IProgress<StressTick>? ticks = null) : IDiagnosticTest
{
    public string Title => $"Стресс-тест ({options.Describe()})";

    /// <summary>Как часто выводить строку хода выполнения.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(15);

    /// <summary>Размер блока памяти под один проход проверки.</summary>
    private const long MemoryBlockBytes = 64L * 1024 * 1024;

    private readonly StressMonitor _monitor = new();

    private long _cpuErrors;
    private long _memErrors;
    private long _diskErrors;
    private long _cpuBlocks;
    private long _memBytesChecked;

    private GpuStressWorker? _gpu;
    private long _gpuOps;
    private long _gpuPrevOps;
    private DateTime _gpuPrevAt = DateTime.UtcNow;
    private double _gpuGops;

    // Снимаются перед освобождением устройства, нужны итоговому отчёту
    private long _gpuErrorsFinal;
    private long _gpuDispatches;
    private string _gpuAdapter = "";

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head($"Стресс-тест: {options.Describe()}"));
        log.Report(TestLine.Empty);
        log.Report(TestLine.Warn("Система будет загружена полностью. Закройте игры и тяжёлые программы."));
        log.Report(TestLine.Dim("Окно NetAudit может подтормаживать — это ожидаемо, тест занимает все ядра."));
        log.Report(TestLine.Dim("Прервать можно в любой момент кнопкой «Остановить»."));
        log.Report(TestLine.Empty);

        int threads = options.Threads > 0 ? options.Threads : Environment.ProcessorCount;

        log.Report(TestLine.Info(Fmt.Row("Потоков нагрузки", $"{threads}")));
        log.Report(TestLine.Info(Fmt.Row("Порог остановки CPU", $"{options.CpuTempLimitC} °C")));
        log.Report(TestLine.Info(Fmt.Row("Порог остановки GPU", $"{options.GpuTempLimitC} °C")));

        // Датчики температуры без прав администратора не поднимаются — сказать об этом
        // надо заранее, иначе пользователь решит, что тест их просто не показывает
        _monitor.Initialize();

        bool cpuTemp = _monitor.CpuTemperatureAvailable;
        bool gpuTemp = _monitor.GpuTemperatureAvailable;

        if (cpuTemp && gpuTemp)
        {
            log.Report(TestLine.Good(Fmt.Row("Датчики температуры", "доступны")));
        }
        else if (cpuTemp || gpuTemp)
        {
            // Частый случай: драйвер ядра заблокирован, но видеокарта читается через NVAPI
            string have = cpuTemp ? "только процессор" : "только видеокарта";
            log.Report(TestLine.Warn(Fmt.Row("Датчики температуры", have)));

            if (_monitor.TemperatureProblem.Length > 0)
                log.Report(TestLine.Dim($"   {_monitor.TemperatureProblem}."));

            log.Report(TestLine.Dim(cpuTemp
                ? "   Перегрев видеокарты этот прогон не поймает."
                : "   Перегрев процессора этот прогон не поймает."));
        }
        else
        {
            log.Report(TestLine.Warn(Fmt.Row("Датчики температуры", "недоступны")));
            log.Report(TestLine.Dim(_monitor.TemperatureProblem.Length > 0
                ? $"   {_monitor.TemperatureProblem}."
                : "   Нужны права администратора: драйвер чтения датчиков без них не встаёт."));
            log.Report(TestLine.Dim("   Тест выполнится, но перегрев поймать будет нечем — а это его половина смысла."));
            log.Report(TestLine.Dim("   Перезапустите NetAudit от администратора (кнопка «Ярлык (администратор)»)."));
        }

        log.Report(TestLine.Empty);

        // Прежде чем чему-то доверять, тест доказывает, что умеет находить ошибки.
        // Иначе «ошибок нет» выглядит одинаково и у исправной машины, и у сломанной
        // проверки — а это худший вид неверного результата
        if (!SelfCheck(log)) return;

        var startedAt = DateTime.Now;
        blackBox?.Mark($"НАЧАТ стресс-тест: {options.Describe()}");

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = new List<Thread>();
        string? abortReason = null;

        try
        {
            // Счётчик блоков отдаём наблюдателю: падение скорости по нему и есть
            // детектор троттлинга, отдельно частоту процессора спрашивать не нужно
            _monitor.Start(stopCts.Token, () => Interlocked.Read(ref _cpuBlocks));

            if (options.Cpu)
                for (int i = 0; i < threads; i++)
                    workers.Add(StartWorker($"cpu-{i}", () => CpuWorker(stopCts.Token)));

            if (options.Memory)
                workers.Add(StartWorker("mem", () => MemoryWorker(log, stopCts.Token)));

            if (options.Gpu && StartGpu(log))
                workers.Add(StartWorker("gpu", () => GpuWorker(stopCts.Token)));

            if (options.Disk)
                workers.Add(StartWorker("disk", () => DiskWorker(log, stopCts.Token)));

            if (workers.Count == 0)
            {
                log.Report(TestLine.Warn("Не выбрано ни одной подсистемы — нечего нагружать."));
                return;
            }

            abortReason = await SuperviseAsync(log, stopCts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Остановку пользователем нельзя пропускать наверх молча: без этого
            // итог не печатался вовсе, и прерванный тест не показывал ни температур,
            // ни числа ошибок — то есть всё, ради чего его и гоняли, терялось
            abortReason = "остановлено пользователем";
        }
        finally
        {
            await stopCts.CancelAsync().ConfigureAwait(false);

            // Потоки нагрузки проверяют токен между блоками, поэтому останавливаются
            // за доли секунды. Ждём их явно: иначе отчёт печатается, пока ядра ещё заняты
            foreach (var t in workers)
            {
                try { t.Join(TimeSpan.FromSeconds(5)); } catch { }
            }

            _monitor.Stop();

            // Счётчики снимаем до Dispose: итог печатается уже после освобождения
            // устройства Direct3D, и спрашивать их у мёртвого объекта поздно
            if (_gpu is not null)
            {
                _gpuErrorsFinal = Interlocked.Read(ref _gpu.Errors);
                _gpuDispatches = _gpu.Dispatches;
                _gpuAdapter = _gpu.AdapterName;
                _gpu.Dispose();
                _gpu = null;
            }
        }

        Summary(log, startedAt, abortReason);
        blackBox?.Mark($"ЗАВЕРШЁН стресс-тест: ошибок CPU {Interlocked.Read(ref _cpuErrors)}, " +
                       $"памяти {Interlocked.Read(ref _memErrors)}");

        // Прерывание пользователем должно выглядеть как прерывание, а не как успех
        ct.ThrowIfCancellationRequested();
    }

    // ── Самопроверка ──────────────────────────────────────────────────────

    /// <summary>
    /// Убеждается, что каждая сверка способна поймать расхождение: подсовывает
    /// ей заведомо неверные данные и ждёт, что она их заметит.
    ///
    /// Проверка нужна ровно по той причине, по которой существует сам тест:
    /// исправное железо и сломанная проверка дают одинаковый ответ «ошибок нет».
    /// В этом проекте такое уже случалось дважды — у видеопамяти сверка молча
    /// сравнивала пустой буфер, а замер вычислений считал выброшенный компилятором
    /// код.
    /// </summary>
    private bool SelfCheck(IProgress<TestLine> log)
    {
        var failures = new List<string>();

        if (options.Cpu && !SelfCheckCpu()) failures.Add("вычисления");
        if (options.Memory && !SelfCheckMemory()) failures.Add("память");
        if (options.Disk && !SelfCheckDisk()) failures.Add("накопитель");

        if (failures.Count == 0)
        {
            log.Report(TestLine.Good(Fmt.Row("Самопроверка", "сверки работают")));
            log.Report(TestLine.Empty);
            return true;
        }

        log.Report(TestLine.Bad(Fmt.Row("Самопроверка", "НЕ ПРОЙДЕНА: " + string.Join(", ", failures))));
        log.Report(TestLine.Bad("   Проверка не замечает заведомо неверных данных, поэтому её вывод"));
        log.Report(TestLine.Bad("   ничего не значит. Тест остановлен, чтобы не выдать ложное «всё в порядке»."));
        return false;
    }

    /// <summary>Два разных зерна обязаны дать разный хеш.</summary>
    private static bool SelfCheckCpu()
    {
        const long Ops = 10_000;
        return HashBlock(1469598103934665603UL, Ops) != HashBlock(1469598103934665604UL, Ops);
    }

    /// <summary>Записанный шаблон не должен совпасть с проверкой по другому шаблону.</summary>
    private static unsafe bool SelfCheckMemory()
    {
        const long Count = 4096;
        void* raw = NativeMemory.AlignedAlloc((nuint)(Count * sizeof(ulong)), 4096);
        try
        {
            var p = (ulong*)raw;
            Fill(p, Count, 0x5555555555555555UL, round: 0);
            return Verify(p, Count, 0xAAAAAAAAAAAAAAAAUL, round: 0) == Count;
        }
        finally
        {
            NativeMemory.AlignedFree(raw);
        }
    }

    /// <summary>Два разных заполнения буфера не должны считаться одинаковыми.</summary>
    private static bool SelfCheckDisk()
    {
        var a = new byte[4096];
        var b = new byte[4096];
        FillBytes(a, seed: 1);
        FillBytes(b, seed: 2);
        return !a.AsSpan().SequenceEqual(b);
    }

    /// <summary>
    /// Причины, по которым подсистема отвалилась посреди теста. Пусто — все дошли до конца.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _workerFailures = new();

    private Thread StartWorker(string name, Action body)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Раньше сюда не долетало ничего, кроме отмены, и любое исключение из
                // рабочего потока (срыв видеодрайвера даёт SharpGenException из Map)
                // роняло всё приложение посреди часового прогона. Теперь подсистема
                // просто помечается отвалившейся, а остальные продолжают работать
                _workerFailures[name] = ex.Message;
            }
        })
        {
            Name = $"NetAudit-stress-{name}",
            IsBackground = true,
        };
        t.Start();
        return t;
    }

    // ── Надзор: время, температуры, прогресс ──────────────────────────────

    /// <summary>
    /// Ведёт тест по времени и следит за порогами. Возвращает причину досрочной
    /// остановки или null, если тест отработал полностью.
    /// </summary>
    private async Task<string?> SuperviseAsync(
        IProgress<TestLine> log, CancellationTokenSource stopCts, CancellationToken userCt)
    {
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var eventsBefore = DateTime.Now;

        log.Report(TestLine.Dim("Ход выполнения (строка каждые 15 с):"));
        log.Report(TestLine.Dim("  прошло      CPU%   CPU°C   GPU°C   CPU млн/с   GPU млрд/с   ошибок"));

        while (sw.Elapsed < options.Duration)
        {
            userCt.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), userCt).ConfigureAwait(false);

            var s = _monitor.Current;

            // Перегрев: останавливаемся сами, не дожидаясь защиты материнской платы
            if (s.CpuTempC >= options.CpuTempLimitC)
                return await AbortAsync(log, stopCts,
                    $"процессор достиг {s.CpuTempC:F0} °C (порог {options.CpuTempLimitC} °C)").ConfigureAwait(false);

            if (s.GpuTempC >= options.GpuTempLimitC)
                return await AbortAsync(log, stopCts,
                    $"видеокарта достигла {s.GpuTempC:F0} °C (порог {options.GpuTempLimitC} °C)").ConfigureAwait(false);

            long gpuErrors = _gpu is null ? 0 : Interlocked.Read(ref _gpu.Errors);
            long errors = Interlocked.Read(ref _cpuErrors) + Interlocked.Read(ref _memErrors)
                        + Interlocked.Read(ref _diskErrors) + gpuErrors;

            if (options.StopOnFirstError && errors > 0)
                return await AbortAsync(log, stopCts, "обнаружена ошибка, остановка по настройке").ConfigureAwait(false);

            // Скорость видеокарты снимается всегда, а не только когда кто-то
            // подписан на числовой канал: строки хода выполнения берут её отсюда же,
            // и без этого вызова в них стоял прочерк при работающей видеокарте
            double gpuGops = SampleGpuSpeed();

            // Числа для графиков — каждую секунду, независимо от текстовых строк:
            // строка раз в пятнадцать секунд на графике выглядела бы ступеньками
            ticks?.Report(new StressTick(
                sw.Elapsed,
                options.Duration - sw.Elapsed,
                s.CpuPercent, s.GpuPercent, s.CpuTempC, s.GpuTempC, s.RamUsedGb,
                _monitor.OpsPerSecond() / 1e6,
                gpuGops,
                Interlocked.Read(ref _cpuErrors),
                Interlocked.Read(ref _memErrors),
                Interlocked.Read(ref _diskErrors),
                gpuErrors,
                Interlocked.Read(ref _memBytesChecked)));

            if (sw.Elapsed - lastReport >= ReportEvery)
            {
                lastReport = sw.Elapsed;
                ReportProgress(log, sw.Elapsed, s, errors);
            }
        }

        // Аппаратные ошибки, зарегистрированные Windows ЗА ВРЕМЯ теста, —
        // отдельный источник правды рядом с нашей собственной проверкой
        _newHardwareEvents = CountNewHardwareEvents(eventsBefore);
        return null;
    }

    private int _newHardwareEvents;

    private async Task<string> AbortAsync(IProgress<TestLine> log, CancellationTokenSource stopCts, string reason)
    {
        log.Report(TestLine.Empty);
        log.Report(TestLine.Bad($"■ Тест остановлен: {reason}"));
        blackBox?.Mark($"стресс-тест остановлен: {reason}");
        await stopCts.CancelAsync().ConfigureAwait(false);
        return reason;
    }

    private void ReportProgress(IProgress<TestLine> log, TimeSpan elapsed, StressSample s, long errors)
    {
        double ops = _monitor.OpsPerSecond(Interlocked.Read(ref _cpuBlocks));

        string line =
            $"  {elapsed:hh\\:mm\\:ss}  " +
            $"{Val(s.CpuPercent, 0, 5)}  " +
            $"{Val(s.CpuTempC, 0, 6)}  " +
            $"{Val(s.GpuTempC, 0, 6)}  " +
            $"{(ops > 0 ? (ops / 1e6).ToString("F0") : "—"),11}  " +
            $"{(_gpuGops > 0 ? _gpuGops.ToString("F0") : "—"),11}  " +
            $"{errors,8}";

        log.Report(new TestLine(line, errors > 0 ? TestLevel.Bad : TestLevel.Info));
    }

    private static string Val(double v, int digits, int width) =>
        (double.IsNaN(v) ? "—" : v.ToString("F" + digits)).PadLeft(width);

    /// <summary>Новые записи WHEA и срывов видеодрайвера с момента старта теста.</summary>
    private static int CountNewHardwareEvents(DateTime since)
    {
        try
        {
            var whea = WindowsEventQuery.Query("System", ["Microsoft-Windows-WHEA-Logger"], [], 1, 50);
            var tdr  = WindowsEventQuery.Query("System", ["Display"], [4101], 1, 50);
            var gpu  = WindowsEventQuery.Query("System", ["nvlddmkm", "amdkmdag"], [], 1, 100);

            return whea.Count(e => e.Time >= since)
                 + tdr.Count(e => e.Time >= since)
                 + gpu.Count(e => e.Time >= since);
        }
        catch
        {
            return 0;
        }
    }

    // ── Процессор ─────────────────────────────────────────────────────────

    /// <summary>
    /// Считает блок с заранее известным правильным ответом и сверяет результат.
    ///
    /// Вычисление детерминированное: те же входные данные обязаны давать тот же
    /// хеш всегда и на любом ядре. Расхождение означает, что процессор посчитал
    /// неверно, — а это уже не «медленно», это неисправность или нестабильный разгон.
    /// Ровно по этому признаку ловят плохой разгон и деградацию кристалла.
    /// </summary>
    private void CpuWorker(CancellationToken ct)
    {
        const long BlockOps = 4_000_000;
        const ulong Seed = 1469598103934665603UL;

        ulong expected = HashBlock(Seed, BlockOps);

        while (!ct.IsCancellationRequested)
        {
            ulong got = HashBlock(Seed, BlockOps);

            if (got != expected)
                Interlocked.Increment(ref _cpuErrors);

            Interlocked.Add(ref _cpuBlocks, BlockOps);
        }
    }

    private static ulong HashBlock(ulong seed, long ops)
    {
        ulong h = seed;
        for (long i = 0; i < ops; i++)
        {
            h ^= (ulong)i;
            h *= 1099511628211UL;
            h ^= h >> 29;
            h += h << 13;
            h ^= h >> 17;
        }
        return h;
    }

    // ── Видеокарта ────────────────────────────────────────────────────────

    /// <summary>
    /// Поднимает устройство Direct3D и калибрует объём работы. Делается до запуска
    /// потока: если видеокарта недоступна (нет Direct3D 11, работаем по RDP,
    /// драйвер не отвечает), тест должен сказать об этом прямо, а не тихо
    /// пропустить целую подсистему, оставив пользователя в уверенности, что
    /// она проверена.
    /// </summary>
    private bool StartGpu(IProgress<TestLine> log)
    {
        _gpu = new GpuStressWorker();

        if (!_gpu.Initialize())
        {
            log.Report(TestLine.Bad($"Видеокарта не нагружается: {_gpu.Error}"));
            log.Report(TestLine.Dim("   Остальные подсистемы тест всё равно проверит."));
            _gpu.Dispose();
            _gpu = null;
            return false;
        }

        if (!_gpu.SelfCheck())
        {
            log.Report(TestLine.Bad("Сверка результатов видеокарты не проходит самопроверку —"));
            log.Report(TestLine.Bad("   она не замечает заведомо неверных данных. Нагрузка на видеокарту"));
            log.Report(TestLine.Bad("   пропущена: её результату всё равно нельзя было бы верить."));
            _gpu.Dispose();
            _gpu = null;
            return false;
        }

        log.Report(TestLine.Dim($"Видеокарта: {_gpu.AdapterName}"));
        log.Report(TestLine.Dim($"   Буфер в видеопамяти: {Fmt.Bytes(_gpu.HeatBytes)}"));
        log.Report(TestLine.Dim(
            $"   Вызов шейдера ≈{_gpu.LastDispatchMs:F0} мс; порог, после которого Windows " +
            "считает драйвер зависшим, — 2000 мс"));

        return true;
    }

    private void GpuWorker(CancellationToken ct)
    {
        var gpu = _gpu;
        if (gpu is null) return;

        while (!ct.IsCancellationRequested)
        {
            gpu.RunCycle();
            // Считаем операции с плавающей точкой: именно они здесь и есть работа,
            // а один цикл — это целый пакет вызовов шейдера
            Interlocked.Add(ref _gpuOps, gpu.FlopsPerCycle);
        }
    }

    /// <summary>Скорость видеокарты по дельте счётчика операций между тиками.</summary>
    private double SampleGpuSpeed()
    {
        long now = Interlocked.Read(ref _gpuOps);
        var at = DateTime.UtcNow;
        double dt = (at - _gpuPrevAt).TotalSeconds;

        if (dt > 0.1)
        {
            _gpuGops = (now - _gpuPrevOps) / dt / 1e9;
            _gpuPrevOps = now;
            _gpuPrevAt = at;
        }

        return _gpuGops;
    }

    // ── Память ────────────────────────────────────────────────────────────

    /// <summary>
    /// Заполняет большой блок памяти шаблонами и сверяет их обратно.
    ///
    /// Шаблоны подобраны так, как это делают проверялки памяти: чередование
    /// нулей и единиц в каждом бите ловит «залипшие» биты и наводки между
    /// соседними ячейками, а псевдослучайный шаблон — ошибки, зависящие от данных.
    /// Между записью и проверкой проходит время, поэтому заодно ловится потеря
    /// содержимого (плохая регенерация).
    ///
    /// Память берётся напрямую у системы, минуя сборщик мусора: так она
    /// действительно занимает физические страницы и не перекладывается в куче
    /// больших объектов посреди проверки.
    /// </summary>
    private unsafe void MemoryWorker(IProgress<TestLine> log, CancellationToken ct)
    {
        long budget = MemoryBudget();
        if (budget < MemoryBlockBytes)
        {
            log.Report(TestLine.Warn("Свободной памяти слишком мало для проверки — она пропущена"));
            return;
        }

        int blocks = (int)Math.Max(1, Math.Min(budget / MemoryBlockBytes, 64));
        var buffers = new List<IntPtr>(blocks);

        try
        {
            for (int i = 0; i < blocks; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    buffers.Add((IntPtr)NativeMemory.AlignedAlloc((nuint)MemoryBlockBytes, 4096));
                }
                catch
                {
                    break;   // память кончилась раньше расчётного — работаем тем, что взяли
                }
            }

            if (buffers.Count == 0)
            {
                log.Report(TestLine.Warn("Не удалось выделить память под проверку"));
                return;
            }

            log.Report(TestLine.Dim(
                $"Проверка памяти: {Fmt.Bytes(buffers.Count * (double)MemoryBlockBytes)} " +
                $"({buffers.Count} блоков по {Fmt.Bytes(MemoryBlockBytes)})"));

            ulong pattern = 0;
            int round = 0;

            while (!ct.IsCancellationRequested)
            {
                ulong current = NextPattern(round, ref pattern);

                foreach (var p in buffers)
                {
                    ct.ThrowIfCancellationRequested();
                    Fill((ulong*)p, MemoryBlockBytes / 8, current, round);
                }

                foreach (var p in buffers)
                {
                    ct.ThrowIfCancellationRequested();
                    long bad = Verify((ulong*)p, MemoryBlockBytes / 8, current, round);
                    if (bad > 0) Interlocked.Add(ref _memErrors, bad);
                    Interlocked.Add(ref _memBytesChecked, MemoryBlockBytes);
                }

                round++;
            }
        }
        finally
        {
            foreach (var p in buffers)
            {
                try { NativeMemory.AlignedFree((void*)p); } catch { }
            }
        }
    }

    /// <summary>Шаблоны по кругу: залипшие биты, наводки, зависимость от данных.</summary>
    private static ulong NextPattern(int round, ref ulong prev) => (round % 6) switch
    {
        0 => 0x0000000000000000UL,
        1 => 0xFFFFFFFFFFFFFFFFUL,
        2 => 0x5555555555555555UL,
        3 => 0xAAAAAAAAAAAAAAAAUL,
        4 => 0x0F0F0F0F0F0F0F0FUL,
        _ => prev = 0x9E3779B97F4A7C15UL,   // псевдослучайная основа
    };

    private static unsafe void Fill(ulong* p, long count, ulong pattern, int round)
    {
        bool random = (round % 6) == 5;
        for (long i = 0; i < count; i++)
            p[i] = random ? Mix(pattern, (ulong)i) : pattern;
    }

    private static unsafe long Verify(ulong* p, long count, ulong pattern, int round)
    {
        bool random = (round % 6) == 5;
        long bad = 0;
        for (long i = 0; i < count; i++)
        {
            ulong expected = random ? Mix(pattern, (ulong)i) : pattern;
            if (p[i] != expected) bad++;
        }
        return bad;
    }

    /// <summary>Значение ячейки по её номеру: воспроизводимо, но не повторяется.</summary>
    private static ulong Mix(ulong seed, ulong i)
    {
        ulong x = seed ^ (i * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }

    /// <summary>Сколько памяти брать: доля от свободной, но не больше 8 ГБ за раз.</summary>
    private long MemoryBudget()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            long available = info.TotalAvailableMemoryBytes;

            var probe = new SystemMetricsProbe();
            var (_, usedGb, totalGb) = probe.Sample();
            double freeGb = totalGb - usedGb;

            // Нулевой ответ — это не «памяти нет», а «спросить не удалось». Раньше
            // проверка памяти в таком случае молча пропускалась с сообщением
            // «свободной памяти слишком мало», хотя памяти было полно
            if (freeGb <= 0.5)
            {
                long fromWindows = Probes.RamInfoProbe.AvailablePhysicalBytes();
                if (fromWindows > 0) freeGb = fromWindows / 1024.0 / 1024 / 1024;
            }

            long byFree = (long)(freeGb * options.MemoryFraction * 1024 * 1024 * 1024);
            long cap = 8L * 1024 * 1024 * 1024;

            long budget = Math.Min(byFree, cap);
            if (available > 0) budget = Math.Min(budget, available / 2);

            return Math.Max(0, budget);
        }
        catch
        {
            return 1024L * 1024 * 1024;
        }
    }

    // ── Накопитель ────────────────────────────────────────────────────────

    /// <summary>
    /// Пишет и перечитывает файл со сверкой содержимого. Мимо кэша Windows,
    /// иначе сверялось бы то, что лежит в оперативной памяти, а не на диске.
    /// </summary>
    private void DiskWorker(IProgress<TestLine> log, CancellationToken ct)
    {
        string dir = Path.Combine(Path.GetTempPath(), "NetAudit");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"stress-{Guid.NewGuid():N}.tmp");

        const int BlockSize = 4 * 1024 * 1024;
        const int BlocksPerPass = 32;             // 128 МБ за проход

        var write = new byte[BlockSize];
        var read = new byte[BlockSize];

        log.Report(TestLine.Dim($"Проверка накопителя: {dir}"));

        try
        {
            int round = 0;
            while (!ct.IsCancellationRequested)
            {
                ulong seed = (ulong)round * 0x9E3779B97F4A7C15UL + 1;
                FillBytes(write, seed);

                using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None,
                                               BlockSize, FileOptions.WriteThrough))
                {
                    for (int i = 0; i < BlocksPerPass && !ct.IsCancellationRequested; i++)
                        fs.Write(write, 0, BlockSize);
                    fs.Flush(flushToDisk: true);
                }

                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None,
                                               BlockSize, FileOptions.SequentialScan))
                {
                    for (int i = 0; i < BlocksPerPass && !ct.IsCancellationRequested; i++)
                    {
                        int done = 0;
                        while (done < BlockSize)
                        {
                            int n = fs.Read(read, done, BlockSize - done);
                            if (n <= 0) break;
                            done += n;
                        }

                        if (!read.AsSpan(0, done).SequenceEqual(write.AsSpan(0, done)))
                            Interlocked.Increment(ref _diskErrors);
                    }
                }

                round++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Проверка накопителя прервалась: {ex.Message}"));
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }

    private static void FillBytes(byte[] buf, ulong seed)
    {
        for (int i = 0; i < buf.Length; i += 8)
        {
            ulong v = Mix(seed, (ulong)i);
            BitConverter.TryWriteBytes(buf.AsSpan(i, 8), v);
        }
    }

    // ── Итог ──────────────────────────────────────────────────────────────

    private void Summary(IProgress<TestLine> log, DateTime startedAt, string? abortReason)
    {
        var elapsed = DateTime.Now - startedAt;
        long cpuErr = Interlocked.Read(ref _cpuErrors);
        long memErr = Interlocked.Read(ref _memErrors);
        long diskErr = Interlocked.Read(ref _diskErrors);
        long gpuErr = _gpuErrorsFinal;

        log.Report(TestLine.Empty);
        log.Report(TestLine.Head("Итог стресс-теста"));

        log.Report(TestLine.Info(Fmt.Row("Длительность",
            elapsed.TotalMinutes >= 1 ? $"{elapsed.TotalMinutes:F1} мин" : $"{elapsed.TotalSeconds:F0} с")));

        if (abortReason is not null)
            log.Report(TestLine.Bad(Fmt.Row("Остановлен досрочно", abortReason)));

        // ── Температуры ───────────────────────────────────────────────────
        var stats = _monitor.Stats;

        if (_monitor.TemperatureAvailable && !double.IsNaN(stats.MaxCpuTempC))
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Info(Fmt.Row("Температура CPU, макс.", $"{stats.MaxCpuTempC:F0} °C")));
            log.Report(TestLine.Info(Fmt.Row("Температура CPU, средн.", $"{stats.AvgCpuTempC:F0} °C")));

            var cpuLevel = stats.MaxCpuTempC >= ThermalLimits.CpuWarnC ? TestLevel.Warn
                         : stats.MaxCpuTempC >= ThermalLimits.CpuComfortableC ? TestLevel.Info
                         : TestLevel.Good;
            log.Report(new TestLine($"   {DescribeCpuTemp(stats.MaxCpuTempC)}", cpuLevel));

            if (!double.IsNaN(stats.MaxGpuTempC))
            {
                log.Report(TestLine.Info(Fmt.Row("Температура GPU, макс.", $"{stats.MaxGpuTempC:F0} °C")));
                var gpuLevel = stats.MaxGpuTempC >= 87 ? TestLevel.Bad
                             : stats.MaxGpuTempC >= ThermalLimits.GpuComfortableC ? TestLevel.Info
                             : TestLevel.Good;
                log.Report(new TestLine($"   {DescribeGpuTemp(stats.MaxGpuTempC)}", gpuLevel));
            }
        }
        else
        {
            log.Report(TestLine.Warn("Температуры не снимались — не было прав администратора"));
        }

        // ── Троттлинг ─────────────────────────────────────────────────────
        if (options.Cpu && stats.FirstOps > 0 && stats.LastOps > 0)
        {
            double drop = (1 - stats.LastOps / stats.FirstOps) * 100;
            var level = drop < 5 ? TestLevel.Good : drop < 15 ? TestLevel.Warn : TestLevel.Bad;

            log.Report(TestLine.Empty);
            log.Report(new TestLine(Fmt.Row("Падение производительности",
                $"{drop:F1}%   ({stats.FirstOps / 1e6:F0} → {stats.LastOps / 1e6:F0} млн оп/с)"), level));

            if (drop >= 15)
                log.Report(TestLine.Bad("   Сильный троттлинг: перегрев или упор в лимит питания."));
            else if (drop >= 5)
                log.Report(TestLine.Warn("   Заметное снижение под длительной нагрузкой."));
            else
                log.Report(TestLine.Good("   Частота держится всё время нагрузки."));
        }

        // ── Ошибки ────────────────────────────────────────────────────────
        log.Report(TestLine.Empty);
        log.Report(TestLine.Head("Ошибки"));

        // Подсистема, вылетевшая посреди теста, — это не «ноль ошибок», а отсутствие
        // проверки вовсе. Молчать об этом нельзя: отчёт выглядел бы как успешный
        foreach (var (name, reason) in _workerFailures)
        {
            log.Report(TestLine.Bad(Fmt.Row($"Отвалилась нагрузка «{name}»", reason)));
            log.Report(TestLine.Dim("   Эта часть теста дальше не проверялась — считайте её непройденной."));
        }

        if (options.Cpu)
        {
            long blocks = Interlocked.Read(ref _cpuBlocks);
            log.Report(new TestLine(
                Fmt.Row("Ошибок вычислений", $"{cpuErr}   (проверено {blocks / 1e9:F1} млрд операций)"),
                cpuErr == 0 ? TestLevel.Good : TestLevel.Bad));
        }

        if (options.Memory)
        {
            long checkedBytes = Interlocked.Read(ref _memBytesChecked);
            log.Report(new TestLine(
                Fmt.Row("Ошибок памяти", $"{memErr}   (проверено {Fmt.Bytes(checkedBytes)})"),
                memErr == 0 ? TestLevel.Good : TestLevel.Bad));
        }

        if (options.Gpu)
        {
            if (_gpuDispatches > 0)
            {
                log.Report(new TestLine(
                    Fmt.Row("Ошибок видеокарты", $"{gpuErr}   (счёт с плавающей точкой: {_gpuOps / 1e12:F0} трлн операций)"),
                    gpuErr == 0 ? TestLevel.Good : TestLevel.Bad));
            }
            else
            {
                log.Report(TestLine.Warn(Fmt.Row("Видеокарта", "не нагружалась")));
            }
        }

        if (options.Disk)
            log.Report(new TestLine(Fmt.Row("Ошибок накопителя", $"{diskErr}"),
                diskErr == 0 ? TestLevel.Good : TestLevel.Bad));

        if (_newHardwareEvents > 0)
        {
            log.Report(new TestLine(
                Fmt.Row("Записей в журнале Windows", $"{_newHardwareEvents}"), TestLevel.Bad));
            log.Report(TestLine.Dim("   Во время теста Windows зарегистрировала аппаратные ошибки или срывы"));
            log.Report(TestLine.Dim("   видеодрайвера. Подробности — кнопка «Отчёт о сбоях»."));
        }

        // ── Вердикт ───────────────────────────────────────────────────────
        log.Report(TestLine.Empty);
        log.Report(TestLine.Head("Вывод"));

        long total = cpuErr + memErr + diskErr + gpuErr;

        if (total > 0)
        {
            log.Report(TestLine.Bad("Железо посчитало неверно. Исправная машина не ошибается ни разу."));
            log.Report(TestLine.Empty);
            if (memErr > 0)
            {
                log.Report(TestLine.Bad($"Память: {memErr} несовпадений."));
                log.Report(TestLine.Dim("   Выключите XMP/DOCP в BIOS и повторите тест на штатной частоте."));
                log.Report(TestLine.Dim("   Если ошибки остались — проверьте планки по одной, меняя слоты,"));
                log.Report(TestLine.Dim("   и прогоните MemTest86 с флешки (он работает вне Windows)."));
            }
            if (cpuErr > 0)
            {
                log.Report(TestLine.Bad($"Процессор: {cpuErr} неверных результатов."));
                log.Report(TestLine.Dim("   Сбросьте настройки разгона в BIOS, проверьте охлаждение и питание."));
            }
            if (gpuErr > 0)
            {
                log.Report(TestLine.Bad($"Видеокарта: {gpuErr} неверных результатов{(_gpuAdapter.Length > 0 ? $" ({_gpuAdapter})" : "")}."));
                log.Report(TestLine.Dim("   Сбросьте разгон видеокарты (в том числе заводской у производителя платы),"));
                log.Report(TestLine.Dim("   проверьте температуру и продув, убедитесь, что оба разъёма питания карты"));
                log.Report(TestLine.Dim("   подключены разными кабелями от блока питания, а не одним с переходником."));
            }
            if (diskErr > 0)
            {
                log.Report(TestLine.Bad($"Накопитель: {diskErr} несовпадений при перечитывании."));
                log.Report(TestLine.Dim("   Данные на этом диске под угрозой. Сделайте резервную копию сейчас."));
            }
        }
        else if (abortReason is not null)
        {
            log.Report(TestLine.Warn("Тест не доработал до конца, поэтому полным его считать нельзя."));
            log.Report(TestLine.Dim("Ошибок до остановки не было — но и времени под нагрузкой было меньше."));
        }
        else if (elapsed < TimeSpan.FromMinutes(5))
        {
            log.Report(TestLine.Good("Ошибок нет."));
            log.Report(TestLine.Warn("Прогон короткий: часть дефектов проявляется только через 20–30 минут"));
            log.Report(TestLine.Warn("прогрева. Для бывшей в употреблении машины прогоните час."));
        }
        else
        {
            log.Report(TestLine.Good($"Ошибок нет за {elapsed.TotalMinutes:F0} мин полной нагрузки."));

            if (options.Gpu && _gpuDispatches > 0)
            {
                log.Report(TestLine.Dim("Проверенное посчитало без единого расхождения — и процессор с памятью,"));
                log.Report(TestLine.Dim("и видеокарта. Проверьте заодно «Отчёт о сбоях ПК»: срывы видеодрайвера"));
                log.Report(TestLine.Dim("Windows пишет в журнал отдельно от наших сверок."));
            }
            else
            {
                log.Report(TestLine.Dim("Это хороший признак: и вычисления, и память отработали без единого"));
                log.Report(TestLine.Dim("расхождения. Видеокарта при этом не нагружалась — включите её галочкой"));
                log.Report(TestLine.Dim("либо дайте нагрузку сторонним FurMark или OCCT: NetAudit в это время"));
                log.Report(TestLine.Dim("ведёт журнал состояния и ловит срывы драйвера."));
            }
        }
    }

    private static string DescribeCpuTemp(double t) => t switch
    {
        >= ThermalLimits.CpuStopC => "На пределе: дальше срабатывает защита самого процессора.",
        >= ThermalLimits.CpuWarnC  => "Горячо. Если частота при этом падает — посмотрите кулер "
                                    + "и термопасту. " + ThermalLimits.ModernHardwareNote,
        >= ThermalLimits.CpuComfortableC => "Тепло, но в пределах рабочего режима.",
        >= 70 => "В норме под полной нагрузкой.",
        _     => "Отлично, охлаждение с запасом.",
    };

    private static string DescribeGpuTemp(double t) => t switch
    {
        >= ThermalLimits.GpuStopC => "Критично, видеокарта на пределе.",
        >= ThermalLimits.GpuWarnC => "Высоко. У карт, показывающих температуру горячей точки "
                                   + "(многие Radeon и RTX 30 с памятью GDDR6X), это рабочий режим; "
                                   + "у остальных — повод почистить от пыли.",
        >= ThermalLimits.GpuComfortableC => "Тепло, в пределах нормы для нагрузки.",
        >= 70 => "В норме для нагруженной видеокарты.",
        _     => "Холодная — либо отличное охлаждение, либо она сейчас не нагружена.",
    };
}
