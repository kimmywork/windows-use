using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinUse.Core.Protocol;

/// <summary>统一响应信封（06 §2.2）。result 与 error 互斥。</summary>
public sealed class RpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RpcError? Error { get; set; }

    public static RpcResponse Ok(JsonElement? id, object? result) =>
        new() { Id = id, Result = result };

    public static RpcResponse Fail(JsonElement? id, RpcError error) =>
        new() { Id = id, Error = error };
}

/// <summary>结构化错误（06 §9）。</summary>
public sealed class RpcError
{
    /// <summary>整数错误码（06 §9.1）。</summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>稳定错误类型字符串，Agent 应据此分支。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>恢复策略：retry/degrade/escalate/abort/wait_resume。</summary>
    [JsonPropertyName("recovery")]
    public string Recovery { get; set; } = "abort";

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}
