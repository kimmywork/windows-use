using WinUse.Core.Protocol;

namespace WinUse.Core.Capabilities;

/// <summary>能力元数据（07 §0.2）。</summary>
public sealed record CapabilityDescriptor(
    string Id,
    string Since,
    Tier Tier,
    RiskLevel Risk,
    Idempotency Idempotency,
    Layer[] Layers,
    string Description)
{
    /// <summary>能力域（首个 '.' 之前的部分）。</summary>
    public string Domain => Id[..Id.IndexOf('.')];
}
