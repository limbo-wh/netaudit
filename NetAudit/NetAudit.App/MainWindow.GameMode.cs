using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NetAudit.Core.GameMode;

namespace NetAudit.App;

/// <summary>
/// Игровой режим: пока на экране игра, NetAudit должен занимать как можно меньше.
///
/// Смысл всей затеи в том, что во время игры от приложения нужен ровно один
/// результат — цифры в оверлее и запись потерь в лог. Всё остальное — перерисовка
/// графиков, обновление двух десятков подписей, опрос процессов — работа в пустоту:
/// главное окно закрыто игрой, и смотреть на него всё равно некому.
///
/// Что при этом НЕ трогается: частота пинга. Ради неё приложение и запускают,
/// и разрежать её во время игры значило бы выключить прибор ровно тогда,
/// когда он нужен.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _gameModeTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool   _gameMode;
    private string _gameProcess = "";

    /// <summary>
    /// Сколько подряд одинаковых наблюдений нужно, чтобы переключить режим.
    /// Без этого alt-tab между игрой и браузером дёргал бы приоритет процесса
    /// туда-сюда каждые две секунды.
    /// </summary>
    private const int FlipThreshold = 2;
    private int _sameReadings;
    private bool _lastReading;

    private ProcessPriorityClass _normalPriority = ProcessPriorityClass.Normal;

    /// <summary>
    /// Окно не видно пользователю: свёрнуто, закрыто игрой или скрыто.
    /// В этом состоянии любая перерисовка — выброшенная работа.
    /// </summary>
    private bool UiIdle => _gameMode || WindowState == WindowState.Minimized || !IsVisible;

    private void SetupGameMode()
    {
        try { _normalPriority = Process.GetCurrentProcess().PriorityClass; }
        catch { }

        _gameModeTimer.Tick += OnGameModeTick;
        _gameModeTimer.Start();
    }

    private void OnGameModeTick(object? sender, EventArgs e)
    {
        if (!_settings.GameModeEnabled)
        {
            if (_gameMode) LeaveGameMode();
            return;
        }

        var state = GameModeDetector.Detect();

        // Своё же окно в полный экран игрой не считается
        bool detected = state.Active && !IsOwnWindow(state);

        if (detected == _lastReading) _sameReadings++;
        else { _lastReading = detected; _sameReadings = 1; }

        if (_sameReadings < FlipThreshold) return;

        if (detected && !_gameMode) EnterGameMode(state);
        else if (!detected && _gameMode) LeaveGameMode();
    }

    private static bool IsOwnWindow(GameModeState state) =>
        state.ProcessName.Equals("NetAudit.App", StringComparison.OrdinalIgnoreCase);

    private void EnterGameMode(GameModeState state)
    {
        _gameMode    = true;
        _gameProcess = state.ProcessName;

        if (_settings.GameModeLowerPriority) SetPriority(ProcessPriorityClass.BelowNormal);
        if (_sysScheduler is not null) _sysScheduler.SlowMode = _settings.GameModeSlowMetrics;

        GameModeText.Text        = _gameProcess.Length > 0
            ? $"🎮 игровой режим · {_gameProcess}"
            : "🎮 игровой режим";
        GameModeBadge.Visibility = Visibility.Visible;
        _tray?.SetGameMode(true, _gameProcess);

        string who = _gameProcess.Length > 0 ? $" ({_gameProcess})" : "";
        AppendEventLog($"🎮 Игровой режим включён{who} — {state.Reason}", BrushCyan);
    }

    private void LeaveGameMode()
    {
        _gameMode    = false;
        _gameProcess = "";

        SetPriority(_normalPriority);
        if (_sysScheduler is not null) _sysScheduler.SlowMode = false;

        GameModeBadge.Visibility = Visibility.Collapsed;
        _tray?.SetGameMode(false, "");
        AppendEventLog("🎮 Игровой режим выключен", BrushCyan);

        // Пока режим держался, графики не перерисовывались — освежаем разом
        RedrawAllPlots();
    }

    /// <summary>
    /// Настройки применяются мгновенно, в том числе прямо посреди игры.
    /// Ждать выхода из режима, чтобы галочка подействовала, было бы странно.
    /// </summary>
    private void ApplyGameModeSettings()
    {
        if (!_gameMode) return;

        SetPriority(_settings.GameModeLowerPriority ? ProcessPriorityClass.BelowNormal : _normalPriority);
        if (_sysScheduler is not null) _sysScheduler.SlowMode = _settings.GameModeSlowMetrics;
    }

    private void SetPriority(ProcessPriorityClass priority)
    {
        try { Process.GetCurrentProcess().PriorityClass = priority; }
        catch { /* приоритет — оптимизация, а не функция: не вышло, значит не вышло */ }
    }

    private void RedrawAllPlots()
    {
        if (_settings.ShowGatewayGraph)    RedrawPlot(PlotGateway);
        if (_settings.ShowCloudflareGraph) RedrawPlot(PlotCloudflare);
        if (_settings.ShowNetworkGraph)    RedrawPlot(PlotNet);
        if (_settings.ShowCpuGraph)        RedrawPlot(PlotCpu);
        if (_settings.ShowRamGraph)        RedrawPlot(PlotRam);
    }

    private void ShutdownGameMode()
    {
        _gameModeTimer.Stop();
        if (_gameMode) SetPriority(_normalPriority);
    }
}
