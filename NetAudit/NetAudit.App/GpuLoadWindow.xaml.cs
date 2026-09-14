using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NetAudit.Core.Diagnostics;
using NetAudit.Core.Probes;

namespace NetAudit.App;

/// <summary>
/// Окно с видимой нагрузкой на видеокарту — то, ради чего у FurMark на экране
/// крутится мохнатый бублик.
///
/// Картинка ничего не измеряет: видеокарта греется от расчёта кадра, вывод на
/// экран стоит доли процента. Смысл в наблюдаемости — видно, что нагрузка идёт,
/// и видно, ровно ли ложатся кадры.
///
/// Устройство: видеокарта рисует не в окно WPF, а в собственное дочернее окно
/// Win32, созданное здесь же. WPF рисует своё содержимое через ту же видеокарту,
/// и деление одной поверхности между ним и нашей цепочкой буферов кончается
/// миганием и потерей кадров. Отдельное дочернее окно разводит их полностью:
/// сверху остаются обычные подписи WPF, внутри — чистый Direct3D.
/// </summary>
public partial class GpuLoadWindow : Window
{
    private readonly GpuVisualLoad _load = new();
    private readonly TemperatureProbe _temp = new();
    private readonly GpuProbe _gpu = new();
    private readonly NvidiaLiveProbe _nvidia = new();
    private readonly DispatcherTimer _hardwareTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _elapsed = new();

    /// <summary>Удалось ли получить показания от самой карты, а не от счётчиков Windows.</summary>
    private bool _nvidiaLive;

    private RenderSurface? _surface;
    private bool _stopped;

    public GpuLoadWindow()
    {
        InitializeComponent();

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;

        _hardwareTimer.Tick += OnHardwareTick;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _surface = new RenderSurface();
        RenderHost.Children.Add(_surface);

        // Карты NVIDIA отдают свои показания сами и без прав администратора —
        // это и точнее счётчиков Windows, и работает в обычном запуске
        try { _nvidiaLive = _nvidia.Start(); } catch { }

        // Запасной путь для всех прочих карт. Датчики температуры требуют прав
        // администратора; без них строка покажет прочерк
        if (!_nvidiaLive)
        {
            try { _temp.Initialize(); } catch { }
            try { _gpu.Initialize(); } catch { }

            // Ватты берутся из nvidia-smi, у AMD и Intel такого источника нет.
            // Раньше плитка просто оставалась пустой, и это выглядело поломкой
            PowerVal.ToolTip = "Показатель доступен только для видеокарт NVIDIA: " +
                               "потребление отдаёт nvidia-smi, у карт AMD и Intel " +
                               "такого источника нет.";
        }

        // Дочернее окно создаётся при первом показе — до этого дескриптора нет
        Dispatcher.InvokeAsync(StartLoad, DispatcherPriority.Loaded);
    }

    private void StartLoad()
    {
        if (_surface?.ChildHandle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            HintText.Text = "Не удалось создать поверхность для отрисовки.";
            return;
        }

        int width = Math.Max(1, (int)_surface.ActualWidth);
        int height = Math.Max(1, (int)_surface.ActualHeight);

        _load.OnFrame = frame => Dispatcher.BeginInvoke(() => ShowFrame(frame));

        if (!_load.Start(hwnd, width, height))
        {
            HintText.Text = $"Нагрузка не запустилась: {_load.Error}";
            HintText.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0x8B, 0x8B));
            return;
        }

        _surface.SizeChangedByHost = (w, h) => _load.Resize(w, h);
        _elapsed.Start();
        _hardwareTimer.Start();
    }

    private void ShowFrame(GpuVisualFrame frame)
    {
        FpsVal.Text   = $"{frame.Fps:F0}";
        LowVal.Text   = $"{frame.OnePercentLowFps:F0}";
        FrameVal.Text = $"{frame.FrameMs:F1} мс";

        if (_load.FailedFrames > 0 && !_stopped)
        {
            HintText.Text = $"Кадры не рисуются ({_load.FailedFrames} сбоев): {_load.Error}";
            HintText.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0x8B, 0x8B));
        }
        else if (frame.Complexity < GpuVisualLoad.DefaultComplexity && !_stopped)
        {
            HintText.Text = "Видеокарта не тянет полную сцену — она упрощена, чтобы не " +
                            "довести драйвер до перезапуска.";
        }
    }

    private void OnHardwareTick(object? sender, EventArgs e)
    {
        var span = _elapsed.Elapsed;
        TimeVal.Text = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";

        double temperature = double.NaN;

        if (_nvidiaLive && _nvidia.Last is { HasData: true } live)
        {
            temperature = live.TemperatureC;

            LoadVal.Text = $"{live.Utilization:F0}%";

            PowerVal.Text = double.IsNaN(live.PowerWatts)
                ? "—"
                : double.IsNaN(live.PowerLimitWatts)
                    ? $"{live.PowerWatts:F0} Вт"
                    : $"{live.PowerWatts:F0} / {live.PowerLimitWatts:F0} Вт";
        }
        else
        {
            // Счётчик Windows занижает загрузку на коротких кадрах, но для карт
            // без nvidia-smi другого источника нет
            try { LoadVal.Text = $"{_gpu.Sample():F0}%"; } catch { }
            try { (_, temperature) = _temp.Sample(); } catch { }
        }

        ShowTemperature(temperature);
    }

    /// <summary>
    /// Порог аварийной остановки нагрузки. Прежние 90 °C обрывали работу почти
    /// сразу на картах, отдающих температуру горячей точки: у многих Radeon и у
    /// RTX 30 с памятью GDDR6X 90–100 °C — рабочая норма, а не авария. Какой
    /// датчик попадёт в показания, решает сама карта, поэтому порог поднят к
    /// верхней границе безопасного диапазона.
    /// </summary>
    private const double StopTemperatureC = 97;

    private void ShowTemperature(double gpuTemp)
    {
        if (double.IsNaN(gpuTemp))
        {
            TempVal.Text = "—";
            TempVal.Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x7A, 0x69));
            TempVal.ToolTip = "Датчик недоступен: нужны права администратора.";
            return;
        }

        TempVal.Text = $"{gpuTemp:F0} °C";
        TempVal.Foreground = new SolidColorBrush(
            gpuTemp >= 87 ? Color.FromRgb(0xEE, 0x8B, 0x8B) :
            gpuTemp >= 80 ? Color.FromRgb(0xDF, 0xC4, 0x6A) :
                            Color.FromRgb(0x86, 0xD9, 0x7A));

        // Доводить карту до предела незачем: нагрузка нужна как наблюдение,
        // а не как проверка на выживание
        if (gpuTemp >= StopTemperatureC && !_stopped)
        {
            StopLoad();
            HintText.Text = $"Остановлено: датчик видеокарты показал {gpuTemp:F0} °C " +
                            $"(порог {StopTemperatureC:F0} °C). Какой это датчик — ядро или " +
                            "горячая точка — зависит от модели карты, поэтому нормальные " +
                            "значения у разных карт разные.";
            HintText.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0x8B, 0x8B));
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopLoad();

    private void StopLoad()
    {
        if (_stopped) return;
        _stopped = true;

        _hardwareTimer.Stop();
        _elapsed.Stop();
        _load.Stop();

        StopBtn.IsEnabled = false;
        HintText.Text = "Нагрузка остановлена.";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _hardwareTimer.Stop();
        _load.Dispose();
        _nvidia.Dispose();
        _temp.Dispose();
        _gpu.Dispose();
        _surface?.Dispose();
    }

    // ── Поверхность для Direct3D ──────────────────────────────────────────

    /// <summary>
    /// Дочернее окно Win32 внутри разметки WPF. Своей отрисовки не имеет —
    /// существует только чтобы отдать видеокарте настоящий дескриптор окна.
    /// </summary>
    private sealed class RenderSurface : HwndHost
    {
        private const int WsChild = 0x40000000;
        private const int WsVisible = 0x10000000;
        private const int WsClipChildren = 0x02000000;
        private const int WsClipSiblings = 0x04000000;

        public IntPtr ChildHandle { get; private set; }

        /// <summary>Сообщает хозяину новый размер: цепочку буферов надо пересоздать.</summary>
        public Action<int, int>? SizeChangedByHost { get; set; }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            // Класс "static" — простейшее готовое окно без собственной логики
            IntPtr handle = CreateWindowEx(
                0, "static", null,
                WsChild | WsVisible | WsClipChildren | WsClipSiblings,
                0, 0, 1, 1,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            ChildHandle = handle;
            return new HandleRef(this, handle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
            ChildHandle = IntPtr.Zero;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            SizeChangedByHost?.Invoke(
                Math.Max(1, (int)info.NewSize.Width),
                Math.Max(1, (int)info.NewSize.Height));
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            int exStyle, string className, string? windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);
    }
}
