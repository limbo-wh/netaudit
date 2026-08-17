using System.IO;

namespace NetAudit.App;

/// <summary>
/// Ярлык на рабочем столе, создаваемый самим приложением.
///
/// Раньше этим занимался install.bat, который двойным щелчком запускал
/// powershell.exe с -ExecutionPolicy Bypass. Для файла, помеченного меткой
/// «загружено из интернета» (Zone.Identifier, её ставит любой браузер),
/// это ровно тот же почерк, что и у типичного дроппера: батник тихо поднимает
/// PowerShell с обходом политики выполнения. Application Control блокирует
/// такие .bat/.cmd наглухо, вне зависимости от того, что внутри — проверено
/// 2026-08-17 на этой машине: install.bat помечался «Dangerous file extension
/// from the web» и не запускался вовсе, при этом install.ps1 и сам exe
/// с той же меткой запускались нормально.
///
/// Поэтому ярлык создаётся здесь, в уже запущенном и доверенном процессе,
/// через WScript.Shell — тот же механизм, что использовал install.ps1,
/// но без стороннего скрипта и без вызова интерпретатора со стороны.
/// </summary>
public static class DesktopShortcut
{
    private static string Path_ =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NetAudit.lnk");

    public static bool Exists() => File.Exists(Path_);

    /// <summary>Создать ярлык. Возвращает false и текст ошибки, если не вышло.</summary>
    public static bool Create(out string error)
    {
        error = "";
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            error = "Не удалось определить путь к программе";
            return false;
        }

        try
        {
            // WScript.Shell — стандартный COM-объект Windows для создания .lnk.
            // Позднее связывание (без ссылки на Windows Script Host Object Model)
            // избавляет от лишней зависимости ради одного объекта
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell недоступен");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(Path_);

            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
            shortcut.Description = "NetAudit — мониторинг сети и системы";
            shortcut.Save();

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
