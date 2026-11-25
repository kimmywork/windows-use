using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Coordinates;

/// <summary>
/// DPI 感知声明（06 §5.1、05 ADR-006、PoC C2）。
/// 必须在创建任何窗口之前调用 <see cref="DeclarePerMonitorV2"/>。
/// </summary>
public sealed class DpiAwareness
{
    private const int S_OK = 0;

    private static bool _declared;
    private static int _resolvedAwareness = NativeMethods.DPI_AWARENESS_INVALID;

    /// <summary>声明 PerMonitorV2；逐级回退。返回最终生效的感知等级。</summary>
    public static int DeclarePerMonitorV2()
    {
        if (_declared)
        {
            return _resolvedAwareness;
        }
        _declared = true;

        // PerMonitorV2 context handle = -4；PerMonitor = -3；System = -2；Unaware = -1。
        if (NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4)))
        {
            _resolvedAwareness = NativeMethods.DPI_AWARENESS_PER_MONITOR_AWARE_V2;
        }
        else if (NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-3)))
        {
            _resolvedAwareness = NativeMethods.DPI_AWARENESS_PER_MONITOR_AWARE;
        }
        else if (NativeMethods.SetProcessDpiAwareness(NativeMethods.PROCESS_PER_MONITOR_DPI_AWARE) == S_OK)
        {
            _resolvedAwareness = NativeMethods.DPI_AWARENESS_PER_MONITOR_AWARE;
        }
        else
        {
            _resolvedAwareness = NativeMethods.DPI_AWARENESS_SYSTEM_AWARE;
        }

        return _resolvedAwareness;
    }

    /// <summary>读取当前线程的 DPI 感知等级（PoC TS-3 的断言方式）。</summary>
    public static int CurrentThreadAwareness()
    {
        var ctx = NativeMethods.GetThreadDpiAwarenessContext();
        if (ctx == IntPtr.Zero)
        {
            return NativeMethods.DPI_AWARENESS_INVALID;
        }
        return NativeMethods.GetAwarenessFromDpiAwarenessContext(ctx);
    }
}
