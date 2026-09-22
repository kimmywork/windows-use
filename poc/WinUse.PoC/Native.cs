// Native.cs — Win32 / IMM32 / Advapi32 P/Invoke 封装（PoC 专用，全部为只读调用）
//
// 【安全声明】本文件只声明"查询/读取"类 API。
// 明确不声明：SendInput / keybd_event / mouse_event / SetCursorPos /
//             SetForegroundWindow / CreateProcess / TerminateProcess / 注册表写入。

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WinUse.PoC;

internal static class Native
{
    // ---------- 系统指标 ----------
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_CMONITORS = 80;
    public const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // ---------- 显示器 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public const int MDT_EFFECTIVE_DPI = 0;
    public const int MDT_ANGULAR_DPI = 1;
    public const int MDT_RAW_DPI = 2;

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---------- DPI Awareness ----------
    // DPI_AWARENESS_CONTEXT 取值（负值，来自 winuser.h）
    public const int DPI_AWARENESS_CONTEXT_UNAWARE = -1;
    public const int DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2;
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // PROCESS_DPI_AWARENESS
    public const int PROCESS_DPI_UNAWARE = 0;
    public const int PROCESS_SYSTEM_DPI_AWARE = 1;
    public const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(int value);

    [DllImport("shcore.dll")]
    public static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    public static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    // ---------- 窗口枚举（只读） ----------
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    public static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern uint GetWindowLongPtr(IntPtr hWnd, int nIndex);

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    // ---------- DWM ----------
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeInt(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// 返回 cloaked 状态：-1=查询失败, 0=未 cloak, 1=DWM_CLOAKED_APP,
    /// 2=DWM_CLOAKED_SHELL, 4=DWM_CLOAKED_INHERITED。
    /// UWP 窗口被"最小化到挂起"后 IsWindowVisible 仍返回 TRUE，只有 cloaked 能识别。
    /// </summary>
    public static int GetCloaked(IntPtr hWnd)
    {
        int hr = DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out int v, sizeof(int));
        return hr == 0 ? v : -1;
    }

    public static string CloakedName(int v) => v switch
    {
        -1 => "QUERY_FAILED",
        0 => "NOT_CLOAKED",
        1 => "CLOAKED_APP",
        2 => "CLOAKED_SHELL",
        4 => "CLOAKED_INHERITED",
        _ => $"CLOAKED_OTHER({v})"
    };

    // ---------- IME ----------
    [DllImport("user32.dll")]
    public static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    public const uint KLF_ACTIVATE = 0x00000001;

    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr himc);

    [DllImport("imm32.dll")]
    public static extern bool ImmGetOpenStatus(IntPtr himc);

    [DllImport("imm32.dll")]
    public static extern bool ImmGetConversionStatus(IntPtr himc, out uint conversion, out uint sentence);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    public static extern int ImmGetDescription(IntPtr hkl, StringBuilder lpszDescription, int uBufLen);

    // IME_CONVERSION_MODE
    public const uint IME_CMODE_NATIVE = 0x0001;
    public const uint IME_CMODE_FULLSHAPE = 0x0008;
    public const uint IME_CMODE_SYMBOL = 0x0400;

    // ---------- 完整性级别 / Token ----------
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;

    public const int TokenElevationType = 18;
    public const int TokenElevation = 20;
    public const int TokenIntegrityLevel = 25;
    public const int TokenUIAccess = 26;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
        IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthority(IntPtr pSid, int nSubAuthority);

    // 完整性级别 RID
    public const int SECURITY_MANDATORY_UNTRUSTED_RID = 0x00000000;
    public const int SECURITY_MANDATORY_LOW_RID = 0x00001000;
    public const int SECURITY_MANDATORY_MEDIUM_RID = 0x00002000;
    public const int SECURITY_MANDATORY_MEDIUM_PLUS_RID = 0x00002100;
    public const int SECURITY_MANDATORY_HIGH_RID = 0x00003000;
    public const int SECURITY_MANDATORY_SYSTEM_RID = 0x00004000;
    public const int SECURITY_MANDATORY_PROTECTED_PROCESS_RID = 0x00005000;

    public static string IlName(int rid) => rid switch
    {
        SECURITY_MANDATORY_UNTRUSTED_RID => "Untrusted",
        SECURITY_MANDATORY_LOW_RID => "Low",
        SECURITY_MANDATORY_MEDIUM_RID => "Medium",
        SECURITY_MANDATORY_MEDIUM_PLUS_RID => "MediumPlus",
        SECURITY_MANDATORY_HIGH_RID => "High",
        SECURITY_MANDATORY_SYSTEM_RID => "System",
        SECURITY_MANDATORY_PROTECTED_PROCESS_RID => "Protected",
        _ => $"Unknown(0x{rid:X4})"
    };

    public static int? GetIntegrityLevel(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out IntPtr tok)) return null;
            try
            {
                GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
                if (len <= 0) return null;
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (!GetTokenInformation(tok, TokenIntegrityLevel, buf, len, out _)) return null;
                    IntPtr sid = Marshal.ReadIntPtr(buf); // TOKEN_MANDATORY_LABEL.Label.Sid
                    byte cnt = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    IntPtr ridPtr = GetSidSubAuthority(sid, cnt - 1);
                    return Marshal.ReadInt32(ridPtr);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(h); }
    }

    public static bool? GetUiAccess(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out IntPtr tok)) return null;
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(8);
                try
                {
                    if (!GetTokenInformation(tok, TokenUIAccess, buf, 4, out int ret)) return null;
                    return Marshal.ReadInt32(buf) != 0;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(h); }
    }

    public static bool? GetElevated(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out IntPtr tok)) return null;
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(8);
                try
                {
                    if (!GetTokenInformation(tok, TokenElevation, buf, 4, out int ret)) return null;
                    return Marshal.ReadInt32(buf) != 0;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(h); }
    }

    // ---------- 便捷封装 ----------
    public static string WindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 2);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string WindowClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int n = GetClassName(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    public static string LangIdName(int langId) => langId switch
    {
        0x0804 => "zh-CN",
        0x0404 => "zh-TW",
        0x1004 => "zh-SG",
        0x0C04 => "zh-HK",
        0x0409 => "en-US",
        0x0809 => "en-GB",
        0x0411 => "ja-JP",
        0x0412 => "ko-KR",
        _ => $"0x{langId:X4}"
    };
}
