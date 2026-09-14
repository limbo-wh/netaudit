using System.Diagnostics;
using Microsoft.Win32;

namespace NetAudit.App;

/// <summary>
/// Автозапуск вместе с Windows.
///
/// Запись идёт в HKCU\...\Run — раздел текущего пользователя. Он не требует прав
/// администратора и виден в «Диспетчере задач → Автозагрузка», то есть пользователь
/// в любой момент может выключить автозапуск помимо нашего окна настроек. Раздел
/// HKLM работал бы для всех пользователей, но потребовал бы прав администратора
/// на каждое переключение галочки — для локальной утилиты это перебор.
///
/// Если установщик уже зарегистрировал задачу "NetAudit (автозапуск)" в Планировщике
/// (с 2026-08-25 это всегда так для установленных копий — см. NetAudit.iss), команда
/// в HKCU\...\Run запускает не сам exe, а эту задачу через schtasks.exe: тот же приём,
/// что у ярлыков на столе, только с зашитым в задачу аргументом --tray. Так автозапуск
/// тоже получает права администратора без UAC-запроса при каждой загрузке Windows.
/// Для портативной копии без установщика (задачи нет) — прежнее поведение: HKCU
/// запускает exe напрямую, обычным пользователем.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath        = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName         = "NetAudit";
    private const string AutostartTaskName = "NetAudit (автозапуск)";

    /// <summary>Аргумент, по которому приложение понимает, что стартовало само и окно показывать не надо.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>Приложение запущено автозапуском в свёрнутом виде.</summary>
    public static bool StartedHidden =>
        Environment.GetCommandLineArgs()
                   .Skip(1)
                   .Any(a => a.Equals(TrayArgument, StringComparison.OrdinalIgnoreCase));

    private static string? ExePath => Environment.ProcessPath;

    /// <summary>Прописан ли автозапуск прямо сейчас. Читает реестр, а не настройки.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Записанная в реестре команда — для показа в настройках.</summary>
    public static string? CurrentCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Включить или выключить автозапуск.
    /// </summary>
    /// <param name="enable">Нужен ли автозапуск.</param>
    /// <param name="hidden">Стартовать сразу в трей, не показывая окна.</param>
    /// <param name="error">Текст ошибки, если не получилось.</param>
    public static bool Apply(bool enable, bool hidden, out string error)
    {
        error = "";

        string? exe = ExePath;
        if (enable && string.IsNullOrEmpty(exe))
        {
            error = "Не удалось определить путь к программе";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "Не удалось открыть раздел автозапуска в реестре";
                return false;
            }

            if (enable)
            {
                string cmd;
                if (AutostartTaskExists())
                {
                    // Задача уже несёт --tray внутри себя (зарегистрирована установщиком
                    // с этим аргументом) — hidden здесь ни на что не влияет, галочка
                    // "скрыто" в настройках просто перестаёт иметь смысл для установленной
                    // через инсталлятор копии, там автозапуск всегда в трей и всегда повышен
                    cmd = $"\"{Environment.SystemDirectory}\\schtasks.exe\" /Run /TN \"{AutostartTaskName}\"";
                }
                else
                {
                    // Портативная копия без задачи в Планировщике — как раньше,
                    // напрямую запускаем exe обычным пользователем. Кавычки обязательны:
                    // путь почти наверняка содержит пробелы
                    cmd = hidden ? $"\"{exe}\" {TrayArgument}" : $"\"{exe}\"";
                }
                key.SetValue(ValueName, cmd, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Существует ли задача автозапуска в Планировщике. Запрос задачи прав
    /// администратора не требует — только её создание/изменение (это уже сделал
    /// установщик заранее). Отсутствие задачи — нормальный случай для портативной
    /// копии, не ошибка.
    /// </summary>
    private static bool AutostartTaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments              = $"/Query /TN \"{AutostartTaskName}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            // Без проверки результата WaitForExit чтение ExitCode у не успевшего
            // процесса бросает InvalidOperationException; зависший schtasks снимаем
            // и считаем, что задачи нет — это нормальный случай для портативной копии
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
