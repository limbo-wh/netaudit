using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetAudit.Core.Probes;

/// <summary>
/// Счётчик кадров в секунду.
///
/// Как это работает и почему именно так. Обычные оверлеи (RTSS, Afterburner, оверлеи
/// драйверов) считают кадры, подменяя вызов Present внутри процесса игры: ставят хук
/// на DXGI или подгружают свою DLL. Для этого проекта такой путь закрыт — античиты
/// расценивают внедрение в свой процесс как вмешательство, и последствия несёт владелец.
///
/// Здесь используется другой способ, тот же, что у PresentMon от Microsoft: Windows
/// сама сообщает о каждом показанном кадре через ETW — системный механизм трассировки.
/// Мы подписываемся на эти сообщения снаружи и просто считаем их. Процесс игры при этом
/// не затрагивается ни одним байтом.
///
/// Цена: сеанс ETW может создать только администратор. Без повышенных прав счётчик
/// молча не работает и показывает прочерк — это осознанный выбор, поднимать всё
/// приложение до администратора ради одной строки в оверлее неправильно.
///
/// Охват: провайдеры DXGI и D3D9 покрывают Direct3D 9/10/11/12, то есть почти все игры
/// под Windows. Игры на Vulkan и OpenGL, не проходящие через DXGI, останутся без цифры.
/// </summary>
public sealed class FpsProbe : IDisposable
{
    // GUID провайдеров ETW. Те же, что перечислены в исходниках PresentMon
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private const string SessionName = "NetAudit-FPS";

    /// <summary>Процесс, не показавший ни кадра за это время, забывается.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    private TraceEventSession?          _session;
    private RegisteredTraceEventParser? _parser;
    private Thread?                     _pump;
    private volatile bool               _disposed;

    private readonly ConcurrentDictionary<int, Counter> _counters = new();

    // Диагностика для «Счётчик FPS: состояние» — если когда-нибудь снова сломается
    // разрешение имён событий, эти цифры сразу покажут это, а не оставят гадать
    private long _eventsSeen;
    private long _presentsMatched;

    /// <summary>Счётчик работает и события идут.</summary>
    public bool Available { get; private set; }

    /// <summary>Человекочитаемая причина, если счётчик не работает.</summary>
    public string Status { get; private set; } = "не запущен";

    /// <summary>Всего событий ETW получено от провайдеров DXGI/D3D9 с момента запуска.</summary>
    public long EventsSeen => Interlocked.Read(ref _eventsSeen);

    /// <summary>Из них опознано как начало кадра (Present).</summary>
    public long PresentsMatched => Interlocked.Read(ref _presentsMatched);

    private sealed class Counter
    {
        public long Presents;          // всего кадров с начала наблюдения
        public long LastReadPresents;  // сколько было при прошлом опросе
        public long LastEventTicks;    // когда пришёл последний кадр
        public long LastReadTicks;     // когда опрашивали в прошлый раз
        public double Fps;             // последнее посчитанное значение
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Пытается поднять сеанс ETW. Не бросает исключений: не получилось — значит
    /// строка FPS покажет прочерк, а всё остальное приложение работает как работало.
    /// </summary>
    public void Start()
    {
        if (_session is not null) return;

        if (!IsElevated)
        {
            Status = "нужны права администратора";
            return;
        }

        try
        {
            // StopOnDispose закрывает сеанс вместе с приложением. Без этого
            // сеанс ETW переживает процесс и остаётся висеть в системе
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };

            // Оба провайдера отдают почти исключительно события кадров, поэтому
            // маску ключевых слов не сужаем: лишнего объёма здесь не будет,
            // а угадывать номер нужного бита — верный способ получить тишину
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(D3d9Provider, TraceEventLevel.Informational, ulong.MaxValue);

            // Критично: голый Source.AllEvents отдаёт «сырые» события без разбора манифеста —
            // TaskName для DXGI/D3D9 в этом случае приходит как "EventID(42)", а не "Present",
            // потому что их манифест зарегистрирован в системе через wevtutil (путь TDH), а не
            // разослан динамически по ETW, и именно динамический путь понимает голый Source.
            // Без этого разбора фильтр по имени не находил вообще ничего, и счётчик молча
            // не считал ни кадра — при этом Available оставался true, ошибки не было,
            // просто событий с нужным именем не приходило никогда. Проверено 2026-08-17
            // на живой Dota 2: с RegisteredTraceEventParser TaskName == "Present" резолвится
            // верно и совпадает по числу 1:1 со Stop-парой (id 42/43 в манифесте DXGI).
            _parser = new RegisteredTraceEventParser(_session.Source);
            _parser.All += OnEvent;

            // Source.Process() блокирует поток до остановки сеанса — ему нужен свой
            _pump = new Thread(Pump)
            {
                IsBackground = true,
                Name         = "NetAudit ETW FPS",
                Priority     = ThreadPriority.BelowNormal,
            };
            _pump.Start();

            Available = true;
            Status    = "работает";
        }
        catch (UnauthorizedAccessException)
        {
            Status = "нужны права администратора";
            Cleanup();
        }
        catch (Exception ex)
        {
            Status = $"не удалось запустить: {ex.Message}";
            Cleanup();
        }
    }

    private void Pump()
    {
        try { _session?.Source.Process(); }
        catch (Exception ex)
        {
            if (_disposed) return;
            Available = false;
            Status    = $"сеанс прерван: {ex.Message}";
        }
    }

    private void OnEvent(TraceEvent ev)
    {
        Interlocked.Increment(ref _eventsSeen);

        // Считаем только начало отрисовки кадра. Событий у провайдера больше,
        // но начало и конец одного кадра дали бы удвоенный FPS
        if (ev.Opcode != TraceEventOpcode.Start) return;
        if (ev.ProcessID <= 0) return;

        // Точное совпадение, не Contains. У DXGI несколько разных событий с "Present"
        // в имени на разных уровнях: "Present" (то, что нужно — сам факт показа кадра),
        // "IDXGISwapChain_Present" (вызов метода приложением) и "PresentMultiplaneOverlay".
        // Проверено на живой Dota 2: подстрока "Present" ловит все три семейства сразу
        // и завтраивает счётчик. Нужен именно голый "Present" — его id 42/43 в манифесте.
        if (ev.TaskName != "Present") return;

        Interlocked.Increment(ref _presentsMatched);

        var c = _counters.GetOrAdd(ev.ProcessID, _ => new Counter
        {
            LastReadTicks = Stopwatch.GetTimestamp(),
        });

        Interlocked.Increment(ref c.Presents);
        Interlocked.Exchange(ref c.LastEventTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Кадров в секунду за время, прошедшее с прошлого вызова.
    /// </summary>
    /// <param name="preferredPid">
    /// Процесс на переднем плане. Если он не рисует, берётся самый активный из рисующих —
    /// игра может держать окно, а кадры выдавать из другого процесса.
    /// </param>
    /// <returns>Кадров в секунду или NaN, если считать нечего.</returns>
    public double Sample(int preferredPid)
    {
        if (!Available) return double.NaN;

        long now = Stopwatch.GetTimestamp();
        double best = double.NaN;
        double bestAny = double.NaN;

        foreach (var (pid, c) in _counters)
        {
            long presents = Interlocked.Read(ref c.Presents);
            long lastEvent = Interlocked.Read(ref c.LastEventTicks);

            // Процесс перестал рисовать — забываем, чтобы словарь не рос вечно
            if (Stopwatch.GetElapsedTime(lastEvent, now) > StaleAfter)
            {
                _counters.TryRemove(pid, out _);
                continue;
            }

            double seconds = Stopwatch.GetElapsedTime(c.LastReadTicks, now).TotalSeconds;
            if (seconds > 0.2)
            {
                c.Fps              = (presents - c.LastReadPresents) / seconds;
                c.LastReadPresents = presents;
                c.LastReadTicks    = now;
            }

            if (pid == preferredPid) best = c.Fps;
            if (double.IsNaN(bestAny) || c.Fps > bestAny) bestAny = c.Fps;
        }

        double result = !double.IsNaN(best) && best > 0 ? best : bestAny;
        return double.IsNaN(result) || result <= 0 ? double.NaN : result;
    }

    private void Cleanup()
    {
        Available = false;
        try { _session?.Dispose(); } catch { }
        _session = null;
        _parser  = null;
    }

    /// <summary>Закрыть сеанс. После этого Start можно вызвать снова.</summary>
    public void Stop()
    {
        if (_session is null && _pump is null) return;

        Cleanup();

        // Process() выходит сам, как только сеанс закрыт. Ждём недолго и не настаиваем:
        // подвесить закрытие приложения из-за счётчика кадров было бы нелепо
        try { _pump?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _pump = null;
        _counters.Clear();
        Interlocked.Exchange(ref _eventsSeen, 0);
        Interlocked.Exchange(ref _presentsMatched, 0);
        Status = "выключен";
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
