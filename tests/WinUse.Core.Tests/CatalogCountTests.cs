using WinUse.Core.Capabilities;
using WinUse.Core.Protocol;

namespace WinUse.Core.Tests;

/// <summary>
/// 能力目录计数断言（09 M0.5 门禁；docs/07 §0.7）。
/// 总数 168；版本 100/38/30；档位 46/48/6/68。
/// </summary>
public class CatalogCountTests
{
    private static CapabilityDescriptor[] All => CapabilityCatalog.Load();

    [Fact]
    public void Total_is_168()
    {
        Assert.Equal(168, All.Length);
    }

    [Fact]
    public void Version_counts_are_100_38_30()
    {
        Assert.Equal(100, All.Count(d => d.Since == "v1"));
        Assert.Equal(38, All.Count(d => d.Since == "v1.5"));
        Assert.Equal(30, All.Count(d => d.Since == "v2"));
    }

    [Fact]
    public void Tier_counts_are_46_48_6_68()
    {
        Assert.Equal(46, All.Count(d => d.Tier == Tier.Core));
        Assert.Equal(48, All.Count(d => d.Tier == Tier.Extended));
        Assert.Equal(6, All.Count(d => d.Tier == Tier.Advanced));
        Assert.Equal(68, All.Count(d => d.Tier == Tier.Future));
    }

    [Fact]
    public void Ids_are_unique()
    {
        Assert.Equal(All.Length, All.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_descriptor_has_layers()
    {
        Assert.All(All, d => Assert.NotEmpty(d.Layers));
    }
}
