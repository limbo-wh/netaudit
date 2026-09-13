using System.Diagnostics;
using System.Globalization;
using System.Management;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NetAudit.Core.Probes;

/// <summary>
/// Собирает паспорт видеокарты из трёх источников, потому что ни один не полон:
///
///   • **DXGI** — производитель, идентификаторы устройства, объём видеопамяти.
///     Работает для любой карты и не требует прав;
///   • **Direct3D 11** — что карта умеет: уровень возможностей, половинная и
///     двойная точность. Для нейросетей поддержка FP16 решает многое;
///   • **nvidia-smi** — то, чего в DirectX нет вовсе: версия VBIOS, вычислительные
///     возможности, состояние шины PCI Express, пределы мощности. Для карты,
///     купленной с рук, это самое интересное: по ним видно и прошивку, и то,
///     не работает ли карта на урезанной шине.
///
/// Всё чтение, никакой нагрузки.
/// </summary>
public static class GpuInfoProbe
{
    public static Task<GpuInfo?> CollectAsync(CancellationToken ct = default) =>
        Task.Run(() => Collect(ct), ct);

    public static GpuInfo? Collect(CancellationToken ct = default)
    {
        var dxgi = QueryDxgi();
        if (dxgi is null) return null;

        var nv = dxgi.VendorId == 0x10DE ? QueryNvidiaSmi(ct) : new Dictionary<string, string>();
        var (driverVersion, driverDate) = QueryDriver(dxgi.Name);
        var (fp16, fp64, level) = QueryCapabilities();

        return new GpuInfo
        {
            Name                 = dxgi.Name,
            Vendor               = VendorName(dxgi.VendorId),
            VendorId             = dxgi.VendorId,
            DeviceId             = dxgi.DeviceId,
            SubsystemId          = dxgi.SubsystemId,
            Revision             = dxgi.Revision,
            DedicatedVideoMemory = dxgi.DedicatedVideoMemory,
            SharedSystemMemory   = dxgi.SharedSystemMemory,

            FeatureLevel  = level,
            SupportsFp16  = fp16,
            SupportsFp64  = fp64,

            DriverVersion = driverVersion,
            DriverDate    = driverDate,

            VbiosVersion        = Get(nv, "vbios_version"),
            ComputeCapability   = Get(nv, "compute_cap"),
            PcieGenCurrent      = GetInt(nv, "pcie.link.gen.current"),
            PcieGenMax          = GetInt(nv, "pcie.link.gen.max"),
            PcieWidthCurrent    = GetInt(nv, "pcie.link.width.current"),
            PcieWidthMax        = GetInt(nv, "pcie.link.width.max"),
            PowerLimitW         = GetDouble(nv, "power.limit"),
            PowerDefaultLimitW  = GetDouble(nv, "power.default_limit"),
            PowerMaxLimitW      = GetDouble(nv, "power.max_limit"),
            MaxGraphicsClockMhz = GetInt(nv, "clocks.max.graphics"),
            MaxMemoryClockMhz   = GetInt(nv, "clocks.max.memory"),
            TemperatureC        = GetDouble(nv, "temperature.gpu"),
        };
    }

    // ── DXGI ──────────────────────────────────────────────────────────────

    private sealed record DxgiData(
        string Name, uint VendorId, uint DeviceId, uint SubsystemId, uint Revision,
        long DedicatedVideoMemory, long SharedSystemMemory);

    private static DxgiData? QueryDxgi()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            // Берём адаптер с наибольшей собственной видеопамятью: у ноутбуков и
            // машин со встроенной графикой их несколько, и нас интересует
            // дискретная карта, а не выводящий картинку интегрированный чип
            IDXGIAdapter1? best = null;
            long bestMemory = -1;

            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                var desc = adapter.Description1;

                // Программный адаптер (WARP) — не железо, пропускаем
                bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;

                // Приводить только через ulong. Прямое (long) идёт через неявный
                // оператор PointerUSize → uint и переполняется на любой карте
                // от четырёх гигабайт: восемь гигабайт роняли весь сбор паспорта
                // с OverflowException, и видеокарта «не определялась» вовсе
                long dedicated = (long)(ulong)desc.DedicatedVideoMemory;

                if (!isSoftware && dedicated > bestMemory)
                {
                    best?.Dispose();
                    best = adapter;
                    bestMemory = dedicated;
                }
                else
                {
                    adapter.Dispose();
                }
            }

            if (best is null) return null;

            using (best)
            {
                var d = best.Description1;
                return new DxgiData(
                    (d.Description ?? "").Trim(),
                    (uint)d.VendorId, (uint)d.DeviceId, (uint)d.SubsystemId, (uint)d.Revision,
                    (long)(ulong)d.DedicatedVideoMemory,
                    (long)(ulong)d.SharedSystemMemory);
            }
        }
        catch
        {
            return null;
        }
    }

    // ── Возможности Direct3D ──────────────────────────────────────────────

    private static (bool Fp16, bool Fp64, string Level) QueryCapabilities()
    {
        try
        {
            var levels = new[] { FeatureLevel.Level_12_1, FeatureLevel.Level_12_0,
                                 FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

            var hr = D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.None, levels,
                out ID3D11Device? device, out FeatureLevel level, out ID3D11DeviceContext? ctx);

            if (hr.Failure || device is null) return (false, false, "");

            using (device)
            using (ctx)
            {
                bool fp64 = false;
                bool fp16 = false;

                try
                {
                    var options = device.CheckFeatureSupport<FeatureDataDoubles>(Vortice.Direct3D11.Feature.Doubles);
                    fp64 = options.DoublePrecisionFloatShaderOps;
                }
                catch { }

                try
                {
                    var minPrecision = device.CheckFeatureSupport<FeatureDataShaderMinPrecisionSupport>(
                        Vortice.Direct3D11.Feature.ShaderMinPrecisionSupport);
                    // 16-битная точность в пиксельных и вычислительных шейдерах.
                    // Ноль означает «только полная точность», любой ненулевой флаг —
                    // что half или 10-битная точность поддерживается
                    fp16 = (int)minPrecision.PixelShaderMinPrecision != 0
                        || (int)minPrecision.AllOtherShaderStagesMinPrecision != 0;
                }
                catch { }

                return (fp16, fp64, LevelName(level));
            }
        }
        catch
        {
            return (false, false, "");
        }
    }

    private static string LevelName(FeatureLevel level) => level switch
    {
        FeatureLevel.Level_12_2 => "12_2",
        FeatureLevel.Level_12_1 => "12_1",
        FeatureLevel.Level_12_0 => "12_0",
        FeatureLevel.Level_11_1 => "11_1",
        FeatureLevel.Level_11_0 => "11_0",
        _ => level.ToString(),
    };

    // ── Драйвер ───────────────────────────────────────────────────────────

    private static (string Version, DateTime? Date) QueryDriver(string name)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");

            foreach (ManagementObject o in s.Get())
            {
                string n = o["Name"]?.ToString() ?? "";
                // Имя в WMI и в DXGI совпадают не всегда дословно — сверяем по вхождению
                if (name.Length > 0 && !n.Contains(name, StringComparison.OrdinalIgnoreCase)
                                    && !name.Contains(n, StringComparison.OrdinalIgnoreCase))
                    continue;

                string version = o["DriverVersion"]?.ToString() ?? "";
                DateTime? date = null;
                try
                {
                    string raw = o["DriverDate"]?.ToString() ?? "";
                    if (raw.Length >= 8)
                        date = ManagementDateTimeConverter.ToDateTime(raw);
                }
                catch { }

                return (version, date);
            }
        }
        catch { }

        return ("", null);
    }

    // ── nvidia-smi ────────────────────────────────────────────────────────

    private static readonly string[] NvidiaFields =
    [
        "vbios_version", "compute_cap",
        "pcie.link.gen.current", "pcie.link.gen.max",
        "pcie.link.width.current", "pcie.link.width.max",
        "power.limit", "power.default_limit", "power.max_limit",
        "clocks.max.graphics", "clocks.max.memory",
        "temperature.gpu",
    ];

    /// <summary>
    /// Спрашивает nvidia-smi одним запуском: процесс стоит около сотни миллисекунд,
    /// а полей нужно больше десятка.
    /// </summary>
    private static Dictionary<string, string> QueryNvidiaSmi(CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(exe)) return result;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = $"--query-gpu={string.Join(",", NvidiaFields)} --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return result;

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            // Первая строка — первая видеокарта; нескольких карт в домашней машине
            // практически не бывает, а брать надо ту же, что выбрал DXGI
            string line = output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            var parts = line.Split(',');

            for (int i = 0; i < NvidiaFields.Length && i < parts.Length; i++)
                result[NvidiaFields[i]] = parts[i].Trim();
        }
        catch { }

        return result;
    }

    private static string Get(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && !v.StartsWith("[N/A", StringComparison.OrdinalIgnoreCase) ? v : "";

    private static int GetInt(Dictionary<string, string> d, string key) =>
        int.TryParse(Get(d, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static double GetDouble(Dictionary<string, string> d, string key) =>
        double.TryParse(Get(d, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : double.NaN;

    private static string VendorName(uint id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        _ => $"0x{id:X4}",
    };
}
