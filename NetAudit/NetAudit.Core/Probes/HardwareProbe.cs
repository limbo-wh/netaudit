using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using NetAudit.Core.Models;

namespace NetAudit.Core.Probes;

public static class HardwareProbe
{
    public static Task<HardwareInfo> CollectAsync() =>
        Task.Run(Collect);

    private static HardwareInfo Collect()
    {
        var cpu   = QueryCpu();
        var ram   = QueryRam();
        var gpu   = QueryGpu();
        var board = QueryBoard();
        var os    = QueryOs();

        return new HardwareInfo(
            cpu.Name, cpu.PhysicalCores, cpu.LogicalCores, cpu.MaxMhz,
            ram.TotalGb, ram.Type, ram.SpeedMhz, ram.Modules,
            gpu.Name, gpu.VramGb,
            board.Vendor, board.Model,
            os.Caption, os.Build, os.DisplayVersion,
            GetDrives(),
            GetAdapters());
    }

    // ── CPU ──────────────────────────────────────────────────────────────

    private static (string Name, int PhysicalCores, int LogicalCores, int MaxMhz) QueryCpu()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");

            // Суммируем по всем сокетам, а не берём первый: на двухпроцессорной
            // машине половина ядер иначе просто не попадала в паспорт
            string name = "";
            int phys = 0, logic = 0, mhz = 0, sockets = 0;

            foreach (ManagementObject o in s.Get())
            {
                if (name.Length == 0)
                {
                    name = o["Name"]?.ToString()?.Trim() ?? "";
                    // убираем лишние пробелы внутри строки
                    name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ");
                }

                phys  += Convert.ToInt32(o["NumberOfCores"]);
                logic += Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                mhz    = Math.Max(mhz, Convert.ToInt32(o["MaxClockSpeed"]));
                sockets++;
            }

            if (sockets > 1) name = $"{sockets} × {name}";
            if (name.Length > 0) return (name, phys, logic, mhz);
        }
        catch { }
        return (GetCpuNameFromRegistry(), Environment.ProcessorCount, Environment.ProcessorCount, 0);
    }

    private static string GetCpuNameFromRegistry()
    {
        try
        {
            return Registry.LocalMachine
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")
                ?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "";
        }
        catch { return ""; }
    }

    // ── RAM ──────────────────────────────────────────────────────────────

    private static (double TotalGb, string Type, int SpeedMhz, int Modules) QueryRam()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            long totalBytes = 0;
            int  speed      = 0;
            int  typeCode   = 0;
            int  modules    = 0;
            foreach (ManagementObject o in s.Get())
            {
                totalBytes += Convert.ToInt64(o["Capacity"]);
                if (speed == 0) speed = Convert.ToInt32(o["Speed"]);
                if (typeCode == 0) typeCode = Convert.ToInt32(o["SMBIOSMemoryType"]);
                modules++;
            }
            // Тип памяти по кодам SMBIOS — та же таблица, что в RamInfoProbe:
            // держать две копии с разными кодами уже приводило к «пустому типу»
            // у ноутбучной памяти LPDDR
            string ramType = RamInfoProbe.MemoryTypeNameFor((byte)typeCode);

            // Объём из WMI бывает пустым на виртуальных машинах и урезанных BIOS,
            // а Windows тем временем прекрасно знает настоящий: берём его оттуда
            double totalGb = totalBytes > 0 ? totalBytes / 1_073_741_824.0 : VisibleRamGb();

            return (totalGb, ramType, speed, modules);
        }
        catch { }
        return (VisibleRamGb(), "", 0, 0);
    }

    /// <summary>Объём памяти по данным Windows — запасной источник, когда WMI молчит.</summary>
    private static double VisibleRamGb()
    {
        long bytes = RamInfoProbe.TotalPhysicalBytes();
        return bytes > 0 ? bytes / 1_073_741_824.0 : 0;
    }

    // ── GPU ──────────────────────────────────────────────────────────────

    private static (string Name, double VramGb) QueryGpu()
    {
        string name    = "";
        double vramGb  = 0;
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM FROM Win32_VideoController WHERE AdapterRAM > 0");
            foreach (ManagementObject o in s.Get())
            {
                string n = o["Name"]?.ToString()?.Trim() ?? "";
                // Пропускаем виртуальные/Microsoft адаптеры
                if (n.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                    n.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                name  = n;
                long adapterRam = Convert.ToInt64(o["AdapterRAM"]);
                vramGb = adapterRam / 1_073_741_824.0;
                break;
            }
        }
        catch { }

        // Более точный объём VRAM из реестра (обходит ограничение UInt32 в WMI)
        if (name.Length > 0)
        {
            try
            {
                const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                var cls = Registry.LocalMachine.OpenSubKey(classKey);
                if (cls != null)
                {
                    foreach (var sub in cls.GetSubKeyNames().Where(k => k.Length == 4 && int.TryParse(k, out _)))
                    {
                        var dev = cls.OpenSubKey(sub);
                        long regVram = dev?.GetValue("HardwareInformation.qwMemorySize") is long v ? v : 0;
                        if (regVram > 0) { vramGb = regVram / 1_073_741_824.0; break; }
                    }
                }
            }
            catch { }
        }

        return (name, vramGb);
    }

    // ── Материнская плата ─────────────────────────────────────────────────

    private static (string Vendor, string Model) QueryBoard()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject o in s.Get())
                return (o["Manufacturer"]?.ToString()?.Trim() ?? "",
                        o["Product"]?.ToString()?.Trim() ?? "");
        }
        catch { }
        return ("", "");
    }

    // ── ОС ───────────────────────────────────────────────────────────────

    private static (string Caption, string Build, string DisplayVersion) QueryOs()
    {
        string caption = "", build = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
            {
                caption = o["Caption"]?.ToString()?.Trim() ?? "";
                build   = o["BuildNumber"]?.ToString()?.Trim() ?? "";
                break;
            }
        }
        catch { }

        string displayVersion = "";
        try
        {
            displayVersion = Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
                ?.GetValue("DisplayVersion")?.ToString() ?? "";
        }
        catch { }

        return (caption, build, displayVersion);
    }

    // ── Накопители ───────────────────────────────────────────────────────

    private static IReadOnlyList<DriveEntry> GetDrives()
    {
        // Пробуем определить тип носителя (SSD/HDD) через WMI Storage namespace
        var diskMedia = new Dictionary<string, string>();
        try
        {
            using var s = new ManagementObjectSearcher(
                @"\\.\ROOT\Microsoft\Windows\Storage",
                "SELECT Number, MediaType FROM MSFT_PhysicalDisk");
            foreach (ManagementObject o in s.Get())
            {
                int mediaType = Convert.ToInt32(o["MediaType"]);
                string mt = mediaType switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                if (mt.Length > 0)
                    diskMedia[Convert.ToInt32(o["Number"]).ToString()] = mt;
            }
        }
        catch { }

        // Сопоставляем диски и разделы
        var partitionDisk  = new Dictionary<string, string>(); // partitionId → diskNum
        var drivePartition = new Dictionary<string, string>(); // driveLetter → partitionId
        try
        {
            using var dd = new ManagementObjectSearcher("SELECT Antecedent,Dependent FROM Win32_DiskDriveToDiskPartition");
            foreach (ManagementObject o in dd.Get())
            {
                string ant = o["Antecedent"]?.ToString() ?? "";
                string dep = o["Dependent"]?.ToString() ?? "";
                string diskNum = ExtractNum(ant, "DiskDrive.DeviceID=\"\\\\\\\\.\\\\PHYSICALDRIVE");
                string partId  = dep;
                if (diskNum.Length > 0 && partId.Length > 0)
                    partitionDisk[partId] = diskNum;
            }

            using var lp = new ManagementObjectSearcher("SELECT Antecedent,Dependent FROM Win32_LogicalDiskToPartition");
            foreach (ManagementObject o in lp.Get())
            {
                string ant = o["Antecedent"]?.ToString() ?? "";
                string dep = o["Dependent"]?.ToString() ?? "";
                string driveLetter = ExtractDeviceId(dep);
                if (driveLetter.Length > 0)
                    drivePartition[driveLetter] = ant;
            }
        }
        catch { }

        var result = new List<DriveEntry>();
        foreach (var di in DriveInfo.GetDrives())
        {
            // Каждый диск — в своём try: метка тома и файловая система бросают на
            // зашифрованном BitLocker разделе и на отвалившемся сетевом диске, а
            // без защиты одно такое исключение уносило весь сбор паспорта железа
            try
            {
                if (!di.IsReady) continue;
                if (di.DriveType == DriveType.CDRom) continue;

                string mediaType = "";
                try
                {
                    string letter = di.Name.TrimEnd('\\');
                    if (drivePartition.TryGetValue(letter, out string? partKey) &&
                        partitionDisk.TryGetValue(partKey, out string? diskNum) &&
                        diskMedia.TryGetValue(diskNum, out string? mt))
                        mediaType = mt;
                }
                catch { }

                if (mediaType.Length == 0)
                    mediaType = di.DriveType == DriveType.Fixed ? "Fixed" : di.DriveType.ToString();

                string label = "";
                try { label = di.VolumeLabel; } catch { }

                string format = "";
                try { format = di.DriveFormat; } catch { }

                result.Add(new DriveEntry(
                    di.Name.TrimEnd('\\'),
                    label,
                    mediaType,
                    format,
                    di.TotalSize,
                    di.AvailableFreeSpace));
            }
            catch { }
        }
        return result;
    }

    // ── Сетевые адаптеры ─────────────────────────────────────────────────

    private static IReadOnlyList<AdapterEntry> GetAdapters()
    {
        var result = new List<AdapterEntry>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Тоже в своём try: GetIPProperties бросает на адаптере, который прямо
            // сейчас поднимается или удаляется — обычное дело при подключении VPN
            try
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (IsPseudoAdapter(ni)) continue;

                var ipList = new List<string>();
                try
                {
                    ipList = ni.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString())
                        .ToList();
                }
                catch { }

                long speedMbps = 0;
                try { speedMbps = ni.Speed / 1_000_000; } catch { }

                string mac = "";
                try { mac = FormatMac(ni.GetPhysicalAddress()); } catch { }

                result.Add(new AdapterEntry(
                    ni.Name,
                    ni.Description,
                    mac,
                    ipList,
                    ni.NetworkInterfaceType.ToString(),
                    speedMbps,
                    ni.OperationalStatus == OperationalStatus.Up));
            }
            catch { }
        }
        return result;
    }

    /// <summary>
    /// Служебные интерфейсы Windows, которые не адаптеры. На машине владельца
    /// 14.09 список из 28 записей: на каждую настоящую карту по три фильтра
    /// (WFP Native, WFP 802.3, QoS Packet Scheduler — все с «-0000» на конце),
    /// десять WAN Miniport под VPN-протоколы, Teredo, 6to4, IP-HTTPS, отладчик
    /// ядра. Настоящих адаптеров — четыре. Показывать всё подряд — значит
    /// повторять предупреждение о медленном линке четыре раза и хоронить
    /// Wi-Fi среди минипортов. VPN-туннели (WireGuard, OpenVPN) остаются:
    /// у них есть IP и они — настоящий путь трафика.
    /// </summary>
    private static bool IsPseudoAdapter(NetworkInterface ni)
    {
        string d = ni.Description;

        if (d.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase)
         || d.Contains("QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase)
         || d.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
         || d.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)
         || d.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
         || d.Contains("6to4", StringComparison.OrdinalIgnoreCase)
         || d.Contains("IP-HTTPS", StringComparison.OrdinalIgnoreCase))
            return true;

        // Туннели и PPP без адреса — заготовки под подключение, а не подключение
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            try
            {
                bool hasIp = ni.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (!hasIp) return true;
            }
            catch { return true; }
        }

        return false;
    }

    // ── Утилиты ──────────────────────────────────────────────────────────

    private static string ExtractNum(string path, string prefix)
    {
        int idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        string rest = path[(idx + prefix.Length)..];
        return new string(rest.TakeWhile(char.IsDigit).ToArray());
    }

    private static string ExtractDeviceId(string path)
    {
        int q = path.LastIndexOf('"');
        if (q <= 0) return "";
        int q2 = path.LastIndexOf('"', q - 1);
        return q2 < 0 ? "" : path[(q2 + 1)..q];
    }

    private static string FormatMac(System.Net.NetworkInformation.PhysicalAddress pa)
    {
        var bytes = pa.GetAddressBytes();
        return bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}
