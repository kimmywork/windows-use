using WinUse.Core.Capabilities;
using WinUse.Core.Protocol;

namespace WinUse.Core.Tools;

/// <summary>
/// 工具导出（06 §1.2）：把可见能力描述符转换为三种 Agent 框架格式。
/// 放在 Core 供 HTTP / CLI 传输共用。
/// </summary>
public static class ToolExporter
{
    public static IReadOnlyList<object> OpenAI(IEnumerable<CapabilityDescriptor> capabilities) =>
        capabilities.Select(d => (object)new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = d.Id,
                ["description"] = Describe(d),
                ["parameters"] = EmptyObjectSchema,
            },
        }).ToArray();

    public static IReadOnlyList<object> Anthropic(IEnumerable<CapabilityDescriptor> capabilities) =>
        capabilities.Select(d => (object)new Dictionary<string, object?>
        {
            ["name"] = d.Id,
            ["description"] = Describe(d),
            ["input_schema"] = EmptyObjectSchema,
        }).ToArray();

    public static object Mcp(IEnumerable<CapabilityDescriptor> capabilities)
    {
        var tools = capabilities.Select(d => (object)new Dictionary<string, object?>
        {
            ["name"] = d.Id,
            ["description"] = Describe(d),
            ["inputSchema"] = EmptyObjectSchema,
        }).ToArray();
        return new Dictionary<string, object?> { ["tools"] = tools };
    }

    /// <summary>描述中强制带上风险/档位/通道，防止 Agent 不知风险即调用（06 §1.2）。</summary>
    private static string Describe(CapabilityDescriptor d) =>
        $"{d.Description} [tier={TierName(d.Tier)}, risk={d.Risk}, layers={string.Join("/", d.Layers.Select(LayerName))}, since={d.Since}]";

    private static string TierName(Tier t) => t.ToString().ToLowerInvariant();

    private static string LayerName(Layer l) => l.ToString();

    /// <summary>
    /// M1 基线：参数级 JSON Schema 尚未随能力表生成（docs/02 持有参数权威），
    /// 先暴露为开放对象；/v1/schema 生成器落地后替换。
    /// </summary>
    private static Dictionary<string, object?> EmptyObjectSchema { get; } = new()
    {
        ["type"] = "object",
        ["$comment"] = "参数模式待 /v1/schema 生成器提供，参见 docs/02",
    };
}
