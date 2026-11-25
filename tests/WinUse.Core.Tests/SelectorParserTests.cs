using System.Text.Json;
using WinUse.Core.Protocol;
using WinUse.Core.Selector;

namespace WinUse.Core.Tests;

/// <summary>选择器解析（09 M0.5 G-B；06 §4）。</summary>
public class SelectorParserTests
{
    private static Sel Parse(string json) =>
        SelectorParser.Parse(JsonDocument.Parse(json).RootElement)!;

    [Fact]
    public void Parses_basic_fields()
    {
        var s = Parse("""{"control_type":"button","name":"确定","automation_id":"okBtn"}""");
        Assert.Equal("button", s.ControlType);
        Assert.Equal("确定", s.Name);
        Assert.Equal("okBtn", s.AutomationId);
    }

    [Fact]
    public void Parses_near_with_anchor()
    {
        var s = Parse("""
        {"control_type":"edit","near":{"selector":{"control_type":"label","name":"用户名"},"max_distance_px":80}}
        """);
        Assert.NotNull(s.Near);
        Assert.Equal(80, s.Near!.MaxDistancePx);
        Assert.Equal("label", s.Near.Anchor.ControlType);
    }

    [Fact]
    public void Parses_within_hwnd()
    {
        var s = Parse("""{"control_type":"button","within":{"hwnd":12345}}""");
        Assert.Equal(12345, s.Within!.Hwnd);
    }

    [Fact]
    public void Parses_name_aliases()
    {
        var s = Parse("""{"name":"确定","name_aliases":{"en":"OK"}}""");
        Assert.Equal("OK", s.NameAliases!["en"]);
    }

    [Fact]
    public void Non_object_returns_null()
    {
        Assert.Null(SelectorParser.Parse(JsonDocument.Parse("[]").RootElement));
    }

    [Fact]
    public void Defaults_visible_and_enabled_only_true()
    {
        var s = Parse("""{"control_type":"button"}""");
        Assert.True(s.VisibleOnly);
        Assert.True(s.EnabledOnly);
    }
}
