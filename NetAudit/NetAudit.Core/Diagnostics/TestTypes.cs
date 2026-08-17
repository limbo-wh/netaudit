namespace NetAudit.Core.Diagnostics;

/// <summary>Смысловая окраска строки вывода. Цвета подбирает UI.</summary>
public enum TestLevel
{
    /// <summary>Заголовок теста или раздела.</summary>
    Header,
    /// <summary>Обычная строка результата.</summary>
    Info,
    /// <summary>Результат в норме.</summary>
    Good,
    /// <summary>Результат подозрительный, стоит посмотреть.</summary>
    Warn,
    /// <summary>Результат плохой или тест не выполнился.</summary>
    Bad,
    /// <summary>Служебное: пояснения, ход выполнения.</summary>
    Muted,
}

/// <summary>Одна строка вывода теста.</summary>
public readonly record struct TestLine(string Text, TestLevel Level)
{
    public static TestLine Head(string t) => new(t, TestLevel.Header);
    public static TestLine Info(string t) => new(t, TestLevel.Info);
    public static TestLine Good(string t) => new(t, TestLevel.Good);
    public static TestLine Warn(string t) => new(t, TestLevel.Warn);
    public static TestLine Bad(string t)  => new(t, TestLevel.Bad);
    public static TestLine Dim(string t)  => new(t, TestLevel.Muted);
    public static TestLine Empty          => new("", TestLevel.Muted);
}

/// <summary>
/// Тест выводит результат построчно по ходу выполнения, а не одним куском в конце.
/// Иначе тест скорости на 30 секунд выглядит как зависшее приложение.
/// </summary>
public interface IDiagnosticTest
{
    /// <summary>Заголовок для шапки вывода.</summary>
    string Title { get; }

    Task RunAsync(IProgress<TestLine> log, CancellationToken ct);
}

/// <summary>Общие мелочи форматирования, чтобы числа выглядели одинаково во всех тестах.</summary>
public static class Fmt
{
    public static string Ms(double ms)   => $"{ms,7:F2} мс";
    public static string Mbit(double mb) => $"{mb,7:F1} Мбит/с";

    /// <summary>Байты в человекочитаемый вид.</summary>
    public static string Bytes(double b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):F2} ГБ",
        >= 1024 * 1024         => $"{b / (1024.0 * 1024):F1} МБ",
        >= 1024                => $"{b / 1024.0:F1} КБ",
        _                      => $"{b:F0} Б",
    };

    /// <summary>
    /// Ровный столбик: подпись фиксированной ширины плюс значение.
    /// Пробел добавляется принудительно — у длинной подписи PadRight ничего не добавит,
    /// и строка слипается в «MTU до шлюза (192.168.31.1)1500 байт».
    /// </summary>
    public static string Row(string label, string value, int labelWidth = 26) =>
        (label.Length >= labelWidth ? label + "  " : label.PadRight(labelWidth)) + value;
}
