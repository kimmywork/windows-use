using WinUse.Core.Capabilities;
using WinUse.Core.Protocol;

namespace WinUse.Core.Policy;

/// <summary>风险判定结果。</summary>
public sealed record RiskDecision(RiskLevel Risk, bool Escalated, string? MatchedKeyword)
{
    public static RiskDecision Static(RiskLevel risk) => new(risk, false, null);
}

/// <summary>
/// 风险分级器（02 §6 静态分级 + 07 §0.6 动态语义升级）。
/// 高危词表可配置、可关闭，但关闭必须显式。
/// </summary>
public sealed class RiskClassifier
{
    // 07 §0.6 规定的中英高危词表（子串匹配，大小写不敏感）。
    public static readonly string[] DefaultKeywords =
    [
        "Send", "发送",
        "Delete", "删除",
        "Format", "格式化",
        "Install", "安装",
        "Uninstall", "卸载",
        "Pay", "支付", "付款",
        "ConfirmOrder", "确认订单",
        "Submit", "提交",
    ];

    private readonly string[] _keywords;

    public RiskClassifier(string[]? keywords = null, bool escalationEnabled = true)
    {
        EscalationEnabled = escalationEnabled;
        _keywords = keywords ?? DefaultKeywords;
    }

    public bool EscalationEnabled { get; }

    /// <summary>对即将执行的动作做风险判定。</summary>
    public RiskDecision Classify(CapabilityDescriptor descriptor, string? targetName = null,
        string? automationId = null, string? className = null)
    {
        var risk = descriptor.Risk;
        if (!EscalationEnabled || risk >= RiskLevel.R3)
        {
            return RiskDecision.Static(risk);
        }

        foreach (var field in new[] { targetName, automationId, className })
        {
            if (string.IsNullOrEmpty(field))
            {
                continue;
            }
            foreach (var kw in _keywords)
            {
                if (field.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    var escalated = (RiskLevel)Math.Max((int)risk, (int)RiskLevel.R3);
                    return new RiskDecision(escalated, true, kw);
                }
            }
        }

        return RiskDecision.Static(risk);
    }
}
