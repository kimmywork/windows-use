using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinUse.Core.Scripts;

/// <summary>
/// 用户脚本（07 §20：宏以可读 JSON 定义；PRD N6 不做流程画布）。
/// 一个脚本 = 有序的能力调用步骤序列。
/// </summary>
public sealed record ScriptDocument(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<ScriptStep> Steps,
    string OnError)
{
    public static readonly IReadOnlySet<string> OnErrorPolicies =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abort", "continue", "rollback" };
}

/// <summary>脚本中的一步：调用一个能力并给定参数。</summary>
public sealed record ScriptStep(
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("params")] JsonElement? Params);

/// <summary>脚本文件的 JSON 表示（磁盘 DTO）。</summary>
internal sealed class ScriptFileDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("on_error")] public string? OnError { get; set; }
    [JsonPropertyName("steps")] public List<ScriptStep>? Steps { get; set; }
}
