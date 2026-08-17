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
            foreach (ManagementObject o in s.Get())
            {
                string name = o["Name"]?.ToString()?.Trim() ?? "";
                // убираем лишние пробелы внутри строки
                name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ");
                int phys  = Convert.ToInt32(o["NumberOfCores"]);
                int logic = Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                int mhz   = Convert.ToInt32(o["MaxClockSpeed"]);
                return (name, phys, logic, mhz);
            }
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
            string ramType = typeCode switch
            {
                20 => "DDR",
                21 => "DDR2",
                24 => "DDR3",
                26 => "DDR4",
                34 => "DDR5",
                _  => ""
            };
            return (totalBytes / 1_073_741_824.0, ramType, speed, modules);
        }
        catch { }
        return (0, "", 0, 0);
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

            result.Add(new DriveEntry(
                di.Name.TrimEnd('\\'),
                di.VolumeLabel,
                mediaType,
                di.DriveFormat,
                di.TotalSize,
                di.AvailableFreeSpace));
        }
        return result;
    }

    // ── Сетевые адаптеры ─────────────────────────────────────────────────

    private static IReadOnlyList<AdapterEntry> GetAdapters()
    {
        var result = new List<AdapterEntry>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var props  = ni.GetIPProperties();
            var ipList = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();

            long speedMbps = 0;
            try { speedMbps = ni.Speed / 1_000_000; } catch { }

            result.Add(new AdapterEntry(
                ni.Name,
                ni.Description,
                FormatMac(ni.GetPhysicalAddress()),
                ipList,
                ni.NetworkInterfaceType.ToString(),
                speedMbps,
                ni.OperationalStatus == OperationalStatus.Up));
        }
        return result;
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
