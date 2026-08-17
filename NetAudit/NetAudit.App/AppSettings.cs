using System.IO;
using System.Text.Json;

namespace NetAudit.App;

public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetAudit", "settings.json");

    public bool LogEnabled         { get; set; } = true;
    public bool LogOnlyImportant   { get; set; } = false;

    public bool ShowGatewayGraph    { get; set; } = true;
    public bool ShowCloudflareGraph { get; set; } = true;
    public bool ShowNetworkGraph    { get; set; } = true;
    public bool ShowCpuGraph        { get; set; } = true;
    public bool ShowRamGraph        { get; set; } = true;

    // Сырой файл version.json в ветке main. Пустая строка отключает проверку.
    public string UpdateCheckUrl { get; set; } =
        "https://raw.githubusercontent.com/limbo-wh/netaudit/main/NetAudit/version.json";

    // ── Оверлей ────────────────────────────────────────────────────────────
    public bool   OverlayEnabled  { get; set; } = false;
    public double OverlayLeft     { get; set; } = 20;
    public double OverlayTop      { get; set; } = 20;
    public double OverlayOpacity  { get; set; } = 0.85;
    public int    OverlayFontSize { get; set; } = 13;
    /// <summary>Строка FPS в оверлее. Работает только с правами администратора — см. FpsProbe.</summary>
    public bool   OvShowFps      { get; set; } = true;
    public bool   OvShowCpu      { get; set; } = true;
    public bool   OvShowGpu      { get; set; } = true;
    public bool   OvShowRam      { get; set; } = true;
    public bool   OvShowNetRx    { get; set; } = true;
    public bool   OvShowNetTx    { get; set; } = true;
    public bool   OvShowGwPing   { get; set; } = true;
    public bool   OvShowCfPing   { get; set; } = true;
    public bool   OvShowGwLoss   { get; set; } = true;
    public bool   OvShowCfLoss   { get; set; } = true;

    // ── Игровой режим ──────────────────────────────────────────────────────
    /// <summary>Замечать запуск игры и сокращать собственное потребление.</summary>
    public bool GameModeEnabled       { get; set; } = true;
    /// <summary>Понижать приоритет процесса, пока идёт игра.</summary>
    public bool GameModeLowerPriority { get; set; } = true;
    /// <summary>Реже опрашивать системные метрики во время игры.</summary>
    public bool GameModeSlowMetrics   { get; set; } = true;
    /// <summary>Не писать строки пинга в лог во время игры — только потери и спайки.</summary>
    public bool GameModeQuietLog      { get; set; } = true;
    /// <summary>
    /// Процессы (без .exe), которые НЕ считать игрой, даже если они на весь экран —
    /// например, плеер или редактор. Проверяется по имени из GameModeState.ProcessName.
    /// </summary>
    public List<string> GameModeExcludedProcesses { get; set; } = [];
    /// <summary>Баллон в трее при серии потерь пинга, пока идёт игра.</summary>
    public bool GameModeLossAlerts { get; set; } = true;

    // ── Разгон перед игрой (Game Boost) ─────────────────────────────────────
    public bool GameBoostPowerPlan     { get; set; } = true;
    public bool GameBoostMuteNotify    { get; set; } = true;
    public bool GameBoostVisualEffects { get; set; } = true;
    public bool GameBoostStopServices  { get; set; } = false;
    public bool GameBoostGamePriority  { get; set; } = true;
    /// <summary>Имена процессов (без .exe), которые разгон закрывает при включении.</summary>
    public List<string> GameBoostCloseApps { get; set; } = [];

    // ── Хоткеи ────────────────────────────────────────────────────────────
    // Модификатор всегда Ctrl+Alt (см. HotkeyManager) — настраивается только клавиша.
    // Значения — виртуальные коды клавиш Windows, для A-Z и 0-9 совпадают с ASCII.
    public uint HotkeyOverlayVk { get; set; } = 0x4F; // O
    public uint HotkeyBoostVk   { get; set; } = 0x42; // B
    public uint HotkeyCorner1Vk { get; set; } = 0x31; // 1
    public uint HotkeyCorner2Vk { get; set; } = 0x32; // 2
    public uint HotkeyCorner3Vk { get; set; } = 0x33; // 3
    public uint HotkeyCorner4Vk { get; set; } = 0x34; // 4

    // ── Трей и автозапуск ──────────────────────────────────────────────────
    /// <summary>Показывать значок в области уведомлений.</summary>
    public bool TrayEnabled     { get; set; } = true;
    /// <summary>Сворачивать в трей вместо панели задач.</summary>
    public bool MinimizeToTray  { get; set; } = true;
    /// <summary>Крестик прячет окно в трей, а не закрывает приложение.</summary>
    public bool CloseToTray     { get; set; } = true;
    /// <summary>
    /// Запускаться вместе с Windows. Хранится не здесь, а в реестре — в этом поле
    /// лежит лишь отражение состояния, чтобы окно настроек не лезло в реестр на каждый кадр.
    /// Истина всегда за <see cref="StartupManager"/>.
    /// </summary>
    public bool AutoStart       { get; set; } = false;
    /// <summary>При автозапуске сразу прятаться в трей, не показывая окна.</summary>
    public bool StartMinimized  { get; set; } = true;

    /// <summary>Баннер «создать ярлык на рабочем столе» уже показывали — второй раз не нужно.</summary>
    public bool ShortcutOffered { get; set; } = false;

    /// <summary>Снимок для отката: настройки применяются сразу, «Отмена» возвращает это состояние.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Вернуть значения из снимка в этот же экземпляр — ссылки на него уже розданы.</summary>
    public void CopyFrom(AppSettings s)
    {
        LogEnabled = s.LogEnabled;
        LogOnlyImportant = s.LogOnlyImportant;

        ShowGatewayGraph = s.ShowGatewayGraph;
        ShowCloudflareGraph = s.ShowCloudflareGraph;
        ShowNetworkGraph = s.ShowNetworkGraph;
        ShowCpuGraph = s.ShowCpuGraph;
        ShowRamGraph = s.ShowRamGraph;

        UpdateCheckUrl = s.UpdateCheckUrl;

        OverlayEnabled = s.OverlayEnabled;
        OverlayLeft = s.OverlayLeft;
        OverlayTop = s.OverlayTop;
        OverlayOpacity = s.OverlayOpacity;
        OverlayFontSize = s.OverlayFontSize;

        OvShowFps = s.OvShowFps;
        OvShowCpu = s.OvShowCpu;
        OvShowGpu = s.OvShowGpu;
        OvShowRam = s.OvShowRam;
        OvShowNetRx = s.OvShowNetRx;
        OvShowNetTx = s.OvShowNetTx;
        OvShowGwPing = s.OvShowGwPing;
        OvShowCfPing = s.OvShowCfPing;
        OvShowGwLoss = s.OvShowGwLoss;
        OvShowCfLoss = s.OvShowCfLoss;

        GameModeEnabled           = s.GameModeEnabled;
        GameModeLowerPriority     = s.GameModeLowerPriority;
        GameModeSlowMetrics       = s.GameModeSlowMetrics;
        GameModeQuietLog          = s.GameModeQuietLog;
        GameModeExcludedProcesses = [.. s.GameModeExcludedProcesses];
        GameModeLossAlerts        = s.GameModeLossAlerts;

        GameBoostPowerPlan     = s.GameBoostPowerPlan;
        GameBoostMuteNotify    = s.GameBoostMuteNotify;
        GameBoostVisualEffects = s.GameBoostVisualEffects;
        GameBoostStopServices  = s.GameBoostStopServices;
        GameBoostGamePriority  = s.GameBoostGamePriority;
        GameBoostCloseApps     = [.. s.GameBoostCloseApps];

        HotkeyOverlayVk = s.HotkeyOverlayVk;
        HotkeyBoostVk   = s.HotkeyBoostVk;
        HotkeyCorner1Vk = s.HotkeyCorner1Vk;
        HotkeyCorner2Vk = s.HotkeyCorner2Vk;
        HotkeyCorner3Vk = s.HotkeyCorner3Vk;
        HotkeyCorner4Vk = s.HotkeyCorner4Vk;

        TrayEnabled    = s.TrayEnabled;
        MinimizeToTray = s.MinimizeToTray;
        CloseToTray    = s.CloseToTray;
        AutoStart      = s.AutoStart;
        StartMinimized = s.StartMinimized;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
