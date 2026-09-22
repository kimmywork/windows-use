// Program.cs — WinUse 非侵入式 PoC 探测程序
//
// 【安全声明】本程序对本机只做"读取"。绝不：
//   - 不调用 SendInput / keybd_event / mouse_event（不产生任何输入事件）
//   - 不调用 SetCursorPos（不移动鼠标指针）
//   - 不调用 SetForegroundWindow / BringWindowToTop（不抢焦点、不打断用户操作）
//   - 不启动、不关闭任何进程；不打开、不关闭任何窗口
//   - 不写注册表、不改任何系统设置、不写文件（除本程序自身输出的结果 JSON）
//
// 唯一的"写"操作是 SetProcessDpiAwarenessContext —— 它只影响本进程自身的
// DPI 感知模式，不影响系统或其他任何进程，且是本 PoC 的核心实验变量之一。
//
// 你可以照常使用电脑，本程序不会干扰你。运行约需 20~60 秒。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace WinUse.PoC;

internal static class Program
{
    private static readonly Dictionary<string, object?> Root = new();

    private static int Main(string[] args)
    {
        string outPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "poc-results.json");

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("==========================================================");
        Console.WriteLine(" WinUse 非侵入式 PoC 探测（只读，不产生任何输入/焦点变化）");
        Console.WriteLine("==========================================================");
        Console.WriteLine();

        Root["meta"] = new Dictionary<string, object?>
        {
            ["generated_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["generated_at_utc"] = DateTime.UtcNow.ToString("o"),
            ["timezone"] = TimeZoneInfo.Local.Id,
            ["safety"] = "read-only: no SendInput/SetCursorPos/SetForegroundWindow/process launch/registry write",
            ["purpose"] = "为 11-assumptions-register.md 中 A-01~A-07 / B-01 等未验证假设提供实测数据"
        };

        Run("P1 环境与权限基线", "environment", P1_Environment);
        Run("P4 坐标一致性（含 DPI Awareness 切换前后对比）", "coordinate_consistency", P4_CoordinateConsistency);
        Run("P2 显示器与 DPI 拓扑", "monitors_and_dpi", P2_MonitorsAndDpi);
        Run("P3 UIA 覆盖率与挂死防护", "uia_coverage", P3_UiaCoverage);
        Run("P5 中文 IME 状态", "ime_state", P5_ImeState);
        Run("P6 对话框与窗口类形态采样", "window_and_dialog_samples", P6_DialogSamples);
        Run("P7 完整性级别版图（UIPI 影响面）", "integrity_landscape", P7_IntegrityLandscape);
        Run("P8 UIPI 交叉验证：高 IL 进程的窗口能否被 UIA 看到", "uipi_cross_check", P8_UipiCrossCheck);

        var json = JsonSerializer.Serialize(Root, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, json, new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine(" 关键结论速览");
        Console.WriteLine("==========================================================");
        PrintHighlights();

        Console.WriteLine();
        Console.WriteLine($"结果已写入: {outPath}");
        Console.WriteLine($"JSON 大小: {json.Length:N0} 字符");
        return 0;
    }

    private static void Run(string label, string key, Func<Dictionary<string, object?>> probe)
    {
        Console.WriteLine($"[运行] {label} ...");
        var sw = Stopwatch.StartNew();
        try
        {
            var d = probe();
            d["_elapsed_ms"] = sw.ElapsedMilliseconds;
            Root[key] = d;
            Console.WriteLine($"       完成 ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Root[key] = new Dictionary<string, object?> { ["_error"] = ex.ToString() };
            Console.WriteLine($"       失败: {ex.Message}");
        }
    }

    // =====================================================================
    // 通用工具
    // =====================================================================

    /// <summary>
    /// 在独立 MTA 线程执行 UIA 调用，超时后放弃等待（不杀线程，只 detach）。
    /// 用来验证 05-architecture.md §10.3 的 UiaIsolationRunner 策略是否成立。
    /// </summary>
    private static (bool Completed, bool TimedOut, T? Result, string? Error, long Ms)
        RunIsolated<T>(Func<T> body, int timeoutMs)
    {
        T? result = default;
        string? error = null;
        bool completed = false;
        var sw = Stopwatch.StartNew();
        var t = new Thread(() =>
        {
            try
            {
                result = body();
                completed = true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        bool joined = t.Join(timeoutMs);
        sw.Stop();
        return (completed && joined, !joined, result, error, sw.ElapsedMilliseconds);
    }

    private static Dictionary<uint, string> BuildPidNameMap()
    {
        var map = new Dictionary<uint, string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                uint id = (uint)p.Id;
                string name;
                try { name = p.ProcessName; } catch { name = "<denied>"; }
                map[id] = name;
            }
            catch { /* ignore */ }
        }
        return map;
    }

    private static List<IntPtr> EnumTopLevelWindows()
    {
        var list = new List<IntPtr>();
        Native.EnumWindowsProc cb = (hWnd, lParam) =>
        {
            if (Native.IsWindowVisible(hWnd))
            {
                uint ex = Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOOLWINDOW) == 0)
                    list.Add(hWnd);
            }
            return true;
        };
        Native.EnumWindows(cb, IntPtr.Zero);
        return list;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 反射取 FlaUI 元素上的属性值：先找元素自身的同名属性，
    /// 再找 Properties.<name>.Value（FlaUI 的 IProperty&lt;T&gt; 模式）。
    /// 用于绕过 FlaUI 5 把部分属性放在非公开基类上导致的编译期不可见问题。
    /// </summary>
    private static object? ReflectVal(object o, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        var t = o.GetType();
        var p = t.GetProperty(name, F);
        if (p != null)
        {
            try { return p.GetValue(o); } catch { /* fall through */ }
        }
        var props = t.GetProperty("Properties", F);
        if (props != null)
        {
            try
            {
                var pv = props.GetValue(o);
                if (pv != null)
                {
                    var ip = pv.GetType().GetProperty(name, F);
                    if (ip != null)
                    {
                        var ipv = ip.GetValue(pv);
                        if (ipv != null)
                        {
                            var vp = ipv.GetType().GetProperty("Value", F);
                            if (vp != null) { try { return vp.GetValue(ipv); } catch { } }
                            return ipv;
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private static long HwndOf(object element)
    {
        var v = ReflectVal(element, "NativeWindowHandle");
        return v switch
        {
            IntPtr ip => ip.ToInt64(),
            int i => i,
            long l => l,
            null => 0,
            _ => Convert.ToInt64(v)
        };
    }

    // =====================================================================
    // P1 环境与权限基线
    // =====================================================================
    private static Dictionary<string, object?> P1_Environment()
    {
        var d = new Dictionary<string, object?>();
        d["os_version_string"] = Environment.OSVersion.VersionString;
        d["os_version"] = Environment.OSVersion.Version.ToString();
        d["is_64bit_os"] = Environment.Is64BitOperatingSystem;
        d["is_64bit_process"] = Environment.Is64BitProcess;
        d["cpu_count"] = Environment.ProcessorCount;
        d["runtime"] = RuntimeInformation.FrameworkDescription;
        d["process_arch"] = RuntimeInformation.ProcessArchitecture.ToString();
        d["user_domain_and_name"] = Environment.UserDomainName + "\\" + Environment.UserName;
        d["current_culture"] = CultureInfo.CurrentCulture.Name;
        d["current_ui_culture"] = CultureInfo.CurrentUICulture.Name;
        d["installed_ui_culture"] = CultureInfo.InstalledUICulture.Name;
        d["is_remote_session"] = Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0;

        try { d["session_id"] = Process.GetCurrentProcess().SessionId; }
        catch (Exception e) { d["session_id"] = "ERR " + e.Message; }

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k != null)
            {
                d["product_name"] = k.GetValue("ProductName")?.ToString();
                d["display_version"] = k.GetValue("DisplayVersion")?.ToString();
                d["current_build"] = k.GetValue("CurrentBuild")?.ToString();
                d["ubr"] = k.GetValue("UBR")?.ToString();
                d["edition_id"] = k.GetValue("EditionID")?.ToString();
                d["installation_type"] = k.GetValue("InstallationType")?.ToString();
            }
        }
        catch (Exception e) { d["registry_error"] = e.Message; }

        // 本进程权限画像（决定我们能做什么、不能做什么）
        uint selfPid = (uint)Process.GetCurrentProcess().Id;
        int? il = Native.GetIntegrityLevel(selfPid);
        d["self_pid"] = selfPid;
        d["self_integrity_level"] = il.HasValue ? Native.IlName(il.Value) : "<unknown>";
        d["self_integrity_rid"] = il.HasValue ? "0x" + il.Value.ToString("X4") : null;
        d["self_elevated_flag"] = IsElevated();
        d["self_token_elevated"] = Native.GetElevated(selfPid);
        d["self_uiaccess"] = Native.GetUiAccess(selfPid);

        // DPI 感知现状（切换前的基线）
        d["self_dpi_awareness_before"] = DescribeAwareness();

        return d;
    }

    private static Dictionary<string, object?> DescribeAwareness()
    {
        var d = new Dictionary<string, object?>();
        int hr = Native.GetProcessDpiAwareness(Process.GetCurrentProcess().Handle, out int aw);
        d["GetProcessDpiAwareness_hr"] = "0x" + hr.ToString("X8");
        d["GetProcessDpiAwareness_value"] = hr == 0 ? aw : null;
        d["GetProcessDpiAwareness_name"] = hr == 0
            ? aw switch
            {
                Native.PROCESS_DPI_UNAWARE => "PROCESS_DPI_UNAWARE",
                Native.PROCESS_SYSTEM_DPI_AWARE => "PROCESS_SYSTEM_DPI_AWARE",
                Native.PROCESS_PER_MONITOR_DPI_AWARE => "PROCESS_PER_MONITOR_DPI_AWARE",
                _ => "?"
            }
            : null;
        IntPtr ctx = Native.GetThreadDpiAwarenessContext();
        int ctxAware = Native.GetAwarenessFromDpiAwarenessContext(ctx);
        d["thread_awareness_context_value"] = ctxAware;
        d["thread_awareness_context_name"] = ctxAware switch
        {
            Native.PROCESS_DPI_UNAWARE => "UNAWARE",
            Native.PROCESS_SYSTEM_DPI_AWARE => "SYSTEM_AWARE",
            Native.PROCESS_PER_MONITOR_DPI_AWARE => "PER_MONITOR_AWARE",
            _ => "?" // v2 在 GetAwarenessFromDpiAwarenessContext 里也返回 2
        };
        bool isV2 = Native.AreDpiAwarenessContextsEqual(ctx, (IntPtr)Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        d["thread_is_per_monitor_v2"] = isV2;
        d["GetDpiForSystem"] = Native.GetDpiForSystem();
        return d;
    }

    // =====================================================================
    // P4 坐标一致性 —— 本 PoC 最重要的一项
    // =====================================================================
    private static Dictionary<string, object?> P4_CoordinateConsistency()
    {
        var d = new Dictionary<string, object?>();
        var pidName = BuildPidNameMap();

        var candidates = EnumTopLevelWindows()
            .Select(h =>
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                return new WindowSample(h, pid, Native.WindowTitle(h), Native.WindowClass(h));
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .Where(x => Native.GetWindowRect(x.H, out var r) && r.Width > 120 && r.Height > 80)
            .Take(12)
            .ToList();

        d["sample_window_count"] = candidates.Count;

        // ---------- Pass A：当前（默认）DPI 感知 ----------
        d["awareness_before"] = DescribeAwareness();
        var passA = MeasureRects(candidates, out var uiaPassA);
        d["pass_A_default_awareness"] = passA;

        // ---------- 切换到 Per-Monitor V2 ----------
        bool setOk = Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        int lastErr = Marshal.GetLastWin32Error();
        d["set_per_monitor_v2"] = new Dictionary<string, object?>
        {
            ["success"] = setOk,
            ["last_error"] = setOk ? 0 : lastErr,
            ["note"] = setOk ? null : "切换失败，通常因为进程已经建立了 DPI 感知（.NET 运行时可能已代为设置）"
        };
        d["awareness_after"] = DescribeAwareness();

        // ---------- Pass B：切换后 ----------
        var passB = MeasureRects(candidates, out var uiaPassB);
        d["pass_B_after_v2"] = passB;

        // ---------- 差异分析 ----------
        var diffs = new List<Dictionary<string, object?>>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var a = passA[i]; var b = passB[i];
            var row = new Dictionary<string, object?>
            {
                ["hwnd"] = "0x" + candidates[i].H.ToInt64().ToString("X"),
                ["title"] = Truncate(candidates[i].Title, 40),
                ["class"] = candidates[i].Class,
                ["process"] = pidName.TryGetValue(candidates[i].Pid, out var pn) ? pn : "?",                ["win32_rect_changed"] = !RectEq(a["win32_rect"], b["win32_rect"]),
                ["dwm_rect_changed"] = !RectEq(a["dwm_rect"], b["dwm_rect"]),
            };
            if (uiaPassA != null && uiaPassB != null &&
                uiaPassA.TryGetValue(candidates[i].H.ToInt64(), out var ra) &&
                uiaPassB.TryGetValue(candidates[i].H.ToInt64(), out var rb))
            {
                row["uia_rect_A"] = ra;
                row["uia_rect_B"] = rb;
                row["uia_rect_changed"] = !RectEq(ra, rb);
            }
            diffs.Add(row);
        }
        d["diff_A_vs_B"] = diffs;
        d["changed_win32_count"] = diffs.Count(x => (bool)x["win32_rect_changed"]!);
        d["changed_dwm_count"] = diffs.Count(x => (bool)x["dwm_rect_changed"]!);

        // ---------- 三方比对（在 Pass B 的坐标系下）----------
        var tri = new List<Dictionary<string, object?>>();
        if (uiaPassB != null)
        {
            foreach (var c in candidates)
            {
                if (!uiaPassB.TryGetValue(c.H.ToInt64(), out var ur)) continue;
                Native.GetWindowRect(c.H, out var wr);
                Native.DwmGetWindowAttribute(c.H, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var dr, Marshal.SizeOf<Native.RECT>());
                var uArr = (int[])ur;
                tri.Add(new Dictionary<string, object?>
                {
                    ["title"] = Truncate(c.Title, 40),
                    ["class"] = c.Class,
                    ["dpi"] = Native.GetDpiForWindow(c.H),
                    ["win32_GetWindowRect"] = new[] { wr.Left, wr.Top, wr.Right, wr.Bottom },
                    ["dwm_extended_frame"] = new[] { dr.Left, dr.Top, dr.Right, dr.Bottom },
                    ["uia_bounding"] = uArr,
                    ["delta_dwm_minus_win32"] = new[] { dr.Left - wr.Left, dr.Top - wr.Top, dr.Right - wr.Right, dr.Bottom - wr.Bottom },
                    ["delta_uia_minus_dwm"] = new[] { uArr[0] - dr.Left, uArr[1] - dr.Top, uArr[2] - dr.Right, uArr[3] - dr.Bottom },
                    ["delta_uia_minus_win32"] = new[] { uArr[0] - wr.Left, uArr[1] - wr.Top, uArr[2] - wr.Right, uArr[3] - wr.Bottom },
                });
            }
        }
        d["three_way_comparison"] = tri;

        // 鼠标位置（只读，不移动）
        if (Native.GetCursorPos(out var pt))
            d["cursor_pos_readonly"] = new[] { pt.X, pt.Y };

        return d;
    }

    private static bool RectEq(object? a, object? b)
    {
        if (a is int[] x && b is int[] y)
            return x.Length == y.Length && x.SequenceEqual(y);
        return Equals(a, b);
    }

    /// <summary>
    /// 一轮测量：Win32 GetWindowRect / DWM EXTENDED_FRAME_BOUNDS / UIA BoundingRectangle 三方取值。
    /// UIA 部分走隔离线程 + 硬超时。
    /// </summary>
    private static List<Dictionary<string, object?>> MeasureRects(
        List<WindowSample> candidates, out Dictionary<long, int[]>? uiaMap)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var c in candidates)
        {
            var row = new Dictionary<string, object?>
            {
                ["hwnd"] = "0x" + c.H.ToInt64().ToString("X"),
                ["title"] = Truncate(c.Title, 40),
                ["class"] = c.Class
            };
            if (Native.GetWindowRect(c.H, out var wr))
                row["win32_rect"] = new[] { wr.Left, wr.Top, wr.Right, wr.Bottom };
            int hr = Native.DwmGetWindowAttribute(c.H, Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var dr, Marshal.SizeOf<Native.RECT>());
            row["dwm_rect"] = hr == 0 ? new[] { dr.Left, dr.Top, dr.Right, dr.Bottom } : null;
            row["dpi_for_window"] = Native.GetDpiForWindow(c.H);
            rows.Add(row);
        }

        var iso = RunIsolated(() =>
        {
            var map = new Dictionary<long, int[]>();
            using var automation = new UIA3Automation();
            var desktop = automation.GetDesktop();
            foreach (var w in desktop.FindAllChildren())
            {
                try
                {
                    long h = HwndOf(w);
                    var br = w.BoundingRectangle;
                    map[h] = new[] { br.Left, br.Top, br.Right, br.Bottom };
                }
                catch { /* 单个元素失败不影响整体 */ }
            }
            return map;
        }, 20000);

        uiaMap = iso.Result;
        return rows;
    }

    private sealed record WindowSample(IntPtr H, uint Pid, string Title, string Class);

    // =====================================================================
    // P2 显示器与 DPI 拓扑
    // =====================================================================
    private static Dictionary<string, object?> P2_MonitorsAndDpi()
    {
        var d = new Dictionary<string, object?>();
        var mons = new List<Dictionary<string, object?>>();

        bool ok = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref Native.RECT rc, IntPtr data) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                var row = new Dictionary<string, object?>
                {
                    ["handle"] = "0x" + hMon.ToInt64().ToString("X"),
                    ["enum_rect"] = new[] { rc.Left, rc.Top, rc.Right, rc.Bottom }
                };
                if (Native.GetMonitorInfo(hMon, ref mi))
                {
                    row["device"] = mi.szDevice;
                    row["monitor_rect"] = new[] { mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom };
                    row["work_rect"] = new[] { mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom };
                    row["is_primary"] = (mi.dwFlags & 1) != 0;
                }
                int hrE = Native.GetDpiForMonitor(hMon, Native.MDT_EFFECTIVE_DPI, out uint dx, out uint dy);
                row["effective_dpi"] = hrE == 0 ? new[] { dx, dy } : null;
                row["scale_percent"] = hrE == 0 ? Math.Round(dx * 100.0 / 96.0, 1) : null;
                int hrR = Native.GetDpiForMonitor(hMon, Native.MDT_RAW_DPI, out uint rx, out uint ry);
                row["raw_dpi"] = hrR == 0 ? new[] { rx, ry } : null;
                int hrA = Native.GetDpiForMonitor(hMon, Native.MDT_ANGULAR_DPI, out uint ax, out uint ay);
                row["angular_dpi"] = hrA == 0 ? new[] { ax, ay } : null;
                mons.Add(row);
                return true;
            }, IntPtr.Zero);

        d["EnumDisplayMonitors_ok"] = ok;
        d["monitors"] = mons;
        d["monitor_count_from_api"] = mons.Count;
        d["SM_CMONITORS"] = Native.GetSystemMetrics(Native.SM_CMONITORS);

        d["virtual_screen"] = new Dictionary<string, object?>
        {
            ["x"] = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
            ["y"] = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
            ["width"] = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
            ["height"] = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN),
            ["origin_is_negative"] = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN) < 0
                                  || Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) < 0
        };
        d["GetDpiForSystem"] = Native.GetDpiForSystem();
        return d;
    }

    // =====================================================================
    // P3 UIA 覆盖率与挂死防护
    // =====================================================================
    private static readonly string[] InterestingProcesses =
    {
        "WeChat", "Weixin", "WXWork", "DingTalk", "Feishu", "Lark", "QQ", "TIM",
        "msedge", "chrome", "firefox", "ApplicationFrameHost",
        "EXCEL", "WINWORD", "POWERPNT", "wps", "et", "wpp",
        "notepad", "Code", "WindowsTerminal", "explorer", "Taskmgr", "mmc",
        "devenv", "idea64", "WeChatAppEx"
    };

    private static Dictionary<string, object?> P3_UiaCoverage()
    {
        var d = new Dictionary<string, object?>();
        var pidName = BuildPidNameMap();

        var iso = RunIsolated(() =>
        {
            var res = new List<Dictionary<string, object?>>();
            using var automation = new UIA3Automation();
            var desktop = automation.GetDesktop();
            var topLevel = desktop.FindAllChildren();
            foreach (var w in topLevel)
            {
                var row = new Dictionary<string, object?>();
                var sw = Stopwatch.StartNew();
                string? err = null;
                try { row["name"] = w.Name ?? ""; } catch (Exception e) { err ??= "Name:" + e.GetType().Name; }
                try { row["class_name"] = w.ClassName; } catch (Exception e) { err ??= "ClassName:" + e.GetType().Name; }
                try { row["control_type"] = w.ControlType.ToString(); } catch (Exception e) { err ??= "ControlType:" + e.GetType().Name; }
                try
                {
                    // FlaUI 5 把 FrameworkId / ProcessId / NativeWindowHandle 放在非公开基类上，
                    // 编译期不可见 —— 用反射取值，同时绕过 API 版本差异。
                    var fv = ReflectVal(w, "FrameworkId");
                    row["framework_id"] = fv?.ToString() ?? "";
                }
                catch (Exception e) { err ??= "FrameworkId:" + e.GetType().Name; }
                try { row["automation_id"] = w.AutomationId ?? ""; } catch (Exception e) { err ??= "AutomationId:" + e.GetType().Name; }
                try
                {
                    var pv = ReflectVal(w, "ProcessId");
                    if (pv is int pi) row["process_id"] = pi;
                    else if (pv != null && int.TryParse(pv.ToString(), out int pi2)) row["process_id"] = pi2;
                }
                catch (Exception e) { err ??= "ProcessId:" + e.GetType().Name; }
                try
                {
                    var hv = ReflectVal(w, "NativeWindowHandle");
                    long hl = hv switch
                    {
                        IntPtr ip => ip.ToInt64(),
                        int i => i,
                        long l => l,
                        _ => hv == null ? 0 : Convert.ToInt64(hv)
                    };
                    row["native_hwnd"] = "0x" + hl.ToString("X");
                    row["native_hwnd_dec"] = hl;
                }
                catch (Exception e) { err ??= "Hwnd:" + e.GetType().Name; }
                try { row["is_offscreen"] = w.IsOffscreen; } catch (Exception e) { err ??= "IsOffscreen:" + e.GetType().Name; }
                try { var br = w.BoundingRectangle; row["bounding"] = new[] { br.Left, br.Top, br.Right, br.Bottom }; }
                catch (Exception e) { err ??= "Bounding:" + e.GetType().Name; }

                int directChildren = -1;
                int depth2 = -1;
                try
                {
                    var kids = w.FindAllChildren();
                    directChildren = kids.Length;
                    int c = 0;
                    foreach (var k in kids.Take(60))
                    {
                        try { c += k.FindAllChildren().Length; } catch { }
                    }
                    depth2 = c;
                }
                catch (Exception e) { err ??= "Children:" + e.GetType().Name; }

                row["direct_children"] = directChildren;
                row["depth2_children_sampled"] = depth2;
                if (err != null) row["errors"] = err;
                sw.Stop();
                row["enum_ms"] = sw.ElapsedMilliseconds;
                res.Add(row);
            }
            return res;
        }, 25000);

        d["isolation_completed"] = iso.Completed;
        d["isolation_timed_out"] = iso.TimedOut;
        d["isolation_error"] = iso.Error;
        d["isolation_elapsed_ms"] = iso.Ms;

        var items = iso.Result ?? new List<Dictionary<string, object?>>();
        d["top_level_uia_count"] = items.Count;

        // Windows 实际可见顶层窗口数（对比用：差多少是 UIA 看不到的）
        var win32Windows = EnumTopLevelWindows();
        d["top_level_win32_visible_count"] = win32Windows.Count;
        d["coverage_gap"] = win32Windows.Count - items.Count;

        // 进程名补全
        foreach (var it in items)
        {
            if (it.TryGetValue("process_id", out var p) && p is int pid)
                it["process_name"] = pidName.TryGetValue((uint)pid, out var n) ? n : "?";
            if (it.TryGetValue("name", out var nm) && nm is string s)
                it["name"] = Truncate(s, 60);
        }

        // 耗时分布
        var times = items.Where(x => x.ContainsKey("enum_ms")).Select(x => (long)x["enum_ms"]!).OrderBy(x => x).ToList();
        if (times.Count > 0)
        {
            d["enum_ms_stats"] = new Dictionary<string, object?>
            {
                ["min"] = times.First(),
                ["p50"] = times[times.Count / 2],
                ["p95"] = times[(int)(times.Count * 0.95)],
                ["max"] = times.Last(),
                ["total"] = times.Sum()
            };
        }

        // FrameworkId 分布
        d["framework_id_histogram"] = items
            .Where(x => x.TryGetValue("framework_id", out var f) && f is string fs && fs.Length > 0)
            .GroupBy(x => (string)x["framework_id"]!)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        d["top_level_windows"] = items;

        // 重点关注应用
        var focus = new List<Dictionary<string, object?>>();
        foreach (var target in InterestingProcesses)
        {
            var matched = items.Where(x =>
                x.TryGetValue("process_name", out var pn) && pn is string s &&
                s.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
            var pids = pidName.Where(kv => kv.Value.Equals(target, StringComparison.OrdinalIgnoreCase))
                              .Select(kv => kv.Key).ToList();
            var row = new Dictionary<string, object?>
            {
                ["process"] = target,
                ["running"] = pids.Count > 0,
                ["process_count"] = pids.Count,
                ["uia_top_level_count"] = matched.Count
            };
            if (matched.Count > 0)
            {
                row["framework_ids"] = matched.Select(x => x["framework_id"]?.ToString() ?? "?").Distinct().ToArray();
                row["total_direct_children"] = matched.Sum(x => x["direct_children"] is int i ? i : 0);
                row["total_depth2_sampled"] = matched.Sum(x => x["depth2_children_sampled"] is int i ? i : 0);
                row["has_any_name"] = matched.Count(x => !string.IsNullOrEmpty(x["name"]?.ToString()));
            }
            focus.Add(row);
        }
        d["interesting_apps"] = focus;

        return d;
    }

    // =====================================================================
    // P5 中文 IME 状态
    // =====================================================================
    private static Dictionary<string, object?> P5_ImeState()
    {
        var d = new Dictionary<string, object?>();

        int n = Native.GetKeyboardLayoutList(0, Array.Empty<IntPtr>());
        d["layout_count"] = n;
        if (n > 0)
        {
            var arr = new IntPtr[n];
            int got = Native.GetKeyboardLayoutList(n, arr);
            var layouts = new List<Dictionary<string, object?>>();
            for (int i = 0; i < Math.Min(got, n); i++)
            {
                long v = arr[i].ToInt64();
                int langId = (int)(v & 0xFFFF);
                var sb = new StringBuilder(512);
                int descLen = Native.ImmGetDescription(arr[i], sb, sb.Capacity);
                layouts.Add(new Dictionary<string, object?>
                {
                    ["hkl"] = "0x" + v.ToString("X"),
                    ["lang_id"] = "0x" + langId.ToString("X4"),
                    ["lang"] = Native.LangIdName(langId),
                    ["device_handle"] = "0x" + ((v >> 16) & 0xFFFF).ToString("X4"),
                    ["description"] = descLen > 0 ? sb.ToString() : null
                });
            }
            d["layouts"] = layouts;
        }

        IntPtr cur = Native.GetKeyboardLayout(0);
        long cv = cur.ToInt64();
        d["current_thread_layout"] = new Dictionary<string, object?>
        {
            ["hkl"] = "0x" + cv.ToString("X"),
            ["lang_id"] = "0x" + ((int)(cv & 0xFFFF)).ToString("X4"),
            ["lang"] = Native.LangIdName((int)(cv & 0xFFFF))
        };

        // 注册表：预加载键盘布局
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
            if (k != null)
            {
                var vals = new Dictionary<string, object?>();
                foreach (var name in k.GetValueNames())
                    vals[name] = k.GetValue(name)?.ToString();
                d["HKCU_KeyboardLayout_Preload"] = vals;
            }
        }
        catch (Exception e) { d["preload_error"] = e.Message; }

        // 区域设置
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
            if (k != null)
            {
                d["locale_name"] = k.GetValue("LocaleName")?.ToString();
                d["s_language"] = k.GetValue("sLanguage")?.ToString();
            }
        }
        catch (Exception e) { d["locale_error"] = e.Message; }

        // 前台窗口的 IME 状态（只读）
        IntPtr fg = Native.GetForegroundWindow();
        var fgInfo = new Dictionary<string, object?>
        {
            ["hwnd"] = "0x" + fg.ToInt64().ToString("X"),
            ["title"] = Truncate(Native.WindowTitle(fg), 60),
            ["class"] = Native.WindowClass(fg)
        };
        IntPtr himc = Native.ImmGetContext(fg);
        if (himc != IntPtr.Zero)
        {
            try
            {
                fgInfo["imm_open"] = Native.ImmGetOpenStatus(himc);
                bool ok = Native.ImmGetConversionStatus(himc, out uint conv, out uint sent);
                if (ok)
                {
                    fgInfo["conversion_mode_raw"] = "0x" + conv.ToString("X4");
                    fgInfo["sentence_mode_raw"] = "0x" + sent.ToString("X4");
                    fgInfo["native_mode"] = (conv & Native.IME_CMODE_NATIVE) != 0;
                    fgInfo["full_shape"] = (conv & Native.IME_CMODE_FULLSHAPE) != 0;
                    fgInfo["symbol_mode"] = (conv & Native.IME_CMODE_SYMBOL) != 0;
                }
                else fgInfo["conversion_status_error"] = Marshal.GetLastWin32Error();
            }
            finally { Native.ImmReleaseContext(fg, himc); }
        }
        else
        {
            fgInfo["imm_error"] = "ImmGetContext 返回 NULL（该窗口无输入上下文，例如纯 UWP/自绘窗口）";
        }
        d["foreground_window_ime"] = fgInfo;

        // 抽几个有标题的窗口测 IME 上下文可得性
        var sample = EnumTopLevelWindows()
            .Where(h => !string.IsNullOrWhiteSpace(Native.WindowTitle(h)))
            .Take(8)
            .Select(h =>
            {
                var row = new Dictionary<string, object?>
                {
                    ["title"] = Truncate(Native.WindowTitle(h), 40),
                    ["class"] = Native.WindowClass(h)
                };
                IntPtr ctx = Native.ImmGetContext(h);
                if (ctx == IntPtr.Zero) { row["imm_context"] = "NULL"; return row; }
                try
                {
                    row["imm_context"] = "OK";
                    row["imm_open"] = Native.ImmGetOpenStatus(ctx);
                    if (Native.ImmGetConversionStatus(ctx, out uint c2, out _))
                        row["native_mode"] = (c2 & Native.IME_CMODE_NATIVE) != 0;
                }
                finally { Native.ImmReleaseContext(h, ctx); }
                return row;
            }).ToList();
        d["ime_context_samples"] = sample;

        return d;
    }

    // =====================================================================
    // P6 窗口类 / 对话框形态采样
    // =====================================================================
    private static Dictionary<string, object?> P6_DialogSamples()
    {
        var d = new Dictionary<string, object?>();
        var pidName = BuildPidNameMap();
        var all = EnumTopLevelWindows();

        var classHist = new Dictionary<string, int>();
        foreach (var h in all)
        {
            string c = Native.WindowClass(h);
            classHist[c] = classHist.TryGetValue(c, out int v) ? v + 1 : 1;
        }
        d["visible_top_level_count"] = all.Count;
        d["class_histogram_top20"] = classHist.OrderByDescending(kv => kv.Value)
            .Take(20).ToDictionary(kv => kv.Key, kv => kv.Value);

        var dialogs = all.Where(h => Native.WindowClass(h) == "#32770").ToList();
        d["modal_dialog_count_now"] = dialogs.Count;
        d["modal_dialog_note"] = dialogs.Count == 0
            ? "当前无 #32770 对话框打开 —— 样本不足，本项结论需后续在打开对话框时补采"
            : null;
        d["modal_dialogs"] = dialogs.Select(h =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            return new Dictionary<string, object?>
            {
                ["hwnd"] = "0x" + h.ToInt64().ToString("X"),
                ["title"] = Truncate(Native.WindowTitle(h), 60),
                ["process"] = pidName.TryGetValue(pid, out var n) ? n : "?",
                ["pid"] = pid
            };
        }).ToList();

        // 全部可见窗口清单（给后续人工核对用）
        d["visible_windows"] = all.Select(h =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            Native.GetWindowRect(h, out var r);
            int cloaked = Native.GetCloaked(h);
            return new Dictionary<string, object?>
            {
                ["hwnd"] = "0x" + h.ToInt64().ToString("X"),
                ["title"] = Truncate(Native.WindowTitle(h), 60),
                ["class"] = Native.WindowClass(h),
                ["pid"] = pid,
                ["process"] = pidName.TryGetValue(pid, out var n) ? n : "?",
                ["rect"] = new[] { r.Left, r.Top, r.Right, r.Bottom },
                ["cloaked"] = Native.CloakedName(cloaked),
                ["truly_visible"] = cloaked == 0 && r.Width > 0 && r.Height > 0
            };
        }).ToList();

        // ---- cloaked 陷阱量化：IsWindowVisible=TRUE 但其实是"幽灵窗口" ----
        int trulyVisible = 0;
        var ghosts = new List<Dictionary<string, object?>>();
        foreach (var h in all)
        {
            int cloaked = Native.GetCloaked(h);
            Native.GetWindowRect(h, out var r);
            bool real = cloaked == 0 && r.Width > 0 && r.Height > 0;
            if (real) trulyVisible++;
            else
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                ghosts.Add(new Dictionary<string, object?>
                {
                    ["hwnd"] = "0x" + h.ToInt64().ToString("X"),
                    ["title"] = Truncate(Native.WindowTitle(h), 50),
                    ["class"] = Native.WindowClass(h),
                    ["process"] = pidName.TryGetValue(pid, out var n) ? n : "?",
                    ["cloaked"] = Native.CloakedName(cloaked),
                    ["rect"] = new[] { r.Left, r.Top, r.Right, r.Bottom }
                });
            }
        }
        d["IsWindowVisible_count"] = all.Count;
        d["truly_visible_count"] = trulyVisible;
        d["ghost_window_count"] = ghosts.Count;
        d["ghost_windows"] = ghosts;
        d["cloaked_trap_note"] = ghosts.Count == 0
            ? "本轮无幽灵窗口"
            : $"⚠️ IsWindowVisible 返回 TRUE 的 {all.Count} 个窗口中，有 {ghosts.Count} 个是 cloaked/零尺寸幽灵窗口。" +
              "window.list 能力必须做 cloaked + 尺寸二次过滤，否则 Agent 会枚举到不存在的窗口。";

        // ---- UIA 幽灵元素：UIA 顶层里存在、但 Win32 侧不存在（或不可见）的元素 ----
        var uiaHwnds = new HashSet<long>();
        if (Root.TryGetValue("uia_coverage", out var ucObj) && ucObj is Dictionary<string, object?> uc)
            foreach (var it in AsList(uc.GetValueOrDefault("top_level_windows")))
                if (it.TryGetValue("native_hwnd_dec", out var v) && v is long l2) uiaHwnds.Add(l2);

        var winSet = all.Select(h => h.ToInt64()).ToHashSet();
        d["uia_top_level_count"] = uiaHwnds.Count;
        d["uia_only_hwnd_count"] = uiaHwnds.Count(h => !winSet.Contains(h));
        d["uia_only_hwnds"] = uiaHwnds.Where(h => !winSet.Contains(h))
            .Select(h => "0x" + h.ToString("X")).ToList();
        d["uia_only_note"] = d["uia_only_hwnd_count"] is int ghostCnt && ghostCnt > 0
            ? "UIA 顶层列表包含系统窗口（任务栏/Progman）与隐藏窗口（Tauri/Chromium 的事件窗口），" +
              "不能直接当作‘用户可见窗口列表’使用，需二次过滤。"
            : null;

        return d;
    }

    // =====================================================================
    // P7 完整性级别版图（UIPI 影响面）
    // =====================================================================
    private static Dictionary<string, object?> P7_IntegrityLandscape()
    {
        var d = new Dictionary<string, object?>();
        int total = 0, denied = 0;
        var hist = new Dictionary<string, int>();
        var high = new List<Dictionary<string, object?>>();

        foreach (var p in Process.GetProcesses())
        {
            total++;
            uint pid;
            string name;
            try { pid = (uint)p.Id; } catch { continue; }
            try { name = p.ProcessName; } catch { name = "<denied>"; }

            int? il = Native.GetIntegrityLevel(pid);
            if (il == null) { denied++; continue; }
            string ilName = Native.IlName(il.Value);
            hist[ilName] = hist.TryGetValue(ilName, out int v) ? v + 1 : 1;

            if (il.Value >= Native.SECURITY_MANDATORY_HIGH_RID)
            {
                high.Add(new Dictionary<string, object?>
                {
                    ["pid"] = pid,
                    ["name"] = name,
                    ["il"] = ilName,
                    ["elevated"] = Native.GetElevated(pid),
                    ["uiaccess"] = Native.GetUiAccess(pid)
                });
            }
        }

        d["process_total"] = total;
        d["process_il_query_denied"] = denied;
        d["integrity_histogram"] = hist.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        d["high_or_above_count"] = high.Count;
        d["high_or_above_processes"] = high.OrderBy(x => x["name"]?.ToString()).ToList();

        // UIPI 影响面推断
        d["uipi_implication"] = new Dictionary<string, object?>
        {
            ["note"] = "本进程为 Medium IL 时，SendInput 无法投递到 High/System IL 进程的窗口（静默失败）；" +
                       "且读取 High IL 进程的 UIA 树通常被 UIPI 阻断（A-01 待验证项）",
            ["high_il_process_count"] = high.Count,
            ["self_il"] = Native.IlName(Native.GetIntegrityLevel((uint)Process.GetCurrentProcess().Id) ?? -1)
        };

        return d;
    }

    // =====================================================================
    // P8 UIPI 交叉验证 —— 直接验证假设 A-01
    // =====================================================================
    private static Dictionary<string, object?> P8_UipiCrossCheck()
    {
        var d = new Dictionary<string, object?>();
        var pidName = BuildPidNameMap();

        uint selfPid = (uint)Process.GetCurrentProcess().Id;
        int? selfIl = Native.GetIntegrityLevel(selfPid);
        d["self_il"] = selfIl.HasValue ? Native.IlName(selfIl.Value) : "?";
        d["self_il_rid"] = selfIl.HasValue ? "0x" + selfIl.Value.ToString("X4") : null;
        d["self_elevated"] = IsElevated();
        d["conclusion_applicable"] = selfIl.HasValue && selfIl.Value < Native.SECURITY_MANDATORY_HIGH_RID;
        d["applicability_note"] = (bool)d["conclusion_applicable"]!
            ? "本进程为 Medium（或更低）IL —— 结论可直接外推到‘默认不提权’的产品形态（ADR-002）"
            : "⚠️ 本进程 IL >= High —— 观察到的可见性不代表默认形态，需以非提权方式重跑";

        // UIA 能看到的顶层窗口 hwnd 集合（来自 P3）
        var uiaHwnds = new HashSet<long>();
        if (Root.TryGetValue("uia_coverage", out var ucObj) && ucObj is Dictionary<string, object?> uc)
        {
            foreach (var it in AsList(uc.GetValueOrDefault("top_level_windows")))
                if (it.TryGetValue("native_hwnd_dec", out var v) && v is long l)
                    uiaHwnds.Add(l);
        }
        d["uia_visible_hwnd_count"] = uiaHwnds.Count;

        // 按进程聚合可见顶层窗口
        var byPid = new Dictionary<uint, List<long>>();
        foreach (var h in EnumTopLevelWindows())
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!byPid.TryGetValue(pid, out var lst)) byPid[pid] = lst = new List<long>();
            lst.Add(h.ToInt64());
        }

        var rows = new List<Dictionary<string, object?>>();
        int highWinTotal = 0, highWinVisible = 0;
        int mediumWinTotal = 0, mediumWinVisible = 0;

        foreach (var kv in byPid)
        {
            uint pid = kv.Key;
            int? il = Native.GetIntegrityLevel(pid);
            if (il == null) continue;
            string ilName = Native.IlName(il.Value);
            int vis = kv.Value.Count(h => uiaHwnds.Contains(h));
            bool high = il.Value >= Native.SECURITY_MANDATORY_HIGH_RID;
            if (high) { highWinTotal += kv.Value.Count; highWinVisible += vis; }
            else { mediumWinTotal += kv.Value.Count; mediumWinVisible += vis; }

            rows.Add(new Dictionary<string, object?>
            {
                ["pid"] = pid,
                ["process"] = pidName.TryGetValue(pid, out var n) ? n : "?",
                ["il"] = ilName,
                ["is_high_or_above"] = high,
                ["visible_window_count"] = kv.Value.Count,
                ["uia_visible_count"] = vis,
                ["uia_fully_visible"] = vis == kv.Value.Count,
                ["uia_partially_visible"] = vis > 0 && vis < kv.Value.Count,
                ["windows"] = kv.Value.Select(h => new Dictionary<string, object?>
                {
                    ["hwnd"] = "0x" + h.ToString("X"),
                    ["title"] = Truncate(Native.WindowTitle((IntPtr)h), 50),
                    ["class"] = Native.WindowClass((IntPtr)h),
                    ["uia_visible"] = uiaHwnds.Contains(h)
                }).ToList()
            });
        }

        d["per_process"] = rows.OrderByDescending(x => (bool)x["is_high_or_above"]!)
                               .ThenBy(x => x["process"]?.ToString()).ToList();
        d["high_il_windows_total"] = highWinTotal;
        d["high_il_windows_visible_in_uia"] = highWinVisible;
        d["medium_il_windows_total"] = mediumWinTotal;
        d["medium_il_windows_visible_in_uia"] = mediumWinVisible;

        if (highWinTotal == 0)
        {
            d["A01_verdict"] = "INCONCLUSIVE_NO_SAMPLE";
            d["A01_note"] = "当前没有 High IL 及以上进程的可见窗口 —— 无法在本轮判定 A-01。" +
                            "需在有管理员程序（如任务管理器‘显示所有用户进程’、mmc、已提权的安装程序）打开时重跑。";
        }
        else
        {
            d["A01_verdict"] = highWinVisible == 0 ? "BLOCKED" : (highWinVisible == highWinTotal ? "VISIBLE" : "PARTIAL");
            d["A01_note"] = highWinVisible == 0
                ? "高 IL 进程的窗口在 UIA 中完全不可见 —— 证实 UIPI 阻断，v1 必须按‘看不到即报 UIPI_BLOCKED’设计（PRD P0-6 已据此改写）"
                : "高 IL 进程的窗口在 UIA 中可见 —— 与‘UIPI 阻断读取’的假设不符，需复核 04 §9 矩阵 #60 的判定";
        }

        return d;
    }

    // =====================================================================
    // 汇总输出
    // =====================================================================
    private static void PrintHighlights()
    {
        void Sec(string t) { Console.WriteLine(); Console.WriteLine("### " + t); }

        Sec("1) 坐标系");
        if (Root.TryGetValue("coordinate_consistency", out var ccObj) && ccObj is Dictionary<string, object?> cc)
        {
            Console.WriteLine($"   采样窗口数: {cc.GetValueOrDefault("sample_window_count")}");
            Console.WriteLine($"   PMv2 切换: {Dig(cc, "set_per_monitor_v2", "success")} (lastError={Dig(cc, "set_per_monitor_v2", "last_error")})");
            Console.WriteLine($"   GetWindowRect 值发生变化的窗口数: {cc.GetValueOrDefault("changed_win32_count")}");
            Console.WriteLine($"   DWM extended 值发生变化的窗口数: {cc.GetValueOrDefault("changed_dwm_count")}");
            if (cc.TryGetValue("three_way_comparison", out var tw) && tw is List<Dictionary<string, object?>> twl)
            {
                Console.WriteLine("   Win32 / DWM / UIA 三方差值（左上右下，单位 px）:");
                foreach (var r in twl.Take(8))
                    Console.WriteLine($"     - {Pad(r["title"]?.ToString(), 26)} dpi={r["dpi"]} " +
                                      $"DWM-Win32={Fmt(r["delta_dwm_minus_win32"])} UIA-DWM={Fmt(r["delta_uia_minus_dwm"])}");
            }
        }

        Sec("2) UIA 覆盖率 / 挂死");
        if (Root.TryGetValue("uia_coverage", out var ucObj) && ucObj is Dictionary<string, object?> uc)
        {
            Console.WriteLine($"   隔离线程完成: {uc.GetValueOrDefault("isolation_completed")}, 超时: {uc.GetValueOrDefault("isolation_timed_out")}, 耗时 {uc.GetValueOrDefault("isolation_elapsed_ms")} ms");
            Console.WriteLine($"   UIA 顶层元素: {uc.GetValueOrDefault("top_level_uia_count")} / Win32 可见顶层窗口: {uc.GetValueOrDefault("top_level_win32_visible_count")} (缺口 {uc.GetValueOrDefault("coverage_gap")})");
            Console.WriteLine($"   FrameworkId 分布: {FmtDict(uc.GetValueOrDefault("framework_id_histogram"))}");
            foreach (var app in AsList(uc.GetValueOrDefault("interesting_apps")).Where(x => (bool)(x["running"] ?? false)))
                Console.WriteLine($"     - {Pad(app["process"]?.ToString(), 20)} UIA顶层={app.GetValueOrDefault("uia_top_level_count")} " +
                                  $"直系子元素={app.GetValueOrDefault("total_direct_children")} FrameworkId={FmtArr(app.GetValueOrDefault("framework_ids"))}");
        }

        Sec("3) 中文 IME");
        if (Root.TryGetValue("ime_state", out var imObj) && imObj is Dictionary<string, object?> im)
        {
            Console.WriteLine($"   已安装键盘布局数: {im.GetValueOrDefault("layout_count")}");
            foreach (var l in AsList(im.GetValueOrDefault("layouts")))
                Console.WriteLine($"     - {l["lang"]} ({l["lang_id"]}) {l["description"]}");
            Console.WriteLine($"   当前线程布局: {Dig(im, "current_thread_layout", "lang")}");
            if (im.TryGetValue("foreground_window_ime", out var fgo) && fgo is Dictionary<string, object?> fg)
                Console.WriteLine($"   前台窗口 IME: open={fg.GetValueOrDefault("imm_open")} native={fg.GetValueOrDefault("native_mode")} fullshape={fg.GetValueOrDefault("full_shape")} [{fg.GetValueOrDefault("title")}]");
        }

        Sec("4) 完整性级别（UIPI 影响面）");
        if (Root.TryGetValue("integrity_landscape", out var ilObj) && ilObj is Dictionary<string, object?> ilv)
        {
            Console.WriteLine($"   进程总数 {ilv.GetValueOrDefault("process_total")}，查询被拒 {ilv.GetValueOrDefault("process_il_query_denied")}");
            Console.WriteLine($"   完整性分布: {FmtDict(ilv.GetValueOrDefault("integrity_histogram"))}");
            Console.WriteLine($"   High 及以上进程数: {ilv.GetValueOrDefault("high_or_above_count")}");
        }

        Sec("5) 显示器 / VSC");
        if (Root.TryGetValue("monitors_and_dpi", out var moObj) && moObj is Dictionary<string, object?> mo)
        {
            foreach (var m in AsList(mo.GetValueOrDefault("monitors")))
                Console.WriteLine($"     - {m.GetValueOrDefault("device")} rect={FmtArr(m.GetValueOrDefault("monitor_rect"))} dpi={FmtArr(m.GetValueOrDefault("effective_dpi"))} scale={m.GetValueOrDefault("scale_percent")}% primary={m.GetValueOrDefault("is_primary")}");
            Console.WriteLine($"   虚拟屏幕: {FmtDict(mo.GetValueOrDefault("virtual_screen"))}");
        }

        Sec("7) 幽灵窗口陷阱（IsWindowVisible 不可信）");
        if (Root.TryGetValue("window_and_dialog_samples", out var wObj) && wObj is Dictionary<string, object?> w6)
        {
            Console.WriteLine($"   IsWindowVisible=TRUE: {w6.GetValueOrDefault("IsWindowVisible_count")} 个，其中真正可见: {w6.GetValueOrDefault("truly_visible_count")} 个，幽灵: {w6.GetValueOrDefault("ghost_window_count")} 个");
            foreach (var g in AsList(w6.GetValueOrDefault("ghost_windows")).Take(8))
                Console.WriteLine($"     - {Pad(g.GetValueOrDefault("process")?.ToString(), 20)} {Pad(g.GetValueOrDefault("class")?.ToString(), 30)} {g.GetValueOrDefault("cloaked")} | {g.GetValueOrDefault("title")}");
            Console.WriteLine($"   UIA 顶层元素里有 {w6.GetValueOrDefault("uia_only_hwnd_count")} 个在 Win32 可见窗口列表中不存在");
        }

        Sec("6) UIPI 交叉验证（假设 A-01）");
        if (Root.TryGetValue("uipi_cross_check", out var uo) && uo is Dictionary<string, object?> u8)
        {
            Console.WriteLine($"   本进程 IL: {u8.GetValueOrDefault("self_il")} (elevated={u8.GetValueOrDefault("self_elevated")})");
            Console.WriteLine($"   高 IL 窗口: {u8.GetValueOrDefault("high_il_windows_visible_in_uia")}/{u8.GetValueOrDefault("high_il_windows_total")} 在 UIA 中可见");
            Console.WriteLine($"   中 IL 窗口: {u8.GetValueOrDefault("medium_il_windows_visible_in_uia")}/{u8.GetValueOrDefault("medium_il_windows_total")} 在 UIA 中可见");
            Console.WriteLine($"   判定: {u8.GetValueOrDefault("A01_verdict")}");
            Console.WriteLine($"   {u8.GetValueOrDefault("A01_note")}");
        }
    }

    // ---- 小工具 ----
    private static string Truncate(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
    private static string Pad(string? s, int n) => (s ?? "").PadRight(n).Substring(0, Math.Min(n, (s ?? "").PadRight(n).Length));
    private static string Fmt(object? o) => o is int[] a ? "[" + string.Join(",", a) + "]" : (o?.ToString() ?? "-");
    private static string FmtArr(object? o) => o is System.Collections.IEnumerable e && o is not string
        ? string.Join(",", e.Cast<object>().Select(x => x?.ToString()))
        : (o?.ToString() ?? "-");
    private static string FmtDict(object? o) => o switch
    {
        Dictionary<string, object?> d1 => string.Join(", ", d1.Select(kv => $"{kv.Key}={kv.Value}")),
        Dictionary<string, int> d2 => string.Join(", ", d2.Select(kv => $"{kv.Key}={kv.Value}")),
        _ => o?.ToString() ?? "-"
    };
    private static object? Dig(Dictionary<string, object?> d, string k1, string k2)
        => d.TryGetValue(k1, out var a) && a is Dictionary<string, object?> b ? b.GetValueOrDefault(k2) : null;
    private static List<Dictionary<string, object?>> AsList(object? o)
        => o as List<Dictionary<string, object?>> ?? new List<Dictionary<string, object?>>();
}
