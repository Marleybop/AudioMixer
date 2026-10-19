using System.Runtime.InteropServices;

namespace AudioMixer.Core.Stats;

/// <summary>A snapshot of the PC's load and temperatures. Null = not known (yet).</summary>
public sealed record PcStats(
    double? CpuLoad,
    double? CpuTemp,
    string? GpuName,
    double? GpuLoad,
    double? GpuTemp,
    double? VramUsedGb,
    double? VramTotalGb,
    double RamUsedGb,
    double RamTotalGb);

/// <summary>
/// Reads CPU, GPU and memory stats with Windows' own APIs, so no admin rights and no vendor libraries:
/// CPU load from GetSystemTimes, RAM from GlobalMemoryStatusEx, GPU temperature and VRAM from the graphics kernel
/// (D3DKMT, what Task Manager uses; dedicated GPUs on WDDM 2.4+), GPU load and VRAM use from the "GPU Engine" and
/// "GPU Adapter Memory" performance counters. Loads are averaged between two calls, so the first read has none.
/// CPU temperature needs a kernel driver and admin rights, so it comes from <see cref="CpuTemperature"/> if set.
/// </summary>
public sealed class PcStatsReader : IDisposable
{
    ulong _idle, _kernel, _user;
    Gpu? _gpu;
    bool _gpuLooked;
    readonly GpuCounters? _counters = GpuCounters.TryCreate();

    /// <summary>Where the CPU temperature comes from (null = not shown).</summary>
    public Func<double?>? CpuTemperature { get; set; }

    public PcStats Read()
    {
        if (!_gpuLooked)
        {
            _gpu = Gpu.FindMain();
            _gpuLooked = true;
        }
        _counters?.Collect();
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        const double Gb = 1024.0 * 1024 * 1024;
        var temp = _gpu?.Temperature();
        if (_gpu is not null && temp is null && !_gpu.StillThere())
            _gpuLooked = false; // driver restarted or the card went away: look again next time
        return new PcStats(
            CpuLoad(),
            CpuTemperature?.Invoke(),
            _gpu?.Name,
            _gpu is null ? null : _counters?.Load(_gpu.LuidLow),
            temp,
            _gpu is null ? null : _counters?.VramUsed(_gpu.LuidLow) / Gb,
            _gpu?.DedicatedMemory is > 0 and var total ? total / Gb : null,
            (memory.TotalPhys - memory.AvailPhys) / Gb,
            memory.TotalPhys / Gb);
    }

    double? CpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;
        ulong dIdle = idle - _idle, dKernel = kernel - _kernel, dUser = user - _user;
        var first = _kernel == 0;
        (_idle, _kernel, _user) = (idle, kernel, user);
        var total = dKernel + dUser; // kernel time includes idle time
        return first || total == 0 ? null : Math.Clamp(100.0 * (total - dIdle) / total, 0, 100);
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _counters?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}

/// <summary>A graphics adapter, queried through the graphics kernel (D3DKMT).</summary>
sealed class Gpu : IDisposable
{
    const int SegmentSize = 3, RegistryInfo = 8, PerfData = 62;

    readonly uint _handle;
    public string Name { get; }
    public uint LuidLow { get; }
    public ulong DedicatedMemory { get; }

    Gpu(uint handle, uint luidLow, string name, ulong dedicated)
    {
        _handle = handle;
        LuidLow = luidLow;
        Name = name;
        DedicatedMemory = dedicated;
    }

    /// <summary>The adapter with the most dedicated memory, i.e. the gaming card rather than the CPU's graphics.</summary>
    public static Gpu? FindMain()
    {
        var all = new AdapterInfo[16];
        var handle = GCHandle.Alloc(all, GCHandleType.Pinned);
        try
        {
            var e = new EnumAdapters2 { Count = (uint)all.Length, Adapters = handle.AddrOfPinnedObject() };
            if (D3DKMTEnumAdapters2(ref e) != 0)
                return null;
            Gpu? best = null;
            foreach (var a in all.Take((int)e.Count))
            {
                if (Query(a.Handle, RegistryInfo, out AdapterRegistryInfo reg) && Query(a.Handle, SegmentSize, out SegmentSizeInfo size)
                    && size.DedicatedVideoMemory > (best?.DedicatedMemory ?? 0))
                {
                    best?.Dispose();
                    best = new Gpu(a.Handle, a.LuidLow, reg.AdapterString.Trim(), size.DedicatedVideoMemory);
                }
                else
                {
                    Close(a.Handle);
                }
            }
            return best;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>°C, or null if the driver doesn't report it.</summary>
    public double? Temperature() =>
        Query(_handle, PerfData, out AdapterPerfData p) && p.Temperature > 0 ? p.Temperature / 10.0 : null;

    public bool StillThere() => Query(_handle, SegmentSize, out SegmentSizeInfo _);

    static bool Query<T>(uint adapter, int type, out T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var zero = new byte[size];
            Marshal.Copy(zero, 0, buffer, size);
            var q = new QueryAdapterInfo { Adapter = adapter, Type = type, Data = buffer, Size = (uint)size };
            var ok = D3DKMTQueryAdapterInfo(ref q) == 0;
            value = ok ? Marshal.PtrToStructure<T>(buffer) : default;
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static void Close(uint adapter)
    {
        var c = new CloseAdapter { Adapter = adapter };
        D3DKMTCloseAdapter(ref c);
    }

    public void Dispose() => Close(_handle);

    [StructLayout(LayoutKind.Sequential)]
    struct AdapterInfo { public uint Handle; public uint LuidLow; public int LuidHigh; public uint Sources; public int PrecisePresentRegions; }

    [StructLayout(LayoutKind.Sequential)]
    struct EnumAdapters2 { public uint Count; public IntPtr Adapters; }

    [StructLayout(LayoutKind.Sequential)]
    struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }

    [StructLayout(LayoutKind.Sequential)]
    struct CloseAdapter { public uint Adapter; }

    [StructLayout(LayoutKind.Sequential)]
    struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOc, MemoryBandwidth, PcieBandwidth;
        public uint FanRpm, Power, Temperature; // Power in tenths of a percent, Temperature in tenths of a degree C
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct AdapterRegistryInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SegmentSizeInfo { public ulong DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory; }

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 e);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo q);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter c);
}

/// <summary>
/// The "GPU Engine" and "GPU Adapter Memory" performance counters (what Task Manager shows), through PDH.
/// Instances are per process and engine, e.g. "pid_1234_luid_0x00000000_0x0000F4B6_phys_0_eng_0_engtype_3D".
/// </summary>
sealed class GpuCounters : IDisposable
{
    const uint FormatDouble = 0x200, NoCap100 = 0x8000;
    const uint MoreData = 0x800007D2;

    readonly IntPtr _query, _engines, _memory;
    Dictionary<string, double> _engineValues = [];
    Dictionary<string, double> _memoryValues = [];

    GpuCounters(IntPtr query, IntPtr engines, IntPtr memory) => (_query, _engines, _memory) = (query, engines, memory);

    public static GpuCounters? TryCreate()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0)
            return null;
        if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var engines) != 0
            | PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out var memory) != 0)
        {
            PdhCloseQuery(query);
            return null;
        }
        return new GpuCounters(query, engines, memory);
    }

    public void Collect()
    {
        if (PdhCollectQueryData(_query) != 0)
            return;
        _engineValues = Values(_engines);
        _memoryValues = Values(_memory);
    }

    /// <summary>The busiest engine's use in percent, as Task Manager shows it.</summary>
    public double? Load(uint luidLow)
    {
        var tag = Tag(luidLow);
        var engines = _engineValues.Where(kv => kv.Key.Contains(tag, StringComparison.OrdinalIgnoreCase))
            .GroupBy(kv => EngineOf(kv.Key))
            .Select(g => g.Sum(kv => kv.Value))
            .ToList();
        return engines.Count == 0 ? null : Math.Clamp(engines.Max(), 0, 100);
    }

    /// <summary>Bytes of dedicated memory in use.</summary>
    public double? VramUsed(uint luidLow)
    {
        var tag = Tag(luidLow);
        var used = _memoryValues.Where(kv => kv.Key.Contains(tag, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).ToList();
        return used.Count == 0 ? null : used.Sum();
    }

    static string Tag(uint luidLow) => $"_0x{luidLow:X8}_";

    static string EngineOf(string instance)
    {
        var i = instance.IndexOf("_eng_", StringComparison.Ordinal);
        return i < 0 ? instance : instance[(i + 5)..];
    }

    static Dictionary<string, double> Values(IntPtr counter)
    {
        var result = new Dictionary<string, double>();
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, FormatDouble | NoCap100, ref size, out _, IntPtr.Zero) != MoreData || size == 0)
            return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble | NoCap100, ref size, out var count, buffer) != 0)
                return result;
            var itemSize = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
                if (item.Status == 0 && Marshal.PtrToStringUni(item.Name) is { } name)
                    result[name] = result.GetValueOrDefault(name) + item.Value;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public void Dispose() => PdhCloseQuery(_query);

    [StructLayout(LayoutKind.Sequential)]
    struct CounterItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
}
