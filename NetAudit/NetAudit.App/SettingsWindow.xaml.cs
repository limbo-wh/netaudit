using System.Windows;
using System.Windows.Controls;

namespace NetAudit.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppSettings _snapshot;   // состояние на момент открытия — для отката
    private readonly Action      _onApply;

    // Пока идёт первичная расстановка контролов, их события не должны
    // дёргать применение: значения ещё не собраны и затрут настройки
    private bool _loading = true;

    public SettingsWindow(AppSettings settings, Action onApply)
    {
        InitializeComponent();
        _settings = settings;

        // Синхронизировать до снимка: иначе «Вернуть как было» откатит автозапуск
        // к устаревшему значению из файла, а не к тому, что реально в реестре
        settings.AutoStart = StartupManager.IsEnabled();

        _snapshot = settings.Clone();
        _onApply  = onApply;

        LoadFromSettings();
        WireLiveApply();

        _loading = false;
    }

    // ── Заполнение контролов ──────────────────────────────────────────────

    private void LoadFromSettings()
    {
        // Лог
        ChkLogEnabled.IsChecked   = _settings.LogEnabled;
        ChkLogImportant.IsChecked = _settings.LogOnlyImportant;

        // Графики
        ChkShowGw.IsChecked  = _settings.ShowGatewayGraph;
        ChkShowCf.IsChecked  = _settings.ShowCloudflareGraph;
        ChkShowNet.IsChecked = _settings.ShowNetworkGraph;
        ChkShowCpu.IsChecked = _settings.ShowCpuGraph;
        ChkShowRam.IsChecked = _settings.ShowRamGraph;

        // Оверлей — вкл
        ChkOverlayEnabled.IsChecked = _settings.OverlayEnabled;

        // Прозрачность
        SliderOpacity.Value = Math.Clamp(_settings.OverlayOpacity, 0.15, 1.0);
        OpacityLabel.Text   = $"{SliderOpacity.Value * 100:F0}%";

        // Шрифт
        CmbFontSize.SelectedItem = null;
        foreach (ComboBoxItem item in CmbFontSize.Items)
        {
            if (item.Tag?.ToString() == _settings.OverlayFontSize.ToString())
            { CmbFontSize.SelectedItem = item; break; }
        }
        if (CmbFontSize.SelectedItem is null) CmbFontSize.SelectedIndex = 1;

        // Позиция оверлея
        SliderOverlayX.Maximum = SystemParameters.PrimaryScreenWidth  - 200;
        SliderOverlayY.Maximum = SystemParameters.PrimaryScreenHeight - 200;
        SliderOverlayX.Value   = Math.Clamp(_settings.OverlayLeft, 0, SliderOverlayX.Maximum);
        SliderOverlayY.Value   = Math.Clamp(_settings.OverlayTop,  0, SliderOverlayY.Maximum);
        OverlayXLabel.Text     = $"{SliderOverlayX.Value:F0} пкс";
        OverlayYLabel.Text     = $"{SliderOverlayY.Value:F0} пкс";

        // Метрики
        ChkOvFps.IsChecked    = _settings.OvShowFps;
        ChkOvCpu.IsChecked    = _settings.OvShowCpu;
        ChkOvGpu.IsChecked    = _settings.OvShowGpu;
        ChkOvRam.IsChecked    = _settings.OvShowRam;
        ChkOvNetRx.IsChecked  = _settings.OvShowNetRx;
        ChkOvNetTx.IsChecked  = _settings.OvShowNetTx;
        ChkOvGwPing.IsChecked = _settings.OvShowGwPing;
        ChkOvGwLoss.IsChecked = _settings.OvShowGwLoss;
        ChkOvCfPing.IsChecked = _settings.OvShowCfPing;
        ChkOvCfLoss.IsChecked = _settings.OvShowCfLoss;

        // Игровой режим
        ChkGameMode.IsChecked        = _settings.GameModeEnabled;
        ChkGamePriority.IsChecked    = _settings.GameModeLowerPriority;
        ChkGameSlowMetrics.IsChecked = _settings.GameModeSlowMetrics;
        ChkGameQuietLog.IsChecked    = _settings.GameModeQuietLog;
        UpdateGameModeAvailability();

        // Трей и автозапуск
        ChkTray.IsChecked            = _settings.TrayEnabled;
        ChkMinimizeToTray.IsChecked  = _settings.MinimizeToTray;
        ChkCloseToTray.IsChecked     = _settings.CloseToTray;
        ChkStartMinimized.IsChecked  = _settings.StartMinimized;

        // Истина об автозапуске — в реестре, а не в настройках: пользователь мог
        // выключить его через «Диспетчер задач», и галочка обязана это показать
        _settings.AutoStart   = StartupManager.IsEnabled();
        ChkAutoStart.IsChecked = _settings.AutoStart;
        UpdateTrayAvailability();
    }

    private void UpdateTrayAvailability()
    {
        bool tray = ChkTray.IsChecked == true;
        ChkMinimizeToTray.IsEnabled = tray;
        ChkCloseToTray.IsEnabled    = tray;
        ChkStartMinimized.IsEnabled = tray && ChkAutoStart.IsChecked == true;

        string? cmd = StartupManager.CurrentCommand();
        AutoStartHint.Text = cmd is null
            ? "Автозапуск выключен."
            : $"В автозагрузке: {cmd}";
    }

    /// <summary>Подпункты имеют смысл только при включённом режиме — иначе они вводят в заблуждение.</summary>
    private void UpdateGameModeAvailability()
    {
        bool on = ChkGameMode.IsChecked == true;
        ChkGamePriority.IsEnabled    = on;
        ChkGameSlowMetrics.IsEnabled = on;
        ChkGameQuietLog.IsEnabled    = on;
    }

    // ── Мгновенное применение ─────────────────────────────────────────────

    /// <summary>
    /// Подписывает все контролы на немедленное применение.
    /// Слайдеры уже имеют обработчики в XAML — они зовут ApplyLive сами.
    /// </summary>
    private void WireLiveApply()
    {
        CheckBox[] boxes =
        [
            ChkLogEnabled, ChkLogImportant,
            ChkShowGw, ChkShowCf, ChkShowNet, ChkShowCpu, ChkShowRam,
            ChkOverlayEnabled,
            ChkOvFps, ChkOvCpu, ChkOvGpu, ChkOvRam, ChkOvNetRx, ChkOvNetTx,
            ChkOvGwPing, ChkOvGwLoss, ChkOvCfPing, ChkOvCfLoss,
            ChkGameMode, ChkGamePriority, ChkGameSlowMetrics, ChkGameQuietLog,
            ChkTray, ChkMinimizeToTray, ChkCloseToTray, ChkAutoStart, ChkStartMinimized,
        ];

        foreach (var box in boxes)
        {
            box.Checked   += OnControlChanged;
            box.Unchecked += OnControlChanged;
        }

        CmbFontSize.SelectionChanged += OnControlChanged;
    }

    private void OnControlChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ChkGameMode)) UpdateGameModeAvailability();

        // Автозапуск живёт в реестре, а не в settings.json, поэтому его пишем отдельно
        if (ReferenceEquals(sender, ChkAutoStart) || ReferenceEquals(sender, ChkStartMinimized))
            ApplyAutoStart();

        if (ReferenceEquals(sender, ChkTray) || ReferenceEquals(sender, ChkAutoStart))
            UpdateTrayAvailability();

        ApplyLive();
    }

    private void ApplyAutoStart()
    {
        if (_loading) return;

        bool enable = ChkAutoStart.IsChecked == true;
        bool hidden = ChkStartMinimized.IsChecked == true && ChkTray.IsChecked == true;

        if (StartupManager.Apply(enable, hidden, out string error))
        {
            UpdateTrayAvailability();
            return;
        }

        MessageBox.Show(this,
            $"Не удалось изменить автозапуск:\n{error}",
            "NetAudit", MessageBoxButton.OK, MessageBoxImage.Warning);

        // Галочка обязана отражать действительность, а не намерение
        _loading = true;
        ChkAutoStart.IsChecked = StartupManager.IsEnabled();
        _loading = false;
        UpdateTrayAvailability();
    }

    /// <summary>Собрать значения, сохранить на диск и применить к живому окну.</summary>
    private void ApplyLive()
    {
        if (_loading) return;
        SaveToSettings();
        _settings.Save();
        _onApply();
    }

    // ── Слайдеры ──────────────────────────────────────────────────────────

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityLabel is not null)
            OpacityLabel.Text = $"{e.NewValue * 100:F0}%";
        ApplyLive();
    }

    private void OnOverlayXChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayXLabel is not null)
            OverlayXLabel.Text = $"{e.NewValue:F0} пкс";
        ApplyLive();
    }

    private void OnOverlayYChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayYLabel is not null)
            OverlayYLabel.Text = $"{e.NewValue:F0} пкс";
        ApplyLive();
    }

    // ── Сбор значений ─────────────────────────────────────────────────────

    private void SaveToSettings()
    {
        _settings.LogEnabled       = ChkLogEnabled.IsChecked == true;
        _settings.LogOnlyImportant = ChkLogImportant.IsChecked == true;

        _settings.ShowGatewayGraph    = ChkShowGw.IsChecked == true;
        _settings.ShowCloudflareGraph = ChkShowCf.IsChecked == true;
        _settings.ShowNetworkGraph    = ChkShowNet.IsChecked == true;
        _settings.ShowCpuGraph        = ChkShowCpu.IsChecked == true;
        _settings.ShowRamGraph        = ChkShowRam.IsChecked == true;

        _settings.OverlayEnabled = ChkOverlayEnabled.IsChecked == true;
        _settings.OverlayOpacity = SliderOpacity.Value;
        _settings.OverlayLeft    = SliderOverlayX.Value;
        _settings.OverlayTop     = SliderOverlayY.Value;

        if (CmbFontSize.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int fs))
            _settings.OverlayFontSize = fs;

        _settings.OvShowFps    = ChkOvFps.IsChecked == true;
        _settings.OvShowCpu    = ChkOvCpu.IsChecked == true;
        _settings.OvShowGpu    = ChkOvGpu.IsChecked == true;
        _settings.OvShowRam    = ChkOvRam.IsChecked == true;
        _settings.OvShowNetRx  = ChkOvNetRx.IsChecked == true;
        _settings.OvShowNetTx  = ChkOvNetTx.IsChecked == true;
        _settings.OvShowGwPing = ChkOvGwPing.IsChecked == true;
        _settings.OvShowGwLoss = ChkOvGwLoss.IsChecked == true;
        _settings.OvShowCfPing = ChkOvCfPing.IsChecked == true;
        _settings.OvShowCfLoss = ChkOvCfLoss.IsChecked == true;

        _settings.GameModeEnabled       = ChkGameMode.IsChecked == true;
        _settings.GameModeLowerPriority = ChkGamePriority.IsChecked == true;
        _settings.GameModeSlowMetrics   = ChkGameSlowMetrics.IsChecked == true;
        _settings.GameModeQuietLog      = ChkGameQuietLog.IsChecked == true;

        _settings.TrayEnabled    = ChkTray.IsChecked == true;
        _settings.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
        _settings.CloseToTray    = ChkCloseToTray.IsChecked == true;
        _settings.StartMinimized = ChkStartMinimized.IsChecked == true;
        _settings.AutoStart      = ChkAutoStart.IsChecked == true;
    }

    // ── Кнопки ────────────────────────────────────────────────────────────

    /// <summary>Откат к состоянию на момент открытия окна.</summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _settings.CopyFrom(_snapshot);
        _settings.Save();

        // Автозапуск в settings.json только отражается — откатывать надо реестр
        StartupManager.Apply(_snapshot.AutoStart,
                             _snapshot.StartMinimized && _snapshot.TrayEnabled,
                             out _);

        _onApply();
        DialogResult = false;
    }

    /// <summary>Закрыть. Всё уже применено и сохранено по ходу.</summary>
    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
