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
///   1. **Греет.** Длинные цепочки independent FMA над float4 в счётном шейдере
///      плюс отдельный поточный шейдер, который между счётными вызовами гонит
///      копирование по большому буферу в видеопамяти. Именно это даёт настоящее
///      энергопотребление: вычислительные блоки FP32 и контроллер памяти и есть
///      основные источники тепла у видеокарты. Первая версия считала
///      целочисленный хеш в регистрах — она давала «загрузку 97%» по счётчику
///      Windows, но лишь 121 Вт из 250 и 57 °C: счётчик загрузки показывает
///      «был ли занят хоть один блок», а не насколько. Вторая трогала память
///      изнутри счётного цикла по случайным адресам — контроллер памяти был
///      занят на 16%, а мощность на 135 Вт при полной частоте. Поток памяти
///      отдельным вызовом, как в замере пропускной способности, — единственное,
///      что грузит контроллер по-настоящему.
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
/// под ~40 мс, нагрузка складывается из множества коротких вызовов подряд.
/// </summary>
public sealed class GpuStressWorker : IDisposable
{
    /// <summary>Потоков в проверочной части. 1 М по 4 байта = 4 МБ буфера сверки.</summary>
    private const int Elements = 1 << 20;

    private const int GroupSize = 256;

    /// <summary>Сколько значений сверяем с эталоном.</summary>
    private const int VerifySamples = 1024;

    /// <summary>
    /// Целевая длительность вызова — в пятьдесят раз меньше порога TDR. Поднята с 25
    /// до 40 мс, когда добавился поток по памяти: он занимает около девяти
    /// миллисекунд, и при 25 мс на счёт оставалось слишком мало.
    /// </summary>
    private static readonly TimeSpan TargetDispatch = TimeSpan.FromMilliseconds(40);

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
            uint HeatOffset;     // сдвигается между вызовами, чтобы обойти весь буфер
            uint3 Padding;
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
            // Соседние потоки берут соседние адреса: контроллер памяти собирает
            // их в одну транзакцию. Прежняя версия давала каждому потоку свой
            // случайный адрес (h % HeatCount) — вместо одной транзакции выходило
            // по одной на поток, блоки стояли в ожидании памяти вместо счёта,
            // и карта показывала «загрузку 100%» при 81 Вт из 250, не нагреваясь
            // выше температуры покоя. Проверено замером на RTX 2080 SUPER
            uint slot = (id.x + HeatOffset) % HeatCount;
            float4 s = Heat[slot];

            // Восемь независимых цепочек вместо четырёх: пока одна FMA ждёт
            // собственный результат, блок считает семь соседних. Четырёх не
            // хватало, чтобы скрыть задержку вычислений и занять конвейер
            float4 v0 = s;
            float4 v1 = s * 1.0000003f + 0.0000011f;
            float4 v2 = s * 0.9999997f - 0.0000007f;
            float4 v3 = s * 1.0000005f + 0.0000003f;
            float4 v4 = s * 1.0000002f + 0.0000009f;
            float4 v5 = s * 0.9999995f - 0.0000004f;
            float4 v6 = s * 1.0000007f + 0.0000002f;
            float4 v7 = s * 0.9999993f - 0.0000006f;

            float4 a = float4(1.0000001f, 0.9999999f, 1.0000002f, 0.9999998f);
            float4 b = float4(0.0000002f, -0.0000001f, 0.0000003f, -0.0000002f);

            [loop]
            for (uint k = 0; k < FloatIterations; k++)
            {
                v0 = mad(v0, a, b);
                v1 = mad(v1, a, b);
                v2 = mad(v2, a, b);
                v3 = mad(v3, a, b);
                v4 = mad(v4, a, b);
                v5 = mad(v5, a, b);
                v6 = mad(v6, a, b);
                v7 = mad(v7, a, b);
            }

            // Память изнутри этого цикла не трогаем: обращения тормозили FMA,
            // а трафика давали мало. Поток по видеопамяти — отдельный шейдер

            Heat[slot] = v0 + v1 + v2 + v3 + v4 + v5 + v6 + v7;
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

    /// <summary>
    /// Поток по видеопамяти: каждый поток читает восемь float4 с шагом в целый
    /// кусок, соседние потоки — соседние адреса, контроллер памяти собирает их
    /// в полные транзакции и работает на всю ширину шины. Это тот же приём, что
    /// в замере «чтение из видеопамяти» у <see cref="GpuBenchmark"/>, который на
    /// этой карте даёт 400 ГБ/с.
    ///
    /// Только чтение — не копирование. Первая версия копировала из дальней
    /// половины буфера в ближнюю, и чтение с записью одного и того же ресурса
    /// сериализовались: 512 МБ за 42 мс, то есть 12 ГБ/с, контроллер памяти
    /// на 8%. Чтение никому не мешает и греет память ровно так же.
    ///
    /// Сумму надо куда-то деть, иначе компилятор выбросит цикл целиком: пишем её
    /// в одну ячейку при условии, которое не выполняется никогда, — записи это
    /// не создаёт (проверено на замере: с условием цифра честная).
    /// </summary>
    private const string StreamShaderSource = """
        RWStructuredBuffer<float4> Heat : register(u0);
        RWStructuredBuffer<uint>   Sink : register(u1);

        cbuffer StreamParams : register(b0)
        {
            uint Count;      // элементов float4 в буфере
            uint Base;       // с какого элемента начинается этот кусок
            uint Chunk;      // сколько элементов покрывает вызов
            uint Padding;
        };

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            // Один элемент на поток, строго подряд: соседние потоки — соседние
            // адреса, страницы видеопамяти идут по порядку. Вариант «восемь чтений
            // на поток с шагом в 256 МБ» давал 95 ГБ/с вместо 400: каждый warp
            // трогал восемь далёких страниц, и таблица страниц карты не успевала.
            // Объём набирается второй осью вызова: Chunk элементов на ряд групп
            if (id.x >= Chunk) return;

            uint i = (Base + id.y * Chunk + id.x) % Count;
            float4 v = Heat[i];

            float s = v.x + v.y + v.z + v.w;
            if (s == 1e30f) Sink[0] = 1;
        }
        """;

    /// <summary>
    /// Элементов float4 на один поточный вызов: 256 МБ. Direct3D 11 не даёт больше
    /// 65535 групп по одной оси, а группа — 256 потоков, отсюда потолок.
    /// </summary>
    private const uint StreamChunk = 65535u * GroupSize;

    /// <summary>
    /// Рядов групп по второй оси вызова: 2 × 256 МБ = 512 МБ чтения за один поточный
    /// вызов. По первой оси Direct3D 11 даёт не больше 65535 групп, по второй —
    /// столько же, объём набирается ею. Восемь рядов (2 ГБ) читались за 25 мс —
    /// поток на этой карте идёт около 80 ГБ/с, а не паспортных 400, — и два таких
    /// вызова съедали всё время, калибровке на счёт ничего не оставалось.
    /// </summary>
    private const uint StreamRows = 2;

    /// <summary>
    /// Поточных вызовов на один счётный. Каждый читает 2 ГБ — около пяти
    /// миллисекунд при 400 ГБ/с; два вызова — примерно четверть времени вызова
    /// при цели 40 мс, столько же, сколько у тяжёлой игры. Больше не нужно:
    /// греет карту именно счёт, память лишь добавляет, а калибровка подбирает
    /// счётную часть под остаток времени.
    /// </summary>
    private const int StreamDispatches = 2;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11ComputeShader? _initShader;
    private ID3D11ComputeShader? _streamShader;
    private ID3D11Buffer? _streamConstants;
    private uint _streamBase;

    private ID3D11Buffer? _verify;
    private ID3D11UnorderedAccessView? _verifyUav;
    private ID3D11Buffer? _heat;
    private ID3D11UnorderedAccessView? _heatUav;
    private ID3D11Buffer? _constants;
    private ID3D11Buffer? _staging;

    private uint _intIterations = 256;
    private uint _floatIterations = 256;
    private uint _heatCount;
    private uint _heatOffset;

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
        if (_initShader is null) return false;

        _streamShader = Compile(StreamShaderSource, "поточный");
        return _streamShader is not null;
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

        // 32 байта: константный буфер кратен шестнадцати, а параметров стало пять
        _constants = _device.CreateBuffer(new BufferDescription(
            32, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));

        // Свой буфер констант у поточного шейдера: обновлять общий между двумя
        // вызовами в одном пакете — лишняя синхронизация ради четырёх чисел
        _streamConstants = _device.CreateBuffer(new BufferDescription(
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
        Span<uint> parms = [_heatCount, 0, 0, 0, 0, 0, 0, 0];
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
        // Проверочная часть намеренно короткая. Её хеш — зависимая цепочка: каждая
        // итерация ждёт предыдущую, блок простаивает на задержках. При 2048 витках
        // она съедала почти весь вызов, грея карту на 100 Вт из 250 и не давая ей
        // поднять частоту выше 1200 МГц при потолке 2130. Для сверки правильности
        // хватает и 128 витков — расхождение ловится не длиной цепочки, а самим
        // фактом сравнения с эталоном
        _intIterations = 128;
        if (_intIterations > MaxIntIterations) _intIterations = MaxIntIterations;
        _floatIterations = 256;

        // Прогрев перед замером. Первый вызов включает компиляцию шейдера
        // драйвером и первое касание буфера в видеопамяти — он в десятки раз
        // дольше настоящего. Пока замер шёл по нему, калибровка видела «54 мс
        // при цели 40» и обрывалась на минимуме итераций: нагрузка выходила
        // 113 Вт из 250 при полной частоте, контроллер памяти на 6%
        for (int i = 0; i < 3; i++)
        {
            DispatchOnce();
            SyncAndRead(verify: false);
        }

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

        // Смещение растёт от вызова к вызову — так нагрузка обходит весь буфер
        // в видеопамяти, а не топчется по одному и тому же куску, который осел бы в кэше
        _heatOffset += Elements;
        if (_heatOffset >= _heatCount) _heatOffset = 0;

        Span<uint> parms = [_intIterations, _floatIterations, FixedSeed, _heatCount, _heatOffset, 0, 0, 0];
        ctx.UpdateSubresource(parms, _constants!);

        ctx.CSSetShader(_shader);
        ctx.CSSetConstantBuffer(0, _constants);

        // После поточного вызова буферы стоят на слотах наоборот — снять оба,
        // прежде чем ставить, иначе Direct3D молча отвяжет один из них
        ctx.CSSetUnorderedAccessView(0, null);
        ctx.CSSetUnorderedAccessView(1, null);
        ctx.CSSetUnorderedAccessView(0, _verifyUav);
        ctx.CSSetUnorderedAccessView(1, _heatUav);

        ctx.Dispatch(Elements / GroupSize, 1, 1);

        // Следом — поток по видеопамяти. Очередь команд не ждёт завершения счётного
        // вызова: карта сама распределяет блоки между ними, и контроллер памяти
        // занят, пока считаются FMA
        for (int i = 0; i < StreamDispatches; i++)
            StreamOnce();
    }

    /// <summary>Один поточный вызов: копия очередного куска буфера, кусок сдвигается.</summary>
    private void StreamOnce()
    {
        var ctx = _context!;

        uint chunk = Math.Min(StreamChunk, _heatCount);
        Span<uint> parms = [_heatCount, _streamBase, chunk, 0];
        ctx.UpdateSubresource(parms, _streamConstants!);

        // Вызов покрывает StreamRows кусков подряд — следующий начинается за ними
        _streamBase = (uint)((_streamBase + (ulong)chunk * StreamRows) % _heatCount);

        ctx.CSSetShader(_streamShader);
        ctx.CSSetConstantBuffer(0, _streamConstants);

        // Слоты меняются местами относительно счётного вызова: греющий буфер
        // на 0, проверочный — как приёмник для суммы — на 1. Сначала снять оба,
        // потом ставить: один ресурс на двух UAV-слотах Direct3D не допускает
        // и молча отвязывает новый
        ctx.CSSetUnorderedAccessView(0, null);
        ctx.CSSetUnorderedAccessView(1, null);
        ctx.CSSetUnorderedAccessView(0, _heatUav);
        ctx.CSSetUnorderedAccessView(1, _verifyUav);

        ctx.Dispatch((chunk + GroupSize - 1) / GroupSize, StreamRows, 1);
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
    /// Восемь независимых FMA над float4 = 64 операции на итерацию каждого потока.
    /// </summary>
    public long FlopsPerDispatch => (long)Elements * _floatIterations * 64;

    /// <summary>То же за один цикл: цикл отправляет целый пакет вызовов.</summary>
    public long FlopsPerCycle => FlopsPerDispatch * BatchSize;

    public void Dispose()
    {
        _staging?.Dispose();
        _constants?.Dispose();
        _streamConstants?.Dispose();
        _heatUav?.Dispose();
        _heat?.Dispose();
        _verifyUav?.Dispose();
        _verify?.Dispose();
        _initShader?.Dispose();
        _streamShader?.Dispose();
        _shader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _staging = null; _constants = null; _streamConstants = null; _heatUav = null; _heat = null;
        _verifyUav = null; _verify = null; _initShader = null; _streamShader = null; _shader = null;
        _context = null; _device = null;
    }
}
