using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScottPlot;
using ScottPlot.Plottables;
using NetAudit.Core;
using NetAudit.Core.Models;
using NetAudit.Core.Probes;
using NetAudit.Core.Scheduler;

// Алиасы для устранения неоднозначности между WPF и ScottPlot
using WpfColor = System.Windows.Media.Color;
using WpfLine  = System.Windows.Shapes.Line;

namespace NetAudit.App;

public partial class MainWindow : Window
{
    private static readonly int PingBufferSize = 2400; // 10 мин × 4 Гц
    private static readonly int SysBufferSize  = 300;  // 5 мин × 1 Гц
    private static readonly int LogCapacity    = 2000;

    private readonly DataStreamer _gwStreamer;
    private readonly DataStreamer _cfStreamer;
    private readonly DataStreamer _rxStreamer;
    private readonly DataStreamer _txStreamer;
    private readonly DataStreamer _cpuStreamer;
    private readonly DataStreamer _ramStreamer;

    // Зеркала данных графиков: ScottPlot не отдаёт значение под курсором,
    // поэтому держим свою копию тех же точек
    private readonly Ring _gwRing  = new(PingBufferSize);
    private readonly Ring _cfRing  = new(PingBufferSize);
    private readonly Ring _rxRing  = new(SysBufferSize);
    private readonly Ring _txRing  = new(SysBufferSize);
    private readonly Ring _cpuRing = new(SysBufferSize);
    private readonly Ring _ramRing = new(SysBufferSize);

    private readonly ObservableCollection<LogEntry> _logEntries = [];
    private readonly List<LogEntry> _pendingLog = [];
    private ICollectionView? _logView;
    private string _logFilter = "all";
    private string _logSearch = "";
    private bool   _logAutoScroll = true;
    private int    _logShown;
    private readonly PingStats _gwStats = new();
    private readonly PingStats _cfStats = new();

    private ProbeScheduler?         _scheduler;
    private SystemMetricsScheduler? _sysScheduler;
    private AppSettings             _settings = AppSettings.Load();
    private HardwareInfo?           _hardware;
    private DateTime                _startTime;

    // Текущие средние для определения спайков
    private double _gwAvg;
    private double _cfAvg;

    // Трекеры серий потерь (UI-поток)
    private long _gwPrevConsec;
    private long _cfPrevConsec;

    // Заполняются при обнаружении обновления
    private string  _updateDownloadUrl = "";
    private string? _updateSha256;

    // Оверлей
    private OverlayWindow? _overlay;
    private double? _gwLastRtt;
    private double? _cfLastRtt;

    // Глобальные хоткеи — оверлей должен управляться, не выходя из игры
    private readonly HotkeyManager _hotkeys = new();

    // Останавливает фоновые циклы при закрытии окна
    private readonly CancellationTokenSource _uiCts = new();

    // Диспетчер процессов
    private readonly ProcessProbe _processProbe = new();
    private readonly ObservableCollection<ProcessViewModel> _processEntries = [];
    private IReadOnlyList<ProcessEntry> _lastProcSample = [];
    private string _procSearch = "";
    private string _procSort   = "cpu";
    private bool   _procGroup;
    private int    _procLimit  = 35;

    // ── Цветовая палитра ──────────────────────────────────────────────────
    private static readonly SolidColorBrush BrushGreen  = new(WpfColor.FromRgb(0x86, 0xD9, 0x7A));
    private static readonly SolidColorBrush BrushYellow = new(WpfColor.FromRgb(0xDF, 0xC4, 0x6A));
    private static readonly SolidColorBrush BrushRed    = new(WpfColor.FromRgb(0xEE, 0x8B, 0x8B));
    private static readonly SolidColorBrush BrushDim    = new(WpfColor.FromRgb(0x4A, 0x63, 0x55));
    private static readonly SolidColorBrush BrushCyan   = new(WpfColor.FromRgb(0x5C, 0xD1, 0xC0));
    private static readonly SolidColorBrush BrushText   = new(WpfColor.FromRgb(0xD8, 0xEA, 0xDF));

    public MainWindow()
    {
        InitializeComponent();
        (_gwStreamer, _cfStreamer, _rxStreamer, _txStreamer, _cpuStreamer, _ramStreamer) = SetupPlots();
        SetupLogView();
        ProcessList.ItemsSource = _processEntries;
        ApplyLogVisibility();
        ApplyGraphVisibility();
        SetupGraphZoom();
        SetupGraphHover();
        SetupTestsTab();
        SetupGameMode();
        SetupGameBoost();
        SetupTray();
        Loaded += OnLoaded;
    }

    // ── Настройка графиков ────────────────────────────────────────────────

    private (DataStreamer gw, DataStreamer cf, DataStreamer rx, DataStreamer tx,
             DataStreamer cpu, DataStreamer ram) SetupPlots()
    {
        ConfigurePingPlot(PlotGateway.Plot);
        ConfigurePingPlot(PlotCloudflare.Plot);

        var gw = PlotGateway.Plot.Add.DataStreamer(PingBufferSize);
        gw.Color = ScottPlot.Color.FromHex("#4FD98B");
        gw.LineWidth = 1.5f;
        gw.ViewScrollLeft();

        var cf = PlotCloudflare.Plot.Add.DataStreamer(PingBufferSize);
        cf.Color = ScottPlot.Color.FromHex("#86D97A");
        cf.LineWidth = 1.5f;
        cf.ViewScrollLeft();

        ConfigureSysPlot(PlotNet.Plot, "МБ/с");
        ConfigureSysPlot(PlotCpu.Plot, "%");
        ConfigureSysPlot(PlotRam.Plot, "ГБ");

        var rx = PlotNet.Plot.Add.DataStreamer(SysBufferSize);
        rx.Color = ScottPlot.Color.FromHex("#5CD1C0");
        rx.LineWidth = 1.2f;
        rx.ViewScrollLeft();

        var tx = PlotNet.Plot.Add.DataStreamer(SysBufferSize);
        tx.Color = ScottPlot.Color.FromHex("#86D97A");
        tx.LineWidth = 1.2f;
        tx.ViewScrollLeft();

        var cpu = PlotCpu.Plot.Add.DataStreamer(SysBufferSize);
        cpu.Color = ScottPlot.Color.FromHex("#E3B44F");
        cpu.LineWidth = 1.2f;
        cpu.ViewScrollLeft();

        var ram = PlotRam.Plot.Add.DataStreamer(SysBufferSize);
        ram.Color = ScottPlot.Color.FromHex("#D4E8A8");
        ram.LineWidth = 1.2f;
        ram.ViewScrollLeft();

        return (gw, cf, rx, tx, cpu, ram);
    }

    private static void ConfigurePingPlot(Plot plot)
    {
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0C1611");
        plot.DataBackground.Color   = ScottPlot.Color.FromHex("#111F18");
        plot.Axes.Color(ScottPlot.Color.FromHex("#5C7A69"));
        plot.Grid.MajorLineColor    = ScottPlot.Color.FromHex("#1C3226");
        plot.Axes.Left.Label.Text   = "мс";
    }

    private static void ConfigureSysPlot(Plot plot, string yLabel)
    {
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0C1611");
        plot.DataBackground.Color   = ScottPlot.Color.FromHex("#111F18");
        plot.Axes.Color(ScottPlot.Color.FromHex("#5C7A69"));
        plot.Grid.MajorLineColor    = ScottPlot.Color.FromHex("#1C3226");
        plot.Axes.Left.Label.Text   = yLabel;
    }

    // ── Hover на графиках ────────────────────────────────────────────────

    // ── Зум области графика ───────────────────────────────────────────────

    /// <summary>Состояние выделения и зума одного графика.</summary>
    private sealed class ZoomState
    {
        public bool   IsZoomed;
        public bool   Dragging;
        public double StartX, StartY;
    }

    private readonly Dictionary<ScottPlot.WPF.WpfPlot, ZoomState> _zoom = [];

    /// <summary>Спрятать курсорный слой графика. Заполняется в SetupPlotHover.</summary>
    private readonly Dictionary<ScottPlot.WPF.WpfPlot, Action> _hideCursor = [];

    private void HideCursor(ScottPlot.WPF.WpfPlot plot)
    {
        if (_hideCursor.TryGetValue(plot, out var hide)) hide();
    }

    /// <summary>Пока график увеличен, автомасштаб не трогаем — иначе зум сбросится на первом же тике.</summary>
    private bool IsZoomed(ScottPlot.WPF.WpfPlot plot) =>
        _zoom.TryGetValue(plot, out var z) && z.IsZoomed;

    private void SetupPlotZoom(ScottPlot.WPF.WpfPlot plot, System.Windows.Shapes.Rectangle rect,
                               System.Windows.Controls.Border badge, params DataStreamer[] streamers)
    {
        var z = new ZoomState();
        _zoom[plot] = z;

        rect.Visibility  = Visibility.Collapsed;
        badge.Visibility = Visibility.Collapsed;

        // DataStreamer сам переустанавливает пределы осей на каждой отрисовке.
        // Пока держим зум, его надо выключить, иначе ось возвращается на место
        // сразу же и увеличение выглядит как будто не сработало.
        void SetManaged(bool on) { foreach (var s in streamers) s.ManageAxisLimits = on; }

        // Своя обработка мыши — чтобы встроенные жесты ScottPlot не спорили с выделением
        plot.UserInputProcessor.Disable();

        plot.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) return;   // двойной клик обрабатывается отдельно
            var p = e.GetPosition(plot);
            z.Dragging = true;
            z.StartX = p.X;
            z.StartY = p.Y;
            plot.CaptureMouse();
        };

        plot.MouseMove += (_, e) =>
        {
            if (!z.Dragging) return;
            var p = e.GetPosition(plot);
            double x = Math.Min(p.X, z.StartX), y = Math.Min(p.Y, z.StartY);
            double w = Math.Abs(p.X - z.StartX), h = Math.Abs(p.Y - z.StartY);
            rect.Margin     = new Thickness(x, y, 0, 0);
            rect.Width      = w;
            rect.Height     = h;
            rect.Visibility = Visibility.Visible;
        };

        plot.MouseLeftButtonUp += (_, e) =>
        {
            if (!z.Dragging) return;
            z.Dragging = false;
            plot.ReleaseMouseCapture();
            rect.Visibility = Visibility.Collapsed;

            var p = e.GetPosition(plot);
            // Слишком маленькое выделение — это клик, а не попытка увеличить
            if (Math.Abs(p.X - z.StartX) < 8 || Math.Abs(p.Y - z.StartY) < 8) return;

            try
            {
                var c1 = plot.Plot.GetCoordinates(new Pixel((float)z.StartX, (float)z.StartY));
                var c2 = plot.Plot.GetCoordinates(new Pixel((float)p.X, (float)p.Y));

                SetManaged(false);
                plot.Plot.Axes.SetLimits(
                    Math.Min(c1.X, c2.X), Math.Max(c1.X, c2.X),
                    Math.Min(c1.Y, c2.Y), Math.Max(c1.Y, c2.Y));

                z.IsZoomed       = true;
                badge.Visibility = Visibility.Visible;
                // Оси уехали — старая курсорная линия и подпись стали враньём
                HideCursor(plot);
                plot.Refresh();
            }
            catch { SetManaged(true); }
        };

        plot.MouseDoubleClick += (_, _) =>
        {
            z.Dragging       = false;
            z.IsZoomed       = false;
            rect.Visibility  = Visibility.Collapsed;
            badge.Visibility = Visibility.Collapsed;
            SetManaged(true);
            plot.Plot.Axes.AutoScale();
            HideCursor(plot);
            plot.Refresh();
        };
    }

    private void SetupGraphZoom()
    {
        SetupPlotZoom(PlotGateway,    GwZoomRect,  GwZoomBadge,  _gwStreamer);
        SetupPlotZoom(PlotCloudflare, CfZoomRect,  CfZoomBadge,  _cfStreamer);
        SetupPlotZoom(PlotNet,        NetZoomRect, NetZoomBadge, _rxStreamer, _txStreamer);
        SetupPlotZoom(PlotCpu,        CpuZoomRect, CpuZoomBadge, _cpuStreamer);
        SetupPlotZoom(PlotRam,        RamZoomRect, RamZoomBadge, _ramStreamer);
    }

    private void SetupGraphHover()
    {
        SetupPlotHover(PlotGateway,    GwHoverBubble,  GwHoverText,  GwCursorLine,  GwCursorDot,  _gwRing,  "мс",   "F2");
        SetupPlotHover(PlotCloudflare, CfHoverBubble,  CfHoverText,  CfCursorLine,  CfCursorDot,  _cfRing,  "мс",   "F2");
        SetupPlotHover(PlotNet,        NetHoverBubble, NetHoverText, NetCursorLine, NetCursorDot, _rxRing,  "МБ/с", "F2", _txRing, "↓", "↑");
        SetupPlotHover(PlotCpu,        CpuHoverBubble, CpuHoverText, CpuCursorLine, CpuCursorDot, _cpuRing, "%",    "F0");
        SetupPlotHover(PlotRam,        RamHoverBubble, RamHoverText, RamCursorLine, RamCursorDot, _ramRing, "ГБ",   "F2");
    }

    /// <summary>
    /// Вертикальная линия под курсором + реальное значение серии в этой точке.
    /// Раньше пузырёк показывал Y-координату курсора, а не данные — это вводило в заблуждение.
    /// </summary>
    private void SetupPlotHover(
        ScottPlot.WPF.WpfPlot plot,
        System.Windows.Controls.Border bubble,
        System.Windows.Controls.TextBlock label,
        WpfLine cursorLine,
        System.Windows.Shapes.Ellipse cursorDot,
        Ring ring, string unit, string fmt,
        Ring? ring2 = null, string prefix1 = "", string prefix2 = "")
    {
        void Hide()
        {
            bubble.Visibility     = Visibility.Collapsed;
            cursorLine.Visibility = Visibility.Collapsed;
            cursorDot.Visibility  = Visibility.Collapsed;
        }

        _hideCursor[plot] = Hide;

        plot.MouseMove += (_, e) =>
        {
            // Во время выделения области курсорный слой только мешает
            if (_zoom.TryGetValue(plot, out var zs) && zs.Dragging) { Hide(); return; }

            int n = ring.Count;
            if (n < 2) { Hide(); return; }

            var pos = e.GetPosition(plot);
            double h = plot.ActualHeight;
            if (h <= 0) { Hide(); return; }

            // Нормируем по текущим пределам оси: так не зависим от того,
            // в каких единицах DataStreamer раскладывает точки по X
            // У DataStreamer координата X — это номер слота в буфере (0..Capacity),
            // а точки прижаты к правому краю. Считаем индекс прямо из X, не через
            // долю оси: так мапинг не врёт ни при неполном буфере, ни при зуме.
            double xData  = plot.Plot.GetCoordinates(new Pixel((float)pos.X, (float)pos.Y)).X;
            int    offset = ring.Capacity - n;          // сколько слотов слева пустует
            int    idx    = (int)Math.Round(xData) - offset;
            if (!ring.TryGet(idx, out double v)) { Hide(); return; }

            // Прилипаем к реальной точке выборки, а не к произвольному пикселю
            double sampleX = idx + offset;

            string text;
            if (ring2 is not null && ring2.TryGet(idx, out double v2))
                text = $"{prefix1}{Fmt(v, fmt, unit)}  {prefix2}{Fmt(v2, fmt, unit)}";
            else
                text = Fmt(v, fmt, unit);

            label.Text = text;

            double lineX = pos.X;
            try
            {
                var pxLine = plot.Plot.GetPixel(new Coordinates(sampleX, double.IsNaN(v) ? 0 : v));
                lineX = pxLine.X;

                if (!double.IsNaN(v))
                {
                    cursorDot.Margin     = new Thickness(lineX - 3.5, pxLine.Y - 3.5, 0, 0);
                    cursorDot.Visibility = Visibility.Visible;
                }
                else cursorDot.Visibility = Visibility.Collapsed;
            }
            catch { cursorDot.Visibility = Visibility.Collapsed; }

            // Вертикальная линия через всю высоту графика
            cursorLine.X1 = 0; cursorLine.X2 = 0;
            cursorLine.Y1 = 0; cursorLine.Y2 = h;
            cursorLine.Margin     = new Thickness(lineX, 0, 0, 0);
            cursorLine.Visibility = Visibility.Visible;

            bubble.UpdateLayout();
            double bx = lineX + 12;
            if (bx + bubble.ActualWidth > plot.ActualWidth) bx = lineX - bubble.ActualWidth - 12;
            bubble.Margin     = new Thickness(Math.Max(0, bx), Math.Max(0, pos.Y - 30), 0, 0);
            bubble.Visibility = Visibility.Visible;
        };

        plot.MouseLeave += (_, _) => Hide();
    }

    private static string Fmt(double v, string fmt, string unit) =>
        double.IsNaN(v) ? "нет ответа" : $"{v.ToString(fmt)} {unit}";

    // ── Запуск ────────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _startTime = DateTime.Now;
            string gateway = NetworkUtils.GetDefaultGateway() ?? "192.168.1.1";
            GatewayLabel.Text = $"Шлюз: {gateway}";
            StatusLabel.Text  = "Работает";

            _scheduler = new ProbeScheduler(gateway, TimeSpan.FromMilliseconds(250));
            _scheduler.GatewayResult    += OnGatewayResult;
            _scheduler.CloudflareResult += OnCloudflareResult;
            _scheduler.Start();

            _sysScheduler = new SystemMetricsScheduler();
            _sysScheduler.SnapshotReady += OnSnapshot;
            _sysScheduler.WifiReady     += OnWifi;
            // Сеанс ETW поднимается только если строка FPS включена — см. FpsProbe
            _sysScheduler.SetFpsEnabled(_settings.OvShowFps);
            _sysScheduler.Start();

            ApplyStartupVisibility();

            // Загружаем железо в фоне — не блокируем старт мониторинга
            _ = LoadHardwareAsync();
            _ = CheckForUpdateAsync();
            _ = RecoverGameBoostAsync();

            if (_settings.OverlayEnabled)
                ShowOverlay();

            // Один раз предлагаем ярлык на рабочем столе. Раньше это делал install.bat,
            // но батник с меткой «из интернета» блокируется Application Control наглухо
            // (проверено 2026-08-17), поэтому теперь ярлык создаёт сам процесс
            if (!_settings.ShortcutOffered && !DesktopShortcut.Exists())
                ShortcutBanner.Visibility = Visibility.Visible;

            _ = RunProcessPollerAsync().ContinueWith(
                    t => Dispatcher.Invoke(() =>
                        AppendEventLog($"⚠ Диспетчер процессов остановлен: {t.Exception?.GetBaseException().Message}", BrushRed)),
                    TaskContinuationOptions.OnlyOnFaulted);

            var ver = System.Reflection.Assembly
                          .GetExecutingAssembly().GetName().Version;
            Title = $"NetAudit {ver?.Major}.{ver?.Minor}.{ver?.Build} — {gateway}";
            await RefreshPlotsLoopAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Ошибка: {ex.Message}";
        }
    }

    // ── Пинг: шлюз ───────────────────────────────────────────────────────

    private void OnGatewayResult(PingResult r)
    {
        _gwLastRtt = r.Success ? r.RttMs : null;
        double gwV = r.Success && r.RttMs.HasValue ? r.RttMs.Value : double.NaN;
        _gwStreamer.Add(gwV);
        _gwRing.Add(gwV);
        _gwStats.Record(r.Success, r.RttMs);

        Dispatcher.InvokeAsync(() =>
        {
            var stats = _gwStats.Get();
            _gwAvg = stats.avg;

            // Пока окно закрыто игрой или свёрнуто, обновлять полтора десятка подписей
            // восемь раз в секунду не для кого. Статистика при этом продолжает копиться —
            // она посчитана выше, до захода в UI-поток
            if (!UiIdle)
            {
                ValGateway.Text       = r.Success ? $"{r.RttMs:F1} мс" : "timeout";
                ValGateway.Foreground = RttBrush(r.RttMs, 15, 40);
                DotGateway.Fill       = RttBrush(r.RttMs, 15, 40);

                UpdateStatsUi(stats, MinGw, AvgGw, MaxGw, JitterGw, AvailGw, LossGw, ConsecGw, PktsGw);
                UptimeLabel.Text = $"Сессия: {(DateTime.Now - _startTime):hh\\:mm\\:ss}";
            }

            // А вот события и лог нужны именно во время игры — ради них всё и затевалось
            HandleConsecutiveEvents("GW", r.Success, stats.consecutive, ref _gwPrevConsec);
            AppendPingLog("GW", r, _gwAvg);
        });
    }

    // ── Пинг: Cloudflare ─────────────────────────────────────────────────

    private void OnCloudflareResult(PingResult r)
    {
        _cfLastRtt = r.Success ? r.RttMs : null;
        double cfV = r.Success && r.RttMs.HasValue ? r.RttMs.Value : double.NaN;
        _cfStreamer.Add(cfV);
        _cfRing.Add(cfV);
        _cfStats.Record(r.Success, r.RttMs);

        Dispatcher.InvokeAsync(() =>
        {
            var stats = _cfStats.Get();
            _cfAvg = stats.avg;

            if (!UiIdle)
            {
                ValCloudflare.Text       = r.Success ? $"{r.RttMs:F1} мс" : "timeout";
                ValCloudflare.Foreground = RttBrush(r.RttMs, 30, 80);
                DotCf.Fill               = RttBrush(r.RttMs, 30, 80);

                UpdateStatsUi(stats, MinCf, AvgCf, MaxCf, JitterCf, AvailCf, LossCf, ConsecCf, PktsCf);
            }

            HandleConsecutiveEvents("CF", r.Success, stats.consecutive, ref _cfPrevConsec);
            AppendPingLog("CF", r, _cfAvg);
        });
    }

    // ── Серии потерь: события в лог ──────────────────────────────────────

    private void HandleConsecutiveEvents(string host, bool success, long consecutive, ref long prev)
    {
        if (success && prev >= 3)
            AppendEventLog($"{host}  ✓ соединение восстановлено (было {prev} потерь подряд)", BrushGreen);
        else if (!success && consecutive is 3 or 5 or 10 || (consecutive > 10 && consecutive % 10 == 0))
            AppendEventLog($"{host}  ⚠ {consecutive} потерь подряд", BrushRed);

        // Баллон только в игре и только один раз за серию (consecutive растёт монотонно,
        // ==5 сработает ровно один раз до следующего успешного ответа). Пока открыто
        // окно, потери и так видны в логе — баллон нужен именно когда NetAudit свёрнут
        if (!success && consecutive == 5 && _gameMode && _settings.GameModeLossAlerts)
            _tray?.ShowBalloon("NetAudit", $"{host}: 5 потерь подряд во время игры", warning: true);

        prev = consecutive;
    }

    // ── Загрузка железа ──────────────────────────────────────────────────

    private async Task LoadHardwareAsync()
    {
        try
        {
            _hardware = await HardwareProbe.CollectAsync();
            Dispatcher.Invoke(() => ApplyHardwareSummary(_hardware));
        }
        catch
        {
            Dispatcher.Invoke(() => HwSummaryLbl.Text = "Не удалось загрузить информацию об оборудовании.");
        }
    }

    private void ApplyHardwareSummary(HardwareInfo h)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (h.CpuName.Length > 0)
        {
            string shortCpu = ShortenCpuName(h.CpuName);
            string cores = h.CpuPhysicalCores > 0 ? $"  {h.CpuPhysicalCores}C/{h.CpuLogicalCores}T" : "";
            parts.Add($"CPU: {shortCpu}{cores}");
        }

        if (h.RamTotalGb > 0)
        {
            string ram = $"{h.RamTotalGb:F0} ГБ";
            if (h.RamType.Length > 0) ram += $" {h.RamType}";
            if (h.RamSpeedMhz > 0)   ram += $" {h.RamSpeedMhz} МГц";
            parts.Add($"RAM: {ram}");
        }

        if (h.GpuName.Length > 0)
            parts.Add($"GPU: {ShortenGpuName(h.GpuName)}");

        if (h.OsCaption.Length > 0)
        {
            string os = h.OsCaption.Replace("Microsoft ", "").Replace("Windows ", "Win ");
            if (h.OsDisplayVersion.Length > 0) os += $" {h.OsDisplayVersion}";
            parts.Add(os);
        }

        HwSummaryLbl.Text = parts.Count > 0
            ? string.Join("  ·  ", parts)
            : "Нет данных";
        HwSummaryLbl.Foreground = new SolidColorBrush(WpfColor.FromRgb(0xB6, 0xCD, 0xBF));
    }

    private static string ShortenCpuName(string name)
    {
        // "Intel(R) Core(TM) i7-12700K CPU @ 3.60GHz" → "Intel Core i7-12700K"
        name = name.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();
        int atIdx = name.IndexOf(" CPU @");
        if (atIdx > 0) name = name[..atIdx].Trim();
        return name;
    }

    private static string ShortenGpuName(string name)
    {
        // "NVIDIA GeForce RTX 3080" → "RTX 3080"; "AMD Radeon RX 6800 XT" → "RX 6800 XT"
        name = name.Replace("NVIDIA GeForce ", "").Replace("AMD Radeon ", "").Trim();
        return name;
    }

    // ── Системные метрики ─────────────────────────────────────────────────

    private void OnSnapshot(SystemSnapshot snap)
    {
        _rxStreamer.Add(snap.RxMBps);
        _txStreamer.Add(snap.TxMBps);
        _cpuStreamer.Add(snap.CpuPercent);
        _ramStreamer.Add(snap.RamUsedGb);

        _rxRing.Add(snap.RxMBps);
        _txRing.Add(snap.TxMBps);
        _cpuRing.Add(snap.CpuPercent);
        _ramRing.Add(snap.RamUsedGb);

        Dispatcher.InvokeAsync(() =>
        {
            // Подписи главного окна — только когда его видно
            if (!UiIdle)
            {
                ValNet.Text  = $"↓{snap.RxMBps:F2}  ↑{snap.TxMBps:F2} МБ/с";
                ValCpu.Text  = $"{snap.CpuPercent:F0}%";
                ValRam.Text  = $"{snap.RamUsedGb:F1} / {snap.RamTotalGb:F1} ГБ";
                StatRx.Text  = $"{snap.RxMBps:F2} МБ/с";
                StatTx.Text  = $"{snap.TxMBps:F2} МБ/с";
                StatCpu.Text = $"{snap.CpuPercent:F0}%";
                StatRam.Text = $"{snap.RamUsedGb:F1} / {snap.RamTotalGb:F1} ГБ";

                StatCpu.Foreground = snap.CpuPercent < 60 ? BrushGreen
                                   : snap.CpuPercent < 85 ? BrushYellow : BrushRed;
                double ramPct = snap.RamTotalGb > 0 ? snap.RamUsedGb / snap.RamTotalGb * 100 : 0;
                StatRam.Foreground = ramPct < 70 ? BrushGreen : ramPct < 90 ? BrushYellow : BrushRed;
            }

            // Оверлей обновляется всегда: во время игры это единственное, что видно
            var gwS = _gwStats.Get();
            var cfS = _cfStats.Get();
            double gwLoss = gwS.sent > 0 ? gwS.lost * 100.0 / gwS.sent : 0;
            double cfLoss = cfS.sent > 0 ? cfS.lost * 100.0 / cfS.sent : 0;
            _overlay?.Push(snap.CpuPercent, snap.GpuPercent,
                           snap.RamUsedGb, snap.RamTotalGb,
                           snap.RxMBps, snap.TxMBps,
                           _gwLastRtt, _cfLastRtt,
                           gwLoss, cfLoss,
                           snap.Fps);

            UpdateTrayTooltip();

            if (UiIdle) return;

            // Батарея
            if (snap.BatteryPercent >= 0)
            {
                string icon   = snap.IsCharging ? "⚡" : "🔋";
                BatteryLbl.Text       = $"{icon} {snap.BatteryPercent}%";
                BatteryLbl.Foreground = snap.BatteryPercent > 20 ? BrushGreen
                                      : snap.BatteryPercent > 10 ? BrushYellow : BrushRed;
                BatteryLbl.Visibility = Visibility.Visible;
            }
            else
            {
                BatteryLbl.Visibility = Visibility.Collapsed;
            }
        });
    }

    // ── Wi-Fi ─────────────────────────────────────────────────────────────

    private void OnWifi(WifiInfo? info)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (info is null || !info.IsWifi)
            {
                WifiTypeLbl.Text       = "Ethernet";
                WifiTypeLbl.Foreground = BrushDim;
                WifiSsidLbl.Text       = "  подключено по кабелю";
                WifiSignalLbl.Text     = "";
                WifiTechLbl.Text       = "";
                return;
            }

            WifiTypeLbl.Text       = "Wi-Fi";
            WifiTypeLbl.Foreground = BrushCyan;
            WifiSsidLbl.Text      = info.Ssid.Length > 0 ? $"  ·  {info.Ssid}" : "";

            string quality = info.SignalPercent switch
            {
                >= 80 => "Отлично",
                >= 60 => "Хорошо",
                >= 40 => "Удовл.",
                _     => "Слабо"
            };
            WifiSignalLbl.Text      = $"  ·  {info.SignalPercent}% ({info.SignalDbm} дБм) · {quality}";
            WifiSignalLbl.Foreground = info.SignalPercent >= 70 ? BrushGreen
                                     : info.SignalPercent >= 40 ? BrushYellow : BrushRed;

            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(info.Band))      parts.Add(info.Band);
            if (info.Channel > 0)                      parts.Add($"кан. {info.Channel}");
            if (!string.IsNullOrEmpty(info.RadioType)) parts.Add(info.RadioType);
            if (info.LinkRxMbps > 0 || info.LinkTxMbps > 0)
                parts.Add($"↓{info.LinkRxMbps:F0}/↑{info.LinkTxMbps:F0} Мбит/с");

            WifiTechLbl.Text = parts.Count > 0 ? $"  ·  {string.Join("  ·  ", parts)}" : "";
        });
    }

    // ── Лог ──────────────────────────────────────────────────────────────

    private void AppendPingLog(string host, PingResult r, double sessionAvg)
    {
        if (!_settings.LogEnabled) return;

        bool isTimeout = !r.Success;
        bool isSpike   = r.Success && r.RttMs.HasValue && sessionAvg > 0
                         && r.RttMs.Value > Math.Max(50, sessionAvg * 4);

        if (_settings.LogOnlyImportant && !isTimeout && !isSpike) return;

        // Во время игры обычные строки пинга в лог не идут: восемь строк в секунду
        // никто не прочитает, а потери и спайки — единственное, что там интересно
        if (_gameMode && _settings.GameModeQuietLog && !isTimeout && !isSpike) return;

        string rttText = r.Success ? $"{r.RttMs,7:F2} мс" : "TIMEOUT        ";
        string tag     = isSpike ? " ⚡SPIKE" : "";
        string line    = $"{r.Timestamp.LocalDateTime:HH:mm:ss.fff}  {host}  {rttText}{tag}";

        var kind = isTimeout ? LogKind.Timeout : isSpike ? LogKind.Spike : LogKind.Ping;
        AddLogEntry(line, isTimeout ? BrushRed : isSpike ? BrushYellow : BrushDim, kind, host);
    }

    private void AppendEventLog(string text, Brush color)
    {
        if (!_settings.LogEnabled) return;
        AddLogEntry($"{DateTime.Now:HH:mm:ss.fff}  {text}", color, LogKind.Event, "");
    }

    /// <summary>
    /// Копим строки и отдаём их в UI пачками из цикла перерисовки.
    ///
    /// Раньше каждая строка сразу шла в привязанную коллекцию и тянула за собой
    /// уведомление представления, генерацию контейнера и ScrollIntoView, который
    /// заставляет список пересчитать разметку. При восьми строках в секунду
    /// это давало +10% одного ядра — больше, чем все пробы вместе взятые.
    /// </summary>
    private void AddLogEntry(string text, Brush color, LogKind kind, string host)
    {
        _pendingLog.Add(new LogEntry(text, color, kind, host));
        if (_pendingLog.Count > LogCapacity) _pendingLog.RemoveAt(0);
    }

    /// <summary>Перенести накопленные строки в список. Вызывается 2 раза в секунду.</summary>
    private void FlushLog()
    {
        if (_pendingLog.Count == 0) return;

        foreach (var entry in _pendingLog)
        {
            // Счётчик ведём по дельте: полный проход по буферу на каждой строке
            // (8 раз в секунду по 2000 записей) — это чистая трата
            if (_logEntries.Count >= LogCapacity)
            {
                if (PassesFilter(_logEntries[0])) _logShown--;
                _logEntries.RemoveAt(0);
            }

            _logEntries.Add(entry);
            if (PassesFilter(entry)) _logShown++;
        }
        _pendingLog.Clear();

        UpdateLogCount();

        // Одна прокрутка на пачку вместо одной на строку. И ни одной, пока окна не видно:
        // ScrollIntoView заставляет список пересчитать разметку, а показывать её некому
        if (_logAutoScroll && !UiIdle && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    // ── Фильтрация лога ──────────────────────────────────────────────────

    /// <summary>Проходит ли строка текущий фильтр и поиск.</summary>
    private bool PassesFilter(LogEntry e)
    {
        bool kindOk = _logFilter switch
        {
            "timeout"  => e.Kind == LogKind.Timeout,
            "spike"    => e.Kind == LogKind.Spike,
            "problems" => e.Kind is LogKind.Timeout or LogKind.Spike,
            "event"    => e.Kind == LogKind.Event,
            "gw"       => e.Host == "GW",
            "cf"       => e.Host == "CF",
            _          => true,
        };
        if (!kindOk) return false;

        return _logSearch.Length == 0
            || e.Text.Contains(_logSearch, StringComparison.OrdinalIgnoreCase);
    }

    private void SetupLogView()
    {
        _logView = CollectionViewSource.GetDefaultView(_logEntries);
        _logView.Filter = o => o is LogEntry e && PassesFilter(e);
        LogList.ItemsSource = _logView;
    }

    private void RefreshLogView()
    {
        _logView?.Refresh();
        RecountLog();
    }

    /// <summary>Полный пересчёт — только при смене фильтра или поиска.</summary>
    private void RecountLog()
    {
        _logShown = 0;
        foreach (var e in _logEntries)
            if (PassesFilter(e)) _logShown++;
        UpdateLogCount();
    }

    private void UpdateLogCount()
    {
        if (LogCountLbl is null) return;
        LogCountLbl.Text = _logShown == _logEntries.Count
            ? $"{_logShown} строк"
            : $"{_logShown} из {_logEntries.Count}";
    }

    private void OnLogFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LogFilterCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            _logFilter = item.Tag?.ToString() ?? "all";
        RefreshLogView();
    }

    private void OnLogSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _logSearch = LogSearchBox.Text ?? "";
        RefreshLogView();
    }

    private void OnLogPauseToggle(object sender, RoutedEventArgs e)
    {
        _logAutoScroll = !_logAutoScroll;
        LogPauseBtn.Content    = _logAutoScroll ? "⏸ Автопрокрутка" : "▶ Прокрутка стоит";
        LogPauseBtn.Foreground = _logAutoScroll ? BrushText : BrushYellow;
    }

    private void OnLogClear(object sender, RoutedEventArgs e)
    {
        _pendingLog.Clear();
        _logEntries.Clear();
        _logShown = 0;
        UpdateLogCount();
    }

    private void OnLogSave(object sender, RoutedEventArgs e)
    {
        FlushLog();   // иначе последние полсекунды строк не попадут в файл
        var lines = (_logView?.Cast<LogEntry>() ?? _logEntries).Select(x => x.Text).ToList();
        if (lines.Count == 0)
        {
            MessageBox.Show(this, "Нечего сохранять — список пуст.", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Сохранить лог",
            Filter     = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            FileName   = $"netaudit_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            DefaultExt = ".txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllLines(dlg.FileName, lines);
            AppendEventLog($"✓ Лог сохранён: {System.IO.Path.GetFileName(dlg.FileName)} ({lines.Count} строк)", BrushGreen);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось сохранить файл:\n{ex.Message}", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Настройки ─────────────────────────────────────────────────────────

    private void OnHardware(object sender, RoutedEventArgs e)
    {
        new HardwareWindow(_hardware) { Owner = this }.ShowDialog();
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings, ApplyAllSettings) { Owner = this };
        win.ShowDialog();
    }

    private void ApplyAllSettings()
    {
        ApplyLogVisibility();
        ApplyGraphVisibility();
        ApplyGameModeSettings();
        RebindHotkeys();

        // Счётчик кадров держит сеанс ETW — поднимаем и гасим его вслед за галочкой
        _sysScheduler?.SetFpsEnabled(_settings.OvShowFps);

        if (_tray is not null) _tray.Visible = _settings.TrayEnabled;
        else if (_settings.TrayEnabled) SetupTray();

        if (_settings.OverlayEnabled && _overlay is null)
            ShowOverlay();
        else if (!_settings.OverlayEnabled && _overlay is not null)
            HideOverlay();
        else
        {
            _overlay?.ApplySettings(_settings);
            _overlay?.MoveTo(_settings.OverlayLeft, _settings.OverlayTop);
        }
    }

    // ── Диспетчер процессов ──────────────────────────────────────────────

    /// <summary>Открыта ли вкладка диспетчера. Читается из фонового цикла опроса.</summary>
    private volatile bool _procTabActive;

    private void OnBottomTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Событие всплывает и от внутренних списков — реагируем только на сами вкладки
        if (!ReferenceEquals(e.OriginalSource, BottomTabs)) return;

        _procTabActive = ReferenceEquals(BottomTabs.SelectedItem, ProcTab);
        if (_procTabActive) RebuildProcessList();
    }

    private async Task RunProcessPollerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        // IsLoaded здесь читать нельзя: после ConfigureAwait(false) цикл живёт
        // в пуле потоков, а это свойство требует UI-потока и бросает исключение.
        // Из-за этого поллер умирал после первой итерации, и диспетчер замирал.
        while (await timer.WaitForNextTickAsync(_uiCts.Token))
        {
            // Обход 120 процессов стоит ~12 мс. Пока вкладка закрыта, это
            // выброшенная работа: результат всё равно никто не увидит
            if (!_procTabActive) continue;

            // Собираем в фоне — итерация процессов медленная
            var entries = await Task.Run(_processProbe.Sample, _uiCts.Token).ConfigureAwait(false);
            // Намеренно не ждём: следующий тик важнее, чем завершение отрисовки
            _ = Dispatcher.InvokeAsync(() =>
            {
                _lastProcSample = entries;
                RebuildProcessList();
            });
        }
    }

    /// <summary>Применяет поиск, группировку, сортировку и лимит к последней выборке.</summary>
    private void RebuildProcessList()
    {
        var src = _lastProcSample;
        if (src.Count == 0) return;

        IEnumerable<ProcessRow> rows;

        if (_procGroup)
        {
            rows = src.GroupBy(p => p.Name)
                      .Select(g => new ProcessRow(
                          g.Key,
                          g.Sum(x => x.CpuPercent),
                          g.Sum(x => x.RamMb),
                          g.Count()));
        }
        else
        {
            rows = src.Select(p => new ProcessRow(p.Name, p.CpuPercent, p.RamMb, 1));
        }

        if (_procSearch.Length > 0)
            rows = rows.Where(r => r.Name.Contains(_procSearch, StringComparison.OrdinalIgnoreCase));

        rows = _procSort switch
        {
            "ram"  => rows.OrderByDescending(r => r.RamMb).ThenByDescending(r => r.Cpu),
            "name" => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _      => rows.OrderByDescending(r => r.Cpu).ThenByDescending(r => r.RamMb),
        };

        var list = rows.ToList();
        int total = list.Count;
        if (_procLimit > 0) list = [.. list.Take(_procLimit)];

        _processEntries.Clear();
        foreach (var r in list)
            _processEntries.Add(new ProcessViewModel(r));

        ProcCountLbl.Text = total == src.Count
            ? $"{list.Count} из {src.Count}"
            : $"{list.Count} из {total} (всего {src.Count})";
    }

    private void OnProcSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _procSearch = ProcSearchBox.Text ?? "";
        RebuildProcessList();
    }

    private void OnProcSortChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProcSortCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            _procSort = item.Tag?.ToString() ?? "cpu";
        RebuildProcessList();
    }

    private void OnProcGroupChanged(object sender, RoutedEventArgs e)
    {
        _procGroup = ProcGroupChk.IsChecked == true;
        RebuildProcessList();
    }

    private void OnProcLimitChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProcLimitCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int lim))
            _procLimit = lim;
        RebuildProcessList();
    }

    /// <summary>Клик по заголовку колонки — сортировка по ней.</summary>
    private void OnProcHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBlock tb) return;
        string tag = tb.Tag?.ToString() ?? "cpu";

        foreach (System.Windows.Controls.ComboBoxItem item in ProcSortCombo.Items)
        {
            if (item.Tag?.ToString() == tag) { ProcSortCombo.SelectedItem = item; break; }
        }
    }

    private void OnProcCopyName(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessViewModel vm) return;
        try { Clipboard.SetText(vm.RawName); } catch { }
    }

    private void OnProcFilterBySelected(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessViewModel vm) return;
        ProcSearchBox.Text = vm.RawName;
    }

    /// <summary>Двойной клик по разделителю — вернуть высоту блока по умолчанию.</summary>
    private void OnSplitterReset(object sender, MouseButtonEventArgs e)
    {
        RowTop.Height    = new GridLength(3, GridUnitType.Star);
        RowBottom.Height = new GridLength(1, GridUnitType.Star);
    }

    // ── Глобальные хоткеи ────────────────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeys.Attach(this);
        RebindHotkeys();
    }

    /// <summary>
    /// (Пере)регистрирует все шесть хоткеев по текущим настройкам. Вызывается при
    /// старте и повторно из <see cref="ApplyAllSettings"/> — смена комбинации в
    /// настройках действует сразу, без перезапуска NetAudit.
    /// </summary>
    private void RebindHotkeys()
    {
        const uint ctrlAlt = HotkeyManager.ModControl | HotkeyManager.ModAlt;
        var failed = new List<string>();

        void Bind(string slot, uint vk, Action action)
        {
            if (!_hotkeys.Bind(slot, ctrlAlt, vk, action))
                failed.Add(HotkeyLabel(vk));
        }

        Bind("overlay", _settings.HotkeyOverlayVk, ToggleOverlay);
        Bind("boost",   _settings.HotkeyBoostVk,   () => _ = ToggleGameBoostAsync());
        // Мышью оверлей не подвинуть — он сквозной, поэтому углы только хоткеями
        Bind("corner1", _settings.HotkeyCorner1Vk, () => _overlay?.SnapToCorner(1));
        Bind("corner2", _settings.HotkeyCorner2Vk, () => _overlay?.SnapToCorner(2));
        Bind("corner3", _settings.HotkeyCorner3Vk, () => _overlay?.SnapToCorner(3));
        Bind("corner4", _settings.HotkeyCorner4Vk, () => _overlay?.SnapToCorner(4));

        if (failed.Count > 0)
            AppendEventLog($"⚠ Хоткеи заняты другой программой: {string.Join(", ", failed)}", BrushYellow);
    }

    private static string HotkeyLabel(uint vk) => $"Ctrl+Alt+{(char)vk}";

    // ── Оверлей ──────────────────────────────────────────────────────────

    private void OnOverlayToggle(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void ToggleOverlay()
    {
        if (_overlay is null)
        {
            ShowOverlay();
            _settings.OverlayEnabled = true;
            _settings.Save();
        }
        else
        {
            HideOverlay();
            _settings.OverlayEnabled = false;
            _settings.Save();
        }
    }

    private void ShowOverlay()
    {
        _overlay = new OverlayWindow(_settings);
        _overlay.Closed += (_, _) =>
        {
            _overlay = null;
            UpdateOverlayButton(false);
        };
        _overlay.Show();
        UpdateOverlayButton(true);
    }

    private void HideOverlay()
    {
        _overlay?.Close();
        _overlay = null;
        UpdateOverlayButton(false);
    }

    private void UpdateOverlayButton(bool active)
    {
        _tray?.SetOverlayState(active);
        OverlayBtn.Content    = active ? "◉ Оверлей" : "◌ Оверлей";
        OverlayBtn.Foreground = active
            ? new SolidColorBrush(WpfColor.FromRgb(0x4F, 0xD9, 0x8B))
            : new SolidColorBrush(WpfColor.FromRgb(0x5C, 0x7A, 0x69));
    }

    // ── Проверка обновлений ───────────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        string url = _settings.EffectiveUpdateCheckUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        var current = System.Reflection.Assembly
                          .GetExecutingAssembly().GetName().Version ?? new System.Version(1, 0);
        var info = await UpdateChecker.CheckAsync(url, current).ConfigureAwait(false);
        if (info is null) return;

        _updateDownloadUrl = info.DownloadUrl ?? "";
        _updateSha256      = info.Sha256;
        Dispatcher.Invoke(() =>
        {
            UpdateBannerText.Text = $"Доступно обновление {info.Version}" +
                (string.IsNullOrWhiteSpace(info.Notes) ? "" : $" — {info.Notes}");
            UpdateBanner.Visibility = Visibility.Visible;

            // Баннер в окне никто не увидит, пока приложение свёрнуто в трей или
            // спрятано игровым режимом — а именно так оно обычно и работает после
            // первого запуска. Всплывающее уведомление у значка добьёт до пользователя
            // в любом состоянии окна
            _tray?.ShowBalloon("Доступно обновление NetAudit",
                $"Версия {info.Version}. Двойной клик по значку → «Тесты и сервис» → «Проверить обновление».");
        });
    }

    private async void OnUpdateDownload(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_updateDownloadUrl)) return;

        var answer = MessageBox.Show(this,
            "Скачать и установить обновление автоматически?\n\n" +
            "NetAudit скачает архив, сверит контрольную сумму, заменит файлы\n" +
            "и перезапустится. Настройки и логи сохранятся.\n\n" +
            "«Нет» — открыть страницу загрузки в браузере.",
            "Обновление NetAudit",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Cancel) return;

        if (answer == MessageBoxResult.No)
        {
            try { Process.Start(new ProcessStartInfo(_updateDownloadUrl) { UseShellExecute = true }); }
            catch { }
            return;
        }

        await InstallUpdateAsync();
    }

    private void OnUpdateDismiss(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void OnShortcutCreate(object sender, RoutedEventArgs e)
    {
        if (DesktopShortcut.Create(out string error))
        {
            AppendEventLog("✓ Ярлык на рабочем столе создан", BrushGreen);
        }
        else
        {
            MessageBox.Show(this, $"Не удалось создать ярлык:\n{error}", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _settings.ShortcutOffered = true;
        _settings.Save();
        ShortcutBanner.Visibility = Visibility.Collapsed;
    }

    private void OnShortcutDismiss(object sender, RoutedEventArgs e)
    {
        _settings.ShortcutOffered = true;
        _settings.Save();
        ShortcutBanner.Visibility = Visibility.Collapsed;
    }

    private void ApplyLogVisibility()
    {
        bool on = _settings.LogEnabled;
        LogList.Visibility       = on ? Visibility.Visible  : Visibility.Collapsed;
        LogDisabledMsg.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyGraphVisibility()
    {
        bool gw  = _settings.ShowGatewayGraph;
        bool cf  = _settings.ShowCloudflareGraph;
        bool net = _settings.ShowNetworkGraph;
        bool cpu = _settings.ShowCpuGraph;
        bool ram = _settings.ShowRamGraph;

        GwGraphPanel.Visibility = gw ? Visibility.Visible : Visibility.Collapsed;
        RowGwGraph.Height = gw ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        CfGraphPanel.Visibility = cf ? Visibility.Visible : Visibility.Collapsed;
        RowCfGraph.Height = cf ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        NetGraphPanel.Visibility = net ? Visibility.Visible : Visibility.Collapsed;
        CpuGraphPanel.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;
        RamGraphPanel.Visibility = ram ? Visibility.Visible : Visibility.Collapsed;

        ColNet.Width  = net ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColSep1.Width = (net && cpu) ? new GridLength(8) : new GridLength(0);
        ColCpu.Width  = cpu ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColSep2.Width = (ram && (net || cpu)) ? new GridLength(8) : new GridLength(0);
        ColRam.Width  = ram ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        bool anySys = net || cpu || ram;
        RowSysGraphs.Height      = anySys ? new GridLength(170) : new GridLength(0);
        SysGraphsGrid.Visibility = anySys ? Visibility.Visible : Visibility.Collapsed;

        // Графики пинга — единственное, что тянет звёздную высоту верхней части.
        // Без них она сжимается по содержимому, а свободное место отдаём нижнему
        // блоку, иначе посреди окна зияет пустота.
        if (gw || cf)
        {
            if (RowTop.Height.IsAuto)
                RowTop.Height = new GridLength(3, GridUnitType.Star);
        }
        else
        {
            RowTop.Height = GridLength.Auto;
        }
    }

    // ── Рендер-луп ────────────────────────────────────────────────────────

    /// <summary>Перерисовать график. Автомасштаб пропускаем, пока пользователь держит зум.</summary>
    private void RedrawPlot(ScottPlot.WPF.WpfPlot plot)
    {
        if (!IsZoomed(plot))
            plot.Plot.Axes.AutoScale();
        plot.Refresh();
    }

    private async Task RefreshPlotsLoopAsync()
    {
        // 2 Гц вместо 20. Пинг приходит 4 раза в секунду, но график с окном
        // в 10 минут перерисовывать чаще двух раз в секунду глазу незачем,
        // а стоит это дорого: отрисовка пяти графиков — главная статья расхода CPU.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync())
        {
            if (!IsLoaded) break;

            FlushLog();

            // Рисовать невидимое окно незачем. Свёрнутого мало: чаще окно не свёрнуто,
            // а просто закрыто игрой на весь экран — WindowState при этом остаётся Normal,
            // и без проверки игрового режима графики продолжали бы перерисовываться впустую
            if (UiIdle) continue;

            if (_gwStreamer.HasNewData && _settings.ShowGatewayGraph)
                RedrawPlot(PlotGateway);
            if (_cfStreamer.HasNewData && _settings.ShowCloudflareGraph)
                RedrawPlot(PlotCloudflare);
            if ((_rxStreamer.HasNewData || _txStreamer.HasNewData) && _settings.ShowNetworkGraph)
                RedrawPlot(PlotNet);
            if (_cpuStreamer.HasNewData && _settings.ShowCpuGraph)
                RedrawPlot(PlotCpu);
            if (_ramStreamer.HasNewData && _settings.ShowRamGraph)
                RedrawPlot(PlotRam);
        }
    }

    // ── Завершение ────────────────────────────────────────────────────────

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Крестик прячет окно в трей — мониторинг продолжается. Настоящий выход
        // только через меню значка, иначе случайный клик тихо выключал бы прибор
        if (TryHideInsteadOfClose())
        {
            e.Cancel = true;
            return;
        }

        _uiCts.Cancel();
        _testCts?.Cancel();
        if (_gameBoost.Active) { try { await _gameBoost.RevertAsync(); } catch { } }
        ShutdownGameMode();
        ShutdownTray();
        _hotkeys.Dispose();
        _overlay?.Close();
        if (_scheduler is not null)    await _scheduler.DisposeAsync();
        if (_sysScheduler is not null) await _sysScheduler.DisposeAsync();
    }

    // ── Вспомогательные ───────────────────────────────────────────────────

    private static SolidColorBrush RttBrush(double? rtt, double warn, double bad)
    {
        if (rtt is null)  return BrushRed;
        if (rtt < warn)   return BrushGreen;
        if (rtt < bad)    return BrushYellow;
        return BrushRed;
    }

    private static void UpdateStatsUi(
        (long sent, long lost, double min, double avg, double max, double jitter, long consecutive) s,
        System.Windows.Controls.TextBlock minTb,
        System.Windows.Controls.TextBlock avgTb,
        System.Windows.Controls.TextBlock maxTb,
        System.Windows.Controls.TextBlock jitterTb,
        System.Windows.Controls.TextBlock availTb,
        System.Windows.Controls.TextBlock lossTb,
        System.Windows.Controls.TextBlock consecTb,
        System.Windows.Controls.TextBlock pktsTb)
    {
        long recv    = s.sent - s.lost;
        double lossP = s.sent > 0 ? s.lost  * 100.0 / s.sent : 0;
        double availP = 100.0 - lossP;

        minTb.Text    = s.min < double.MaxValue ? $"{s.min:F1} мс" : "—";
        avgTb.Text    = recv > 0 ? $"{s.avg:F1} мс" : "—";
        maxTb.Text    = recv > 0 ? $"{s.max:F1} мс" : "—";
        jitterTb.Text = recv > 1 ? $"{s.jitter:F1} мс" : "—";

        availTb.Text      = s.sent > 0 ? $"{availP:F2}%" : "—";
        availTb.Foreground = availP >= 99 ? BrushGreen : availP >= 95 ? BrushYellow : BrushRed;

        lossTb.Text      = $"{lossP:F2}%";
        lossTb.Foreground = lossP < 1 ? BrushGreen : lossP < 5 ? BrushYellow : BrushRed;

        consecTb.Text      = s.consecutive > 0 ? $"{s.consecutive}" : "—";
        consecTb.Foreground = s.consecutive == 0 ? BrushDim
                            : s.consecutive < 5  ? BrushYellow : BrushRed;

        pktsTb.Text = $"{recv} / {s.sent}";
    }

    // ── Модель строки диспетчера ─────────────────────────────────────────

    /// <summary>Строка диспетчера после группировки: Count — сколько процессов слито в одну.</summary>
    private readonly record struct ProcessRow(string Name, double Cpu, double RamMb, int Count);

    private sealed class ProcessViewModel(ProcessRow r)
    {
        private static readonly SolidColorBrush Green  = new(WpfColor.FromRgb(0x86, 0xD9, 0x7A));
        private static readonly SolidColorBrush Yellow = new(WpfColor.FromRgb(0xDF, 0xC4, 0x6A));
        private static readonly SolidColorBrush Red    = new(WpfColor.FromRgb(0xEE, 0x8B, 0x8B));
        private static readonly SolidColorBrush Dim    = new(WpfColor.FromRgb(0x5C, 0x7A, 0x69));

        public string RawName { get; } = r.Name;
        public string Name    { get; } = r.Count > 1 ? $"{r.Name}  ×{r.Count}" : r.Name;
        public string CpuText { get; } = r.Cpu > 0.05 ? $"{r.Cpu:F1}%" : "—";
        public string RamText { get; } = $"{r.RamMb:F0}";
        public Brush  CpuColor { get; } = r.Cpu < 1  ? Dim
                                        : r.Cpu < 30 ? Green
                                        : r.Cpu < 70 ? Yellow : Red;

        /// <summary>Имя строки в дереве автоматизации — иначе диктор читает имя класса.</summary>
        public override string ToString() => $"{Name}, ЦП {CpuText}, память {RamText} МБ";
    }

    // ── Модель строки лога ────────────────────────────────────────────────

    private enum LogKind { Ping, Timeout, Spike, Event }

    private sealed class LogEntry(string text, Brush color, LogKind kind, string host)
    {
        public string  Text  { get; } = text;
        public Brush   Color { get; } = color;
        public LogKind Kind  { get; } = kind;
        public string  Host  { get; } = host;

        /// <summary>Имя строки в дереве автоматизации — иначе диктор читает имя класса.</summary>
        public override string ToString() => Text;
    }

    // ── Кольцевой буфер значений графика ──────────────────────────────────

    /// <summary>
    /// Зеркало данных DataStreamer. Пишется из потока проб, читается из UI —
    /// отсюда блокировка.
    /// </summary>
    private sealed class Ring(int capacity)
    {
        private readonly double[] _buf = new double[capacity];
        private readonly object   _lock = new();
        private int _next;
        private int _count;

        public int Count    { get { lock (_lock) return _count; } }
        public int Capacity => capacity;

        public void Add(double v)
        {
            lock (_lock)
            {
                _buf[_next] = v;
                _next = (_next + 1) % capacity;
                if (_count < capacity) _count++;
            }
        }

        /// <summary>Значение по позиции слева направо: 0 — самое старое, Count-1 — самое свежее.</summary>
        public bool TryGet(int i, out double v)
        {
            lock (_lock)
            {
                if (i < 0 || i >= _count) { v = double.NaN; return false; }
                int start = _count == capacity ? _next : 0;
                v = _buf[(start + i) % capacity];
                return true;
            }
        }
    }

    // ── Потоко-безопасная статистика ──────────────────────────────────────

    private sealed class PingStats
    {
        private readonly object _lock = new();
        private long   _sent;
        private long   _lost;
        private long   _consecutive;
        private double _min = double.MaxValue;
        private double _max;
        private double _sum;
        private double _prev = double.NaN;
        private double _jitterSum;
        private long   _jitterCount;

        public void Record(bool success, double? rtt)
        {
            lock (_lock)
            {
                _sent++;
                if (!success || rtt is null) { _lost++; _consecutive++; return; }
                _consecutive = 0;
                double v = rtt.Value;
                if (v < _min) _min = v;
                if (v > _max) _max = v;
                _sum += v;
                if (!double.IsNaN(_prev)) { _jitterSum += Math.Abs(v - _prev); _jitterCount++; }
                _prev = v;
            }
        }

        public (long sent, long lost, double min, double avg, double max, double jitter, long consecutive) Get()
        {
            lock (_lock)
            {
                long recv     = _sent - _lost;
                double avg    = recv > 0 ? _sum / recv : 0;
                double jitter = _jitterCount > 0 ? _jitterSum / _jitterCount : 0;
                return (_sent, _lost, _min, avg, _max, jitter, _consecutive);
            }
        }
    }
}
