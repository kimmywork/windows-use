using System.Runtime.InteropServices;
using System.Text;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Windowing;

/// <summary>窗口信息（07 §3.1 window.list/find）。</summary>
public sealed record WindowInfo(
    long Hwnd,
    string Title,
    string ClassName,
    int ProcessId,
    int[] Rect,
    int[]? VisibleRect,
    string State,
    bool Visible,
    bool Cloaked,
    bool IsTopLevel);

/// <summary>
/// 窗口枚举器（06 §5.2、PoC C5/C6）。
/// 权威数据源 = EnumWindows + cloaked 过滤；**禁止**用 UIA 桌面子元素
/// （含任务栏/Progman/幽灵窗口）。
/// </summary>
public sealed class WindowEnumerator
{
    public IReadOnlyList<WindowInfo> Enumerate(
        string? titleContains = null,
        int? processId = null,
        bool visibleOnly = false)
    {
        var windows = new List<WindowInfo>();

        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return true;
            }

            var cloaked = ReadCloaked(hwnd);
            var visible = NativeMethods.IsWindowVisible(hwnd);
            var title = ReadTitle(hwnd);
            var className = ReadClassName(hwnd);
            var pid = ReadPid(hwnd);

            if (cloaked)
            {
                return true; // 幽灵窗口（设置/UWP 后台）一律过滤
            }
            if (visibleOnly && !visible)
            {
                return true;
            }
            if (processId is { } wantedPid && pid != wantedPid)
            {
                return true;
            }
            if (titleContains is { Length: > 0 } tc &&
                !title.Contains(tc, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rect = ReadWindowRect(hwnd);
            var visibleRect = ReadExtendedFrame(hwnd);
            var state = ReadState(hwnd);

            windows.Add(new WindowInfo(
                hwnd.ToInt64(), title, className, pid,
                rect, visibleRect, state, visible, false, true));
            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        return windows;
    }

    public static int ReadPid(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public static string ReadTitle(IntPtr hwnd)
    {
        // GetWindowText(Length) 跨进程走 SendMessage(WM_GETTEXT)，目标 UI 线程挂起时
        // 会无限阻塞 EnumWindows 回调（window.list 挂起根因）。改用 SendMessageTimeout
        // + SMTO_ABORTIFHUNG，超时即跳过该窗口标题。
        const uint timeoutMs = 300;
        const int maxTitleChars = 512;
        const uint flags = NativeMethods.SMTO_ABORTIFHUNG;

        if (NativeMethods.SendMessageTimeoutW(hwnd, NativeMethods.WM_GETTEXTLENGTH,
                IntPtr.Zero, IntPtr.Zero, flags, timeoutMs, out var lengthResult) == IntPtr.Zero)
        {
            return "";
        }

        var length = (int)lengthResult.ToInt64();
        if (length <= 0)
        {
            return "";
        }
        length = Math.Min(length, maxTitleChars);

        var buffer = Marshal.AllocHGlobal((length + 1) * 2);
        try
        {
            Marshal.WriteInt16(buffer, length * 2, 0);
            if (NativeMethods.SendMessageTimeoutW(hwnd, NativeMethods.WM_GETTEXT,
                    (IntPtr)(length + 1), buffer, flags, timeoutMs, out var copied) == IntPtr.Zero)
            {
                return "";
            }
            return Marshal.PtrToStringUni(buffer, (int)copied.ToInt64()) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string ReadClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool ReadCloaked(IntPtr hwnd)
    {
        if (NativeMethods.DwmGetWindowAttributeInt(hwnd, NativeMethods.DWMWA_CLOAKED,
                out int cloaked, Marshal.SizeOf<int>()) != 0)
        {
            return false;
        }
        return cloaked != 0;
    }

    public static int[] ReadWindowRect(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
        {
            return new[] { 0, 0, 0, 0 };
        }
        return new[] { r.Left, r.Top, r.Width, r.Height };
    }

    /// <summary>DWM 扩展帧：视觉链路的唯一权威基准（PoC C3/C12）。</summary>
    public static int[]? ReadExtendedFrame(IntPtr hwnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var r, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            return null;
        }
        return new[] { r.Left, r.Top, r.Width, r.Height };
    }

    public static string ReadState(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            return "minimized";
        }
        if (NativeMethods.IsZoomed(hwnd))
        {
            return "maximized";
        }
        return "normal";
    }
}
