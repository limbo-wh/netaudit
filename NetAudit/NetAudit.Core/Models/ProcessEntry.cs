namespace NetAudit.Core.Models;

public record ProcessEntry(int Pid, string Name, float CpuPercent, double RamMb);
