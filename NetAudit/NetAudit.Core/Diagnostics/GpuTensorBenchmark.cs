using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DirectML;
using Vortice.DXGI;

namespace NetAudit.Core.Diagnostics;

/// <summary>Результат замера на тензорных блоках.</summary>
public sealed record GpuTensorResult
{
    /// <summary>Умножение матриц в половинной точности, триллионов операций в секунду.</summary>
    public double Fp16Tflops { get; init; }

    /// <summary>То же в одинарной точности — для сравнения.</summary>
    public double Fp32Tflops { get; init; }

    public string? Error { get; init; }

    public bool Ok => Error is null && Fp16Tflops > 0;

    /// <summary>Во сколько раз половинная точность быстрее. У тензорных блоков — в разы.</summary>
    public double Speedup => Fp32Tflops > 0 ? Fp16Tflops / Fp32Tflops : 0;
}

/// <summary>
/// Замер умножения матриц через DirectML — то есть на тензорных блоках видеокарты.
///
/// Именно из умножения матриц состоит работа языковой модели, и именно для него
/// у видеокарт начиная с Volta есть отдельные блоки, считающие в разы быстрее
/// обычных. Из шейдера Direct3D 11 к ним не подобраться; DirectML — штатный путь
/// Windows, тот же, которым пользуются ONNX Runtime и встроенные в систему
/// нейросетевые функции.
///
/// **История этой реализации стоит того, чтобы её прочитать перед правками.**
/// Первая версия молча возвращала нули, а при попытке обойти это — роняла
/// устройство Direct3D 12. Причина оказалась ровно одна, и она описана в
/// документации: у оператора умножения матриц ТРИ входа (A, B и необязательное
/// слагаемое C), и привязать нужно все три. Привязка двух — не «недостающий
/// необязательный аргумент», а нарушение контракта, за которое обещано удаление
/// устройства. Отсюда и нули (устройство уже мертво, а исключения ещё нет), и
/// «device removed» чуть позже, при следующем обращении.
///
/// Второе препятствие — обёртка Vortice 3.8.3: пустая привязка
/// (<c>default(BindingDescription)</c>) валится в ней с NullReferenceException,
/// исправление вышло уже после релиза. Поэтому третий вход привязывается не
/// пустышкой, а настоящим буфером с нулевым шагом: тензор形ально есть, занимает
/// четыре байта, размножается по всей матрице и при <c>Beta = 0</c> на результат
/// не влияет.
/// </summary>
public sealed class GpuTensorBenchmark : IDiagnosticTest
{
    public string Title => "Замер тензорных блоков (DirectML)";

    /// <summary>
    /// Сторона квадратной матрицы. 2048³ — это 17 миллиардов операций на одно
    /// умножение: достаточно, чтобы за замером стояла работа видеокарты, а не
    /// накладные расходы. С матрицей 1024 обе точности показывали одинаковые
    /// 4,4 TFLOPS при паспортных 11 — мерилось время на запись и отправку команд.
    /// </summary>
    private const int MatrixSize = 2048;

    /// <summary>
    /// Сколько умножений отправляется одним списком команд. Отправка и ожидание
    /// стоят около полумиллисекунды — на фоне десятка умножений это уже мелочь.
    /// Между ними ставится барьер, иначе видеокарта вправе выполнить их
    /// одновременно, и замер покажет скорость, которой нет.
    /// </summary>
    private const int BatchSize = 8;

    private const int WarmupRuns = 2;
    private const int MeasuredRuns = 5;

    private ID3D12Device? _device;
    private ID3D12CommandQueue? _queue;
    private ID3D12CommandAllocator? _allocator;
    private ID3D12GraphicsCommandList? _list;
    private ID3D12Fence? _fence;
    private IDMLDevice? _dml;
    private ulong _fenceValue;

    public GpuTensorResult? Result { get; private set; }

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Тензорные блоки (умножение матриц)"));
        log.Report(TestLine.Dim($"Матрицы {MatrixSize}×{MatrixSize} через DirectML — та операция,"));
        log.Report(TestLine.Dim("из которой состоит работа языковой модели."));
        log.Report(TestLine.Empty);

        await Task.Run(() => Run(log, ct), ct).ConfigureAwait(false);
    }

    private void Run(IProgress<TestLine> log, CancellationToken ct)
    {
        try
        {
            if (!Initialize(log)) return;

            double fp32 = MeasureGemm(TensorDataType.Float32, half: false, ct, log);
            log.Report(TestLine.Info(Fmt.Row("Одинарная точность (FP32)", $"{fp32,8:F1} TFLOPS")));

            double fp16 = 0;
            if (_dml!.CheckTensorDataTypeSupport(TensorDataType.Float16))
            {
                fp16 = MeasureGemm(TensorDataType.Float16, half: true, ct, log);
                log.Report(TestLine.Info(Fmt.Row("Половинная точность (FP16)", $"{fp16,8:F1} TFLOPS")));
            }
            else
            {
                log.Report(TestLine.Warn(Fmt.Row("Половинная точность", "видеокартой не поддержана")));
            }

            Result = new GpuTensorResult { Fp32Tflops = fp32, Fp16Tflops = fp16 };

            log.Report(TestLine.Empty);
            Interpret(log, Result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Замер не выполнился: {ex.Message}"));

            string reason = DeviceRemovedReason();
            if (reason.Length > 0) log.Report(TestLine.Dim($"   {reason}"));

            Result = new GpuTensorResult { Error = ex.Message };
        }
        finally
        {
            Cleanup();
        }
    }

    private static void Interpret(IProgress<TestLine> log, GpuTensorResult r)
    {
        if (r.Fp16Tflops <= 0) return;

        log.Report(TestLine.Info(Fmt.Row("Выигрыш половинной точности", $"×{r.Speedup:F1}")));

        if (r.Speedup >= 3)
        {
            log.Report(TestLine.Good("   Тензорные блоки работают в полную силу — это лучшее, что"));
            log.Report(TestLine.Good("   видеокарта может дать нейросетям."));
        }
        else if (r.Speedup >= 1.6)
        {
            log.Report(TestLine.Warn("   Ускорение есть, но похоже на обычные вычислительные блоки."));
            log.Report(TestLine.Dim("   Тензорные могли не задействоваться: так бывает на маленьких матрицах"));
            log.Report(TestLine.Dim("   и на видеокартах, где их попросту нет."));
        }
        else
        {
            log.Report(TestLine.Warn("   Половинная точность не даёт выигрыша — тензорных блоков нет."));
        }
    }

    // ── Инициализация ─────────────────────────────────────────────────────

    private bool Initialize(IProgress<TestLine> log)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();

            IDXGIAdapter1? chosen = null;
            long best = -1;

            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                var d = adapter.Description1;
                long mem = (long)(ulong)d.DedicatedVideoMemory;

                if ((d.Flags & AdapterFlags.Software) == 0 && mem > best)
                {
                    chosen?.Dispose();
                    chosen = adapter;
                    best = mem;
                }
                else
                {
                    adapter.Dispose();
                }
            }

            if (chosen is null)
            {
                log.Report(TestLine.Bad("Видеокарта не найдена"));
                return false;
            }

            using (chosen)
            {
                var hr = D3D12.D3D12CreateDevice(chosen, Vortice.Direct3D.FeatureLevel.Level_11_0,
                                                 out ID3D12Device? device);
                if (hr.Failure || device is null)
                {
                    log.Report(TestLine.Bad($"Direct3D 12 недоступен: {hr.Description}"));
                    return false;
                }
                _device = device;
            }

            _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
            _allocator = _device.CreateCommandAllocator(CommandListType.Direct);
            _list = _device.CreateCommandList<ID3D12GraphicsCommandList>(
                0, CommandListType.Direct, _allocator, null);
            _list.Close();
            _fence = _device.CreateFence(0);

            _dml = DML.DMLCreateDevice(_device, CreateDeviceFlags.None);
            return true;
        }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"DirectML не поднялся: {ex.Message}"));
            log.Report(TestLine.Dim("   Нужны Direct3D 12 и DirectML — они есть в Windows 10 версии 1903"));
            log.Report(TestLine.Dim("   и новее. Остальные замеры это не затрагивает."));
            return false;
        }
    }

    // ── Замер ─────────────────────────────────────────────────────────────

    private double MeasureGemm(TensorDataType dataType, bool half, CancellationToken ct,
                               IProgress<TestLine> log)
    {
        uint n = MatrixSize;
        uint elementSize = half ? 2u : 4u;
        ulong tensorBytes = (ulong)n * n * elementSize;

        var sizes = new uint[] { 1, 1, n, n };

        var ab = new BufferTensorDescription
        {
            DataType = dataType,
            Flags = TensorFlags.None,
            Sizes = sizes,
            TotalTensorSizeInBytes = tensorBytes,
        };

        // Слагаемого C нет вовсе: оно необязательное, а с фиктивным тензором
        // DirectML выбирал медленную реализацию. Привязка для него всё равно
        // нужна — пустая, см. BindInputsRaw
        var gemm = new GeneralMatrixMultiplyOperatorDescription
        {
            ATensor = new TensorDescription(ab),
            BTensor = new TensorDescription(ab),
            CTensor = null,
            OutputTensor = new TensorDescription(ab),
            TransformA = MatrixTransform.None,
            TransformB = MatrixTransform.None,
            Alpha = 1.0f,
            Beta = 0.0f,
        };

        using var op = _dml!.CreateOperator(gemm);

        // Половинная точность разрешается явно: без этого флага DirectML вправе
        // считать её обычной, и тензорные блоки не задействуются
        using var compiled = _dml.CompileOperator(op,
            half ? ExecutionFlags.AllowHalfPrecisionComputation : ExecutionFlags.None);

        var execProps = compiled.GetBindingProperties();

        using var initializer = _dml.CreateOperatorInitializer([compiled]);
        var initProps = initializer.GetBindingProperties();

        uint descriptorCount = Math.Max(execProps.RequiredDescriptorCount, initProps.RequiredDescriptorCount);

        using var heap = _device!.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            descriptorCount, DescriptorHeapFlags.ShaderVisible));

        ulong temporarySize = Math.Max(execProps.TemporaryResourceSize, initProps.TemporaryResourceSize);
        ulong persistentSize = execProps.PersistentResourceSize;

        using var aBuf = CreateBuffer(tensorBytes);
        using var bBuf = CreateBuffer(tensorBytes);
        using var cBuf = CreateBuffer(16);
        using var outBuf = CreateBuffer(tensorBytes);
        using var temporary = temporarySize > 0 ? CreateBuffer(temporarySize) : null;
        using var persistent = persistentSize > 0 ? CreateBuffer(persistentSize) : null;

        using var recorder = _dml.CreateCommandRecorder();

        var tableDesc = new BindingTableDescription
        {
            Dispatchable = initializer,
            CPUDescriptorHandle = heap.GetCPUDescriptorHandleForHeapStart(),
            GPUDescriptorHandle = heap.GetGPUDescriptorHandleForHeapStart(),
            SizeInDescriptors = descriptorCount,
        };

        using var table = _dml.CreateBindingTable(tableDesc);

        // ── Инициализация оператора ──────────────────────────────────────
        // Входы инициализатору не нужны: тензоров, которыми владеет DirectML,
        // у нас нет. Постоянный ресурс привязывается как его ВЫХОД — он его и заполняет
        if (temporary is not null) table.BindTemporaryResource(Bind(temporary, temporarySize));
        if (persistent is not null) table.BindOutputs([Bind(persistent, persistentSize)]);

        Record(recorder, initializer, table, heap);
        ExecuteAndWait();

        // ── Подготовка данных ────────────────────────────────────────────
        FillWithOnes(aBuf, (int)(n * n), half);
        FillWithOnes(bBuf, (int)(n * n), half);
        FillWithOnes(cBuf, 1, half, zero: true);

        // ── Переключение таблицы на сам оператор ─────────────────────────
        tableDesc.Dispatchable = compiled;
        table.Reset(tableDesc);

        if (temporary is not null) table.BindTemporaryResource(Bind(temporary, temporarySize));
        if (persistent is not null) table.BindPersistentResource(Bind(persistent, persistentSize));

        // Ровно три привязки — столько входов у оператора, считая необязательное
        // слагаемое C. Меньше нельзя: документация обещает за это удаление
        // устройства, что и происходило — молчаливые нули, а следом «device removed»
        BindInputsRaw(table,
            (aBuf, tensorBytes),
            (bBuf, tensorBytes),
            (null, 0));
        table.BindOutputs([Bind(outBuf, tensorBytes)]);

        for (int i = 0; i < WarmupRuns; i++)
        {
            ct.ThrowIfCancellationRequested();
            Record(recorder, compiled, table, heap);
            ExecuteAndWait();
        }

        // Проверяем, что умножение действительно произошло: все входы — единицы,
        // значит каждый элемент результата обязан равняться числу столбцов
        double got = ReadFirst(outBuf, half);
        if (double.IsNaN(got) || Math.Abs(got - n) > n * 0.02)
        {
            log.Report(TestLine.Bad(
                $"   Результат умножения неверен: {got:F1} вместо {n} — замер недостоверен"));
            return 0;
        }

        double best = 0;
        double flops = 2.0 * MatrixSize * MatrixSize * MatrixSize * BatchSize;

        for (int i = 0; i < MeasuredRuns; i++)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            Record(recorder, compiled, table, heap, BatchSize, outBuf);
            ExecuteAndWait();
            sw.Stop();

            if (sw.Elapsed.TotalSeconds > 0)
                best = Math.Max(best, flops / sw.Elapsed.TotalSeconds / 1e12);
        }

        // Ресурсы обязаны дожить до конца работы видеокарты: сборщик мусора
        // не знает, что на них ссылается таблица привязок, и его финализатор
        // освободил бы их прямо под работающим GPU
        GC.KeepAlive(table);
        GC.KeepAlive(heap);
        GC.KeepAlive(temporary);
        GC.KeepAlive(persistent);

        return best;
    }

    private static BindingDescription Bind(ID3D12Resource resource, ulong bytes) =>
        new(new BufferBinding { Buffer = resource, Offset = 0, SizeInBytes = bytes });

    // ── Привязка входов в обход обёртки ───────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBindingDescription
    {
        public int Type;        // 0 — нет привязки, 1 — буфер
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBufferBinding
    {
        public IntPtr Buffer;
        public ulong Offset;
        public ulong SizeInBytes;
    }

    /// <summary>
    /// Привязывает входы напрямую через таблицу виртуальных методов COM-объекта.
    ///
    /// Нужно ровно ради одной возможности: сказать «этого входа нет»
    /// (<c>DML_BINDING_TYPE_NONE</c>) для необязательного слагаемого C. Обёртка
    /// Vortice 3.8.3 на пустой привязке падает с NullReferenceException —
    /// исправление вышло уже после релиза, а нужный для обхода интерфейс
    /// объявлен внутренним, поэтому обойти его средствами самой обёртки нельзя.
    ///
    /// Альтернатива — подсунуть фиктивный тензор C из одного значения — работает,
    /// но мешает DirectML выбрать быструю реализацию: с ней умножение шло вдвое
    /// медленнее возможного и тензорные блоки не включались.
    ///
    /// Номер метода в таблице (8) взят из заголовка DirectML.h: IUnknown занимает
    /// слоты 0–2, IDMLObject — 3–6, IDMLDeviceChild — 7, и первым методом самой
    /// таблицы привязок идёт BindInputs.
    /// </summary>
    private static unsafe void BindInputsRaw(IDMLBindingTable table,
                                             params (ID3D12Resource? Resource, ulong Bytes)[] inputs)
    {
        var buffers = stackalloc NativeBufferBinding[inputs.Length];
        var descriptions = stackalloc NativeBindingDescription[inputs.Length];

        for (int i = 0; i < inputs.Length; i++)
        {
            if (inputs[i].Resource is null)
            {
                descriptions[i] = new NativeBindingDescription { Type = 0, Description = IntPtr.Zero };
                continue;
            }

            buffers[i] = new NativeBufferBinding
            {
                Buffer = inputs[i].Resource!.NativePointer,
                Offset = 0,
                SizeInBytes = inputs[i].Bytes,
            };
            descriptions[i] = new NativeBindingDescription
            {
                Type = 1,
                Description = (IntPtr)(&buffers[i]),
            };
        }

        IntPtr self = table.NativePointer;
        void** vtbl = *(void**)self is null ? null : *(void***)self;
        if (vtbl is null) throw new InvalidOperationException("таблица привязок недоступна");

        ((delegate* unmanaged[Stdcall]<IntPtr, uint, NativeBindingDescription*, void>)vtbl[8])(
            self, (uint)inputs.Length, descriptions);

        GC.KeepAlive(table);
        foreach (var (resource, _) in inputs) GC.KeepAlive(resource);
    }

    private void Record(IDMLCommandRecorder recorder, IDMLDispatchable dispatchable,
                        IDMLBindingTable table, ID3D12DescriptorHeap heap,
                        int repeats = 1, ID3D12Resource? output = null)
    {
        _allocator!.Reset();
        _list!.Reset(_allocator);

        // Обязательно после каждого Reset: список команд забывает привязанные
        // кучи дескрипторов, а DirectML читает свои дескрипторы именно оттуда
        _list.SetDescriptorHeaps(heap);

        for (int i = 0; i < repeats; i++)
        {
            // Барьер между повторами. RecordDispatch своих барьеров не ставит,
            // и без него видеокарта вправе считать умножения внахлёст — замер
            // показал бы скорость, которой на деле нет
            if (i > 0 && output is not null)
                _list.ResourceBarrierUnorderedAccessView(output);

            recorder.RecordDispatch(_list, dispatchable, table);
        }

        _list.Close();
    }

    private void ExecuteAndWait()
    {
        _queue!.ExecuteCommandList(_list!);

        _fenceValue++;
        _queue.Signal(_fence!, _fenceValue);

        while (_fence!.CompletedValue < _fenceValue)
        {
            // Удаление устройства «липкое»: дальше всё будет возвращать ошибку,
            // и без этой проверки причина потерялась бы за вторичными симптомами
            var removed = _device!.DeviceRemovedReason;
            if (removed.Failure)
                throw new InvalidOperationException(
                    $"устройство Direct3D 12 удалено: {removed.Description}");

            Thread.SpinWait(64);
        }
    }

    private string DeviceRemovedReason()
    {
        try
        {
            var r = _device?.DeviceRemovedReason;
            return r is { Failure: true } ? $"причина удаления устройства: {r.Value.Description}" : "";
        }
        catch
        {
            return "";
        }
    }

    // ── Данные ────────────────────────────────────────────────────────────

    /// <summary>Буфер в видеопамяти. Создаётся приёмником копирования — данные в него ещё зальют.</summary>
    private ID3D12Resource CreateBuffer(ulong bytes) =>
        _device!.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties,
            HeapFlags.None,
            ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.Common);

    private unsafe void FillWithOnes(ID3D12Resource target, int count, bool half, bool zero = false)
    {
        ulong bytes = (ulong)count * (half ? 2u : 4u);
        if (bytes < 16) bytes = 16;

        using var upload = _device!.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(bytes), ResourceStates.GenericRead);

        void* p = null;
        upload.Map(0, &p).CheckError();
        try
        {
            new Span<byte>(p, (int)bytes).Clear();
            if (!zero)
            {
                if (half) new Span<Half>(p, count).Fill((Half)1.0f);
                else      new Span<float>(p, count).Fill(1.0f);
            }
        }
        finally
        {
            upload.Unmap(0);
        }

        _allocator!.Reset();
        _list!.Reset(_allocator);
        _list.ResourceBarrierTransition(target, ResourceStates.Common, ResourceStates.CopyDest);
        _list.CopyBufferRegion(target, 0, upload, 0, bytes);
        _list.ResourceBarrierTransition(target, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
        _list.Close();
        ExecuteAndWait();
    }

    private unsafe double ReadFirst(ID3D12Resource source, bool half)
    {
        int elementSize = half ? 2 : 4;
        ulong bytes = 256;

        using var readback = _device!.CreateCommittedResource(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(bytes), ResourceStates.CopyDest);

        _allocator!.Reset();
        _list!.Reset(_allocator);
        _list.ResourceBarrierTransition(source, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        _list.CopyBufferRegion(readback, 0, source, 0, bytes);
        _list.ResourceBarrierTransition(source, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        _list.Close();
        ExecuteAndWait();

        void* p = null;
        readback.Map(0, &p).CheckError();
        try
        {
            return half
                ? (double)new ReadOnlySpan<Half>(p, (int)(bytes / (ulong)elementSize))[0]
                : new ReadOnlySpan<float>(p, (int)(bytes / (ulong)elementSize))[0];
        }
        finally
        {
            readback.Unmap(0);
        }
    }

    private void Cleanup()
    {
        _dml?.Dispose();
        _fence?.Dispose();
        _list?.Dispose();
        _allocator?.Dispose();
        _queue?.Dispose();
        _device?.Dispose();

        _dml = null; _fence = null; _list = null;
        _allocator = null; _queue = null; _device = null;
    }
}
