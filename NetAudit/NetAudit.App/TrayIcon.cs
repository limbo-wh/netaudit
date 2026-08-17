using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace NetAudit.App;

/// <summary>
/// Значок в области уведомлений.
///
/// Построен на NotifyIcon из WinForms: собственного значка трея у WPF нет, а писать
/// обёртку над Shell_NotifyIcon руками ради одного элемента незачем — WinForms уже
/// лежит в том же Windows Desktop Runtime, который нужен самому WPF.
///
/// Меню собирается один раз, а пункты, у которых меняется подпись, хранятся полями:
/// пересобирать меню на каждое открытие — лишний мусор для сборщика.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon      _icon;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _overlayItem;
    private readonly Forms.ToolStripMenuItem _gameModeItem;
    private readonly Forms.ToolStripMenuItem _boostItem;

    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? OverlayToggleRequested;
    public event Action? BoostToggleRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _showItem     = new Forms.ToolStripMenuItem("Показать окно");
        _overlayItem  = new Forms.ToolStripMenuItem("Оверлей: выключен");
        _gameModeItem = new Forms.ToolStripMenuItem("Игровой режим: нет") { Enabled = false };
        _boostItem    = new Forms.ToolStripMenuItem("🚀 Включить разгон");

        _showItem.Click    += (_, _) => ShowRequested?.Invoke();
        _overlayItem.Click += (_, _) => OverlayToggleRequested?.Invoke();
        _boostItem.Click    += (_, _) => BoostToggleRequested?.Invoke();

        var settingsItem = new Forms.ToolStripMenuItem("Настройки…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var exitItem = new Forms.ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            _showItem,
            new Forms.ToolStripSeparator(),
            _gameModeItem,
            _overlayItem,
            _boostItem,
            new Forms.ToolStripSeparator(),
            settingsItem,
            new Forms.ToolStripSeparator(),
            exitItem,
        ]);

        _icon = new Forms.NotifyIcon
        {
            Icon             = LoadIcon(),
            Text             = "NetAudit",
            Visible          = false,
            ContextMenuStrip = menu,
        };

        // Двойной клик — самый ожидаемый способ вернуть окно из трея
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    /// <summary>
    /// Иконка берётся из ресурсов сборки. Если ресурс почему-то не найден —
    /// подставляем системную: значок без картинки в трее выглядит как сбой.
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/NetAudit.ico", UriKind.Absolute);
            var res = System.Windows.Application.GetResourceStream(uri);
            if (res is not null)
            {
                using var stream = res.Stream;
                // Явные ширина и высота, а не Size: Size есть и в WPF, и в WinForms,
                // и перегрузка выбирается не та
                return new Icon(stream, 16, 16);
            }
        }
        catch { }

        return SystemIcons.Application;
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>Подпись пункта меню и всплывающая подсказка значка.</summary>
    public void SetOverlayState(bool on) =>
        _overlayItem.Text = on ? "Оверлей: включён" : "Оверлей: выключен";

    public void SetWindowShown(bool shown) =>
        _showItem.Text = shown ? "Свернуть в трей" : "Показать окно";

    public void SetGameMode(bool active, string process)
    {
        _gameModeItem.Text = active
            ? (process.Length > 0 ? $"Игровой режим: {process}" : "Игровой режим: да")
            : "Игровой режим: нет";
    }

    public void SetBoostState(bool active) =>
        _boostItem.Text = active ? "⏹ Выключить разгон" : "🚀 Включить разгон";

    /// <summary>Короткая сводка в подсказке значка. Windows режет её на 63 символах.</summary>
    public void SetTooltip(string text)
    {
        const int Limit = 63;
        _icon.Text = text.Length <= Limit ? text : text[..Limit];
    }

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText  = text;
            _icon.BalloonTipIcon  = warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000);
        }
        catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
