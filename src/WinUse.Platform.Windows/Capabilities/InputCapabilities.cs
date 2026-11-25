using WinUse.Core.Capabilities;
using WinUse.Core.Protocol;
using WinUse.Core.Selector;
using WinUse.Platform.Windows.Input;
using WinUse.Platform.Windows.Uia;

namespace WinUse.Platform.Windows.Capabilities;

// ---- mouse.* ----

internal sealed class MouseMove : WindowsCapability
{
    public override string Id => "mouse.move";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var x = context.Args.RequireInt32("x");
        var y = context.Args.RequireInt32("y");
        context.GetRequiredService<MouseInjector>().Move(x, y);
        return Ok(new { x, y });
    }
}

internal sealed class MouseClick : WindowsCapability
{
    public override string Id => "mouse.click";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var x = context.Args.RequireInt32("x");
        var y = context.Args.RequireInt32("y");
        var button = context.Args.GetString("button") ?? "left";
        var count = context.Args.GetInt32("click_count")
            ?? (context.Args.GetBool("double") == true ? 2 : 1);
        context.GetRequiredService<MouseInjector>().Click(x, y, button, count);
        return Ok(new { x, y, button, click_count = count });
    }
}

internal sealed class MouseScroll : WindowsCapability
{
    public override string Id => "mouse.scroll";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var x = context.Args.RequireInt32("x");
        var y = context.Args.RequireInt32("y");
        var clicks = context.Args.GetInt32("clicks") ?? 3;
        var direction = context.Args.GetString("direction") ?? "up";
        var horizontal = direction is "left" or "right";
        var signed = direction is "down" or "right" ? -clicks : clicks;
        context.GetRequiredService<MouseInjector>().Wheel(x, y, signed, horizontal);
        return Ok(new { x, y, direction, clicks });
    }
}

// ---- keyboard.* ----

internal sealed class KeyboardType : WindowsCapability
{
    public override string Id => "keyboard.type";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var text = context.Args.RequireString("text");
        var selector = SelectorParser.Parse(context.Args);
        var scope = ScopeResolver.Resolve(context, selector);

        // 有目标选择器：走完整策略链（含每级读回校验）。
        if (selector is not null)
        {
            context.GetRequiredService<UiaService>()
                .ActAsync(scope, selector, "setfocus").GetAwaiter().GetResult();
            var outcome = context.GetRequiredService<TextInputStrategy>()
                .InputAsync(scope, selector, text).GetAwaiter().GetResult();
            if (!outcome.Success)
            {
                return Fail("VERIFY_FAILED",
                    $"三级输入策略均未通过读回校验，最终失败于 {outcome.Method}: {outcome.FailureReason}",
                    new { method = outcome.Method, reason = outcome.FailureReason });
            }
            return Ok(new { typed = true, method = outcome.Method });
        }

        // 无选择器：直接注入当前焦点；尝试读回，读不到通道时如实标注。
        context.GetRequiredService<KeyboardInjector>().TypeUnicode(text);
        Thread.Sleep(100);
        var actual = context.GetRequiredService<UiaService>()
            .GetFocusedTextAsync().GetAwaiter().GetResult();
        var verified = !string.IsNullOrEmpty(actual)
            ? actual.Replace("\r\n", "\n").Contains(text.Replace("\r\n", "\n"), StringComparison.Ordinal)
            : (bool?)null;
        return Ok(new
        {
            typed = true,
            method = "unicode",
            verified,
            verification = verified is null ? "unavailable" : "readback",
        });
    }
}

internal sealed class KeyboardPress : WindowsCapability
{
    public override string Id => "keyboard.press";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var key = context.Args.RequireString("key");
        context.GetRequiredService<KeyboardInjector>().Tap(key);
        return Ok(new { key });
    }
}

internal sealed class KeyboardHotkey : WindowsCapability
{
    public override string Id => "keyboard.hotkey";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        string[] modifiers;
        string mainKey;

        if (context.Args.Raw("keys") is { } keys && keys.ValueKind == global::System.Text.Json.JsonValueKind.Array)
        {
            var list = keys.EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (list.Length == 0)
            {
                throw new MissingParameterException("keys");
            }
            mainKey = list[^1];
            modifiers = list[..^1];
        }
        else
        {
            mainKey = context.Args.RequireString("key");
            modifiers = context.Args.Raw("modifiers") is { } mods &&
                        mods.ValueKind == global::System.Text.Json.JsonValueKind.Array
                ? mods.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : Array.Empty<string>();
        }

        // 保留组合键：系统刻意拦截，无自动化路径。
        var joined = string.Join("+", modifiers.Append(mainKey).Select(s => s.ToLowerInvariant()));
        if (joined is "win+l" or "ctrl+alt+del")
        {
            return Fail("RESERVED_KEY", $"组合键 {joined} 为 Windows 保留键");
        }

        context.GetRequiredService<KeyboardInjector>().Hotkey(modifiers, mainKey);
        return Ok(new { modifiers, key = mainKey });
    }
}

// ---- clipboard.* ----

internal sealed class ClipboardGet : WindowsCapability
{
    public override string Id => "clipboard.get";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var text = context.GetRequiredService<ClipboardGuard>().GetText();
        return Ok(new { text });
    }
}

internal sealed class ClipboardSet : WindowsCapability
{
    public override string Id => "clipboard.set";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var text = context.Args.RequireString("text");
        context.GetRequiredService<ClipboardGuard>().SetText(text);
        return Ok(new { set = true });
    }
}
