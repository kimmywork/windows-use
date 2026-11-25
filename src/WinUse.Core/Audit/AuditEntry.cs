using System.Text.Json.Serialization;

namespace WinUse.Core.Audit;

/// <summary>审计条目（03 P0-15、06 §12）。每个请求一条，100% 覆盖。</summary>
public sealed class AuditEntry
{
    [JsonPropertyName("ts")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>请求 ID（字符串/数字原样转字符串；通知为 null）。</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("risk")]
    public required string Risk { get; init; }

    /// <summary>ok / error。</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("error_type")]
    public string? ErrorType { get; init; }

    [JsonPropertyName("duration_ms")]
    public required double DurationMs { get; init; }

    /// <summary>参数是否在日志中脱敏。</summary>
    [JsonPropertyName("masked")]
    public bool Masked { get; init; } = true;

    /// <summary>该动作无内置/显式校验（06 §9.2 补充说明）。</summary>
    [JsonPropertyName("unverified")]
    public bool Unverified { get; init; }

    [JsonPropertyName("replayed")]
    public bool Replayed { get; init; }
}
