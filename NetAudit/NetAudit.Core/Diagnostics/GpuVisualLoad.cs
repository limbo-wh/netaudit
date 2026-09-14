using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NetAudit.Core.Diagnostics;

/// <summary>Мгновенное состояние видимой нагрузки.</summary>
public readonly record struct GpuVisualFrame(
    double Fps,
    double FrameMs,
    double OnePercentLowFps,
    long TotalFrames,
    int Complexity);

/// <summary>
/// Нагрузка видеокарты с картинкой на экране — то же, что делает FurMark своим
/// «мохнатым бубликом».
///
/// Картинка здесь не украшение и не замена замеру: видеокарта греется от расчёта
/// кадра, а вывод на экран стоит доли процента. Смысл в наблюдаемости — видно,
/// что нагрузка идёт, и видно, как ведут себя кадры: ровно или рывками.
///
/// Рисуется вращающийся тор, покрытый «мехом», — дань тому самому бублику.
/// Вся сцена считается одним пиксельным шейдером методом трассировки лучей:
/// геометрии нет вовсе, есть формула расстояния до поверхности, и для каждого
/// пикселя луч шагает к ней. Это дорого ровно так, как нужно тесту: вес сцены
/// задаётся количеством пикселей, для каждого из которых считается всё заново.
///
/// Кадры выводятся без синхронизации с монитором (<c>Present(0, ...)</c>):
/// иначе видеокарта простаивала бы между обновлениями экрана, и вместо нагрузки
/// получилась бы заставка.
/// </summary>
public sealed class GpuVisualLoad : IDisposable
{
    /// <summary>
    /// Предел шагов луча. Это не регулятор нагрузки, а страховка: луч
    /// останавливается, коснувшись поверхности или уйдя за сцену, и до предела
    /// почти никогда не доходит. Запас нужен ровно для лучей, идущих вдоль
    /// поверхности по касательной, — без него у края бублика появлялась кайма.
    /// Уменьшается только автоподстройкой, если видеокарта не тянет сцену.
    /// </summary>
    public const int DefaultComplexity = 420;

    /// <summary>Кадр дольше этого — сцена упрощается: до порога перезапуска драйвера далеко.</summary>
    private const double MaxFrameMs = 200;

    private const int MinComplexity = 12;

    /// <summary>
    /// По скольким последним кадрам считается статистика. Про запас взято
    /// около десяти секунд: худший процент — это самые медленные кадры окна,
    /// и на коротком окне их набирается всего пара штук. Тогда показатель
    /// прыгает от любой единичной заминки композитора Windows и описывает
    /// не плавность картинки, а случайность.
    /// </summary>
    private const int StatsWindow = 1024;

    /// <summary>
    /// Внутреннее разрешение отрисовки. Сцена всегда считается в нём и только
    /// потом растягивается на окно — иначе нагрузка зависела бы от того,
    /// насколько пользователь растянул окно: в маленьком пикселей вчетверо
    /// меньше, и видеокарта загружалась на 60% вместо полной.
    /// </summary>
    /// <remarks>
    /// Разрешение выбрано выше типового экрана намеренно: нагрузка регулируется
    /// именно количеством пикселей. Через число шагов луча её не поднять — лучи
    /// завершаются раньше предела, и увеличение потолка ничего не меняет: при
    /// росте с 220 до 420 шагов время кадра осталось прежним.
    ///
    /// На 2560×1440 RTX 2080 SUPER выдавала 167 кадров при 80% загрузки и 177 Вт
    /// из 240 предельных — карта не была занята полностью. Вчетверо больше
    /// пикселей (3840×2160) догружают её до предела, а кадров остаётся столько,
    /// что картинка по-прежнему движется плавно.
    /// </remarks>
    private const int RenderWidth = 3840;
    private const int RenderHeight = 2160;

    private const string ShaderSource = """
        struct VsOut
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        VsOut VsMain(uint id : SV_VertexID)
        {
            VsOut o;
            o.uv  = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        // Перенос готового кадра на экран: собственно нагрузки не создаёт.
        // Имя намеренно не «Scene» — так ниже называется функция расстояния,
        // и компилятор шейдеров отказывается принимать два разных смысла
        Texture2D    RenderedFrame : register(t0);
        SamplerState LinearSampler : register(s0);

        float4 PsBlit(VsOut input) : SV_TARGET
        {
            return RenderedFrame.SampleLevel(LinearSampler, input.uv, 0);
        }

        cbuffer Params : register(b0)
        {
            uint  Steps;        // предел шагов трассировки — страховка от зацикливания
            float Time;
            float2 Resolution;
        };

        float Hash(float3 p)
        {
            p = frac(p * 0.3183099 + 0.1);
            p *= 17.0;
            return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
        }

        // Плавный шум: из него делается «мех» на поверхности бублика
        float Noise(float3 x)
        {
            float3 i = floor(x);
            float3 f = frac(x);
            f = f * f * (3.0 - 2.0 * f);

            return lerp(lerp(lerp(Hash(i + float3(0,0,0)), Hash(i + float3(1,0,0)), f.x),
                             lerp(Hash(i + float3(0,1,0)), Hash(i + float3(1,1,0)), f.x), f.y),
                        lerp(lerp(Hash(i + float3(0,0,1)), Hash(i + float3(1,0,1)), f.x),
                             lerp(Hash(i + float3(0,1,1)), Hash(i + float3(1,1,1)), f.x), f.y), f.z);
        }

        float2x2 Rotate(float a)
        {
            float s = sin(a), c = cos(a);
            return float2x2(c, -s, s, c);
        }

        // Расстояние до поверхности тора, обросшего мехом
        float Scene(float3 p)
        {
            // Наклон постоянный, вращение — только вокруг собственной оси.
            // Свободное вращение по двум осям регулярно поворачивало бублик
            // ребром к зрителю, и он читался как бесформенный камень
            p.yz = mul(p.yz, Rotate(0.95));
            p.xz = mul(p.xz, Rotate(Time * 0.5));

            // Кольцо тоньше, чем было: при толщине 0.38 дырка почти затягивалась,
            // и фигура читалась как приплюснутый камень
            float2 q = float2(length(p.xz) - 1.0, p.y);
            float torus = length(q) - 0.26;

            // Шерсть: слои шума разного масштаба поверх гладкой поверхности.
            // Каждый слой считается для каждого шага луча и для каждой из трёх
            // точек, по которым берётся нормаль, — отсюда и вес сцены
            // Амплитуды намеренно небольшие: при крупном шуме бублик заплывал
            // в бесформенный камень, и узнать в нём фигуру было нельзя
            float fur = Noise(p * 9.0 + Time * 0.4) * 0.030
                      + Noise(p * 23.0 - Time * 0.2) * 0.014
                      + Noise(p * 47.0 + Time * 0.15) * 0.007
                      + Noise(p * 97.0 - Time * 0.1) * 0.004
                      + Noise(p * 181.0) * 0.002;

            return torus - fur + 0.02;
        }

        float3 Normal(float3 p)
        {
            float2 e = float2(0.002, 0);
            return normalize(float3(
                Scene(p + e.xyy) - Scene(p - e.xyy),
                Scene(p + e.yxy) - Scene(p - e.yxy),
                Scene(p + e.yyx) - Scene(p - e.yyx)));
        }

        float4 PsMain(VsOut input) : SV_TARGET
        {
            float2 uv = (input.uv * 2.0 - 1.0);
            uv.x *= Resolution.x / max(Resolution.y, 1.0);

            float3 ro = float3(0, 0, -3.2);              // откуда смотрим
            float3 rd = normalize(float3(uv, 1.6));      // куда смотрит луч

            float t = 0.0;
            bool hit = false;

            [loop]
            for (uint i = 0; i < Steps; i++)
            {
                float3 p = ro + rd * t;
                float d = Scene(p);

                if (d < 0.002) { hit = true; break; }
                if (t > 8.0) break;

                t += d * 0.75;
            }

            // Фон — тёмно-зелёный, как всё оформление программы
            float3 color = lerp(float3(0.02, 0.07, 0.05),
                                float3(0.04, 0.12, 0.08),
                                1.0 - length(uv) * 0.4);

            if (hit)
            {
                float3 p = ro + rd * t;
                float3 n = Normal(p);

                float3 lightDir = normalize(float3(0.6, 0.8, -0.5));
                float diffuse = saturate(dot(n, lightDir));
                float rim = pow(1.0 - saturate(dot(n, -rd)), 2.5);

                float3 warm = float3(0.31, 0.85, 0.55);   // зелёный акцент программы
                float3 cool = float3(0.10, 0.35, 0.30);

                color = cool * 0.35
                      + warm * diffuse * 0.9
                      + float3(0.55, 0.95, 0.70) * rim * 0.5;

                // Затемнение по глубине: дальние части бублика темнее
                color *= saturate(1.3 - t * 0.18);
            }

            return float4(color, 1);
        }
        """;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11PixelShader? _blit;
    private ID3D11Buffer? _constants;

    // Сцена рисуется сюда в постоянном разрешении, а на экран уже растягивается
    private ID3D11Texture2D? _sceneTexture;
    private ID3D11RenderTargetView? _sceneRtv;
    private ID3D11ShaderResourceView? _sceneSrv;
    private ID3D11SamplerState? _sampler;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private int _complexity = DefaultComplexity;
    private int _width, _height;

    /// <summary>Вызывается примерно раз в полсекунды с текущими числами.</summary>
    public Action<GpuVisualFrame>? OnFrame { get; set; }

    /// <summary>Сообщение об ошибке, если запуск не удался.</summary>
    public string? Error { get; private set; }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Сколько кадров не удалось отрисовать. Больше нуля — нагрузка ненастоящая.</summary>
    public long FailedFrames => _failedFrames;

    private long _failedFrames;

    /// <summary>
    /// Запускает отрисовку в указанное окно. Окно должно существовать; его размер
    /// читается сам и отслеживается по ходу.
    /// </summary>
    public bool Start(IntPtr hwnd, int width, int height)
    {
        try
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            if (!Initialize(hwnd)) return false;

            _cts = new CancellationTokenSource();
            _thread = new Thread(() => Loop(_cts.Token))
            {
                Name = "NetAudit-gpu-visual",
                IsBackground = true,
            };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Cleanup();
            return false;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _thread?.Join(TimeSpan.FromSeconds(3)); } catch { }
        _thread = null;
        Cleanup();
    }

    /// <summary>Сообщает о новом размере окна: цепочку буферов надо пересоздать.</summary>
    public void Resize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _resizePending = true;
    }

    private volatile bool _resizePending;
    private bool _tearingAllowed;

    /// <summary>Поддерживает ли система показ кадра без ожидания обновления экрана.</summary>
    private static bool SupportsTearing(IDXGIFactory2 factory)
    {
        try
        {
            using var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>();
            return factory5?.PresentAllowTearing == true;
        }
        catch
        {
            return false;
        }
    }

    // ── Подготовка ────────────────────────────────────────────────────────

    private bool Initialize(IntPtr hwnd)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        var hr = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context);

        if (hr.Failure || device is null || context is null)
        {
            Error = $"Direct3D 11 недоступен: {hr.Description}";
            return false;
        }

        _device = device;
        _context = context;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();

        // Глубина очереди кадров. По умолчанию драйвер копит до трёх кадров
        // вперёд: Present возвращается то мгновенно, то с задержкой на всю
        // очередь, и замеренное время кадра относится не к тому кадру, который
        // сейчас на экране. С очередью в один кадр числа в окне описывают то,
        // что видит глаз. Разброс худшего процента, ради которого это и
        // пробовалось, при этом не изменился — его причина в другом: единичные
        // задержки композитора Windows, а не глубина очереди
        using (var dxgiDevice1 = dxgiDevice.QueryInterfaceOrNull<IDXGIDevice1>())
        {
            if (dxgiDevice1 is not null) dxgiDevice1.MaximumFrameLatency = 1;
        }

        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        // Без этого композитор Windows выдаёт кадры ровно с частотой монитора,
        // сколько ни проси не ждать: на 144-герцевом экране получалось 144 кадра
        // и четверть загрузки видеокарты вместо полной. Флаг разрешает показывать
        // кадр сразу, не дожидаясь обновления экрана; картинка при этом может
        // «рваться» по горизонтали — для теста нагрузки это не порок, а признак,
        // что ограничения сняты
        _tearingAllowed = SupportsTearing(factory);

        var desc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = _tearingAllowed ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, desc);

        // Alt+Enter на весь экран здесь не нужен и только мешает
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        if (!CompileShaders()) return false;

        _constants = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));

        CreateRenderTarget();
        CreateSceneTarget();
        return true;
    }

    private bool CompileShaders()
    {
        var vs = Compiler.Compile(ShaderSource, "VsMain", "netaudit_visual.hlsl", "vs_5_0",
                                  out var vsBlob, out var vsErr);
        using (vsErr)
        {
            if (vs.Failure || vsBlob is null)
            {
                Error = "вершинный шейдер: " + (vsErr?.AsString() ?? vs.Description);
                return false;
            }
        }
        using (vsBlob) { _vs = _device!.CreateVertexShader(vsBlob.AsSpan()); }

        var ps = Compiler.Compile(ShaderSource, "PsMain", "netaudit_visual.hlsl", "ps_5_0",
                                  out var psBlob, out var psErr);
        using (psErr)
        {
            if (ps.Failure || psBlob is null)
            {
                Error = "пиксельный шейдер: " + (psErr?.AsString() ?? ps.Description);
                return false;
            }
        }
        using (psBlob) { _ps = _device!.CreatePixelShader(psBlob.AsSpan()); }

        var blit = Compiler.Compile(ShaderSource, "PsBlit", "netaudit_visual.hlsl", "ps_5_0",
                                    out var blitBlob, out var blitErr);
        using (blitErr)
        {
            if (blit.Failure || blitBlob is null)
            {
                Error = "шейдер переноса: " + (blitErr?.AsString() ?? blit.Description);
                return false;
            }
        }
        using (blitBlob) { _blit = _device!.CreatePixelShader(blitBlob.AsSpan()); }

        return true;
    }

    /// <summary>Постоянная по размеру цель отрисовки — от неё не зависит размер окна.</summary>
    private void CreateSceneTarget()
    {
        _sceneTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = RenderWidth,
            Height = RenderHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });

        _sceneRtv = _device.CreateRenderTargetView(_sceneTexture);
        _sceneSrv = _device.CreateShaderResourceView(_sceneTexture);

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue,
        });
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(backBuffer);
    }

    // ── Цикл отрисовки ────────────────────────────────────────────────────

    private void Loop(CancellationToken ct)
    {
        var recent = new Queue<double>(StatsWindow);
        var sw = Stopwatch.StartNew();
        var report = Stopwatch.StartNew();
        var life = Stopwatch.StartNew();
        long frames = 0;
        bool calibrated = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_resizePending) ApplyResize();

                sw.Restart();
                RenderFrame((float)life.Elapsed.TotalSeconds);
                _swapChain!.Present(0, _tearingAllowed ? PresentFlags.AllowTearing : PresentFlags.None);
                sw.Stop();

                double ms = sw.Elapsed.TotalMilliseconds;
                frames++;

                // Первый настоящий кадр решает, потянет ли видеокарта эту сцену.
                // На слабой карте длинный кадр довёл бы до перезапуска драйвера
                if (!calibrated && frames > 8)
                {
                    calibrated = true;
                    if (ms > MaxFrameMs)
                        _complexity = Math.Max(MinComplexity, (int)(_complexity * (MaxFrameMs / ms)));
                }

                if (recent.Count == StatsWindow) recent.Dequeue();
                recent.Enqueue(ms);

                if (report.Elapsed.TotalMilliseconds >= 500)
                {
                    report.Restart();
                    ReportStats(recent, frames);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Отдельный сбой кадра не должен ронять окно, но и молчать нельзя:
                // повторяющаяся ошибка превращает нагрузку в видимость работы —
                // кадры «идут», а видеокарта простаивает
                Error ??= ex.Message;
                _failedFrames++;
                Thread.Sleep(50);
            }
        }
    }

    private void ReportStats(Queue<double> recent, long frames)
    {
        if (OnFrame is null || recent.Count == 0) return;

        var sorted = recent.ToArray();
        Array.Sort(sorted);

        double average = sorted.Average();
        int tail = Math.Max(1, sorted.Length / 100);
        double low = sorted.TakeLast(tail).Average();

        OnFrame(new GpuVisualFrame(
            Fps: average > 0 ? 1000.0 / average : 0,
            FrameMs: average,
            OnePercentLowFps: low > 0 ? 1000.0 / low : 0,
            TotalFrames: frames,
            Complexity: _complexity));
    }

    private void ApplyResize()
    {
        _resizePending = false;

        try
        {
            _rtv?.Dispose();
            _rtv = null;

            _swapChain!.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm,
                                      _tearingAllowed ? SwapChainFlags.AllowTearing : SwapChainFlags.None);
            CreateRenderTarget();
        }
        catch { }
    }

    private void RenderFrame(float time)
    {
        var ctx = _context!;

        Span<uint> parms =
        [
            (uint)_complexity,
            BitConverter.SingleToUInt32Bits(time),
            BitConverter.SingleToUInt32Bits(RenderWidth),
            BitConverter.SingleToUInt32Bits(RenderHeight),
        ];
        ctx.UpdateSubresource(parms, _constants!);

        ctx.VSSetShader(_vs);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Первый проход — сама сцена, всегда в постоянном разрешении
        ctx.OMSetRenderTargets(_sceneRtv!);
        ctx.RSSetViewport(new Viewport(0, 0, RenderWidth, RenderHeight, 0, 1));
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _constants);
        ctx.PSSetShaderResource(0, null!);  // null здесь законен: отвязать текстуру
        ctx.Draw(3, 0);

        // Второй — перенос готового кадра в окно с растяжением под его размер
        ctx.OMSetRenderTargets(_rtv!);
        ctx.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        ctx.PSSetShader(_blit);
        ctx.PSSetShaderResource(0, _sceneSrv!);
        ctx.PSSetSampler(0, _sampler);
        ctx.Draw(3, 0);

        // Текстуру нельзя оставлять привязанной: следующий кадр будет в неё писать
        ctx.PSSetShaderResource(0, null!);  // null здесь законен: отвязать текстуру
    }

    private void Cleanup()
    {
        try { _cts?.Dispose(); } catch { }
        _cts = null;

        _sampler?.Dispose();

        _sceneSrv?.Dispose();

        _sceneRtv?.Dispose();

        _sceneTexture?.Dispose();

        _constants?.Dispose();

        _blit?.Dispose();

        _ps?.Dispose();
        _vs?.Dispose();
        _rtv?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _sampler = null; _sceneSrv = null; _sceneRtv = null; _sceneTexture = null;

        _constants = null; _blit = null; _ps = null; _vs = null;
        _rtv = null; _swapChain = null; _context = null; _device = null;
    }

    public void Dispose() => Stop();
}
