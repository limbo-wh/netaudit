using System.Globalization;
using NetAudit.Core.Models;

namespace NetAudit.Core.Logging;

public sealed class PingLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

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

        lock (_lock)
            _writer.WriteLine(line);
    }

    public void Dispose()
    {
        _writer.WriteLine($"# session end {DateTimeOffset.Now:HH:mm:ss}");
        _writer.Dispose();
    }
}
