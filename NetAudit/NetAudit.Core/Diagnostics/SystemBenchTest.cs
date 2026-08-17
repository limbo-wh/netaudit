using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NetAudit.Core.Diagnostics;

/// <summary>Что именно гонять в тесте железа.</summary>
[Flags]
public enum BenchParts
{
    Cpu    = 1,
    Memory = 2,
    Disk   = 4,
    All    = Cpu | Memory | Disk,
}

/// <summary>
/// Замер процессора, памяти и накопителя.
///
/// Абсолютные «попугаи» тут не главное — сравнивать их не с чем. Главное — две вещи,
/// которые видно только под нагрузкой:
///   1. Троттлинг. Если через десять секунд полной загрузки производительность падает,
///      значит система перегревается или упирается в лимит питания. В играх это выглядит
///      как просадки FPS через несколько минут после начала матча.
///   2. Масштабирование по ядрам. Слабый прирост от многопоточности при исправном
///      охлаждении обычно означает, что частота сбрасывается сразу.
/// </summary>
public sealed class SystemBenchTest(BenchParts parts = BenchParts.All) : IDiagnosticTest
{
    public string Title => "Тест процессора, памяти и диска";

    private static readonly TimeSpan CpuPhase   = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SoakPhase  = TimeSpan.FromSeconds(14);
    private const long DiskFileSize = 256L * 1024 * 1024;
    private const int  DiskBlock    = 1024 * 1024;

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Тест железа"));
        log.Report(TestLine.Dim("Во время теста система нагружена полностью — закройте игры и тяжёлые программы"));
        log.Report(TestLine.Empty);

        if (parts.HasFlag(BenchParts.Cpu))    await CpuAsync(log, ct).ConfigureAwait(false);
        if (parts.HasFlag(BenchParts.Memory)) await MemoryAsync(log, ct).ConfigureAwait(false);
        if (parts.HasFlag(BenchParts.Disk))   await DiskAsync(log, ct).ConfigureAwait(false);

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Цифры имеют смысл в сравнении с самими собой: прогоните тест сейчас и"));
        log.Report(TestLine.Dim("после чистки от пыли или смены термопасты — разница будет видна сразу."));
    }

    // ── Процессор ─────────────────────────────────────────────────────────

    private static async Task CpuAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        int cores = Environment.ProcessorCount;
        log.Report(TestLine.Head($"Процессор ({cores} логических ядер)"));

        log.Report(TestLine.Dim("Прогрев…"));
        await Task.Run(() => Burn(TimeSpan.FromSeconds(1), ct), ct).ConfigureAwait(false);

        log.Report(TestLine.Dim($"Одно ядро, {CpuPhase.TotalSeconds:F0} с…"));
        long single = await Task.Run(() => Burn(CpuPhase, ct), ct).ConfigureAwait(false);
        double singleOps = single / CpuPhase.TotalSeconds;

        log.Report(TestLine.Dim($"Все ядра, {CpuPhase.TotalSeconds:F0} с…"));
        long multi = await BurnAllAsync(CpuPhase, cores, ct).ConfigureAwait(false);
        double multiOps = multi / CpuPhase.TotalSeconds;

        log.Report(TestLine.Info(Fmt.Row("Одно ядро", $"{singleOps / 1e6,8:F1} млн оп/с")));
        log.Report(TestLine.Info(Fmt.Row("Все ядра",  $"{multiOps / 1e6,8:F1} млн оп/с")));

        double scale = singleOps > 0 ? multiOps / singleOps : 0;
        double scalePct = cores > 0 ? scale / cores * 100 : 0;
        var scaleLevel = scalePct >= 70 ? TestLevel.Good : scalePct >= 45 ? TestLevel.Warn : TestLevel.Bad;
        log.Report(new TestLine(
            Fmt.Row("Прирост от многопоточности", $"×{scale:F1} из ×{cores} ({scalePct:F0}%)"), scaleLevel));

        if (scalePct < 45)
            log.Report(TestLine.Dim("   Низкий прирост обычно значит сброс частоты под нагрузкой всех ядер."));
        else if (scalePct < 70)
            log.Report(TestLine.Dim("   Похоже на гиперпоточность: логические ядра дают не полный прирост, это нормально."));

        // ── Троттлинг ───────────────────────────────────────────────────────
        log.Report(TestLine.Dim($"Длительная нагрузка, {SoakPhase.TotalSeconds:F0} с — проверка троттлинга…"));
        var (firstOps, lastOps) = await SoakAsync(SoakPhase, cores, ct).ConfigureAwait(false);

        if (firstOps <= 0)
        {
            log.Report(TestLine.Warn("Не удалось измерить троттлинг"));
        }
        else
        {
            double drop = (1 - lastOps / firstOps) * 100;
            var level = drop < 5 ? TestLevel.Good : drop < 15 ? TestLevel.Warn : TestLevel.Bad;
            log.Report(new TestLine(
                Fmt.Row("Падение за время нагрузки", $"{drop:F1}%   (старт {firstOps / 1e6:F0} → конец {lastOps / 1e6:F0} млн оп/с)"),
                level));

            if (drop >= 15)
                log.Report(TestLine.Bad("   Заметный троттлинг: система перегревается или упирается в лимит питания."));
            else if (drop >= 5)
                log.Report(TestLine.Warn("   Небольшое снижение. Для ноутбука это норма, для настольного — повод посмотреть охлаждение."));
            else
                log.Report(TestLine.Good("   Частота держится, охлаждение справляется."));
        }

        log.Report(TestLine.Empty);
    }

    /// <summary>
    /// Считаем операции за отведённое время, а не время за фиксированное число операций:
    /// так же меряется и троттлинг, и обе половины теста сравнимы между собой.
    /// </summary>
    private static long Burn(TimeSpan duration, CancellationToken ct)
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

        // Результат обязан утечь наружу, иначе цикл имеет право исчезнуть при оптимизации
        if (h == 0) done++;
        return done;
    }

    private static async Task<long> BurnAllAsync(TimeSpan duration, int threads, CancellationToken ct)
    {
        var tasks = new Task<long>[threads];
        for (int i = 0; i < threads; i++)
            tasks[i] = Task.Run(() => Burn(duration, ct), ct);
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Sum();
    }

    /// <summary>Длительная нагрузка: возвращает производительность в начале и в конце.</summary>
    private static async Task<(double first, double last)> SoakAsync(
        TimeSpan total, int threads, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(3);
        long counter = 0;

        var workers = new Task[threads];
        using var soakCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

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
                if (h == 0) Interlocked.Increment(ref counter);
            }, soakCts.Token);

        long a0 = Interlocked.Read(ref counter);
        var sw = Stopwatch.StartNew();
        await Task.Delay(window, ct).ConfigureAwait(false);
        long a1 = Interlocked.Read(ref counter);
        double first = (a1 - a0) / sw.Elapsed.TotalSeconds;

        // Середина: греемся
        await Task.Delay(total - window - window, ct).ConfigureAwait(false);

        long b0 = Interlocked.Read(ref counter);
        sw.Restart();
        await Task.Delay(window, ct).ConfigureAwait(false);
        long b1 = Interlocked.Read(ref counter);
        double last = (b1 - b0) / sw.Elapsed.TotalSeconds;

        await soakCts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }

        return (first, last);
    }

    // ── Память ────────────────────────────────────────────────────────────

    private static async Task MemoryAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Оперативная память"));

        try
        {
            var (copyGbs, latencyNs) = await Task.Run(() => MeasureMemory(ct), ct).ConfigureAwait(false);

            log.Report(TestLine.Info(Fmt.Row("Скорость копирования", $"{copyGbs,8:F1} ГБ/с")));
            log.Report(TestLine.Info(Fmt.Row("Задержка случайного доступа", $"{latencyNs,8:F1} нс")));

            if (latencyNs > 130)
                log.Report(TestLine.Warn("   Задержка высокая. Проверьте, включён ли профиль XMP/EXPO в BIOS."));
            else if (latencyNs > 90)
                log.Report(TestLine.Info("   Задержка средняя — типично для памяти на штатной частоте."));
            else
                log.Report(TestLine.Good("   Задержка низкая, память настроена хорошо."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Тест памяти не выполнился: {ex.Message}"));
        }

        log.Report(TestLine.Empty);
    }

    private static (double copyGbs, double latencyNs) MeasureMemory(CancellationToken ct)
    {
        // ── Пропускная способность ──────────────────────────────────────────
        const int Size = 96 * 1024 * 1024;      // заметно больше любого L3, чтобы мерить именно ОЗУ
        var src = new byte[Size];
        var dst = new byte[Size];
        Random.Shared.NextBytes(src.AsSpan(0, 1024 * 1024));

        Buffer.BlockCopy(src, 0, dst, 0, Size);  // прогрев и раскладка страниц

        var sw = Stopwatch.StartNew();
        int rounds = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(2) && !ct.IsCancellationRequested)
        {
            Buffer.BlockCopy(src, 0, dst, 0, Size);
            rounds++;
        }
        sw.Stop();
        double copyGbs = rounds * (double)Size / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024);

        // ── Задержка случайного доступа ─────────────────────────────────────
        // Обход по цепочке: следующий индекс известен только после чтения текущего,
        // поэтому процессор не может подгрузить данные заранее и мы видим настоящую задержку
        const int Cells = 8 * 1024 * 1024;       // 32 МБ индексов
        var chain = new int[Cells];
        var order = new int[Cells];
        for (int i = 0; i < Cells; i++) order[i] = i;

        var rng = Random.Shared;
        for (int i = Cells - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int i = 0; i < Cells - 1; i++) chain[order[i]] = order[i + 1];
        chain[order[Cells - 1]] = order[0];

        int steps = 4_000_000;
        int p = 0;
        for (int i = 0; i < 100_000; i++) p = chain[p];   // прогрев

        sw.Restart();
        for (int i = 0; i < steps; i++) p = chain[p];
        sw.Stop();

        if (p == int.MinValue) steps++;   // не дать выбросить цикл
        double latencyNs = sw.Elapsed.TotalMilliseconds * 1e6 / steps;

        return (copyGbs, latencyNs);
    }

    // ── Диск ──────────────────────────────────────────────────────────────

    private static async Task DiskAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        string dir  = Path.Combine(Path.GetTempPath(), "NetAudit");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"bench-{Guid.NewGuid():N}.tmp");

        log.Report(TestLine.Head("Накопитель"));
        log.Report(TestLine.Dim($"Временный файл {Fmt.Bytes(DiskFileSize)} в {dir}"));

        try
        {
            double writeMbs = await Task.Run(() => WriteTest(file, ct), ct).ConfigureAwait(false);
            log.Report(TestLine.Info(Fmt.Row("Последовательная запись", $"{writeMbs,8:F0} МБ/с")));

            double readMbs = await Task.Run(() => ReadTest(file, ct), ct).ConfigureAwait(false);
            log.Report(TestLine.Info(Fmt.Row("Последовательное чтение", $"{readMbs,8:F0} МБ/с")));

            double best = Math.Max(writeMbs, readMbs);
            if (best > 1500)
                log.Report(TestLine.Good("   Похоже на NVMe SSD."));
            else if (best > 300)
                log.Report(TestLine.Good("   Похоже на SATA SSD."));
            else if (best > 80)
                log.Report(TestLine.Warn("   Скорость на уровне жёсткого диска. Игры с него грузятся заметно дольше."));
            else
                log.Report(TestLine.Bad("   Очень медленно. Проверьте состояние накопителя и свободное место."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Тест диска не выполнился: {ex.Message}"));
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        log.Report(TestLine.Empty);
    }

    private static double WriteTest(string path, CancellationToken ct)
    {
        var buf = new byte[DiskBlock];
        Random.Shared.NextBytes(buf);

        var sw = Stopwatch.StartNew();
        // WriteThrough: без него замерялась бы скорость записи в кэш Windows, а не на диск
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                       DiskBlock, FileOptions.WriteThrough))
        {
            for (long written = 0; written < DiskFileSize; written += DiskBlock)
            {
                ct.ThrowIfCancellationRequested();
                fs.Write(buf, 0, DiskBlock);
            }
            fs.Flush(flushToDisk: true);
        }
        sw.Stop();

        return DiskFileSize / sw.Elapsed.TotalSeconds / (1024.0 * 1024);
    }

    /// <summary>
    /// Чтение мимо кэша Windows: иначе файл, только что записанный, читался бы из
    /// оперативной памяти и цифра говорила бы о чём угодно, кроме диска.
    /// FILE_FLAG_NO_BUFFERING требует буфер, выровненный по границе сектора, — отсюда
    /// ручное выделение памяти вместо обычного массива.
    /// </summary>
    private static unsafe double ReadTest(string path, CancellationToken ct)
    {
        const int NoBuffering = 0x20000000;   // FILE_FLAG_NO_BUFFERING

        void* raw = NativeMemory.AlignedAlloc((nuint)DiskBlock, 4096);
        try
        {
            var span = new Span<byte>(raw, DiskBlock);

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None,
                                               (FileOptions)NoBuffering | FileOptions.SequentialScan);

            var sw = Stopwatch.StartNew();
            long offset = 0;
            while (offset < DiskFileSize)
            {
                ct.ThrowIfCancellationRequested();
                int n = System.IO.RandomAccess.Read(handle, span, offset);
                if (n <= 0) break;
                offset += n;
            }
            sw.Stop();

            return offset / sw.Elapsed.TotalSeconds / (1024.0 * 1024);
        }
        finally
        {
            NativeMemory.AlignedFree(raw);
        }
    }
}
