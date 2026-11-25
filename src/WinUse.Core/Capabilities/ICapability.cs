namespace WinUse.Core.Capabilities;

/// <summary>能力实现接口。一个能力 ID 对应一个实现。</summary>
public interface ICapability
{
    /// <summary>能力 ID，须与 <see cref="CapabilityDescriptor.Id"/> 一致。</summary>
    string Id { get; }

    /// <summary>执行能力。实现内部不得吞掉异常后返回成功。</summary>
    Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken);
}
