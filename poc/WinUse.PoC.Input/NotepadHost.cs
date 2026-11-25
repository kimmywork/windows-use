// NotepadHost.cs — 自建 notepad 的生命周期与安全护栏。
//
// 【核心安全契约】
//   本类是唯一允许向窗口发送按键/文本消息的通道，且必须同时满足三条：
//     1) 目标窗口句柄属于本对象自己 Process.Start 出来的进程（Pid 校验）；
//     2) 发送瞬间 GetForegroundWindow 的 pid 必须等于该 Pid；
//     3) 发送瞬间 GetGUIThreadInfo 的焦点窗口必须是该窗口树内的编辑框。
//   任一不满足即拒绝发送（返回 false），绝不向任何用户窗口发送输入。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinUse.PoC.Input;

internal sealed class NotepadHost
{
    /// <summary>notepad 编辑控件的常见类名，按优先级排列。</summary>
    private static readonly string[] PreferredEditClasses =
    {
        "RichEditD2DPT", "RICHEDIT50W", "RichEdit20W", "RichEdit20A", "Edit"
    };

    private Process? _process;

    public uint Pid { get; private set; }
    public IntPtr MainWindow { get; private set; } = IntPtr.Zero;
    public IntPtr EditWindow { get; private set; } = IntPtr.Zero;
    public string EditClass { get; private set; } = "";
    public string LaunchDetail { get; private set; } = "";
    public bool Available { get; private set; }

    /// <summary>
    /// 启动 notepad 并定位其主窗口与编辑控件。只认自己启动的进程；
    /// 若 notepad 把创建请求重定向到既有实例（本进程随即退出），则判定为不可用。
    /// </summary>
    public static NotepadHost Launch()
    {
        var host = new NotepadHost();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "notepad.exe",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start 返回 null");
            host._process = proc;
            host.Pid = (uint)proc.Id;
            host.LaunchDetail = $"已启动 notepad（pid={host.Pid}）";

            // 等待主窗口出现（最多 10 秒）
            IntPtr main = IntPtr.Zero;
            var timeout = Stopwatch.StartNew();
            while (timeout.ElapsedMilliseconds < 10000 && !IsCanceled())
            {
                if (proc.HasExited)
                {
                    host.LaunchDetail =
                        $"notepad 进程启动后立即退出（pid={host.Pid}），疑似重定向到既有实例。" +
                        "为避免向用户窗口发送输入，第一轮中止。";
                    return host;
                }
                main = FindMainWindowOfProcess(host.Pid);
                if (main != IntPtr.Zero) break;
                Thread.Sleep(100);
            }

            if (main == IntPtr.Zero)
            {
                host.LaunchDetail = "10 秒内未找到本进程拥有的 notepad 主窗口";
                return host;
            }

            host.MainWindow = main;

            // 等待编辑控件出现（Win11 的 RichEdit 可能延迟创建）
            IntPtr edit = IntPtr.Zero;
            timeout.Restart();
            while (timeout.ElapsedMilliseconds < 8000 && !IsCanceled())
            {
                edit = FindEditWindow(main);
                if (edit != IntPtr.Zero) break;
                Thread.Sleep(120);
            }

            if (edit != IntPtr.Zero)
            {
                host.EditWindow = edit;
                host.EditClass = Native.ClassName(edit);
                host.LaunchDetail += $"，主窗口={Native.Hx(main)}，编辑控件={Native.Hx(edit)}（class={host.EditClass}）";
            }
            else
            {
                host.LaunchDetail += $"，主窗口={Native.Hx(main)}，但未定位到编辑控件（class 候选均未命中）";
            }

            host.Available = true;
        }
        catch (Exception ex)
        {
            host.LaunchDetail = "启动失败：" + ex.GetType().Name + "：" + ex.Message;
            host.Available = false;
        }
        return host;
    }

    private static bool IsCanceled() => Program.CancelRequested;

    // =================================================================
    // 窗口定位
    // =================================================================
    private static IntPtr FindMainWindowOfProcess(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd)) return true;
            if (!Native.BelongsToProcess(hWnd, pid)) return true;
            string cls = Native.ClassName(hWnd);
            if (cls == "Notepad")
            {
                found = hWnd;
                return false;
            }
            if (found == IntPtr.Zero)
            {
                string title = Native.Title(hWnd);
                if (!string.IsNullOrEmpty(title)) found = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>递归查找编辑控件：先按类名优先级，再退回"任意可接收文本的子窗口"。</summary>
    private static IntPtr FindEditWindow(IntPtr main)
    {
        var candidates = new List<IntPtr>();
        CollectDescendants(main, candidates, depth: 4);

        foreach (string preferred in PreferredEditClasses)
        {
            foreach (IntPtr h in candidates)
            {
                if (Native.ClassName(h) == preferred) return h;
            }
        }

        foreach (IntPtr h in candidates)
        {
            string cls = Native.ClassName(h);
            if (cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("Edit", StringComparison.OrdinalIgnoreCase))
            {
                return h;
            }
        }
        return IntPtr.Zero;
    }

    private static void CollectDescendants(IntPtr parent, List<IntPtr> sink, int depth)
    {
        if (parent == IntPtr.Zero || depth <= 0) return;
        Native.EnumChildWindows(parent, (hWnd, _) =>
        {
            sink.Add(hWnd);
            CollectDescendants(hWnd, sink, depth - 1);
            return true;
        }, IntPtr.Zero);
    }

    // =================================================================
    // 焦点护栏
    // =================================================================
    /// <summary>
    /// 把自建 notepad 提升到前台，并严格校验焦点确实落在它的编辑控件上。
    /// 返回 true 才允许后续发送任何输入。
    /// </summary>
    public bool EnsureFocus(out string detail)
    {
        detail = "";
        if (!Available || MainWindow == IntPtr.Zero || !Native.IsWindow(MainWindow))
        {
            detail = "主窗口不可用";
            return false;
        }

        Native.ShowWindowAsync(MainWindow, Native.SW_RESTORE);

        string lastReason = "";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt == 1)
            {
                Native.SetForegroundWindow(MainWindow);
                Thread.Sleep(150);
                if (CheckFocusOwnedByUs(out string why))
                {
                    detail = "SetForegroundWindow 成功（第 1 次尝试）";
                    return true;
                }
                lastReason = why;
            }
            else
            {
                // 退路：把当前前台线程的输入队列与自身捆绑后再激活。
                // 只作用于本程序自己启动的 notepad，不涉及任何用户窗口的输入。
                uint fgThread = Native.ThreadOf(Native.GetForegroundWindow());
                uint myThread = Native.GetCurrentThreadId();
                bool forward = fgThread != 0 && fgThread != myThread && Native.AttachThreadInput(fgThread, myThread, true);
                bool backward = !forward && fgThread != 0 && fgThread != myThread && Native.AttachThreadInput(myThread, fgThread, true);
                try
                {
                    Native.BringWindowToTop(MainWindow);
                    Native.SetForegroundWindow(MainWindow);
                    if (EditWindow != IntPtr.Zero) Native.SetFocus(EditWindow);
                    Thread.Sleep(150);
                }
                finally
                {
                    if (forward) Native.AttachThreadInput(fgThread, myThread, false);
                    if (backward) Native.AttachThreadInput(myThread, fgThread, false);
                }

                if (CheckFocusOwnedByUs(out string why2))
                {
                    detail = attempt == 2
                        ? "AttachThreadInput(fg→self) + SetForegroundWindow 成功"
                        : "AttachThreadInput(self→fg) + SetForegroundWindow 成功";
                    return true;
                }
                lastReason = why2;
            }
        }

        detail = "无法把焦点可靠地放到自建 notepad 上：" + lastReason
                 + "（已放弃，本用例不发送任何输入）";
        return false;
    }

    /// <summary>焦点校验：前台窗口属于本进程，且线程焦点位于本窗口树内。</summary>
    private bool CheckFocusOwnedByUs(out string reason)
    {
        reason = "";
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            reason = "GetForegroundWindow 为空";
            return false;
        }
        if (!Native.BelongsToProcess(fg, Pid))
        {
            Native.GetWindowThreadProcessId(fg, out uint owner);
            reason = $"前台窗口 {Native.Hx(fg)} 属于 pid={owner}（期望 pid={Pid}）";
            return false;
        }

        uint tid = Native.ThreadOf(MainWindow);
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(tid, ref info))
        {
            reason = "GetGUIThreadInfo 失败";
            return false;
        }

        IntPtr focus = info.hwndFocus;
        if (focus == IntPtr.Zero)
        {
            reason = "thread 无焦点窗口";
            return false;
        }
        if (!Native.IsSelfOrDescendant(focus, MainWindow))
        {
            reason = $"焦点窗口 {Native.Hx(focus)}（class={Native.ClassName(focus)}）不在 notepad 窗口树内";
            return false;
        }
        if (EditWindow != IntPtr.Zero && focus != EditWindow)
        {
            // 焦点在窗口树的其它部分（例如焦点落在外层容器），仍属可接受，但记录。
            reason = $"焦点窗口为 {Native.ClassName(focus)}，非编辑控件 {EditClass}";
            return true;
        }
        return true;
    }

    /// <summary>供用例记录：当前焦点窗口类名。</summary>
    public string FocusClassName()
    {
        uint tid = Native.ThreadOf(MainWindow);
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(tid, ref info) || info.hwndFocus == IntPtr.Zero) return "";
        return Native.ClassName(info.hwndFocus);
    }

    // =================================================================
    // 文本读写
    // =================================================================
    /// <summary>WM_GETTEXT 读回（跨进程走 SendMessageTimeout，避免目标挂起把本进程拖死）。</summary>
    public bool TryReadText(out string text, out string detail, int maxChars = 8192)
    {
        text = "";
        detail = "";
        if (EditWindow == IntPtr.Zero)
        {
            detail = "无编辑控件句柄";
            return false;
        }

        IntPtr res = Native.SendMessageTimeout(EditWindow, Native.WM_GETTEXTLENGTH,
            IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, Native.DefaultMessageTimeoutMs, out IntPtr lenResult);
        if (res == IntPtr.Zero)
        {
            detail = "WM_GETTEXTLENGTH 超时或失败";
            return false;
        }

        int length = lenResult.ToInt32();
        if (length < 0) length = 0;

        int capacity = Math.Min(length + 1, maxChars);
        var sb = new StringBuilder(capacity);
        IntPtr ret = Native.SendMessageTimeoutText(EditWindow, Native.WM_GETTEXT,
            (IntPtr)capacity, sb, Native.SMTO_ABORTIFHUNG, Native.DefaultMessageTimeoutMs, out IntPtr copied);
        if (ret == IntPtr.Zero)
        {
            detail = "WM_GETTEXT 超时或失败";
            return false;
        }

        text = sb.ToString();
        // RichEdit 内部行尾可能是 \r\n；只裁掉尾部换行，不改动其它字符。
        text = text.TrimEnd('\r', '\n');
        detail = $"WM_GETTEXT 成功（len={length}）";
        return true;
    }

    /// <summary>
    /// 清空编辑区。优先 WM_SETTEXT（不发任何按键），失败再退回 Ctrl+A + Delete。
    /// </summary>
    public bool TryClear(out string method, out string detail)
    {
        method = "";
        detail = "";
        if (EditWindow == IntPtr.Zero)
        {
            detail = "无编辑控件句柄";
            return false;
        }

        IntPtr ret = Native.SendMessageTimeoutStr(EditWindow, Native.WM_SETTEXT,
            IntPtr.Zero, string.Empty, Native.SMTO_ABORTIFHUNG, Native.DefaultMessageTimeoutMs, out IntPtr ok);
        if (ret != IntPtr.Zero && ok.ToInt64() != 0)
        {
            method = "WM_SETTEXT('')";
            Thread.Sleep(30);
            TryReadText(out string after, out _);
            if (after.Length == 0)
            {
                detail = "已清空";
                return true;
            }
            detail = "WM_SETTEXT 返回成功但读回非空，继续尝试按键方式";
        }

        // 退路：Ctrl+A → Delete（均在已通过上述焦点护栏的自建 notepad 内）
        var events = new List<Native.INPUT>
        {
            Native.VirtualKey(Native.VK_CONTROL, keyUp: false),
            Native.VirtualKey(Native.VK_A, keyUp: false),
            Native.VirtualKey(Native.VK_A, keyUp: true),
            Native.VirtualKey(Native.VK_CONTROL, keyUp: true),
            Native.VirtualKey(0x2E, keyUp: false),        // VK_DELETE
            Native.VirtualKey(0x2E, keyUp: true)
        };
        uint sent = Native.SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<Native.INPUT>());
        Thread.Sleep(80);
        TryReadText(out string afterKeys, out _);
        method = "Ctrl+A + Delete";
        if (afterKeys.Length == 0)
        {
            detail = "已清空（SendInput 成功数=" + sent + "）";
            return true;
        }
        detail = "两次清空均未成功，读回长度=" + afterKeys.Length;
        return false;
    }

    // =================================================================
    // 收尾
    // =================================================================
    /// <summary>
    /// 清理并关闭自建 notepad：先清空内容并复位 EM 脏标记，让 WM_CLOSE 不弹"是否保存"；
    /// 万一仍弹出，由 UiAutomationProbe.TryInvokeDontSave 处理（不发送按键）。
    /// </summary>
    public string Close(out bool closed)
    {
        closed = false;
        var steps = new List<string>();

        if (_process == null)
        {
            return "未启动过 notepad";
        }

        try
        {
            if (Native.IsWindow(EditWindow))
            {
                Native.SendMessageTimeoutStr(EditWindow, Native.WM_SETTEXT, IntPtr.Zero, string.Empty,
                    Native.SMTO_ABORTIFHUNG, Native.DefaultMessageTimeoutMs, out _);
                steps.Add("已清空编辑区内容");

                Native.SendMessage(EditWindow, Native.EM_SETMODIFY, IntPtr.Zero, IntPtr.Zero);
                steps.Add("EM_SETMODIFY(FALSE)；让关闭流程不认为文档被改动");
            }

            if (Native.IsWindow(MainWindow))
            {
                Native.SendMessageTimeout(MainWindow, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, Native.DefaultMessageTimeoutMs, out _);
                steps.Add("WM_CLOSE");
            }

            int waited = 0;
            while (waited < 4000 && !_process.HasExited)
            {
                IntPtr dialog = FindDialogOfProcess();
                if (dialog != IntPtr.Zero)
                {
                    steps.Add("检测到保存对话框 " + Native.Hx(dialog) + "，尝试点“不保存”");
                    using var probe = UiAutomationProbe.Attach(dialog, maxDepth: 5);
                    if (probe.TryInvokeDontSave(dialog, out string dialogDetail))
                    {
                        steps.Add("UIA：" + dialogDetail);
                    }
                    else
                    {
                        steps.Add("UIA 失败：" + dialogDetail + "；尝试 BM_CLICK");
                        BM_ClickDontSave(dialog, steps);
                    }
                }
                Thread.Sleep(200);
                waited += 200;
            }

            if (!_process.HasExited)
            {
                // 仍存活：最后一次机会用进程句柄结束（这是本程序自己创建的进程，不含用户数据）
                steps.Add("对话框处理无效，调用 KillOwnedProcess（仅作用于本程序启动的 pid=" + Pid + "）");
                _process.Kill();
                _process.WaitForExit(3000);
            }

            closed = _process.HasExited;
            steps.Add(closed ? "notepad 已退出" : "notepad 仍未退出");
        }
        catch (Exception ex)
        {
            steps.Add("关闭异常：" + ex.GetType().Name + "：" + ex.Message);
            try
            {
                if (_process != null && !_process.HasExited) _process.Kill();
            }
            catch
            {
                // 忽略
            }
        }

        return string.Join("；", steps);
    }

    /// <summary>查找本进程弹出的对话框（保存提示等）。</summary>
    private IntPtr FindDialogOfProcess()
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd)) return true;
            if (!Native.BelongsToProcess(hWnd, Pid)) return true;
            if (hWnd == MainWindow) return true;
            string cls = Native.ClassName(hWnd);
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>对话框按钮的文本匹配 + BM_CLICK（纯 Win32，不按键）。</summary>
    private static void BM_ClickDontSave(IntPtr dialog, List<string> steps)
    {
        var buttons = new List<IntPtr>();
        CollectDescendants(dialog, buttons, depth: 4);
        string[] keys = { "不保存", "不要保存", "Don't save", "Don’t save", "否" };
        foreach (IntPtr h in buttons)
        {
            string cls = Native.ClassName(h);
            if (!cls.Contains("Button", StringComparison.OrdinalIgnoreCase)) continue;
            string text = Native.Title(h);
            if (string.IsNullOrEmpty(text)) continue;
            foreach (string key in keys)
            {
                if (text.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Native.SendMessageTimeout(h, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero,
                        Native.SMTO_ABORTIFHUNG, Native.DefaultMessageTimeoutMs, out _);
                    steps.Add($"BM_CLICK[{text}]");
                    return;
                }
            }
        }
        steps.Add("未找到可点击的“不保存”按钮");
    }

    /// <summary>把自建窗口挪到人能看见的位置（仅对自己的窗口调用）。</summary>
    public void MoveToVisibleSpot(int x, int y, int width, int height)
    {
        if (MainWindow == IntPtr.Zero || !Native.BelongsToProcess(MainWindow, Pid)) return;
        Native.SetWindowPos(MainWindow, IntPtr.Zero, x, y, width, height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public bool IsEditAlive => EditWindow != IntPtr.Zero && Native.IsWindow(EditWindow);
}
