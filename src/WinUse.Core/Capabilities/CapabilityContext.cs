using WinUse.Core.Protocol;

namespace WinUse.Core.Capabilities;

/// <summary>
/// 能力执行上下文：携带本次调用的参数、会话信息与服务容器。
/// </summary>
public sealed class CapabilityContext
{
    public required string SessionId { get; init; }
    public required CapabilityArgs Args { get; init; }
    public required IServiceProvider Services { get; init; }

    /// <summary>权限模式：observe / standard / elevated。</summary>
    public string Mode { get; init; } = "standard";

    /// <summary>本次会话已声明的档位。</summary>
    public IReadOnlySet<Tier> GrantedTiers { get; init; } = new HashSet<Tier> { Tier.Core };

    public T GetRequiredService<T>() where T : notnull =>
        Services.GetService(typeof(T)) is T t
            ? t
            : throw new InvalidOperationException($"服务未注册: {typeof(T).FullName}");

    public T? GetService<T>() =>
        Services.GetService(typeof(T)) is T t ? t : default;
}
