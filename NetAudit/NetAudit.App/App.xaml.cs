using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NetAudit.App;

public partial class App : Application
{
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetAudit", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SingleInstance.AcquireOrNotifyExisting())
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;

        // Падения вне UI-потока диспетчер не ловит, а именно они убивают процесс молча
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Dump(args.ExceptionObject as Exception, "AppDomain");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Dump(args.Exception, "Task");
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private void OnDispatcherException(object s, DispatcherUnhandledExceptionEventArgs e)
    {
        Dump(e.Exception, "Dispatcher");
        MessageBox.Show($"{e.Exception.Message}\n\nПодробности: {CrashLog}",
            "NetAudit — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Пишем полный стек в файл: без него причина падения теряется безвозвратно.</summary>
    private static void Dump(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);

            // Одна ошибка в шаблоне может повторяться на каждой отрисовке и
            // за минуту раздуть файл до сотен мегабайт — держим потолок
            var fi = new FileInfo(CrashLog);
            if (fi.Exists && fi.Length > 1_000_000) File.Delete(CrashLog);
            File.AppendAllText(CrashLog,
                $"""

                ═══ {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {source} ═══
                {ex}

                """);
        }
        catch { }
    }
}
