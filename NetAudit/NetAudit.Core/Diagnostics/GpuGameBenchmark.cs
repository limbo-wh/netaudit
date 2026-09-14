using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NetAudit.Core.Diagnostics;

/// <summary>Итог игрового замера.</summary>
public sealed record GpuGameResult
{
    public double AverageFps { get; init; }

    /// <summary>Кадры в секунду в худшем проценте кадров — то, что ощущается как рывки.</summary>
    public double OnePercentLowFps { get; init; }

    public double AverageFrameMs { get; init; }
    public double WorstFrameMs { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
    public string? Error { get; init; }

    public bool Ok => Error is null && AverageFps > 0;

    /// <summary>Насколько ровно идут кадры. Чем ближе к единице, тем плавнее картинка.</summary>
    public double Smoothness => AverageFps > 0 ? OnePercentLowFps / AverageFps : 0;
}

/// <summary>
/// Игровой замер: видеокарта рисует кадры так же, как в игре, — через растеризацию,
/// с тяжёлым пиксельным шейдером и выборкой из текстуры.
///
/// Почему это отдельно от остальных замеров. Вычислительный шейдер нагружает
/// только математические блоки. В игре кроме них работают растеризатор, блоки
/// выборки текстур, кэши и вывод в кадровый буфер — и узким местом часто
/// оказываются именно они. Поэтому «столько-то TFLOPS» и «столько-то кадров»
/// связаны куда слабее, чем кажется.
///
/// Кадр рисуется в собственную текстуру, а не в окно: окно потребовало бы
/// синхронизации с монитором, и замер уперся бы в частоту обновления экрана,
/// а не в возможности видеокарты.
///
/// Кроме среднего числа кадров считается «худший процент» — время, в которое
/// укладываются 99% кадров. Именно редкие долгие кадры ощущаются как рывки,
/// и по среднему их не видно вовсе.
/// </summary>
public sealed class GpuGameBenchmark(int seconds = 12) : IDiagnosticTest
{
    public string Title => "Игровой замер видеокарты";

    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>
    /// Сколько раз повторяется тяжёлая часть шейдера на каждый пиксель. Подобрано
    /// так, чтобы результат попадал в привычный игровой диапазон: на RTX 2080 SUPER
    /// выходит около сотни кадров, на слабой карте — три-четыре десятка, на сильной
    /// — пара сотен. При меньшей сложности карта выдавала пятьсот кадров в секунду,
    /// и число переставало что-либо говорить на глаз.
    /// </summary>
    private const int DefaultComplexity = 800;

    /// <summary>
    /// Кадр дольше этого — повод упростить сцену. Порог, после которого Windows
    /// считает видеодрайвер зависшим и перезапускает его, равен двум секундам;
    /// четверти секунды хватает, чтобы до него было далеко даже с запасом на
    /// случайную заминку.
    /// </summary>
    private const double MaxFrameMs = 250;

    /// <summary>Ниже этого упрощать бессмысленно — сцена перестаёт быть нагрузкой.</summary>
    private const int MinComplexity = 25;

    /// <summary>Фактически использованная сложность. Меньше стандартной — сцену упростили.</summary>
    private int _complexity = DefaultComplexity;

    private const string ShaderSource = """
        // Полноэкранный треугольник строится без вершинного буфера: три вершины
        // выводятся прямо из номера вершины. Меньше кода и никакой возни с буферами
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

        Texture2D    Tex : register(t0);
        SamplerState Smp : register(s0);

        cbuffer Params : register(b0)
        {
            uint  Complexity;
            float Time;
            float2 Padding;
        };

        // Шум — основа почти любого игрового эффекта: от освещения до тумана.
        // Считается на каждый пиксель, поэтому нагружает ровно то же, что игра
        float Noise(float2 p)
        {
            return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
        }

        float4 PsMain(VsOut i) : SV_TARGET
        {
            float3 color = float3(0, 0, 0);
            float2 uv = i.uv;

            [loop]
            for (uint k = 0; k < Complexity; k++)
            {
                float fk = (float)k;

                // Выборка из текстуры по смещающимся координатам: блоки выборки
                // и кэш текстур нагружаются так же, как при наложении материалов
                float2 offset = float2(sin(fk * 0.37 + Time), cos(fk * 0.53 + Time)) * 0.03;
                float4 sampled = Tex.SampleLevel(Smp, uv + offset, 0);

                float n = Noise(uv * (fk + 1.0) + Time);
                float3 light = normalize(float3(sin(fk), cos(fk), 1.0));
                float diffuse = saturate(dot(light, float3(0, 0, 1))) * n;

                color += sampled.rgb * diffuse * 0.01;
            }

            return float4(color, 1);
        }
        """;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _constants;

    public GpuGameResult? Result { get; private set; }

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Игровой замер"));
        log.Report(TestLine.Dim($"Видеокарта рисует кадры {Width}×{Height} с тяжёлым шейдером —"));
        log.Report(TestLine.Dim("растеризация, выборка текстур и освещение, как в настоящей игре."));
        log.Report(TestLine.Empty);

        await Task.Run(() => Run(log, ct), ct).ConfigureAwait(false);
    }

    private void Run(IProgress<TestLine> log, CancellationToken ct)
    {
        try
        {
            if (!Initialize(log)) return;

            // Прогрев: первые кадры всегда медленнее — видеокарта ещё поднимает
            // частоты, а драйвер компилирует шейдер под конкретное железо
            for (int i = 0; i < 30; i++) RenderFrame(i);
            Sync();

            Calibrate(log, ct);

            var frames = new List<double>(4096);
            var sw = Stopwatch.StartNew();
            var total = Stopwatch.StartNew();
            int frame = 0;

            while (total.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                ct.ThrowIfCancellationRequested();

                sw.Restart();
                RenderFrame(frame++);
                Sync();
                sw.Stop();

                frames.Add(sw.Elapsed.TotalMilliseconds);
            }

            total.Stop();

            if (frames.Count < 10)
            {
                log.Report(TestLine.Bad("Кадров получилось слишком мало для оценки"));
                return;
            }

            frames.Sort();

            double average = frames.Average();
            // Худший процент: берём кадры из длинного хвоста, они и ощущаются как рывки
            int tail = Math.Max(1, frames.Count / 100);
            double onePercentLow = frames.TakeLast(tail).Average();

            Result = new GpuGameResult
            {
                AverageFps       = 1000.0 / average,
                OnePercentLowFps = 1000.0 / onePercentLow,
                AverageFrameMs   = average,
                WorstFrameMs     = frames[^1],
                Width            = Width,
                Height           = Height,
            };

            Report(log, Result, frames.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Замер не выполнился: {ex.Message}"));
            Result = new GpuGameResult { Error = ex.Message };
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// Упрощает сцену, если видеокарта её не тянет.
    ///
    /// Сложность подобрана под карту уровня RTX 2080 SUPER. На встроенной графике,
    /// которая в десятки раз медленнее, тот же кадр считался бы секунды — а через
    /// две секунды Windows объявляет видеодрайвер зависшим и перезапускает его.
    /// Тест, который роняет драйвер на слабой машине, — плохой тест, поэтому здесь
    /// замеряется один настоящий кадр и сложность снижается до посильной.
    ///
    /// Обратное упрощение не делается: на быстрой карте сложность остаётся
    /// стандартной, иначе числа кадров с разных машин было бы не сравнить.
    /// </summary>
    private void Calibrate(IProgress<TestLine> log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        RenderFrame(0);
        Sync();
        sw.Stop();

        double frameMs = sw.Elapsed.TotalMilliseconds;
        if (frameMs <= MaxFrameMs) return;

        ct.ThrowIfCancellationRequested();

        int reduced = (int)Math.Max(MinComplexity, _complexity * (MaxFrameMs / frameMs));
        log.Report(TestLine.Warn(
            $"Кадр занимает {frameMs:F0} мс — сцена упрощена с {_complexity} до {reduced}"));
        log.Report(TestLine.Dim("   Видеокарта не тянет стандартную сложность. Так тест не доведёт"));
        log.Report(TestLine.Dim("   драйвер до перезапуска по таймауту, но число кадров станет"));
        log.Report(TestLine.Dim("   несравнимым с результатами более быстрых карт."));
        log.Report(TestLine.Empty);

        _complexity = reduced;

        // Ещё раз прогреваем на новой сложности: старые кадры мерились на другой сцене
        for (int i = 0; i < 10; i++) RenderFrame(i);
        Sync();
    }

    private static void Report(IProgress<TestLine> log, GpuGameResult r, int frames)
    {
        log.Report(TestLine.Info(Fmt.Row("Кадров отрисовано", $"{frames}")));
        log.Report(TestLine.Info(Fmt.Row("Средний кадр", $"{r.AverageFrameMs,8:F1} мс")));
        log.Report(TestLine.Info(Fmt.Row("Худший кадр", $"{r.WorstFrameMs,8:F1} мс")));
        log.Report(TestLine.Empty);
        log.Report(TestLine.Good(Fmt.Row("Кадров в секунду", $"{r.AverageFps,8:F0}")));
        log.Report(TestLine.Info(Fmt.Row("Худший процент кадров", $"{r.OnePercentLowFps,8:F0}")));
        log.Report(TestLine.Empty);

        double smoothness = r.Smoothness;
        var level = smoothness >= 0.7 ? TestLevel.Good : smoothness >= 0.5 ? TestLevel.Warn : TestLevel.Bad;

        log.Report(new TestLine(Fmt.Row("Ровность кадров", $"{smoothness * 100:F0}%"), level));

        if (smoothness >= 0.7)
            log.Report(TestLine.Dim("   Кадры идут ровно — картинка будет плавной."));
        else if (smoothness >= 0.5)
            log.Report(TestLine.Dim("   Разброс заметный: отдельные кадры выпадают, возможны редкие рывки."));
        else
            log.Report(TestLine.Dim("   Большой разброс. В игре это ощущается как подёргивания даже при"));

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Число кадров здесь — не предсказание для конкретной игры: у каждой свой"));
        log.Report(TestLine.Dim("набор эффектов. Это мера того, на что способна сама видеокарта, и её"));
        log.Report(TestLine.Dim("полезно сравнивать с собой же — до и после чистки, смены драйвера или разгона."));
    }

    // ── Подготовка ────────────────────────────────────────────────────────

    private bool Initialize(IProgress<TestLine> log)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        var hr = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.None, levels,
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context);

        if (hr.Failure || device is null || context is null)
        {
            log.Report(TestLine.Bad($"Не удалось создать устройство Direct3D 11: {hr.Description}"));
            return false;
        }

        _device = device;
        _context = context;

        if (!CompileShaders(log)) return false;

        // Цель отрисовки — своя текстура, а не окно: иначе замер упёрся бы
        // в частоту обновления монитора вместо возможностей видеокарты
        _target = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _rtv = _device.CreateRenderTargetView(_target);

        CreateSourceTexture();

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxLOD = float.MaxValue,
        });

        _constants = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));

        return true;
    }

    private bool CompileShaders(IProgress<TestLine> log)
    {
        var vsResult = Compiler.Compile(ShaderSource, "VsMain", "netaudit_game.hlsl", "vs_5_0",
                                        out var vsBlob, out var vsErrors);
        using (vsErrors)
        {
            if (vsResult.Failure || vsBlob is null)
            {
                log.Report(TestLine.Bad("Вершинный шейдер не скомпилировался: "
                                      + (vsErrors?.AsString() ?? vsResult.Description)));
                return false;
            }
        }
        using (vsBlob) { _vs = _device!.CreateVertexShader(vsBlob.AsSpan()); }

        var psResult = Compiler.Compile(ShaderSource, "PsMain", "netaudit_game.hlsl", "ps_5_0",
                                        out var psBlob, out var psErrors);
        using (psErrors)
        {
            if (psResult.Failure || psBlob is null)
            {
                log.Report(TestLine.Bad("Пиксельный шейдер не скомпилировался: "
                                      + (psErrors?.AsString() ?? psResult.Description)));
                return false;
            }
        }
        using (psBlob) { _ps = _device!.CreatePixelShader(psBlob.AsSpan()); }

        return true;
    }

    /// <summary>Текстура-источник: без неё блоки выборки простаивали бы, а в игре они заняты всегда.</summary>
    private void CreateSourceTexture()
    {
        const int size = 1024;
        var pixels = new uint[size * size];
        var rng = new Random(1);

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (uint)rng.Next(int.MinValue, int.MaxValue);

        unsafe
        {
            fixed (uint* p = pixels)
            {
                var data = new SubresourceData((IntPtr)p, size * sizeof(uint));

                _texture = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = size,
                    Height = size,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                }, [data]);
            }
        }

        _srv = _device!.CreateShaderResourceView(_texture);
    }

    // ── Отрисовка ─────────────────────────────────────────────────────────

    private void RenderFrame(int frame)
    {
        var ctx = _context!;

        Span<uint> parms = [(uint)_complexity, BitConverter.SingleToUInt32Bits(frame * 0.01f), 0, 0];
        ctx.UpdateSubresource(parms, _constants!);

        ctx.OMSetRenderTargets(_rtv!);
        ctx.RSSetViewport(new Viewport(0, 0, Width, Height, 0, 1));

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetShaderResource(0, _srv);
        ctx.PSSetSampler(0, _sampler);
        ctx.PSSetConstantBuffer(0, _constants);

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.Draw(3, 0);
    }

    /// <summary>Дожидается, пока видеокарта действительно нарисует кадр.</summary>
    private void Sync()
    {
        var ctx = _context!;
        using var query = _device!.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));

        ctx.End(query);
        ctx.Flush();

        while (!ctx.GetData(query, out int done) || done == 0)
            Thread.SpinWait(64);
    }

    private void Cleanup()
    {
        _constants?.Dispose();
        _sampler?.Dispose();
        _srv?.Dispose();
        _texture?.Dispose();
        _rtv?.Dispose();
        _target?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _constants = null; _sampler = null; _srv = null; _texture = null;
        _rtv = null; _target = null; _ps = null; _vs = null;
        _context = null; _device = null;
    }
}
