using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Проверка видеопамяти шаблонами — то же, что MemTest делает для оперативной,
/// только на видеокарте и её же силами.
///
/// Зачем именно это для карты, купленной с рук: деградация памяти — самая частая
/// болезнь старых и намайненных видеокарт. Она не убивает карту сразу, а даёт
/// редкие ошибки под нагрузкой: артефакты, вылеты из игр и ровно те сообщения
/// драйвера, которые Windows пишет в журнал как «nvlddmkm». Обычный стресс-тест
/// такое ловит плохо: он гоняет вычисления, а не всю память подряд.
///
/// Как устроено:
///   1. занимаем почти всю свободную видеопамять блоками;
///   2. шейдер заполняет блок шаблоном;
///   3. второй шейдер читает каждое значение, сверяет с ожидаемым и считает
///      несовпадения атомарным счётчиком — **сверка идёт на самой видеокарте**,
///      потому что тащить гигабайты обратно через шину ради сравнения было бы
///      в сотни раз медленнее;
///   4. между записью и проверкой проходит время — заодно ловится потеря
///      содержимого.
///
/// Шаблоны те же, что у проверок оперативной памяти: чередование нулей и единиц
/// в каждом бите ловит залипшие ячейки и наводки между соседними, «бегущая
/// единица» — ошибки адресации, псевдослучайный — зависящие от данных.
/// </summary>
public sealed class GpuMemoryTest(int passes = 2) : IDiagnosticTest
{
    public string Title => "Проверка видеопамяти";

    /// <summary>Размер одного блока. 256 МБ — компромисс между дроблением и гибкостью.</summary>
    private const long BlockBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Какую долю свободной видеопамяти оставить системе: рабочий стол и окна тоже
    /// в ней живут. Доля, а не постоянная величина: прежние 1200 МБ на карте с 2 ГБ
    /// не оставляли под проверку почти ничего, а на карте с 24 ГБ были каплей.
    /// </summary>
    private const double ReserveShare = 0.2;

    /// <summary>Меньше этого не оставляем в любом случае — рабочему столу нужно место.</summary>
    private const long MinReserveBytes = 300L * 1024 * 1024;

    private const int GroupSize = 256;

    private const string ShaderSource = """
        RWStructuredBuffer<uint> Data   : register(u0);
        RWStructuredBuffer<uint> Errors : register(u1);

        cbuffer Params : register(b0)
        {
            uint Count;       // элементов в блоке
            uint Pattern;     // базовый шаблон
            uint Mode;        // 0 — заполнить, 1 — проверить
            uint Kind;        // 0 — постоянный шаблон, 1 — зависящий от адреса
        };

        uint Expected(uint i)
        {
            if (Kind == 0) return Pattern;

            // Значение, зависящее от адреса: ловит ошибки адресации, когда
            // запись уходит не в ту ячейку, а читается «правильная»
            uint x = Pattern ^ (i * 2654435761u);
            x ^= x >> 16;
            x *= 2246822519u;
            x ^= x >> 13;
            return x;
        }

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= Count) return;

            if (Mode == 0)
            {
                Data[id.x] = Expected(id.x);
            }
            else
            {
                if (Data[id.x] != Expected(id.x))
                    InterlockedAdd(Errors[0], 1);
            }
        }
        """;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _constants;
    private ID3D11Buffer? _errors;
    private ID3D11UnorderedAccessView? _errorsUav;
    private ID3D11Buffer? _errorsStaging;

    private readonly List<(ID3D11Buffer Buffer, ID3D11UnorderedAccessView Uav, long Bytes)> _blocks = [];

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Проверка видеопамяти"));
        log.Report(TestLine.Dim("Шаблоны записываются во всю свободную видеопамять и сверяются обратно."));
        log.Report(TestLine.Dim("Сверка идёт на самой видеокарте: гонять гигабайты через шину незачем."));
        log.Report(TestLine.Empty);

        await Task.Run(() => Run(log, ct), ct).ConfigureAwait(false);
    }

    private void Run(IProgress<TestLine> log, CancellationToken ct)
    {
        try
        {
            if (!Initialize(log)) return;

            long allocated = AllocateBlocks(log, ct);
            if (allocated == 0)
            {
                log.Report(TestLine.Bad("Не удалось занять видеопамять под проверку"));
                return;
            }

            log.Report(TestLine.Info(Fmt.Row("Под проверкой", Fmt.Bytes(allocated))));
            log.Report(TestLine.Empty);

            // Самопроверка. Тест, который не умеет находить ошибки, отрапортует
            // «всё в порядке» с тем же видом, что и исправная память, — и это
            // худший вид неверного результата. Поэтому сначала заведомо ломаем
            // сверку и убеждаемся, что счётчик ошибок сработал
            if (!SanityCheck(log, ct)) return;

            long totalErrors = 0;
            var sw = Stopwatch.StartNew();

            for (int pass = 0; pass < passes; pass++)
            {
                foreach (var (pattern, kind, name) in Patterns())
                {
                    ct.ThrowIfCancellationRequested();

                    long errors = RunPattern(pattern, kind, ct);
                    totalErrors += errors;

                    var level = errors == 0 ? TestLevel.Good : TestLevel.Bad;
                    log.Report(new TestLine(
                        Fmt.Row($"   Проход {pass + 1}, {name}",
                                errors == 0 ? "без ошибок" : $"ОШИБОК: {errors}"),
                        level));
                }
            }

            sw.Stop();

            // Каждый шаблон — это полная запись и полное чтение всего занятого.
            // Число шаблонов берём у самого набора: вручную вписанная шестёрка
            // соврала бы при первой же правке списка
            int patternCount = Patterns().Count();
            double moved = allocated * passes * patternCount * 2.0;

            log.Report(TestLine.Empty);
            log.Report(TestLine.Info(Fmt.Row("Проверено всего",
                Fmt.Bytes(allocated * passes * (double)patternCount))));
            log.Report(TestLine.Info(Fmt.Row("Время", $"{sw.Elapsed.TotalSeconds:F1} с")));

            if (sw.Elapsed.TotalSeconds > 0.05)
            {
                double gbs = moved / sw.Elapsed.TotalSeconds / (1024.0 * 1024 * 1024);
                log.Report(TestLine.Info(Fmt.Row("Скорость записи и чтения", $"{gbs:F0} ГБ/с")));
            }

            log.Report(TestLine.Empty);
            if (totalErrors == 0)
            {
                log.Report(TestLine.Good("Ошибок видеопамяти не обнаружено."));
                log.Report(TestLine.Dim("Проверена только свободная часть памяти — та, что занята системой"));
                log.Report(TestLine.Dim("и рабочим столом, недоступна ни одной программе. Полную проверку"));
                log.Report(TestLine.Dim("даёт только загрузка с флешки, но она и не нужна: неисправные"));
                log.Report(TestLine.Dim("чипы обычно дают ошибки в любой области."));
            }
            else
            {
                log.Report(TestLine.Bad($"Видеопамять ошибается: {totalErrors} несовпадений."));
                log.Report(TestLine.Dim("Это и есть причина артефактов, вылетов из игр и сообщений"));
                log.Report(TestLine.Dim("видеодрайвера в журнале Windows. Что делать:"));
                log.Report(TestLine.Dim("   • сбросить разгон видеопамяти, если он есть (в том числе заводской);"));
                log.Report(TestLine.Dim("   • проверить охлаждение: у памяти свои термопрокладки, они стареют;"));
                log.Report(TestLine.Dim("   • повторить проверку после прогрева — ошибки часто появляются только горячей;"));
                log.Report(TestLine.Dim("   • если ошибки стабильны — карта неисправна, это не лечится настройками."));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report(TestLine.Bad($"Проверка не выполнилась: {ex.Message}"));
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// Проверяет, что сверка вообще способна обнаружить ошибку: заполняет блок
    /// нулями и сверяет его с единицами. Несовпасть обязано всё до последнего
    /// элемента; если счётчик показал ноль, значит не работает сам механизм —
    /// шейдер не запустился, счётчик не читается или буфер не тот. Молчать об
    /// этом нельзя: снаружи такой результат выглядит как исправная память.
    /// </summary>
    private bool SanityCheck(IProgress<TestLine> log, CancellationToken ct)
    {
        var (_, uav, bytes) = _blocks[0];
        long elements = bytes / sizeof(uint);

        ResetErrorCounter();
        Dispatch(uav, bytes, 0x00000000u, mode: 0, kind: 0, ct);
        Dispatch(uav, bytes, 0xFFFFFFFFu, mode: 1, kind: 0, ct);
        long found = ReadErrorCounter();

        if (found == elements)
        {
            log.Report(TestLine.Good(Fmt.Row("Самопроверка теста", "сверка работает")));
            log.Report(TestLine.Empty);
            return true;
        }

        log.Report(TestLine.Bad(Fmt.Row("Самопроверка теста", "НЕ ПРОЙДЕНА")));
        log.Report(TestLine.Dim($"   Ожидалось несовпадений: {elements}, обнаружено: {found}."));
        log.Report(TestLine.Bad("   Механизм сверки не работает, поэтому результату проверки памяти"));
        log.Report(TestLine.Bad("   доверять нельзя. Тест остановлен, чтобы не выдать ложное «всё в порядке»."));
        return false;
    }

    /// <summary>Шаблон, вид (0 — постоянный, 1 — от адреса) и человеческое имя.</summary>
    private static IEnumerable<(uint Pattern, uint Kind, string Name)> Patterns()
    {
        yield return (0x00000000u, 0, "нули");
        yield return (0xFFFFFFFFu, 0, "единицы");
        yield return (0x55555555u, 0, "шахматный 0101");
        yield return (0xAAAAAAAAu, 0, "шахматный 1010");
        yield return (0x80000001u, 1, "адресный");
        yield return (0x9E3779B9u, 1, "псевдослучайный");
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

        var compile = Compiler.Compile(ShaderSource, "main", "netaudit_vram.hlsl", "cs_5_0",
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

        _constants = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None));

        _errors = _device.CreateBuffer(new BufferDescription(
            sizeof(uint) * 4, BindFlags.UnorderedAccess, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, sizeof(uint)));
        _errorsUav = _device.CreateUnorderedAccessView(_errors);

        _errorsStaging = _device.CreateBuffer(new BufferDescription(
            sizeof(uint) * 4, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        return true;
    }

    /// <summary>
    /// Занимает видеопамять блоками, пока выделение не начнёт отказывать.
    /// Просить ровно «всё свободное» бессмысленно: часть памяти уже занята рабочим
    /// столом и другими программами, и сколько именно — Windows не говорит честно.
    /// </summary>
    private long AllocateBlocks(IProgress<TestLine> log, CancellationToken ct)
    {
        // Потолок на случай, когда узнать бюджет не удалось (старый драйвер, удалённый
        // рабочий стол). Раньше здесь стоял long.MaxValue, и на встроенной графике,
        // где видеопамять общая с системной, тест выгребал гигабайты оперативной
        // памяти до самого свопа — машина вставала колом
        long budget = 2L * 1024 * 1024 * 1024;
        bool budgetKnown = false;

        try
        {
            using var dxgi = _device!.QueryInterfaceOrNull<Vortice.DXGI.IDXGIDevice>();
            var adapter = dxgi?.GetAdapter();
            if (adapter is not null)
            {
                using (adapter)
                {
                    using var adapter3 = adapter.QueryInterfaceOrNull<Vortice.DXGI.IDXGIAdapter3>();
                    if (adapter3 is not null)
                    {
                        var info = adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
                        long available = (long)info.Budget - (long)info.CurrentUsage;

                        // Резерв — доля объёма, а не постоянные 1200 МБ: на карте с
                        // 2 ГБ такой резерв оставлял под проверку один блок, а на
                        // карте с 24 ГБ отнимал незаметную мелочь
                        long reserve = Math.Max(MinReserveBytes, (long)(available * ReserveShare));

                        budget = Math.Max(0, available - reserve);
                        budgetKnown = true;
                    }
                }
            }
        }
        catch { }

        if (!budgetKnown)
            log.Report(TestLine.Warn("Драйвер не сообщил, сколько видеопамяти свободно — "
                                   + $"беру не больше {Fmt.Bytes(budget)}."));

        long allocated = 0;

        while (allocated < budget)
        {
            ct.ThrowIfCancellationRequested();

            long size = Math.Min(BlockBytes, budget - allocated);
            if (size < 64L * 1024 * 1024) break;

            try
            {
                var buffer = _device!.CreateBuffer(new BufferDescription(
                    (uint)size, BindFlags.UnorderedAccess, ResourceUsage.Default,
                    CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, sizeof(uint)));

                var uav = _device.CreateUnorderedAccessView(buffer);
                _blocks.Add((buffer, uav, size));
                allocated += size;
            }
            catch
            {
                // Видеокарта отказала в выделении — значит взяли всё, что дают
                break;
            }
        }

        log.Report(TestLine.Dim($"Занято блоков: {_blocks.Count} по {Fmt.Bytes(BlockBytes)}"));
        return allocated;
    }

    private long RunPattern(uint pattern, uint kind, CancellationToken ct)
    {
        ResetErrorCounter();

        // Сначала заполняем все блоки, только потом проверяем: между записью и
        // проверкой проходит время, и это ловит ещё и потерю содержимого
        foreach (var (_, uav, bytes) in _blocks)
        {
            ct.ThrowIfCancellationRequested();
            Dispatch(uav, bytes, pattern, mode: 0, kind, ct);
        }

        foreach (var (_, uav, bytes) in _blocks)
        {
            ct.ThrowIfCancellationRequested();
            Dispatch(uav, bytes, pattern, mode: 1, kind, ct);
        }

        return ReadErrorCounter();
    }

    private void Dispatch(ID3D11UnorderedAccessView uav, long bytes,
                          uint pattern, uint mode, uint kind, CancellationToken ct)
    {
        var ctx = _context!;

        uint count = (uint)(bytes / sizeof(uint));
        Span<uint> parms = [count, pattern, mode, kind];
        ctx.UpdateSubresource(parms, _constants!);

        ctx.CSSetShader(_shader);
        ctx.CSSetConstantBuffer(0, _constants);
        ctx.CSSetUnorderedAccessView(0, uav);
        ctx.CSSetUnorderedAccessView(1, _errorsUav);

        // Дробим на части: один вызов на все 256 МБ рискует упереться в порог,
        // после которого Windows считает видеодрайвер зависшим
        const uint ChunkThreads = 16 * 1024 * 1024;
        for (uint offset = 0; offset < count; offset += ChunkThreads)
        {
            ct.ThrowIfCancellationRequested();
            uint todo = Math.Min(ChunkThreads, count - offset);
            ctx.Dispatch((todo + GroupSize - 1) / GroupSize, 1, 1);
            ctx.Flush();
        }
    }

    private void ResetErrorCounter()
    {
        Span<uint> zero = [0, 0, 0, 0];
        _context!.UpdateSubresource(zero, _errors!);
    }

    private long ReadErrorCounter()
    {
        var ctx = _context!;
        ctx.CopyResource(_errorsStaging!, _errors!);

        var mapped = ctx.Map(_errorsStaging!, 0, MapMode.Read);
        try
        {
            unsafe
            {
                return new ReadOnlySpan<uint>((void*)mapped.DataPointer, 1)[0];
            }
        }
        finally
        {
            ctx.Unmap(_errorsStaging!, 0);
        }
    }

    private void Cleanup()
    {
        foreach (var (buffer, uav, _) in _blocks)
        {
            try { uav.Dispose(); } catch { }
            try { buffer.Dispose(); } catch { }
        }
        _blocks.Clear();

        _errorsStaging?.Dispose();
        _errorsUav?.Dispose();
        _errors?.Dispose();
        _constants?.Dispose();
        _shader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _errorsStaging = null; _errorsUav = null; _errors = null;
        _constants = null; _shader = null; _context = null; _device = null;
    }
}
