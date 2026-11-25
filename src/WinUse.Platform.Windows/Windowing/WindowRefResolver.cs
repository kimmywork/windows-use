using System.Runtime.InteropServices;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Windowing;

/// <summary>
/// 窗口引用解析：把参数中的 window 描述（hwnd / title / process）解析为具体 hwnd。
/// 多命中 → AMBIGUOUS；零命中 → ELEMENT_NOT_FOUND（窗口语义）。
/// </summary>
public sealed class WindowRefResolver
{
    private readonly WindowEnumerator _enumerator;

    public WindowRefResolver(WindowEnumerator enumerator)
    {
        _enumerator = enumerator;
    }

    public IntPtr Resolve(Core.Protocol.CapabilityArgs args)
    {
        // 显式 hwnd（数字或字符串）。
        if (args.GetInt64("hwnd") is { } hwndValue)
        {
            var h = new IntPtr(hwndValue);
            return NativeMethods.IsWindow(h)
                ? h
                : throw new WindowNotFoundException($"hwnd 0x{hwndValue:X} 不存在");
        }
        if (args.Raw("hwnd") is { ValueKind: global::System.Text.Json.JsonValueKind.String } he)
        {
            var text = he.GetString()!;
            if (long.TryParse(text, out var parsed))
            {
                var h = new IntPtr(parsed);
                return NativeMethods.IsWindow(h)
                    ? h
                    : throw new WindowNotFoundException($"hwnd 0x{parsed:X} 不存在");
            }
        }

        if (args.Raw("window") is { } windowNode)
        {
            return ResolveNode(windowNode);
        }

        if (args.GetString("title") is { } title)
        {
            return ResolveByTitle(title, args.GetInt32("process_id"));
        }
        if (args.GetInt32("process_id") is { } pid)
        {
            var matches = _enumerator.Enumerate(processId: pid, visibleOnly: true);
            return Unique(matches, $"process_id={pid}");
        }

        throw new WindowNotFoundException("未提供 hwnd/title/process 等窗口引用");
    }

    public IntPtr ResolveNode(global::System.Text.Json.JsonElement node)
    {
        if (node.TryGetProperty("hwnd", out var h))
        {
            var hwnd = new IntPtr(h.ValueKind == global::System.Text.Json.JsonValueKind.String
                ? long.Parse(h.GetString()!)
                : h.GetInt64());
            return NativeMethods.IsWindow(hwnd)
                ? hwnd
                : throw new WindowNotFoundException($"hwnd 0x{hwnd.ToInt64():X} 不存在");
        }
        if (node.TryGetProperty("title", out var t))
        {
            int? pid = node.TryGetProperty("process_id", out var p) ? p.GetInt32() : null;
            return ResolveByTitle(t.GetString()!, pid);
        }
        if (node.TryGetProperty("process_id", out var pidProp))
        {
            var matches = _enumerator.Enumerate(processId: pidProp.GetInt32(), visibleOnly: true);
            return Unique(matches, "process_id");
        }
        throw new WindowNotFoundException("window 引用缺少 hwnd/title/process_id");
    }

    private IntPtr ResolveByTitle(string title, int? pid)
    {
        var matches = _enumerator.Enumerate(titleContains: title, processId: pid, visibleOnly: true);
        var exact = matches.Where(w => w.Title.Equals(title, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return new IntPtr(exact[0].Hwnd);
        }
        return Unique(matches, $"title={title}");
    }

    private static IntPtr Unique(IReadOnlyList<WindowInfo> matches, string what)
    {
        if (matches.Count == 0)
        {
            throw new WindowNotFoundException($"{what} 未找到窗口");
        }
        if (matches.Count > 1)
        {
            throw new AmbiguousWindowException(matches.Count);
        }
        return new IntPtr(matches[0].Hwnd);
    }
}

public sealed class WindowNotFoundException : Exception
{
    public WindowNotFoundException(string message) : base(message)
    {
    }
}

public sealed class AmbiguousWindowException : Exception
{
    public AmbiguousWindowException(int count) : base($"{count} 个窗口命中，需缩小范围")
    {
    }
}
