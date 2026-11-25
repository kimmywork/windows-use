// SelfEditHost.cs — 进程内自建目标窗口（标准 Win32 EDIT 控件）
//
// 为什么需要它
// ------------
// Windows 11（25H2）的 notepad.exe 是**单实例重定向**应用：
//   Process.Start("notepad.exe") 拿到的进程要么立即退出，要么始终拿不到主窗口
//   （实测：pid=5780 存活但 MainWindowHandle=0，真实窗口属于另一个 pid）。
//   因此 NotepadHost 的「前台窗口 pid 必须等于本进程启动的 pid」校验永远过不了，
//   护栏会正确地拒绝发送输入 —— 但也因此拿不到任何数据。
//
// 本类改为**在本进程内自建一个标准 EDIT 控件**作为目标：
//   · 同进程 → 无重定向、无归属歧义，护栏的 pid 校验天然成立；
//   · 标准 Win32 EDIT → 是最典型的文本输入目标，且 IME 照常介入，
//     因此「KEYEVENTF_UNICODE 能否可靠输入中文」这个 A-05 的核心问题依然有效；
//   · 无外部依赖 → 不依赖任何已安装应用。
//
// 安全性：窗口由本进程创建、由本进程销毁；EnsureFocus 仍严格校验
//         GetForegroundWindow 属于本进程、且焦点确实落在 EDIT 控件上。

using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinUse.PoC.Input;

internal sealed class SelfEditHost
{
    private const string MainWindowClassName = "WinUsePoCInputHost";
    private const string EditClassName = "Edit";
    private const string UnicodeEditClassName = "WinUsePoCUnicodeEdit";

    // 窗口样式
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VSCROLL = 0x00200000;
    private const uint WS_BORDER = 0x00800000;

    // EDIT 控件样式
    private const uint ES_MULTILINE = 0x0004;
    private const uint ES_AUTOVSCROLL = 0x0040;
    private const uint ES_WANTRETURN = 0x1000;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUIT = 0x0012;
    // 自定义消息：要求 **UI 线程**执行置焦。
    // 不能在主线程直接调 SetFocus / AttachThreadInput —— 两者都绑定到
    //「拥有窗口的那个线程」的输入队列，跨线程调用无效（首轮 FOCUS_GUARD_FAILED 的根因）。
    private const uint WM_APP_ENSURE_FOCUS = 0x8001;
    private const uint WM_APP_SET_KL = 0x8002;
    private const uint WM_APP_RESTORE_KL = 0x8003;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_RESTORE = 9;
    private const int SMTO_ABORTIFHUNG = 0x0002;
    private const int COLOR_WINDOW = 5;

    private Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProcDelegate _wndProc;
    private readonly string _editMode;

    // 置焦结果：UI 线程写，主线程读
    private readonly ManualResetEventSlim _focusDone = new(false);
    private bool _focusOk;
    private string _focusDetail = "";

    // 线程键盘布局切换结果：UI 线程写，主线程读
    private readonly ManualResetEventSlim _klDone = new(false);
    private bool _klOk;
    private string _klDetail = "";
    private IntPtr _klPrev = IntPtr.Zero;
    private string _klRequested = "";

    public uint Pid { get; }
    public IntPtr MainWindow { get; private set; } = IntPtr.Zero;
    public IntPtr EditWindow { get; private set; } = IntPtr.Zero;
    public string EditClass { get; private set; } = EditClassName;
    public string LaunchDetail { get; private set; } = "";
    public bool Available { get; private set; }
    public bool IsEditAlive => EditWindow != IntPtr.Zero && Native.IsWindow(EditWindow);

    private SelfEditHost(string editMode)
    {
        Pid = (uint)Environment.ProcessId;
        // 持有委托引用，防止被 GC 回收（窗口过程委托必须存活到窗口销毁）
        _wndProc = WndProc;
        // "unicode" → CreateWindowExW（Unicode 窗口，现代应用的常态）
        // "ansi"    → CreateWindowExA（ANSI 窗口，老程序/部分行业软件仍是）
        // 两者的差别只在「系统投递 WM_CHAR 时要不要把码元按 ACP 做 best-fit 转换」，
        // 这正是 A-05 必须分开跑两组的原因。
        _editMode = editMode == "ansi" ? "ansi" : "unicode";
    }
    public string EditMode => _editMode;

    // ------------------------------------------------------------------
    // 启动：在独立 STA 线程上建窗口 + 跑消息循环
    // ------------------------------------------------------------------
    public static SelfEditHost Launch(string editMode = "unicode")
    {
        var host = new SelfEditHost(editMode);
        host._thread = new Thread(host.ThreadBody);
        host._thread.IsBackground = true;
        host._thread.SetApartmentState(ApartmentState.STA);
        host._thread.Name = "SelfEditHost-UI";
        host._thread.Start();

        if (!host._ready.Wait(TimeSpan.FromSeconds(10)))
        {
            host.LaunchDetail = "10 秒内自建目标窗口未就绪";
            host.Available = false;
            return host;
        }
        if (string.IsNullOrEmpty(host.LaunchDetail))
        {
            host.LaunchDetail = host.Available
                ? $"已在本进程内自建目标窗口（pid={host.Pid}）"
                : "自建目标窗口创建失败（10 秒内未就绪或创建返回空句柄）";
        }
        return host;
    }

    private void ThreadBody()
    {
        try
        {
            ThreadBodyCore();
        }
        catch (Exception ex)
        {
            // UI 线程崩掉必须留下痕迹，否则主线程只能看到「自建窗口未就绪」这种假象
            LaunchDetail = "自建窗口线程异常：" + ex.GetType().Name + " / " + ex.Message;
            _ready.Set();
        }
    }

    private void ThreadBodyCore()
    {
        IntPtr hInstance = GetModuleHandle(null);
        _ownWindowIsTarget = _editMode == "unicode";

        var wcex = new WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = IntPtr.Zero,
            hCursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
            hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
            lpszMenuName = null,
            lpszClassName = MainWindowClassName,
            hIconSm = IntPtr.Zero,
        };
        RegisterClassEx(ref wcex);

        IntPtr main = CreateWindowEx(
            0, MainWindowClassName, "WinUse PoC — 输入目标（自建）",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            200, 140, 860, 640,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (main == IntPtr.Zero)
        {
            Available = false;
            _ready.Set();
            return;
        }

        uint editStyle = WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_BORDER | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN;

        IntPtr edit;
        if (_editMode == "ansi")
        {
            // ANSI EDIT：老程序形态。系统会给它投递「按 ACP 转码后」的 WM_CHAR
            edit = CreateWindowExA(0, EditClassName, "", editStyle,
                8, 8, 826, 560, main, IntPtr.Zero, hInstance, IntPtr.Zero);
            EditClass = EditClassName;
        }
        else
        {
            // 目标就是主窗口本身：它的类是我们用 RegisterClassExW 注册的，
            // 因此 IsWindowUnicode=TRUE（等价于一类现代应用的窗口）。
            // 走这条路是为了绕开「系统预注册 Edit 类本身是 ANSI 类」这个坑 ——
            // 对它做超类化会让进程原生崩溃。
            edit = main;
            EditClass = MainWindowClassName;
        }

        MainWindow = main;
        EditWindow = edit;
        Available = main != IntPtr.Zero && edit != IntPtr.Zero;

        // 只有 ANSI EDIT 那条路需要挂 wndproc 抓字符消息；
        // Unicode 模式下目标就是主窗口，WndProc 里直接累积即可。
        if (_editMode == "ansi") SubclassEdit();

        ShowWindowAsync(main, SW_SHOWNORMAL);
        _ready.Set();

        // 消息循环
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            // 诊断：KEYEVENTF_UNICODE 事件到达时是 WM_KEYDOWN + VK_PACKET，
            // 码元放在 lParam 的 scan code 位（16..31）。先记下它，才能区分
            // 「SendInput 端就送错了」还是「控件端转码错了」。
            if (msg.message == Native.WM_KEYDOWN && msg.wParam.ToInt64() == Native.VK_PACKET)
            {
                int scan = (int)((msg.lParam.ToInt64() >> 16) & 0xFFFF);
                lock (_charLogLock)
                {
                    if (_charLog.Count < 1024) _charLog.Add($"PKT:0x{scan:X4}");
                }
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_SET_KL)
        {
            _klPrev = Native.GetKeyboardLayout(0);
            IntPtr hkl = Native.LoadKeyboardLayout(_klRequested, Native.KLF_ACTIVATE);
            IntPtr act = hkl != IntPtr.Zero ? Native.ActivateKeyboardLayout(hkl, 0) : IntPtr.Zero;
            IntPtr now = Native.GetKeyboardLayout(0);
            _klOk = hkl != IntPtr.Zero;
            _klDetail = $"线程键盘布局 原=0x{_klPrev.ToInt64():X} → 请求 {_klRequested} → "
                        + $"LoadKeyboardLayout=0x{hkl.ToInt64():X}，Activate=0x{act.ToInt64():X}，现=0x{now.ToInt64():X}";
            _klDone.Set();
            return IntPtr.Zero;
        }
        if (msg == WM_APP_RESTORE_KL)
        {
            if (_klPrev != IntPtr.Zero) Native.ActivateKeyboardLayout(_klPrev, 0);
            _klDetail = $"已还原线程键盘布局 → 现=0x{Native.GetKeyboardLayout(0).ToInt64():X}（进入时 0x{_klPrev.ToInt64():X}）";
            _klOk = true;
            _klDone.Set();
            return IntPtr.Zero;
        }
        if (msg == Native.WM_CHAR || msg == Native.WM_UNICHAR || msg == Native.WM_IME_CHAR)
        {
            LogCharMessage(msg, wParam);
            // Unicode 模式：目标就是本窗口，自己把收到的码元累积成文本
            if (_ownWindowIsTarget && hwnd == EditWindow)
            {
                lock (_bufferLock) _buffer.Append((char)(ushort)(wParam.ToInt64() & 0xFFFF));
            }
        }
        if (msg == WM_DESTROY)
        {
            PostMessage(hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            return IntPtr.Zero;
        }
        if (msg == WM_APP_ENSURE_FOCUS)
        {
            // 已在拥有窗口的线程上 —— 此处调用 SetFocus / AttachThreadInput 才有效
            _focusOk = DoEnsureFocusOnUiThread(out string d);
            _focusDetail = d;
            _focusDone.Set();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------
    // 焦点：置前台 + 把焦点放到 EDIT，并严格校验
    // ------------------------------------------------------------------
    /// <summary>
    /// 在**本进程 UI 线程内**临时切换键盘布局。不碰全局输入法，但退出前必须还原。
    /// 用途：验证「KEYEVENTF_UNICODE 生成的 WM_CHAR 取决于线程键盘布局的代码页」。
    /// </summary>
    public bool TrySetThreadKeyboardLayout(string klid, out string detail)
    {
        detail = "";
        if (!Available) { detail = "目标窗口不可用"; return false; }
        _klRequested = klid;
        return PostKlMessage(WM_APP_SET_KL, out detail);
    }

    public bool RestoreThreadKeyboardLayout(out string detail)
        => PostKlMessage(WM_APP_RESTORE_KL, out detail);

    private bool PostKlMessage(uint msg, out string detail)
    {
        _klDone.Reset();
        _klOk = false;
        _klDetail = "";
        PostMessage(MainWindow, msg, IntPtr.Zero, IntPtr.Zero);
        if (!_klDone.Wait(TimeSpan.FromSeconds(5)))
        {
            detail = $"消息 0x{msg:X} 在 UI 线程上 5 秒内未完成";
            return false;
        }
        detail = _klDetail;
        return _klOk;
    }

    public bool EnsureFocus(out string detail)
    {
        detail = "";
        if (!Available) { detail = "目标窗口不可用"; return false; }

        // 投递到 UI 线程执行，等它完成（最多 5 秒）
        _focusDone.Reset();
        _focusOk = false;
        _focusDetail = "";
        PostMessage(MainWindow, WM_APP_ENSURE_FOCUS, IntPtr.Zero, IntPtr.Zero);
        if (!_focusDone.Wait(TimeSpan.FromSeconds(5)))
        {
            detail = "置焦请求在 UI 线程上 5 秒内未完成（消息泵可能未响应）";
            return false;
        }
        detail = _focusDetail;
        return _focusOk;
    }

    /// <summary>必须在拥有窗口的线程上调用。</summary>
    private bool DoEnsureFocusOnUiThread(out string detail)
    {
        detail = "";
        Native.ShowWindowAsync(MainWindow, SW_RESTORE);

        string lastReason = "";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt == 1)
            {
                Native.SetForegroundWindow(MainWindow);
                // 必须显式把焦点放到 EDIT 上。只 SetForegroundWindow 的话，
                // 焦点会停在主窗口本身，后续 SendInput 的字符被 DefWindowProc 吞掉
                // —— 首轮 15/15 全 0 的根因就是这个。
                if (EditWindow != IntPtr.Zero) Native.SetFocus(EditWindow);
                PumpFor(150);
                if (CheckFocusOwnedByUs(out string why)) { detail = "SetForegroundWindow + SetFocus(EDIT) 成功（第 1 次尝试）"; return true; }
                lastReason = why;
            }
            else
            {
                // 退路：与当前前台线程捆绑输入队列后再激活（NotepadHost 里验证过的做法）
                uint fgThread = Native.ThreadOf(Native.GetForegroundWindow());
                uint myThread = Native.GetCurrentThreadId();
                bool forward = fgThread != 0 && fgThread != myThread && Native.AttachThreadInput(fgThread, myThread, true);
                bool backward = !forward && fgThread != 0 && fgThread != myThread && Native.AttachThreadInput(myThread, fgThread, true);
                try
                {
                    Native.BringWindowToTop(MainWindow);
                    Native.SetForegroundWindow(MainWindow);
                    if (EditWindow != IntPtr.Zero) Native.SetFocus(EditWindow);
                    PumpFor(150);
                }
                finally
                {
                    if (forward) Native.AttachThreadInput(fgThread, myThread, false);
                    if (backward) Native.AttachThreadInput(myThread, fgThread, false);
                }
                if (CheckFocusOwnedByUs(out string why2))
                {
                    detail = forward ? "AttachThreadInput(fg→self) + SetForegroundWindow 成功"
                                     : "AttachThreadInput(self→fg) + SetForegroundWindow 成功";
                    return true;
                }
                lastReason = why2;
            }
        }

        detail = "无法把焦点可靠地放到自建窗口上：" + lastReason + "（已放弃，本次不发送任何输入）";
        return false;
    }

    /// <summary>焦点校验：前台窗口属于本进程，且线程焦点在自建窗口树内。</summary>
    private bool CheckFocusOwnedByUs(out string reason)
    {
        reason = "";
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) { reason = "GetForegroundWindow 为空"; return false; }
        if (!Native.BelongsToProcess(fg, Pid))
        {
            Native.GetWindowThreadProcessId(fg, out uint owner);
            reason = $"前台窗口 {Native.Hx(fg)} 属于 pid={owner}（期望 pid={Pid}）";
            return false;
        }

        uint tid = Native.ThreadOf(MainWindow);
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(tid, ref info)) { reason = "GetGUIThreadInfo 失败"; return false; }

        IntPtr focus = info.hwndFocus;
        if (focus == IntPtr.Zero) { reason = "thread 无焦点窗口"; return false; }
        if (!Native.IsSelfOrDescendant(focus, MainWindow))
        {
            reason = $"焦点窗口 {Native.Hx(focus)}（class={Native.ClassName(focus)}）不在自建窗口树内";
            return false;
        }
        if (EditWindow != IntPtr.Zero && focus != EditWindow)
        {
            // 不能「仍可接受」：向非 EDIT 的窗口注入字符会被 DefWindowProc 吞掉，
            // 结果是静默全 0 —— 宁可让护栏拦下来报 FOCUS_GUARD_FAILED。
            reason = $"焦点窗口为 {Native.Hx(focus)}（class={Native.ClassName(focus)}），非 EDIT 控件（期望 {Native.Hx(EditWindow)}）";
            return false;
        }
        return true;
    }

    public string FocusClassName()
    {
        // GetFocus() 只反映**调用线程**的焦点，主线程拿不到 UI 线程的焦点 → 走 GetGUIThreadInfo
        if (MainWindow == IntPtr.Zero) return "";
        uint tid = Native.ThreadOf(MainWindow);
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(tid, ref info) || info.hwndFocus == IntPtr.Zero) return "";
        return Native.ClassName(info.hwndFocus);
    }

    // ------------------------------------------------------------------
    // EDIT 子类化：记录它实际收到的字符消息 wParam
    // ------------------------------------------------------------------
    private IntPtr _editOrigProc = IntPtr.Zero;
    private WndProcDelegate? _editProc;
    private bool _ownWindowIsTarget;
    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();
    private readonly List<string> _charLog = new();
    private readonly object _charLogLock = new();

    /// <summary>EDIT 控件是否是 Unicode 窗口（ANSI 窗口会把 WM_CHAR 的码元按 ACP 做 best-fit 转换）。</summary>
    public bool EditIsUnicode => EditWindow != IntPtr.Zero && Native.IsWindowUnicode(EditWindow);
    public IntPtr EditOriginalProc => _editOrigProc;

    private void SubclassEdit()
    {
        if (EditWindow == IntPtr.Zero) return;
        _editProc = EditWndProc;
        _editOrigProc = Native.SetWindowSubclassProc(EditWindow,
            Marshal.GetFunctionPointerForDelegate(_editProc));
    }

    private IntPtr EditWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_CHAR || msg == Native.WM_UNICHAR || msg == Native.WM_IME_CHAR
            || msg == Native.WM_DEADCHAR || msg == Native.WM_SYSCHAR)
        {
            LogCharMessage(msg, wParam);
        }
        return Native.CallWindowProc(_editOrigProc, hwnd, msg, wParam, lParam);
    }

    private void LogCharMessage(uint msg, IntPtr wParam)
    {
        string name = msg switch
        {
            Native.WM_CHAR => "WM_CHAR",
            Native.WM_UNICHAR => "WM_UNICHAR",
            Native.WM_IME_CHAR => "WM_IME_CHAR",
            Native.WM_DEADCHAR => "WM_DEADCHAR",
            _ => "WM_SYSCHAR"
        };
        lock (_charLogLock)
        {
            if (_charLog.Count < 1024)
                _charLog.Add($"{name}:0x{(ulong)(wParam.ToInt64() & 0xFFFFFFFFL):X4}");
        }
    }

    public void ClearCharLog() { lock (_charLogLock) _charLog.Clear(); }

    public List<string> SnapshotCharLog() { lock (_charLogLock) return new List<string>(_charLog); }

    /// <summary>在拥有窗口的线程上泵消息 ms 毫秒。</summary>
    /// 不能用 Thread.Sleep 代替：SetForegroundWindow / SetFocus 产生的
    /// WM_ACTIVATE / WM_SETFOCUS 要真正派发后焦点状态才更新，睡过去等于没等。
    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (PeekMessage(out MSG m, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
    }

    // ------------------------------------------------------------------
    // 读回 / 清空
    // ------------------------------------------------------------------
    public bool TryReadText(out string text, out string detail, int maxChars = 8192)
    {
        text = "";
        detail = "";
        if (_ownWindowIsTarget)
        {
            lock (_bufferLock) text = _buffer.ToString();
            return true;
        }
        if (!IsEditAlive) { detail = "EDIT 控件已失效"; return false; }

        var sb = new StringBuilder(maxChars);
        IntPtr r = Native.SendMessageTimeoutText(EditWindow, Native.WM_GETTEXT, (IntPtr)maxChars, sb,
            SMTO_ABORTIFHUNG, 3000, out _);
        if (r == IntPtr.Zero)
        {
            detail = "WM_GETTEXT 失败（超时或超时被放弃）";
            return false;
        }
        text = sb.ToString();
        return true;
    }

    public bool TryClear(out string method, out string detail)
    {
        method = "WM_SETTEXT";
        detail = "";
        if (_ownWindowIsTarget)
        {
            lock (_bufferLock) _buffer.Clear();
            method = "清空自建 Unicode 窗口的字符累积缓冲";
            return true;
        }
        if (!IsEditAlive) { detail = "EDIT 控件已失效"; return false; }

        IntPtr r = Native.SendMessageTimeoutStr(EditWindow, Native.WM_SETTEXT, IntPtr.Zero, "",
            SMTO_ABORTIFHUNG, 3000, out _);
        if (r == IntPtr.Zero) { detail = "WM_SETTEXT 清空失败"; return false; }
        return true;
    }

    // ------------------------------------------------------------------
    // 位置与关闭
    // ------------------------------------------------------------------
    public void MoveToVisibleSpot(int x, int y, int width, int height)
    {
        if (MainWindow == IntPtr.Zero) return;
        MoveWindow(MainWindow, x, y, width, height, true);
    }

    public string Close(out bool closed)
    {
        closed = false;
        if (MainWindow == IntPtr.Zero) return "无自建窗口可关闭";

        try
        {
            PostMessage(MainWindow, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            closed = true;
            return "已发送 WM_CLOSE（自建窗口，无保存对话框）";
        }
        catch (Exception ex)
        {
            return "关闭自建窗口异常：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Native.POINT pt;
    }

    // 消息循环
    private const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "RegisterClassExW", ExactSpelling = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    // 超类化（superclass）用：取系统 "Edit" 类的原始信息。
    // 系统预注册的 "Edit" 类是 ANSI 类 —— CreateWindowExW 也建不出 Unicode EDIT，
    // 必须自己用 RegisterClassExW 注册一个新类，IsWindowUnicode 才会是 TRUE。
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "GetClassInfoExW", ExactSpelling = true)]
    private static extern bool GetClassInfoExW(IntPtr hInstance, string lpClassName, out WNDCLASSEX lpWndClass);

    // 显式指向 W 版：不能让 .NET 按 ExactSpelling=false 去猜，
    // 猜错就会拿到 ANSI 版窗口（首轮 IsWindowUnicode=False 的直接原因）。
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "CreateWindowExW", ExactSpelling = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Ansi,
               EntryPoint = "CreateWindowExA", ExactSpelling = true)]
    private static extern IntPtr CreateWindowExA(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // GetMessage 返回 int：>0 有消息、0 = WM_QUIT、-1 = 出错。
    // 不能声明成 bool —— 消息循环需要区分三态（尤其 -1）。
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
