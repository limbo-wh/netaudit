using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetAudit.App;

/// <summary>
/// Глобальные хоткеи через RegisterHotKey + WM_HOTKEY.
/// Работают, даже когда фокус в игре — без этого оверлей неуправляем.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    // Модификаторы RegisterHotKey
    public const uint ModAlt      = 0x0001;
    public const uint ModControl  = 0x0002;
    public const uint ModShift    = 0x0004;
    // MOD_NOREPEAT — без него зажатая клавиша шлёт поток событий
    private const uint ModNoRepeat = 0x4000;

    // Виртуальные коды клавиш
    public const uint VkO = 0x4F;
    public const uint VkB = 0x42;
    public const uint Vk1 = 0x31;
    public const uint Vk2 = 0x32;
    public const uint Vk3 = 0x33;
    public const uint Vk4 = 0x34;

    private readonly Dictionary<int, Action> _actions = [];
    private HwndSource? _source;
    private int _nextId = 1;

    /// <summary>Привязать к окну. Вызывать не раньше OnSourceInitialized — до него HWND нет.</summary>
    public void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("HWND ещё не создан — вызывать из OnSourceInitialized.");

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>
    /// Зарегистрировать хоткей. Возвращает false, если комбинацию уже занял
    /// другой процесс — это штатная ситуация, падать не надо.
    /// </summary>
    public bool Register(uint modifiers, uint vk, Action action)
    {
        if (_source is null) return false;

        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | ModNoRepeat, vk))
            return false;

        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is null) return;

        foreach (int id in _actions.Keys)
            UnregisterHotKey(_source.Handle, id);

        _actions.Clear();
        _source.RemoveHook(WndProc);
        _source = null;
    }
}
