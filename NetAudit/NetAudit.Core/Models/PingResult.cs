namespace NetAudit.Core.Models;

/// <param name="RttMs">Задержка сети — то, что намерило ядро Windows.</param>
/// <param name="StallMs">
/// Сколько ответ пролежал, пока NetAudit до него добрался: время по своему
/// секундомеру минус сетевое. Больше нескольких миллисекунд — машина в этот
/// момент подвисала, и сеть тут ни при чём.
/// </param>
public record PingResult(
    string Target,
    double? RttMs,
    bool Success,
    DateTimeOffset Timestamp,
    double StallMs = 0
);
