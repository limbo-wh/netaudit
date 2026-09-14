using System.Diagnostics;
using NetAudit.Core.Models;

namespace NetAudit.Core.Probes;

public sealed class ProcessProbe
{
    // Метка времени — монотонные тики Stopwatch, а не DateTime.Now: местное время
    // прыгает при переводе часов и переходе на летнее время, и интервал между
    // двумя замерами становился отрицательным или огромным, а колонка CPU — мусором
    private Dictionary<int, (long cpuTicks, long stamp)> _prev = [];

    public List<ProcessEntry> Sample()
    {
        long now    = Stopwatch.GetTimestamp();
        var result  = new List<ProcessEntry>();
        var newPrev = new Dictionary<int, (long, long)>();
        int cores   = Environment.ProcessorCount;

        foreach (var p in Process.GetProcesses())
        {
            int    pid  = 0;
            string name = "";
            try
            {
                pid  = p.Id;
                name = p.ProcessName;

                long cpuTicks = p.TotalProcessorTime.Ticks;
                newPrev[pid] = (cpuTicks, now);

                float cpuPct = 0f;
                if (_prev.TryGetValue(pid, out var prev))
                {
                    double elapsed = (now - prev.stamp) / (double)Stopwatch.Frequency;
                    if (elapsed > 0.1)
                    {
                        double delta = (cpuTicks - prev.cpuTicks) / (double)TimeSpan.TicksPerSecond;
                        cpuPct = (float)Math.Clamp(delta / elapsed / cores * 100, 0, 100);
                    }
                }

                result.Add(new ProcessEntry(pid, name, cpuPct,
                                            p.WorkingSet64 / 1_048_576.0));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Отказано в доступе: защищённый процесс (антивирус, Registry, ядро)
                // не отдаёт ни время процессора, ни рабочий набор. Раньше он выпадал
                // из списка целиком, и системные процессы просто исчезали из окна;
                // строка с нулями честнее, чем вид, будто процесса нет.
                // Завершившийся процесс (InvalidOperationException) сюда не попадает
                // и по-прежнему пропускается — показывать покойника незачем
                if (pid != 0 && name.Length > 0)
                    result.Add(new ProcessEntry(pid, name, 0f, 0));
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
