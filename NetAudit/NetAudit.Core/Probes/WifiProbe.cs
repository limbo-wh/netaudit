using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NetAudit.Core.Models;

namespace NetAudit.Core.Probes;

public sealed class WifiProbe
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>
    /// Что подставляется в <see cref="WifiInfo.RadioType"/>, когда вывод netsh получен,
    /// но ни одного знакомого ключа в нём нет. Позволяет вызывающему коду отличить
    /// «Wi-Fi выключен» (пустая строка) от «не смогли разобрать» — иначе на немецкой
    /// или польской Windows блок Wi-Fi молча выглядел бы как отсутствие сети.
    /// </summary>
    public const string ParseFailedMarker = "локаль Windows не распознана";

    /// <summary>
    /// netsh печатает в кодовой странице консоли (866 на русской Windows), а не в UTF-8.
    /// Раньше здесь стояла <c>Encoding.Default</c> — в .NET это всегда UTF-8, и кириллица
    /// приходила мусором: ключ «Состояние» не находился, и работающий Wi-Fi показывался
    /// отключённым. Кодовые страницы, кроме UTF-8, в .NET доступны только после регистрации
    /// провайдера, поэтому регистрация идёт здесь же.
    /// </summary>
    private static readonly Encoding OemEncoding = ResolveOemEncoding();

    private static Encoding ResolveOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch { }

        // Кодовая страница недоступна — берём то, что объявляет сама консоль,
        // и только в самом крайнем случае UTF-8
        try { return Console.OutputEncoding; }
        catch { return Encoding.UTF8; }
    }

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
                StandardOutputEncoding = OemEncoding
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
        if (state is null)
        {
            // Ключа состояния нет. Это либо «беспроводных интерфейсов в системе нет»
            // (тогда netsh печатает одну строку), либо локаль, названий полей которой
            // мы не знаем. Второй случай нельзя выдавать за выключенный Wi-Fi:
            // адаптер работает, просто вывод не прочитан
            int fieldLines = output.Split('\n').Count(l => l.IndexOf(':') > 0);
            return new WifiInfo(false, "", 0, 0, 0, "",
                                fieldLines >= 3 ? ParseFailedMarker : "", 0, 0);
        }

        // Отрицание проверяется первым: «disconnected» содержит в себе «connected»,
        // и прямая проверка на вхождение считала отключённый адаптер подключённым
        bool connected = !state.Contains("disconnect", StringComparison.OrdinalIgnoreCase)
                      && !state.Contains("отключ", StringComparison.OrdinalIgnoreCase)
                      && (state.Contains("connected", StringComparison.OrdinalIgnoreCase)
                       || state.Contains("подключ", StringComparison.OrdinalIgnoreCase));
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
        bool signalKnown = false;
        if (signalStr is not null)
        {
            var digits = new string(signalStr.TakeWhile(char.IsDigit).ToArray());
            signalKnown = int.TryParse(digits, out signal);
        }

        // Ноль означает «не знаем». Прежний безусловный расчёт при неразобранном
        // сигнале выдавал ровно −100 дБм — правдоподобное значение на месте пропуска
        int dbm = signalKnown ? signal / 2 - 100 : 0;

        // Channel
        string? channelStr = Get(output, "Channel", "Канал");
        var channelDigits = new string((channelStr ?? "").Where(char.IsDigit).ToArray());
        int.TryParse(channelDigits, out int channel);

        // Диапазон Windows 11 печатает отдельным полем, и верить надо ему: в 6 ГГц
        // нумерация каналов начинается заново, поэтому канал 37 в Wi-Fi 6E по одному
        // только номеру неотличим от канала 37 в 5 ГГц и подписывался как «5 ГГц»
        string band = NormalizeBand(Get(output, "Band", "Диапазон"));
        if (band.Length == 0)
            band = channel switch
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

    /// <summary>«5 GHz», «2.4 ГГц», «6 GHz» → единая русская подпись. Пусто, если поля нет.</summary>
    private static string NormalizeBand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string digits = new([.. raw.Trim().TakeWhile(c => char.IsDigit(c) || c is '.' or ',')]);
        digits = digits.Replace(',', '.');

        return digits.Length > 0 ? $"{digits} ГГц" : "";
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
