using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Uia;

/// <summary>UIA 高层服务：所有方法经 <see cref="UiaIsolationRunner"/> 隔离执行。</summary>
public sealed class UiaService
{
    private readonly UiaIsolationRunner _runner;
    private readonly TimeSpan _hardTimeout = TimeSpan.FromSeconds(25);

    public UiaService(UiaIsolationRunner runner)
    {
        _runner = runner;
    }

    public sealed record FindOutcome(ElementNode[] Matches, int Total, bool Ambiguous, ElementNode[] Candidates);

    // ---- Public API ----

    public Task<FindOutcome> FindAsync(IntPtr rootHwnd, Core.Selector.Selector selector,
        int maxResults = 50, bool requireUnique = false, CancellationToken ct = default) =>
        _runner.RunAsync(a =>
        {
            var root = ResolveRoot(a, rootHwnd);
            var all = Collect(a, root, selector);
            var total = all.Count;

            if (selector.Near is not null)
            {
                var anchors = Collect(a, root, selector.Near.Anchor);
                if (anchors.Count > 0)
                {
                    var anchorNode = Project(a, anchors[0], "");
                    var ac = anchorNode.Center!;
                    all = all
                        .Select(e => (e, n: Project(a, e, "")))
                        .Where(t => t.n.Center is { } c &&
                            Math.Sqrt(Math.Pow(c[0] - ac[0], 2) + Math.Pow(c[1] - ac[1], 2)) <= selector.Near.MaxDistancePx)
                        .Select(t => t.e)
                        .ToList();
                }
            }

            if (requireUnique && all.Count > 1 && selector.Index is null)
            {
                return new FindOutcome(Array.Empty<ElementNode>(), total, true,
                    all.Take(10).Select((e, i) => Project(a, e, i.ToString())).ToArray());
            }

            var startIndex = selector.Index ?? 0;
            var page = all.Skip(Math.Max(0, startIndex)).Take(maxResults)
                .Select((e, i) => Project(a, e, (startIndex + i).ToString()))
                .ToArray();
            return new FindOutcome(page, total, false,
                all.Take(10).Select((e, i) => Project(a, e, i.ToString())).ToArray());
        }, _hardTimeout, ct);

    public Task<string> GetFocusedTextAsync(CancellationToken ct = default) =>
        _runner.RunAsync(a =>
        {
            if (a.GetFocusedElement(out var focused) != 0 || focused is null)
            {
                return "";
            }
            var valuePattern = GetPattern<IUIAutomationValuePattern>(focused, UIA.ValuePatternId);
            if (valuePattern is not null && valuePattern.get_CurrentValue(out var v) == 0)
            {
                return v ?? "";
            }
            var textPattern = GetPattern<IUIAutomationTextPattern>(focused, UIA.TextPatternId);
            if (textPattern is not null && textPattern.get_DocumentRange(out var range) == 0 && range is not null)
            {
                range.ExpandToEnclosingUnit(6);
                range.GetText(-1, out var text);
                return text ?? "";
            }
            return "";
        }, _hardTimeout, ct);

    public Task<ElementNode> GetFocusedElementAsync(CancellationToken ct = default) =>
        _runner.RunAsync(a =>
        {
            if (a.GetFocusedElement(out var focused) != 0 || focused is null)
            {
                throw new ElementNotFoundException();
            }
            return Project(a, focused, "");
        }, _hardTimeout, ct);

    public Task<ElementNode> TreeAsync(IntPtr rootHwnd, int maxDepth, CancellationToken ct = default) =>
        _runner.RunAsync(a =>
        {
            var root = ResolveRoot(a, rootHwnd);
            var nodeCount = 0;
            ElementNode Walk(IUIAutomationElement e, int depth, string path)
            {
                var node = Project(a, e, path);
                if (depth >= maxDepth || ++nodeCount > 500)
                {
                    return node with { Children = Array.Empty<ElementNode>() };
                }
                var children = GetChildren(a, e);
                var kids = children
                    .Select((c, i) => Walk(c, depth + 1, path == "" ? i.ToString() : $"{path}/{i}"))
                    .ToArray();
                return node with { Children = kids };
            }
            return Walk(root, 0, "");
        }, _hardTimeout, ct);

    public Task<ElementNode> FindSingleAsync(IntPtr rootHwnd, Core.Selector.Selector selector,
        bool requireUnique = true, CancellationToken ct = default) =>
        _runner.RunAsync(a =>
        {
            var root = ResolveRoot(a, rootHwnd);
            var all = Collect(a, root, selector);
            if (all.Count == 0)
            {
                throw new ElementNotFoundException();
            }
            if (requireUnique && all.Count > 1 && selector.Index is null)
            {
                throw new AmbiguousElementException(all.Count);
            }
            var idx = selector.Index ?? 0;
            return Project(a, all[Math.Min(idx, all.Count - 1)], idx.ToString());
        }, _hardTimeout, ct);

    public Task<object?> ActAsync(IntPtr rootHwnd, Core.Selector.Selector selector, string action,
        CancellationToken ct = default) =>
        _runner.RunAsync<object?>(a =>
        {
            var root = ResolveRoot(a, rootHwnd);
            var all = Collect(a, root, selector);
            if (all.Count == 0)
            {
                throw new ElementNotFoundException();
            }
            if (all.Count > 1 && selector.Index is null)
            {
                throw new AmbiguousElementException(all.Count);
            }
            var element = all[selector.Index ?? 0];
            ApplyAction(a, element, action);
            return null;
        }, _hardTimeout, ct);

    public Task SetValueAsync(IntPtr rootHwnd, Core.Selector.Selector selector, string value,
        CancellationToken ct = default) =>
        _runner.RunAsync<object?>(a =>
        {
            var root = ResolveRoot(a, rootHwnd);
            var all = Collect(a, root, selector);
            if (all.Count == 0)
            {
                throw new ElementNotFoundException();
            }
            if (all.Count > 1 && selector.Index is null)
            {
                throw new AmbiguousElementException(all.Count);
            }
            var pattern = GetPattern<IUIAutomationValuePattern>(all[selector.Index ?? 0], UIA.ValuePatternId);
            if (pattern is null)
            {
                throw new PatternUnsupportedException("value");
            }
            pattern.SetValue(value);
            return null;
        }, _hardTimeout, ct);

    public Task<string> GetTextAsync(IntPtr rootHwnd, Core.Selector.Selector? selector,
        CancellationToken ct = default) =>
        _runner.RunAsync(a =>
        {
            var element = selector is null
                ? ResolveRoot(a, rootHwnd)
                : Collect(a, ResolveRoot(a, rootHwnd), selector) is { Count: > 0 } list
                    ? list[selector.Index ?? 0]
                    : throw new ElementNotFoundException();

            // ① ValuePattern（Edit 类）
            var valuePattern = GetPattern<IUIAutomationValuePattern>(element, UIA.ValuePatternId);
            if (valuePattern is not null && valuePattern.get_CurrentValue(out var v) == 0)
            {
                return v ?? "";
            }

            // ② TextPattern（Document/富文本）
            var textPattern = GetPattern<IUIAutomationTextPattern>(element, UIA.TextPatternId);
            if (textPattern is not null && textPattern.get_DocumentRange(out var range) == 0 && range is not null)
            {
                range.ExpandToEnclosingUnit(6); // TextUnit_Document
                range.GetText(-1, out var text);
                return text ?? "";
            }

            return "";
        }, _hardTimeout, ct);

    public Task ScrollAsync(IntPtr rootHwnd, Core.Selector.Selector selector,
        string? direction, int amount, double? horizontalPercent, double? verticalPercent,
        CancellationToken ct = default) =>
        _runner.RunAsync<object?>(a =>
        {
            var root = ResolveRoot(a, rootHwnd);
            var all = Collect(a, root, selector);
            if (all.Count == 0)
            {
                throw new ElementNotFoundException();
            }
            var element = all[selector.Index ?? 0];
            var scroll = GetPattern<IUIAutomationScrollPattern>(element, UIA.ScrollPatternId);
            if (scroll is null)
            {
                throw new PatternUnsupportedException("scroll");
            }

            if (horizontalPercent.HasValue || verticalPercent.HasValue)
            {
                scroll.SetScrollPercent(horizontalPercent ?? -1d, verticalPercent ?? -1d);
                return null;
            }

            var (h, v) = (direction ?? "down").ToLowerInvariant() switch
            {
                "up" => (2, amount > 0 ? 1 : 3),
                "down" => (2, amount > 0 ? 3 : 1),
                "left" => (amount > 0 ? 1 : 3, 2),
                "right" => (amount > 0 ? 3 : 1, 2),
                _ => (2, 3),
            };
            for (var i = 0; i < Math.Max(1, amount); i++)
            {
                scroll.Scroll(h, v);
            }
            return null;
        }, _hardTimeout, ct);

    // ---- Internals: traversal ----

    private static IUIAutomationElement ResolveRoot(IUIAutomation automation, IntPtr hwnd)
    {
        IUIAutomationElement root;
        if (hwnd == IntPtr.Zero)
        {
            Check(automation.GetRootElement(out root!), "GetRootElement");
        }
        else
        {
            Check(automation.ElementFromHandle(hwnd, out root!), "ElementFromHandle");
        }
        return root ?? throw new InvalidOperationException("根元素为空");
    }

    private static List<IUIAutomationElement> Collect(IUIAutomation automation,
        IUIAutomationElement root, Core.Selector.Selector selector)
    {
        var condition = BuildCondition(automation, selector);
        Check(root.FindAll(UIA.ScopeDescendants, condition, out var arrayPtr), "FindAll");
        var result = new List<IUIAutomationElement>();
        try
        {
            var array = (IUIAutomationElementArray)Marshal.GetTypedObjectForIUnknown(
                arrayPtr, typeof(IUIAutomationElementArray));
            array.get_Length(out var len);
            for (var i = 0; i < len; i++)
            {
                array.GetElement(i, out var element);
                if (element is null)
                {
                    continue;
                }
                if (selector.VisibleOnly && ReadBool(element, UIA.IsOffscreenPropertyId) == true)
                {
                    Marshal.ReleaseComObject(element);
                    continue;
                }
                if (selector.EnabledOnly && ReadBool(element, UIA.IsEnabledPropertyId) == false)
                {
                    Marshal.ReleaseComObject(element);
                    continue;
                }
                if (!NameMatches(selector, ReadString(element, UIA.NamePropertyId)))
                {
                    Marshal.ReleaseComObject(element);
                    continue;
                }
                result.Add(element);
            }
        }
        finally
        {
            Marshal.Release(arrayPtr);
        }
        return result;
    }

    private static List<IUIAutomationElement> GetChildren(IUIAutomation automation, IUIAutomationElement element)
    {
        Check(automation.get_ControlViewCondition(out var controlView), "get_ControlViewCondition");
        Check(element.FindAll(UIA.ScopeChildren, controlView, out var arrayPtr), "FindAll(children)");
        var result = new List<IUIAutomationElement>();
        try
        {
            var array = (IUIAutomationElementArray)Marshal.GetTypedObjectForIUnknown(
                arrayPtr, typeof(IUIAutomationElementArray));
            array.get_Length(out var len);
            for (var i = 0; i < len; i++)
            {
                array.GetElement(i, out var child);
                if (child is not null)
                {
                    result.Add(child);
                }
            }
        }
        finally
        {
            Marshal.Release(arrayPtr);
        }
        return result;
    }

    private static IUIAutomationCondition BuildCondition(IUIAutomation automation, Core.Selector.Selector selector)
    {
        var conditions = new List<IUIAutomationCondition>();

        void Add(int propertyId, object value)
        {
            Check(automation.CreatePropertyCondition(propertyId, value, out var c), "CreatePropertyCondition");
            conditions.Add(c);
        }

        if (selector.AutomationId is { Length: > 0 } aid)
        {
            Add(UIA.AutomationIdPropertyId, aid);
        }
        if (selector.ControlType is { Length: > 0 } ct && ControlTypeToId(ct) is { } ctid)
        {
            Add(UIA.ControlTypePropertyId, ctid);
        }
        if (selector.NameMatch == "exact" && selector.Name is { Length: > 0 } exactName)
        {
            Add(UIA.NamePropertyId, exactName);
        }
        if (selector.Class is { Length: > 0 } cls)
        {
            Add(UIA.ClassNamePropertyId, cls);
        }
        if (selector.FrameworkId is { Length: > 0 } fw)
        {
            Add(UIA.FrameworkIdPropertyId, fw);
        }

        if (conditions.Count == 0)
        {
            Check(automation.CreateTrueCondition(out var t), "CreateTrueCondition");
            return t;
        }
        if (conditions.Count == 1)
        {
            return conditions[0];
        }

        IUIAutomationCondition? combined = conditions[0];
        foreach (var c in conditions.Skip(1))
        {
            Check(automation.CreateAndCondition(combined!, c, out var next), "CreateAndCondition");
            combined = next;
        }
        return combined!;
    }

    private static bool NameMatches(Core.Selector.Selector selector, string? actual)
    {
        var candidates = new List<string>();
        if (selector.Name is { Length: > 0 } n)
        {
            candidates.Add(n);
        }
        if (selector.NameAliases is not null)
        {
            candidates.AddRange(selector.NameAliases.Values);
        }
        if (candidates.Count == 0)
        {
            return true;
        }
        actual ??= "";
        foreach (var candidate in candidates)
        {
            bool ok = selector.NameMatch switch
            {
                "contains" => actual.Contains(candidate, StringComparison.OrdinalIgnoreCase),
                "prefix" => actual.StartsWith(candidate, StringComparison.OrdinalIgnoreCase),
                "regex" => Regex.IsMatch(actual, candidate, RegexOptions.IgnoreCase),
                _ => actual.Equals(candidate, StringComparison.Ordinal),
            };
            if (ok)
            {
                return true;
            }
        }
        return false;
    }

    // ---- Internals: projection ----

    private static ElementNode Project(IUIAutomation automation, IUIAutomationElement element, string path)
    {
        var pid = ReadInt(element, UIA.ProcessIdPropertyId);
        var aid = ReadString(element, UIA.AutomationIdPropertyId);
        var name = ReadString(element, UIA.NamePropertyId) ?? "";
        var cls = ReadString(element, UIA.ClassNamePropertyId);
        var ctId = ReadInt(element, UIA.ControlTypePropertyId);
        var fw = ReadString(element, UIA.FrameworkIdPropertyId);
        var handle = ReadInt(element, UIA.NativeWindowHandlePropertyId) ?? 0;
        var rect = ReadRect(element);
        var patterns = DetectPatterns(element);
        var id = ElementFingerprint.Build(pid ?? 0, aid, name, cls,
            ControlTypeIdToName(ctId) ?? "Unknown", path);

        return new ElementNode(
            id, name,
            ControlTypeIdToName(ctId) ?? "Unknown",
            aid ?? "", cls ?? "", fw ?? "",
            pid, handle, rect,
            ReadBool(element, UIA.IsEnabledPropertyId) ?? true,
            ReadBool(element, UIA.IsOffscreenPropertyId) ?? false,
            ReadBool(element, UIA.HasKeyboardFocusPropertyId) ?? false,
            patterns, null);
    }

    private static string[] DetectPatterns(IUIAutomationElement element)
    {
        var probes = new (int Id, string Name)[]
        {
            (UIA.InvokePatternId, "invoke"),
            (UIA.ValuePatternId, "value"),
            (UIA.TogglePatternId, "toggle"),
            (UIA.SelectionItemPatternId, "selection_item"),
            (UIA.ExpandCollapsePatternId, "expand_collapse"),
            (UIA.ScrollPatternId, "scroll"),
            (UIA.ScrollItemPatternId, "scroll_item"),
            (UIA.TextPatternId, "text"),
            (UIA.WindowPatternId, "window"),
            (UIA.RangeValuePatternId, "range_value"),
            (UIA.LegacyIAccessiblePatternId, "legacy_accessible"),
        };
        var found = new List<string>();
        foreach (var (id, name) in probes)
        {
            if (element.GetCurrentPattern(id, out var p) == 0 && p != IntPtr.Zero)
            {
                found.Add(name);
                Marshal.Release(p);
            }
        }
        return found.ToArray();
    }

    private static void ApplyAction(IUIAutomation automation, IUIAutomationElement element, string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "invoke":
                Required<IUIAutomationInvokePattern>(element, UIA.InvokePatternId, "invoke").Invoke();
                break;
            case "toggle":
                Required<IUIAutomationTogglePattern>(element, UIA.TogglePatternId, "toggle").Toggle();
                break;
            case "select":
                Required<IUIAutomationSelectionItemPattern>(element, UIA.SelectionItemPatternId, "selection_item").Select();
                break;
            case "expand":
                Required<IUIAutomationExpandCollapsePattern>(element, UIA.ExpandCollapsePatternId, "expand_collapse").Expand();
                break;
            case "collapse":
                Required<IUIAutomationExpandCollapsePattern>(element, UIA.ExpandCollapsePatternId, "expand_collapse").Collapse();
                break;
            case "setfocus":
                element.SetFocus();
                break;
            case "scrollintoview":
                Required<IUIAutomationScrollItemPattern>(element, UIA.ScrollItemPatternId, "scroll_item").ScrollIntoView();
                break;
            default:
                throw new InvalidOperationException($"未知动作: {action}");
        }
    }

    private static T? GetPattern<T>(IUIAutomationElement element, int patternId) where T : class
    {
        if (element.GetCurrentPattern(patternId, out var p) != 0 || p == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            return (T)Marshal.GetTypedObjectForIUnknown(p, typeof(T));
        }
        finally
        {
            Marshal.Release(p);
        }
    }

    private static T Required<T>(IUIAutomationElement element, int patternId, string name) where T : class =>
        GetPattern<T>(element, patternId) ?? throw new PatternUnsupportedException(name);

    // ---- Internals: primitive reads ----

    private static int? ReadInt(IUIAutomationElement e, int id)
    {
        if (e.GetCurrentPropertyValue(id, out var v) != 0)
        {
            return null;
        }
        return v switch
        {
            int i => i,
            short s => (int)s,
            _ => null,
        };
    }

    private static bool? ReadBool(IUIAutomationElement e, int id)
    {
        if (e.GetCurrentPropertyValue(id, out var v) != 0)
        {
            return null;
        }
        return v as bool?;
    }

    private static string? ReadString(IUIAutomationElement e, int id)
    {
        if (e.GetCurrentPropertyValue(id, out var v) != 0)
        {
            return null;
        }
        return v as string;
    }

    private static int[] ReadRect(IUIAutomationElement e)
    {
        if (e.GetCurrentPropertyValue(UIA.BoundingRectanglePropertyId, out var v) != 0 || v is not double[] r || r.Length < 4)
        {
            return new[] { 0, 0, 0, 0 };
        }
        return new[]
        {
            (int)Math.Round(r[0]), (int)Math.Round(r[1]),
            (int)Math.Round(r[2]), (int)Math.Round(r[3]),
        };
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0)
        {
            throw new COMException($"{what} 失败: 0x{hr:X8}", hr);
        }
    }

    // ---- Control type maps ----

    private static readonly Dictionary<string, int> NameToId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["button"] = UIA.ButtonControlTypeId,
        ["checkbox"] = UIA.CheckBoxControlTypeId,
        ["combobox"] = UIA.ComboBoxControlTypeId,
        ["edit"] = UIA.EditControlTypeId,
        ["hyperlink"] = UIA.HyperlinkControlTypeId,
        ["image"] = UIA.ImageControlTypeId,
        ["list"] = UIA.ListControlTypeId,
        ["listitem"] = UIA.ListItemControlTypeId,
        ["menu"] = UIA.MenuControlTypeId,
        ["menubar"] = UIA.MenuBarControlTypeId,
        ["menuitem"] = UIA.MenuItemControlTypeId,
        ["progressbar"] = UIA.ProgressBarControlTypeId,
        ["radiobutton"] = UIA.RadioButtonControlTypeId,
        ["scrollbar"] = UIA.ScrollBarControlTypeId,
        ["slider"] = UIA.SliderControlTypeId,
        ["spinner"] = UIA.SpinnerControlTypeId,
        ["statusbar"] = UIA.StatusBarControlTypeId,
        ["tab"] = UIA.TabControlTypeId,
        ["tabitem"] = UIA.TabItemControlTypeId,
        ["text"] = UIA.TextControlTypeId,
        ["toolbar"] = UIA.ToolBarControlTypeId,
        ["tooltip"] = UIA.ToolTipControlTypeId,
        ["tree"] = UIA.TreeControlTypeId,
        ["treeitem"] = UIA.TreeItemControlTypeId,
        ["custom"] = UIA.CustomControlTypeId,
        ["group"] = UIA.GroupControlTypeId,
        ["thumb"] = UIA.ThumbControlTypeId,
        ["datagrid"] = UIA.DataGridControlTypeId,
        ["dataitem"] = UIA.DataItemControlTypeId,
        ["document"] = UIA.DocumentControlTypeId,
        ["splitbutton"] = UIA.SplitButtonControlTypeId,
        ["window"] = UIA.WindowControlTypeId,
        ["pane"] = UIA.PaneControlTypeId,
        ["header"] = UIA.HeaderControlTypeId,
        ["headeritem"] = UIA.HeaderItemControlTypeId,
        ["table"] = UIA.TableControlTypeId,
        ["titlebar"] = UIA.TitleBarControlTypeId,
        ["separator"] = UIA.SeparatorControlTypeId,
    };

    private static int? ControlTypeToId(string name) =>
        NameToId.TryGetValue(name, out var id) ? id : null;

    private static string? ControlTypeIdToName(int? id) =>
        id is { } i ? NameToId.FirstOrDefault(kv => kv.Value == i).Key : null;
}

// ---- Domain exceptions (mapped to protocol errors by capability layer) ----

public sealed class ElementNotFoundException : Exception
{
    public ElementNotFoundException() : base("选择器未命中任何元素")
    {
    }
}

public sealed class AmbiguousElementException : Exception
{
    public int Count { get; }
    public AmbiguousElementException(int count) : base($"{count} 个元素命中且未指定 index")
    {
        Count = count;
    }
}

public sealed class PatternUnsupportedException : Exception
{
    public PatternUnsupportedException(string pattern) : base($"控件不支持 Pattern: {pattern}")
    {
    }
}
