using System.Collections;
using System.Diagnostics;

namespace NetAudit.Core.Probes;

/// <summary>
/// Загрузка GPU как сумма по 3D-движкам категории «GPU Engine».
///
/// Категория содержит экземпляр на каждый процесс и движок — на реальной машине
/// это сотни штук (замерено: 408 всего, из них 108 с engtype_3D). Держать по
/// объекту <see cref="PerformanceCounter"/> на каждый и звать NextValue() в цикле
/// стоило 110–120 мс на тик, то есть около 12% одного ядра при опросе раз в секунду.
///
/// Здесь вместо этого один запрос ReadCategory() на всю категорию (1–2 мс) и
/// собственный подсчёт дельт через CounterSampleCalculator. Значения совпадают
/// с прежними, стоимость ниже примерно в 70 раз.
/// </summary>
public sealed class GpuProbe : IDisposable
{
    private const string CategoryName = "GPU Engine";
    private const string CounterName  = "Utilization Percentage";
    private const string EngineFilter = "engtype_3D";

    private readonly object _lock = new();
    private readonly Dictionary<string, CounterSample> _prev = new(StringComparer.OrdinalIgnoreCase);

    private PerformanceCounterCategory? _category;
    private bool _available;
    private bool _initialized;

    public void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;
        }

        try
        {
            if (!PerformanceCounterCategory.Exists(CategoryName)) return;
            var cat = new PerformanceCounterCategory(CategoryName);

            // Первое чтение только засевает предыдущие значения:
            // процент считается по дельте, одной точки для него мало
            ReadInto(cat, seedOnly: true);

            lock (_lock)
            {
                _category  = cat;
                _available = true;
            }
        }
        catch
        {
            // Категории может не быть на старых драйверах — тогда просто отдаём 0
        }
    }

    public float Sample()
    {
        PerformanceCounterCategory? cat;
        lock (_lock)
        {
            if (!_available) return 0f;
            cat = _category;
        }
        if (cat is null) return 0f;

        try
        {
            return Math.Clamp(ReadInto(cat, seedOnly: false), 0f, 100f);
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>Один запрос на всю категорию, сумма процентов по 3D-движкам.</summary>
    private float ReadInto(PerformanceCounterCategory cat, bool seedOnly)
    {
        var instances = cat.ReadCategory()[CounterName];
        float total = 0f;

        foreach (DictionaryEntry entry in instances)
        {
            if (entry.Key is not string name || entry.Value is not InstanceData data)
                continue;

            // ReadCategory отдаёт имена в нижнем регистре, в отличие от
            // GetInstanceNames() — сравнение обязано быть регистронезависимым,
            // иначе не совпадёт ничего и GPU всегда будет 0%
            if (name.IndexOf(EngineFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            lock (_lock)
            {
                if (!seedOnly && _prev.TryGetValue(name, out var prev))
                {
                    try { total += CounterSampleCalculator.ComputeCounterValue(prev, data.Sample); }
                    catch { }
                }
                _prev[name] = data.Sample;
            }
        }

        return total;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _prev.Clear();
            _category  = null;
            _available = false;
        }
    }
}
