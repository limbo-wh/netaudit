# NetAudit — инструкция по старту на Windows + ТЗ

**Дата:** 2026-08-16
**Парный документ:** `2026-08-16_netaudit_desktop_windows_analysis.md` (обоснование решений)
**Назначение:** этот файл переносится на Windows-машину и служит рабочим ТЗ.

---

## 0. Уточнение по версии .NET

В обсуждении фигурировал .NET 8. **Бери .NET 10 (LTS, вышел в ноябре 2025)** —
.NET 8 уходит с поддержки в ноябре 2026. Всё описанное ниже идентично для обеих
версий, разница только в `<TargetFramework>`.

---

## 1. Подготовка окружения (Windows)

### 1.1 SDK и IDE

```powershell
# .NET SDK
winget install Microsoft.DotNet.SDK.10

# IDE — одно из двух:
winget install Microsoft.VisualStudio.2022.Community   # workload ".NET desktop development"
# или
winget install Microsoft.VisualStudioCode              # + расширение "C# Dev Kit"

# Проверка
dotnet --list-sdks
```

### 1.2 Для оверлея в exclusive fullscreen (опционально, позже)

```powershell
winget install Guru3D.RTSS      # RivaTuner Statistics Server
```

SDK со структурами shared memory лежит после установки в
`C:\Program Files (x86)\RivaTuner Statistics Server\SDK` — оттуда брать описание
`RTSS_SHARED_MEMORY` / `OSDENTRY`, не гадать по памяти.

### 1.3 Git

```powershell
winget install Git.Git
```

---

## 2. Создание solution

```powershell
mkdir C:\dev\NetAudit; cd C:\dev\NetAudit

dotnet new sln -n NetAudit
dotnet new classlib -n NetAudit.Core   -f net10.0
dotnet new wpf      -n NetAudit.App    -f net10.0
dotnet new classlib -n NetAudit.Rtss   -f net10.0

dotnet sln add NetAudit.Core NetAudit.App NetAudit.Rtss
dotnet add NetAudit.App reference NetAudit.Core NetAudit.Rtss

# Пакеты
dotnet add NetAudit.App  package ScottPlot.WPF
dotnet add NetAudit.Core package Microsoft.Data.Sqlite
dotnet add NetAudit.Core package Microsoft.Diagnostics.Tracing.TraceEvent
```

Оверлей — отдельное `Window` внутри `NetAudit.App`, отдельный проект под него не нужен.

### 2.1 Настройки csproj

`NetAudit.App.csproj` — минимально необходимое:

```xml
<PropertyGroup>
  <TargetFramework>net10.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <ApplicationManifest>app.manifest</ApplicationManifest>
</PropertyGroup>
```

`NetAudit.Core.csproj` тоже переводи на `net10.0-windows` — там Win32 API.

### 2.2 Манифест: DPI и права

`app.manifest` (создаётся шаблоном WPF, дополнить):

- `requestedExecutionLevel` — **`asInvoker`** на этапах 0–2. Поднимать до
  `requireAdministrator` только когда дойдёшь до ETW (этап 3). Не запрашивай админа
  раньше, чем он реально нужен — иначе не заметишь, что случайно построил
  зависимость от прав там, где она не требовалась.
- DPI-awareness: `PerMonitorV2` — иначе оверлей будет мазаться на мониторах
  с масштабированием.

---

## 3. Ключевые технические рецепты

Ниже — не готовый код, а точки, где легко потерять день. Всё остальное пишется
обычным способом.

### 3.1 Click-through оверлей (этап 2 — главный риск)

XAML окна:

```xml
<Window x:Class="NetAudit.App.OverlayWindow"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        SizeToContent="WidthAndHeight" Left="20" Top="20">
```

Code-behind, в `OnSourceInitialized` (не в конструкторе — HWND ещё нет):

```csharp
const int GWL_EXSTYLE = -20;
const int WS_EX_TRANSPARENT = 0x00000020;   // клики проходят насквозь
const int WS_EX_LAYERED     = 0x00080000;
const int WS_EX_TOOLWINDOW  = 0x00000080;   // нет в Alt-Tab
const int WS_EX_NOACTIVATE  = 0x08000000;   // не ворует фокус у игры

[DllImport("user32.dll")] static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
    int X, int Y, int cx, int cy, uint uFlags);

protected override void OnSourceInitialized(EventArgs e)
{
    base.OnSourceInitialized(e);
    var hwnd = new WindowInteropHelper(this).Handle;
    var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
    SetWindowLong(hwnd, GWL_EXSTYLE,
        ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
}
```

**Три грабли, каждая стоит часа отладки:**

1. **Игра отбирает Z-order.** Borderless-игра при переключении фокуса встаёт выше
   оверлея. Лечится таймером раз в 1–2 с:
   `SetWindowPos(hwnd, HWND_TOPMOST(-1), 0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)`.
   Флаг `SWP_NOACTIVATE` обязателен, иначе будешь выбивать игру из фокуса каждые 2 секунды.
2. **Полностью прозрачный фон не ловит мышь вообще** — это и нужно, но помни:
   настраивать положение оверлея мышью не выйдет. Позицию задавай из главного окна
   или хоткеями.
3. **Не рисуй в оверлее тяжёлые графики.** `AllowsTransparency=true` + WPF
   композиция — текст и короткий спарклайн максимум. Полные графики живут
   в главном окне.

Глобальный хоткей вкл/выкл: `RegisterHotKey` + обработка `WM_HOTKEY` через
`HwndSource.AddHook`. Без глобального хоткея оверлей неуправляем из игры.

### 3.2 Пробы задержки (этап 0–1)

```csharp
// ICMP — прав администратора НЕ требует (обёртка над IcmpSendEcho2)
using var ping = new Ping();
var reply = await ping.SendPingAsync(target, timeout: 1000);
```

- **Отдельный экземпляр `Ping` на каждую цель.** Один экземпляр не умеет
  параллельные операции, а тебе нужно 4 цели одновременно.
- Тик — `PeriodicTimer(TimeSpan.FromMilliseconds(250))`, реальные интервалы мерить
  `Stopwatch`. `System.Timers.Timer` для этого не годится.
- **TCP-проба** обязательна как дубль ICMP: транзитные роутеры деприоритизируют
  ICMP, и его цифры не равны игровому RTT. Меряй `Stopwatch` вокруг
  `socket.ConnectAsync(ip, port)` — время SYN→SYN/ACK.

**Traceroute без raw-сокетов и без админа** — тот же `Ping` с растущим TTL:

```csharp
var opts = new PingOptions(ttl: hop, dontFragment: true);
var reply = await ping.SendPingAsync(target, 1000, buffer, opts);
// reply.Status == IPStatus.TtlExpired → reply.Address это промежуточный хоп
```

Первый хоп = роутер, второй = обычно граница провайдера. Кэшируй результат
и пингуй эти два адреса постоянно — это и есть сегментация «кто виноват».

### 3.3 Сегментация задержки (этап 1 — главная ценность)

Четыре параллельные серии на одном таймлайне:

| Серия | Цель | О чём говорит рост |
|---|---|---|
| L1 | default gateway | Wi-Fi / кабель / роутер |
| L2 | hop 2 из traceroute | last mile провайдера |
| L3 | 1.1.1.1 | магистраль / провайдер |
| L4 | IP сервера игры | сам сервер или маршрут к нему |

Вклад сегмента = L(n) − L(n−1). На графике — stacked-области: видно, чьи это
миллисекунды. Default gateway берётся из
`NetworkInterface.GetAllNetworkInterfaces()` →
`GetIPProperties().GatewayAddresses`.

### 3.4 ETW: IP сервера игры и трафик по процессам (этап 3–4, нужен админ)

Единственный рабочий способ узнать remote-IP для UDP по процессу.
`GetExtendedUdpTable` **не даёт remote-адрес** — не трать на него время.
`GetExtendedTcpTable` даёт, но игровой трафик почти всегда UDP.

```csharp
// Требует запуска от администратора
using var session = new TraceEventSession("NetAuditKernel");
session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
session.Source.Kernel.UdpIpSend += e => { /* e.ProcessID, e.daddr, e.size */ };
session.Source.Kernel.UdpIpRecv += e => { /* ... */ };
session.Source.Process();   // блокирующий — в отдельный поток
```

Логика детекта сервера игры: из потока событий по PID игры собрать
топ-1 удалённый IP по объёму/частоте за последние N секунд, отбросить
широковещательные и локальные (RFC1918, multicast) — это и будет игровой сервер.

Тот же поток даёт разбивку трафика по процессам (кто жрёт канал).
**Оговорка:** под высоким pps ETW теряет события — для определения IP это
безразлично, для точного подсчёта байт лучше сверяться со счётчиками адаптера.

Поиск процесса игры: список известных exe + эвристика «полноэкранное окно
в фокусе» (`GetForegroundWindow` → `GetWindowThreadProcessId`). Ручной выбор
процесса оставь всегда — эвристика будет ошибаться.

### 3.5 Bufferbloat (этап 4)

1. Замерить baseline RTT в простое (медиана за ~10 с).
2. Нагрузить канал скачиванием с публичной точки (`speed.cloudflare.com`,
   несколько параллельных потоков — один TCP-поток канал не насытит).
3. Параллельно мерить RTT.
4. Повторить для upload — у домашних каналов проблема чаще именно там.
5. Дельта RTT → буквенная оценка A–F в стиле Waveform Bufferbloat Test.

Своего сервера не требуется, что соответствует условию «на сервере ничего нет».

### 3.6 Wi-Fi (этап 5)

`wlanapi.dll` через P/Invoke: `WlanOpenHandle` → `WlanEnumInterfaces` →
`WlanQueryInterface(wlan_intf_opcode_current_connection)` — RSSI, link quality,
rx/tx rate, BSSID. `WlanGetNetworkBssList` — соседние сети и загруженность каналов.

**Грабля:** с Windows 11 24H2 SSID/BSSID возвращаются пустыми без включённых
служб геолокации и разрешения для приложения. RSSI и link speed при этом читаются.
Обработай этот случай в UI явным сообщением, иначе будет выглядеть как баг.

### 3.7 Пропускная способность локально

`NetworkInterface.GetIPv4Statistics()` → дельта `BytesReceived`/`BytesSent`
по времени. Без админа, без драйверов. Достаточно для графика загрузки канала.

### 3.8 Графики (ScottPlot 5)

Для realtime использовать `DataStreamer` / `DataLogger` из ScottPlot 5 —
они рассчитаны на потоковые данные и скользящее окно, в отличие от пересоздания
`Scatter` на каждом тике (это главная причина тормозов у самодельных realtime-графиков).

Буфер в памяти: 10 минут × 4 Гц ≈ 2400 точек на серию. Полная история — в SQLite,
чтобы вернуться к «вчера в 21:00 лагало».

---

## 4. Порядок работ

| Этап | Содержание | Критерий готовности |
|---|---|---|
| **0** | WPF-окно + ScottPlot, ICMP-проба до шлюза и 1.1.1.1, живой график | график едет, не тормозит через 10 минут работы |
| **1** | Traceroute на TTL, 4 сегмента, stacked-график вклада, jitter и loss | видно, какой участок даёт задержку |
| **2** | Layered-оверлей, click-through, глобальный хоткей | **проверено в реальной игре в borderless** |
| **3** | ETW: авто-детект IP сервера игры, пинг до него (манифест → requireAdministrator) | в шутере оверлей показывает ping до реального сервера |
| **4** | Bufferbloat-тест + трафик по процессам | видно, что торрент в фоне убивает пинг |
| **5** | Wi-Fi-модуль | |
| **6** | RTSS-бэкенд для exclusive fullscreen, SQLite-история, просмотр прошлых сессий | |

**Этап 2 делать раньше этапа 3.** Оверлей — то, ради чего всё затевается, и его
поведение поверх конкретных игр надо проверить эмпирически как можно раньше;
если layered-окно у тебя где-то не заработает, это меняет план (переход на RTSS
как основной, а не опциональный путь).

---

## 5. Чего не делать

- **Не писать свой хук DXGI/Present/Vulkan и не инжектить DLL в процесс игры.**
  EAC, BattlEye, Vanguard, FACEIT трактуют это как чит — это реальные баны, а не
  теоретический риск. Для exclusive fullscreen существует RTSS, который уже
  в белых списках античитов.
- Не пытаться получить remote-адрес UDP через `GetExtendedUdpTable` — Windows
  его там не отдаёт.
- Не запрашивать права администратора на этапах 0–2 — почти всё (ICMP,
  traceroute, Wi-Fi, счётчики адаптера) работает от обычного пользователя,
  и это стоит сохранить.
- Не рисовать полноценные графики в оверлее.

---

## 6. Промпт для продолжения на Windows

Скопировать оба .md на Windows-машину и начать сессию так:

> Читай `2026-08-16_netaudit_desktop_windows_analysis.md` и
> `2026-08-16_netaudit_windows_инструкция_и_ТЗ.md`. Это ТЗ на приложение NetAudit.
> Сделай этап 0 из раздела «Порядок работ»: solution по разделу 2, WPF-окно
> со ScottPlot и ICMP-пробами до default gateway и 1.1.1.1, живой график
> с тиком 250 мс. Прав администратора не требовать.

---

## 7. Границы

Не проверено эмпирически (проверять на Windows по ходу):
поведение layered-оверлея в конкретных играх с античитами; актуальная структура
shared memory текущей версии RTSS (сверять с её SDK-папкой); потери ETW-событий
под высоким pps; поведение Wi-Fi API на Windows 11 24H2+; IPv6-сценарии.
