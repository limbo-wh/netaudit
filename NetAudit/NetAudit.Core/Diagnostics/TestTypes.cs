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

/// <summary>
/// Пороги температур, общие для всех тестов.
///
/// Раньше каждый тест держал свою копию, и все три были рассчитаны на железо
/// прошлых поколений: 80 °C считалось поводом посмотреть охлаждение, 90 — тревогой.
/// Между тем Ryzen 7000/9000 и Intel 13/14-го поколений штатно работают под полной
/// нагрузкой на 95 °C — это их проектный режим, а не поломка, и стресс-тест на
/// исправной новой машине останавливался через десяток секунд с ложным вердиктом.
/// Видеокарты, отдающие температуру горячей точки (Radeon RX 6000/7000, RTX 30 с
/// памятью GDDR6X), в норме показывают 90–100 °C по тому же датчику.
///
/// Отсюда нынешние значения: беспокоиться стоит после 95 °C, а останавливать
/// нагрузку — на 100 °C, за несколько градусов до защитного отключения самого железа.
/// </summary>
public static class ThermalLimits
{
    /// <summary>Ниже этого температура точно не мешает работать.</summary>
    public const double CpuComfortableC = 85;

    /// <summary>Выше этого стоит посмотреть охлаждение, но само по себе это не авария.</summary>
    public const double CpuWarnC = 95;

    /// <summary>Порог аварийной остановки нагрузки.</summary>
    public const double CpuStopC = 100;

    public const double GpuComfortableC = 80;
    public const double GpuWarnC = 90;
    public const double GpuStopC = 100;

    /// <summary>
    /// Предел для памяти GDDR6/GDDR6X. Производители памяти дают 105 °C, после чего
    /// карта либо сбрасывает частоты, либо виснет. 95 °C — рубеж, за которым стоит
    /// заняться термопрокладками.
    /// </summary>
    public const double GpuMemoryWarnC = 95;

    /// <summary>
    /// Разрыв между горячей точкой кристалла и температурой ядра. У исправной карты
    /// со свежей термопастой под нагрузкой это 10–15 °C. Больше 25 °C означает, что
    /// термоинтерфейс высох или прижим неравномерный — частая история у карт,
    /// которые несколько лет не обслуживали.
    /// </summary>
    public const double GpuHotSpotDeltaWarnC = 25;

    /// <summary>
    /// Пояснение, почему высокая температура — не всегда беда. Показывается рядом
    /// с предупреждением, чтобы владелец новой машины не побежал менять кулер зря.
    /// </summary>
    public const string ModernHardwareNote =
        "Для процессоров AMD Ryzen 7000/9000 и Intel 13/14 поколений 95 °C под полной "
      + "нагрузкой — штатный режим, а не поломка.";
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
