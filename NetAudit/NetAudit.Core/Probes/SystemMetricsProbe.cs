using System.Runtime.InteropServices;

namespace NetAudit.Core.Probes;

public sealed class SystemMetricsProbe
{
    // ── CPU ──────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(
        out long idleTime, out long kernelTime, out long userTime);

    private long _prevIdle;
    private long _prevKernel;
    private long _prevUser;

    // ── RAM ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    // ── Battery ───────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;       // 0=батарея, 1=сеть, 255=неизвестно
        public byte BatteryFlag;        // бит 3=зарядка, бит 7=нет батареи
        public byte BatteryLifePercent; // 0–100, 255=неизвестно
        public byte Reserved1;
        public int  BatteryLifeTime;
        public int  BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    // ── Методы ───────────────────────────────────────────────────────────

    public (float CpuPercent, double RamUsedGb, double RamTotalGb) Sample()
    {
        GetSystemTimes(out long idle, out long kernel, out long user);

        float cpu = 0;
        if (_prevKernel != 0 || _prevUser != 0)
        {
            long dIdle   = idle   - _prevIdle;
            long dKernel = kernel - _prevKernel;
            long dUser   = user   - _prevUser;
            long dTotal  = dKernel + dUser;
            if (dTotal > 0)
                cpu = (float)((dTotal - dIdle) * 100.0 / dTotal);
            cpu = Math.Clamp(cpu, 0f, 100f);
        }
        _prevIdle   = idle;
        _prevKernel = kernel;
        _prevUser   = user;

        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);
        double total   = mem.ullTotalPhys / 1_073_741_824.0;
        double used    = (mem.ullTotalPhys - mem.ullAvailPhys) / 1_073_741_824.0;

        return (cpu, used, total);
    }

    public (int BatteryPercent, bool IsCharging, bool HasBattery) GetBattery()
    {
        if (!GetSystemPowerStatus(out var ps))
            return (-1, false, false);

        bool hasBattery = (ps.BatteryFlag & 128) == 0; // бит 7 = нет батареи
        if (!hasBattery)
            return (-1, false, false);

        int pct = ps.BatteryLifePercent == 255 ? -1 : ps.BatteryLifePercent;
        bool charging = (ps.BatteryFlag & 8) != 0; // бит 3 = идёт зарядка
        return (pct, charging, true);
    }
}
