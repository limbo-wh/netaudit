using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Сравнение DNS-серверов: свой (от роутера или провайдера) против публичных.
///
/// Запросы шлются напрямую по UDP, а не через Dns.GetHostAddresses — иначе мерился бы
/// кэш Windows, а не сервер. Заодно видно подмену: если на заведомо несуществующее имя
/// приходит IP-адрес, значит провайдер подменяет NXDOMAIN своей страницей-заглушкой,
/// и это ломает всё, что полагается на честный ответ «такого имени нет».
/// </summary>
public sealed class DnsTest : IDiagnosticTest
{
    public string Title => "Тест DNS-серверов";

    private const int Attempts  = 4;
    private const int TimeoutMs = 2000;

    private static readonly string[] Probe =
    [
        "cloudflare.com",
        "google.com",
        "steamcommunity.com",
    ];

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Тест DNS-серверов"));
        log.Report(TestLine.Dim("Запросы идут напрямую по UDP, минуя кэш Windows"));
        log.Report(TestLine.Empty);

        var servers = BuildServerList();

        if (servers.Count == 0)
        {
            log.Report(TestLine.Bad("Не удалось определить ни одного DNS-сервера"));
            return;
        }

        // Прогрев. Без него первый сервер в списке платит за рекурсивное разрешение
        // имён, а всем следующим те же имена достаются уже из его кэша: в первом
        // замере вышло 32 мс против 1,5 мс у того же самого 1.1.1.1, стоявшего ниже.
        // Сравнивать после такого нечего.
        log.Report(TestLine.Dim("Прогрев кэшей серверов…"));
        foreach (var (_, ip, _) in servers)
            foreach (var domain in Probe)
            {
                ct.ThrowIfCancellationRequested();
                await QueryAsync(ip, domain, ct).ConfigureAwait(false);
            }

        log.Report(TestLine.Empty);
        log.Report(TestLine.Info(
            "Сервер".PadRight(24) + "медиана".PadLeft(10) + "лучший".PadLeft(10) +
            "потери".PadLeft(9) + "  проверка"));
        log.Report(TestLine.Dim(new string('─', 72)));

        var results = new List<(string name, double median, bool isMine)>();

        foreach (var (name, ip, isMine) in servers)
        {
            ct.ThrowIfCancellationRequested();

            var times = new List<double>();
            int fails = 0;

            foreach (var domain in Probe)
            {
                for (int a = 0; a < Attempts / Probe.Length + 1; a++)
                {
                    var (ms, _) = await QueryAsync(ip, domain, ct).ConfigureAwait(false);
                    if (double.IsNaN(ms)) fails++;
                    else times.Add(ms);
                }
            }

            int total = times.Count + fails;
            double median = PingUtil.Median(times);
            double best   = times.Count > 0 ? times.Min() : double.NaN;
            double lossPct = total > 0 ? fails * 100.0 / total : 100;

            // Заведомо несуществующее имя: честный сервер обязан ответить «нет такого»
            string hijack = "—";
            var (_, nx) = await QueryAsync(ip, RandomName(), ct).ConfigureAwait(false);
            if (nx is { Count: > 0 }) hijack = "ПОДМЕНА NXDOMAIN";
            else if (nx is not null) hijack = "ок";

            string row = name.PadRight(24)
                       + (double.IsNaN(median) ? "—" : $"{median:F1} мс").PadLeft(10)
                       + (double.IsNaN(best)   ? "—" : $"{best:F1} мс").PadLeft(10)
                       + $"{lossPct:F0}%".PadLeft(9)
                       + "  " + hijack;

            var level = double.IsNaN(median) ? TestLevel.Bad
                      : hijack == "ПОДМЕНА NXDOMAIN" ? TestLevel.Bad
                      : lossPct > 20 ? TestLevel.Bad
                      : median < 30 ? TestLevel.Good
                      : median < 80 ? TestLevel.Info
                      : TestLevel.Warn;

            log.Report(new TestLine(row, level));
            if (!double.IsNaN(median)) results.Add((name, median, isMine));
            else if (isMine)
                log.Report(TestLine.Bad(
                    $"   {ip} прописан в настройках адаптера, но на запросы не отвечает. " +
                    "Windows будет ждать его таймаута перед обращением к следующему серверу."));
        }

        log.Report(TestLine.Empty);

        if (results.Count == 0)
        {
            log.Report(TestLine.Bad("Ни один сервер не ответил. UDP-порт 53 может быть закрыт."));
            return;
        }

        var fastest = results.OrderBy(r => r.median).First();
        log.Report(TestLine.Good($"Быстрее всех: {fastest.name} — {fastest.median:F1} мс"));

        WarnIfIntercepted(log, results);

        // Из своих берём лучший: их может быть несколько, и виноват в медлительности
        // не набор целиком, а конкретный сервер
        var mine = results.Where(r => r.isMine).OrderBy(r => r.median).ToList();
        if (mine.Count == 0)
        {
            log.Report(TestLine.Warn("Ни один из ваших DNS-серверов не ответил."));
            return;
        }

        double bestMine = mine[0].median;
        if (bestMine > fastest.median * 2 && bestMine - fastest.median > 20)
        {
            log.Report(TestLine.Warn(
                $"Ваш DNS медленнее лучшего на {bestMine - fastest.median:F0} мс. " +
                "Смена сервера в настройках адаптера ускорит открытие сайтов."));
            log.Report(TestLine.Dim("На пинг в игре это не влияет: имя сервера разрешается один раз при подключении."));
        }
        else
        {
            log.Report(TestLine.Dim("Ваш DNS работает нормально, менять его смысла нет."));
        }
    }

    /// <summary>
    /// Все публичные серверы отвечают одинаково и слишком быстро — значит отвечают не они.
    ///
    /// Cloudflare, Google, Quad9 и Яндекс стоят в разных дата-центрах и физически не могут
    /// отзываться за одно и то же время меньше пяти миллисекунд: свет за это время проходит
    /// от силы несколько сотен километров. Такая картина означает прозрачное
    /// перенаправление: провайдер или роутер перехватывает весь трафик на порт 53
    /// и отвечает сам, каким бы адрес ни был прописан в настройках.
    /// </summary>
    private static void WarnIfIntercepted(IProgress<TestLine> log,
                                          List<(string name, double median, bool isMine)> results)
    {
        var publics = results.Where(r => !r.isMine || r.name.Contains('·')).ToList();
        if (publics.Count < 3) return;

        double max = publics.Max(r => r.median);
        double min = publics.Min(r => r.median);
        if (max >= 5) return;                  // нормальные интернет-задержки, вопросов нет
        if (max - min > 2) return;             // разброс есть, значит серверы всё-таки разные

        log.Report(TestLine.Empty);
        log.Report(TestLine.Warn(
            $"⚠ Все публичные серверы отвечают за {min:F1}–{max:F1} мс. Так не бывает:"));
        log.Report(TestLine.Warn(
            "   они стоят в разных дата-центрах, и одинаковая задержка меньше 5 мс означает,"));
        log.Report(TestLine.Warn(
            "   что запросы на порт 53 перехватываются и на них отвечает оборудование рядом с вами."));
        log.Report(TestLine.Dim(
            "   Выбор DNS-сервера в настройках адаптера при этом не имеет силы: отвечает всё равно"));
        log.Report(TestLine.Dim(
            "   перехватчик. Обходится шифрованным DNS — DoH или DoT — в настройках Windows или браузера."));
    }

    /// <summary>
    /// Список серверов для проверки. Свои идут первыми и помечаются флагом.
    ///
    /// Если в настройках адаптера прописан один из публичных серверов — а так бывает
    /// часто — он не дублируется отдельной строкой: в первом прогоне 1.1.1.1 выводился
    /// дважды с разными числами, и это выглядело как поломка теста.
    /// </summary>
    private static List<(string name, IPAddress ip, bool isMine)> BuildServerList()
    {
        var known = new (string name, string ip)[]
        {
            ("Cloudflare", "1.1.1.1"),
            ("Google",     "8.8.8.8"),
            ("Quad9",      "9.9.9.9"),
            ("Яндекс",     "77.88.8.8"),
        };

        var list = new List<(string, IPAddress, bool)>();
        var mine = SystemResolvers();

        foreach (var ip in mine)
        {
            string? knownName = known.FirstOrDefault(k => k.ip == ip.ToString()).name;
            list.Add((knownName is null ? $"ваш ({ip})" : $"ваш · {knownName}", ip, true));
        }

        foreach (var (name, ipText) in known)
        {
            var ip = IPAddress.Parse(ipText);
            if (mine.Any(m => m.Equals(ip))) continue;   // уже добавлен как «ваш»
            list.Add((name, ip, false));
        }

        return list;
    }

    /// <summary>DNS-серверы, прописанные на активных адаптерах. Дубликаты и IPv6 отбрасываем.</summary>
    private static List<IPAddress> SystemResolvers()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.GetIPProperties().GatewayAddresses.Count == 0) continue;

                foreach (var dns in ni.GetIPProperties().DnsAddresses)
                {
                    if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (list.Any(x => x.Equals(dns))) continue;
                    list.Add(dns);
                }
            }
        }
        catch { }
        return list;
    }

    private static string RandomName() =>
        $"netaudit-{Guid.NewGuid():N}"[..24] + ".example.com";

    /// <summary>
    /// Один UDP-запрос типа A.
    /// Возвращает задержку и список найденных адресов (null — ответа не было).
    /// </summary>
    private static async Task<(double ms, List<string>? answers)> QueryAsync(
        IPAddress server, string domain, CancellationToken ct)
    {
        try
        {
            ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            byte[] query = BuildQuery(id, domain);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = TimeoutMs;
            udp.Connect(server, 53);

            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs);

            var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            sw.Stop();

            // Чужой ответ — не наш запрос
            if (result.Buffer.Length < 12) return (double.NaN, null);
            if (BinaryPrimitives.ReadUInt16BigEndian(result.Buffer) != id) return (double.NaN, null);

            return (sw.Elapsed.TotalMilliseconds, ParseAnswers(result.Buffer));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (double.NaN, null); }
    }

    private static byte[] BuildQuery(ushort id, string domain)
    {
        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int size = 12 + labels.Sum(l => l.Length + 1) + 1 + 4;
        var buf = new byte[size];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), 0x0100);  // стандартный запрос, рекурсия
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), 1);       // один вопрос

        int p = 12;
        foreach (var label in labels)
        {
            buf[p++] = (byte)label.Length;
            foreach (char c in label) buf[p++] = (byte)c;
        }
        buf[p++] = 0;                                                   // конец имени
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(p), 1); p += 2; // QTYPE = A
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(p), 1);         // QCLASS = IN

        return buf;
    }

    /// <summary>Адреса из секции ответов. Пустой список — имя не найдено (NXDOMAIN или пусто).</summary>
    private static List<string> ParseAnswers(byte[] b)
    {
        var found = new List<string>();
        try
        {
            int qd = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(4));
            int an = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(6));

            int p = 12;
            for (int i = 0; i < qd; i++)
            {
                p = SkipName(b, p);
                p += 4;                       // QTYPE + QCLASS
            }

            for (int i = 0; i < an && p < b.Length; i++)
            {
                p = SkipName(b, p);
                if (p + 10 > b.Length) break;

                int type = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
                int len  = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 8));
                p += 10;

                if (type == 1 && len == 4 && p + 4 <= b.Length)
                    found.Add($"{b[p]}.{b[p + 1]}.{b[p + 2]}.{b[p + 3]}");

                p += len;
            }
        }
        catch { }
        return found;
    }

    /// <summary>Пропустить имя. Сжатое имя (два старших бита 11) занимает ровно два байта.</summary>
    private static int SkipName(byte[] b, int p)
    {
        while (p < b.Length)
        {
            int len = b[p];
            if (len == 0) return p + 1;
            if ((len & 0xC0) == 0xC0) return p + 2;
            p += len + 1;
        }
        return p;
    }
}
