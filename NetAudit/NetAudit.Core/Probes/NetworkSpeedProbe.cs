using System.Net.NetworkInformation;

namespace NetAudit.Core.Probes;

public sealed class NetworkSpeedProbe
{
    private long _prevRx;
    private long _prevTx;
    private DateTime _prevTime = DateTime.MinValue;

    public (double RxMBps, double TxMBps) Sample()
    {
        long rx = 0, tx = 0;

        // Опрос идёт по таймеру, и любое исключение здесь валило бы планировщик
        // целиком. Адаптеры появляются и исчезают на ходу — поднялся VPN, уснул
        // Wi-Fi, — и обращение к статистике исчезнувшего адаптера бросает
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (IsVirtualAdapter(ni.Name, ni.Description)) continue;

                    var stats = ni.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                catch { }
            }
        }
        catch { }

        var now = DateTime.UtcNow;
        double rxMBps = 0, txMBps = 0;

        if (_prevTime != DateTime.MinValue)
        {
            double elapsed = (now - _prevTime).TotalSeconds;
            if (elapsed > 0)
            {
                rxMBps = (rx - _prevRx) / elapsed / 1_048_576.0;
                txMBps = (tx - _prevTx) / elapsed / 1_048_576.0;
                if (rxMBps < 0) rxMBps = 0;
                if (txMBps < 0) txMBps = 0;
            }
        }

        _prevRx = rx;
        _prevTx = tx;
        _prevTime = now;

        return (rxMBps, txMBps);
    }

    private static bool IsVirtualAdapter(string name, string description)
    {
        static bool Contains(string s, string sub) =>
            s.Contains(sub, StringComparison.OrdinalIgnoreCase);

        return Contains(name, "vEthernet")
            || Contains(name, "WSL")
            || Contains(name, "Loopback")
            || Contains(description, "Virtual")
            || Contains(description, "Hyper-V")
            || Contains(description, "TAP")
            || Contains(description, "Tunnel");
    }
}
