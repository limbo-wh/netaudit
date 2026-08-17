using System.Windows;
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
