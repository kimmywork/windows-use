using WinUse.Core.Protocol;

namespace WinUse.Core.Capabilities;

/// <summary>能力执行结果。失败必须携带结构化错误，禁止静默成功。</summary>
public sealed class CapabilityResult
{
    public bool Ok { get; private init; }
    public object? Value { get; private init; }
    public GroundingInfo? Grounding { get; private init; }
    public RpcError? Error { get; private init; }

    public static CapabilityResult Success(object? value = null, GroundingInfo? grounding = null) =>
        new() { Ok = true, Value = value, Grounding = grounding };

    public static CapabilityResult Failure(string errorType, string? message = null, object? data = null) =>
        new() { Ok = false, Error = Errors.ErrorCatalog.Rpc(errorType, message, data) };

    public static CapabilityResult Failure(RpcError error) =>
        new() { Ok = false, Error = error };
}
