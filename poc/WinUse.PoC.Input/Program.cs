// Program.cs — 第二轮 B：侵入式实测主控。
//
// 第一阶段（A-05）：中文输入三方案在自建 notepad 上的实测对比。
// 第二阶段（A-02）：双屏负坐标下 SetCursorPos 的可达性（PMv2 前后各一组）。
//
// 安全约束（见 Initialize 与 Cleanup）：
//   · 输入只发给自己启动的 notepad，发送前逐次校验前台进程与焦点窗口；
//   · 鼠标只移动不点击，结束后必定还原原位置；
//   · 进程退出前必定关闭自建 notepad（不保存）+ 还原剪贴板 + 还原前台焦点；
//   · 启动后先打印完整计划并等待 5 秒，Ctrl+C 可中止（异常路径同样走 finally 清理）。

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WinUse.PoC.Input;

internal static class Program
{
    // ===============================================================
    // 常量与样本
    // ===============================================================
    private const int CountdownMs = 5000;
    private const int SettleFirstWaitMs = 120;
    private const int ReadStabilizeTimeoutMs = 3000;

    public static volatile bool CancelRequested;

    private const string DefaultNotepadRect = "x=200,y=140,w=860,h=640";

    /// <summary>五组样本：缺一不可。</summary>
    private sealed record SampleCase(string Id, string Label, string Text, string Note);

    private static readonly List<SampleCase> Samples = BuildSamples();

    private static List<SampleCase> BuildSamples()
    {
        string surrogate = "\U00020BB7";                     // 𠮷 U+20BB7 → UTF-16 长度 2
        string longPool = "一二三四五六七八九十甲乙丙丁戊己庚辛壬癸子丑寅卯辰巳午未申酉戌亥" +
                          "金木水火土天地玄黄宇宙洪荒日月盈昃辰宿列张寒来暑往秋收冬藏闰余成岁律吕调阳";
        var longBuilder = new StringBuilder(200);
        int poolIndex = 0;
        while (longBuilder.Length < 200)
        {
            longBuilder.Append(longPool[poolIndex % longPool.Length]);
            poolIndex++;
        }

        return new List<SampleCase>
        {
            new("S1", "纯中文", "你好世界", "4 个汉字"),
            new("S2", "中英数混排", "Hello 世界 123", "ASCII + CJK + 数字"),
            new("S3", "含中文全角标点", "测试：成功了吗？", "含 U+FF1A 冒号 / U+FF1F 问号"),
            new("S4", "UTF-16 代理对", surrogate, "U+20BB7，单个码位占两个 UTF-16 码元"),
            new("S5", "长串", longBuilder.ToString(), "200 个汉字（循环常用字表，便于定位丢字/乱序）")
        };
    }

    private static readonly List<(string Key, string Name)> StrategyOrder = new()
    {
        ("A", "KEYEVENTF_UNICODE"),
        ("B", "剪贴板+Ctrl+V"),
        ("C", "UIA ValuePattern")
    };

    // ===============================================================
    // 运行期状态
    // ===============================================================
    private static readonly InputRun Run = new();
    private static string _outputPath = "";
    private static string _imeArg = "unknown";
    private static string _phaseArg = "1,2";
    private static string _editArg = "unicode";
    private static string _threadKlArg = "";
    private static string _awarenessArg = "pmv2";
    private static bool _runPhase1 = true;
    private static bool _runPhase2 = true;
    private static bool _noWait;

    private static IntPtr _fg0 = IntPtr.Zero;
    private static Native.POINT _cursor0;
    private static bool _cursor0Ok;

    private static readonly ClipboardGuard Clipboard;
    private static readonly List<(IntPtr Handle, int Index, Native.RECT Rect)> MonitorHandles = new();

    // 目标宿主：进程内自建 EDIT 窗口。
    // 不用 NotepadHost 的原因见 SelfEditHost.cs 文件头 —— Windows 11 的 notepad.exe
    // 是单实例重定向应用，Process.Start 拿不到归属明确的窗口，护栏会（正确地）拒绝发送输入。
    private static SelfEditHost? _host;
    private static UiAutomationProbe? _probe;

    static Program()
    {
        Clipboard = new ClipboardGuard(verbose: true);
    }

    // ===============================================================
    // 入口
    // ===============================================================
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[warn] 设置控制台 UTF-8 编码失败：" + ex.Message);
        }

        Console.CancelKeyPress += OnCancelKey;

        if (args.Any(a => a == "--help" || a == "-h"))
        {
            PrintUsage();
            return 0;
        }

        ParseArgs(args);

        var root = FindRepoRoot();
        _outputPath = _outputPath != "" ? _outputPath : Path.Combine(root, "poc", "poc-input-results.json");

        FillRunMetadata(args);
        CaptureBaseline();
        SnapshotTopology();
        PrintPlan();

        WaitBeforeStart();

        int exit = 0;
        try
        {
            if (_runPhase1) Phase1_ChineseInput();
            if (_runPhase2) Phase2_NegativeCoordinates();
        }
        catch (Exception ex)
        {
            Run.Errors.Add("主流程异常：" + ex.GetType().Name + "：" + ex.Message);
            Console.WriteLine();
            Console.WriteLine("!! 主流程异常：" + ex);
            exit = 2;
        }
        finally
        {
            Cleanup();
            SaveResults();
        }

        PrintSummary();
        return exit;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("WinUse.PoC.Input — 第二轮 B 侵入式实测");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  WinUse.PoC.Input [选项]");
        Console.WriteLine();
        Console.WriteLine("  --ime cn|en        本轮运行前请把输入法切到该状态（cn=中文输入法，en=英文）");
        Console.WriteLine("  --phase 1|2|1,2    要跑的阶段，默认 1,2");
        Console.WriteLine("  --awareness auto|pmv2|pmv1|system|unaware");
        Console.WriteLine("                     第二阶段要声明的 DPI 感知级别，默认 pmv2");
        Console.WriteLine("  --edit unicode|ansi 目标 EDIT 的窗口形态，默认 unicode");
        Console.WriteLine("                     ansi = 老程序形态，系统投递 WM_CHAR 时会按 ACP 转码，用于对照");
        Console.WriteLine("  --out <路径>       结果 JSON 路径，默认 <仓库根>/poc/poc-input-results.json");
        Console.WriteLine("  --no-wait          跳过启动后的 5 秒等待（脚本调用时用，交互运行不建议）");
        Console.WriteLine();
        Console.WriteLine("安全约束：输入只发给自己启动的 notepad；鼠标只移动不点击；退出必定清理。");
    }

    private static void ParseArgs(string[] args)
    {
        _imeArg = ArgValue(args, "--ime", "unknown").ToLowerInvariant();
        _phaseArg = ArgValue(args, "--phase", "1,2");
        _awarenessArg = ArgValue(args, "--awareness", "pmv2").ToLowerInvariant();
        _outputPath = ArgValue(args, "--out", "");
        _editArg = ArgValue(args, "--edit", "unicode").ToLowerInvariant();
        _threadKlArg = ArgValue(args, "--thread-kl", "").ToLowerInvariant();
        if (_editArg != "unicode" && _editArg != "ansi")
        {
            Console.WriteLine($"[warn] --edit={_editArg} 无效，按 unicode 处理。");
            _editArg = "unicode";
        }
        _noWait = args.Contains("--no-wait");

        var phases = _phaseArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _runPhase1 = phases.Contains("1");
        _runPhase2 = phases.Contains("2");
        if (!_runPhase1 && !_runPhase2)
        {
            Console.WriteLine("[warn] --phase 未给出有效阶段，按 1,2 处理。");
            _runPhase1 = true;
            _runPhase2 = true;
        }
    }

    private static string ArgValue(string[] args, string key, string fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == key && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(key + "=", StringComparison.Ordinal)) return args[i][(key.Length + 1)..];
        }
        return fallback;
    }

    private static void FillRunMetadata(string[] args)
    {
        Run.RunId = Guid.NewGuid().ToString("N")[..8];
        Run.Timestamp = DateTimeOffset.Now.ToString("o");
        Run.MachineName = Environment.MachineName;
        Run.OsVersion = Environment.OSVersion.VersionString;
        Run.Is64BitProcess = Environment.Is64BitProcess;
        Run.CommandLine = string.Join(' ', args);
        Run.ImeArgument = _imeArg;
        Run.Phases = _phaseArg;
        Run.RequestedAwareness = _awarenessArg;

        IntPtr hkl = Native.GetKeyboardLayout(0);
        int langId = (int)(hkl.ToInt64() & 0xFFFF);
        Run.Environment.InputLocaleStart = langId;
        Run.Environment.InputLocaleStartName = Native.LangIdName(langId);
        Run.Environment.SystemDpi = Native.GetDpiForSystem();
    }

    /// <summary>记录进入程序前的前台窗口与鼠标位置（收尾要还原）。</summary>
    private static void CaptureBaseline()
    {
        _fg0 = Native.GetForegroundWindow();
        Run.Environment.InitialForegroundHwnd = Native.Hx(_fg0);
        Run.Environment.InitialForegroundClass = Native.ClassName(_fg0);
        Run.Environment.InitialForegroundTitle = Truncate(Native.Title(_fg0), 44);

        _cursor0Ok = Native.GetCursorPos(out _cursor0);
        Run.Environment.InitialCursor = _cursor0Ok
            ? new[] { _cursor0.X, _cursor0.Y }
            : Array.Empty<int>();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
    }

    // ===============================================================
    // 计划打印 + 倒计时
    // ===============================================================
    private static void PrintPlan()
    {
        Console.WriteLine();
        Console.WriteLine("=====================================================================");
        Console.WriteLine(" WinUse.PoC.Input — 第二轮 B：侵入式实测");
        Console.WriteLine("   A-05 中文输入三方案（KEYEVENTF_UNICODE / 剪贴板+Ctrl+V / ValuePattern）");
        Console.WriteLine("   A-02 双屏负坐标 SetCursorPos 可达性");
        Console.WriteLine("=====================================================================");
        Console.WriteLine($" 运行 ID        : {Run.RunId}");
        Console.WriteLine($" 机器 / 系统    : {Run.MachineName} / {Run.OsVersion}");
        Console.WriteLine($" 进程位数       : {(Run.Is64BitProcess ? 64 : 32)} 位");
        Console.WriteLine($" 本轮 IME 参数  : --ime {_imeArg}   （程序启动前请手动把输入法切到该状态）");
        Console.WriteLine($" 进程初始布局   : {Run.Environment.InputLocaleStartName} (0x{Run.Environment.InputLocaleStart:X4})");
        Console.WriteLine($" 结果输出       : {_outputPath}");
        Console.WriteLine();
        Console.WriteLine(" 当前 SOP 约定的 IME 操作说明：");
        Console.WriteLine("   · cn 轮：请先把输入法切到【中文】状态（候选窗/IME 打开）再运行；");
        Console.WriteLine("   · en 轮：请先把输入法切到【英文】状态再运行；");
        Console.WriteLine("   · 本程序不会修改你的输入法状态，只读取记录。");
        Console.WriteLine();
        Console.WriteLine(" 已记录的还原基线：");
        Console.WriteLine($"   · 进入程序前的前台窗口：{Native.Hx(_fg0)} class={Run.Environment.InitialForegroundClass} title={Run.Environment.InitialForegroundTitle}");
        Console.WriteLine($"   · 鼠标原位置            ：{(Run.Environment.InitialCursor.Length == 2 ? $"({Run.Environment.InitialCursor[0]},{Run.Environment.InitialCursor[1]})" : "(读取失败)")}");
        Console.WriteLine();
        Console.WriteLine("---------------------------------------------------------------------");
        Console.WriteLine(" 执行计划（安全边界）");
        Console.WriteLine("---------------------------------------------------------------------");
        Console.WriteLine(" 1) 只允许向本程序自己 Process.Start 启动的 notepad 窗口发送输入；");
        Console.WriteLine("    每次发送前都会校验：前台窗口 pid == 该 notepad pid，且线程焦点在其编辑控件上。");
        Console.WriteLine(" 2) 允许用户按键：普通字符、Ctrl+V、Ctrl+A，以及自建 notepad 内的 Delete。");
        Console.WriteLine("    禁止输入发送 Win / Alt+F4 / Ctrl+Alt+Del / Alt+Tab 等系统级组合键。");
        Console.WriteLine(" 3) 鼠标只调用 SetCursorPos 移动，绝不点击；结束前必定还原到进入程序前的位置。");
        Console.WriteLine(" 4) 不在用户目录写任何文件；唯一写出的产物是上述结果 JSON。");
        Console.WriteLine(" 5) 退出前必定关闭自建 notepad（不保存），并还原剪贴板与前台焦点。");
        Console.WriteLine();
        Console.WriteLine("---------------------------------------------------------------------");
        Console.WriteLine(" 阶段安排");
        Console.WriteLine("---------------------------------------------------------------------");
        if (_runPhase1)
        {
            Console.WriteLine(" 第一阶段：启动自建 notepad → 三方案 × 五样本 = 15 个用例");
            foreach (var s in Samples)
            {
                Console.WriteLine($"   · {s.Id} {s.Label}：长度={s.Text.Length}（UTF-16 码元）  {s.Note}");
            }
            Console.WriteLine("   每例：清空 → 注入 → 读回稳定后逐字符比对 → 记录首个不符位置/耗时/异常。");
            Console.WriteLine("   读回：优先 WM_GETTEXT，同时记录 UIA ValuePattern 值作为交叉校验。");
        }
        if (_runPhase2)
        {
            Console.WriteLine(" 第二阶段：仅 SetCursorPos 移动鼠标（不点击），四组坐标点：");
            Console.WriteLine("   · 副屏中心 (960,-600) / 主屏中心 (960,600) / 副屏左上内缩 (1,-1199) / 副屏右下内缩 (1918,-1)");
            Console.WriteLine($"   · 先以【未声明】(default) 组测一轮，再以 --awareness {_awarenessArg} 组测一轮做对照。");
        }
        Console.WriteLine("---------------------------------------------------------------------");
    }

    private static void WaitBeforeStart()
    {
        if (_noWait)
        {
            Console.WriteLine("[--no-wait] 跳过等待，立即开始。");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($">>> {CountdownMs / 1000} 秒后开始，Ctrl+C 可中止。请把输入法保持在 --ime {_imeArg} 指定的状态。");
        int waited = 0;
        while (waited < CountdownMs && !CancelRequested)
        {
            Thread.Sleep(100);
            waited += 100;
        }
        if (CancelRequested)
        {
            Console.WriteLine("[中止] 用户按下 Ctrl+C，未执行任何侵入式操作。");
        }
        else
        {
            Console.WriteLine(">>> 开始执行。");
        }
        Console.WriteLine();
    }

    private static void Announce(string text)
    {
        Console.WriteLine();
        Console.WriteLine("─────────────────────────────────────────────────────────────────────");
        Console.WriteLine(" 即将执行：" + text);
        Console.WriteLine("─────────────────────────────────────────────────────────────────────");
    }

    private static void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        CancelRequested = true;
        Console.WriteLine();
        Console.WriteLine("[Ctrl+C] 收到中止信号，正在收尾并清理（还原鼠标 / 关闭自建 notepad / 还原焦点与剪贴板）…");
    }

    private static void CheckCancel()
    {
        if (CancelRequested) throw new OperationCanceledException("用户按下 Ctrl+C");
    }

    // ===============================================================
    // 第一阶段：中文输入三方案实测
    // ===============================================================
    private static void Phase1_ChineseInput()
    {
        Announce($"第一阶段：启动自建目标窗口，执行三方案 × 五样本（--ime {_imeArg}）");

        CheckCancel();
        _host = SelfEditHost.Launch(_editArg);
        Console.WriteLine("   " + _host.LaunchDetail);
        Console.WriteLine($"   目标 EDIT 形态：{_host.EditMode}（IsWindowUnicode={_host.EditIsUnicode}；进程 ACP={Native.GetACP()}）");

        if (_threadKlArg is "zh" or "en")
        {
            string klid = _threadKlArg == "zh" ? "00000804" : "00000409";
            bool klOk = _host.TrySetThreadKeyboardLayout(klid, out string klDetail);
            Console.WriteLine("   [线程键盘布局实验] " + (klOk ? "已切换" : "切换失败") + "：" + klDetail);
            Run.Notes.Add("线程键盘布局实验（--thread-kl " + _threadKlArg + "）：" + klDetail);
        }

        Run.Target.Pid = (int)_host.Pid;
        Run.Target.MainWindowHwnd = Native.Hx(_host.MainWindow);
        Run.Target.MainWindowClass = Native.ClassName(_host.MainWindow);
        Run.Target.MainWindowTitle = Truncate(Native.Title(_host.MainWindow), 40);
        Run.Target.EditHwnd = Native.Hx(_host.EditWindow);
        Run.Target.EditClass = _host.EditClass;
        Run.Target.SelfStarted = _host.Available;
        Run.Target.LaunchDetail = _host.LaunchDetail;

        if (!_host.Available)
        {
            Run.Errors.Add("第一阶段跳过：自建目标窗口不可用 —— " + _host.LaunchDetail);
            Console.WriteLine("   无法获得自建 notepad 窗口，第一阶段跳过（未发送任何输入）。");
            return;
        }

        if (Native.GetWindowRect(_host.MainWindow, out var rect))
        {
            Run.Target.MainWindowRect = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom };
        }
        Run.Environment.NotepadWindowDpi = Native.GetDpiForWindow(_host.MainWindow);

        // 把窗口挪到不遮挡主屏任务区的位置（只对本程序自己的窗口调用）
        _host.MoveToVisibleSpot(200, 140, 860, 640);
        Console.WriteLine($"   已把自建 notepad 移动到 {DefaultNotepadRect}（仅对自己的窗口调用 SetWindowPos）");

        Console.WriteLine("   正在附加 UIA 探针以定位 ValuePattern 文档元素…");
        _probe = UiAutomationProbe.Attach(_host.MainWindow);
        Run.Target.UiAutomationDocumentFound = _probe.Document != null;
        Console.WriteLine("   UIA：" + _probe.Detail);
        if (_probe.Document == null)
        {
            Run.Notes.Add("UIA 未定位到 ValuePattern 元素；方案 C 将按“不可用”记录。" + _probe.Detail);
        }

        // 剪贴板备份（方案 B 会占用剪贴板）
        Clipboard.TryBackup(out string backupDetail);
        Console.WriteLine("   [剪贴板] " + backupDetail);

        int total = Samples.Count * StrategyOrder.Count;
        int index = 0;

        foreach (SampleCase sample in Samples)
        {
            foreach ((string key, string name) in StrategyOrder)
            {
                index++;
                InputCase c = RunCase(sample, key, name, index, total);
                Run.Cases.Add(c);
                PrintCaseLine(c, index, total);

                // 某方案连续不可用时不必中断，但焦点护栏失败则整轮停止（安全优先）
                if (!c.ForegroundGuardPassed && c.Status.StartsWith("FOCUS", StringComparison.Ordinal))
                {
                    string msg = $"焦点护栏失败（{c.Status}），为避免误输入，第一阶段在第 {index}/{total} 例处停止。";
                    Run.Errors.Add(msg);
                    Console.WriteLine("   !! " + msg);
                    return;
                }
            }
        }
    }

    private static InputCase RunCase(SampleCase sample, string key, string name, int index, int total)
    {
        var c = new InputCase
        {
            SampleId = sample.Id,
            SampleLabel = sample.Label,
            Strategy = key,
            StrategyName = name,
            ImeArgument = _imeArg,
            ExpectedLength = sample.Text.Length,
            ExpectedValue = sample.Text,
            ReadMethod = "WM_GETTEXT"
        };

        CheckCancel();
        Console.WriteLine($"   [{index}/{total}] 方案 {key}({name}) × 样本 {sample.Id}({sample.Label})："
                          + $"期望 {sample.Text.Length} 个 UTF-16 码元 → {Diff.Preview(sample.Text, 20)}");

        if (_host == null || !_host.Available || !_host.IsEditAlive)
        {
            c.Status = "NOTEPAD_UNAVAILABLE";
            c.Anomalies.Add("目标 notepad 不可用");
            return c;
        }

        if (!_host.EnsureFocus(out string focusDetail))
        {
            c.Status = "FOCUS_GUARD_FAILED";
            c.InjectDetail = focusDetail;
            return c;
        }

        c.ForegroundGuardPassed = true;
        c.FocusWindowClass = _host.FocusClassName();
        c.Ime = CaptureIme();

        if (!_host.TryClear(out string clearMethod, out string clearDetail))
        {
            c.Anomalies.Add("CLEAR_FAILED:" + clearDetail);
        }
        c.ClearMethod = clearMethod;
        _host.TryReadText(out string baseline, out _);
        c.BaselineEmpty = baseline.Length == 0;
        if (!c.BaselineEmpty)
        {
            c.Anomalies.Add("CLEAR_BASELINE_NOT_EMPTY:" + Diff.Preview(baseline, 12));
        }

        // 注入前最后一次护栏校验：前台窗口必须仍是自己启动的 notepad
        IntPtr fgNow = Native.GetForegroundWindow();
        if (fgNow == IntPtr.Zero || !Native.BelongsToProcess(fgNow, _host.Pid))
        {
            Native.GetWindowThreadProcessId(fgNow, out uint ownerPid);
            c.Status = "FOCUS_LOST_BEFORE_INJECT";
            c.InjectDetail = $"注入前前台窗口 {Native.Hx(fgNow)} 属于 pid={ownerPid}（期望 pid={_host.Pid}），已拒绝发送";
            return c;
        }

        // 诊断：清空 EDIT 的字符消息日志，注入后回看它实际收到了什么
        _host.ClearCharLog();

        InjectResult injected = key switch
        {
            "A" => Strategies.InjectUnicode(sample.Text),
            "B" => Strategies.InjectClipboardPaste(sample.Text, Clipboard),
            "C" => Strategies.InjectValuePattern(sample.Text, _probe ?? UiAutomationProbe.Attach(_host.MainWindow)),
            _ => throw new InvalidOperationException("未知方案 " + key)
        };

        c.InjectMs = injected.ElapsedMs;
        c.EventsRequested = injected.EventsRequested;
        c.EventsInserted = injected.EventsInserted;
        c.Win32Error = injected.Win32Error;
        c.InjectDetail = injected.Detail;

        if (!injected.Ok)
        {
            c.Status = "INJECT_FAILED";
            c.Anomalies.Add("INJECT_CALL_FAILED");
        }

        Thread.Sleep(SettleFirstWaitMs);
        (string actual, double readMs, bool settled) = ReadStabilized(_host);
        c.ActualValue = actual;
        c.ActualLength = actual.Length;
        c.ReadMs = readMs;
        c.ReadSettled = settled;
        if (!settled)
        {
            c.Anomalies.Add("READBACK_NOT_SETTLED");
        }

        if (_probe != null && _probe.Document != null)
        {
            c.UiaValue = _probe.ReadValueOrDefault("");
            c.UiaAgreesWithWin32 = string.Equals(c.UiaValue.TrimEnd('\r', '\n'), actual, StringComparison.Ordinal);
        }

        DiffResult d = Diff.Compare(sample.Text, actual);
        c.LengthEqual = d.LengthEqual;
        c.ExactMatch = d.ExactMatch;
        c.FirstMismatchIndex = d.FirstMismatchIndex;
        c.ExpectedSnippetAtMismatch = d.ExpectedSnippet;
        c.ActualSnippetAtMismatch = d.ActualSnippet;
        c.Anomalies.AddRange(d.Anomalies);
        c.Executed = true;
        c.Status = injected.Ok ? d.Verdict : "INJECT_FAILED";

        // 诊断输出：EDIT 控件实际收到的字符消息 wParam（十六进制）
        List<string> charLog = _host.SnapshotCharLog();
        string logHead = charLog.Count == 0 ? "空" : string.Join(",", charLog.Take(8));
        c.InjectDetail += $" | CHARLOG({charLog.Count}):{logHead}";
        Console.WriteLine($"        [诊断] IsWindowUnicode(EDIT)={_host.EditIsUnicode}；"
                          + $"字符消息 {charLog.Count} 条 → {logHead}");

        return c;
    }

    private static void PrintCaseLine(InputCase c, int index, int total)
    {
        string mark = c.Status == "OK" ? "✓ 一致" : "✗ " + c.Status;
        Console.WriteLine($"        → {mark}；实际 {c.ActualLength}/{c.ExpectedLength} 码元；"
                          + $"首错位置={c.FirstMismatchIndex}；注入={c.InjectMs:F1}ms；读回={c.ReadMs:F1}ms；"
                          + $"读回方式={c.ReadMethod}"
                          + (c.UiaAgreesWithWin32 == null ? "" : (c.UiaAgreesWithWin32.Value ? "；UIA=一致" : "；UIA=不一致"))
                          + (c.Anomalies.Count > 0 ? "；异常=" + string.Join(",", c.Anomalies) : ""));
    }

    private static ImeContextRecord CaptureIme()
    {
        var rec = new ImeContextRecord();
        if (_host == null || _host.MainWindow == IntPtr.Zero) return rec;

        uint tid = Native.ThreadOf(_host.MainWindow);
        IntPtr hkl = Native.GetKeyboardLayout(tid);
        int langId = (int)(hkl.ToInt64() & 0xFFFF);
        rec.NotepadThreadLangId = langId;
        rec.NotepadThreadLangName = Native.LangIdName(langId);

        var desc = new StringBuilder(128);
        int n = Native.ImmGetDescription(hkl, desc, desc.Capacity);
        rec.LayoutDescription = n > 0 ? desc.ToString() : "(ImmGetDescription 无返回)";

        IntPtr himc = Native.ImmGetContext(_host.MainWindow);
        rec.ImmContextAcquired = himc != IntPtr.Zero;
        if (himc != IntPtr.Zero)
        {
            rec.ImmOpenStatus = Native.ImmGetOpenStatus(himc);
            if (Native.ImmGetConversionStatus(himc, out uint conv, out _))
            {
                rec.ImmConversionRaw = conv;
                rec.ImmNativeMode = (conv & Native.IME_CMODE_NATIVE) != 0;
            }
            Native.ImmReleaseContext(_host.MainWindow, himc);
        }
        return rec;
    }

    /// <summary>轮询读回直到连续两次一致，避免把"尚未写入完成"误判为丢字。</summary>
    private static (string Text, double Ms, bool Settled) ReadStabilized(SelfEditHost host, int timeoutMs = ReadStabilizeTimeoutMs)
    {
        var sw = Stopwatch.StartNew();
        host.TryReadText(out string prev, out _);
        int stable = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(60);
            host.TryReadText(out string cur, out _);
            if (string.Equals(cur, prev, StringComparison.Ordinal))
            {
                stable++;
                if (stable >= 2) break;
            }
            else
            {
                prev = cur;
                stable = 0;
            }
        }
        bool settled = stable >= 2;
        return (prev, sw.Elapsed.TotalMilliseconds, settled);
    }

    // ===============================================================
    // 第二阶段：双屏负坐标可达性
    // ===============================================================
    private static void Phase2_NegativeCoordinates()
    {
        Announce("第二阶段：双屏负坐标 SetCursorPos 可达性（只移动鼠标，不点击）");

        MonitorTopology t = Run.Topology;
        Console.WriteLine($"   显示器数量={t.MonitorCount}；虚拟屏幕原点=({t.VirtualX},{t.VirtualY})，尺寸={t.VirtualWidth}x{t.VirtualHeight}；"
                          + $"原点为负={t.NegativeOrigin}");
        foreach (MonitorRecord m in t.Monitors)
        {
            Console.WriteLine($"   #{m.Index} {(m.IsPrimary ? "[主]" : "[副]")} {m.Device} rect=[{string.Join(",", m.MonitorRect)}] "
                              + $"dpi={m.EffectiveDpiX} ({m.ScalePercent}%)");
        }

        if (!t.EligibleForPhase2)
        {
            string msg = "副屏拓扑不满足前置条件（" + t.SkipReason + "），跳过第二阶段，不伪造结果。";
            Run.Notes.Add(msg);
            Console.WriteLine("   " + msg);
            return;
        }

        // 组 1：未声明 DPI 感知（进程默认）——对照组
        CheckCancel();
        Run.DefaultAwareness = DescribeAwareness("default");
        Console.WriteLine($"   对照组 [{Run.DefaultAwareness.Group}]：线程感知={Run.DefaultAwareness.ThreadLabel}，"
                          + $"GetProcessDpiAwareness={Run.DefaultAwareness.ProcessAwarenessName}"
                          + $"（虚拟屏幕 ({Run.DefaultAwareness.VirtualX},{Run.DefaultAwareness.VirtualY})"
                          + $"{Run.DefaultAwareness.VirtualWidth}x{Run.DefaultAwareness.VirtualHeight}）");
        RunCursorProbeGroup("default");

        if (_awarenessArg == "auto")
        {
            Run.Notes.Add("--awareness auto：未调用 SetProcessDpiAwarenessContext，只有未声明对照组。");
            return;
        }

        // 组 2：显式声明
        CheckCancel();
        int contextValue = _awarenessArg switch
        {
            "pmv2" => Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
            "pmv1" => Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE,
            "system" => Native.DPI_AWARENESS_CONTEXT_SYSTEM_AWARE,
            "unaware" => Native.DPI_AWARENESS_CONTEXT_UNAWARE,
            _ => Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        };

        var state = DescribeAwareness(_awarenessArg);
        state.SetApiCalled = true;
        bool ok = Native.SetProcessDpiAwarenessContext(contextValue);
        state.SetApiSucceeded = ok;
        state.SetApiWin32Error = ok ? 0 : Marshal.GetLastWin32Error();
        state.SetApiErrorText = ok ? "" : new System.ComponentModel.Win32Exception(state.SetApiWin32Error).Message;

        DescribeInto(state);
        Run.AppliedAwareness = state;

        Console.WriteLine($"   实验组 [{state.Group}]：SetProcessDpiAwarenessContext({contextValue}) → succeeded={ok}"
                          + (ok ? "" : $"，Win32Error={state.SetApiWin32Error} {state.SetApiErrorText}")
                          + $"；当前线程感知={state.ThreadLabel}；GetProcessDpiAwareness={state.ProcessAwarenessName}");
        if (!ok)
        {
            Run.Notes.Add($"SetProcessDpiAwarenessContext({contextValue}) 失败：Win32Error={state.SetApiWin32Error} {state.SetApiErrorText}"
                          + "。该组实测值实际等同进程默认感知。");
        }

        RunCursorProbeGroup(_awarenessArg);
    }

    private static readonly (string Label, int X, int Y)[] ProbePoints =
    {
        ("副屏中心（y 为负）", 960, -600),
        ("主屏中心", 960, 600),
        ("副屏左上内缩", 1, -1199),
        ("副屏右下内缩", 1918, -1)
    };

    private static void RunCursorProbeGroup(string group)
    {
        Console.WriteLine($"   —— 坐标组 [{group}]：开始 SetCursorPos → GetCursorPos 读回 ——");
        foreach ((string label, int x, int y) in ProbePoints)
        {
            CheckCancel();
            var sw = Stopwatch.StartNew();
            bool setOk = Native.SetCursorPos(x, y);
            Native.GetCursorPos(out Native.POINT actual);
            sw.Stop();

            int dx = actual.X - x;
            int dy = actual.Y - y;
            var probe = new CursorProbe
            {
                Group = group,
                Label = label,
                TargetX = x,
                TargetY = y,
                ActualX = actual.X,
                ActualY = actual.Y,
                DeltaX = dx,
                DeltaY = dy,
                ExactHit = dx == 0 && dy == 0,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                SetCursorPosSucceeded = setOk
            };

            IntPtr mon = Native.MonitorFromPoint(actual, Native.MONITOR_DEFAULTTONEAREST);
            foreach ((IntPtr handle, int idx, Native.RECT rect) in MonitorHandles)
            {
                if (handle == mon)
                {
                    probe.MonitorIndexFromPoint = idx;
                    probe.MonitorRectFromPoint = rect.ToString();
                    break;
                }
            }

            Run.CursorProbes.Add(probe);
            Console.WriteLine($"      {label}：目标=({x},{y}) → 读回=({actual.X},{actual.Y})；"
                              + $"误差=({dx},{dy})；{(probe.ExactHit ? "精确命中" : "未精确命中")}；"
                              + $"{probe.ElapsedMs:F2}ms；落在显示器 #{probe.MonitorIndexFromPoint} {probe.MonitorRectFromPoint}");
        }

        RestoreCursor();
    }

    private static void RestoreCursor()
    {
        if (!_cursor0Ok) return;
        Native.SetCursorPos(_cursor0.X, _cursor0.Y);
    }

    // ===============================================================
    // DPI 感知描述
    // ===============================================================
    private static DpiState DescribeAwareness(string group)
    {
        var state = new DpiState { Group = group };
        DescribeInto(state);
        return state;
    }

    private static void DescribeInto(DpiState state)
    {
        int hr = Native.GetProcessDpiAwareness(Native.GetCurrentProcess(), out int processAwareness);
        state.ProcessAwarenessValue = hr == 0 ? processAwareness : -999;
        state.ProcessAwarenessName = hr == 0
            ? processAwareness switch
            {
                0 => "PROCESS_DPI_UNAWARE",
                1 => "PROCESS_SYSTEM_DPI_AWARE",
                2 => "PROCESS_PER_MONITOR_DPI_AWARE",
                _ => $"UNKNOWN({processAwareness})"
            }
            : $"GetProcessDpiAwareness 失败 hr=0x{hr:X8}";

        IntPtr ctx = Native.GetThreadDpiAwarenessContext();
        state.ThreadAwarenessValue = Native.GetAwarenessFromDpiAwarenessContext(ctx);
        state.ThreadLabel = Native.AreDpiAwarenessContextsEqual(ctx, (IntPtr)Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) ? "PER_MONITOR_AWARE_V2"
            : Native.AreDpiAwarenessContextsEqual(ctx, (IntPtr)Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE) ? "PER_MONITOR_AWARE"
            : Native.AreDpiAwarenessContextsEqual(ctx, (IntPtr)Native.DPI_AWARENESS_CONTEXT_SYSTEM_AWARE) ? "SYSTEM_AWARE"
            : Native.AreDpiAwarenessContextsEqual(ctx, (IntPtr)Native.DPI_AWARENESS_CONTEXT_UNAWARE) ? "UNAWARE"
            : $"OTHER({state.ThreadAwarenessValue})";

        state.VirtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        state.VirtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        state.VirtualWidth = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        state.VirtualHeight = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        state.SystemDpi = Native.GetDpiForSystem();
        state.NotepadDpi = _host != null && _host.MainWindow != IntPtr.Zero
            ? Native.GetDpiForWindow(_host.MainWindow)
            : 0;
    }

    // ===============================================================
    // 显示器拓扑
    // ===============================================================
    private static void SnapshotTopology()
    {
        MonitorTopology t = Run.Topology;
        int index = 0;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT rc, IntPtr data) =>
        {
            var rec = new MonitorRecord
            {
                Index = index,
                Device = "",
                MonitorRect = new[] { rc.Left, rc.Top, rc.Right, rc.Bottom }
            };
            MonitorHandles.Add((hMon, index, rc));

            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(hMon, ref mi))
            {
                rec.Device = mi.szDevice ?? "";
                rec.MonitorRect = new[] { mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom };
                rec.WorkRect = new[] { mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom };
                rec.IsPrimary = (mi.dwFlags & 1) != 0;
                // 用 monitor_rect 而非 enum_rect 更新句柄缓存，保证后续映射一致
                MonitorHandles[^1] = (hMon, index, mi.rcMonitor);
            }

            int hr = Native.GetDpiForMonitor(hMon, Native.MDT_EFFECTIVE_DPI, out uint dx, out uint dy);
            if (hr == 0)
            {
                rec.EffectiveDpiX = (int)dx;
                rec.EffectiveDpiY = (int)dy;
                rec.ScalePercent = Math.Round(dx * 100.0 / 96.0, 1);
            }

            t.Monitors.Add(rec);
            index++;
            return true;
        }, IntPtr.Zero);

        t.MonitorCount = t.Monitors.Count;
        t.SmCMonitors = Native.GetSystemMetrics(Native.SM_CMONITORS);
        t.VirtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        t.VirtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        t.VirtualWidth = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        t.VirtualHeight = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        t.NegativeOrigin = t.VirtualX < 0 || t.VirtualY < 0;

        if (t.MonitorCount < 2)
        {
            t.EligibleForPhase2 = false;
            t.SkipReason = $"EnumDisplayMonitors 只返回 {t.MonitorCount} 个显示器（要求 >= 2）";
        }
        else if (!t.NegativeOrigin)
        {
            t.EligibleForPhase2 = false;
            t.SkipReason = $"虚拟屏幕原点为 ({t.VirtualX},{t.VirtualY})，没有负值坐标，无法验证负坐标可达性";
        }
        else
        {
            t.EligibleForPhase2 = true;
            t.SkipReason = "";
        }
    }

    // ===============================================================
    // 收尾清理（正常/异常路径都会走）
    // ===============================================================
    private static void Cleanup()
    {
        Console.WriteLine();
        Console.WriteLine("─────────────────────────────────────────────────────────────────────");
        Console.WriteLine(" 收尾清理");
        Console.WriteLine("─────────────────────────────────────────────────────────────────────");

        var steps = new List<string>();

        if (_host != null)
        {
            if (!string.IsNullOrEmpty(_threadKlArg))
            {
                bool klBack = _host.RestoreThreadKeyboardLayout(out string klDetail);
                Console.WriteLine("   · 还原线程键盘布局：" + (klBack ? "已还原" : "失败") + "：" + klDetail);
                steps.Add("线程键盘布局=" + (klBack ? "已还原" : "还原失败"));
            }

            string detail = _host.Close(out bool closed);
            Run.Cleanup.NotepadClosed = closed;
            Run.Cleanup.NotepadCloseMethod = detail;
            Console.WriteLine("   · 关闭自建 notepad：" + detail);
            steps.Add("notepad=" + (closed ? "已关闭" : "关闭失败"));
        }
        else
        {
            Console.WriteLine("   · 未启动过 notepad，无需关闭");
        }

        _probe?.Dispose();
        _probe = null;

        bool restored = Clipboard.TryRestore(out string clipDetail);
        Run.Cleanup.ClipboardRestored = restored;
        Run.Cleanup.ClipboardRestoreDetail = clipDetail;
        Console.WriteLine("   · " + clipDetail);

        RestoreCursor();
        Native.GetCursorPos(out Native.POINT after);
        Run.Cleanup.CursorRestored = _cursor0Ok && after.X == _cursor0.X && after.Y == _cursor0.Y;
        Run.Cleanup.CursorAfterRestore = new[] { after.X, after.Y };
        Console.WriteLine($"   · 鼠标已还原到 ({after.X},{after.Y})，目标="
                          + (Run.Environment.InitialCursor.Length == 2
                              ? $"({Run.Environment.InitialCursor[0]},{Run.Environment.InitialCursor[1]})"
                              : "(未知)"));

        bool fgOk = false;
        if (_fg0 != IntPtr.Zero)
        {
            fgOk = Native.SetForegroundWindow(_fg0);
            Thread.Sleep(100);
            fgOk = Native.GetForegroundWindow() == _fg0;
        }
        Run.Cleanup.ForegroundRestored = fgOk;
        Console.WriteLine("   · 前台焦点归还：" + (fgOk ? "成功" : "失败/可能被系统拒绝（SetForegroundWindow）")
                          + " → " + Native.Hx(_fg0) + " " + Run.Environment.InitialForegroundClass);
        if (!fgOk)
        {
            Run.Notes.Add("还原前台焦点失败：SetForegroundWindow 被系统拒绝或被其他窗口抢占；"
                          + "这是 Windows 的前台锁定策略限制，非本程序逻辑错误。");
        }

        Run.Cleanup.Detail = string.Join("；", steps);
    }

    // ===============================================================
    // 结果落盘
    // ===============================================================
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static void SaveResults()
    {
        if (string.IsNullOrEmpty(_outputPath)) return;

        try
        {
            ResultStore store;
            if (File.Exists(_outputPath))
            {
                try
                {
                    string existing = File.ReadAllText(_outputPath);
                    store = JsonSerializer.Deserialize<ResultStore>(existing) ?? new ResultStore();
                    store.Runs ??= new List<InputRun>();
                }
                catch (Exception ex)
                {
                    string fallback = Path.ChangeExtension(_outputPath, ".json")
                        .Replace(".json", "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                    File.Copy(_outputPath, fallback, overwrite: false);
                    store = new ResultStore();
                    Console.WriteLine($"   [warn] 已有结果文件解析失败（{ex.Message}），已备份为 {fallback}，本次结果写入新文件。");
                }
            }
            else
            {
                store = new ResultStore();
            }

            // 同一 RunId 不重复追加
            store.Runs.RemoveAll(r => r.RunId == Run.RunId);
            store.Runs.Add(Run);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath) ?? ".");
            string tmp = _outputPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, options));
            File.Move(tmp, _outputPath, overwrite: true);
            Console.WriteLine("   · 原始结果已写入：" + _outputPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("   [error] 结果写入失败：" + ex.Message);
        }
    }

    // ===============================================================
    // 人类可读摘要
    // ===============================================================
    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("=====================================================================");
        Console.WriteLine(" 摘要");
        Console.WriteLine("=====================================================================");

        if (Run.Cases.Count == 0)
        {
            Console.WriteLine(" 第一阶段未产生用例数据。" + (Run.Errors.Count > 0 ? " 原因：" + string.Join("；", Run.Errors) : ""));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"[第一阶段] 中文输入三方案实测（本次 --ime {_imeArg}）");
            Console.WriteLine(" 样本    长度   方案  IME参数  IME实测布局        结果      首错位置  注入ms   读回ms   读回方式     异常");
            Console.WriteLine(" ------- ------ ----  -------  ------------------  --------  --------  -------  -------  -----------  ----------------------------------");
            foreach (InputCase c in Run.Cases)
            {
                Console.WriteLine(" " + PadRight(c.SampleId, 7) + " " + PadLeft(c.ExpectedLength.ToString(), 6)
                    + "  " + PadRight(c.Strategy, 4)
                    + "  " + PadRight(c.ImeArgument, 7)
                    + "  " + PadRight(c.Ime.NotepadThreadLangName + (c.Ime.ImmContextAcquired == true ? "/ctx" : "/ctx?null"), 18)
                    + "  " + PadRight(c.Status, 8)
                    + "  " + PadLeft(c.FirstMismatchIndex.ToString(), 8)
                    + "  " + PadLeft(c.InjectMs.ToString("F1"), 7)
                    + "  " + PadLeft(c.ReadMs.ToString("F1"), 7)
                    + "  " + PadRight(c.ReadMethod, 11)
                    + "  " + (c.Anomalies.Count > 0 ? string.Join(",", c.Anomalies) : "-"));
            }

            Console.WriteLine();
            Console.WriteLine(" 按方案汇总：");
            foreach ((string key, string name) in StrategyOrder)
            {
                var rows = Run.Cases.Where(x => x.Strategy == key).ToList();
                if (rows.Count == 0)
                {
                    Console.WriteLine($"   方案 {key}（{name}）：本轮无数据");
                    continue;
                }
                int ok = rows.Count(x => x.ExactMatch);
                Console.WriteLine($"   方案 {key}（{name}）：{ok}/{rows.Count} 逐字符一致；"
                                  + $"失败样本=" + (rows.Where(x => !x.ExactMatch).Select(x => x.SampleId).DefaultIfEmpty("-").Aggregate((a, b) => a + "," + b)));
            }

            Console.WriteLine();
            Console.WriteLine(" 读回方式说明：默认 WM_GETTEXT（对自建 notepad 的编辑控件，SendMessageTimeout + SMTO_ABORTIFHUNG）；");
            Console.WriteLine("               UIA ValuePattern/LegacyIAccessible 作为交叉校验（字段 uia_agrees_with_win32）；");
            Console.WriteLine("               字段 uia_value 为 UIA 侧读到的原文（缺失表示该次未取到）。");
        }

        if (Run.Topology.MonitorCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"[第二阶段] 显示拓扑：{Run.Topology.MonitorCount} 个显示器，虚拟屏幕原点=({Run.Topology.VirtualX},{Run.Topology.VirtualY})，"
                              + $"尺寸={Run.Topology.VirtualWidth}x{Run.Topology.VirtualHeight}，原点为负={Run.Topology.NegativeOrigin}");
        }

        if (Run.CursorProbes.Count == 0)
        {
            string why = string.IsNullOrEmpty(Run.Topology.SkipReason)
                ? "未执行（可能 --phase 未包含 2）"
                : Run.Topology.SkipReason;
            Console.WriteLine();
            Console.WriteLine("[第二阶段] 无坐标探针数据：" + why);
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("[第二阶段] SetCursorPos → GetCursorPos 读回对比");
            Console.WriteLine(" 组别        目标点              目标(x,y)        读回(x,y)        误差(dx,dy)    结果      耗时ms    落在显示器");
            Console.WriteLine(" ----------  ------------------  --------------  ---------------  ------------  --------  --------  ------------------------");
            foreach (CursorProbe p in Run.CursorProbes)
            {
                Console.WriteLine(" " + PadRight(p.Group, 10)
                    + "  " + PadRight(p.Label, 18)
                    + "  " + PadRight($"({p.TargetX},{p.TargetY})", 14)
                    + "  " + PadRight($"({p.ActualX},{p.ActualY})", 15)
                    + "  " + PadRight($"({p.DeltaX},{p.DeltaY})", 12)
                    + "  " + PadRight(p.ExactHit ? "精确命中" : "未命中", 8)
                    + "  " + PadLeft(p.ElapsedMs.ToString("F2"), 8)
                    + "  #" + p.MonitorIndexFromPoint + " " + p.MonitorRectFromPoint);
            }

            Console.WriteLine();
            Console.WriteLine(" DPI 感知对照：");
            Console.WriteLine("   · 未声明组 (default)：线程感知=" + Run.DefaultAwareness.ThreadLabel
                              + "，GetProcessDpiAwareness=" + Run.DefaultAwareness.ProcessAwarenessName
                              + $"，SM_X/YVIRTUALSCREEN=({Run.DefaultAwareness.VirtualX},{Run.DefaultAwareness.VirtualY})"
                              + $"，虚拟屏幕 {Run.DefaultAwareness.VirtualWidth}x{Run.DefaultAwareness.VirtualHeight}");
            if (Run.AppliedAwareness.SetApiCalled)
            {
                Console.WriteLine("   · 声明组 (" + Run.AppliedAwareness.Group + ")：SetProcessDpiAwarenessContext → "
                                  + (Run.AppliedAwareness.SetApiSucceeded
                                      ? "成功"
                                      : $"失败（Win32Error={Run.AppliedAwareness.SetApiWin32Error} {Run.AppliedAwareness.SetApiErrorText}）")
                                  + "，线程感知=" + Run.AppliedAwareness.ThreadLabel
                                  + "，GetProcessDpiAwareness=" + Run.AppliedAwareness.ProcessAwarenessName
                                  + $"，SM_X/YVIRTUALSCREEN=({Run.AppliedAwareness.VirtualX},{Run.AppliedAwareness.VirtualY})"
                                  + $"，虚拟屏幕 {Run.AppliedAwareness.VirtualWidth}x{Run.AppliedAwareness.VirtualHeight}");
            }
        }

        if (Run.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(" 错误/中止记录：");
            foreach (string e in Run.Errors) Console.WriteLine("   · " + e);
        }

        if (Run.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(" 备注：");
            foreach (string n in Run.Notes) Console.WriteLine("   · " + n);
        }

        Console.WriteLine();
        Console.WriteLine(" 清理结果：notepad=" + (Run.Cleanup.NotepadClosed ? "已关闭" : "未/无需关闭")
                          + "；鼠标还原=" + Run.Cleanup.CursorRestored
                          + "；前台还原=" + Run.Cleanup.ForegroundRestored
                          + "；剪贴板还原=" + Run.Cleanup.ClipboardRestored
                          + "（" + Run.Cleanup.ClipboardRestoreDetail + "）");
        Console.WriteLine(" 原始结果：" + _outputPath);
    }

    private static string PadRight(string s, int width)
    {
        int pad = width - VisibleWidth(s);
        return pad > 0 ? s + new string(' ', pad) : s;
    }

    private static string PadLeft(string s, int width)
    {
        int pad = width - VisibleWidth(s);
        return pad > 0 ? new string(' ', pad) + s : s;
    }

    /// <summary>按"显示宽度"计算（CJK/全角占位 2），用于控制台表格对齐。</summary>
    private static int VisibleWidth(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int w = 0;
        foreach (char c in s)
        {
            int code = c;
            bool wide = (code >= 0x1100 && code <= 0x115F)
                        || (code >= 0x2E80 && code <= 0x303E)
                        || (code >= 0x3041 && code <= 0x33FF)
                        || (code >= 0x3400 && code <= 0x4DBF)
                        || (code >= 0x4E00 && code <= 0x9FFF)
                        || (code >= 0xA000 && code <= 0xA4CF)
                        || (code >= 0xAC00 && code <= 0xD7A3)
                        || (code >= 0xF900 && code <= 0xFAFF)
                        || (code >= 0xFE30 && code <= 0xFE6F)
                        || (code >= 0xFF00 && code <= 0xFF60)
                        || (code >= 0xFFE0 && code <= 0xFFE6);
            w += wide ? 2 : 1;
        }
        return w;
    }
}
