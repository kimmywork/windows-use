using global::System.Diagnostics;
using WinUse.Core.Capabilities;
using WinUse.Core.Selector;
using WinUse.Platform.Windows.Windowing;

namespace WinUse.Platform.Windows.Capabilities;

/// <summary>
/// 元素作用域解析：selector.within → 顶层 window/hwnd/title → 桌面（IntPtr.Zero）。
/// </summary>
internal static class ScopeResolver
{
    public static IntPtr Resolve(CapabilityContext context, Selector? selector)
    {
        if (selector?.Within is { } within)
        {
            if (within.Hwnd is { } h)
            {
                var hwnd = new IntPtr(h);
                return Native.NativeMethods.IsWindow(hwnd)
                    ? hwnd
                    : throw new WindowNotFoundException($"within.hwnd 0x{h:X} 不存在");
            }
            if (within.ProcessName is { Length: > 0 } processName)
            {
                var enumerator = context.GetRequiredService<WindowEnumerator>();
                var matches = enumerator.Enumerate(visibleOnly: true)
                    .Where(w =>
                    {
                        var pid = WindowEnumerator.ReadPid(new IntPtr(w.Hwnd));
                        try
                        {
                            return Process.GetProcessById(pid).ProcessName
                                .Equals(processName, StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();
                if (matches.Count == 0)
                {
                    throw new WindowNotFoundException($"within.processName={processName} 未找到窗口");
                }
                if (matches.Count > 1)
                {
                    throw new AmbiguousWindowException(matches.Count);
                }
                return new IntPtr(matches[0].Hwnd);
            }
        }

        var args = context.Args;
        if (args.Has("hwnd") || args.Has("window") || args.Has("title") || args.Has("process_id"))
        {
            return context.GetRequiredService<WindowRefResolver>().Resolve(args);
        }

        return IntPtr.Zero; // 整个桌面
    }
}
