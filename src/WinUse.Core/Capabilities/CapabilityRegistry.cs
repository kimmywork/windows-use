using WinUse.Core.Protocol;

namespace WinUse.Core.Capabilities;

/// <summary>
/// 能力目录：描述符（构建期由 docs/07 §0.7 生成）+ 实现绑定。
/// 未知 method 与未实现能力的区分是协议骨架的核心断言（09 T-S2/T-S4）。
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<string, CapabilityDescriptor> _descriptors;
    private readonly Dictionary<string, ICapability> _implementations;

    public CapabilityRegistry(IEnumerable<CapabilityDescriptor> descriptors, IEnumerable<ICapability> implementations)
    {
        _descriptors = descriptors.ToDictionary(d => d.Id, StringComparer.Ordinal);
        _implementations = implementations.ToDictionary(c => c.Id, StringComparer.Ordinal);

        // 实现不允许指向不存在的能力 ID。
        foreach (var id in _implementations.Keys)
        {
            if (!_descriptors.ContainsKey(id))
            {
                throw new InvalidOperationException($"能力实现绑定了未注册的 ID: {id}");
            }
        }
    }

    public bool IsKnown(string method) => _descriptors.ContainsKey(method);

    public bool TryGetDescriptor(string method, out CapabilityDescriptor descriptor) =>
        _descriptors.TryGetValue(method, out descriptor!);

    public ICapability? GetImplementation(string method) =>
        _implementations.TryGetValue(method, out var impl) ? impl : null;

    public bool IsImplemented(string method) => _implementations.ContainsKey(method);

    /// <summary>按档位/域过滤目录。null 档位默认只返回 core（06 §3.2）。</summary>
    public CapabilityDescriptor[] Query(Tier? tier = null, string? domain = null)
    {
        var q = _descriptors.Values.AsEnumerable();
        if (tier is { } t)
        {
            q = q.Where(d => d.Tier == t);
        }
        if (!string.IsNullOrEmpty(domain))
        {
            q = q.Where(d => d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
        }
        return q.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>按已授权档位列出可见能力（future 永不出现）。</summary>
    public CapabilityDescriptor[] ListVisible(IReadOnlySet<Tier> grantedTiers, string? domain = null)
    {
        return _descriptors.Values
            .Where(d => d.Tier != Tier.Future && grantedTiers.Contains(d.Tier))
            .Where(d => domain is null || d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
