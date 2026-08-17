using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using NetAudit.Core.GameBoost;

namespace NetAudit.App;

/// <summary>
/// Вкладка «Игровой режим»: авто-режим NetAudit (перенесён сюда из окна настроек)
/// и разгон системы перед игрой одной кнопкой.
/// </summary>
public partial class MainWindow
{
    private readonly GameBoostService _gameBoost = new();
    private readonly ObservableCollection<BoostProcessItem> _boostProcesses = [];

    public sealed class BoostProcessItem
    {
        public string Name    { get; set; } = "";
        public bool   Checked { get; set; }
    }

    private void SetupGameBoost()
    {
        ChkGameMode.IsChecked        = _settings.GameModeEnabled;
        ChkGamePriority.IsChecked    = _settings.GameModeLowerPriority;
        ChkGameSlowMetrics.IsChecked = _settings.GameModeSlowMetrics;
        ChkGameQuietLog.IsChecked    = _settings.GameModeQuietLog;
        UpdateGameModeSubAvailability();

        ChkBoostPower.IsChecked    = _settings.GameBoostPowerPlan;
        ChkBoostNotify.IsChecked   = _settings.GameBoostMuteNotify;
        ChkBoostEffects.IsChecked  = _settings.GameBoostVisualEffects;
        ChkBoostServices.IsChecked = _settings.GameBoostStopServices;
        ChkBoostPriority.IsChecked = _settings.GameBoostGamePriority;

        BoostProcessList.ItemsSource = _boostProcesses;
        foreach (string name in _settings.GameBoostCloseApps)
            _boostProcesses.Add(new BoostProcessItem { Name = name, Checked = true });

        UpdateBoostButton();
    }

    // ── Авто-режим ───────────────────────────────────────────────────────

    private void OnGameCheckChanged(object sender, RoutedEventArgs e)
    {
        _settings.GameModeEnabled       = ChkGameMode.IsChecked == true;
        _settings.GameModeLowerPriority = ChkGamePriority.IsChecked == true;
        _settings.GameModeSlowMetrics   = ChkGameSlowMetrics.IsChecked == true;
        _settings.GameModeQuietLog      = ChkGameQuietLog.IsChecked == true;
        _settings.Save();

        UpdateGameModeSubAvailability();
        ApplyGameModeSettings();
    }

    /// <summary>Подпункты имеют смысл только при включённом авто-режиме.</summary>
    private void UpdateGameModeSubAvailability()
    {
        bool on = ChkGameMode.IsChecked == true;
        ChkGamePriority.IsEnabled    = on;
        ChkGameSlowMetrics.IsEnabled = on;
        ChkGameQuietLog.IsEnabled    = on;
    }

    // ── Разгон: чекбоксы твиков ─────────────────────────────────────────

    private void OnBoostOptionChanged(object sender, RoutedEventArgs e)
    {
        _settings.GameBoostPowerPlan     = ChkBoostPower.IsChecked == true;
        _settings.GameBoostMuteNotify    = ChkBoostNotify.IsChecked == true;
        _settings.GameBoostVisualEffects = ChkBoostEffects.IsChecked == true;
        _settings.GameBoostStopServices  = ChkBoostServices.IsChecked == true;
        _settings.GameBoostGamePriority  = ChkBoostPriority.IsChecked == true;
        _settings.Save();
    }

    // ── Список процессов для закрытия ────────────────────────────────────

    private void OnBoostRefreshProcesses(object sender, RoutedEventArgs e)
    {
        var checkedNames = new HashSet<string>(
            _boostProcesses.Where(p => p.Checked).Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.MainWindowHandle == IntPtr.Zero) continue;
                if (string.IsNullOrWhiteSpace(proc.MainWindowTitle)) continue;
                if (proc.ProcessName.Equals("NetAudit.App", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(proc.ProcessName);
            }
            catch { }
        }

        // То, что пользователь уже отметил, сохраняем даже если сейчас не запущено
        foreach (var name in checkedNames) names.Add(name);

        _boostProcesses.Clear();
        foreach (var name in names)
            _boostProcesses.Add(new BoostProcessItem { Name = name, Checked = checkedNames.Contains(name) });
    }

    private void OnBoostProcessCheckChanged(object sender, RoutedEventArgs e)
    {
        _settings.GameBoostCloseApps = [.. _boostProcesses.Where(p => p.Checked).Select(p => p.Name)];
        _settings.Save();
    }

    // ── Включение / выключение ───────────────────────────────────────────

    private async void OnBoostToggle(object sender, RoutedEventArgs e) => await ToggleGameBoostAsync();

    private async Task ToggleGameBoostAsync()
    {
        BoostToggleBtn.IsEnabled = false;
        try
        {
            if (_gameBoost.Active)
            {
                var report = await _gameBoost.RevertAsync();
                LogBoostReport("Разгон выключен", report);
            }
            else
            {
                var opts = new GameBoostOptions(
                    HighPerfPowerPlan:      _settings.GameBoostPowerPlan,
                    MuteNotifications:      _settings.GameBoostMuteNotify,
                    DisableVisualEffects:   _settings.GameBoostVisualEffects,
                    StopBackgroundServices: _settings.GameBoostStopServices,
                    BoostGamePriority:      _settings.GameBoostGamePriority,
                    ProcessesToClose:       _settings.GameBoostCloseApps);

                var report = await _gameBoost.ApplyAsync(opts);
                LogBoostReport("Разгон включён", report);

                // Игра уже может идти в момент нажатия кнопки — приоритет ей поднимаем сразу,
                // а не только при следующем срабатывании авто-режима
                if (_settings.GameBoostGamePriority && _gameMode && _gameProcess.Length > 0)
                    _gameBoost.BoostGameProcessPriority(_gameProcess);
            }
        }
        catch (Exception ex)
        {
            AppendEventLog($"⚠ Разгон: {ex.Message}", BrushRed);
        }
        finally
        {
            UpdateBoostButton();
            BoostToggleBtn.IsEnabled = true;
        }
    }

    private void UpdateBoostButton()
    {
        BoostToggleBtn.Content = _gameBoost.Active ? "⏹ Выключить разгон" : "🚀 Включить разгон";
        _tray?.SetBoostState(_gameBoost.Active);
    }

    private void LogBoostReport(string title, GameBoostReport report)
    {
        var parts = new List<string>();
        if (report.Applied.Count > 0) parts.Add(string.Join("; ", report.Applied));
        if (report.Closed.Count  > 0) parts.Add("закрыты: " + string.Join(", ", report.Closed));

        string line = parts.Count > 0 ? $"🚀 {title}: {string.Join(" · ", parts)}" : $"🚀 {title}";
        AppendEventLog(line, BrushCyan);
        BoostStatusText.Text = line;
    }

    /// <summary>
    /// Один раз при старте: если прошлый сеанс включил разгон и не откатил его штатно
    /// (крах, kill), твики остались применены в системе. Без этой проверки план питания,
    /// уведомления и остановленные службы так и остались бы «разогнанными» навсегда.
    /// </summary>
    private async Task RecoverGameBoostAsync()
    {
        try
        {
            var report = await _gameBoost.RecoverIfDirtyAsync();
            if (report is null || report.Applied.Count == 0) return;

            AppendEventLog(
                "🚀 Прошлый сеанс закрылся с включённым разгоном — твики возвращены обратно: " +
                string.Join("; ", report.Applied), BrushYellow);
        }
        catch { }
    }
}
