using System.Text.Json;
using WinUse.Core.Protocol;

namespace WinUse.Core.Selector;

/// <summary>选择器解析（06 §4）。</summary>
public static class SelectorParser
{
    /// <summary>从参数中解析选择器（默认取 "selector" 字段）。</summary>
    public static Selector? Parse(CapabilityArgs args, string key = "selector")
    {
        var raw = args.Raw(key);
        return raw is null ? null : Parse(raw.Value);
    }

    public static Selector? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var s = new Selector
        {
            AutomationId = Str(element, "automation_id"),
            ControlType = Str(element, "control_type"),
            Name = Str(element, "name"),
            Class = Str(element, "class"),
            FrameworkId = Str(element, "framework_id"),
            Path = Str(element, "path"),
            NameMatch = Str(element, "name_match") ?? "exact",
            Index = Int(element, "index"),
            Depth = Int(element, "depth"),
        };

        if (element.TryGetProperty("near", out var near) && near.ValueKind == JsonValueKind.Object)
        {
            var anchor = near.TryGetProperty("selector", out var anc) ? Parse(anc) : null;
            if (anchor is not null)
            {
                s.Near = new NearClause
                {
                    Anchor = anchor,
                    MaxDistancePx = Int(near, "max_distance_px") ?? 120,
                };
            }
        }

        if (element.TryGetProperty("within", out var within) && within.ValueKind == JsonValueKind.Object)
        {
            s.Within = new WithinClause
            {
                Hwnd = Long(within, "hwnd"),
                ProcessName = Str(within, "processName") ?? Str(within, "process_name"),
                Class = Str(within, "class"),
            };
        }

        if (element.TryGetProperty("name_aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Object)
        {
            s.NameAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in aliases.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                {
                    s.NameAliases[p.Name] = v;
                }
            }
        }

        if (element.TryGetProperty("visible_only", out var vo) && vo.ValueKind == JsonValueKind.False)
        {
            s.VisibleOnly = false;
        }
        if (element.TryGetProperty("enabled_only", out var eo) && eo.ValueKind == JsonValueKind.False)
        {
            s.EnabledOnly = false;
        }

        return s;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v))
        {
            return v;
        }
        return null;
    }

    private static long? Long(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v))
        {
            return v;
        }
        return null;
    }
}
