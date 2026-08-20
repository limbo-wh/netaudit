using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NetAudit.App;

public partial class SettingsWindow : Window
{
    /// <summary>Одна строка перетаскиваемого списка метрик оверлея.</summary>
    private sealed class MetricItem(string key, string label, string tooltip, bool visible) : INotifyPropertyChanged
    {
        public string Key     { get; } = key;
        public string Label   { get; } = label;
        public string Tooltip { get; } = tooltip;

        private bool _visible = visible;
        public bool Visible
        {
            get => _visible;
            set { _visible = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<MetricItem> _metrics = [];
    private Point _metricDragStart;
    private MetricItem? _metricDragItem;

    /// <summary>Кандидаты для хоткеев: A-Z и 0-9. Значения совпадают с виртуальными кодами клавиш Windows.</summary>
    private static readonly (string Label, uint Vk)[] HotkeyKeyOptions =
        [.. Enumerable.Range('A', 26).Select(c => (((char)c).ToString(), (uint)c))
                       .Concat(Enumerable.Range('0', 10).Select(c => (((char)c).ToString(), (uint)c)))];

    /// <summary>
    /// Заполняет комбобокс явными ComboBoxItem с Tag = код клавиши — тот же приём, что уже
    /// используется для CmbFontSize. ItemsSource + DisplayMemberPath здесь не подошли: кастомный
    /// ControlTemplate ComboBox в Theme.xaml корректно показывает SelectionBoxItem для обычных
    /// ComboBoxItem, но с DisplayMemberPath по произвольному классу вместо текста показывал
    /// ToString() типа целиком — проверено живым запуском.
    /// </summary>
    private static void FillHotkeyCombo(ComboBox cmb, uint currentVk)
    {
        cmb.Items.Clear();
        foreach (var (label, vk) in HotkeyKeyOptions)
        {
            var item = new ComboBoxItem { Content = label, Tag = vk };
            cmb.Items.Add(item);
            if (vk == currentVk) cmb.SelectedItem = item;
        }
    }

    private static uint? SelectedVk(ComboBox cmb) =>
        cmb.SelectedItem is ComboBoxItem item && item.Tag is uint vk ? vk : null;

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

        // Хоткеи
        FillHotkeyCombo(CmbHkOverlay, _settings.HotkeyOverlayVk);
        FillHotkeyCombo(CmbHkBoost,   _settings.HotkeyBoostVk);
        FillHotkeyCombo(CmbHkCorner1, _settings.HotkeyCorner1Vk);
        FillHotkeyCombo(CmbHkCorner2, _settings.HotkeyCorner2Vk);
        FillHotkeyCombo(CmbHkCorner3, _settings.HotkeyCorner3Vk);
        FillHotkeyCombo(CmbHkCorner4, _settings.HotkeyCorner4Vk);

        // Метрики — порядок из настроек (или по умолчанию), видимость по ключу
        _metrics.Clear();
        foreach (var key in OverlayMetrics.Normalize(_settings.OverlayMetricOrder))
        {
            var (_, label, tooltip) = Array.Find(OverlayMetrics.Catalog, c => c.Key == key);
            var item = new MetricItem(key, label, tooltip, GetMetricVisible(key));
            item.PropertyChanged += (_, __) => ApplyLive();
            _metrics.Add(item);
        }
        MetricsOrderList.ItemsSource = _metrics;

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

        // Обновления
        ChkBetaUpdates.IsChecked = _settings.UseBetaUpdates;
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
            ChkTray, ChkMinimizeToTray, ChkCloseToTray, ChkAutoStart, ChkStartMinimized,
            ChkBetaUpdates,
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

    private void OnHotkeyChanged(object sender, SelectionChangedEventArgs e) => ApplyLive();

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

        if (SelectedVk(CmbHkOverlay) is uint hkOverlay) _settings.HotkeyOverlayVk = hkOverlay;
        if (SelectedVk(CmbHkBoost)   is uint hkBoost)   _settings.HotkeyBoostVk   = hkBoost;
        if (SelectedVk(CmbHkCorner1) is uint hkC1)      _settings.HotkeyCorner1Vk = hkC1;
        if (SelectedVk(CmbHkCorner2) is uint hkC2)      _settings.HotkeyCorner2Vk = hkC2;
        if (SelectedVk(CmbHkCorner3) is uint hkC3)      _settings.HotkeyCorner3Vk = hkC3;
        if (SelectedVk(CmbHkCorner4) is uint hkC4)      _settings.HotkeyCorner4Vk = hkC4;

        foreach (var m in _metrics) SetMetricVisible(m.Key, m.Visible);
        _settings.OverlayMetricOrder = [.. _metrics.Select(m => m.Key)];

        _settings.TrayEnabled    = ChkTray.IsChecked == true;
        _settings.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
        _settings.CloseToTray    = ChkCloseToTray.IsChecked == true;
        _settings.StartMinimized = ChkStartMinimized.IsChecked == true;
        _settings.AutoStart      = ChkAutoStart.IsChecked == true;

        _settings.UseBetaUpdates = ChkBetaUpdates.IsChecked == true;
    }

    // ── Метрики оверлея: видимость по ключу ─────────────────────────────────
    // Ключи per-метрика вместо словаря — bool-поля AppSettings уже разложены по
    // отдельным свойствам ради читаемого settings.json, ломать это не стали.

    private bool GetMetricVisible(string key) => key switch
    {
        "Fps"     => _settings.OvShowFps,
        "Cpu"     => _settings.OvShowCpu,
        "Gpu"     => _settings.OvShowGpu,
        "CpuTemp" => _settings.OvShowCpuTemp,
        "GpuTemp" => _settings.OvShowGpuTemp,
        "Ram"     => _settings.OvShowRam,
        "NetRx"   => _settings.OvShowNetRx,
        "NetTx"   => _settings.OvShowNetTx,
        "GwPing"  => _settings.OvShowGwPing,
        "GwLoss"  => _settings.OvShowGwLoss,
        "CfPing"  => _settings.OvShowCfPing,
        "CfLoss"  => _settings.OvShowCfLoss,
        _         => true,
    };

    private void SetMetricVisible(string key, bool visible)
    {
        switch (key)
        {
            case "Fps":     _settings.OvShowFps     = visible; break;
            case "Cpu":     _settings.OvShowCpu     = visible; break;
            case "Gpu":     _settings.OvShowGpu     = visible; break;
            case "CpuTemp": _settings.OvShowCpuTemp = visible; break;
            case "GpuTemp": _settings.OvShowGpuTemp = visible; break;
            case "Ram":     _settings.OvShowRam     = visible; break;
            case "NetRx":   _settings.OvShowNetRx   = visible; break;
            case "NetTx":   _settings.OvShowNetTx   = visible; break;
            case "GwPing":  _settings.OvShowGwPing  = visible; break;
            case "GwLoss":  _settings.OvShowGwLoss  = visible; break;
            case "CfPing":  _settings.OvShowCfPing  = visible; break;
            case "CfLoss":  _settings.OvShowCfLoss  = visible; break;
        }
    }

    // ── Метрики оверлея: перетаскивание ──────────────────────────────────────
    // Оверлей сам мышью не подвинуть и не нажать — он сквозной по построению
    // (см. stack.md). Порядок строк поэтому настраивается здесь, а не хватанием
    // прямо за оверлей.

    private void OnMetricRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _metricDragStart = e.GetPosition(null);
        _metricDragItem  = (e.OriginalSource as FrameworkElement)?.DataContext as MetricItem;
    }

    private void OnMetricRowMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _metricDragItem is null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _metricDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _metricDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        DragDrop.DoDragDrop(MetricsOrderList, _metricDragItem, DragDropEffects.Move);
    }

    private void OnMetricRowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnMetricRowDrop(object sender, DragEventArgs e)
    {
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as MetricItem;
        var source = _metricDragItem;
        _metricDragItem = null;

        if (source is null || target is null || ReferenceEquals(source, target)) return;

        int oldIndex = _metrics.IndexOf(source);
        int newIndex = _metrics.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0) return;

        _metrics.Move(oldIndex, newIndex);
        ApplyLive();
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
