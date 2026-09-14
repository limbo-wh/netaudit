using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace NetAudit.App;

public partial class AboutWindow : Window
{
    /// <summary>Номер карты без пробелов — именно в таком виде он нужен в буфере обмена.</summary>
    private const string CardNumber = "2204240149215792";

    private readonly DispatcherTimer _copiedTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };

    public AboutWindow()
    {
        InitializeComponent();

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionLbl.Text = v is null ? "" : $"версия {v.Major}.{v.Minor}.{v.Build}";

        _copiedTimer.Tick += (_, _) =>
        {
            CopiedLbl.Visibility = Visibility.Hidden;
            _copiedTimer.Stop();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Окно не тянется, а его 700 точек высоты не влезают на экран ноутбука;
        // содержимое теперь в ScrollViewer, поэтому окно можно безопасно укоротить
        App.FitToScreen(this);
    }

    private void OnCopyCard(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CardNumber);
            CopiedLbl.Text       = "Номер скопирован в буфер обмена";
            CopiedLbl.Visibility = Visibility.Visible;
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }
        catch
        {
            // Буфер обмена бывает занят другим процессом — не повод падать
            CopiedLbl.Text       = "Не удалось скопировать, выделите номер вручную";
            CopiedLbl.Visibility = Visibility.Visible;
        }
    }

    private void OnLicenseClick(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            // UseShellExecute=false (умолчание в .NET Core) не откроет URL — только
            // подходящий exe напрямую. true отдаёт запуск оболочке Windows, как
            // двойной клик по ссылке
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* нет браузера по умолчанию или он не настроен — не повод падать */ }
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
