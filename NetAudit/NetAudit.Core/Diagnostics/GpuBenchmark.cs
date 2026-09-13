using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace NetAudit.Core.Diagnostics;

/// <summary>Что намерили у видеокарты.</summary>
public sealed record GpuBenchmarkResult
{
    /// <summary>Чтение из видеопамяти, ГБ/с.</summary>
    public double ReadGbs { get; init; }

    /// <summary>Запись в видеопамять, ГБ/с.</summary>
    public double WriteGbs { get; init; }

    /// <summary>Копирование внутри видеопамяти (чтение + запись), ГБ/с.</summary>
    public double CopyGbs { get; init; }

    /// <summary>Вычисления одинарной точности, триллионов операций в секунду.</summary>
    public double Fp32Tflops { get; init; }

    /// <summary>Передача по шине PCI Express в видеокарту, ГБ/с.</summary>
    public double UploadGbs { get; init; }

    /// <summary>Передача по шине PCI Express обратно, ГБ/с.</summary>
    public double DownloadGbs { get; init; }

    /// <summary>Наибольшая измеренная скорость памяти — её и берут за пропускную способность.</summary>
    public double PeakMemoryGbs => Math.Max(ReadGbs, Math.Max(WriteGbs, CopyGbs));
}

/// <summary>
/// Замеряет то, от чего зависит поведение видеокарты в играх и в нейросетях:
/// скорость видеопамяти, скорость вычислений и скорость шины до компьютера.
///
/// Почему именно эти три:
///   • **память** — главный предел для языковых моделей. Генерация каждого токена
///     требует прочитать все веса модели, поэтому скорость ответа упирается в
///     пропускную способность памяти, а не в вычислительную мощность;
///   • **вычисления** — предел для игр и для обработки длинного запроса, где
///     считаются большие матричные произведения;
///   • **шина PCI Express** — важна, когда модель не влезла в видеопамять и часть
///     весов приходится подтягивать из оперативной. Разница между шиной и
///     видеопамятью здесь десятикратная, отсюда и обвал скорости в таких случаях.
///
/// Каждый замер синхронный: результат забирается с видеокарты, и это гарантирует,
/// что измерено выполнение, а не время постановки команд в очередь.
/// </summary>
public sealed class GpuBenchmark : IDiagnosticTest
{
    public string Title => "Замеры производительности видеокарты";

    private const long BufferBytes = 256L * 1024 * 1024;
    private const int GroupSize = 256;

    /// <summary>Сколько повторов каждого замера; берётся лучший — он ближе к истине.</summary>
    private const int Repeats = 5;

    private const string ShaderSource = """
        RWStructuredBuffer<float4> A : register(u0);
        RWStructuredBuffer<float4> B : register(u1);
        RWStructuredBuffer<uint>   Sink : register(u2);

        cbuffer Params : register(b0)
        {
            uint Count;
            uint Mode;        // 0 — чтение, 1 — запись, 2 — копирование, 3 — вычисления
            uint Iterations;
            uint Pad;
        };

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= Count) return;

            if (Mode == 0)
            {
                // Чтение: сумма должна куда-то деться, иначе компилятор выбросит
                // весь цикл. Пишем её в единственную ячейку и только при условии,
                // которое никогда не выполняется — трафика записи это не создаёт
                float4 v = A[id.x];
                float s = v.x + v.y + v.z + v.w;
                if (s == 1e30f) Sink[0] = 1;
            }
            else if (Mode == 1)
            {
                B[id.x] = float4(1.5f, 2.5f, 3.5f, 4.5f);
            }
            else if (Mode == 2)
            {
                B[id.x] = A[id.x];
            }
            else
            {
                // Вычисления: четыре независимые цепочки умножения со сложением
                float4 a = float4(1.0000001f, 0.9999999f, 1.0000002f, 0.9999998f);
                float4 b = float4(0.0000002f, -0.0000001f, 0.0000003f, -0.0000002f);
                float4 v0 = A[id.x];
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

                // Результат записывается безусловно. Через условие «если равно
                // 1e30» компилятор шейдера видел, что три цепочки из четырёх на
                // итог почти не влияют, и выбрасывал их: замер показывал 40 TFLOPS
                // там, где у карты по паспорту 11,2 — вчетверо больше возможного.
                // Одна запись на поток против тысяч итераций стоит пренебрежимо мало
                B[id.x] = v0 + v1 + v2 + v3;
            }
        }
        """;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _a, _b, _sink, _constants, _staging, _upload;
    private ID3D11UnorderedAccessView? _aUav, _bUav, _sinkUav;

    public GpuBenchmarkResult? Result { get; private set; }

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Замеры производительности видеокарты"));
        await Task.Run(() => Run(log, ct), ct).ConfigureAwait(false);
    }

    private void Run(IProgress<TestLine> log, CancellationToken ct)
    {
        try
        {
            if (!Initialize(log)) return;

            long elements = BufferBytes / 16;   // float4

            double read  = Measure(() => Dispatch(0, elements, 0), BufferBytes, ct);
            double write = Measure(() => Dispatch(1, elements, 0), BufferBytes, ct);
            double copy  = Measure(() => Dispatch(2, elements, 0), BufferBytes * 2, ct);

            log.Report(TestLine.Info(Fmt.Row("Чтение из видеопамяти", $"{read,8:F0} ГБ/с")));
            log.Report(TestLine.Info(Fmt.Row("Запись в видеопамять", $"{write,8:F0} ГБ/с")));
            log.Report(TestLine.Info(Fmt.Row("Копирование в ней же", $"{copy,8:F0} ГБ/с")));

            double tflops = MeasureCompute(elements, ct);
            log.Report(TestLine.Info(Fmt.Row("Вычисления FP32", $"{tflops,8:F1} TFLOPS")));

            var (upload, download) = MeasureBus(ct);
            log.Report(TestLine.Info(Fmt.Row("Шина PCIe, в карту", $"{upload,8:F1} ГБ/с")));
            log.Report(TestLine.Info(Fmt.Row("Шина PCIe, из карты", $"{download,8:F1} ГБ/с")));

            Result = new GpuBenchmarkResult
            {
                ReadGbs = read,
                WriteGbs = write,
                CopyGbs = copy,
                Fp32Tflops = tflops,
                UploadGbs = upload,
                DownloadGbs = download,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Замеры не выполнились: {ex.Message}"));
        }
        finally
        {
            Cleanup();
        }
    }

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

        var compile = Compiler.Compile(ShaderSource, "main", "netaudit_gpubench.hlsl", "cs_5_0",
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

        _a = CreateStructured(BufferBytes, 16);
        _b = CreateStructured(BufferBytes, 16);
        _aUav = _device.CreateUnorderedAccessView(_a);
        _bUav = _device.CreateUnorderedAccessView(_b);

        _sink = CreateStructured(sizeof(uint) * 4, sizeof(uint));
        _sinkUav = _device.CreateUnorderedAccessView(_sink);

        _constants = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));

        // Для замера шины: staging читается процессором, upload им же пишется
        _staging = _device.CreateBuffer(new BufferDescription(
            (uint)BufferBytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _upload = _device.CreateBuffer(new BufferDescription(
            (uint)BufferBytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write));

        return true;
    }

    private ID3D11Buffer CreateStructured(long bytes, int stride) =>
        _device!.CreateBuffer(new BufferDescription(
            (uint)bytes, BindFlags.UnorderedAccess, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, (uint)stride));

    private void Dispatch(uint mode, long elements, uint iterations)
    {
        var ctx = _context!;

        Span<uint> parms = [(uint)elements, mode, iterations, 0];
        ctx.UpdateSubresource(parms, _constants!);

        ctx.CSSetShader(_shader);
        ctx.CSSetConstantBuffer(0, _constants);
        ctx.CSSetUnorderedAccessView(0, _aUav);
        ctx.CSSetUnorderedAccessView(1, _bUav);
        ctx.CSSetUnorderedAccessView(2, _sinkUav);

        ctx.Dispatch((uint)((elements + GroupSize - 1) / GroupSize), 1, 1);
    }

    /// <summary>
    /// Замеряет скорость: прогрев, затем несколько повторов, берётся лучший.
    /// Худшие искажены посторонней нагрузкой на видеокарту (рабочий стол, браузер),
    /// лучший ближе к тому, на что карта способна.
    /// </summary>
    private double Measure(Action work, double bytesPerRun, CancellationToken ct)
    {
        work();
        Sync();

        double best = 0;

        for (int i = 0; i < Repeats; i++)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            work();
            Sync();
            sw.Stop();

            if (sw.Elapsed.TotalSeconds > 0)
            {
                double gbs = bytesPerRun / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024);
                best = Math.Max(best, gbs);
            }
        }

        return best;
    }

    /// <summary>
    /// Вычислительная мощность. Число итераций подбирается так, чтобы вызов длился
    /// заметно дольше накладных расходов, но заведомо меньше порога, после которого
    /// Windows считает видеодрайвер зависшим.
    /// </summary>
    private double MeasureCompute(long elements, CancellationToken ct)
    {
        uint iterations = 2048;
        double best = 0;

        for (int i = 0; i < Repeats; i++)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            Dispatch(3, elements, iterations);
            Sync();
            sw.Stop();

            // 4 цепочки × float4 × (умножение + сложение) = 32 операции за итерацию
            double flops = (double)elements * iterations * 32;
            double tflops = flops / sw.Elapsed.TotalSeconds / 1e12;
            best = Math.Max(best, tflops);
        }

        return best;
    }

    /// <summary>Скорость шины PCI Express в обе стороны.</summary>
    private (double Upload, double Download) MeasureBus(CancellationToken ct)
    {
        var ctx = _context!;
        double upload = 0, download = 0;

        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();

            // В карту: заполняем staging процессором и копируем в видеопамять
            var mapped = ctx.Map(_upload!, 0, MapMode.Write);
            ctx.Unmap(_upload!, 0);

            var sw = Stopwatch.StartNew();
            ctx.CopyResource(_a!, _upload!);
            Sync();
            sw.Stop();
            upload = Math.Max(upload, BufferBytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024));

            // Из карты: копируем в staging и ждём, пока станет доступен процессору
            sw.Restart();
            ctx.CopyResource(_staging!, _a!);
            var m = ctx.Map(_staging!, 0, MapMode.Read);
            ctx.Unmap(_staging!, 0);
            sw.Stop();
            download = Math.Max(download, BufferBytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024));
        }

        return (upload, download);
    }

    /// <summary>
    /// Дожидается, пока видеокарта действительно доделает работу. Без этого
    /// замерялось бы время постановки команд в очередь — то есть почти ноль.
    /// </summary>
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
        _upload?.Dispose();
        _staging?.Dispose();
        _constants?.Dispose();
        _sinkUav?.Dispose();
        _sink?.Dispose();
        _bUav?.Dispose();
        _aUav?.Dispose();
        _b?.Dispose();
        _a?.Dispose();
        _shader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _upload = null; _staging = null; _constants = null;
        _sinkUav = null; _sink = null; _bUav = null; _aUav = null;
        _b = null; _a = null; _shader = null; _context = null; _device = null;
    }
}
