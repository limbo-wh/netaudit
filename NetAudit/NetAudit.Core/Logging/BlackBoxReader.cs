using System.Globalization;
using System.IO;

namespace NetAudit.Core.Logging;

/// <summary>Одна посекундная запись из файла чёрного ящика.</summary>
public readonly record struct BlackBoxSample(
    TimeSpan TimeOfDay,
    double CpuPercent,
    double CpuTempC,
    double GpuPercent,
    double GpuTempC,
    double RamUsedGb,
    double Fps,
    double PingMs,
    double LossPercent,
    string Mode);

/// <summary>Итог по одному файлу сеанса.</summary>
public sealed class BlackBoxSession
{
    public required string FilePath { get; init; }
    public required DateTime Started { get; init; }
    public required DateTime LastRecord { get; init; }

    /// <summary>Файл закрыт штатно. False — сеанс оборвался: краш, BSOD, снятие питания.</summary>
    public required bool ClosedCleanly { get; init; }

    public required int SampleCount { get; init; }

    /// <summary>Последние записи перед обрывом — самое ценное в файле.</summary>
    public required IReadOnlyList<BlackBoxSample> Tail { get; init; }

    /// <summary>Пометки событий («начат стресс-тест» и т.п.) в хвосте файла.</summary>
    public required IReadOnlyList<string> TailMarks { get; init; }

    public double MaxCpuTempC { get; init; } = double.NaN;
    public double MaxGpuTempC { get; init; } = double.NaN;
    public double MaxCpuPercent { get; init; } = double.NaN;

    public TimeSpan Duration => LastRecord - Started;
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>
/// Разбор файлов чёрного ящика. Отдельный класс от писателя: читает его и отчёт
/// о сбоях, и пользователь по кнопке «Журнал состояния», а записью занят только
/// один экземпляр <see cref="BlackBoxRecorder"/> в работающем приложении.
/// </summary>
public static class BlackBoxReader
{
    /// <summary>Сколько последних замеров показывать по каждому оборванному сеансу.</summary>
    public const int TailSize = 12;

    /// <summary>Все сеансы, новые сверху. Файл, который пишется прямо сейчас, тоже попадает.</summary>
    public static IReadOnlyList<BlackBoxSession> ScanSessions(string? excludePath = null)
    {
        var result = new List<BlackBoxSession>();

        try
        {
            var dir = new DirectoryInfo(BlackBoxRecorder.Directory);
            if (!dir.Exists) return result;

            foreach (var f in dir.GetFiles("session-*.csv").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (excludePath is not null &&
                    string.Equals(f.FullName, excludePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var s = ReadSession(f.FullName);
                if (s is not null) result.Add(s);
            }
        }
        catch { }

        return result;
    }

    /// <summary>Сеансы, оборвавшиеся без маркера штатного закрытия.</summary>
    public static IReadOnlyList<BlackBoxSession> ScanCrashedSessions(string? excludePath = null) =>
        ScanSessions(excludePath).Where(s => !s.ClosedCleanly && s.SampleCount > 0).ToList();

    public static BlackBoxSession? ReadSession(string path)
    {
        try
        {
            // FileShare.ReadWrite — файл текущего сеанса открыт на запись, без этого читать нельзя
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);

            DateTime started = File.GetCreationTime(path);
            bool clean = false;
            int count = 0;
            double maxCpuTemp = double.NaN, maxGpuTemp = double.NaN, maxCpu = double.NaN;

            var tail = new Queue<BlackBoxSample>(TailSize);
            var marks = new Queue<string>(8);
            TimeSpan lastTime = TimeSpan.Zero;

            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;

                if (line[0] == '#')
                {
                    if (line.StartsWith(BlackBoxRecorder.CleanMarker, StringComparison.Ordinal))
                    {
                        clean = true;
                    }
                    else if (line.StartsWith("# начало: ", StringComparison.Ordinal))
                    {
                        var text = line["# начало: ".Length..];
                        int paren = text.IndexOf(" (", StringComparison.Ordinal);
                        if (paren > 0) text = text[..paren];
                        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                                              DateTimeStyles.None, out var dt))
                            started = dt;
                    }
                    else if (!IsHeaderLine(line))
                    {
                        if (marks.Count == 8) marks.Dequeue();
                        marks.Enqueue(line[2..]);
                    }
                    continue;
                }

                // Заголовок таблицы
                if (line.StartsWith("время;", StringComparison.Ordinal)) continue;

                var sample = ParseSample(line);
                if (sample is null) continue;

                count++;
                lastTime = sample.Value.TimeOfDay;

                if (!double.IsNaN(sample.Value.CpuTempC))
                    maxCpuTemp = double.IsNaN(maxCpuTemp) ? sample.Value.CpuTempC : Math.Max(maxCpuTemp, sample.Value.CpuTempC);
                if (!double.IsNaN(sample.Value.GpuTempC))
                    maxGpuTemp = double.IsNaN(maxGpuTemp) ? sample.Value.GpuTempC : Math.Max(maxGpuTemp, sample.Value.GpuTempC);
                if (!double.IsNaN(sample.Value.CpuPercent))
                    maxCpu = double.IsNaN(maxCpu) ? sample.Value.CpuPercent : Math.Max(maxCpu, sample.Value.CpuPercent);

                if (tail.Count == TailSize) tail.Dequeue();
                tail.Enqueue(sample.Value);
            }

            // Дата последней записи: берём день начала и время из строки
            DateTime last = File.GetLastWriteTime(path);
            if (count > 0)
            {
                var candidate = started.Date + lastTime;
                // Сеанс мог перешагнуть полночь
                if (candidate < started) candidate = candidate.AddDays(1);
                last = candidate;
            }

            return new BlackBoxSession
            {
                FilePath      = path,
                Started       = started,
                LastRecord    = last,
                ClosedCleanly = clean,
                SampleCount   = count,
                Tail          = tail.ToArray(),
                TailMarks     = marks.ToArray(),
                MaxCpuTempC   = maxCpuTemp,
                MaxGpuTempC   = maxGpuTemp,
                MaxCpuPercent = maxCpu,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Служебные строки шапки файла — не события. Без этой проверки в «пометках
    /// перед обрывом» вместо полезного («вход в игру», «начат стресс-тест»)
    /// показывались название машины и путь к exe.
    /// </summary>
    private static bool IsHeaderLine(string line) =>
        line.StartsWith("# NetAudit", StringComparison.Ordinal)
     || line.StartsWith("# машина:", StringComparison.Ordinal)
     || line.StartsWith("# версия", StringComparison.Ordinal)
     || line.StartsWith("# строки", StringComparison.Ordinal)
     || line.StartsWith("# завершение:", StringComparison.Ordinal);

    private static BlackBoxSample? ParseSample(string line)
    {
        var p = line.Split(';');
        if (p.Length < 14) return null;
        if (!TimeSpan.TryParse(p[0], CultureInfo.InvariantCulture, out var t)) return null;

        return new BlackBoxSample(
            t,
            D(p[2]), D(p[3]), D(p[4]), D(p[5]), D(p[6]), D(p[8]), D(p[11]), D(p[12]),
            p[13]);
    }

    private static double D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : double.NaN;
}
