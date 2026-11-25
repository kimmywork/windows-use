using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinUse.Core.Protocol;

/// <summary>统一请求信封（06 §2.1）。</summary>
public sealed class RpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>请求 ID；通知（无 id）为 null。</summary>
    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("meta")]
    public RequestMeta? Meta { get; set; }
}

/// <summary>请求元信息（06 §2.1）。</summary>
public sealed class RequestMeta
{
    [JsonPropertyName("client")]
    public string? Client { get; set; }

    [JsonPropertyName("client_version")]
    public string? ClientVersion { get; set; }

    /// <summary>幂等键（06 §10）。</summary>
    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; set; }

    /// <summary>本次会话声明需要的档位（07 §0.7.13）。</summary>
    [JsonPropertyName("want_tiers")]
    public List<string>? WantTiers { get; set; }

    /// <summary>追踪 ID。</summary>
    [JsonPropertyName("trace_id")]
    public string? TraceId { get; set; }
}
