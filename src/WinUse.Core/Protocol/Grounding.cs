using System.Text.Json.Serialization;

namespace WinUse.Core.Protocol;

/// <summary>定位/感知证据（06 §2.3、§5）。</summary>
public sealed class GroundingInfo
{
    /// <summary>实际生效的通道：UIA / PIXEL / WIN32 / COM。</summary>
    [JsonPropertyName("layer")]
    public string Layer { get; set; } = "";

    /// <summary>置信度：high / medium / low。</summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "high";

    /// <summary>是否发生了降级。</summary>
    [JsonPropertyName("degraded")]
    public bool Degraded { get; set; }

    /// <summary>降级原因。</summary>
    [JsonPropertyName("fallback_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackReason { get; set; }

    /// <summary>尝试过的层数。</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; } = 1;

    public static GroundingInfo For(Layer layer, string confidence = "high") =>
        new() { Layer = layer.ToString(), Confidence = confidence };
}
