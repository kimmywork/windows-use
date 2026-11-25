using System.Diagnostics;
using System.Text.Json;
using WinUse.Core.Audit;
using WinUse.Core.Capabilities;
using WinUse.Core.Errors;
using WinUse.Core.IdempotencyFlow;
using WinUse.Core.Policy;
using WinUse.Core.Protocol;

namespace WinUse.Core.Dispatch;

/// <summary>
/// 唯一编排入口（05 §6、06 §2）：信封校验 → 目录查找 → 档位校验 →
/// 风险/确认 → 幂等 → 执行 → 审计。
/// </summary>
public sealed class CapabilityDispatcher
{
    private readonly CapabilityRegistry _registry;
    private readonly SessionState _session;
    private readonly RiskClassifier _risk;
    private readonly ApprovalGate _approvals;
    private readonly IdempotencyStore _idempotency;
    private readonly AuditWriter _audit;
    private readonly IServiceProvider _services;

    public CapabilityDispatcher(
        CapabilityRegistry registry,
        SessionState session,
        RiskClassifier risk,
        ApprovalGate approvals,
        IdempotencyStore idempotency,
        AuditWriter audit,
        IServiceProvider services)
    {
        _registry = registry;
        _session = session;
        _risk = risk;
        _approvals = approvals;
        _idempotency = idempotency;
        _audit = audit;
        _services = services;
    }

    public async Task<RpcResponse?> DispatchAsync(RpcRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return RpcResponse.Fail(null, ErrorCatalog.Standard(ErrorCatalog.InvalidRequest, "请求体为空或不是合法 JSON"));
        }

        var method = request.Method;
        var sw = Stopwatch.StartNew();
        RpcResponse response;
        var risk = "R0";

        try
        {
            if (string.IsNullOrEmpty(method))
            {
                response = RpcResponse.Fail(request.Id,
                    ErrorCatalog.Standard(ErrorCatalog.InvalidRequest, "缺少 method 字段"));
            }
            else
            {
                var decision = await DispatchMethodAsync(request, method!, cancellationToken).ConfigureAwait(false);
                response = decision.Response;
                risk = decision.Risk;
            }
        }
        catch (OperationCanceledException)
        {
            response = RpcResponse.Fail(request.Id, ErrorCatalog.Rpc("TIMEOUT", "操作被取消或超时"));
        }
        catch (MissingParameterException ex)
        {
            response = RpcResponse.Fail(request.Id, ErrorCatalog.Standard(ErrorCatalog.InvalidParams,
                ex.Message, new Dictionary<string, object?> { ["field"] = ex.ParameterName }));
        }
        catch (Exception ex)
        {
            response = RpcResponse.Fail(request.Id, ErrorCatalog.Standard(ErrorCatalog.InternalError,
                $"内部错误: {ex.GetType().Name}: {ex.Message}"));
        }

        sw.Stop();
        WriteAudit(request, method, risk, response, sw.Elapsed.TotalMilliseconds);

        // 通知（无 id）：执行但不回响应。
        return request.Id is null ? null : response;
    }

    private async Task<(RpcResponse Response, string Risk)> DispatchMethodAsync(
        RpcRequest request, string method, CancellationToken cancellationToken)
    {
        if (!_registry.TryGetDescriptor(method, out var descriptor))
        {
            return (RpcResponse.Fail(request.Id,
                ErrorCatalog.Standard(ErrorCatalog.MethodNotFound, $"未知方法: {method}")), "R0");
        }

        // 档位校验（07 §0.7.13、06 §3.2）。
        if (descriptor.Tier == Tier.Future || !_session.GrantedTiers.Contains(descriptor.Tier))
        {
            var error = ErrorCatalog.Rpc("CAPABILITY_NOT_GRANTED",
                $"能力 {method} 需要档位 {descriptor.Tier.ToString().ToLowerInvariant()}，本次会话未声明",
                new Dictionary<string, object?>
                {
                    ["required_tier"] = descriptor.Tier.ToString().ToLowerInvariant(),
                    ["granted_tiers"] = _session.GrantedTiers.Select(t => t.ToString().ToLowerInvariant()),
                });
            return (RpcResponse.Fail(request.Id, error), descriptor.Risk.ToString());
        }

        var implementation = _registry.GetImplementation(method);
        if (implementation is null)
        {
            return (RpcResponse.Fail(request.Id, ErrorCatalog.Rpc("NOT_IMPLEMENTED",
                $"能力 {method} 已注册但尚未实现")), descriptor.Risk.ToString());
        }

        var args = request.Params is { } p && p.ValueKind == JsonValueKind.Object
            ? new CapabilityArgs(p)
            : new CapabilityArgs(Json.SerializeEmptyObject());

        var context = new CapabilityContext
        {
            SessionId = _session.SessionId,
            Args = args,
            Services = _services,
            Mode = _session.Mode,
            GrantedTiers = _session.GrantedTiers,
        };

        // 静态风险确认；支持动态风险的能力（如 macro.run）按实际参数评估。
        var decision = _risk.Classify(descriptor);
        if (implementation is IDynamicRiskCapability dynamicRisk)
        {
            var assessed = await dynamicRisk
                .AssessRiskAsync(context, cancellationToken).ConfigureAwait(false);
            decision = RiskDecision.Static(assessed);
        }

        var approval = await _approvals.CheckAsync(
            new ApprovalRequest(method, decision.Risk, descriptor.Description, null),
            _session.Mode, cancellationToken).ConfigureAwait(false);

        if (approval.Kind == ApprovalOutcomeKind.Denied)
        {
            var type = approval.Reason?.Contains("observe") == true ? "NOT_SUPPORTED" : "APPROVAL_DENIED";
            return (RpcResponse.Fail(request.Id, ErrorCatalog.Rpc(type, approval.Reason)), decision.Risk.ToString());
        }
        if (approval.Kind == ApprovalOutcomeKind.TimedOut)
        {
            return (RpcResponse.Fail(request.Id, ErrorCatalog.Rpc("TIMEOUT", "确认超时")), decision.Risk.ToString());
        }

        async Task<RpcResponse> ProduceAsync()
        {
            var result = await implementation.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            return result.Ok
                ? RpcResponse.Ok(request.Id, result.Value)
                : RpcResponse.Fail(request.Id, result.Error!);
        }

        var response = request.Meta?.IdempotencyKey is { Length: > 0 } key
            ? await _idempotency.GetOrAddAsync(key, ProduceAsync).ConfigureAwait(false)
            : await ProduceAsync().ConfigureAwait(false);

        return (response, decision.Risk.ToString());
    }

    private void WriteAudit(RpcRequest request, string? method, string risk, RpcResponse response, double durationMs)
    {
        try
        {
            _audit.Write(new AuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = _session.SessionId,
                Method = method ?? "<none>",
                RequestId = AuditWriter.RequestIdToString(request.Id),
                Mode = _session.Mode,
                Risk = risk,
                Outcome = response.Error is null ? "ok" : "error",
                ErrorType = response.Error?.Type,
                DurationMs = Math.Round(durationMs, 2),
                Masked = true,
            });
        }
        catch
        {
            // 审计写入失败不得吞掉业务响应；Host 应通过磁盘监控另行告警。
        }
    }
}

internal static class Json
{
    public static System.Text.Json.JsonElement SerializeEmptyObject() =>
        System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
}
