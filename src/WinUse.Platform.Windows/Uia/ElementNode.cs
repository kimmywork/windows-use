using System.Security.Cryptography;
using System.Text;

namespace WinUse.Platform.Windows.Uia;

/// <summary>投影给 Agent 的元素节点（07 §2.1）。</summary>
public sealed record ElementNode(
    string ElementId,
    string Name,
    string ControlType,
    string AutomationId,
    string ClassName,
    string FrameworkId,
    int? ProcessId,
    long NativeHandle,
    int[] Rect,
    bool IsEnabled,
    bool IsOffscreen,
    bool HasKeyboardFocus,
    string[] Patterns,
    ElementNode[]? Children)
{
    public int[]? Center => Rect is [var x, var y, var w, var h]
        ? new[] { x + (w / 2), y + (h / 2) }
        : null;
}

/// <summary>
/// 元素指纹（ADR-007）：跨调用稳定的身份依据。
/// 窗口重建后指纹失配 → ELEMENT_STALE。
/// </summary>
public static class ElementFingerprint
{
    public static string Build(
        int processId,
        string? automationId,
        string? name,
        string? className,
        string controlType,
        string? path)
    {
        var raw = string.Join("|",
            processId,
            automationId ?? "",
            name ?? "",
            className ?? "",
            controlType,
            path ?? "");
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return "e" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
