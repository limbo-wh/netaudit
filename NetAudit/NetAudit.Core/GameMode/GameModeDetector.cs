using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetAudit.Core.GameMode;

/// <param name="Active">Сейчас на переднем плане полноэкранное приложение.</param>
/// <param name="Reason">Как именно это определено — для лога.</param>
/// <param name="ProcessName">Имя процесса игры, если удалось узнать.</param>
public readonly record struct GameModeState(bool Active, string Reason, string ProcessName);

/// <summary>
/// Определяет, идёт ли сейчас игра, чтобы NetAudit ушёл с дороги.
///
/// Принципиально: никаких перехватов DXGI/Present и никаких внедрений в чужой процесс.
/// Всё, чем мы пользуемся, — это документированные функции оболочки Windows и
/// геометрия окна переднего плана. Античиты такое не трогают, потому что то же самое
/// делает сама Windows, когда решает не показывать всплывающие уведомления.
///
/// Две проверки, потому что случаи разные:
///   1. SHQueryUserNotificationState ловит настоящий полноэкранный Direct3D
///      (exclusive fullscreen) и режим презентации.
///   2. Проверка геометрии ловит окно без рамки во весь экран (borderless) —
///      а именно так запускается сейчас почти всё, и на первую проверку оно
///      не отзывается.
/// </summary>
public static class GameModeDetector
{
    // ── Win32 ─────────────────────────────────────────────────────────────

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder buf, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    private const int GwlStyle    = -16;
    private const int WsCaption   = 0x00C00000;   // заголовок вместе с рамкой
    private const int WsThickFrame = 0x00040000;  // рамка изменения размера

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private const uint MonitorDefaultToNearest = 2;

    // Значения QUERY_USER_NOTIFICATION_STATE. Нумерация начинается с 1, не с 0
    private const int QunsBusy                 = 2;  // полноэкранное приложение
    private const int QunsRunningD3dFullScreen = 3;  // полноэкранный Direct3D
    private const int QunsPresentationMode     = 4;  // режим презентации

    /// <summary>Окна оболочки: рабочий стол и панель задач тоже «во весь экран», но игрой не являются.</summary>
    private static readonly string[] ShellClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Windows.UI.Core.CoreWindow",
        "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
        "Windows.UI.Composition.DesktopWindowContentBridge",
    ];

    /// <summary>
    /// Процессы оболочки, у которых бывают безрамочные окна во весь экран.
    /// Например TextInputHost с классом Windows.UI.Core.CoreWindow держит окно
    /// 1920×1080 постоянно — по одной геометрии оно неотличимо от игры.
    /// </summary>
    private static readonly string[] ShellProcesses =
    [
        "explorer", "TextInputHost", "ShellExperienceHost", "SearchHost",
        "StartMenuExperienceHost", "SearchApp", "LockApp", "ApplicationFrameHost",
    ];

    private static readonly int OwnPid = Environment.ProcessId;

    public static GameModeState Detect()
    {
        try
        {
            // ── Проверка 1: что об этом думает сама Windows ──────────────────
            if (SHQueryUserNotificationState(out int state) == 0)
            {
                if (state is QunsRunningD3dFullScreen or QunsPresentationMode or QunsBusy)
                {
                    string reason = state switch
                    {
                        QunsRunningD3dFullScreen => "полноэкранный Direct3D",
                        QunsPresentationMode     => "режим презентации",
                        _                        => "полноэкранное приложение",
                    };
                    return new GameModeState(true, reason, ForegroundProcessName());
                }
            }

            // ── Проверка 2: окно без рамки во весь монитор ───────────────────
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return default;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)OwnPid) return default;

            var cls = new StringBuilder(128);
            GetClassNameW(hwnd, cls, cls.Capacity);
            string className = cls.ToString();
            if (ShellClasses.Contains(className)) return default;

            // Ключевая проверка, без которой всё остальное бессмысленно.
            //
            // Развёрнутое обычное окно закрывает монитор ничуть не хуже игры: замерено,
            // проводник и Chromium в развёрнутом виде дают 1936×1096 при мониторе 1920×1080,
            // то есть даже с запасом. А если панель задач скрывается автоматически, рабочая
            // область совпадает с монитором, и никакой геометрией их уже не различить.
            // Без этой проверки NetAudit уходил в игровой режим от развёрнутого браузера
            // и не возвращался: графики стояли, приоритет был понижен, лог молчал.
            //
            // Отличие простое: у игры в полноэкранном окне нет заголовка и рамки —
            // она рисует кадр во весь экран сама. У браузера, проводника и терминала
            // WS_CAPTION стоит всегда, даже когда заголовок нарисован приложением.
            int style = GetWindowLong(hwnd, GwlStyle);
            if ((style & WsCaption) != 0) return default;

            // Развёрнутое окно без заголовка тоже бывает у оболочки и всплывающих панелей.
            // Игры разворачиваются не через WS_MAXIMIZE, а задают размер во весь экран сами
            if (IsZoomed(hwnd) && (style & WsThickFrame) != 0) return default;

            if (!GetWindowRect(hwnd, out var wnd)) return default;

            IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(mon, ref mi)) return default;

            // Допуск в пиксель-другой: у окон без рамки координаты иногда съезжают
            const int Slack = 2;
            bool coversMonitor =
                wnd.Left   <= mi.Monitor.Left   + Slack &&
                wnd.Top    <= mi.Monitor.Top    + Slack &&
                wnd.Right  >= mi.Monitor.Right  - Slack &&
                wnd.Bottom >= mi.Monitor.Bottom - Slack;

            if (!coversMonitor) return default;

            string process = ProcessNameByPid((int)pid);
            if (ShellProcesses.Contains(process, StringComparer.OrdinalIgnoreCase)) return default;

            return new GameModeState(true, "окно во весь экран", process);
        }
        catch
        {
            // Определение игрового режима — удобство, а не функция ради которой всё затевалось.
            // Если оно сломалось, приложение должно просто работать как обычно.
            return default;
        }
    }

    /// <summary>PID процесса, чьё окно сейчас на переднем плане. 0 — определить не вышло.</summary>
    public static int ForegroundPid()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }
        catch { return 0; }
    }

    private static string ForegroundProcessName()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 ? "" : ProcessNameByPid((int)pid);
    }

    private static string ProcessNameByPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }
}
