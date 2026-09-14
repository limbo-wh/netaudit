using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NetAudit.Core.Probes;

namespace NetAudit.Core.Diagnostics;

/// <summary>Результаты замеров памяти. Всё, что можно измерить, не открывая корпус.</summary>
public sealed class RamBenchmarkResult
{
    public double ReadGbs { get; init; }
    public double WriteGbs { get; init; }
    public double CopyGbs { get; init; }

    /// <summary>Чтение одним потоком: столько получает программа, не умеющая в многопоточность.</summary>
    public double SingleThreadReadGbs { get; init; }

    /// <summary>Задержка случайного доступа, нс — косвенная замена таймингам.</summary>
    public double LatencyNs { get; init; }

    /// <summary>Задержка по размеру рабочего блока: видно, где кончается кэш и начинается ОЗУ.</summary>
    public IReadOnlyList<(long Bytes, double Ns)> Ladder { get; init; } = [];

    public int Threads { get; init; }
    public long BufferBytes { get; init; }
    public bool NonTemporalWrites { get; init; }
}

/// <summary>
/// Замеры оперативной памяти: пропускная способность и задержка.
///
/// Зачем это нужно: две одинаковые с виду планки DDR4 дают разную скорость в
/// зависимости от того, включён ли профиль XMP, работают ли они в двухканальном
/// режиме и сколько у них рангов. Ни одного из этих признаков не видно в свойствах
/// системы — а разница между удачной и неудачной конфигурацией доходит до полутора
/// раз, и в играх это прямо видно по кадрам (особенно на процессорах AMD Ryzen, где
/// от частоты памяти зависит ещё и скорость внутренней шины между блоками процессора).
///
/// Что меряется и почему именно так:
///   • чтение — суммирование всего буфера векторными инструкциями;
///   • запись — потоковая запись мимо кэша (non-temporal). Обычная запись сначала
///     читает строку кэша, чтобы изменить её часть, и цифра выходит заметно ниже правды;
///   • копирование — честный memcpy средствами среды выполнения;
///   • задержка — обход случайной цепочки, где адрес следующего шага известен
///     только после чтения текущего: процессор не может подгрузить данные заранее.
///
/// Все потоковые замеры многопоточные: одно ядро физически не способно выбрать
/// всю пропускную способность двухканального контроллера, и однопоточная цифра
/// показывала бы возможности ядра, а не памяти. Однопоточное чтение меряется
/// отдельно — оно тоже полезно, но как другая величина.
/// </summary>
public sealed class RamBenchmark : IDiagnosticTest
{
    public string Title => "Скорость оперативной памяти";

    public RamBenchmarkResult? Result { get; private set; }

    /// <summary>Сколько времени крутить каждый потоковый замер.</summary>
    private static readonly TimeSpan Phase = TimeSpan.FromSeconds(1.5);

    /// <summary>Верхняя граница буфера: важно уйти за пределы кэша, а не занять всю память.</summary>
    private const long MaxBuffer = 512L * 1024 * 1024;

    private const long MinBuffer = 32L * 1024 * 1024;

    /// <summary>Область для замера задержки. Заведомо больше любого кэша нынешних процессоров.</summary>
    private const long LatencyArea = 256L * 1024 * 1024;

    /// <summary>Сюда стекают результаты циклов, чтобы оптимизатор не выбросил сами циклы.</summary>
    private static long _sink;

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Скорость памяти"));

        long available = RamInfoProbe.AvailablePhysicalBytes();
        // Два буфера под копирование плюс запас: тест не должен сам вызвать нехватку памяти
        long buffer = Math.Clamp(available / 6, MinBuffer, MaxBuffer);
        buffer -= buffer % (1024 * 1024);

        int threads = Environment.ProcessorCount;

        log.Report(TestLine.Dim($"Буфер {Fmt.Bytes(buffer)} × 2, потоков {threads}. "
                              + "Замер идёт около пятнадцати секунд."));

        try
        {
            var result = await Task.Run(() => Measure(buffer, threads, log, ct), ct).ConfigureAwait(false);
            Result = result;
            Report(log, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Замер не выполнился: {ex.Message}"));
        }

        log.Report(TestLine.Empty);
    }

    // ── Отчёт ─────────────────────────────────────────────────────────────

    private static void Report(IProgress<TestLine> log, RamBenchmarkResult r)
    {
        log.Report(TestLine.Info(Fmt.Row("Чтение",      $"{r.ReadGbs,8:F1} ГБ/с")));
        log.Report(TestLine.Info(Fmt.Row("Запись",      $"{r.WriteGbs,8:F1} ГБ/с"
                                       + (r.NonTemporalWrites ? "" : "   (без потоковой записи)"))));
        log.Report(TestLine.Info(Fmt.Row("Копирование", $"{r.CopyGbs,8:F1} ГБ/с")));
        log.Report(TestLine.Dim("   Копирование всегда примерно вдвое ниже чтения и записи: столько байт"));
        log.Report(TestLine.Dim("   перенесено, а по шине памяти при этом прошло вдвое больше."));
        log.Report(TestLine.Info(Fmt.Row("Чтение одним ядром", $"{r.SingleThreadReadGbs,8:F1} ГБ/с")));
        log.Report(TestLine.Dim("   Одному ядру всю полосу не выбрать — это нормально и так у всех."));
        log.Report(TestLine.Empty);

        var level = r.LatencyNs switch
        {
            < 70  => TestLevel.Good,
            < 90  => TestLevel.Info,
            < 110 => TestLevel.Warn,
            _     => TestLevel.Bad,
        };
        log.Report(new TestLine(Fmt.Row("Задержка доступа", $"{r.LatencyNs,8:F1} нс"), level));

        log.Report(TestLine.Dim(r.LatencyNs switch
        {
            < 70  => "   Отличная задержка: высокая частота и плотные тайминги.",
            < 90  => "   Обычная задержка для настроенной DDR4.",
            < 110 => "   Высоковато. Так выглядит память на штатной частоте, без профиля XMP/EXPO.",
            _     => "   Очень высокая задержка. Профиль XMP/EXPO почти наверняка выключен в BIOS.",
        }));

        if (r.Ladder.Count > 0)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Dim("Задержка по размеру рабочей области — так видно кэши процессора:"));

            foreach (var (bytes, ns) in r.Ladder)
            {
                // Где именно данные нашлись, видно по самой задержке: у кэшей каждого
                // уровня она отличается в разы, и границы уровней узнаваемы по цифрам
                string where = ns switch
                {
                    < 3   => "кэш первого уровня",
                    < 8   => "кэш второго уровня",
                    < 45  => "кэш третьего уровня",
                    _     => "оперативная память",
                };
                log.Report(TestLine.Info($"   {Fmt.Bytes(bytes),10}   {ns,6:F1} нс   {where}"));
            }

            log.Report(TestLine.Dim("   Строка, где задержка резко подскочила, и есть граница: до неё данные"));
            log.Report(TestLine.Dim("   помещались в кэш процессора, после — пошли запросы к самим планкам."));
        }
    }

    // ── Замеры ────────────────────────────────────────────────────────────

    /// <summary>
    /// Буферы адресуются через <see cref="nint"/>, а не через типизированный указатель:
    /// указатель нельзя передать в лямбду и в <c>Parallel.For</c>, а именно так здесь
    /// раздаётся работа потокам.
    /// </summary>
    private static RamBenchmarkResult Measure(
        long bufferBytes, int threads, IProgress<TestLine> log, CancellationToken ct)
    {
        nint a = Alloc(bufferBytes);
        nint b = Alloc(bufferBytes);

        double read, write, copy, single;

        try
        {
            // Прогрев: до первого касания страницы физической памяти за ней нет, и замер
            // поймал бы раздачу страниц операционной системой, а не работу планок
            Fill(a, bufferBytes, 0x5A);
            Fill(b, bufferBytes, 0xA5);

            log.Report(TestLine.Dim("Чтение…"));
            read = Loop(bufferBytes, threads, ct, (off, len) => ReadBlock(a + (nint)off, len));

            log.Report(TestLine.Dim("Запись…"));
            bool nt = Avx.IsSupported;
            write = Loop(bufferBytes, threads, ct, (off, len) => WriteBlock(a + (nint)off, len, nt));

            log.Report(TestLine.Dim("Копирование…"));
            copy = Loop(bufferBytes, threads, ct, (off, len) => Copy(a + (nint)off, b + (nint)off, len));

            log.Report(TestLine.Dim("Чтение одним ядром…"));
            single = Loop(bufferBytes, 1, ct, (off, len) => ReadBlock(a + (nint)off, len));
        }
        finally
        {
            // Освобождаем до замеров задержки: там нужна своя большая область,
            // и держать гигабайт занятым ради уже снятых цифр незачем
            Free(a);
            Free(b);
        }

        log.Report(TestLine.Dim("Задержка случайного доступа…"));
        double latency = Latency(LatencyArea, ct);

        log.Report(TestLine.Dim("Кэши процессора…"));
        var ladder = new List<(long, double)>();
        foreach (long size in new[]
                 {
                     32L * 1024, 256L * 1024, 1L * 1024 * 1024, 4L * 1024 * 1024,
                     16L * 1024 * 1024, 64L * 1024 * 1024, 256L * 1024 * 1024,
                 })
        {
            ct.ThrowIfCancellationRequested();
            ladder.Add((size, Latency(size, ct)));
        }

        return new RamBenchmarkResult
        {
            ReadGbs             = read,
            WriteGbs            = write,
            CopyGbs             = copy,
            SingleThreadReadGbs = single,
            LatencyNs           = latency,
            Ladder              = ladder,
            Threads             = threads,
            BufferBytes         = bufferBytes,
            NonTemporalWrites   = Avx.IsSupported,
        };
    }

    /// <summary>
    /// Гоняет операцию по всему буферу, пока не выйдет время, и переводит объём в ГБ/с.
    /// Буфер делится между потоками поровну: каждый работает со своим куском и не
    /// толкается с соседями за одни и те же строки кэша.
    /// </summary>
    private static double Loop(long bufferBytes, int threads, CancellationToken ct,
                               Action<long, long> operation)
    {
        long chunk = bufferBytes / threads;
        chunk -= chunk % 128;
        if (chunk <= 0) { chunk = bufferBytes - bufferBytes % 128; threads = 1; }

        long passes = 0;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < Phase && !ct.IsCancellationRequested)
        {
            if (threads == 1)
                operation(0, chunk);
            else
                Parallel.For(0, threads, i => operation(i * chunk, chunk));

            passes++;
        }

        sw.Stop();
        ct.ThrowIfCancellationRequested();

        double bytes = (double)passes * chunk * threads;
        return bytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024);
    }

    // ── Элементарные операции над памятью ─────────────────────────────────

    private static unsafe nint Alloc(long bytes) => (nint)NativeMemory.AlignedAlloc((nuint)bytes, 64);

    private static unsafe void Free(nint p) => NativeMemory.AlignedFree((void*)p);

    private static unsafe void Fill(nint p, long bytes, byte value) =>
        NativeMemory.Fill((void*)p, (nuint)bytes, value);

    private static unsafe void Copy(nint src, nint dst, long bytes) =>
        Buffer.MemoryCopy((void*)src, (void*)dst, bytes, bytes);

    /// <summary>Чтение блока векторами. Сумма нужна только чтобы чтение не выбросили как бесполезное.</summary>
    private static unsafe void ReadBlock(nint address, long length)
    {
        byte* p = (byte*)address;
        long i = 0;
        long tail = 0;

        if (Avx2.IsSupported)
        {
            var acc = Vector256<long>.Zero;

            // Четыре независимые загрузки за виток: у ядра несколько портов чтения,
            // одна цепочка зависимостей их не займёт
            for (; i + 128 <= length; i += 128)
            {
                var v0 = Avx.LoadVector256((long*)(p + i));
                var v1 = Avx.LoadVector256((long*)(p + i + 32));
                var v2 = Avx.LoadVector256((long*)(p + i + 64));
                var v3 = Avx.LoadVector256((long*)(p + i + 96));
                acc = Avx2.Add(acc, Avx2.Add(Avx2.Add(v0, v1), Avx2.Add(v2, v3)));
            }

            for (int k = 0; k < Vector256<long>.Count; k++) tail += acc.GetElement(k);
        }

        for (; i + 8 <= length; i += 8) tail += *(long*)(p + i);

        Interlocked.Exchange(ref _sink, tail);
    }

    /// <summary>
    /// Запись блока. Потоковая (non-temporal) запись идёт мимо кэша: обычная сначала
    /// читает строку из памяти, чтобы изменить её часть, и замер показывал бы смесь
    /// чтения с записью вместо чистой скорости записи.
    /// </summary>
    private static unsafe void WriteBlock(nint address, long length, bool nonTemporal)
    {
        byte* p = (byte*)address;
        long i = 0;

        if (nonTemporal)
        {
            var v = Vector256.Create(0x0102030405060708L);
            for (; i + 128 <= length; i += 128)
            {
                Avx.StoreAlignedNonTemporal((long*)(p + i),      v);
                Avx.StoreAlignedNonTemporal((long*)(p + i + 32), v);
                Avx.StoreAlignedNonTemporal((long*)(p + i + 64), v);
                Avx.StoreAlignedNonTemporal((long*)(p + i + 96), v);
            }

            // Потоковые записи оседают в отдельном буфере процессора — без барьера
            // часть из них ещё не дошла до памяти к моменту остановки секундомера
            Sse.StoreFence();
        }

        for (; i + 8 <= length; i += 8) *(long*)(p + i) = 0x0102030405060708L;
    }

    /// <summary>
    /// Задержка случайного доступа: обход перемешанной цепочки индексов. Адрес
    /// следующего шага получается из содержимого текущей ячейки, поэтому механизм
    /// предзагрузки процессора бессилен — и секундомер меряет настоящий путь до планки.
    /// </summary>
    private static unsafe double Latency(long areaBytes, CancellationToken ct)
    {
        int cells = (int)Math.Min(areaBytes / sizeof(int), int.MaxValue / 2);
        if (cells < 1024) cells = 1024;

        nint area = Alloc((long)cells * sizeof(int));
        try
        {
            int* chain = (int*)area;

            // Перемешанный порядок обхода строим в обычном массиве и сразу отпускаем:
            // держать вторую такую же область рядом незачем
            var order = new int[cells];
            for (int i = 0; i < cells; i++) order[i] = i;

            var rng = Random.Shared;
            for (int i = cells - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            for (int i = 0; i < cells - 1; i++) chain[order[i]] = order[i + 1];
            chain[order[cells - 1]] = order[0];

            ct.ThrowIfCancellationRequested();

            int steps = Math.Clamp(cells, 1_000_000, 4_000_000);
            int p = 0;

            for (int i = 0; i < 200_000; i++) p = chain[p];   // прогрев

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < steps; i++) p = chain[p];
            sw.Stop();

            Interlocked.Exchange(ref _sink, p);
            return sw.Elapsed.TotalMilliseconds * 1e6 / steps;
        }
        finally
        {
            Free(area);
        }
    }
}
