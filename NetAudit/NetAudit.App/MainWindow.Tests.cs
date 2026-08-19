using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using NetAudit.Core.Diagnostics;
using NetAudit.Core.Probes;

using WpfColor = System.Windows.Media.Color;
// В MainWindow уже есть свой Fmt для подписей на графиках — разводим псевдонимом
using CoreFmt  = NetAudit.Core.Diagnostics.Fmt;

namespace NetAudit.App;

/// <summary>
/// Вкладка «Тесты и сервис»: разовые проверки и сброс сети.
///
/// Вынесено в отдельный файл: обработчики главного окна и без того занимают
/// полторы тысячи строк, а тесты живут своей жизнью и почти ничего оттуда не трогают.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<TestOutputLine> _testLines = [];
    private CancellationTokenSource? _testCts;

    /// <summary>Сколько строк держим в выводе. Трассировка и полная диагностика дают под сотню.</summary>
    private const int TestOutputCapacity = 4000;

    private void SetupTestsTab()
    {
        TestOutput.ItemsSource = _testLines;
        UpdateResetHint();
        Greeting();
    }

    private void Greeting()
    {
        Emit(TestLine.Head("NetAudit — тесты и сервис"));
        Emit(TestLine.Dim("Выберите проверку слева. Результат появится здесь."));
        Emit(TestLine.Empty);
        Emit(TestLine.Dim("Сетевые тесты нагружают канал и на это время могут мешать играм и звонкам."));
        Emit(TestLine.Dim("Тест железа нагружает процессор целиком."));
    }

    // ── Запуск тестов ─────────────────────────────────────────────────────

    private bool TestRunning => _testCts is not null;

    private async void OnTestSpeed(object s, RoutedEventArgs e)   => await RunAsync(new SpeedTest());
    private async void OnTestDns(object s, RoutedEventArgs e)     => await RunAsync(new DnsTest());
    private async void OnTestMtu(object s, RoutedEventArgs e)     => await RunAsync(new MtuTest());
    private async void OnTestHardware(object s, RoutedEventArgs e) => await RunAsync(new SystemBenchTest());
    private async void OnTestCpu(object s, RoutedEventArgs e)     => await RunAsync(new SystemBenchTest(BenchParts.Cpu));

    private async void OnTestTrace(object s, RoutedEventArgs e)
    {
        // Трассируем туда, где точно есть ответ и где виден весь путь до интернета
        await RunAsync(new TracerouteTest("8.8.8.8"));
    }

    private async void OnTestFullNetwork(object s, RoutedEventArgs e)
    {
        await RunAsync(
            new SpeedTest(),
            new DnsTest(),
            new MtuTest(),
            new TracerouteTest("8.8.8.8"));
    }

    /// <summary>Выполняет тесты по очереди, отдавая строки в вывод по мере поступления.</summary>
    private async Task RunAsync(params IDiagnosticTest[] tests)
    {
        if (TestRunning)
        {
            Emit(TestLine.Warn("Тест уже выполняется — дождитесь конца или нажмите «Остановить»"));
            return;
        }

        _testCts = new CancellationTokenSource();
        SetTestUiBusy(true);

        // Progress создаётся в UI-потоке, поэтому Report сам возвращает нас в него
        var progress = new Progress<TestLine>(Emit);
        var sw = Stopwatch.StartNew();

        try
        {
            for (int i = 0; i < tests.Length; i++)
            {
                var test = tests[i];
                TestStatusLbl.Text = tests.Length > 1
                    ? $"Выполняется {i + 1} из {tests.Length}: {test.Title}"
                    : $"Выполняется: {test.Title}";

                if (i > 0) Emit(TestLine.Empty);
                Emit(TestLine.Dim(new string('═', 72)));

                await test.RunAsync(progress, _testCts.Token);
            }

            Emit(TestLine.Empty);
            Emit(TestLine.Good(sw.Elapsed.TotalSeconds < 1
                ? "✓ Готово меньше чем за секунду"
                : $"✓ Готово за {sw.Elapsed.TotalSeconds:F0} с"));
        }
        catch (OperationCanceledException)
        {
            Emit(TestLine.Empty);
            Emit(TestLine.Warn("■ Остановлено пользователем"));
        }
        catch (Exception ex)
        {
            Emit(TestLine.Empty);
            Emit(TestLine.Bad($"Тест прервался ошибкой: {ex.Message}"));
            AppendEventLog($"⚠ Тест завершился ошибкой: {ex.Message}", BrushRed);
        }
        finally
        {
            _testCts?.Dispose();
            _testCts = null;
            SetTestUiBusy(false);
        }
    }

    private void SetTestUiBusy(bool busy)
    {
        TestStopBtn.IsEnabled = busy;
        if (!busy) TestStatusLbl.Text = "Тест не запущен";
    }

    private void OnTestStop(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        TestStatusLbl.Text = "Останавливаю…";
    }

    // ── Вывод ─────────────────────────────────────────────────────────────

    private void Emit(TestLine line)
    {
        _testLines.Add(new TestOutputLine(line));
        if (_testLines.Count > TestOutputCapacity) _testLines.RemoveAt(0);

        // Прокрутка на каждой строке допустима: тесты дают единицы строк в секунду,
        // а не восемь, как лог пинга — там из-за этого и пришлось вводить пачки
        TestOutput.ScrollIntoView(_testLines[^1]);
    }

    private void OnTestClear(object sender, RoutedEventArgs e) => _testLines.Clear();

    private void OnTestCopy(object sender, RoutedEventArgs e)
    {
        if (_testLines.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, _testLines.Select(l => l.Text))); }
        catch { }
    }

    private void OnTestSave(object sender, RoutedEventArgs e)
    {
        if (_testLines.Count == 0)
        {
            MessageBox.Show(this, "Нечего сохранять — вывод пуст.", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Сохранить отчёт",
            Filter     = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            FileName   = $"netaudit_отчёт_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            DefaultExt = ".txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllLines(dlg.FileName, _testLines.Select(l => l.Text));
            Emit(TestLine.Good($"✓ Сохранено: {dlg.FileName}"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось сохранить файл:\n{ex.Message}", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Сброс сети ────────────────────────────────────────────────────────

    private ResetScope SelectedScope =>
        ResetScopeCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
        int.TryParse(item.Tag?.ToString(), out int v)
            ? (ResetScope)v
            : ResetScope.Caches;

    private void OnResetScopeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateResetHint();

    private void UpdateResetHint()
    {
        if (ResetScopeHint is null) return;
        ResetScopeHint.Text = NetworkResetService.Describe(SelectedScope);
    }

    private async void OnNetworkReset(object sender, RoutedEventArgs e)
    {
        if (TestRunning)
        {
            MessageBox.Show(this, "Сначала дождитесь конца текущего теста.", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var scope = SelectedScope;

        // Сброс рвёт связь и требует прав администратора — спрашиваем явно
        string warning = scope switch
        {
            ResetScope.Caches  => "Будут очищены кэши DNS, ARP и NetBIOS.\nСоединение не разорвётся.",
            ResetScope.Dhcp    => "Кэши плюс переполучение IP-адреса у роутера.\nСвязь прервётся на 2–5 секунд.",
            ResetScope.Adapter => "Кэши, новый адрес и перезапуск сетевого адаптера.\n" +
                                  "Связь прервётся на 5–15 секунд.\n\n" +
                                  "Если вы подключены удалённо — не делайте этого.",
            _                  => "Полный сброс Winsock и стека TCP/IP.\n\n" +
                                  "Подействует только после перезагрузки Windows.\n" +
                                  "Сбросятся сторонние сетевые надстройки: VPN, антивирусные фильтры,\n" +
                                  "виртуальные адаптеры. Их, возможно, придётся настроить заново.",
        };

        var answer = MessageBox.Show(this,
            warning + "\n\nПотребуются права администратора. Продолжить?",
            "Перезагрузка сети",
            MessageBoxButton.OKCancel,
            scope >= ResetScope.Adapter ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        BottomTabs.SelectedItem = TestTab;

        _testCts = new CancellationTokenSource();
        SetTestUiBusy(true);
        TestStatusLbl.Text = "Сброс сети…";
        ResetNetBtn.IsEnabled = false;

        string gatewayBefore = NetworkUtils.GetDefaultGateway() ?? "";
        var progress = new Progress<TestLine>(Emit);

        try
        {
            Emit(TestLine.Empty);
            Emit(TestLine.Dim(new string('═', 72)));
            await new NetworkResetService().RunAsync(scope, progress, _testCts.Token);
            AppendEventLog($"⟳ Выполнен сброс сети ({scope})", BrushYellow);

            await AfterResetAsync(gatewayBefore);
        }
        catch (NetworkResetService.CancelledByUserException)
        {
            // Строка про отмену уже выведена самим сервисом
        }
        catch (OperationCanceledException)
        {
            Emit(TestLine.Warn("■ Остановлено"));
        }
        catch (Exception ex)
        {
            Emit(TestLine.Bad($"Сброс не выполнился: {ex.Message}"));
        }
        finally
        {
            _testCts?.Dispose();
            _testCts = null;
            SetTestUiBusy(false);
            ResetNetBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// После сброса адрес шлюза мог смениться. Если это случилось, старый планировщик
    /// продолжил бы пинговать несуществующий адрес и показывал бы 100% потерь —
    /// поэтому пробы перезапускаются на новый адрес.
    /// </summary>
    private async Task AfterResetAsync(string gatewayBefore)
    {
        // Адаптеру нужно время подняться и получить адрес
        await Task.Delay(TimeSpan.FromSeconds(3));

        string gatewayNow = NetworkUtils.GetDefaultGateway() ?? "";

        if (gatewayNow.Length == 0)
        {
            Emit(TestLine.Bad("Шлюз не определяется — сеть ещё не поднялась или связь потеряна"));
            Emit(TestLine.Dim("Подождите 15–20 секунд. Если не восстановится — проверьте кабель и роутер."));
            return;
        }

        if (gatewayNow == gatewayBefore)
        {
            Emit(TestLine.Good($"Шлюз прежний: {gatewayNow}. Пробы продолжают идти."));
            return;
        }

        Emit(TestLine.Warn($"Шлюз сменился: {gatewayBefore} → {gatewayNow}. Перезапускаю пробы."));
        await RestartProbesAsync(gatewayNow);
        Emit(TestLine.Good("Пробы перезапущены на новый адрес"));
    }

    private async Task RestartProbesAsync(string gateway)
    {
        if (_scheduler is not null)
        {
            _scheduler.GatewayResult    -= OnGatewayResult;
            _scheduler.CloudflareResult -= OnCloudflareResult;
            await _scheduler.DisposeAsync();
        }

        _scheduler = new NetAudit.Core.Scheduler.ProbeScheduler(gateway, TimeSpan.FromMilliseconds(250));
        _scheduler.GatewayResult    += OnGatewayResult;
        _scheduler.CloudflareResult += OnCloudflareResult;
        _scheduler.Start();

        GatewayLabel.Text = $"Шлюз: {gateway}";
        AppendEventLog($"⟳ Шлюз сменился на {gateway}, пробы перезапущены", BrushYellow);
    }

    // ── Прочее ────────────────────────────────────────────────────────────

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        Emit(TestLine.Empty);
        Emit(TestLine.Head("Проверка обновлений"));

        string url = _settings.EffectiveUpdateCheckUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            Emit(TestLine.Warn("Адрес проверки не задан."));
            Emit(TestLine.Dim("Он появится, когда проект будет выложен на GitHub. До тех пор обновляться неоткуда."));
            return;
        }

        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                      ?? new Version(1, 0);
        Emit(TestLine.Dim($"Установлено: {current.Major}.{current.Minor}.{current.Build}"));
        Emit(TestLine.Dim($"Канал: {(_settings.UseBetaUpdates ? "бета" : "стабильный")}"));
        Emit(TestLine.Dim($"Источник: {url}"));

        try
        {
            var info = await NetAudit.Core.UpdateChecker.CheckAsync(url, current);
            if (info is null)
            {
                Emit(TestLine.Good("Установлена последняя версия"));
                return;
            }

            _updateDownloadUrl      = info.DownloadUrl ?? "";
            _updateSha256           = info.Sha256;
            UpdateBannerText.Text   = $"Доступно обновление {info.Version}" +
                (string.IsNullOrWhiteSpace(info.Notes) ? "" : $" — {info.Notes}");
            UpdateBanner.Visibility = Visibility.Visible;

            Emit(TestLine.Good($"Доступна версия {info.Version}"));
            if (!string.IsNullOrWhiteSpace(info.Notes)) Emit(TestLine.Info($"Что нового: {info.Notes}"));
            Emit(TestLine.Dim("Кнопка «Скачать» — в баннере вверху окна."));
        }
        catch (Exception ex)
        {
            Emit(TestLine.Bad($"Не удалось проверить: {ex.Message}"));
        }
    }

    /// <summary>
    /// Состояние счётчика кадров. Строка «—» в оверлее может означать три разные вещи,
    /// и без этой кнопки отличить их невозможно.
    /// </summary>
    private void OnFpsStatus(object sender, RoutedEventArgs e)
    {
        Emit(TestLine.Empty);
        Emit(TestLine.Head("Счётчик кадров (FPS)"));

        Emit(TestLine.Dim("Кадры считаются по событиям Present, которые Windows выдаёт через ETW —"));
        Emit(TestLine.Dim("тем же способом, что и PresentMon от Microsoft. В процесс игры ничего не"));
        Emit(TestLine.Dim("внедряется: перехваты и подгрузка своих библиотек в чужой процесс — это"));
        Emit(TestLine.Dim("то, за что античиты выдают блокировки, и здесь такого нет."));
        Emit(TestLine.Empty);

        Emit(TestLine.Info(CoreFmt.Row("Строка в оверлее", _settings.OvShowFps ? "включена" : "выключена")));
        Emit(TestLine.Info(CoreFmt.Row("Права администратора",
            NetAudit.Core.Probes.FpsProbe.IsElevated ? "есть" : "нет")));

        string status = _sysScheduler?.FpsStatus ?? "планировщик не запущен";
        bool ok = _sysScheduler?.FpsAvailable == true;
        Emit(new TestLine(CoreFmt.Row("Состояние счётчика", status), ok ? TestLevel.Good : TestLevel.Warn));

        Emit(TestLine.Empty);

        if (!_settings.OvShowFps)
        {
            Emit(TestLine.Warn("Включите «Кадры (FPS)» в настройках оверлея."));
            return;
        }

        if (!NetAudit.Core.Probes.FpsProbe.IsElevated)
        {
            Emit(TestLine.Warn("Нужны права администратора: сеанс трассировки ETW может создать только он."));
            Emit(TestLine.Dim("Права не сохраняются между запусками — если сейчас его нет, значит просто"));
            Emit(TestLine.Dim("запустили обычным способом. Поднимать до администратора всё приложение"));
            Emit(TestLine.Dim("манифестом ради одной строки неправильно, поэтому решение за вами."));
            Emit(TestLine.Empty);

            var answer = MessageBox.Show(this,
                "Права администратора нужны только для счётчика кадров — остальное работает и без них.\n\n" +
                "«Да» — перезапустить сейчас один раз.\n" +
                "«Нет» — создать на рабочем столе отдельный ярлык, который всегда " +
                "запускает NetAudit с правами администратора (Windows будет спрашивать " +
                "подтверждение при каждом запуске с него — это не обход, а обычная элевация).\n" +
                "«Отмена» — ничего не делать.",
                "Счётчик FPS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer == MessageBoxResult.Yes) RestartElevated();
            else if (answer == MessageBoxResult.No) OnCreateElevatedShortcut(this, new RoutedEventArgs());
            return;
        }

        if (!ok)
        {
            Emit(TestLine.Bad("Права есть, но сеанс не поднялся. Причина выше."));
            return;
        }

        Emit(TestLine.Good("Счётчик работает. Прочерк в оверлее означает, что на переднем плане"));
        Emit(TestLine.Good("нет ничего рисующего — сверните NetAudit и запустите игру."));
        Emit(TestLine.Empty);
        Emit(TestLine.Dim("Охват: Direct3D 9/10/11/12, то есть почти все игры под Windows."));
        Emit(TestLine.Dim("Игры на Vulkan и OpenGL мимо DXGI счётчик не увидит."));

        // Живая самодиагностика: если разрешение имён событий когда-нибудь снова
        // сломается (как уже было — session.Source.AllEvents не резолвил "Present"
        // и молча не находил ни одного события), эти цифры это сразу покажут,
        // а не оставят гадать по одному прочерку в оверлее
        long seen    = _sysScheduler?.FpsEventsSeen ?? 0;
        long matched = _sysScheduler?.FpsPresentsMatched ?? 0;
        Emit(TestLine.Empty);
        Emit(TestLine.Dim($"Диагностика: событий ETW получено {seen}, из них кадров опознано {matched}."));

        if (seen > 0 && matched == 0)
        {
            Emit(TestLine.Warn("События идут, но ни одно не опознано как кадр — похоже, сломалось распознавание имён."));
        }
        else if (_sysScheduler?.FpsLooksDead == true)
        {
            Emit(TestLine.Bad("Сеанс поднят, но не пришло ни одного события — счётчик мёртв."));
            Emit(TestLine.Dim("Обычная причина: в системе остался сеанс трассировки от предыдущего запуска,"));
            Emit(TestLine.Dim("который не был закрыт штатно (падение или снятие через диспетчер задач)."));
            Emit(TestLine.Dim("Приложение снимает такой сеанс само при старте, но если это не помогло —"));
            Emit(TestLine.Dim("перезапустите NetAudit, а в крайнем случае перезагрузите Windows."));
        }
    }

    /// <summary>Перезапуск с повышением прав. Настройки уже на диске, терять нечего.</summary>
    private void RestartElevated()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Emit(TestLine.Bad("Не удалось определить путь к программе"));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb            = "runas",
            });

            _reallyExiting = true;
            Close();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Emit(TestLine.Warn("Повышение прав отменено — счётчик остался выключенным"));
        }
        catch (Exception ex)
        {
            Emit(TestLine.Bad($"Не удалось перезапустить: {ex.Message}"));
        }
    }

    /// <summary>
    /// Скачивает и ставит обновление. Ход показывается во вкладке тестов: своё окно
    /// прогресса заводить незачем, а глухое ожидание на десятки мегабайт выглядит как зависание.
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        BottomTabs.SelectedItem = TestTab;

        Emit(TestLine.Empty);
        Emit(TestLine.Head("Установка обновления"));
        Emit(TestLine.Dim($"Источник: {_updateDownloadUrl}"));

        if (string.IsNullOrWhiteSpace(_updateSha256))
            Emit(TestLine.Warn("В version.json нет контрольной суммы — проверить целостность архива нечем"));

        var progress = new Progress<NetAudit.Core.Updates.UpdateProgress>(p =>
            TestStatusLbl.Text = p.Percent > 0 ? $"{p.Stage}  {p.Percent}%" : p.Stage);

        UpdateDownloadBtn.IsEnabled = false;
        try
        {
            var installer = new NetAudit.Core.Updates.UpdateInstaller();
            string dir = await installer.DownloadAsync(
                _updateDownloadUrl, _updateSha256, progress, CancellationToken.None);

            Emit(TestLine.Good("Архив скачан и проверен"));
            Emit(TestLine.Dim($"Распаковано во временную папку: {dir}"));

            var go = MessageBox.Show(this,
                "Обновление скачано и проверено.\n\n" +
                "NetAudit сейчас закроется, заменит файлы и запустится заново.\n" +
                "Это займёт несколько секунд.",
                "Установить обновление",
                MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.OK);

            if (go != MessageBoxResult.OK)
            {
                Emit(TestLine.Warn("Установка отменена. Скачанное осталось во временной папке."));
                return;
            }

            NetAudit.Core.Updates.UpdateInstaller.ApplyAndRestart(dir, () =>
            {
                _reallyExiting = true;
                Close();
            });
        }
        catch (NetAudit.Core.Updates.UpdateInstaller.UpdateException ex)
        {
            Emit(TestLine.Bad(ex.Message));
            MessageBox.Show(this, ex.Message, "Обновление не установлено",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Emit(TestLine.Bad($"Не удалось обновиться: {ex.Message}"));
        }
        finally
        {
            UpdateDownloadBtn.IsEnabled = true;
            TestStatusLbl.Text = "Тест не запущен";
        }
    }

    private void OnCreateShortcutFromTests(object sender, RoutedEventArgs e)
    {
        if (DesktopShortcut.Create(out string error))
        {
            Emit(TestLine.Good("✓ Ярлык на рабочем столе создан"));
        }
        else
        {
            Emit(TestLine.Bad($"Не удалось создать ярлык: {error}"));
        }
        _settings.ShortcutOffered = true;
        _settings.Save();
        ShortcutBanner.Visibility = Visibility.Collapsed;
    }

    private void OnCreateElevatedShortcut(object sender, RoutedEventArgs e)
    {
        if (DesktopShortcut.CreateElevated(out string error))
        {
            Emit(TestLine.Good("✓ Создан ярлык «NetAudit (администратор)» на рабочем столе"));
            Emit(TestLine.Dim("При запуске с него Windows будет спрашивать подтверждение (UAC) каждый раз —"));
            Emit(TestLine.Dim("это штатная элевация, не обход. Зато счётчик FPS будет работать сразу."));
        }
        else
        {
            Emit(TestLine.Bad($"Не удалось создать ярлык: {error}"));
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetAudit");
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось открыть папку:\n{ex.Message}", "NetAudit",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Строка вывода ─────────────────────────────────────────────────────

    private sealed class TestOutputLine
    {
        private static readonly SolidColorBrush Accent = new(WpfColor.FromRgb(0x4F, 0xD9, 0x8B));
        private static readonly SolidColorBrush Plain  = new(WpfColor.FromRgb(0xD8, 0xEA, 0xDF));
        private static readonly SolidColorBrush Ok     = new(WpfColor.FromRgb(0x86, 0xD9, 0x7A));
        private static readonly SolidColorBrush Warn   = new(WpfColor.FromRgb(0xE3, 0xB4, 0x4F));
        private static readonly SolidColorBrush Bad    = new(WpfColor.FromRgb(0xEE, 0x8B, 0x8B));
        private static readonly SolidColorBrush Mute   = new(WpfColor.FromRgb(0x5C, 0x7A, 0x69));

        public string Text { get; }
        public Brush Color { get; }
        public FontWeight Weight { get; }

        public TestOutputLine(TestLine line)
        {
            Text  = line.Text;
            Color = line.Level switch
            {
                TestLevel.Header => Accent,
                TestLevel.Good   => Ok,
                TestLevel.Warn   => Warn,
                TestLevel.Bad    => Bad,
                TestLevel.Muted  => Mute,
                _                => Plain,
            };
            Weight = line.Level == TestLevel.Header ? FontWeights.Bold : FontWeights.Normal;
        }

        /// <summary>
        /// Имя элемента в дереве автоматизации берётся отсюда. Без переопределения
        /// экранный диктор зачитывал бы «MainWindow+TestOutputLine» вместо строки отчёта.
        /// </summary>
        public override string ToString() => Text;
    }
}
