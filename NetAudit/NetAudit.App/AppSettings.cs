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

        GameModeEnabled       = s.GameModeEnabled;
        GameModeLowerPriority = s.GameModeLowerPriority;
        GameModeSlowMetrics   = s.GameModeSlowMetrics;
        GameModeQuietLog      = s.GameModeQuietLog;

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
