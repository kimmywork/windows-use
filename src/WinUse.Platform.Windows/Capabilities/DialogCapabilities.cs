using WinUse.Core.Capabilities;
using WinUse.Core.Selector;
using WinUse.Platform.Windows.Dialogs;
using WinUse.Platform.Windows.Uia;
using WinUse.Platform.Windows.Windowing;

namespace WinUse.Platform.Windows.Capabilities;

// ---- dialog.* ----

internal sealed class DialogDetect : WindowsCapability
{
    public override string Id => "dialog.detect";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var dialogs = context.GetRequiredService<DialogService>()
            .DetectAsync().GetAwaiter().GetResult();
        return Ok(new { count = dialogs.Count, dialogs });
    }
}

internal sealed class DialogButtonClick : WindowsCapability
{
    public override string Id => "dialog.button.click";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var button = context.Args.RequireString("button");
        long? hwnd = context.Args.GetInt64("hwnd");
        var ok = context.GetRequiredService<DialogService>()
            .ClickButtonAsync(hwnd, button).GetAwaiter().GetResult();
        if (!ok)
        {
            return Fail("ELEMENT_NOT_FOUND", "未找到对话框或目标按钮");
        }
        return Ok(new { clicked = true, button });
    }
}

// ---- filedialog.* ----

internal sealed class FileDialogSetPath : WindowsCapability
{
    public override string Id => "filedialog.set_path";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var path = context.Args.RequireString("path");
        var hwnd = FileDialogHwnd.Resolve(context);
        var uia = context.GetRequiredService<UiaService>();

        // 文件名输入框：经典对话框为 Edit/ComboBox，名称随 UI 语言；用别名匹配。
        var selector = new Selector
        {
            ControlType = "combobox",
            Name = "文件名(N):",
            NameMatch = "prefix",
            NameAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en1"] = "File name:",
                ["en2"] = "Filename:",
            },
            VisibleOnly = true,
        };

        // 先试 ComboBox（现代/经典通用），再退化到 Edit。
        try
        {
            uia.SetValueAsync(hwnd, selector, path).GetAwaiter().GetResult();
        }
        catch (ElementNotFoundException)
        {
            var editSelector = selector with { ControlType = "edit" };
            uia.SetValueAsync(hwnd, editSelector, path).GetAwaiter().GetResult();
        }
        return Ok(new { path_set = true, path });
    }
}

internal sealed class FileDialogConfirm : WindowsCapability
{
    public override string Id => "filedialog.confirm";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var hwnd = FileDialogHwnd.Resolve(context);
        var uia = context.GetRequiredService<UiaService>();

        // 主按钮随对话框类型为"打开(O)"/"保存(S)"，英文 Open/Save。
        var selector = new Selector
        {
            ControlType = "button",
            Name = context.Args.GetString("button") ?? "打开(O):",
            NameMatch = "prefix",
            NameAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["save"] = "保存(S):",
                ["open"] = "Open",
                ["save_en"] = "Save",
            },
            VisibleOnly = true,
        };
        uia.ActAsync(hwnd, selector, "invoke").GetAwaiter().GetResult();
        return Ok(new { confirmed = true });
    }
}

internal static class FileDialogHwnd
{
    public static IntPtr Resolve(CapabilityContext context)
    {
        if (context.Args.GetInt64("hwnd") is { } h)
        {
            return new IntPtr(h);
        }
        var dialogs = context.GetRequiredService<DialogService>()
            .DetectAsync().GetAwaiter().GetResult();
        if (dialogs.Count == 0)
        {
            throw new WindowNotFoundException("未检测到文件对话框");
        }
        return new IntPtr(dialogs[0].Hwnd);
    }
}
