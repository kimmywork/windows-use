// Native.cs — 第二轮 B（侵入式 PoC）所需的 Win32 / IMM32 / Kernel32 声明。
//
// 【安全约束】本文件只做声明。所有"输入类"API（SendInput / SetCursorPos / WM_SETTEXT / WM_CLOSE）
// 是否允许调用，由调用层负责校验：目标窗口必须是本程序自行启动的 notepad 进程的窗口。
// 明确不声明：TerminateProcess、注册表写入、SetWindowsHookEx、SendKeys 类全局钩子。

using System.Runtime.InteropServices;
using System.Text;

namespace WinUse.PoC.Input;

internal static class Native
{
    // =================================================================
    // 系统指标
    // =================================================================
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_CMONITORS = 80;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // =================================================================
    // 基础结构
    // =================================================================
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public override string ToString() => $"({X},{Y})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public override string ToString() => $"[{Left},{Top},{Right},{Bottom}] {Width}x{Height}";
    }

    // =================================================================
    // 显示器拓扑（用于 A-02 负坐标前置校验）
    // =================================================================
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

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);

    public const int MDT_EFFECTIVE_DPI = 0;

    // GetDpiForMonitor 在 shcore.dll（不在 user32.dll）
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    // =================================================================
    // DPI Awareness
    // =================================================================
    public const int DPI_AWARENESS_CONTEXT_UNAWARE = -1;
    public const int DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2;
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    public const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(int value);

    // GetProcessDpiAwareness 在 shcore.dll（已知坑）
    [DllImport("shcore.dll")]
    public static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    public static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    // =================================================================
    // 窗口
    // =================================================================
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc fn, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int maxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int maxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    public const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetFocus(IntPtr hWnd);

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    // =================================================================
    // 消息（跨进程一律走 SendMessageTimeout + SMTO_ABORTIFHUNG，避免目标挂起时本进程卡死）
    // =================================================================
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_CLOSE = 0x0010;
    public const uint EM_SETMODIFY = 0x00B9;   // 关闭时的"是否保存"对话框依赖该脏标记
    public const uint BM_CLICK = 0x00F5;

    // 字符投递通道诊断用
    public const uint WM_CHAR = 0x0102;
    public const uint WM_UNICHAR = 0x0109;
    public const uint WM_IME_CHAR = 0x0286;
    public const uint WM_DEADCHAR = 0x0103;
    public const uint WM_SYSCHAR = 0x0106;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint VK_PACKET = 0x00E7;

    // 子类化支持：为了抓 EDIT 实际收到的 WM_CHAR wParam
    [DllImport("kernel32.dll")]
    public static extern uint GetACP();

    // 键盘布局（线程级）。KEYEVENTF_UNICODE 的 WM_CHAR 生成发生在 TranslateMessage，
    // 用的是「调用线程的键盘布局所对应代码页」，不是进程 ACP —— 需要能改它来做对照。
    public const uint KLF_ACTIVATE = 0x00000001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadKeyboardLayoutW", ExactSpelling = true)]
    public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

    [DllImport("user32.dll")]
    public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint Flags);
    // GetKeyboardLayout 在本文件后面已声明（线程级布局读取），此处不再重复。

    public const int GWLP_WNDPROC = -4;

    [DllImport("user32.dll")]
    public static extern bool IsWindowUnicode(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    public static IntPtr SetWindowSubclassProc(IntPtr hWnd, IntPtr newProc)
        => SetWindowLongPtr64(hWnd, GWLP_WNDPROC, newProc);

    public static IntPtr GetWindowSubclassProc(IntPtr hWnd)
        => GetWindowLongPtr64(hWnd, GWLP_WNDPROC);

    [DllImport("user32.dll")]
    public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint DefaultMessageTimeoutMs = 2000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeout")]
    public static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeout")]
    public static extern IntPtr SendMessageTimeoutStr(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // =================================================================
    // 光标
    // =================================================================
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    // =================================================================
    // 合成输入（本次实测的核心）
    // =================================================================
    public const uint INPUT_KEYBOARD = 1;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_A = 0x41;
    public const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // =================================================================
    // 剪贴板（走原生 API，不依赖 STA / System.Windows.Forms）
    // =================================================================
    public const uint CF_UNICODE = 13;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);

    public const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    // =================================================================
    // IME（只读查询；不得调用 ImmSet* 改变用户输入法状态）
    // =================================================================
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

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    public const uint IME_CMODE_NATIVE = 0x0001;
    public const uint IME_CMODE_FULLSHAPE = 0x0008;

    // =================================================================
    // 便捷封装
    // =================================================================
    public static string ClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        int n = GetClassName(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    public static string Title(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        int n = GetWindowText(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    public static string Hx(IntPtr h) => "0x" + h.ToInt64().ToString("X");

    public static uint ThreadOf(IntPtr hWnd) => GetWindowThreadProcessId(hWnd, out _);

    /// <summary>hwnd 是否属于指定进程。</summary>
    public static bool BelongsToProcess(IntPtr hWnd, uint pid)
    {
        if (hWnd == IntPtr.Zero || pid == 0) return false;
        GetWindowThreadProcessId(hWnd, out uint owner);
        return owner == pid;
    }

    /// <summary>child 是否就是 parent 或其子孙窗口。</summary>
    public static bool IsSelfOrDescendant(IntPtr child, IntPtr parent)
    {
        if (child == IntPtr.Zero || parent == IntPtr.Zero) return false;
        IntPtr cur = child;
        for (int i = 0; i < 32 && cur != IntPtr.Zero; i++)
        {
            if (cur == parent) return true;
            cur = GetParent(cur);
        }
        return false;
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

    /// <summary>构造一条 Unicode 合成按键事件（KEYEVENTF_UNICODE，含 keydown/keyup）。</summary>
    public static INPUT UnicodeKey(char codeUnit, bool keyUp)
    {
        uint flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0u);
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Data = new INPUTUNION
            {
                Keyboard = new KEYBDINPUT
                {
                    Vk = 0,
                    Scan = codeUnit,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    /// <summary>构造一条虚拟键按下/抬起事件（仅用于 Ctrl+A / Ctrl+V 这类允许的组合键）。</summary>
    public static INPUT VirtualKey(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Data = new INPUTUNION
            {
                Keyboard = new KEYBDINPUT
                {
                    Vk = vk,
                    Scan = 0,
                    Flags = keyUp ? KEYEVENTF_KEYUP : 0u,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}
