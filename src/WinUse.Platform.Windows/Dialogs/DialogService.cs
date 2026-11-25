using WinUse.Platform.Windows.Uia;

namespace WinUse.Platform.Windows.Dialogs;

/// <summary>对话框信息（07 §15）。</summary>
public sealed record DialogInfo(long Hwnd, string Title, string ClassName, string[] Buttons, bool Modal);

/// <summary>
/// 对话框检测/操作（07 §15、06 §7）。
/// 经典 Win32 对话框类 #32770；现代 UIA 模态窗口按"标题 + 对话框按钮组合"识别。
/// </summary>
public sealed class DialogService
{
    private readonly Windowing.WindowEnumerator _windows;
    private readonly UiaService _uia;

    private static readonly string[] DialogButtonNames =
    {
        "确定", "OK", "取消", "Cancel", "是", "Yes", "否", "No",
        "应用", "Apply", "关闭", "Close", "重试", "Retry", "忽略", "Ignore",
        "保存", "Save", "不要保存", "Don't Save",
    };

    public DialogService(Windowing.WindowEnumerator windows, UiaService uia)
    {
        _windows = windows;
        _uia = uia;
    }

    public async Task<IReadOnlyList<DialogInfo>> DetectAsync(CancellationToken ct = default)
    {
        var result = new List<DialogInfo>();
        var tops = _windows.Enumerate(visibleOnly: true);

        foreach (var w in tops)
        {
            var isClassic = w.ClassName == "#32770";
            string[] buttons = Array.Empty<string>();
            try
            {
                var tree = await _uia.TreeAsync(new IntPtr(w.Hwnd), maxDepth: 3, ct);
                buttons = CollectButtons(tree)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                // UIA 不可达（UIPI 等）→ 仍可凭类名判定
            }

            var looksLikeDialog = isClassic ||
                (buttons.Any(b => DialogButtonNames.Contains(b, StringComparer.OrdinalIgnoreCase)) &&
                 buttons.Intersect(new[] { "确定", "OK", "取消", "Cancel", "是", "Yes", "否", "No" })
                     .Any());

            if (looksLikeDialog)
            {
                result.Add(new DialogInfo(w.Hwnd, w.Title, w.ClassName, buttons, isClassic));
            }
        }
        return result;
    }

    public async Task<bool> ClickButtonAsync(long? hwnd, string buttonName,
        CancellationToken ct = default)
    {
        IntPtr target;
        if (hwnd is { } h)
        {
            target = new IntPtr(h);
        }
        else
        {
            var dialogs = await DetectAsync(ct);
            if (dialogs.Count == 0)
            {
                return false;
            }
            target = new IntPtr(dialogs[0].Hwnd);
        }

        var selector = new Core.Selector.Selector
        {
            ControlType = "button",
            Name = buttonName,
            NameMatch = "exact",
            NameAliases = ButtonAliases(buttonName),
            VisibleOnly = true,
        };
        await _uia.ActAsync(target, selector, "invoke", ct);
        return true;
    }

    private static IEnumerable<string> CollectButtons(ElementNode node)
    {
        if (node.ControlType is "button" or "splitbutton" && !string.IsNullOrEmpty(node.Name))
        {
            yield return node.Name;
        }
        if (node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                foreach (var b in CollectButtons(child))
                {
                    yield return b;
                }
            }
        }
    }

    /// <summary>常见按钮的中英别名（06 §7）。</summary>
    internal static Dictionary<string, string> ButtonAliases(string name)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["确定"] = "OK", ["ok"] = "确定",
            ["取消"] = "Cancel", ["cancel"] = "取消",
            ["是"] = "Yes", ["yes"] = "是",
            ["否"] = "No", ["no"] = "否",
            ["应用"] = "Apply", ["apply"] = "应用",
            ["关闭"] = "Close", ["close"] = "关闭",
            ["重试"] = "Retry", ["retry"] = "重试",
            ["保存"] = "Save", ["save"] = "保存",
        };
        var aliases = new Dictionary<string, string>();
        if (map.TryGetValue(name, out var alt))
        {
            aliases["alt"] = alt;
        }
        return aliases;
    }
}
