using System.Text.Json;

namespace WinUse.Core.Selector;

/// <summary>选择器模型（06 §4.1）。定位字段 AND 语义组合。</summary>
public sealed record Selector
{
    public string? AutomationId { get; set; }
    public string? ControlType { get; set; }
    public string? Name { get; set; }

    /// <summary>exact / prefix / contains / regex，默认 exact。</summary>
    public string NameMatch { get; set; } = "exact";

    /// <summary>Win32 类名（UIA ClassName）。</summary>
    public string? Class { get; set; }
    public string? FrameworkId { get; set; }

    /// <summary>树路径（不稳定，仅兜底）。</summary>
    public string? Path { get; set; }

    public int? Index { get; set; }
    public NearClause? Near { get; set; }
    public WithinClause? Within { get; set; }
    public int? Depth { get; set; }

    public bool VisibleOnly { get; set; } = true;
    public bool EnabledOnly { get; set; } = true;

    /// <summary>多语言别名：BCP-47 → 名称。</summary>
    public Dictionary<string, string>? NameAliases { get; set; }

    /// <summary>是否提供了任意定位字段。</summary>
    public bool HasLocator =>
        AutomationId is not null || ControlType is not null || Name is not null ||
        Class is not null || FrameworkId is not null || Path is not null ||
        NameAliases is { Count: > 0 };
}

/// <summary>邻近定位（06 §4.1）。</summary>
public sealed class NearClause
{
    public required Selector Anchor { get; set; }
    public int MaxDistancePx { get; set; } = 120;
}

/// <summary>子树限定（06 §4.1）。</summary>
public sealed class WithinClause
{
    public long? Hwnd { get; set; }
    public string? ProcessName { get; set; }
    public string? Class { get; set; }
}
