using WinUse.Core.Protocol;

namespace WinUse.Core.Policy;

/// <summary>确认请求。</summary>
public sealed record ApprovalRequest(
    string Method,
    RiskLevel Risk,
    string Summary,
    IReadOnlyDictionary<string, object?>? Details);

public enum ApprovalOutcomeKind
{
    Approved,
    Denied,
    TimedOut,
}

/// <summary>确认结果。</summary>
public sealed record ApprovalResult(ApprovalOutcomeKind Kind, string? Reason = null)
{
    public static readonly ApprovalResult Approved = new(ApprovalOutcomeKind.Approved);
    public static ApprovalResult Denied(string? reason = null) => new(ApprovalOutcomeKind.Denied, reason);
}

/// <summary>确认处理器：Host 提供交互式实现（托盘确认窗）；默认无头拒绝。</summary>
public interface IApprovalHandler
{
    Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>无头处理器：默认拒绝一切需要确认的动作（最安全的回退）。</summary>
public sealed class HeadlessApprovalHandler : IApprovalHandler
{
    public Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalResult.Denied("当前为无头模式，无法弹出确认窗"));
}

/// <summary>
/// 确认门（06 §8、03 P0-10）。
/// 规则：observe 模式只读；R0–R1 自动放行；R2–R3 需确认；R4 不可预授权、必须人工。
/// </summary>
public sealed class ApprovalGate
{
    private readonly IApprovalHandler _handler;

    public ApprovalGate(IApprovalHandler handler)
    {
        _handler = handler;
    }

    public async Task<ApprovalResult> CheckAsync(ApprovalRequest request, string mode, CancellationToken cancellationToken)
    {
        if (mode.Equals("observe", StringComparison.OrdinalIgnoreCase) && request.Risk >= RiskLevel.R1)
        {
            return ApprovalResult.Denied("observe 模式为只读");
        }

        return request.Risk switch
        {
            RiskLevel.R0 or RiskLevel.R1 => ApprovalResult.Approved,
            RiskLevel.R2 or RiskLevel.R3 => await _handler.RequestAsync(request, cancellationToken).ConfigureAwait(false),
            _ => await _handler.RequestAsync(request, cancellationToken).ConfigureAwait(false), // R4：必须人工，不可预授权
        };
    }
}
