using System.Globalization;
using NetAudit.Core.Models;

namespace NetAudit.Core.Logging;

public sealed class PingLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    /// <summary>
    /// С какой задержки потока писать подвисание. Пара миллисекунд набегает и
    /// на здоровой машине — переключение потоков, продолжение после await.
    /// </summary>
    public const double StallLogMs = 10;

    public PingLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        _writer.WriteLine($"# NetAudit session start {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
    }

    public void Log(PingResult r)
    {
        // LocalDateTime обязателен: Timestamp — это UtcNow, а имя файла и заголовок
        // сессии пишутся в локальном времени. Без него лог расходится сам с собой.
        var ts = r.Timestamp.LocalDateTime;

        string line = r.Success
            ? $"{ts:HH:mm:ss.fff}  {r.Target,-15}  {r.RttMs,7:F2} ms"
            : $"{ts:HH:mm:ss.fff}  {r.Target,-15}  TIMEOUT";

        // Подвисание машины пишется рядом с сетевой задержкой, а не вместо неё:
        // по паре чисел сразу видно, что фриз был, а сеть в этот момент жила
        if (r.StallMs >= StallLogMs)
            line += $"  stall {r.StallMs:F0} ms";

        lock (_lock)
            _writer.WriteLine(line);
    }

    public void Dispose()
    {
        _writer.WriteLine($"# session end {DateTimeOffset.Now:HH:mm:ss}");
        _writer.Dispose();
    }
}
