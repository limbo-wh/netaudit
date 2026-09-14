using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace NetAudit.Core.Diagnostics;

/// <summary>Что намерили на задаче, по устройству совпадающей с работой нейросети.</summary>
public sealed record GpuLlmResult
{
    /// <summary>Вычисления в половинной точности, триллионов операций в секунду.</summary>
    public double Fp16Tflops { get; init; }

    /// <summary>То же в одинарной точности — для сравнения.</summary>
    public double Fp32Tflops { get; init; }

    public bool Fp16Supported { get; init; }
    public string? Error { get; init; }

    /// <summary>Во сколько раз половинная точность быстрее одинарной.</summary>
    public double Speedup => Fp32Tflops > 0 ? Fp16Tflops / Fp32Tflops : 0;
}

/// <summary>
/// Замер того, что определяет скорость языковой модели на этой видеокарте:
/// вычисления в половинной точности.
///
/// Почему половинная точность: языковые модели хранят веса в 16 битах (а чаще
/// и вовсе в 4–8), и обработка запроса упирается в скорость именно таких
/// вычислений. Одинарная точность меряется рядом для сравнения — отношение
/// показывает, умеет ли видеокарта считать половинную точность вдвое быстрее
/// (так делают все карты начиная примерно с 2016 года) или выполняет её как
/// обычную.
///
/// **Чего этот замер не делает.** Он не задействует тензорные блоки — отдельные
/// матричные ускорители, которые есть у NVIDIA начиная с Volta и дают ещё
/// несколько крат сверху. Из шейдера Direct3D 11 к ним не подобраться; штатный
/// путь — DirectML, и он в этом проекте пока не заработал (попытка приводила к
/// сбросу устройства Direct3D 12, см. tasks.md). Поэтому пиковые цифры
/// производителя для тензорных блоков здесь честно не подтверждаются и не
/// опровергаются — измеряется то, что можно измерить надёжно.
///
/// Вторая половина ответа про языковые модели — пропускная способность
/// видеопамяти: скорость выдачи слов упирается именно в неё, и её меряет
/// <see cref="GpuBenchmark"/>.
/// </summary>
public sealed class GpuLlmBenchmark : IDiagnosticTest
{
    public string Title => "Замер для нейросетей";

    private const long ElementCount = 4 * 1024 * 1024;   // потоков в замере
    private const int GroupSize = 256;
    private const int Repeats = 5;

    private const string ShaderSource = """
        RWStructuredBuffer<float4>      Out32 : register(u0);
        RWStructuredBuffer<min16float4> Out16 : register(u1);

        cbuffer Params : register(b0)
        {
            uint Count;
            uint Iterations;
            uint Half;        // 1 — считать в половинной точности
            uint Pad;
        };

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= Count) return;

            if (Half == 0)
            {
                float4 a = float4(1.0000001f, 0.9999999f, 1.0000002f, 0.9999998f);
                float4 b = float4(0.0000002f, -0.0000001f, 0.0000003f, -0.0000002f);
                float4 v0 = Out32[id.x];
                float4 v1 = v0 + 0.5f;
                float4 v2 = v0 + 1.5f;
                float4 v3 = v0 + 2.5f;

                [loop]
                for (uint i = 0; i < Iterations; i++)
                {
                    v0 = mad(v0, a, b);
                    v1 = mad(v1, a, b);
                    v2 = mad(v2, a, b);
                    v3 = mad(v3, a, b);
                }

                Out32[id.x] = v0 + v1 + v2 + v3;
            }
            else
            {
                // min16float — просьба к видеокарте считать с точностью не ниже
                // 16 бит. Карты, умеющие половинную точность, выполняют такие
                // операции вдвое быстрее обычных; остальные честно считают их
                // как одинарные, и тогда разницы в замере не будет
                min16float4 a = min16float4(1.001, 0.999, 1.002, 0.998);
                min16float4 b = min16float4(0.002, -0.001, 0.003, -0.002);
                min16float4 v0 = Out16[id.x];
                min16float4 v1 = v0 + min16float(0.5);
                min16float4 v2 = v0 + min16float(1.5);
                min16float4 v3 = v0 + min16float(2.5);

                [loop]
                for (uint i = 0; i < Iterations; i++)
                {
                    v0 = mad(v0, a, b);
                    v1 = mad(v1, a, b);
                    v2 = mad(v2, a, b);
                    v3 = mad(v3, a, b);
                }

                Out16[id.x] = v0 + v1 + v2 + v3;
            }
        }
        """;

    /// <summary>Сколько ждать видеокарту, прежде чем считать, что драйвер сорвался.</summary>
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(10);

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _buf32, _buf16, _constants;
    private ID3D11UnorderedAccessView? _uav32, _uav16;

    public GpuLlmResult? Result { get; private set; }

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Производительность для нейросетей"));
        log.Report(TestLine.Dim("Языковые модели считают в половинной точности — её и меряем."));
        log.Report(TestLine.Empty);

        await Task.Run(() => Run(log, ct), ct).ConfigureAwait(false);
    }

    private void Run(IProgress<TestLine> log, CancellationToken ct)
    {
        try
        {
            if (!Initialize(log)) return;

            bool fp16 = CheckFp16Support();

            double tf32 = Measure(half: false, ct);
            log.Report(TestLine.Info(Fmt.Row("Одинарная точность (FP32)", $"{tf32,8:F1} TFLOPS")));

            double tf16 = Measure(half: true, ct);
            log.Report(TestLine.Info(Fmt.Row("Половинная точность (FP16)", $"{tf16,8:F1} TFLOPS")));

            Result = new GpuLlmResult
            {
                Fp32Tflops = tf32,
                Fp16Tflops = tf16,
                Fp16Supported = fp16,
            };

            log.Report(TestLine.Empty);
            Interpret(log, Result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Замер не выполнился: {ex.Message}"));
            Result = new GpuLlmResult { Error = ex.Message };
        }
        finally
        {
            Cleanup();
        }
    }

    private static void Interpret(IProgress<TestLine> log, GpuLlmResult r)
    {
        double speedup = r.Speedup;
        log.Report(TestLine.Info(Fmt.Row("Выигрыш половинной точности", $"×{speedup:F1}")));

        if (speedup >= 1.6)
            log.Report(TestLine.Good("   Видеокарта считает половинную точность быстрее обычной —"
                                   + " для нейросетей это и нужно."));
        else
            log.Report(TestLine.Warn("   Выигрыша нет: половинная точность выполняется как обычная."));

        log.Report(TestLine.Empty);
        log.Report(TestLine.Dim("Тензорные блоки (матричные ускорители) этим замером не задействованы:"));
        log.Report(TestLine.Dim("из обычного шейдера к ним доступа нет. На деле языковая модель через"));
        log.Report(TestLine.Dim("готовый движок вроде llama.cpp или ONNX Runtime использует их и получает"));
        log.Report(TestLine.Dim("ещё несколько крат — то есть эта цифра осторожная, а не завышенная."));
    }

    private bool Initialize(IProgress<TestLine> log)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        // Тот же адаптер, что и в остальных замерах, — см. GpuDeviceFactory
        var created = GpuDeviceFactory.Create(levels, DeviceCreationFlags.None);
        var device = created?.Device;
        var context = created?.Context;

        if (device is null || context is null)
        {
            log.Report(TestLine.Bad("Не удалось создать устройство Direct3D 11"));
            return false;
        }

        _device = device;
        _context = context;

        var compile = Compiler.Compile(ShaderSource, "main", "netaudit_llm.hlsl", "cs_5_0",
                                       out var blob, out var errors);
        using (errors)
        {
            if (compile.Failure || blob is null)
            {
                log.Report(TestLine.Bad("Шейдер не скомпилировался: " + (errors?.AsString() ?? compile.Description)));
                return false;
            }
        }
        using (blob) { _shader = _device.CreateComputeShader(blob.AsSpan()); }

        _buf32 = _device.CreateBuffer(new BufferDescription(
            (uint)(ElementCount * 16), BindFlags.UnorderedAccess, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
        _uav32 = _device.CreateUnorderedAccessView(_buf32);

        // Для половинной точности элемент вдвое меньше
        _buf16 = _device.CreateBuffer(new BufferDescription(
            (uint)(ElementCount * 8), BindFlags.UnorderedAccess, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 8));
        _uav16 = _device.CreateUnorderedAccessView(_buf16);

        _constants = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));

        return true;
    }

    /// <summary>Объявляет ли видеокарта поддержку пониженной точности в шейдерах.</summary>
    private bool CheckFp16Support()
    {
        try
        {
            var p = _device!.CheckFeatureSupport<FeatureDataShaderMinPrecisionSupport>(
                Vortice.Direct3D11.Feature.ShaderMinPrecisionSupport);
            return (int)p.AllOtherShaderStagesMinPrecision != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Подбирает число итераций под скорость карты: цель — вызов около 40 мс.
    /// Начинаем с малого и увеличиваем, иначе первый же пробный вызов на слабой
    /// карте окажется тем самым долгим, от которого мы и защищаемся.
    /// </summary>
    private uint Calibrate(bool half, CancellationToken ct)
    {
        const double TargetMs = 40;
        const uint MaxIterations = 8192;

        uint probe = 32;
        double ms;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            Dispatch(half, probe);
            Sync();
            sw.Stop();

            ms = sw.Elapsed.TotalMilliseconds;
            if (ms >= 1) break;
            if (probe >= MaxIterations) return MaxIterations;

            probe *= 4;
        }

        return (uint)Math.Clamp(probe * (TargetMs / ms), 16, MaxIterations);
    }

    private double Measure(bool half, CancellationToken ct)
    {
        // Подбираем нагрузку под конкретную карту. Прежние жёсткие 4096 итераций на
        // встроенной графике считались дольше двух секунд — порога, после которого
        // Windows перезапускает видеодрайвер
        uint Iterations = Calibrate(half, ct);
        double best = 0;

        // Прогрев: первый вызов включает компиляцию под конкретную карту
        Dispatch(half, Iterations);
        Sync();

        for (int i = 0; i < Repeats; i++)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            Dispatch(half, Iterations);
            Sync();
            sw.Stop();

            if (sw.Elapsed.TotalSeconds <= 0) continue;   // иначе в отчёт попадёт бесконечность

            // 4 цепочки × 4 компоненты × (умножение + сложение)
            double flops = (double)ElementCount * Iterations * 32;
            best = Math.Max(best, flops / sw.Elapsed.TotalSeconds / 1e12);
        }

        return best;
    }

    private void Dispatch(bool half, uint iterations)
    {
        var ctx = _context!;

        Span<uint> parms = [(uint)ElementCount, iterations, half ? 1u : 0u, 0];
        ctx.UpdateSubresource(parms, _constants!);

        ctx.CSSetShader(_shader);
        ctx.CSSetConstantBuffer(0, _constants);
        ctx.CSSetUnorderedAccessView(0, _uav32);
        ctx.CSSetUnorderedAccessView(1, _uav16);

        ctx.Dispatch((uint)((ElementCount + GroupSize - 1) / GroupSize), 1, 1);
    }

    /// <summary>Ждёт, пока видеокарта действительно доделает работу.</summary>
    private void Sync()
    {
        var ctx = _context!;
        using var query = _device!.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));

        ctx.End(query);
        ctx.Flush();

        // Ждём не бесконечно: после срыва видеодрайвера (TDR) запрос никогда не
        // завершится, и прежний цикл жёг целое ядро до самого закрытия программы,
        // не реагируя даже на кнопку «Остановить»
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (!ctx.GetData(query, out int done) || done == 0)
        {
            if (sw.Elapsed > SyncTimeout)
                throw new TimeoutException(
                    "Видеокарта не ответила за " + SyncTimeout.TotalSeconds.ToString("F0") +
                    " с — похоже на срыв драйвера. Тест остановлен.");

            Thread.SpinWait(64);
        }
    }

    private void Cleanup()
    {
        _uav16?.Dispose();
        _uav32?.Dispose();
        _buf16?.Dispose();
        _buf32?.Dispose();
        _constants?.Dispose();
        _shader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _uav16 = null; _uav32 = null; _buf16 = null; _buf32 = null;
        _constants = null; _shader = null; _context = null; _device = null;
    }
}
