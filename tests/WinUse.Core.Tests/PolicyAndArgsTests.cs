using System.Text.Json;
using WinUse.Core.Capabilities;
using WinUse.Core.Policy;
using WinUse.Core.Protocol;

namespace WinUse.Core.Tests;

public class RiskClassifierTests
{
    private static CapabilityDescriptor Descriptor(RiskLevel risk) => new(
        "element.invoke", "1.0", Tier.Core, risk, Idempotency.N,
        new[] { Layer.UIA }, "调用元素");

    [Fact]
    public void Static_risk_passes_through()
    {
        var decision = new RiskClassifier().Classify(Descriptor(RiskLevel.R1), "确定");
        Assert.Equal(RiskLevel.R1, decision.Risk);
        Assert.False(decision.Escalated);
    }

    [Theory]
    [InlineData("删除该文件")]
    [InlineData("Delete item")]
    [InlineData("发送邮件")]
    public void Dangerous_keywords_escalate_to_R3(string name)
    {
        var decision = new RiskClassifier().Classify(Descriptor(RiskLevel.R1), name);
        Assert.Equal(RiskLevel.R3, decision.Risk);
        Assert.True(decision.Escalated);
    }

    [Fact]
    public void Already_R3_is_not_re_escalated()
    {
        var decision = new RiskClassifier().Classify(Descriptor(RiskLevel.R3), "删除");
        Assert.False(decision.Escalated);
    }
}

public class CapabilityArgsTests
{
    private static CapabilityArgs Args(string json) =>
        new(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Typed_accessors_work()
    {
        var args = Args("""{"s":"x","i":3,"b":true,"d":1.5}""");
        Assert.Equal("x", args.GetString("s"));
        Assert.Equal(3, args.GetInt32("i"));
        Assert.True(args.GetBool("b"));
        Assert.Equal(1.5, args.GetDouble("d"));
    }

    [Fact]
    public void RequireString_throws_when_missing()
    {
        Assert.Throws<MissingParameterException>(() => Args("{}").RequireString("s"));
    }

    [Fact]
    public void Missing_optional_returns_null()
    {
        Assert.Null(Args("{}").GetString("s"));
    }
}
