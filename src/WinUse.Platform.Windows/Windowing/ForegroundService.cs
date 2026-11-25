using System.Runtime.InteropServices;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Windowing;

/// <summary>前台/焦点服务（07 window.focus、PoC C19）。</summary>
public sealed class ForegroundService
{
    /// <summary>恢复（如最小化）并置前。返回是否最终成为前台窗口。</summary>
    public bool Focus(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SwitchToThisWindow(hwnd, true);
        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);

        Thread.Sleep(80);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>关闭窗口（WM_CLOSE，异步投递；不强制终止进程）。</summary>
    public bool Close(IntPtr hwnd)
    {
        return NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}

/// <summary>窗口放置：移动/缩放/状态切换（07 window.*，PoC C3/C12：读回用 DWM 扩展帧）。</summary>
public sealed class WindowPlacer
{
    public int[] Place(IntPtr hwnd, int? x, int? y, int? width, int? height)
    {
        // 最大化状态下先还原，坐标才有意义。
        if (NativeMethods.IsZoomed(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
        };
        NativeMethods.GetWindowPlacement(hwnd, ref placement);

        if (x is { } nx)
        {
            placement.RcNormalPosition.Left = nx;
            placement.RcNormalPosition.Right = nx + (width ?? placement.RcNormalPosition.Width);
        }
        else if (width is { } w)
        {
            placement.RcNormalPosition.Right = placement.RcNormalPosition.Left + w;
        }
        if (y is { } ny)
        {
            placement.RcNormalPosition.Top = ny;
            placement.RcNormalPosition.Bottom = ny + (height ?? placement.RcNormalPosition.Height);
        }
        else if (height is { } h)
        {
            placement.RcNormalPosition.Bottom = placement.RcNormalPosition.Top + h;
        }

        placement.ShowCmd = NativeMethods.SW_SHOWNORMAL;
        if (!NativeMethods.SetWindowPlacement(hwnd, ref placement))
        {
            throw new InvalidOperationException("SetWindowPlacement 失败",
                new global::System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        Thread.Sleep(60);
        return WindowEnumerator.ReadExtendedFrame(hwnd) ?? WindowEnumerator.ReadWindowRect(hwnd);
    }

    public string SetState(IntPtr hwnd, string state)
    {
        var cmd = state.ToLowerInvariant() switch
        {
            "minimized" => NativeMethods.SW_MINIMIZE,
            "maximized" => NativeMethods.SW_MAXIMIZE,
            "normal" => NativeMethods.SW_RESTORE,
            _ => throw new ArgumentException($"未知窗口状态: {state}"),
        };
        NativeMethods.ShowWindow(hwnd, cmd);
        Thread.Sleep(60);
        return WindowEnumerator.ReadState(hwnd);
    }
}
