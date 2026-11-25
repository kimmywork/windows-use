using WinUse.Core.Policy;
using WinUse.Core.Protocol;

namespace WinUse.Core.Capabilities;

/// <summary>
/// 能基于实际参数动态给出风险级别的能力（如 macro.run：风险取宏内最高风险动作，07 §20.2）。
/// Dispatcher 在确认门前调用；未实现此接口的能力一律使用描述符静态风险。
/// </summary>
public interface IDynamicRiskCapability
{
    Task<RiskLevel> AssessRiskAsync(CapabilityContext context, CancellationToken cancellationToken);
}
