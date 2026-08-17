using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Скорость канала и — что для игр важнее — насколько растёт задержка, когда канал занят.
///
/// Отдельный «тест скорости» показывает мегабиты, но лагает игра не от их нехватки.
/// Лагает она от bufferbloat: роутер или оборудование провайдера набивает очередь
/// пакетами закачки, и игровой трафик стоит в этой очереди. Поэтому здесь задержка
/// меряется одновременно с прокачкой канала, а результат сравнивается с задержкой в покое.
/// </summary>
public sealed class SpeedTest(bool doUpload = true) : IDiagnosticTest
{
    public string Title => "Скорость канала и задержка под нагрузкой";

    // Эндпоинты Cloudflare: те же, что использует их собственный speed.cloudflare.com
    private const string DownUrl = "https://speed.cloudflare.com/__down?bytes=";
    private const string UpUrl   = "https://speed.cloudflare.com/__up";

    private const int    Streams      = 4;              // одним потоком широкий канал не насытить
    private const int    DownChunk    = 25_000_000;     // 25 МБ на запрос
    private const int    UpChunk      = 5_000_000;      // отдача обычно уже, чем приём
    private static readonly TimeSpan Phase      = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SlowStart  = TimeSpan.FromSeconds(2);   // окно разгона TCP не считаем

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // По умолчанию .NET держит 2 соединения на хост — этого мало для 4 потоков
            MaxConnectionsPerServer = 16,
            AutomaticDecompression  = DecompressionMethods.None,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Тест скорости и bufferbloat"));
        log.Report(TestLine.Dim("Сервер: speed.cloudflare.com · 4 потока · по 10 секунд на фазу"));
        log.Report(TestLine.Dim("Во время теста канал занят полностью — это нормально"));
        log.Report(TestLine.Empty);

        // ── Задержка в покое ────────────────────────────────────────────────
        var anchor = await PingUtil.PickAnchorAsync(ct).ConfigureAwait(false);
        if (anchor is null)
        {
            log.Report(TestLine.Warn("Ни один публичный адрес не отвечает на ICMP."));
            log.Report(TestLine.Warn("Задержку под нагрузкой измерить не выйдет, останется только скорость."));
        }
        else
        {
            log.Report(TestLine.Dim($"Замер задержки по {anchor.Address} (TTL {anchor.Ttl})"));
            if (anchor.LooksIntercepted)
                log.Report(TestLine.Warn(
                    $"⚠ {anchor.Address} отвечает за {anchor.RttMs:F0} мс при TTL {anchor.Ttl} — " +
                    "адрес перехвачен внутри локальной сети, это не настоящий интернет-хост"));
        }

        double idleRtt = double.NaN;
        if (anchor is not null)
        {
            log.Report(TestLine.Dim("Измеряю задержку в покое (5 секунд)…"));
            var idle = await PingUtil.SampleRttAsync(anchor.Address, 20, 250, ct).ConfigureAwait(false);
            idleRtt = PingUtil.Median(idle);
            log.Report(TestLine.Info(Fmt.Row("Задержка в покое", $"{Fmt.Ms(idleRtt)}  (медиана из {idle.Count})")));
        }

        log.Report(TestLine.Empty);

        // ── Загрузка ────────────────────────────────────────────────────────
        var (downMbit, downRtt) = await PhaseAsync(false, anchor, log, ct).ConfigureAwait(false);
        log.Report(TestLine.Info(Fmt.Row("Приём (download)", Fmt.Mbit(downMbit))));

        double upMbit = double.NaN, upRtt = double.NaN;
        if (doUpload)
        {
            log.Report(TestLine.Empty);
            (upMbit, upRtt) = await PhaseAsync(true, anchor, log, ct).ConfigureAwait(false);
            log.Report(TestLine.Info(Fmt.Row("Отдача (upload)", Fmt.Mbit(upMbit))));
        }

        // ── Итог ────────────────────────────────────────────────────────────
        log.Report(TestLine.Empty);
        log.Report(TestLine.Head("Итог"));
        log.Report(TestLine.Info(Fmt.Row("Приём", Fmt.Mbit(downMbit))));
        if (doUpload) log.Report(TestLine.Info(Fmt.Row("Отдача", Fmt.Mbit(upMbit))));

        if (double.IsNaN(idleRtt))
        {
            log.Report(TestLine.Warn("Оценка bufferbloat невозможна: не с чем сравнивать задержку."));
            return;
        }

        log.Report(TestLine.Info(Fmt.Row("Задержка в покое", Fmt.Ms(idleRtt))));
        ReportDelta(log, "Задержка при приёме", downRtt, idleRtt);
        if (doUpload) ReportDelta(log, "Задержка при отдаче", upRtt, idleRtt);

        double worst = Math.Max(Delta(downRtt, idleRtt), doUpload ? Delta(upRtt, idleRtt) : 0);
        if (double.IsNaN(worst)) return;

        var (grade, level, comment) = Grade(worst);
        log.Report(TestLine.Empty);
        log.Report(new TestLine(Fmt.Row("Оценка bufferbloat", $"{grade}   (+{worst:F0} мс под нагрузкой)"), level));
        log.Report(new TestLine(comment, level));

        if (worst >= 60)
        {
            log.Report(TestLine.Empty);
            log.Report(TestLine.Dim("Что с этим делать: включить в роутере управление очередью (SQM / fq_codel / Smart Queue)"));
            log.Report(TestLine.Dim("или ограничить скорость в настройках роутера до ~90% от измеренной. Лаги в играх во время"));
            log.Report(TestLine.Dim("закачек берутся именно отсюда, а не из нехватки скорости."));
        }
    }

    private static double Delta(double loaded, double idle) =>
        double.IsNaN(loaded) || double.IsNaN(idle) ? double.NaN : Math.Max(0, loaded - idle);

    private static void ReportDelta(IProgress<TestLine> log, string label, double loaded, double idle)
    {
        if (double.IsNaN(loaded))
        {
            log.Report(TestLine.Warn(Fmt.Row(label, "нет данных")));
            return;
        }
        double d = Delta(loaded, idle);
        var level = d < 30 ? TestLevel.Good : d < 100 ? TestLevel.Warn : TestLevel.Bad;
        log.Report(new TestLine(Fmt.Row(label, $"{Fmt.Ms(loaded)}   (+{d:F0} мс)"), level));
    }

    /// <summary>Шкала в духе теста Waveform: важна не абсолютная задержка, а её прирост.</summary>
    private static (string grade, TestLevel level, string comment) Grade(double delta) => delta switch
    {
        <   5 => ("A+", TestLevel.Good, "Канал не копит очередь. Закачки не мешают играм и звонкам."),
        <  30 => ("A",  TestLevel.Good, "Очередь почти не растёт. Для игр это хороший результат."),
        <  60 => ("B",  TestLevel.Warn, "Заметный прирост задержки. В играх при активной закачке будет подлагивать."),
        < 200 => ("C",  TestLevel.Warn, "Канал сильно копит очередь. Играть во время закачки будет тяжело."),
        < 400 => ("D",  TestLevel.Bad,  "Очередь огромная. Любая фоновая загрузка выбивает из игры."),
        _     => ("F",  TestLevel.Bad,  "Канал захлёбывается. Онлайн-игры во время закачки невозможны."),
    };

    /// <summary>Одна фаза: качаем или отдаём в несколько потоков, параллельно меряя задержку.</summary>
    private async Task<(double mbit, double rtt)> PhaseAsync(
        bool upload, Anchor? anchor, IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Dim(upload ? "Отдача, 10 секунд…" : "Приём, 10 секунд…"));

        long bytes = 0;
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var rttSamples = new List<double>();
        Task? pinger = anchor is null
            ? null
            : PingUtil.PingLoopAsync(anchor.Address, rttSamples, 200, phaseCts.Token);

        var workers = new Task[Streams];
        for (int i = 0; i < Streams; i++)
            workers[i] = upload
                ? UploadWorkerAsync(n => Interlocked.Add(ref bytes, n), phaseCts.Token)
                : DownloadWorkerAsync(n => Interlocked.Add(ref bytes, n), phaseCts.Token);

        // Разгон TCP не считаем: первые секунды всегда медленнее установившейся скорости
        try { await Task.Delay(SlowStart, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }

        long startBytes = Interlocked.Read(ref bytes);
        var sw = Stopwatch.StartNew();
        lock (rttSamples) rttSamples.Clear();   // задержку тоже меряем на установившемся режиме

        try { await Task.Delay(Phase, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }

        sw.Stop();
        long moved = Interlocked.Read(ref bytes) - startBytes;

        await phaseCts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
        if (pinger is not null) { try { await pinger.ConfigureAwait(false); } catch { } }

        double mbit = sw.Elapsed.TotalSeconds > 0
            ? moved * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0
            : 0;

        double rtt;
        lock (rttSamples) rtt = PingUtil.Median(rttSamples);

        log.Report(TestLine.Dim($"   передано {Fmt.Bytes(moved)} за {sw.Elapsed.TotalSeconds:F1} с"));
        return (mbit, rtt);
    }

    private static async Task DownloadWorkerAsync(Action<long> report, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await Http.GetAsync(DownUrl + DownChunk,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int n;
                while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    report(n);
            }
            catch (OperationCanceledException) { return; }
            catch { await SafeDelay(ct).ConfigureAwait(false); }
        }
    }

    private static async Task UploadWorkerAsync(Action<long> report, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var content = new ProgressContent(UpChunk, report);
                using var resp = await Http.PostAsync(UpUrl, content, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch { await SafeDelay(ct).ConfigureAwait(false); }
        }
    }

    /// <summary>
    /// Тело POST-запроса известной длины, которое отчитывается по мере отправки.
    ///
    /// ByteArrayContent тоже сгодился бы, но тогда счётчик прирастал бы только по
    /// завершении всего запроса: на узком канале за десять секунд это два-три
    /// отсчёта, и замер получается грубым. Здесь шаг — 64 КБ.
    ///
    /// Длину объявляем заранее (TryComputeLength возвращает true): при chunked-передаче
    /// сервер может повести себя иначе, и цифра перестанет отражать канал.
    /// </summary>
    private sealed class ProgressContent(long length, Action<long> report) : HttpContent
    {
        private const int Block = 64 * 1024;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            // Случайные байты, а не нули: нули мог бы сжать какой-нибудь узел по пути
            var buf = new byte[Block];
            Random.Shared.NextBytes(buf);

            long left = length;
            while (left > 0)
            {
                int n = (int)Math.Min(Block, left);
                await stream.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                left -= n;
                report(n);
            }
        }

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }

    private static async Task SafeDelay(CancellationToken ct)
    {
        try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { }
    }
}
