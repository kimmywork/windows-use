using WinUse.Core.Capabilities;
using WinUse.Platform.Windows.System;
using WinUse.Platform.Windows.Windowing;

namespace WinUse.Platform.Windows.Capabilities;

// ---- process.* ----

internal sealed class ProcessStart : WindowsCapability
{
    public override string Id => "process.start";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var path = context.Args.RequireString("path");
        var service = context.GetRequiredService<ProcessService>();
        var elevated = context.Args.GetBool("elevated") ?? false;

        if (elevated)
        {
            // runas 触发 UAC：前置层显式上报 NEEDS_HUMAN(UAC_OPEN)。
            return Fail("NEEDS_HUMAN", "启动提权进程需要用户在 UAC 安全桌面上确认",
                new { reason = "UAC_OPEN" });
        }

        var pid = service.Start(path,
            context.Args.GetString("arguments"),
            context.Args.GetString("working_directory"),
            elevated);
        return Ok(new { pid });
    }
}

internal sealed class ProcessList : WindowsCapability
{
    public override string Id => "process.list";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var list = context.GetRequiredService<ProcessService>()
            .List(context.Args.GetString("name"));
        return Ok(new
        {
            count = list.Count,
            processes = list,
        });
    }
}

// ---- window.* ----

internal sealed class WindowList : WindowsCapability
{
    public override string Id => "window.list";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var list = context.GetRequiredService<WindowEnumerator>().Enumerate(
            context.Args.GetString("title"),
            context.Args.GetInt32("process_id"),
            context.Args.GetBool("visible_only") ?? true);
        return Ok(new
        {
            count = list.Count,
            windows = list,
        });
    }
}

internal sealed class WindowFind : WindowsCapability
{
    public override string Id => "window.find";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var enumerator = context.GetRequiredService<WindowEnumerator>();
        var list = enumerator.Enumerate(
            context.Args.GetString("title"),
            context.Args.GetInt32("process_id"),
            visibleOnly: true);

        if (list.Count == 0)
        {
            return Fail("ELEMENT_NOT_FOUND", "未找到匹配窗口");
        }
        if (list.Count > 1)
        {
            return Fail("AMBIGUOUS", data: new { count = list.Count });
        }
        return Ok(list[0]);
    }
}

internal sealed class WindowFocus : WindowsCapability
{
    public override string Id => "window.focus";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var resolver = context.GetRequiredService<WindowRefResolver>();
        var hwnd = resolver.Resolve(context.Args);
        var ok = context.GetRequiredService<ForegroundService>().Focus(hwnd);
        if (!ok)
        {
            return Fail("FOCUS_LOST", "窗口已还原/置前但未成为前台窗口（可能被前台锁定策略拦截）");
        }
        return Ok(new
        {
            hwnd = hwnd.ToInt64(),
            foreground = true,
        });
    }
}

internal sealed class WindowClose : WindowsCapability
{
    public override string Id => "window.close";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var resolver = context.GetRequiredService<WindowRefResolver>();
        var hwnd = resolver.Resolve(context.Args);
        var ok = context.GetRequiredService<ForegroundService>().Close(hwnd);
        return Ok(new
        {
            hwnd = hwnd.ToInt64(),
            close_message_sent = ok,
        });
    }
}
