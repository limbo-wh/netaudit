using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Нагрузка видеокарты вычислительным шейдером Direct3D 11 — по смыслу то же, что
/// делает FurMark, только без окна и картинки.
///
/// Это обычное использование графического API на собственном устройстве: свой
/// <c>ID3D11Device</c>, свой шейдер, свои буферы. Хуков и внедрения в чужой процесс
/// нет, красная линия проекта (правило 4) не затронута — запрет касается перехвата
/// Present внутри процесса игры, а не создания своего устройства Direct3D.
///
/// Шейдер делает две несвязанные вещи одновременно:
///
///   1. **Греет.** Длинные цепочки independent FMA над float4 плюс чтение и запись
///      большого буфера в видеопамяти по разбросанным адресам. Именно это даёт
///      настоящее энергопотребление: вычислительные блоки FP32 и контроллер памяти
///      и есть основные источники тепла у видеокарты. Первая версия считала
///      целочисленный хеш в регистрах — она давала «загрузку 97%» по счётчику
///      Windows, но лишь 121 Вт из 250 и 57 °C: счётчик загрузки показывает
///      «был ли занят хоть один блок», а не насколько.
///   2. **Проверяет правильность.** Отдельная целочисленная часть считает
///      детерминированный хеш, который сверяется с эталоном, посчитанным на
///      процессоре. Расхождение означает, что видеокарта посчитала неверно —
///      признак нестабильной видеопамяти, перегрева или разгона.
///
/// Разделение принципиальное: сверять результаты float-вычислений нельзя, потому
/// что компилятор шейдеров вправе переупорядочить операции, и совпадения бит в бит
/// с процессором никто не обещает. Целочисленная часть такой свободы не оставляет.
///
/// **TDR.** Если один вызов шейдера длится дольше двух секунд, Windows считает
/// драйвер зависшим и сбрасывает его. Объём работы на вызов подбирается замером
/// под ~25 мс, нагрузка складывается из множества коротких вызовов подряд.
/// </summary>
public sealed class GpuStressWorker : IDisposable
{
    /// <summary>Потоков в проверочной части. 1 М по 4 байта = 4 МБ буфера сверки.</summary>
    private const int Elements = 1 << 20;

    private const int GroupSize = 256;

    /// <summary>Сколько значений сверяем с эталоном.</summary>
    private const int VerifySamples = 1024;

    /// <summary>Целевая длительность вызова — в восемьдесят раз меньше порога TDR.</summary>
    private static readonly TimeSpan TargetDispatch = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Потолок итераций целочисленной части. Ограничен ценой эталона: его считает
    /// процессор в один поток, и миллионы итераций уходят в минуты.
    /// </summary>
    private const uint MaxIntIterations = 20_000;

    /// <summary>Потолок итераций греющей части. Её на процессоре считать не нужно.</summary>
    private const uint MaxFloatIterations = 2_000_000;

    private const uint FixedSeed = 0x9E3779B9;

    /// <summary>
    /// Сколько вызовов уходит в очередь до одной синхронизации. Синхронизация на
    /// каждом вызове оставляла видеокарту простаивать, пока процессор копировал
    /// результат и считал сверку — при пакете простоя нет.
    /// </summary>
    private const int BatchSize = 8;

    /// <summary>Желаемый размер греющего буфера в видеопамяти, в байтах.</summary>
    private static readonly long[] HeatBufferSizes =
    [
        768L * 1024 * 1024,
        512L * 1024 * 1024,
        256L * 1024 * 1024,
        128L * 1024 * 1024,
        64L * 1024 * 1024,
    ];

    private const string ShaderSource = """
        // Проверочная часть: детерминированный хеш, сверяется с процессором
        RWStructuredBuffer<uint> Verify : register(u0);

        // Греющая часть: большой буфер в видеопамяти, float4 ради ширины шины
        RWStructuredBuffer<float4> Heat : register(u1);

        cbuffer Params : register(b0)
        {
            uint IntIterations;
            uint FloatIterations;
            uint Seed;
            uint HeatCount;      // сколько элементов float4 в греющем буфере
        };

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            // ── 1. Правильность ────────────────────────────────────────────
            uint h = Seed ^ id.x;

            [loop]
            for (uint i = 0; i < IntIterations; i++)
            {
                h ^= i;
                h *= 16777619u;
                h ^= h >> 13;
                h += h << 7;
            }

            if (id.x < 1024)
                Verify[id.x] = h;

            // ── 2. Нагрев ──────────────────────────────────────────────────
            // Четыре независимые цепочки: соседние FMA не ждут друг друга,
            // и конвейеры вычислительных блоков заполняются целиком
            uint slot = h % HeatCount;
            float4 v0 = Heat[slot];
            float4 v1 = v0 * 1.0000003f + 0.0000011f;
            float4 v2 = v0 * 0.9999997f - 0.0000007f;
            float4 v3 = v0 * 1.0000005f + 0.0000003f;

            float4 a = float4(1.0000001f, 0.9999999f, 1.0000002f, 0.9999998f);
            float4 b = float4(0.0000002f, -0.0000001f, 0.0000003f, -0.0000002f);

            [loop]
            for (uint k = 0; k < FloatIterations; k++)
            {
                v0 = mad(v0, a, b);
                v1 = mad(v1, a, b);
                v2 = mad(v2, a, b);
                v3 = mad(v3, a, b);
            }

            // Запись обратно по разбросанному адресу — нагрузка на видеопамять.
            // Гонки между потоками тут безвредны: содержимое греющего буфера
            // ни с чем не сверяется, важен сам трафик
            Heat[slot] = v0 + v1 + v2 + v3;
        }
        """;

    /// <summary>Заполняет греющий буфер осмысленными числами: мусор из видеопамяти может быть NaN.</summary>
    private const string InitShaderSource = """
        RWStructuredBuffer<float4> Heat : register(u0);

        cbuffer InitParams : register(b0)
        {
            uint HeatCount;
            uint3 Padding;
        };

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= HeatCount) return;
            float f = 1.0f + (float)(id.x & 1023) * 0.001f;
            Heat[id.x] = float4(f, f * 1.01f, f * 0.99f, f * 1.02f);
        }
        """;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11ComputeShader? _initShader;

    private ID3D11Buffer? _verify;
    private ID3D11UnorderedAccessView? _verifyUav;
    private ID3D11Buffer? _heat;
    private ID3D11UnorderedAccessView? _heatUav;
    private ID3D11Buffer? _constants;
    private ID3D11Buffer? _staging;

    private uint _intIterations = 256;
    private uint _floatIterations = 256;
    private uint _heatCount;

    private readonly uint[] _expected = new uint[VerifySamples];

    public string AdapterName { get; private set; } = "";
    public string? Error { get; private set; }
    public bool Available => _device is not null;

    /// <summary>Размер греющего буфера в видеопамяти, байт.</summary>
    public long HeatBytes { get; private set; }

    /// <summary>Сколько занял вызов при калибровке, мс. Видно, далеко ли до порога TDR.</summary>
    public double LastDispatchMs { get; private set; }

    public long Errors;
    public long Dispatches;

    public bool Initialize()
    {
        try
        {
            var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

            // Тот же адаптер, что и в замерах: греть встроенную графику, показывая
            // температуру дискретной карты, — худшее, что может сделать стресс-тест
            var created = GpuDeviceFactory.Create(featureLevels, DeviceCreationFlags.None);
            var device = created?.Device;
            var context = created?.Context;

            if (device is null || context is null)
            {
                Error = "не удалось создать устройство Direct3D 11";
                return false;
            }

            _device = device;
            _context = context;

            using (var dxgi = _device.QueryInterfaceOrNull<Vortice.DXGI.IDXGIDevice>())
            {
                var adapter = dxgi?.GetAdapter();
                if (adapter is not null)
                {
                    AdapterName = adapter.Description.Description;
                    adapter.Dispose();
                }
            }

            if (!CompileShaders()) return false;
            if (!CreateBuffers()) return false;

            InitHeatBuffer();
            Calibrate();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Dispose();
            return false;
        }
    }

    private bool CompileShaders()
    {
        _shader = Compile(ShaderSource, "основной");
        if (_shader is null) return false;

        _initShader = Compile(InitShaderSource, "заполняющий");
        return _initShader is not null;
    }

    private ID3D11ComputeShader? Compile(string source, string what)
    {
        var hr = Compiler.Compile(source, "main", $"netaudit_gpu_{what}.hlsl", "cs_5_0",
                                  out var blob, out var errors);
        using (errors)
        {
            if (hr.Failure || blob is null)
            {
                Error = $"{what} шейдер не скомпилировался: " + (errors?.AsString() ?? hr.Description);
                return null;
            }
        }

        using (blob)
        {
            return _device!.CreateComputeShader(blob.AsSpan());
        }
    }

    private bool CreateBuffers()
    {
        _verify = _device!.CreateBuffer(new BufferDescription(
            Elements * sizeof(uint), BindFlags.UnorderedAccess, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, sizeof(uint)));
        _verifyUav = _device.CreateUnorderedAccessView(_verify);

        // Приёмник ровно того же размера, что и источник: CopyResource требует
        // совпадения и при разнице не копирует ничего, никак об этом не сообщая.
        // Первая версия делала приёмник на 4 КБ ради экономии шины — в итоге
        // сверялись нули, и исправная карта показывала 1024 «ошибки» за вызов
        _staging = _device.CreateBuffer(new BufferDescription(
            Elements * sizeof(uint), BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        _constants = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));

        // Греющий буфер: берём самый большой, который согласилась дать видеокарта.
        // Чем он больше, тем дальше разбросаны обращения и тем честнее нагрузка
        // на видеопамять — а у подозрительной карты именно память и проверяем
        foreach (long size in HeatBufferSizes)
        {
            try
            {
                _heat = _device.CreateBuffer(new BufferDescription(
                    (uint)size, BindFlags.UnorderedAccess, ResourceUsage.Default,
                    CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
                _heatUav = _device.CreateUnorderedAccessView(_heat);

                HeatBytes = size;
                _heatCount = (uint)(size / 16);
                break;
            }
            catch
            {
                _heat = null;
            }
        }

        if (_heat is null)
        {
            Error = "не удалось выделить буфер в видеопамяти";
            return false;
        }

        return true;
    }

    private void InitHeatBuffer()
    {
        var ctx = _context!;
        Span<uint> parms = [_heatCount, 0, 0, 0];
        ctx.UpdateSubresource(parms, _constants!);

        ctx.CSSetShader(_initShader);
        ctx.CSSetConstantBuffer(0, _constants);
        ctx.CSSetUnorderedAccessView(0, _heatUav);
        ctx.Dispatch((_heatCount + GroupSize - 1) / GroupSize, 1, 1);
        ctx.Flush();
    }

    /// <summary>
    /// Подбирает объём работы на вызов под целевые ~25 мс. Растим только греющую
    /// часть: проверочная упирается в цену эталона на процессоре, а тепло даёт
    /// именно она — вторая.
    /// </summary>
    private void Calibrate()
    {
        _intIterations = 2048;
        if (_intIterations > MaxIntIterations) _intIterations = MaxIntIterations;
        _floatIterations = 256;

        var last = TimeSpan.Zero;

        for (int attempt = 0; attempt < 16; attempt++)
        {
            var sw = Stopwatch.StartNew();
            DispatchOnce();
            SyncAndRead(verify: false);
            sw.Stop();
            last = sw.Elapsed;

            if (last >= TargetDispatch) break;
            if (_floatIterations >= MaxFloatIterations) break;

            double factor = last.TotalMilliseconds > 0.3
                ? TargetDispatch.TotalMilliseconds / last.TotalMilliseconds
                : 4;

            ulong next = (ulong)(_floatIterations * Math.Clamp(factor, 1.5, 8));
            _floatIterations = (uint)Math.Min(next, MaxFloatIterations);
        }

        LastDispatchMs = last.TotalMilliseconds;
        PrepareExpected();
    }

    /// <summary>
    /// Доказывает, что сверка умеет находить расхождения: временно портит эталон
    /// и убеждается, что ошибки посчитались. Ровно этой проверки не хватало, когда
    /// сверка сравнивала пустой буфер и на исправной карте насчитала девять
    /// миллиардов «ошибок»; обратный случай — молчаливо сломанная сверка,
    /// показывающая ноль, — выглядел бы как здоровая карта.
    /// </summary>
    public bool SelfCheck()
    {
        if (_shader is null) return false;

        uint saved = _expected[0];
        long before = Interlocked.Read(ref Errors);

        _expected[0] = unchecked(saved + 1);
        try
        {
            RunCycle();
            long after = Interlocked.Read(ref Errors);

            // Восстанавливаем и счётчик, и эталон: самопроверка не должна
            // оставлять следов в итоговых числах теста
            Interlocked.Exchange(ref Errors, before);
            return after > before;
        }
        finally
        {
            _expected[0] = saved;
        }
    }

    /// <summary>
    /// Один цикл: пакет вызовов подряд, затем одна сверка. Пакетом — чтобы
    /// видеокарта не простаивала, пока процессор забирает и проверяет результат.
    /// </summary>
    public void RunCycle()
    {
        for (int i = 0; i < BatchSize; i++)
        {
            DispatchOnce();
            Interlocked.Increment(ref Dispatches);
        }

        SyncAndRead(verify: true);
    }

    private void DispatchOnce()
    {
        var ctx = _context!;

        Span<uint> parms = [_intIterations, _floatIterations, FixedSeed, _heatCount];
        ctx.UpdateSubresource(parms, _constants!);

        ctx.CSSetShader(_shader);
        ctx.CSSetConstantBuffer(0, _constants);
        ctx.CSSetUnorderedAccessView(0, _verifyUav);
        ctx.CSSetUnorderedAccessView(1, _heatUav);

        ctx.Dispatch(Elements / GroupSize, 1, 1);
    }

    /// <summary>
    /// Забирает результат и сверяет. Map на staging и есть точка синхронизации:
    /// он не вернётся, пока видеокарта не доделает всё, что стоит в очереди.
    /// </summary>
    private void SyncAndRead(bool verify)
    {
        var ctx = _context!;
        ctx.CopyResource(_staging!, _verify!);

        var mapped = ctx.Map(_staging!, 0, MapMode.Read);
        try
        {
            if (!verify) return;

            unsafe
            {
                var data = new ReadOnlySpan<uint>((void*)mapped.DataPointer, VerifySamples);
                long bad = 0;
                for (int i = 0; i < VerifySamples; i++)
                    if (data[i] != _expected[i]) bad++;

                if (bad > 0) Interlocked.Add(ref Errors, bad);
            }
        }
        finally
        {
            ctx.Unmap(_staging!, 0);
        }
    }

    /// <summary>Тот же целочисленный расчёт на процессоре — эталон для сверки.</summary>
    private void PrepareExpected()
    {
        uint iterations = _intIterations;

        for (int idx = 0; idx < VerifySamples; idx++)
        {
            uint h = FixedSeed ^ (uint)idx;
            for (uint i = 0; i < iterations; i++)
            {
                h ^= i;
                h *= 16777619u;
                h ^= h >> 13;
                h += h << 7;
            }
            _expected[idx] = h;
        }
    }

    /// <summary>
    /// Сколько операций с плавающей точкой приходится на один вызов шейдера.
    /// Четыре независимые FMA над float4 = 32 операции на итерацию каждого потока.
    /// </summary>
    public long FlopsPerDispatch => (long)Elements * _floatIterations * 32;

    /// <summary>То же за один цикл: цикл отправляет целый пакет вызовов.</summary>
    public long FlopsPerCycle => FlopsPerDispatch * BatchSize;

    public void Dispose()
    {
        _staging?.Dispose();
        _constants?.Dispose();
        _heatUav?.Dispose();
        _heat?.Dispose();
        _verifyUav?.Dispose();
        _verify?.Dispose();
        _initShader?.Dispose();
        _shader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _staging = null; _constants = null; _heatUav = null; _heat = null;
        _verifyUav = null; _verify = null; _initShader = null; _shader = null;
        _context = null; _device = null;
    }
}
