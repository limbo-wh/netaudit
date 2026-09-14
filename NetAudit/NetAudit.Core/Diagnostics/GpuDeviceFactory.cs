using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// Создание устройства Direct3D 11 на той видеокарте, которую владелец считает
/// своей главной.
///
/// Зачем понадобилось отдельное место. Каждый тест раньше звал
/// <c>D3D11CreateDevice(null, DriverType.Hardware, …)</c> — то есть адаптер по
/// умолчанию. На настольной машине с одной картой это она и есть, а на ноутбуке
/// адаптером по умолчанию обычно оказывается встроенная графика. При этом замер
/// тензорных блоков выбирал адаптер сам — по наибольшей видеопамяти, то есть
/// дискретную карту. В итоге в одном отчёте рядом стояли цифры двух разных
/// видеокарт, и понять это по тексту было невозможно.
///
/// Теперь выбор один на всех: карта с наибольшей собственной видеопамятью. Это тот
/// же признак, по которому дискретную карту отличает от встроенной и паспорт
/// (<see cref="Probes.GpuInfoProbe"/>), так что имя в паспорте и цифры в замерах
/// относятся к одному и тому же железу.
/// </summary>
internal static class GpuDeviceFactory
{
    /// <summary>Что получилось создать и на чём именно.</summary>
    internal readonly record struct Created(
        ID3D11Device Device,
        ID3D11DeviceContext Context,
        string AdapterName,
        long AdapterLuid);

    /// <summary>
    /// Создаёт устройство на выбранном адаптере. При любой неудаче честно откатывается
    /// на адаптер по умолчанию: лучше замерить хоть что-то, чем не запуститься вовсе
    /// на машине с необычной конфигурацией видео.
    /// </summary>
    internal static Created? Create(FeatureLevel[] levels, DeviceCreationFlags flags)
    {
        var picked = PickAdapter();

        if (picked is not null)
        {
            using (picked.Adapter)
            {
                // DriverType.Unknown обязателен, когда адаптер передан явно: с
                // Hardware вызов не принимает адаптер и возвращает ошибку
                var hr = D3D11.D3D11CreateDevice(
                    picked.Adapter, DriverType.Unknown, flags, levels,
                    out ID3D11Device? device, out _, out ID3D11DeviceContext? context);

                if (hr.Success && device is not null && context is not null)
                    return new Created(device, context, picked.Name, picked.Luid);

                device?.Dispose();
                context?.Dispose();
            }
        }

        var fallback = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, flags, levels,
            out ID3D11Device? d, out _, out ID3D11DeviceContext? c);

        if (fallback.Failure || d is null || c is null)
        {
            d?.Dispose();
            c?.Dispose();
            return null;
        }

        return new Created(d, c, "", 0);
    }

    private sealed record Picked(IDXGIAdapter1 Adapter, string Name, long Luid);

    /// <summary>
    /// Видеокарта с наибольшей собственной видеопамятью. Программные адаптеры
    /// (Microsoft Basic Render Driver) пропускаются: считать на них — значит
    /// мерить процессор под видом видеокарты.
    /// </summary>
    private static Picked? PickAdapter()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            Picked? best = null;
            long bestMemory = -1;

            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
            {
                if (adapter is null) continue;

                var desc = adapter.Description1;
                bool software = (desc.Flags & AdapterFlags.Software) != 0;
                long memory = (long)desc.DedicatedVideoMemory;

                if (software || memory <= bestMemory)
                {
                    adapter.Dispose();
                    continue;
                }

                best?.Adapter.Dispose();

                long luid = ((long)desc.Luid.HighPart << 32) | (uint)desc.Luid.LowPart;
                best = new Picked(adapter, desc.Description ?? "", luid);
                bestMemory = memory;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }
}
