using System.IO;
using System.Windows;
using System.Windows.Media;
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

        if (TryElevateSilently())
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

    /// <summary>
    /// Перезапускает приложение с правами администратора через задачу Планировщика,
    /// если это возможно сделать молча. Вызывается до создания окна — иначе окно
    /// успело бы мелькнуть и исчезнуть.
    ///
    /// Молча — значит без UAC-запроса: задача уже создана и указывает на этот же
    /// файл. Если задачи нет, здесь ничего не происходит: предложение настроить
    /// покажет главное окно, потому что спрашивать разрешение должен видимый
    /// интерфейс, а не процесс без окон.
    /// </summary>
    /// <returns>true — перезапуск пошёл, этот экземпляр обязан завершиться.</returns>
    private static bool TryElevateSilently()
    {
        try
        {
            if (ElevationService.IsElevated) return false;
            if (ElevationService.IsRelaunch) return false;

            var settings = AppSettings.Load();
            if (!settings.AutoElevate) return false;
            if (!ElevationService.TaskReady()) return false;

            // Задача могла оказаться нерабочей — например, запрещена политикой.
            // Без этой проверки приложение перезапускало бы себя бесконечно
            if (ElevationService.AttemptedRecently(TimeSpan.FromMinutes(2))) return false;

            ElevationService.MarkAttempt();

            // Имя Mutex надо освободить раньше, чем поднимется новый экземпляр,
            // иначе тот решит, что программа уже запущена, и молча выйдет
            SingleInstance.ReleaseForRelaunch();

            if (ElevationService.RelaunchViaTask()) return true;

            // Не получилось — работаем как есть, без прав
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ужимает окно до рабочей области того монитора, на котором оно открывается.
    /// Стартовые размеры окон подобраны под большой монитор; на ноутбуке 1366×768
    /// или при масштабе 150% нижняя часть с вкладками и кнопками уезжала за край
    /// экрана, а у окон с <c>ResizeMode="NoResize"</c> дотянуться до неё было нечем.
    /// Вызывать из <c>OnSourceInitialized</c>: раньше окно ещё не знает своего
    /// монитора, позже пользователь успеет увидеть скачок размера.
    /// </summary>
    /// <param name="reserve">Запас на рамку и панель задач, в точках WPF.</param>
    public static void FitToScreen(Window window, double reserve = 48)
    {
        try
        {
            var area = WorkAreaFor(window);

            // Нижние границы — чтобы кривые данные о мониторе не схлопнули окно в точку
            double maxWidth  = Math.Max(360, area.Width  - reserve);
            double maxHeight = Math.Max(280, area.Height - reserve);

            // Сравнение с NaN всегда ложно — незаданный размер (SizeToContent) не трогаем
            if (window.Width  > maxWidth)  window.Width  = maxWidth;
            if (window.Height > maxHeight) window.Height = maxHeight;
        }
        catch { }
    }

    /// <summary>
    /// Рабочая область монитора окна в точках WPF. WinForms отдаёт границы в
    /// физических пикселях, поэтому переводим их матрицей источника представления —
    /// иначе при масштабе 150% «свободные» 768 пикселей превратятся в завышенные
    /// 768 точек, и вся проверка потеряет смысл.
    /// </summary>
    private static Rect WorkAreaFor(Window window)
    {
        try
        {
            // Окно ещё без источника, если его размер правят до показа —
            // тогда ориентируемся на владельца, он уже на нужном мониторе
            Visual? visual = PresentationSource.FromVisual(window) is not null
                ? window
                : window.Owner;

            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero && window.Owner is { } owner)
                hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;

            if (hwnd == IntPtr.Zero || visual is null) return SystemParameters.WorkArea;

            var transform = PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformFromDevice;
            if (transform is not { } m) return SystemParameters.WorkArea;

            var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            var topLeft     = m.Transform(new Point(wa.Left,  wa.Top));
            var bottomRight = m.Transform(new Point(wa.Right, wa.Bottom));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            // Основной монитор — разумный запасной вариант: его размеры
            // SystemParameters отдаёт уже в точках WPF
            return SystemParameters.WorkArea;
        }
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
