using WinUse.Core.Capabilities;
using WinUse.Core.Protocol;
using WinUse.Core.Selector;
using WinUse.Platform.Windows.Uia;

namespace WinUse.Platform.Windows.Capabilities;

internal sealed class ElementFind : WindowsCapability
{
    public override string Id => "element.find";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var selector = SelectorParser.Parse(context.Args)
                       ?? throw new MissingParameterException("selector");
        var scope = ScopeResolver.Resolve(context, selector);
        var outcome = context.GetRequiredService<UiaService>()
            .FindAsync(scope, selector,
                context.Args.GetInt32("max_results") ?? 50,
                requireUnique: false).GetAwaiter().GetResult();

        if (outcome.Ambiguous)
        {
            return Fail("AMBIGUOUS",
                $"共 {outcome.Total} 个元素命中，写操作要求唯一；候选见 candidates",
                new { total = outcome.Total, candidates = outcome.Candidates });
        }
        return Ok(new
        {
            total = outcome.Total,
            matches = outcome.Matches,
        });
    }
}

internal sealed class ElementTree : WindowsCapability
{
    public override string Id => "element.tree";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var selector = SelectorParser.Parse(context.Args);
        var scope = ScopeResolver.Resolve(context, selector);
        var depth = context.Args.GetInt32("depth") ?? 4;
        var tree = context.GetRequiredService<UiaService>()
            .TreeAsync(scope, Math.Clamp(depth, 1, 12)).GetAwaiter().GetResult();
        return Ok(tree);
    }
}

internal sealed class ElementTextGet : WindowsCapability
{
    public override string Id => "element.text.get";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var selector = SelectorParser.Parse(context.Args);
        var scope = ScopeResolver.Resolve(context, selector);
        var text = context.GetRequiredService<UiaService>()
            .GetTextAsync(scope, selector).GetAwaiter().GetResult();
        return Ok(new { text });
    }
}

internal sealed class ElementInvoke : WindowsCapability
{
    public override string Id => "element.invoke";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var selector = SelectorParser.Parse(context.Args)
                       ?? throw new MissingParameterException("selector");
        var scope = ScopeResolver.Resolve(context, selector);
        var action = context.Args.GetString("action") ?? "invoke";
        context.GetRequiredService<UiaService>()
            .ActAsync(scope, selector, action).GetAwaiter().GetResult();
        return Ok(new { invoked = true, action });
    }
}

internal sealed class ElementSetValue : WindowsCapability
{
    public override string Id => "element.set_value";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var selector = SelectorParser.Parse(context.Args)
                       ?? throw new MissingParameterException("selector");
        var value = context.Args.GetString("value")
                    ?? (context.Args.Raw("value") is { } v ? v.GetRawText() : null)
                    ?? throw new MissingParameterException("value");
        var scope = ScopeResolver.Resolve(context, selector);
        context.GetRequiredService<UiaService>()
            .SetValueAsync(scope, selector, value).GetAwaiter().GetResult();
        return Ok(new { set = true });
    }
}

internal sealed class ElementToggle : WindowsCapability
{
    public override string Id => "element.toggle";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var selector = SelectorParser.Parse(context.Args)
                       ?? throw new MissingParameterException("selector");
        var scope = ScopeResolver.Resolve(context, selector);
        context.GetRequiredService<UiaService>()
            .ActAsync(scope, selector, "toggle").GetAwaiter().GetResult();
        return Ok(new { toggled = true });
    }
}

internal sealed class ElementScroll : WindowsCapability
{
    public override string Id => "element.scroll";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var selector = SelectorParser.Parse(context.Args)
                       ?? throw new MissingParameterException("selector");
        var scope = ScopeResolver.Resolve(context, selector);
        var args = context.Args;
        context.GetRequiredService<UiaService>()
            .ScrollAsync(scope, selector,
                args.GetString("direction"),
                args.GetInt32("amount") ?? 3,
                args.GetDouble("horizontal_percent"),
                args.GetDouble("vertical_percent"))
            .GetAwaiter().GetResult();
        return Ok(new { scrolled = true });
    }
}
