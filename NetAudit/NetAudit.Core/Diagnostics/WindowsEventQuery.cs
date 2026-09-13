using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace NetAudit.Core.Diagnostics;

/// <summary>Одно событие журнала Windows в удобном виде.</summary>
public sealed class WinEvent
{
    public required DateTime Time { get; init; }
    public required int Id { get; init; }
    public required string Provider { get; init; }
    public required string Level { get; init; }

    /// <summary>
    /// Текст события. Часто пуст: сообщение собирается из манифеста провайдера,
    /// а у драйверов вроде nvlddmkm манифест в системе не зарегистрирован —
    /// тогда смысл несут только <see cref="Data"/>.
    /// </summary>
    public string Message { get; init; } = "";

    /// <summary>Именованные поля из EventData — то, что переживает отсутствие манифеста.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        new Dictionary<string, string>();

    public string Get(string name) => Data.TryGetValue(name, out var v) ? v : "";
}

/// <summary>
/// Чтение журналов Windows без запуска внешних процессов.
///
/// Через PowerShell (Get-WinEvent) то же самое стоило бы 150–300 мс на каждый
/// запуск процесса, а запросов в отчёте о сбоях десяток. EventLogReader читает
/// напрямую и отбирает нужное XPath-фильтром на стороне Windows, а не перебором
/// всего журнала в нашем коде.
///
/// Журналы System и Application доступны обычному пользователю — права
/// администратора здесь не нужны.
/// </summary>
public static class WindowsEventQuery
{
    /// <summary>
    /// События по списку провайдеров и кодов за последние <paramref name="days"/> суток.
    /// Пустой список кодов — любые коды этих провайдеров.
    /// </summary>
    /// <summary>Уровни события: 1 — критическая, 2 — ошибка, 3 — предупреждение.</summary>
    public static readonly int[] ErrorsOnly = [1, 2];

    public static List<WinEvent> Query(
        string logName,
        IReadOnlyCollection<string> providers,
        IReadOnlyCollection<int> ids,
        int days,
        int maxEvents,
        CancellationToken ct = default,
        IReadOnlyCollection<int>? levels = null)
    {
        var result = new List<WinEvent>();

        try
        {
            string xpath = BuildXPath(providers, ids, days, levels);
            var query = new EventLogQuery(logName, PathType.LogName, xpath)
            {
                ReverseDirection = true,   // сначала самые свежие
                TolerateQueryErrors = true,
            };

            using var reader = new EventLogReader(query);
            for (EventRecord? rec = reader.ReadEvent();
                 rec is not null && result.Count < maxEvents;
                 rec = reader.ReadEvent())
            {
                ct.ThrowIfCancellationRequested();
                using (rec)
                {
                    var ev = Convert(rec);
                    if (ev is not null) result.Add(ev);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* журнал недоступен или очищен — отчёт обходится без этого блока */ }

        return result;
    }

    /// <summary>Сколько событий подходит под условие. Для сводок «столько-то ошибок за неделю».</summary>
    public static int Count(
        string logName,
        IReadOnlyCollection<string> providers,
        IReadOnlyCollection<int> ids,
        int days,
        int cap = 5000,
        CancellationToken ct = default)
        => Query(logName, providers, ids, days, cap, ct).Count;

    private static string BuildXPath(
        IReadOnlyCollection<string> providers, IReadOnlyCollection<int> ids, int days,
        IReadOnlyCollection<int>? levels)
    {
        var parts = new List<string>(4);

        if (providers.Count > 0)
        {
            var names = providers.Select(p => $"@Name='{Escape(p)}'");
            parts.Add($"Provider[{string.Join(" or ", names)}]");
        }

        if (ids.Count > 0)
        {
            var codes = ids.Select(i => $"EventID={i}");
            parts.Add($"({string.Join(" or ", codes)})");
        }

        // Без фильтра по уровню в выборку попадает информационное. Проверено
        // на этой машине: Ntfs 98 — это «Том D: работоспособен, действий не
        // требуется», и без фильтра отчёт показывал двенадцать таких сообщений
        // как «ошибки файловой системы»
        if (levels is { Count: > 0 })
        {
            var lv = levels.Select(l => $"Level={l}");
            parts.Add($"({string.Join(" or ", lv)})");
        }

        // TimeCreated в XPath задаётся в миллисекундах назад от текущего момента
        if (days > 0)
        {
            long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
            parts.Add($"TimeCreated[timediff(@SystemTime) <= {ms}]");
        }

        return parts.Count == 0 ? "*" : $"*[System[{string.Join(" and ", parts)}]]";
    }

    private static string Escape(string s) => s.Replace("'", "&apos;");

    private static WinEvent? Convert(EventRecord rec)
    {
        try
        {
            string message = "";
            // FormatDescription бросает, когда манифест провайдера не зарегистрирован —
            // это штатная ситуация для драйверов, а не ошибка чтения журнала
            try { message = rec.FormatDescription() ?? ""; } catch { }

            return new WinEvent
            {
                Time     = rec.TimeCreated ?? DateTime.MinValue,
                Id       = rec.Id,
                Provider = rec.ProviderName ?? "",
                Level    = LevelName(rec.Level),
                Message  = message.Trim(),
                Data     = ExtractData(rec),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string LevelName(byte? level) => level switch
    {
        1 => "критическая",
        2 => "ошибка",
        3 => "предупреждение",
        4 => "сведения",
        5 => "подробно",
        _ => "",
    };

    /// <summary>
    /// Именованные поля EventData из XML события. Именно отсюда берутся BugcheckCode
    /// у Kernel-Power 41 и параметры WHEA — в человекочитаемом тексте их может не быть вовсе.
    /// </summary>
    private static Dictionary<string, string> ExtractData(EventRecord rec)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var xml = XDocument.Parse(rec.ToXml());
            XNamespace ns = xml.Root?.GetDefaultNamespace() ?? XNamespace.None;

            int unnamed = 0;
            foreach (var el in xml.Descendants(ns + "Data"))
            {
                string name = el.Attribute("Name")?.Value ?? $"Data{unnamed++}";
                dict[name] = el.Value.Trim();
            }
        }
        catch { }
        return dict;
    }
}
