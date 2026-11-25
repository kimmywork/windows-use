namespace WinUse.Core.Protocol;

/// <summary>静态风险分级（06 §3 / 02 §6）。数字越大风险越高。</summary>
public enum RiskLevel
{
    R0 = 0,
    R1 = 1,
    R2 = 2,
    R3 = 3,
    R4 = 4,
}

/// <summary>幂等性（07 §0.3）：I 幂等；C 条件幂等（需 etag/if_match）；N 非幂等。</summary>
public enum Idempotency
{
    /// <summary>幂等：重复调用结果一致。</summary>
    I,
    /// <summary>条件幂等：需配合条件写入。</summary>
    C,
    /// <summary>非幂等：重复调用产生重复副作用。</summary>
    N,
}

/// <summary>感知/执行通道层（05 §4 L0–L4）。</summary>
public enum Layer
{
    UIA,
    PIXEL,
    WIN32,
    COM,
}

/// <summary>能力暴露档位（07 §0.7.13）。</summary>
public enum Tier
{
    Core,
    Extended,
    Advanced,
    Future,
}
