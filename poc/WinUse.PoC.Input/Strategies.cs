// Strategies.cs — 中文输入三种方案的注入实现。
//
//   A: KEYEVENTF_UNICODE（docs/06 §6 策略链第 ② 级，本次核心被测对象）
//   B: 剪贴板(CF_UNICODE) + Ctrl+V（第 ③ 级）
//   C: UIA ValuePattern.SetValue（第 ① 级）
//
// 所有注入方法都不自行决定目标：调用方必须先用 NotepadHost.EnsureFocus 通过
// "前台进程 == 自建 notepad 且焦点在编辑控件" 的校验，才允许调用本类。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinUse.PoC.Input;

internal sealed class InjectResult
{
    public bool Ok { get; set; }
    public double ElapsedMs { get; set; }
    public uint EventsRequested { get; set; }
    public uint EventsInserted { get; set; }
    public int Win32Error { get; set; }
    public string Detail { get; set; } = "";
}

internal static class Strategies
{
    /// <summary>
    /// 方案 A：逐 UTF-16 码元构造 KEYEVENTF_UNICODE 事件。
    /// 代理对（如 U+20BB7）天然就是两个码元，逐个发送即可，不做额外合并。
    /// </summary>
    public static InjectResult InjectUnicode(string text)
    {
        var result = new InjectResult();
        if (string.IsNullOrEmpty(text))
        {
            result.Ok = true;
            result.Detail = "空串无需注入";
            return result;
        }

        var events = new List<Native.INPUT>(text.Length * 2);
        foreach (char codeUnit in text)
        {
            events.Add(Native.UnicodeKey(codeUnit, keyUp: false));
            events.Add(Native.UnicodeKey(codeUnit, keyUp: true));
        }

        var array = events.ToArray();
        result.EventsRequested = (uint)array.Length;

        var sw = Stopwatch.StartNew();
        uint inserted = Native.SendInput((uint)array.Length, array, Marshal.SizeOf<Native.INPUT>());
        sw.Stop();

        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        result.EventsInserted = inserted;
        result.Win32Error = inserted != result.EventsRequested ? Marshal.GetLastWin32Error() : 0;
        result.Ok = inserted == result.EventsRequested;
        result.Detail = result.Ok
            ? $"SendInput 插入 {inserted}/{result.EventsRequested} 个事件"
            : $"SendInput 只插入 {inserted}/{result.EventsRequested} 个事件，Win32Error={result.Win32Error}";
        return result;
    }

    /// <summary>
    /// 方案 B：剪贴板写入 Unicode 文本后发送 Ctrl+V。
    /// 先 Ctrl+A 再 Ctrl+V，确保粘贴替换整个文档（内容可控、便于比对）。
    /// </summary>
    public static InjectResult InjectClipboardPaste(string text, ClipboardGuard clipboard)
    {
        var result = new InjectResult();

        if (!clipboard.SetUnicodeText(text, out string setDetail))
        {
            result.Detail = "剪贴板写入失败：" + setDetail;
            return result;
        }

        var events = new List<Native.INPUT>
        {
            // Ctrl+A（全选）
            Native.VirtualKey(Native.VK_CONTROL, keyUp: false),
            Native.VirtualKey(Native.VK_A, keyUp: false),
            Native.VirtualKey(Native.VK_A, keyUp: true),
            Native.VirtualKey(Native.VK_CONTROL, keyUp: true),
            // Ctrl+V（粘贴）
            Native.VirtualKey(Native.VK_CONTROL, keyUp: false),
            Native.VirtualKey(Native.VK_V, keyUp: false),
            Native.VirtualKey(Native.VK_V, keyUp: true),
            Native.VirtualKey(Native.VK_CONTROL, keyUp: true)
        };

        var array = events.ToArray();
        result.EventsRequested = (uint)array.Length;

        var sw = Stopwatch.StartNew();
        uint inserted = Native.SendInput((uint)array.Length, array, Marshal.SizeOf<Native.INPUT>());
        sw.Stop();

        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        result.EventsInserted = inserted;
        result.Win32Error = inserted != result.EventsRequested ? Marshal.GetLastWin32Error() : 0;
        result.Ok = inserted == result.EventsRequested;
        result.Detail = (result.Ok ? "剪贴板粘贴完成" : "SendInput 未全部插入")
                        + "（写入：" + setDetail + "）";
        return result;
    }

    /// <summary>方案 C：UIA ValuePattern.SetValue。</summary>
    public static InjectResult InjectValuePattern(string text, UiAutomationProbe probe)
    {
        var result = new InjectResult();
        var sw = Stopwatch.StartNew();
        bool ok = probe.TrySetValue(text, out string detail);
        sw.Stop();

        result.Ok = ok;
        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        result.EventsRequested = 0;
        result.EventsInserted = 0;
        result.Detail = detail;
        return result;
    }
}
