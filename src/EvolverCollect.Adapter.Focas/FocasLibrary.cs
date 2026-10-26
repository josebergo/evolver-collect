namespace EvolverCollect.Adapter.Focas;

using System.Runtime.InteropServices;

/// <summary>FOCAS 库抽象：真机走 fwlib32 P/Invoke；未找到库时用 mock 生成仿真机床数据。</summary>
public abstract class FocasLibrary
{
    public abstract string Name { get; }
    public abstract (bool Success, string? Error) Connect(string host, int port);
    public abstract MachineData? ReadMachineData();
    public abstract void Disconnect();

    public static FocasLibrary Create(bool preferFwlib)
    {
        if (preferFwlib && TryLoadFwlib(out var fwlib)) return fwlib!;
        // 未显式要求或库不存在：仍尝试自动发现一次（用户可能已把 dll 放在旁边）
        if (TryLoadFwlib(out var found)) return found!;
        return new MockFocasLibrary();
    }

    private static bool TryLoadFwlib(out FwlibFocasLibrary? library)
    {
        library = null;
        try
        {
            var handle = NativeMethods.TryLoadLibrary("fwlib32.dll");
            if (handle == IntPtr.Zero)
            {
                var local = Path.Combine(AppContext.BaseDirectory, "fwlib32.dll");
                if (!File.Exists(local)) return false;
                handle = NativeMethods.TryLoadLibrary(local);
                if (handle == IntPtr.Zero) return false;
            }
            library = new FwlibFocasLibrary(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>机床数据快照（统一数据模型）。</summary>
public sealed record MachineData(
    string Status, string ProgramNo, int SpindleSpeed, double SpindleLoad,
    double FeedRate, int PartsCount, int AlarmNo, string AbsolutePosition);

/// <summary>mock：仿真数据发生器（演示与联调用）。</summary>
public sealed class MockFocasLibrary : FocasLibrary
{
    private bool _connected;
    private int _tick;
    private int _parts;

    public override string Name => "mock";

    public override (bool Success, string? Error) Connect(string host, int port)
    {
        _connected = true;
        return (true, null);
    }

    public override MachineData? ReadMachineData()
    {
        if (!_connected) return null;
        _tick++;
        if (_tick % 37 == 0) _parts++;
        var phase = Math.Sin(_tick * 0.18);
        var status = phase > -0.75 ? "RUN" : _tick % 2 == 0 ? "IDLE" : "ALARM";
        return new MachineData(
            Status: status,
            ProgramNo: $"O{1000 + (_tick / 60) % 10:0000}",
            SpindleSpeed: status == "RUN" ? 3000 + (int)(phase * 1500) : 0,
            SpindleLoad: status == "RUN" ? 35 + phase * 40 : 0,
            FeedRate: status == "RUN" ? 2500 + phase * 800 : 0,
            PartsCount: _parts,
            AlarmNo: status == "ALARM" ? 501 : 0,
            AbsolutePosition: $"X{123.4 + _tick * 0.1:0.0} Y{-45.0 - phase * 2:0.0} Z88.5");
    }

    public override void Disconnect() => _connected = false;
}

/// <summary>真机实现：动态 P/Invoke fwlib32（用户从 FANUC 官网/随机光盘自行获取并放置）。</summary>
public sealed class FwlibFocasLibrary(IntPtr _module) : FocasLibrary
{
    private ushort _handle;
    private bool _connected;

    public override string Name => "fwlib32";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate short Allclibhndl3Delegate(string ip, ushort port, int timeout, out ushort handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate short FreelibhndlDelegate(ushort handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate short StatinfoDelegate(ushort handle, IntPtr statinfo);

    private T? GetProc<T>(string name) where T : Delegate
    {
        var ptr = NativeMethods.Resolve(_module, name);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    public override (bool Success, string? Error) Connect(string host, int port)
    {
        var proc = GetProc<Allclibhndl3Delegate>("cnc_allclibhndl3");
        if (proc is null) return (false, "fwlib32.dll 中未找到 cnc_allclibhndl3（FOCAS2 库不完整）");
        var rc = proc(host, (ushort)port, 5, out _handle);
        _connected = rc == 0;
        return _connected ? (true, null) : (false, $"cnc_allclibhndl3 返回 {rc}（检查机床 IP/端口 8193 与以太网参数）");
    }

    public override MachineData? ReadMachineData()
    {
        if (!_connected) return null;
        // 数据项逐个接入：cnc_statinfo(状态) / cnc_rdprognum(程序号) / cnc_rdspload(负载)…
        // 结构体布局随机型（Series 0i/30i）有差异，留待真机现场按手册 A-02-0347 校准填充。
        return new MachineData("UNKNOWN", "O0000", 0, 0, 0, 0, 0, "-");
    }

    public override void Disconnect()
    {
        if (_connected) GetProc<FreelibhndlDelegate>("cnc_freelibhndl")?.Invoke(_handle);
        _connected = false;
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadLibraryA(string fileName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    public static IntPtr TryLoadLibrary(string path) => LoadLibraryA(path);
    public static IntPtr Resolve(IntPtr module, string name) => GetProcAddress(module, name);
}
