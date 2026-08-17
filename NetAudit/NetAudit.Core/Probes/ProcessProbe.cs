using System.Diagnostics;
using NetAudit.Core.Models;

namespace NetAudit.Core.Probes;

public sealed class ProcessProbe
{
    private Dictionary<int, (long cpuTicks, DateTime time)> _prev = [];

    public List<ProcessEntry> Sample()
    {
        var now     = DateTime.Now;
        var result  = new List<ProcessEntry>();
        var newPrev = new Dictionary<int, (long, DateTime)>();
        int cores   = Environment.ProcessorCount;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                long cpuTicks = p.TotalProcessorTime.Ticks;
                newPrev[p.Id] = (cpuTicks, now);

                float cpuPct = 0f;
                if (_prev.TryGetValue(p.Id, out var prev))
                {
                    double elapsed = (now - prev.time).TotalSeconds;
                    if (elapsed > 0.1)
                    {
                        double delta = (cpuTicks - prev.cpuTicks) / (double)TimeSpan.TicksPerSecond;
                        cpuPct = (float)Math.Clamp(delta / elapsed / cores * 100, 0, 100);
                    }
                }

                result.Add(new ProcessEntry(p.Id, p.ProcessName, cpuPct,
                                            p.WorkingSet64 / 1_048_576.0));
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        _prev = newPrev;
        // Отдаём всё: сортировку, фильтр, группировку и лимит выбирает UI
        return [.. result.OrderByDescending(x => x.CpuPercent)
                         .ThenByDescending(x => x.RamMb)];
    }
}
