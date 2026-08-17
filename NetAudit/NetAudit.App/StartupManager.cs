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
/// Планировщик заданий дал бы ещё и запуск с правами администратора без запроса UAC
/// (это пригодилось бы счётчику FPS), но создание задания тоже требует прав.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "NetAudit";

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
                // Кавычки обязательны: путь почти наверняка содержит пробелы
                string cmd = hidden ? $"\"{exe}\" {TrayArgument}" : $"\"{exe}\"";
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
}
