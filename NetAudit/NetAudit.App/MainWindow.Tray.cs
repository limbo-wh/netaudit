using System.Windows;

namespace NetAudit.App;

/// <summary>
/// Значок в трее и поведение окна при сворачивании и закрытии.
///
/// Логика тут одна и она важная: приложение имеет смысл ровно тогда, когда оно
/// работает непрерывно. Значок в трее нужен не для красоты, а чтобы окно можно было
/// убрать с глаз, не выключая мониторинг.
/// </summary>
public partial class MainWindow
{
    private TrayIcon? _tray;

    /// <summary>Пользователь выбрал «Выход» — закрытие настоящее, а не сворачивание.</summary>
    private bool _reallyExiting;

    private bool _closeHintShown;

    private void SetupTray()
    {
        if (!_settings.TrayEnabled) return;

        _tray = new TrayIcon();
        _tray.ShowRequested          += ToggleWindowFromTray;
        _tray.SettingsRequested      += OnTraySettings;
        _tray.OverlayToggleRequested += ToggleOverlay;
        _tray.BoostToggleRequested   += () => _ = ToggleGameBoostAsync();
        _tray.ExitRequested          += ExitFromTray;

        _tray.Visible = true;
        _tray.SetOverlayState(_settings.OverlayEnabled);
        _tray.SetWindowShown(true);

        StateChanged += OnWindowStateChanged;
    }

    /// <summary>Автозапуск стартует приложение сразу в трей — окно показывать не нужно.</summary>
    private void ApplyStartupVisibility()
    {
        if (!StartupManager.StartedHidden) return;

        if (_tray is null)
        {
            // Трей выключен, но нас попросили стартовать скрытыми: прятать окно
            // без единого способа его вернуть нельзя — сворачиваем в панель задач
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
        _tray.SetWindowShown(false);
        _tray.ShowBalloon("NetAudit работает",
                          "Мониторинг запущен и свёрнут в трей. Двойной клик по значку — открыть окно.");
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        if (_tray is null || !_settings.MinimizeToTray) return;

        Hide();
        _tray.SetWindowShown(false);
    }

    private void ToggleWindowFromTray()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();
            _tray?.SetWindowShown(false);
            return;
        }

        ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        // Порядок важен: сначала вернуть состояние, потом поднять окно, иначе
        // оно всплывает свёрнутым и выглядит как будто ничего не произошло
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;

        _tray?.SetWindowShown(true);

        // Пока окно было скрыто, графики не перерисовывались
        RedrawAllPlots();
    }

    private void OnTraySettings()
    {
        ShowFromTray();
        OnSettings(this, new RoutedEventArgs());
    }

    private void ExitFromTray()
    {
        _reallyExiting = true;
        Close();
    }

    /// <summary>
    /// Подсказка значка обновляется раз в секунду вместе с метриками: там держим
    /// самое нужное — задержку и потери, чтобы не открывать окно ради одного взгляда.
    /// </summary>
    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;

        string gw = _gwLastRtt.HasValue ? $"{_gwLastRtt:F0} мс" : "нет";
        string cf = _cfLastRtt.HasValue ? $"{_cfLastRtt:F0} мс" : "нет";

        var s = _cfStats.Get();
        double loss = s.sent > 0 ? s.lost * 100.0 / s.sent : 0;

        _tray.SetTooltip($"NetAudit · шлюз {gw} · сеть {cf} · потери {loss:F1}%");
    }

    private void ShutdownTray()
    {
        StateChanged -= OnWindowStateChanged;
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>
    /// Крестик прячет окно в трей, если так настроено. Настоящий выход — только
    /// через меню значка, иначе мониторинг тихо прекращался бы от случайного клика.
    /// </summary>
    private bool TryHideInsteadOfClose()
    {
        if (_reallyExiting) return false;
        if (_tray is null || !_settings.CloseToTray) return false;

        Hide();
        _tray.SetWindowShown(false);

        // Объясняем один раз за сеанс: на десятый раз это уже не подсказка, а помеха
        if (!_closeHintShown)
        {
            _closeHintShown = true;
            _tray.ShowBalloon("NetAudit продолжает работать",
                              "Окно свёрнуто в трей. Полностью закрыть — правый клик по значку → «Выход».");
        }
        return true;
    }
}
