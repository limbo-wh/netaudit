using System.Diagnostics;
using System.Runtime.InteropServices;
using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>Итог проверки: сколько проверено, сколько ошибок и на каком шаблоне.</summary>
public sealed class RamIntegrityResult
{
    public long TestedBytes { get; init; }
    public int Passes { get; init; }
    public long Errors { get; init; }
    public IReadOnlyList<string> FirstErrors { get; init; } = [];
    public TimeSpan Elapsed { get; init; }
    public bool Ok => Errors == 0;
}

/// <summary>
/// Проверка оперативной памяти шаблонами — то же, что делает MemTest86, но из-под
/// работающей Windows и своими силами.
///
/// Зачем: сбойная планка не выдаёт себя постоянно. Она даёт редкие одиночные
/// ошибки под нагрузкой, а наружу это выходит как синий экран MEMORY_MANAGEMENT,
/// вылет игры без объяснений или повреждённый архив. Обычный стресс-тест такое
/// ловит плохо: он гоняет вычисления в кэше, а не всю память подряд.
///
/// Честная граница метода, которую надо понимать: из-под работающей системы
/// нельзя проверить всю память. Часть занята ядром Windows, драйверами и самими
/// программами — эти области недоступны никакому пользовательскому процессу.
/// Проверяется столько, сколько удалось занять, и это обычно 60–80% планок.
/// Полную проверку даёт только загрузочный MemTest86 — но он требует перезагрузки
/// и нескольких часов, а этот тест можно запустить прямо сейчас и поймать
/// большинство реальных дефектов.
///
/// Шаблоны подобраны под разные типы дефектов:
///   • нули и единицы — залипшие в одном состоянии ячейки;
///   • шахматка 0x55/0xAA — наводки между соседними ячейками;
///   • бегущая единица — сбои отдельных линий данных;
///   • собственный адрес ячейки — ошибки адресации, когда запись уходит не туда;
///   • псевдослучайный — дефекты, зависящие от сочетания данных.
/// </summary>
public sealed class RamIntegrityTest(int passes = 1, double sharePercent = 60) : IDiagnosticTest
{
    public string Title => "Проверка оперативной памяти";

    public RamIntegrityResult? Result { get; private set; }

    /// <summary>Размер одного блока. Крупнее — меньше накладных расходов, мельче — гибче по объёму.</summary>
    private const long BlockBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Сколько памяти оставить системе. Если занять всё, Windows начнёт выгружать
    /// наши же страницы в файл подкачки — и тест будет проверять диск, а не планки.
    /// </summary>
    private const long ReserveBytes = 1536L * 1024 * 1024;

    /// <summary>Сколько первых ошибок показывать подробно: дальше список бесполезен.</summary>
    private const int MaxReportedErrors = 12;

    /// <summary>Сколько шаблонов прогоняется за один проход — для строки отчёта.</summary>
    private static int PatternCount => Patterns.Length;

    private static readonly (string Name, uint Kind, uint Pattern)[] Patterns =
    [
        ("нули",              0, 0x0000_0000),
        ("единицы",           0, 0xFFFF_FFFF),
        ("шахматка 0x55",     0, 0x5555_5555),
        ("шахматка 0xAA",     0, 0xAAAA_AAAA),
        ("бегущая единица",   1, 0),
        ("собственный адрес", 2, 0),
        ("псевдослучайный",   3, 0x9E37_79B9),
    ];

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Проверка оперативной памяти"));

        long available = RamInfoProbe.AvailablePhysicalBytes();
        long budget = (long)(available * sharePercent / 100.0);
        budget = Math.Min(budget, available - ReserveBytes);
        budget -= budget % BlockBytes;

        if (budget < BlockBytes)
        {
            log.Report(TestLine.Bad($"Свободно всего {Fmt.Bytes(available)} — проверять нечего."));
            log.Report(TestLine.Dim("Закройте тяжёлые программы и запустите тест снова."));
            log.Report(TestLine.Empty);
            return;
        }

        int blocks = (int)(budget / BlockBytes);

        log.Report(TestLine.Dim($"Свободно {Fmt.Bytes(available)}, беру под проверку {Fmt.Bytes(budget)} "
                              + $"({blocks} блоков по {Fmt.Bytes(BlockBytes)})."));
        log.Report(TestLine.Dim($"Проходов: {passes}. Шаблонов в проходе: {Patterns.Length}."));

        // Оценка по опыту прогонов: запись и сверка вместе идут около 6 ГБ/с. Цифра
        // грубая, но без неё владелец машины с 64 ГБ памяти не догадается, что нажал
        // на кнопку, которая займёт полчаса
        double minutes = budget * 2.0 * Patterns.Length * passes / (6.0 * 1024 * 1024 * 1024) / 60;
        log.Report(TestLine.Dim(minutes >= 1
            ? $"Займёт примерно {minutes:F0} мин."
            : $"Займёт примерно {minutes * 60:F0} с."));

        log.Report(TestLine.Dim("Пока идёт проверка, компьютер будет ощутимо задумчивым — это нормально."));
        log.Report(TestLine.Empty);

        try
        {
            var result = await Task.Run(() => Run(blocks, log, ct), ct).ConfigureAwait(false);
            Result = result;
            Report(log, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (OutOfMemoryException)
        {
            log.Report(TestLine.Bad("Не хватило памяти под проверку — что-то заняло её прямо во время теста."));
        }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Проверка не выполнилась: {ex.Message}"));
        }

        log.Report(TestLine.Empty);
    }

    // ── Отчёт ─────────────────────────────────────────────────────────────

    private static void Report(IProgress<TestLine> log, RamIntegrityResult r)
    {
        log.Report(TestLine.Empty);
        log.Report(TestLine.Info(Fmt.Row("Проверено",
            $"{Fmt.Bytes(r.TestedBytes)} × {r.Passes} "
          + $"{(r.Passes == 1 ? "проход" : "прохода")} × {PatternCount} шаблонов")));

        log.Report(TestLine.Info(Fmt.Row("Времени заняло", r.Elapsed.TotalMinutes >= 1
            ? $"{r.Elapsed.TotalMinutes:F1} мин"
            : $"{r.Elapsed.TotalSeconds:F0} с")));

        if (r.Ok)
        {
            log.Report(TestLine.Good(Fmt.Row("Ошибок", "нет")));
            log.Report(TestLine.Dim("Проверенная часть памяти исправна. Напомню: из-под работающей"));
            log.Report(TestLine.Dim("Windows проверяется не вся память — область ядра и драйверов"));
            log.Report(TestLine.Dim("недоступна. При подозрении на сбойную планку прогоните MemTest86"));
            log.Report(TestLine.Dim("с загрузочной флешки: он проверяет всё и в несколько проходов."));
            return;
        }

        log.Report(TestLine.Bad(Fmt.Row("Ошибок", r.Errors.ToString())));
        foreach (var line in r.FirstErrors)
            log.Report(TestLine.Bad("   " + line));

        log.Report(TestLine.Empty);
        log.Report(TestLine.Bad("Память возвращает не то, что в неё записали. Это не программная"));
        log.Report(TestLine.Bad("ошибка: тест сверяет байты, которые сам же и записал секунду назад."));
        log.Report(TestLine.Empty);
        log.Report(TestLine.Warn("Что делать по порядку:"));
        log.Report(TestLine.Warn("   1. Выключить разгон памяти (профиль XMP/EXPO) и проверить снова —"));
        log.Report(TestLine.Warn("      чаще всего виноват не дефект планки, а слишком смелый профиль."));
        log.Report(TestLine.Warn("   2. Если планок несколько — проверять по одной, так находится виновная."));
        log.Report(TestLine.Warn("   3. Переставить планку в другой слот: бывает виноват слот, а не память."));
        log.Report(TestLine.Warn("   4. Подтвердить приговор загрузочным MemTest86 перед заменой."));
    }

    // ── Проверка ──────────────────────────────────────────────────────────

    private RamIntegrityResult Run(int blocks, IProgress<TestLine> log, CancellationToken ct)
    {
        var addresses = new nint[blocks];
        var errors = new List<string>();
        long errorCount = 0;
        var sw = Stopwatch.StartNew();

        try
        {
            // Занимаем и сразу касаемся каждой страницы: до первого касания за
            // выделенным адресом ещё нет физической памяти
            for (int i = 0; i < blocks; i++)
            {
                ct.ThrowIfCancellationRequested();
                addresses[i] = Alloc(BlockBytes);
                Fill(addresses[i], BlockBytes, 0);
            }

            for (int pass = 1; pass <= passes; pass++)
            {
                foreach (var (name, kind, pattern) in Patterns)
                {
                    ct.ThrowIfCancellationRequested();
                    log.Report(TestLine.Dim($"Проход {pass}/{passes}, шаблон «{name}»…"));

                    // Сначала записываем весь объём и только потом сверяем: между
                    // записью и чтением данные успевают покинуть кэш процессора,
                    // и сверяется содержимое планок, а не кэша
                    Parallel.For(0, blocks, new ParallelOptions { CancellationToken = ct },
                                 i => WriteBlock(addresses[i], BlockBytes, kind, pattern, (uint)i));

                    long found = 0;
                    var lockObj = new Lock();

                    Parallel.For(0, blocks, new ParallelOptions { CancellationToken = ct }, i =>
                    {
                        var (count, details) = VerifyBlock(addresses[i], BlockBytes, kind, pattern, (uint)i);
                        if (count == 0) return;

                        lock (lockObj)
                        {
                            found += count;
                            foreach (var e in details)
                            {
                                if (errors.Count >= MaxReportedErrors) break;
                                errors.Add($"шаблон «{name}», блок {i}, {e}");
                            }
                        }
                    });

                    if (found > 0)
                    {
                        errorCount += found;
                        log.Report(TestLine.Bad($"   несовпадений: {found}"));
                    }
                }
            }
        }
        finally
        {
            foreach (var p in addresses)
                if (p != 0) Free(p);
        }

        sw.Stop();

        return new RamIntegrityResult
        {
            TestedBytes = (long)blocks * BlockBytes,
            Passes      = passes,
            Errors      = errorCount,
            FirstErrors = errors,
            Elapsed     = sw.Elapsed,
        };
    }

    /// <summary>
    /// Ожидаемое значение ячейки. Вынесено в одно место: запись и сверка обязаны
    /// считать его одинаково, иначе тест будет находить собственные ошибки.
    /// </summary>
    private static uint Expected(uint kind, uint pattern, uint index, uint block) => kind switch
    {
        0 => pattern,
        1 => 1u << (int)(index & 31),                        // бегущая единица
        2 => index ^ (block << 24),                          // собственный адрес ячейки
        _ => Scramble(index ^ (block * 2654435761u) ^ pattern),
    };

    /// <summary>Быстрый разброс битов: соседние индексы дают совсем непохожие значения.</summary>
    private static uint Scramble(uint x)
    {
        x ^= x >> 16;
        x *= 2246822519u;
        x ^= x >> 13;
        x *= 3266489917u;
        x ^= x >> 16;
        return x;
    }

    private static unsafe void WriteBlock(nint address, long bytes, uint kind, uint pattern, uint block)
    {
        uint* p = (uint*)address;
        long count = bytes / sizeof(uint);

        for (long i = 0; i < count; i++)
            p[i] = Expected(kind, pattern, (uint)i, block);
    }

    /// <summary>
    /// Сверяет блок и возвращает число несовпадений плюс подробности по первым из них:
    /// на сбойной планке ошибок бывают миллионы, и складывать их все в список незачем.
    /// </summary>
    private static unsafe (long Count, List<string> Details) VerifyBlock(
        nint address, long bytes, uint kind, uint pattern, uint block)
    {
        var details = new List<string>();
        long errors = 0;

        uint* p = (uint*)address;
        long count = bytes / sizeof(uint);

        for (long i = 0; i < count; i++)
        {
            uint expected = Expected(kind, pattern, (uint)i, block);
            uint actual = p[i];
            if (actual == expected) continue;

            errors++;
            if (details.Count >= MaxReportedErrors) continue;

            // Разница по битам говорит о характере дефекта: один бит — сбой ячейки
            // или наводка, много бит подряд — линия данных или адресация
            uint diff = actual ^ expected;
            details.Add($"смещение 0x{i * 4:X8}: ожидалось 0x{expected:X8}, прочитано 0x{actual:X8} "
                      + $"(различаются биты 0x{diff:X8})");
        }

        return (errors, details);
    }

    private static unsafe nint Alloc(long bytes) => (nint)NativeMemory.AlignedAlloc((nuint)bytes, 64);

    private static unsafe void Free(nint p) => NativeMemory.AlignedFree((void*)p);

    private static unsafe void Fill(nint p, long bytes, byte value) =>
        NativeMemory.Fill((void*)p, (nuint)bytes, value);
}
