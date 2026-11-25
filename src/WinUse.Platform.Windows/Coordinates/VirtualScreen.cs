using System.Runtime.InteropServices;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Coordinates;

/// <summary>单个显示器信息（07 display.list）。</summary>
public sealed record MonitorInfo(
    string Id,
    int X, int Y, int Width, int Height,
    int WorkX, int WorkY, int WorkWidth, int WorkHeight,
    bool Primary,
    uint Dpi,
    double Scale,
    string DeviceName);

/// <summary>虚拟屏幕：VSC 原点/范围 + 显示器拓扑（06 §5、07 display.list）。</summary>
public sealed class VirtualScreen
{
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int MONITORINFOF_PRIMARY = 1;

    public sealed record Snapshot(
        MonitorInfo[] Monitors,
        int OriginX, int OriginY, int Width, int Height,
        bool OriginIsNegative);

    public Snapshot Capture()
    {
        var monitors = new List<MonitorInfo>();

        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc,
            ref NativeMethods.RECT lprc, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFOEX
                {
                    CbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                };
                if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                {
                    return true;
                }

                NativeMethods.GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI,
                    out var dpiX, out uint dpiY);
                if (dpiX == 0)
                {
                    dpiX = 96;
                }

                var m = info.RcMonitor;
                var w = info.RcWork;
                monitors.Add(new MonitorInfo(
                    hMonitor.ToString("x"),
                    m.Left, m.Top, m.Width, m.Height,
                    w.Left, w.Top, w.Width, w.Height,
                    (info.DwFlags & MONITORINFOF_PRIMARY) != 0,
                    dpiX,
                    Math.Round(dpiX / 96.0, 3),
                    info.SzDevice));
                return true;
            };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
        {
            throw new InvalidOperationException("EnumDisplayMonitors 失败",
                new global::System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        var ordered = monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ToArray();
        var originX = ordered.Length == 0 ? 0 : ordered.Min(m => m.X);
        var originY = ordered.Length == 0 ? 0 : ordered.Min(m => m.Y);
        var maxX = ordered.Length == 0 ? 0 : ordered.Max(m => m.X + m.Width);
        var maxY = ordered.Length == 0 ? 0 : ordered.Max(m => m.Y + m.Height);

        return new Snapshot(
            ordered,
            originX, originY, maxX - originX, maxY - originY,
            originX < 0 || originY < 0);
    }
}
