namespace NetAudit.Core.Probes;

/// <summary>Паспорт видеокарты: всё, что удалось узнать без нагрузки.</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public uint SubsystemId { get; init; }
    public uint Revision { get; init; }

    /// <summary>Собственная видеопамять, байт.</summary>
    public long DedicatedVideoMemory { get; init; }

    /// <summary>Системная память, которую карта может занять под себя.</summary>
    public long SharedSystemMemory { get; init; }

    public string FeatureLevel { get; init; } = "";

    /// <summary>Поддержка половинной точности в шейдерах — важна для нейросетей.</summary>
    public bool SupportsFp16 { get; init; }

    /// <summary>Поддержка двойной точности.</summary>
    public bool SupportsFp64 { get; init; }

    public string DriverVersion { get; init; } = "";
    public DateTime? DriverDate { get; init; }

    // ── Данные NVIDIA (nvidia-smi), для других производителей пусты ──

    public string VbiosVersion { get; init; } = "";
    public string ComputeCapability { get; init; } = "";
    public int PcieGenCurrent { get; init; }
    public int PcieGenMax { get; init; }
    public int PcieWidthCurrent { get; init; }
    public int PcieWidthMax { get; init; }
    public double PowerLimitW { get; init; }
    public double PowerDefaultLimitW { get; init; }
    public double PowerMaxLimitW { get; init; }
    public int MaxGraphicsClockMhz { get; init; }
    public int MaxMemoryClockMhz { get; init; }
    public double TemperatureC { get; init; } = double.NaN;

    /// <summary>Есть ли тензорные ядра. У NVIDIA — начиная с Volta (compute 7.0).</summary>
    public bool HasTensorCores
    {
        get
        {
            if (!double.TryParse(ComputeCapability,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double cc))
                return false;
            return cc >= 7.0;
        }
    }

    /// <summary>Поколение NVIDIA по compute capability — для понятного описания возраста.</summary>
    public string Architecture => ComputeCapability switch
    {
        "5.0" or "5.2" or "5.3" => "Maxwell (2014–2016)",
        "6.0" or "6.1" or "6.2" => "Pascal (2016–2018)",
        "7.0" or "7.2"          => "Volta (2017)",
        "7.5"                   => "Turing (2018–2019)",
        "8.0" or "8.6" or "8.7" => "Ampere (2020–2022)",
        "8.9"                   => "Ada Lovelace (2022–2024)",
        "9.0"                   => "Hopper (2022)",
        "10.0" or "12.0"        => "Blackwell (2025)",
        _                       => "",
    };

    public bool IsNvidia => VendorId == 0x10DE;
    public bool IsAmd    => VendorId == 0x1002;
    public bool IsIntel  => VendorId == 0x8086;
}
