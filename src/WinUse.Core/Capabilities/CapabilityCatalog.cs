namespace WinUse.Core.Capabilities;

/// <summary>
/// 能力目录公共入口：包装构建期生成的内部描述符表（docs/07 §0.7）。
/// </summary>
public static class CapabilityCatalog
{
    /// <summary>加载全部 168 条能力描述符（含 future）。</summary>
    public static CapabilityDescriptor[] Load() => CapabilityCatalogGenerated.CreateDescriptors();
}
