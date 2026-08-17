using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NetAudit.Core.GameBoost;

/// <summary>
/// Разгон системы перед игрой одной кнопкой: план питания, уведомления Windows,
/// визуальные эффекты, службы SysMain/WSearch, приоритет процесса игры, закрытие
/// выбранных фоновых программ.
///
/// Состояние для отката пишется на диск сразу после каждого шага, а не только
/// хранится в памяти: при аварийном завершении процесса поля класса пропадают
/// вместе с ним, а применённые системные твики — нет. Тот же урок, что и с
/// осиротевшей ETW-сессией FPS (см. FpsProbe.StopStaleSession) — что бы ни
/// осталось после сбоя, следующий запуск обязан это заметить и вернуть как было.
/// </summary>
public sealed class GameBoostService
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetAudit", "gameboost-state.json");

    // Стандартный GUID схемы «Высокая производительность» — присутствует в Windows
    // всегда, даже если скрыт из панели управления.
    private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string ToastRegKey  = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    private static readonly string[] BoostServices = ["SysMain", "WSearch"];

    public bool Active { get; private set; }

    private int? _boostedPid;
    private ProcessPriorityClass _boostedPidOriginalPriority;

    private sealed class SavedState
    {
        public string?   PowerSchemeGuid   { get; set; }
        public int?      ToastEnabledValue { get; set; }
        public bool?     UiEffectsWasOn    { get; set; }
        public string[]  ServicesStopped   { get; set; } = [];
    }

    // ── Включение ────────────────────────────────────────────────────────

    public async Task<GameBoostReport> ApplyAsync(GameBoostOptions opts)
    {
        var report = new GameBoostReport();
        var state  = new SavedState();

        if (opts.HighPerfPowerPlan)
        {
            string? current = GetActivePowerScheme();
            if (current is not null && !current.Equals(HighPerfGuid, StringComparison.OrdinalIgnoreCase))
            {
                if (SetActivePowerScheme(HighPerfGuid))
                {
                    state.PowerSchemeGuid = current;
                    report.Applied.Add("план питания «Высокая производительность»");
                }
                else report.Skipped.Add("план питания — не удалось переключить");
            }
            else if (current is not null)
                report.Skipped.Add("план питания уже высокая производительность");
        }
        SaveState(state);

        if (opts.MuteNotifications)
        {
            int prev = GetToastEnabled();
            if (prev != 0)
            {
                SetToastEnabled(0);
                state.ToastEnabledValue = prev;
                report.Applied.Add("уведомления Windows выключены");
            }
            else report.Skipped.Add("уведомления уже выключены");
        }
        SaveState(state);

        if (opts.DisableVisualEffects)
        {
            bool prev = GetUiEffects();
            if (prev)
            {
                SetUiEffects(false);
                state.UiEffectsWasOn = true;
                report.Applied.Add("визуальные эффекты выключены");
            }
            else report.Skipped.Add("визуальные эффекты уже выключены");
        }
        SaveState(state);

        if (opts.StopBackgroundServices)
        {
            var running = BoostServices.Where(IsServiceRunning).ToArray();
            if (running.Length > 0)
            {
                bool ok = await RunElevatedServiceCommandAsync(running, stop: true).ConfigureAwait(false);
                if (ok)
                {
                    state.ServicesStopped = running;
                    report.Applied.Add($"остановлены службы: {string.Join(", ", running)}");
                }
                else report.Skipped.Add("службы — отменено или не удалось повысить права");
            }
            else report.Skipped.Add("службы уже остановлены");
        }
        SaveState(state);

        foreach (string name in opts.ProcessesToClose)
            if (CloseProcess(name)) report.Closed.Add(name);

        Active = true;
        return report;
    }

    // ── Выключение ───────────────────────────────────────────────────────

    public async Task<GameBoostReport> RevertAsync()
    {
        var report = new GameBoostReport();
        var state  = LoadState();
        if (state is not null) await RevertFromStateAsync(state, report).ConfigureAwait(false);

        RestoreGameProcessPriority();
        Active = false;
        DeleteState();
        return report;
    }

    /// <summary>
    /// Вызывать один раз при старте приложения. Если прошлый сеанс включил разгон
    /// и не откатил его штатно — файл состояния остался на диске, а значит и твики
    /// в системе всё ещё применены. Без этой проверки они остались бы навсегда.
    /// </summary>
    public async Task<GameBoostReport?> RecoverIfDirtyAsync()
    {
        var state = LoadState();
        if (state is null) return null;

        var report = new GameBoostReport();
        await RevertFromStateAsync(state, report).ConfigureAwait(false);
        DeleteState();
        return report;
    }

    private static async Task RevertFromStateAsync(SavedState state, GameBoostReport report)
    {
        if (state.PowerSchemeGuid is not null && SetActivePowerScheme(state.PowerSchemeGuid))
            report.Applied.Add("план питания возвращён");

        if (state.ToastEnabledValue is int toast)
        {
            SetToastEnabled(toast);
            report.Applied.Add("уведомления Windows включены");
        }

        if (state.UiEffectsWasOn == true)
        {
            SetUiEffects(true);
            report.Applied.Add("визуальные эффекты включены");
        }

        if (state.ServicesStopped.Length > 0)
        {
            bool ok = await RunElevatedServiceCommandAsync(state.ServicesStopped, stop: false).ConfigureAwait(false);
            report.Applied.Add(ok
                ? $"запущены службы: {string.Join(", ", state.ServicesStopped)}"
                : "не удалось запустить службы обратно — сделайте это вручную через services.msc");
        }
    }

    // ── Приоритет процесса игры ─────────────────────────────────────────

    public void BoostGameProcessPriority(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || _boostedPid is not null) return;
        try
        {
            var proc = Process.GetProcessesByName(processName).FirstOrDefault();
            if (proc is null) return;
            _boostedPidOriginalPriority = proc.PriorityClass;
            proc.PriorityClass = ProcessPriorityClass.AboveNormal;
            _boostedPid = proc.Id;
        }
        catch { /* повышение приоритета — удобство, не критично если не вышло */ }
    }

    public void RestoreGameProcessPriority()
    {
        if (_boostedPid is not { } pid) return;
        _boostedPid = null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.PriorityClass = _boostedPidOriginalPriority;
        }
        catch { }
    }

    // ── План питания ─────────────────────────────────────────────────────

    private static string? GetActivePowerScheme()
    {
        try
        {
            string output = RunCapture("powercfg", "/getactivescheme");
            var m = Regex.Match(output,
                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value : null;
        }
        catch { return null; }
    }

    private static bool SetActivePowerScheme(string guid)
    {
        try { return RunExit("powercfg", $"/setactive {guid}") == 0; }
        catch { return false; }
    }

    // ── Уведомления (toast) ─────────────────────────────────────────────

    private static int GetToastEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ToastRegKey);
            var val = key?.GetValue("ToastEnabled");
            return val is int i ? i : 1;   // ключа нет — уведомления включены по умолчанию
        }
        catch { return 1; }
    }

    private static void SetToastEnabled(int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ToastRegKey);
            key.SetValue("ToastEnabled", value, RegistryValueKind.DWord);
        }
        catch { }
    }

    // ── Визуальные эффекты ───────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    private const uint SpiGetUiEffects   = 0x103E;
    private const uint SpiSetUiEffects   = 0x103F;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange    = 0x02;

    private static bool GetUiEffects()
    {
        bool value = true;
        try { SystemParametersInfo(SpiGetUiEffects, 0, ref value, 0); } catch { }
        return value;
    }

    private static void SetUiEffects(bool on)
    {
        try
        {
            bool value = on;
            SystemParametersInfo(SpiSetUiEffects, 0, ref value, SpifUpdateIniFile | SpifSendChange);
        }
        catch { }
    }

    // ── Службы SysMain / WSearch ─────────────────────────────────────────

    private static bool IsServiceRunning(string name)
    {
        try { return RunCapture("sc", $"query {name}").Contains("RUNNING", StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>
    /// Один повышенный PowerShell на все службы разом — UAC спрашивается один раз,
    /// а не по разу на каждую службу.
    /// </summary>
    private static async Task<bool> RunElevatedServiceCommandAsync(IReadOnlyList<string> names, bool stop)
    {
        if (names.Count == 0) return true;

        string verb = stop ? "Stop-Service" : "Start-Service";
        string list = string.Join(",", names.Select(n => $"'{n}'"));
        string command = $"{verb} -Name {list} -Force -ErrorAction SilentlyContinue";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments       = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = true,
            Verb            = "runas",
            CreateNoWindow  = true,
            WindowStyle     = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;   // пользователь отменил UAC
        }
        catch { return false; }
    }

    // ── Закрытие выбранных программ ──────────────────────────────────────

    private static bool CloseProcess(string name)
    {
        try
        {
            string bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            var procs = Process.GetProcessesByName(bare);
            if (procs.Length == 0) return false;
            foreach (var p in procs)
                try { p.CloseMainWindow(); } catch { }
            return true;
        }
        catch { return false; }
    }

    // ── Состояние на диске ───────────────────────────────────────────────

    private static void SaveState(SavedState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private static SavedState? LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            return JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StateFile));
        }
        catch { return null; }
    }

    private static void DeleteState()
    {
        try { if (File.Exists(StateFile)) File.Delete(StateFile); } catch { }
    }

    // ── Запуск вспомогательных процессов ─────────────────────────────────

    private static string RunCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow          = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return "";
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return output;
    }

    private static int RunExit(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p is null) return -1;
        p.WaitForExit(3000);
        return p.ExitCode;
    }
}
