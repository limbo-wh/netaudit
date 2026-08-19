using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetAudit.App;

/// <summary>
/// Не даёт запустить второй NetAudit одновременно. Две копии дублируют сетевые
/// пробы, дерутся за одни и те же глобальные хоткеи (см. лог «Хоткеи заняты другой
/// программой») и могут одновременно тронуть Game Boost. Именованный Mutex общий
/// для сессии Windows независимо от прав — портативная копия и версия, поднятая
/// задачей Планировщика с правами администратора, видят один и тот же Mutex.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "NetAudit_SingleInstance_7F2E9C41";
    private static readonly int ShowMessage = RegisterWindowMessage("NetAudit_ShowMainWindow_7F2E9C41");
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    private static Mutex? _mutex;

    /// <returns>true — это единственный экземпляр, можно запускаться дальше.
    /// false — уже есть запущенный: ему отправлен сигнал показаться, а этот
    /// экземпляр обязан завершиться немедленно, не создавая окна.</returns>
    public static bool AcquireOrNotifyExisting()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew) return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Существующий экземпляр поднят с другими правами (например, через
            // задачу Планировщика из установщика) — Mutex с этим именем уже есть,
            // но открыть его нельзя. Это тоже значит "уже запущено", а не ошибка.
        }

        PostMessage(HwndBroadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);
        return false;
    }

    /// <summary>
    /// Подписывает окно на сигнал от второго экземпляра — показать себя.
    /// Между разными уровнями прав (обычный запуск против повышенного и наоборот)
    /// сообщение может не дойти из-за UIPI — тогда штатно ищите через трей.
    /// </summary>
    public static void Attach(Window window, Action onShowRequested)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == ShowMessage)
            {
                onShowRequested();
                handled = true;
            }
            return IntPtr.Zero;
        });
    }

    [DllImport("user32.dll")]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
