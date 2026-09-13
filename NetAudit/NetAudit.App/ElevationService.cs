using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace NetAudit.App;

/// <summary>
/// Права администратора без UAC-запроса при каждом запуске.
///
/// Половина возможностей приложения требует прав администратора: температуры
/// (драйвер чтения датчиков), счётчик кадров (сеанс трассировки ETW), очистка
/// списка ожидания памяти. Три способа их получить, и все три плохи по-своему:
///
///   • манифест <c>requireAdministrator</c> — UAC-окно при КАЖДОМ запуске, включая
///     автозапуск вместе с Windows. Отвергнуто: приложение работает постоянно;
///   • перезапуск через <c>runas</c> — то же окно, только вручную и каждый раз;
///   • **задача в Планировщике с уровнем «Наивысшие права»** — Windows поднимает
///     процесс как системная служба, минуя обычный путь элевации через проводник,
///     и поэтому без единого диалога. Запрос прав нужен ровно один раз — в момент
///     создания самой задачи.
///
/// Третий путь и выбран. Это не обход защиты, а документированный приём: создать
/// задачу может только администратор, а запускает её тот же пользователь, для
/// которого она создана.
///
/// Ту же задачу заводит установщик (<c>installer/register-task.ps1</c>). Здесь она
/// создаётся для копий, поставленных иначе — распакованных из архива или собранных
/// из исходников, — чтобы «из коробки» работало и у них.
/// </summary>
public static class ElevationService
{
    /// <summary>Имя задачи. Совпадает с тем, что заводит установщик, — чтобы не плодить дубли.</summary>
    public const string TaskName = "NetAudit (администратор)";

    /// <summary>Аргумент, по которому перезапущенный экземпляр знает, что элевация уже была.</summary>
    public const string RelaunchedArgument = "--elevated-relaunch";

    private static bool? _isElevated;

    /// <summary>Работает ли процесс с правами администратора прямо сейчас.</summary>
    public static bool IsElevated
    {
        get
        {
            if (_isElevated is { } cached) return cached;

            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                _isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _isElevated = false;
            }

            return _isElevated.Value;
        }
    }

    /// <summary>Этот запуск — уже перезапуск ради прав. Второй раз пытаться не нужно.</summary>
    public static bool IsRelaunch =>
        Environment.GetCommandLineArgs()
                   .Skip(1)
                   .Any(a => a.Equals(RelaunchedArgument, StringComparison.OrdinalIgnoreCase));

    private static string? ExePath => Environment.ProcessPath;

    // ── Состояние задачи ──────────────────────────────────────────────────

    /// <summary>Задача заведена и указывает на этот же исполняемый файл.</summary>
    public static bool TaskReady()
    {
        string? target = TaskTargetExe();
        if (target is null || ExePath is null) return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(target), Path.GetFullPath(ExePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Задача существует, но запускает другую копию приложения.</summary>
    public static bool TaskPointsElsewhere() => TaskTargetExe() is not null && !TaskReady();

    private static string? _cachedTarget;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    private static readonly object CacheLock = new();

    /// <summary>
    /// На какой файл настроена задача. Читается из её XML: путь к exe меняется при
    /// переносе папки или переустановке, и задача, указывающая в пустоту, хуже
    /// отсутствующей — она молча запускает не то или ничего.
    ///
    /// Результат кэшируется на несколько секунд. Ответ приходит от внешнего
    /// процесса schtasks, а спрашивают его подряд несколько раз — состояние,
    /// описание, доступность кнопок. Без кэша окно настроек открывалось с
    /// многосекундной задержкой, потому что запускало schtasks трижды прямо
    /// в конструкторе.
    /// </summary>
    private static string? TaskTargetExe()
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow - _cachedAt < CacheLifetime) return _cachedTarget;
        }

        string? result = QueryTaskTargetExe();

        lock (CacheLock)
        {
            _cachedTarget = result;
            _cachedAt = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>Сбрасывает кэш — после создания или удаления задачи он устарел.</summary>
    private static void InvalidateCache()
    {
        lock (CacheLock) _cachedAt = DateTime.MinValue;
    }

    private static string? QueryTaskTargetExe()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments              = $"/Query /TN \"{TaskName}\" /XML",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                // Кодировку не навязываем. Попытка читать вывод как Unicode
                // превращала XML в иероглифы, «<Command>» не находился, задача
                // считалась ненастроенной — и автоповышение молча не срабатывало
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            if (p.ExitCode != 0) return null;

            const string open = "<Command>";
            const string close = "</Command>";
            int a = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            int b = xml.IndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (a < 0 || b <= a) return null;

            return xml[(a + open.Length)..b].Trim().Trim('"');
        }
        catch
        {
            return null;
        }
    }

    // ── Создание задачи ───────────────────────────────────────────────────

    /// <summary>
    /// Создаёт или обновляет задачу. Требует прав администратора — это тот самый
    /// единственный раз, когда Windows спросит подтверждение.
    ///
    /// Связка <c>RunLevel=Highest</c> + <c>LogonType=Interactive</c> обязательна:
    /// первое даёт повышенный токен, второе — доступ к рабочему столу. Без второго
    /// окно приложения просто не появится.
    /// </summary>
    public static bool SetupTask(out string error)
    {
        error = "";

        string? exe = ExePath;
        if (string.IsNullOrEmpty(exe))
        {
            error = "не удалось определить путь к программе";
            return false;
        }

        // Скрипт пишется во временный файл: передавать его текстом в аргументах
        // PowerShell 5.1 — верный способ потерять кириллицу и кавычки
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $action = New-ScheduledTaskAction -Execute '{{exe}}'
                $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
                                                        -RunLevel Highest -LogonType Interactive
                $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
                                                         -DontStopIfGoingOnBatteries `
                                                         -ExecutionTimeLimit ([TimeSpan]::Zero) `
                                                         -MultipleInstances IgnoreNew
                Register-ScheduledTask -TaskName '{{TaskName}}' -Action $action `
                                       -Principal $principal -Settings $settings -Force | Out-Null
                exit 0
            } catch {
                exit 1
            }
            """;

        string path = Path.Combine(Path.GetTempPath(), $"netaudit-task-{Guid.NewGuid():N}.ps1");

        try
        {
            // BOM обязателен: PowerShell 5.1 читает файл с диска, и без метки
            // кодировки кириллица в имени задачи превращается в мусор
            File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{path}\"",
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                error = "не удалось запустить настройку";
                return false;
            }

            p.WaitForExit(60_000);

            if (p.ExitCode != 0)
            {
                error = "не удалось создать задачу в Планировщике";
                return false;
            }

            InvalidateCache();

            return TaskReady();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "запрос прав отклонён";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>Убирает задачу. Нужна кнопке «отключить автоповышение».</summary>
    public static bool RemoveTask(out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments       = $"/Delete /TN \"{TaskName}\" /F",
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            };

            using var p = Process.Start(psi);
            p?.WaitForExit(30_000);
            InvalidateCache();
            return p?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── Перезапуск ────────────────────────────────────────────────────────

    /// <summary>
    /// Запускает приложение заново через задачу Планировщика — с правами и без
    /// единого диалога. Возвращает false, если задача не годится: тогда решать
    /// вызывающему, предлагать ли настройку.
    /// </summary>
    public static bool RelaunchViaTask()
    {
        if (!TaskReady()) return false;

        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments       = $"/Run /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return false;

            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Обычный перезапуск с запросом прав — запасной путь, если задачи нет.</summary>
    public static bool RelaunchWithPrompt(out string error)
    {
        error = "";

        string? exe = ExePath;
        if (string.IsNullOrEmpty(exe))
        {
            error = "не удалось определить путь к программе";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb            = "runas",
                Arguments       = RelaunchedArgument,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "запрос прав отклонён";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── Защита от зацикливания ────────────────────────────────────────────

    private static string AttemptMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetAudit", "elevation-attempt");

    /// <summary>
    /// Была ли попытка повыситься только что. Задача Планировщика запускает exe
    /// без аргументов, поэтому поднятый ею экземпляр не может узнать о перезапуске
    /// из командной строки — а если задача почему-то не дала прав (сломана,
    /// изменена, запрещена политикой), приложение перезапускало бы себя по кругу.
    /// Метка на диске разрывает этот круг.
    /// </summary>
    public static bool AttemptedRecently(TimeSpan within)
    {
        try
        {
            var fi = new FileInfo(AttemptMarkerPath);
            return fi.Exists && DateTime.UtcNow - fi.LastWriteTimeUtc < within;
        }
        catch
        {
            return false;
        }
    }

    public static void MarkAttempt()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AttemptMarkerPath)!);
            File.WriteAllText(AttemptMarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    /// <summary>Человеческое описание текущего состояния — для окна настроек.</summary>
    public static string DescribeState()
    {
        if (IsElevated) return "права администратора есть";
        if (TaskReady()) return "настроено, но запущено без прав — перезапустите через ярлык";
        if (TaskPointsElsewhere()) return "задача указывает на другую копию программы";
        return "не настроено";
    }
}
