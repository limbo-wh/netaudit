using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using NetAudit.Core.Diagnostics;
using ScottPlot;
using ScottPlot.Plottables;

using CoreFmt = NetAudit.Core.Diagnostics.Fmt;

namespace NetAudit.App;

/// <summary>
/// Вкладка «Стресс-тест»: нагрузка с живыми графиками.
///
/// Отдельно от вкладки «Тесты и сервис» по существу дела, а не ради красоты.
/// Разовый тест — это «нажал и прочитал итог», а стресс-тест идёт часами, и
/// смотреть на него нужно именно в динамике: важна не конечная цифра, а как
/// менялись температура и скорость вычислений по ходу. Ступенька на графике
/// скорости в тот момент, когда температура упёрлась в потолок, — это и есть
/// троттлинг, увиденный своими глазами; в текстовом итоге он превращается в
/// одну строчку «падение 12%», по которой не понять, когда и почему.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<TestOutputLine> _stressLines = [];
    private CancellationTokenSource? _stressCts;

    /// <summary>Окно графиков: час при тике в секунду. Дальше точки уезжают влево.</summary>
    private const int StressBufferSize = 3600;

    private DataStreamer? _stCpuTempStream;
    private DataStreamer? _stGpuTempStream;
    private DataStreamer? _stCpuLoadStream;
    private DataStreamer? _stGpuLoadStream;
    private DataStreamer? _stCpuSpeedStream;
    private DataStreamer? _stGpuSpeedStream;

    private double _stMaxCpuTemp = double.NaN;
    private double _stMaxGpuTemp = double.NaN;

    private bool StressRunning => _stressCts is not null;

    private void SetupStressTab()
    {
        StressOutput.ItemsSource = _stressLines;

        ConfigureStressPlot(PlotStressTemp.Plot, "°C");
        ConfigureStressPlot(PlotStressLoad.Plot, "%");
        ConfigureStressPlot(PlotStressSpeed.Plot, "");

        _stCpuTempStream  = AddStream(PlotStressTemp,  "#E3B44F");
        _stGpuTempStream  = AddStream(PlotStressTemp,  "#5CD1C0");
        _stCpuLoadStream  = AddStream(PlotStressLoad,  "#E3B44F");
        _stGpuLoadStream  = AddStream(PlotStressLoad,  "#5CD1C0");
        _stCpuSpeedStream = AddStream(PlotStressSpeed, "#D4E8A8");
        _stGpuSpeedStream = AddStream(PlotStressSpeed, "#86D97A");

        // До первого запуска пределы по Y ставим осмысленные: пустой график с осью
        // «-10 … 10» у температуры выглядит так, будто датчик показывает минус
        PlotStressTemp.Plot.Axes.SetLimitsY(20, 100);
        PlotStressLoad.Plot.Axes.SetLimitsY(0, 105);
        PlotStressSpeed.Plot.Axes.SetLimitsY(0, 10);

        // Пределы осей оставлены на усмотрение DataStreamer. Попытка зафиксировать
        // ось Y у графика загрузки через ManageAxisLimits = false обошлась дорого:
        // этот флаг управляет и осью X тоже, а X у DataStreamer — номер слота в
        // кольцевом буфере. Без управления окно X оставалось на начальных значениях,
        // данные уходили в слоты за его пределами, и график загрузки оставался
        // пустым при работающем тесте — поймано на живом прогоне у владельца.

        StressGreeting();
        UpdateStressHint();
    }

    private static DataStreamer AddStream(ScottPlot.WPF.WpfPlot plot, string hex)
    {
        var s = plot.Plot.Add.DataStreamer(StressBufferSize);
        s.Color = ScottPlot.Color.FromHex(hex);
        s.LineWidth = 1.4f;
        s.ViewScrollLeft();
        return s;
    }

    private static void ConfigureStressPlot(Plot plot, string yLabel)
    {
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0C1611");
        plot.DataBackground.Color   = ScottPlot.Color.FromHex("#111F18");
        plot.Axes.Color(ScottPlot.Color.FromHex("#5C7A69"));
        plot.Grid.MajorLineColor    = ScottPlot.Color.FromHex("#1C3226");
        if (yLabel.Length > 0) plot.Axes.Left.Label.Text = yLabel;

        // Подписи нижней оси убраны намеренно. У DataStreamer координата X — это
        // номер слота в кольцевом буфере, и на экране он выглядит как «-11 … 11»:
        // числа, которые ничего не значат ни для времени, ни для чего-либо ещё.
        // Время теста показывают счётчики «Прошло/Осталось» слева
        plot.Axes.Bottom.TickLabelStyle.IsVisible = false;
        plot.Axes.Bottom.MajorTickStyle.Length = 0;
        plot.Axes.Bottom.MinorTickStyle.Length = 0;
    }

    private void StressGreeting()
    {
        EmitStress(TestLine.Head("Стресс-тест"));
        EmitStress(TestLine.Dim("Проверяется не скорость, а правильность: каждый блок вычислений и каждая"));
        EmitStress(TestLine.Dim("страница памяти сверяются с заранее известным верным ответом. Исправное"));
        EmitStress(TestLine.Dim("железо не ошибается ни разу, поэтому даже одно расхождение — это диагноз."));
        EmitStress(TestLine.Empty);
        EmitStress(TestLine.Dim("Слева выберите подсистемы и длительность, затем «Запустить»."));
    }

    private void UpdateStressHint()
    {
        bool elevated = NetAudit.Core.Probes.FpsProbe.IsElevated;

        StTempHint.Text = elevated ? "" : "  — нужны права администратора";
        StHintLbl.Text = elevated
            ? "Тест остановится сам при 95 °C. Прервать вручную можно в любой момент."
            : "Температуры не читаются: нет прав администратора. Тест выполнится, но "
            + "перегрев поймать будет нечем — перезапустите NetAudit от администратора.";
    }

    // ── Запуск ────────────────────────────────────────────────────────────

    private async void OnStressStart(object sender, RoutedEventArgs e)
    {
        if (StressRunning) return;

        if (TestRunning)
        {
            MessageBox.Show(this, "Сначала дождитесь конца теста на вкладке «Тесты и сервис».",
                            "NetAudit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var options = new StressOptions
        {
            Duration = TimeSpan.FromMinutes(SelectedStressTabMinutes),
            Cpu      = StCpuChk.IsChecked == true,
            Memory   = StMemChk.IsChecked == true,
            Gpu      = StGpuChk.IsChecked == true,
            Disk     = StDiskChk.IsChecked == true,
        };

        if (!options.Cpu && !options.Memory && !options.Gpu && !options.Disk)
        {
            MessageBox.Show(this, "Отметьте хотя бы одну подсистему для нагрузки.", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmStress(options)) return;

        _stressCts = new CancellationTokenSource();
        SetStressUiBusy(true);
        ResetStressPlots();

        // Progress создаётся здесь, в UI-потоке, поэтому Report сам возвращает нас в него
        var lines = new Progress<TestLine>(EmitStress);
        var ticks = new Progress<StressTick>(OnStressTick);

        try
        {
            EmitStress(TestLine.Empty);
            EmitStress(TestLine.Dim(new string('═', 72)));

            var test = new StressTest(options, _blackBox, ticks);
            await test.RunAsync(lines, _stressCts.Token);

            StStateLbl.Text = "завершён";
            AppendEventLog($"✓ Стресс-тест завершён ({options.Describe()})", BrushGreen);
        }
        catch (OperationCanceledException)
        {
            EmitStress(TestLine.Empty);
            EmitStress(TestLine.Warn("■ Остановлено пользователем"));
            StStateLbl.Text = "остановлен";
            AppendEventLog("■ Стресс-тест остановлен пользователем", BrushYellow);
        }
        catch (Exception ex)
        {
            EmitStress(TestLine.Empty);
            EmitStress(TestLine.Bad($"Тест прервался ошибкой: {ex.Message}"));
            StStateLbl.Text = "ошибка";
            AppendEventLog($"⚠ Стресс-тест завершился ошибкой: {ex.Message}", BrushRed);
        }
        finally
        {
            _stressCts?.Dispose();
            _stressCts = null;
            SetStressUiBusy(false);
        }
    }

    /// <summary>
    /// Спрашиваем явно: тест занимает машину целиком на десятки минут и греет
    /// её до предела. Случайно нажатая кнопка не должна этого запускать.
    /// </summary>
    private bool ConfirmStress(StressOptions options)
    {
        string warning =
            $"Нагрузка: {options.Describe()}.\n\n" +
            "Компьютер будет загружен полностью: он нагреется, вентиляторы выйдут на максимум,\n" +
            "остальные программы начнут подтормаживать. Это нормально и есть смысл теста.\n\n" +
            $"Тест остановится сам, если процессор дойдёт до {options.CpuTempLimitC} °C.\n" +
            "Прервать вручную можно кнопкой «Остановить» в любой момент.\n\n";

        if (options.Gpu)
            warning += "Видеокарта будет нагружена собственным вычислительным шейдером —\n" +
                       "это самая горячая часть теста.\n\n";

        if (!NetAudit.Core.Probes.FpsProbe.IsElevated)
            warning += "Внимание: без прав администратора температуры не читаются,\n" +
                       "и перегрев поймать будет нечем.\n\n";

        if (_gameMode)
            warning += "Сейчас запущена игра — нагружать машину параллельно с ней бессмысленно.\n\n";

        warning += "Запустить?";

        return MessageBox.Show(this, warning, "Стресс-тест", MessageBoxButton.OKCancel,
                               MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private int SelectedStressTabMinutes =>
        StDurationCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
        int.TryParse(item.Tag?.ToString(), out int v)
            ? v
            : 15;

    private void OnStressStop(object sender, RoutedEventArgs e)
    {
        _stressCts?.Cancel();
        StStateLbl.Text = "останавливаю…";
    }

    private void SetStressUiBusy(bool busy)
    {
        StStartBtn.IsEnabled = !busy;
        StStopBtn.IsEnabled  = busy;
        StCpuChk.IsEnabled   = !busy;
        StMemChk.IsEnabled   = !busy;
        StGpuChk.IsEnabled   = !busy;
        StDiskChk.IsEnabled  = !busy;
        StDurationCombo.IsEnabled = !busy;

        if (busy)
        {
            StStateLbl.Text = "идёт нагрузка";
            UpdateStressHint();
        }
    }

    // ── Живые данные ──────────────────────────────────────────────────────

    /// <summary>Приходит раз в секунду из теста, уже в UI-потоке (через Progress).</summary>
    private void OnStressTick(StressTick t)
    {
        // Графики. NaN — это «датчика нет»; DataStreamer рисует разрыв, и это честно:
        // ноль градусов и отсутствие датчика — разные вещи
        _stCpuTempStream?.Add(t.CpuTempC);
        _stGpuTempStream?.Add(t.GpuTempC);
        _stCpuLoadStream?.Add(t.CpuPercent);
        _stGpuLoadStream?.Add(t.GpuPercent);
        _stCpuSpeedStream?.Add(t.CpuMops);
        _stGpuSpeedStream?.Add(t.GpuGops);

        PlotStressTemp.Refresh();
        PlotStressLoad.Refresh();
        PlotStressSpeed.Refresh();

        // Плитки
        StCpuVal.Text     = Num(t.CpuPercent, 0, "%");
        StGpuVal.Text     = Num(t.GpuPercent, 0, "%");
        StCpuTempVal.Text = Num(t.CpuTempC, 0, "°C");
        StGpuTempVal.Text = Num(t.GpuTempC, 0, "°C");
        StSpeedVal.Text   = t.CpuMops > 0 ? $"{t.CpuMops:F0} млн" : "—";

        StCpuTempVal.Foreground = TempBrush(t.CpuTempC, 80, 90);
        StGpuTempVal.Foreground = TempBrush(t.GpuTempC, 80, 87);

        StErrVal.Text = t.TotalErrors.ToString();
        StErrVal.Foreground = t.TotalErrors == 0 ? BrushGreen : BrushRed;

        // Ход
        StProgress.Value  = t.Progress;
        StElapsedLbl.Text = Span(t.Elapsed);
        StRemainLbl.Text  = t.Remaining > TimeSpan.Zero ? Span(t.Remaining) : "—";

        if (!double.IsNaN(t.CpuTempC))
            _stMaxCpuTemp = double.IsNaN(_stMaxCpuTemp) ? t.CpuTempC : Math.Max(_stMaxCpuTemp, t.CpuTempC);
        if (!double.IsNaN(t.GpuTempC))
            _stMaxGpuTemp = double.IsNaN(_stMaxGpuTemp) ? t.GpuTempC : Math.Max(_stMaxGpuTemp, t.GpuTempC);

        StMaxCpuTempLbl.Text = Num(_stMaxCpuTemp, 0, "°C");
        StMaxGpuTempLbl.Text = Num(_stMaxGpuTemp, 0, "°C");
        StMemCheckedLbl.Text = t.MemBytesChecked > 0 ? CoreFmt.Bytes(t.MemBytesChecked) : "—";
    }

    private void ResetStressPlots()
    {
        foreach (var s in new[] { _stCpuTempStream, _stGpuTempStream, _stCpuLoadStream,
                                  _stGpuLoadStream, _stCpuSpeedStream, _stGpuSpeedStream })
            s?.Data.Clear();

        _stMaxCpuTemp = double.NaN;
        _stMaxGpuTemp = double.NaN;

        StProgress.Value = 0;
        StErrVal.Text = "0";
        StErrVal.Foreground = BrushGreen;
        StMaxCpuTempLbl.Text = "—";
        StMaxGpuTempLbl.Text = "—";
        StMemCheckedLbl.Text = "—";

        PlotStressTemp.Refresh();
        PlotStressLoad.Refresh();
        PlotStressSpeed.Refresh();
    }

    private static string Num(double v, int digits, string unit) =>
        double.IsNaN(v) ? "—" : $"{v.ToString("F" + digits)} {unit}";

    private static string Span(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                          : $"{t.Minutes:00}:{t.Seconds:00}";

    private static SolidColorBrush TempBrush(double t, double warn, double bad) =>
        double.IsNaN(t) ? BrushDim : t >= bad ? BrushRed : t >= warn ? BrushYellow : BrushGreen;

    // ── Вывод ─────────────────────────────────────────────────────────────

    private void EmitStress(TestLine line)
    {
        _stressLines.Add(new TestOutputLine(line));
        if (_stressLines.Count > TestOutputCapacity) _stressLines.RemoveAt(0);
        StressOutput.ScrollIntoView(_stressLines[^1]);
    }

    private void OnStressSave(object sender, RoutedEventArgs e)
    {
        if (_stressLines.Count == 0)
        {
            MessageBox.Show(this, "Нечего сохранять — тест ещё не запускался.", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Сохранить отчёт стресс-теста",
            Filter     = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            FileName   = $"netaudit_стресс_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            DefaultExt = ".txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllLines(dlg.FileName, _stressLines.Select(l => l.Text));
            EmitStress(TestLine.Good($"✓ Сохранено: {dlg.FileName}"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось сохранить файл:\n{ex.Message}", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
