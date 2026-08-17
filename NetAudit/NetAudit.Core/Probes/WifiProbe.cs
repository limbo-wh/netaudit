using System.Diagnostics;
using System.Text;
using NetAudit.Core.Models;

namespace NetAudit.Core.Probes;

public sealed class WifiProbe
{
    /// <summary>
    /// Есть ли вообще поднятый беспроводной адаптер.
    ///
    /// Запуск netsh — это создание процесса, замерено 130 мс, то есть около
    /// 2.6% одного ядра при опросе раз в 5 секунд. На машине с кабелем это
    /// тратится полностью впустую, поэтому сначала дешёвая проверка через
    /// NetworkInterface (доли миллисекунды, без порождения процессов).
    /// </summary>
    private static bool HasWirelessAdapter()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Any(ni => ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                        && ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);
        }
        catch { return false; }
    }

    public async Task<WifiInfo?> SampleAsync()
    {
        if (!HasWirelessAdapter()) return null;

        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.Default
            };
            using var proc = Process.Start(psi)!;
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return Parse(output);
        }
        catch { return null; }
    }

    private static WifiInfo? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // Ищем значение по ключевым словам только в части строки до первого ':'
        static string? Get(string text, params string[] hints)
        {
            foreach (var line in text.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line[..colon];
                foreach (var hint in hints)
                    if (key.Contains(hint, StringComparison.OrdinalIgnoreCase))
                        return line[(colon + 1)..].Trim();
            }
            return null;
        }

        string? state = Get(output, "State", "Состояние");
        bool connected = state?.Contains("connected", StringComparison.OrdinalIgnoreCase) == true
                      || state?.Contains("подключен", StringComparison.OrdinalIgnoreCase) == true;
        if (!connected)
            return new WifiInfo(false, "", 0, 0, 0, "", "", 0, 0);

        // SSID — избегаем строку с BSSID
        string ssid = "";
        foreach (var line in output.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon];
            if (key.Contains("SSID", StringComparison.OrdinalIgnoreCase) &&
               !key.Contains("BSSID", StringComparison.OrdinalIgnoreCase))
            {
                ssid = line[(colon + 1)..].Trim();
                break;
            }
        }

        // Signal → %
        string? signalStr = Get(output, "Signal", "Сигнал");
        int signal = 0;
        if (signalStr is not null)
        {
            var digits = new string(signalStr.TakeWhile(char.IsDigit).ToArray());
            int.TryParse(digits, out signal);
        }
        int dbm = signal / 2 - 100;

        // Channel
        string? channelStr = Get(output, "Channel", "Канал");
        var channelDigits = new string((channelStr ?? "").Where(char.IsDigit).ToArray());
        int.TryParse(channelDigits, out int channel);

        string band = channel switch
        {
            >= 1  and <= 14  => "2.4 ГГц",
            >= 36 and <= 177 => "5 ГГц",
            > 177            => "6 ГГц",
            _                => ""
        };

        string radioType = Get(output, "Radio type", "Тип радио") ?? "";

        // Link rates (Mbps)
        string? rxStr = Get(output, "Receive rate", "Скорость получения", "Частота получения");
        string? txStr = Get(output, "Transmit rate", "Скорость передачи", "Частота передачи");

        double rxMbps = ParseRate(rxStr);
        double txMbps = ParseRate(txStr);

        return new WifiInfo(true, ssid, signal, dbm, channel, band, radioType, rxMbps, txMbps);
    }

    private static double ParseRate(string? s)
    {
        if (s is null) return 0;
        var chars = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
        if (double.TryParse(chars.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v))
            return v;
        return 0;
    }
}
