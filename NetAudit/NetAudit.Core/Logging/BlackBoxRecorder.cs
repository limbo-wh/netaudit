using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NetAudit.Core.Models;

namespace NetAudit.Core.Logging;

/// <summary>
/// «Чёрный ящик»: посекундная запись состояния машины на диск, рассчитанная на то,
/// что процесс не получит шанса закрыться.
///
/// Обычный лог с буферизацией при синем экране, зависании или пропаже питания теряет
/// именно тот кусок, ради которого он и вёлся — последние секунды перед смертью
/// остаются в буфере ОЗУ и исчезают вместе с ним. Поэтому здесь:
///
///   • <see cref="FileOptions.WriteThrough"/> плюс <c>Flush(true)</c> после каждой
///     строки — данные уходят мимо кэша Windows прямо на носитель. Цена — одна
///     мелкая синхронная запись в секунду, потеря при крахе не больше одной строки;
///   • маркер штатного закрытия дописывается последней строкой. Его отсутствие в
///     файле — и есть признак аварийного завершения: спрашивать об этом Windows
///     не нужно, файл отвечает сам за себя;
///   • файл открывается с <see cref="FileShare.ReadWrite"/>, чтобы отчёт о сбоях
///     мог читать текущий сеанс, не останавливая запись.
///
/// Формат — CSV с заголовком: его открывает Excel, читает глазами человек и
/// разбирает <see cref="BlackBoxReader"/>. Строки, начинающиеся с «#», — пометки
/// событий (старт стресс-теста, вход в игру), они не мешают разбору чисел.
/// </summary>
public sealed class BlackBoxRecorder : IDisposable
{
    /// <summary>Заголовок CSV. Менять только вместе с <see cref="BlackBoxReader"/>.</summary>
    private const string Header =
        "время;аптайм_с;cpu_%;cpu_°C;gpu_%;gpu_°C;озу_ГБ;озу_всего_ГБ;fps;приём_МБ/с;отдача_МБ/с;пинг_мс;потери_%;режим";

    /// <summary>Последняя строка штатно закрытого файла. Нет её — сеанс оборвался.</summary>
    internal const string CleanMarker = "# сеанс закрыт штатно";

    /// <summary>Сколько файлов прошлых сеансов держим. Старые удаляются при старте.</summary>
    private const int KeepSessions = 20;

    /// <summary>Потолок одного файла. Сутки записи — это около 6 МБ, запас четырёхкратный.</summary>
    private const long MaxFileBytes = 24L * 1024 * 1024;

    private readonly object _gate = new();
    private FileStream? _stream;
    private StreamWriter? _writer;
    private bool _closedCleanly;
    private long _written;

    public string? FilePath { get; private set; }
    public bool IsRecording => _stream is not null;

    /// <summary>Папка с файлами сеансов.</summary>
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetAudit", "blackbox");

    /// <summary>
    /// Открывает новый файл сеанса и пишет шапку с описанием машины — без неё
    /// файл, отправленный кому-то на разбор, не говорит даже о том, какое это железо.
    /// </summary>
    public void Start(string reason = "запуск")
    {
        lock (_gate)
        {
            if (_stream is not null) return;

            System.IO.Directory.CreateDirectory(Directory);
            CleanupOldSessions();

            string path = Path.Combine(Directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            // WriteThrough здесь — главное: без него строки оседают в кэше Windows
            // и при синем экране пропадают ровно те, что важнее всего
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite,
                                     4096, FileOptions.WriteThrough);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = false,
            };

            FilePath = path;
            _closedCleanly = false;
            _written = 0;

            WriteRaw($"# NetAudit — журнал состояния системы");
            WriteRaw($"# начало: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({reason})");
            WriteRaw($"# машина: {Environment.MachineName}, Windows {Environment.OSVersion.Version}, ядер {Environment.ProcessorCount}");
            WriteRaw($"# версия NetAudit: {Environment.ProcessPath}");
            WriteRaw("# строки с «#» — пометки событий, остальные — посекундные замеры");
            WriteRaw(Header);
            Flush();
        }
    }

    /// <summary>
    /// Одна посекундная строка. Пустые значения (нет прав на датчик, нет Wi-Fi)
    /// пишутся пустым полем, а не нулём: ноль градусов и «датчика нет» — разные вещи.
    /// </summary>
    public void Write(SystemSnapshot s, double pingMs, double lossPercent, string mode)
    {
        lock (_gate)
        {
            if (_writer is null) return;

            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(160);
            sb.Append(DateTime.Now.ToString("HH:mm:ss", ci)).Append(';');
            sb.Append(((long)(Environment.TickCount64 / 1000)).ToString(ci)).Append(';');
            sb.Append(Num(s.CpuPercent, 1)).Append(';');
            sb.Append(Num(s.CpuTempC, 1)).Append(';');
            sb.Append(Num(s.GpuPercent, 1)).Append(';');
            sb.Append(Num(s.GpuTempC, 1)).Append(';');
            sb.Append(Num(s.RamUsedGb, 2)).Append(';');
            sb.Append(Num(s.RamTotalGb, 2)).Append(';');
            sb.Append(Num(s.Fps, 0)).Append(';');
            sb.Append(Num(s.RxMBps, 2)).Append(';');
            sb.Append(Num(s.TxMBps, 2)).Append(';');
            sb.Append(Num(pingMs, 2)).Append(';');
            sb.Append(Num(lossPercent, 1)).Append(';');
            sb.Append(mode);

            WriteRaw(sb.ToString());
            Flush();
        }
    }

    /// <summary>
    /// Пометка события в ленте: «начат стресс-тест», «вход в игру», «температура выше порога».
    /// По ней потом видно, что именно происходило в секунду перед крахом.
    /// </summary>
    public void Mark(string text)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            WriteRaw($"# {DateTime.Now:HH:mm:ss} {text}");
            Flush();
        }
    }

    /// <summary>
    /// Штатное закрытие. Ставит маркер, по отсутствию которого следующий запуск
    /// понимает, что прошлый сеанс оборвался. Вызывать обязательно при выходе.
    /// </summary>
    public void CloseCleanly()
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                WriteRaw($"# завершение: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                WriteRaw(CleanMarker);
                Flush();
                _closedCleanly = true;
            }
            catch { }
            CloseStream();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // Dispose без CloseCleanly — это аварийный путь (падение, снятие процесса).
            // Маркер намеренно не ставим: файл должен остаться помеченным как оборванный.
            if (!_closedCleanly) CloseStream();
        }
    }

    // ── Внутреннее ────────────────────────────────────────────────────────

    private void WriteRaw(string line)
    {
        try
        {
            _writer!.WriteLine(line);
            _written += line.Length + 2;
        }
        catch { }
    }

    private void Flush()
    {
        try
        {
            _writer!.Flush();
            // Flush(true) сбрасывает и буфер самой ОС — именно это переживает
            // выключение питания, обычного Flush() для такого мало
            _stream!.Flush(flushToDisk: true);

            if (_written > MaxFileBytes)
            {
                WriteRaw("# файл достиг предела размера, запись остановлена");
                _writer!.Flush();
                _stream!.Flush(flushToDisk: true);
                CloseStream();
            }
        }
        catch { }
    }

    private void CloseStream()
    {
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _writer = null;
        _stream = null;
    }

    /// <summary>Число с точкой-разделителем; NaN и бесконечность — пустое поле.</summary>
    private static string Num(double v, int digits) =>
        double.IsNaN(v) || double.IsInfinity(v)
            ? ""
            : v.ToString("F" + digits, CultureInfo.InvariantCulture);

    /// <summary>Старые сеансы за пределами <see cref="KeepSessions"/> удаляются, самые новые остаются.</summary>
    private static void CleanupOldSessions()
    {
        try
        {
            var files = new DirectoryInfo(Directory)
                .GetFiles("session-*.csv")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepSessions);

            foreach (var f in files)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }
}
