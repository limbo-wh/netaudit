namespace NetAudit.Core.Models;

public record WifiInfo(
    bool   IsWifi,
    string Ssid,
    int    SignalPercent,
    int    SignalDbm,
    int    Channel,
    string Band,
    string RadioType,
    double LinkRxMbps,
    double LinkTxMbps
);
