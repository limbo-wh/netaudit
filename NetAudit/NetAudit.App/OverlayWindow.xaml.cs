using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetAudit.App;

public partial class OverlayWindow : Window
{
    // P/Invoke для клик-сквозности и удержания поверх игры
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                    int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;  // клики проходят насквозь в игру
    private const int WS_EX_TOOLWINDOW  = 0x00000080;  // нет в Alt-Tab
    private const int WS_EX_NOACTIVATE  = 0x08000000;  // не ворует фокус у игры

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private static readonly SolidColorBrush BrushGreen  = new(Color.FromRgb(0x86, 0xD9, 0x7A));
    private static readonly SolidColorBrush BrushYellow = new(Color.FromRgb(0xDF, 0xC4, 0x6A));
    private static readonly SolidColorBrush BrushRed    = new(Color.FromRgb(0xEE, 0x8B, 0x8B));
    private static readonly SolidColorBrush BrushValue  = new(Color.FromRgb(0xD8, 0xEA, 0xDF));
    private static readonly SolidColorBrush BrushDim    = new(Color.FromRgb(0x5C, 0x7A, 0x69));

    private AppSettings _settings;

    // Borderless-игра при переключении фокуса встаёт выше оверлея —
    // лечится периодической переустановкой topmost
    private readonly DispatcherTimer _topmostKeeper = new()
    {
        Interval = TimeSpan.FromSeconds(1.5)
    };

    private IntPtr _hwnd;

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Left = settings.OverlayLeft;
        Top  = settings.OverlayTop;
        ApplySettings(settings);

        _topmostKeeper.Tick += (_, _) => ReassertTopmost();
        Closed += (_, _) => _topmostKeeper.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Окно полностью прозрачно для мыши: ни нажать, ни перетащить.
        // NOACTIVATE обязателен — иначе оверлей выбивает игру из фокуса.
        _hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE,
            style | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        _topmostKeeper.Start();
    }

    /// <summary>
    /// Вернуть окно на самый верх. SWP_NOACTIVATE обязателен, иначе будем
    /// выбивать игру из фокуса каждые полторы секунды.
    /// </summary>
    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Вызывается из настроек при изменении позиции
    public void MoveTo(double left, double top)
    {
        Left = left;
        Top  = top;
    }

    /// <summary>
    /// Переставить оверлей в угол рабочей области.
    /// Мышью его не подвинуть — окно сквозное по построению, поэтому только хоткеи.
    /// Угол: 1 — левый верхний, 2 — правый верхний, 3 — левый нижний, 4 — правый нижний.
    /// </summary>
    public void SnapToCorner(int corner)
    {
        const double margin = 20;
        var area = SystemParameters.WorkArea;

        double w = ActualWidth  > 0 ? ActualWidth  : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(w)) w = 200;
        if (double.IsNaN(h)) h = 140;

        (Left, Top) = corner switch
        {
            2 => (area.Right  - w - margin, area.Top    + margin),
            3 => (area.Left       + margin, area.Bottom - h - margin),
            4 => (area.Right  - w - margin, area.Bottom - h - margin),
            _ => (area.Left       + margin, area.Top    + margin),
        };

        _settings.OverlayLeft = Left;
        _settings.OverlayTop  = Top;
        _settings.Save();
    }

    public void Push(float cpu, float gpu, double ramUsed, double ramTotal,
                     double rxMBps, double txMBps,
                     double? gwMs, double? cfMs,
                     double gwLossPct, double cfLossPct,
                     double fps)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ApplyFps(fps);

            OvCpu.Text       = $"{cpu:F0}%";
            OvCpu.Foreground = cpu < 60 ? BrushGreen : cpu < 85 ? BrushYellow : BrushRed;

            OvGpu.Text       = gpu > 0 ? $"{gpu:F0}%" : "—";
            OvGpu.Foreground = gpu < 60 ? BrushGreen : gpu < 85 ? BrushYellow : BrushRed;

            OvRam.Text = ramTotal > 0 ? $"{ramUsed:F1} / {ramTotal:F1} ГБ" : "—";
            double ramPct = ramTotal > 0 ? ramUsed / ramTotal * 100 : 0;
            OvRam.Foreground = ramPct < 70 ? BrushGreen : ramPct < 90 ? BrushYellow : BrushRed;

            OvRx.Text = $"{rxMBps:F2} МБ/с";
            OvTx.Text = $"{txMBps:F2} МБ/с";

            // Потери вынесены в отдельные строки — в игре важнее видеть их сразу,
            // а не дописанными в хвост к задержке
            OvGw.Text       = gwMs.HasValue ? $"{gwMs:F0} мс" : "таймаут";
            OvGw.Foreground = !gwMs.HasValue  ? BrushRed
                            : gwMs < 15 ? BrushGreen : gwMs < 40 ? BrushYellow : BrushRed;

            OvCf.Text       = cfMs.HasValue ? $"{cfMs:F0} мс" : "таймаут";
            OvCf.Foreground = !cfMs.HasValue  ? BrushRed
                            : cfMs < 30 ? BrushGreen : cfMs < 80 ? BrushYellow : BrushRed;

            ApplyLoss(OvGwLoss, gwLossPct);
            ApplyLoss(OvCfLoss, cfLossPct);
        });
    }

    /// <summary>
    /// Кадры в секунду. Прочерк означает не «ноль кадров», а «считать нечем»:
    /// либо счётчик выключен, либо нет прав администратора, либо на переднем плане
    /// нет ничего рисующего. Подробную причину показывает вкладка «Тесты и сервис».
    /// </summary>
    private void ApplyFps(double fps)
    {
        if (double.IsNaN(fps) || fps <= 0)
        {
            OvFps.Text       = "—";
            OvFps.Foreground = BrushDim;
            return;
        }

        OvFps.Text = $"{fps:F0}";
        // Пороги под 60 Гц: ниже 30 играть тяжело, 30–55 заметно дёргается
        OvFps.Foreground = fps >= 55 ? BrushGreen : fps >= 30 ? BrushYellow : BrushRed;
    }

    /// <summary>Потери за сессию. Порог зелёного жёсткий: для игр даже 1% уже заметен.</summary>
    private static void ApplyLoss(System.Windows.Controls.TextBlock tb, double lossPct)
    {
        tb.Text       = $"{lossPct:F2}%";
        tb.Foreground = lossPct < 1 ? BrushGreen : lossPct < 5 ? BrushYellow : BrushRed;
    }

    public void ApplySettings(AppSettings s)
    {
        _settings = s;
        Opacity = Math.Clamp(s.OverlayOpacity, 0.15, 1.0);

        double fs = s.OverlayFontSize;
        foreach (var tb in new[] { OvFps, OvCpu, OvGpu, OvRam, OvRx, OvTx,
                                   OvGw, OvGwLoss, OvCf, OvCfLoss,
                                   LblFps, LblCpu, LblGpu, LblRam, LblRx, LblTx,
                                   LblGw, LblGwLoss, LblCf, LblCfLoss })
            tb.FontSize = fs;

        SetRow(RowFps,    s.OvShowFps);
        SetRow(RowCpu,    s.OvShowCpu);
        SetRow(RowGpu,    s.OvShowGpu);
        SetRow(RowRam,    s.OvShowRam);
        SetRow(RowRx,     s.OvShowNetRx);
        SetRow(RowTx,     s.OvShowNetTx);
        SetRow(RowGw,     s.OvShowGwPing);
        SetRow(RowGwLoss, s.OvShowGwLoss);
        SetRow(RowCf,     s.OvShowCfPing);
        SetRow(RowCfLoss, s.OvShowCfLoss);

        // Скрытая строка не должна резервировать ширину в общей колонке подписей
        UpdateLayout();
    }

    private static void SetRow(UIElement el, bool visible) =>
        el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
