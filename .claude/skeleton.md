# Скелет проекта NetAudit

Карта репозитория: что где лежит, что уже есть, чего ещё нет.
Обновлять при добавлении/удалении файлов и проектов.

## Дерево

```
app_lan/                                  ← корень git-репозитория
├── .git/
├── .gitignore                            bin/obj/dist, сертификаты, локальные настройки
├── .gitattributes                        LF в репозитории, CRLF для .ps1/.bat
├── README.md                             описание для GitHub
├── LICENSE                                PolyForm Noncommercial 1.0.0
├── docs/
│   └── screenshots/                      скриншоты для README (вкладки, настройки, оверлей)
├── .claude/
│   ├── stack.md                          стек, версии, ключевые решения
│   ├── skeleton.md                       этот файл
│   ├── tasks.md                          список задач
│   ├── rules.md                          правила работы агента
│   └── settings.local.json               не версионируется
├── CLAUDE.md                             точка входа для агента
├── 2026-08-16_netaudit_desktop_windows_analysis.md   анализ и обоснование решений
├── 2026-08-16_netaudit_windows_инструкция_и_ТЗ.md    рабочее ТЗ
└── NetAudit/
    ├── NetAudit.slnx
    ├── version.json                      манифест обновления, стабильный канал (ветка main)
    ├── version-beta.json                 манифест обновления, бета-канал (ветка dev) — появляется после первого -Beta релиза
    ├── release.ps1                       сборка: publish → (опц. подпись) → zip → установщик → SHA-256 → version.json/-beta.json (флаг -Beta)
    ├── sign-setup.ps1                    создание сертификата подписи (для -Sign, узкий круг машин)
    ├── trust-cert.ps1                    импорт сертификата в доверенные (только при -Sign)
    ├── installer/
    │   ├── NetAudit.iss                  скрипт Inno Setup: Program Files, ярлыки, удаление
    │   ├── register-task.ps1             задача в Планировщике (RunLevel=Highest) — FPS без UAC
    │   ├── unregister-task.ps1           снятие задачи при удалении
    │   └── pre-uninstall.ps1             гарантированно закрывает процесс перед удалением файлов
    ├── dist/                             артефакты сборки, не версионируются
    │
    ├── NetAudit.Core/                    измерения и диагностика, без UI
    │   ├── GameBoost/
    │   │   ├── GameBoostOptions.cs        какие твики применять, отчёт о результате
    │   │   └── GameBoostService.cs        разгон перед игрой: питание, уведомления,
    │   │                                  эффекты, службы, приоритет — с откатом
    │   │                                  после сбоя через своё состояние на диске
    │   ├── Models/
    │   │   ├── PingResult.cs             Target, RttMs?, Success, Timestamp
    │   │   ├── SystemSnapshot.cs         CPU/GPU/RAM/сеть/батарея/FPS/темп. CPU и GPU
    │   │   ├── HardwareInfo.cs           CPU/GPU/RAM/ОС/материнка
    │   │   ├── WifiInfo.cs               SSID, RSSI, канал, band, link rate
    │   │   └── ProcessEntry.cs           имя, CPU%, RAM МБ
    │   ├── Probes/
    │   │   ├── IcmpProbe.cs              один Ping на цель, Stopwatch
    │   │   ├── NetworkUtils.cs           default gateway, фильтр виртуальных адаптеров
    │   │   ├── SystemMetricsProbe.cs     CPU/RAM/батарея
    │   │   ├── GpuProbe.cs               загрузка GPU одним ReadCategory
    │   │   ├── FpsProbe.cs               кадры по событиям Present из ETW, нужен админ
    │   │   ├── TemperatureProbe.cs       температура CPU/GPU, LibreHardwareMonitorLib, нужен админ
    │   │   ├── NetworkSpeedProbe.cs      дельта BytesReceived/Sent
    │   │   ├── WifiProbe.cs              netsh, только при наличии Wi-Fi адаптера
    │   │   ├── HardwareProbe.cs          WMI, одноразовый сбор
    │   │   └── ProcessProbe.cs           список процессов, без ограничения сверху
    │   ├── Diagnostics/                  разовые тесты, вкладка «Тесты и сервис»
    │   │   ├── TestTypes.cs              TestLine, TestLevel, IDiagnosticTest, Fmt
    │   │   ├── PingUtil.cs               выбор якоря, TTL-проверка на перехват, медиана
    │   │   ├── SpeedTest.cs              скорость + прирост задержки под нагрузкой
    │   │   ├── DnsTest.cs                свой DNS-клиент по UDP, сравнение резолверов
    │   │   ├── TracerouteTest.cs         пинг с растущим TTL + вердикт по участкам
    │   │   ├── MtuTest.cs                двоичный поиск размера пакета без дробления
    │   │   ├── SystemBenchTest.cs        CPU/RAM/диск, проверка на троттлинг
    │   │   ├── NetworkResetService.cs    сброс сети через повышенный PowerShell
    │   │   └── RamCacheService.cs        очистка standby list через повышенный PowerShell
    │   ├── GameMode/GameModeDetector.cs  полноэкранное приложение на переднем плане
    │   ├── Scheduler/
    │   │   ├── ProbeScheduler.cs         тик 250 мс, шлюз + 1.1.1.1 параллельно
    │   │   └── SystemMetricsScheduler.cs тик 1 с (2 с в игре), снимок + Wi-Fi + FPS
    │   ├── Logging/PingLogger.cs         лог в %LOCALAPPDATA%\NetAudit\ping_*.log
    │   ├── Updates/UpdateInstaller.cs    скачать, сверить SHA-256, подменить, перезапустить
    │   └── UpdateChecker.cs              сравнение с version.json по URL
    │
    ├── NetAudit.App/                     WPF
    │   ├── App.xaml(.cs)                 подключение темы + запись crash.log
    │   ├── Theme.xaml                    тёмно-зелёная палитра и шаблоны контролов
    │   ├── NetAudit.ico                  значок приложения и трея, 8 размеров
    │   ├── app.manifest                  asInvoker, PerMonitorV2
    │   ├── AppSettings.cs                %LOCALAPPDATA%\NetAudit\settings.json + Clone/CopyFrom
    │   ├── StartupManager.cs             автозапуск через HKCU\...\Run, аргумент --tray
    │   ├── DesktopShortcut.cs            ярлык на рабочем столе через WScript.Shell, без install.bat
    │   ├── TrayIcon.cs                   NotifyIcon с меню, подсказкой и уведомлениями
    │   ├── HotkeyManager.cs              RegisterHotKey + WM_HOTKEY через HwndSource
    │   ├── SingleInstance.cs             именованный Mutex — запрет второго экземпляра
    │   ├── MainWindow.xaml(.cs)          графики, статистика, лог, диспетчер, хоткеи
    │   ├── MainWindow.Tests.cs           вкладка тестов, сброс сети, обновление
    │   ├── MainWindow.GameMode.cs        игровой режим: приоритет, отрисовка, лог
    │   ├── MainWindow.GameBoost.cs       вкладка «Игровой режим»: авто-режим + разгон
    │   ├── MainWindow.Tray.cs            трей, сворачивание и закрытие в трей
    │   ├── OverlayWindow.xaml(.cs)       layered click-through оверлей, строка FPS первой
    │   ├── SettingsWindow.xaml(.cs)      настройки, мгновенное применение + откат
    │   ├── AboutWindow.xaml(.cs)         о программе, поддержка разработки
    │   └── HardwareWindow.xaml(.cs)      карточка железа
    │
    └── NetAudit.Rtss/                    ПУСТОЙ проект-заглушка (этап 6)
```

## Что реализовано

- **Этап 0 — готов.** WPF + ScottPlot, ICMP до шлюза и 1.1.1.1 с тиком 250 мс,
  живые графики, min/avg/max/jitter/loss/серии потерь, лог с подсветкой спайков.
- **Этап 2 — код готов, ждёт проверки в игре.** Layered click-through оверлей с
  полным набором ex-стилей (`LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE`),
  таймер удержания `HWND_TOPMOST` раз в 1.5 с, глобальные хоткеи Ctrl+Alt+O
  (вкл/выкл) и Ctrl+Alt+1…4 (углы экрана). Первая строка — кадры в секунду.
- **Этап 4 — частично.** Bufferbloat меряется в `SpeedTest`: задержка снимается
  одновременно с прокачкой канала, оценка A+…F по приросту. Трафика по процессам нет.
- **Этап 5 — частично.** `WifiProbe` есть, списка соседних сетей и загруженности
  каналов нет.
- **Сверх ТЗ:** метрики системы, карточка железа, диспетчер процессов, вкладка
  тестов (скорость, DNS, трассировка, MTU, железо), сброс сети без перезагрузки
  Windows, игровой режим, разгон перед игрой (Game Boost), значок в трее,
  автозапуск, автообновление с проверкой контрольной суммы, установщик, подпись кода.

## Чего нет

- **Этап 1** — traceroute есть как отдельный тест, но нет постоянной сегментации
  задержки: четырёх серий на одном таймлайне, stacked-графика вклада и
  TCP-connect-пробы.
- **Этап 3** — ETW для сетевого трафика, авто-детект IP сервера игры.
- **Этап 4** — трафик по процессам.
- **Этап 6** — RTSS-бэкенд (проект пуст), SQLite-история, просмотр прошлых сессий.

## Точки роста в Core

Каталоги, которых пока нет и которые появятся по мере работ:
`Core/Etw/` (KernelNetworkListener для сетевого трафика по процессам),
`Core/Analysis/` (постоянная сегментация задержки), `Core/Storage/` (SQLite).

Каталог `Core/Diagnostics/` — для разовых тестов «нажал и посмотрел».
Постоянные измерения живут в `Probes/` и `Scheduler/`, смешивать их не надо.
