namespace NetAudit.Core.Models;

public record PingResult(
    string Target,
    double? RttMs,
    bool Success,
    DateTimeOffset Timestamp
);
