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

    private static string ElevatedPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NetAudit (администратор).lnk");

    public static bool Exists() => File.Exists(Path_);
    public static bool ElevatedExists() => File.Exists(ElevatedPath);

    /// <summary>Создать обычный ярлык. Возвращает false и текст ошибки, если не вышло.</summary>
    public static bool Create(out string error) => CreateAt(Path_, elevated: false, out error);

    /// <summary>
    /// Ярлык, который Windows всегда запускает с правами администратора — тот же эффект,
    /// что даёт галочка «Запуск от имени администратора» в свойствах ярлыка (вкладка
    /// «Совместимость» → «Дополнительно»). UAC всё равно спросит подтверждение при каждом
    /// запуске — это не обход, а штатное поведение элевации, — но искать галочку вручную
    /// не придётся.
    ///
    /// Нужен из-за счётчика FPS в оверлее: сеанс ETW, на котором он держится, создаёт
    /// только администратор, а права не сохраняются между запусками программы (проверено
    /// 2026-08-17 — второй обычный запуск снова оказался без прав, хотя первый был
    /// с ними). Приложение целиком по-прежнему запускается asInvoker по умолчанию —
    /// требовать администратора манифестом ради одной строки в оверлее было бы неправильно
    /// для тех, кому FPS не нужен.
    /// </summary>
    public static bool CreateElevated(out string error) => CreateAt(ElevatedPath, elevated: true, out error);

    private static bool CreateAt(string path, bool elevated, out string error)
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
            dynamic shortcut = shell.CreateShortcut(path);

            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
            shortcut.Description = elevated
                ? "NetAudit — мониторинг сети и системы (с правами администратора, для счётчика FPS)"
                : "NetAudit — мониторинг сети и системы";
            shortcut.Save();

            if (elevated) SetRunAsAdminFlag(path);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Проставляет бит «запускать с повышением» прямо в бинарном формате .lnk.
    /// COM-интерфейс IShellLinkW этого не умеет — только сам Проводник через диалог
    /// свойств, который дёргает недокументированное расширение формата. Бит лежит
    /// в LinkFlags (offset 0x15, он же третий байт 32-битного поля flags) под маской
    /// 0x20 — это ровно то же значение, что ставит галочка «Запуск от имени
    /// администратора». Задокументировано практикой, не MS-SHLLINK: спецификация
    /// формата эту возможность не описывает, но флаг стабилен уже больше десяти лет.
    /// </summary>
    private static void SetRunAsAdminFlag(string lnkPath)
    {
        const int Offset = 0x15;
        const byte Bit   = 0x20;

        byte[] bytes = File.ReadAllBytes(lnkPath);
        if (bytes.Length <= Offset) return;   // не похоже на валидный .lnk — не трогаем

        bytes[Offset] |= Bit;
        File.WriteAllBytes(lnkPath, bytes);
    }
}
