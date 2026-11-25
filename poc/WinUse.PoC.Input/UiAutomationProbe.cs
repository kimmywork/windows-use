// UiAutomationProbe.cs — FlaUI(UIA3) 访问层。
//
// 用途：
//   1) 方案 C：对 notepad 的文档元素使用 ValuePattern.SetValue 直接写入；
//   2) 读回校验：ValuePattern.Current.Value / LegacyIAccessible.Value 作为 WM_GETTEXT 的备选；
//   3) 收尾容错：若关闭时弹出"是否保存"对话框，用 UIA Invoke 点"不保存"（不发送任何按键）。
//
// 安全性：所有操作对象均限定在本程序自行启动的 notepad 进程的窗口树内。

using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace WinUse.PoC.Input;

internal sealed class UiAutomationProbe : IDisposable
{
    private UIA3Automation? _automation;
    private bool _disposed;

    /// <summary>找到的支持 ValuePattern 的文档元素（找不到则为 null）。</summary>
    public AutomationElement? Document { get; private set; }

    /// <summary>定位过程说明，用于报告。</summary>
    public string Detail { get; private set; } = "";

    /// <summary>该元素支持 ValuePattern 与否。</summary>
    public bool ValuePatternSupported { get; private set; }

    /// <summary>
    /// 在指定主窗口下递归寻找第一个支持 ValuePattern 的元素（深度受限，避免整树遍历开销）。
    /// </summary>
    public static UiAutomationProbe Attach(IntPtr mainHwnd, int maxDepth = 8)
    {
        var probe = new UiAutomationProbe();
        try
        {
            probe._automation = new UIA3Automation();
            AutomationElement root = probe._automation.FromHandle(mainHwnd);
            probe.Document = probe.FindValueCapable(root, maxDepth, out int visited);
            if (probe.Document == null)
            {
                probe.Detail = $"未找到支持 ValuePattern 的元素（遍历 {visited} 个节点，深度上限 {maxDepth}）";
            }
            else
            {
                probe.ValuePatternSupported = true;
                probe.Detail = $"找到文档元素：ControlType={SafeValue(() => probe.Document!.ControlType.ToString(), "?")}，" +
                               $"Class={SafeValue(() => probe.Document!.ClassName, "?")}（遍历 {visited} 个节点）";
            }
        }
        catch (Exception ex)
        {
            probe.Detail = "UIA 初始化失败：" + ex.GetType().Name + "：" + ex.Message;
        }
        return probe;
    }

    private AutomationElement? FindValueCapable(AutomationElement node, int maxDepth, out int visited)
    {
        visited = 0;
        return Walk(node, maxDepth, ref visited);
    }

    private AutomationElement? Walk(AutomationElement node, int depth, ref int visited)
    {
        visited++;
        try
        {
            if (ValueOfNode(node) != null)
            {
                return node;
            }
        }
        catch
        {
            // 个别元素属性访问抛异常时跳过
        }

        if (depth <= 0) return null;

        AutomationElement[] children;
        try
        {
            children = node.FindAllChildren();
        }
        catch
        {
            return null;
        }

        foreach (AutomationElement child in children)
        {
            AutomationElement? found = Walk(child, depth - 1, ref visited);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>读取元素值：优先 ValuePattern.Current.Value，回落 LegacyIAccessible.Value。</summary>
    private static string? ValueOfNode(AutomationElement node)
    {
        try
        {
            object? v = ReadValuePattern(node);
            if (v != null) return v as string;
        }
        catch
        {
            // 继续尝试 LegacyIAccessible
        }

        try
        {
            object? legacy = AccessLegacy(node);
            if (legacy is string ls) return ls;
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    private static bool HasValuePattern(AutomationElement node)
    {
        try
        {
            return ReflectPattern(node, "Value") != null;
        }
        catch
        {
            return false;
        }
    }

    public string ReadValueOrDefault(string fallback = "")
    {
        if (Document == null) return fallback;
        try
        {
            return ValueOfNode(Document) ?? fallback;
        }
        catch (Exception ex)
        {
            Console.WriteLine("    [UIA] 读取失败：" + ex.GetType().Name + "：" + ex.Message);
            return fallback;
        }
    }

    /// <summary>方案 C：直接 SetValue。</summary>
    public bool TrySetValue(string text, out string detail)
    {
        detail = "";
        if (Document == null)
        {
            detail = "无可用文档元素";
            return false;
        }

        try
        {
            object? patternObj = ReflectPattern(Document, "Value") ?? throw new InvalidOperationException("ValuePattern 不可用");
            InvokeMethod(patternObj, "SetValue", text);
            detail = "ValuePattern.SetValue 已调用";
            return true;
        }
        catch (Exception ex)
        {
            detail = "SetValue 失败：" + ex.GetType().Name + "：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 收尾容错：在 notepad 进程弹出的对话框里点"不保存/Don't save"。
    /// 只用 UIA Invoke，不发送任何按键。
    /// </summary>
    public bool TryInvokeDontSave(IntPtr dialogHwnd, out string detail)
    {
        detail = "";
        if (_automation == null || dialogHwnd == IntPtr.Zero)
        {
            detail = "无可用的 automation 或对话框句柄";
            return false;
        }

        try
        {
            AutomationElement dialog = _automation.FromHandle(dialogHwnd);
            string[] wanted =
            {
                "不保存", "不要保存", "否", DonTSaveEnglish()
            };
            var found = new List<AutomationElement>();
            CollectButtons(dialog, found, depth: 5);

            foreach (AutomationElement button in found)
            {
                string name = SafeValue(() => button.Name ?? "", "");
                foreach (string key in wanted)
                {
                    if (!string.IsNullOrEmpty(key) && name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        object? invoke = ReflectPattern(button, "Invoke");
                        if (invoke == null)
                        {
                            detail = $"按钮[{name}]不支持 InvokePattern";
                            return false;
                        }
                        InvokeMethod(invoke, "Invoke");
                        detail = $"已点按钮[{name}]";
                        return true;
                    }
                }
            }

            detail = found.Count == 0
                ? "对话框内未发现按钮元素"
                : "未匹配到“不保存”按钮：" + string.Join(" | ", found.Select(b => SafeValue(() => b.Name ?? "", "?")));
            return false;
        }
        catch (Exception ex)
        {
            detail = "UIA 打开对话框失败：" + ex.GetType().Name + "：" + ex.Message;
            return false;
        }
    }

    private static string DonTSaveEnglish() => "Don't save";

    private void CollectButtons(AutomationElement node, List<AutomationElement> sink, int depth)
    {
        if (depth <= 0) return;
        AutomationElement[] children;
        try
        {
            children = node.FindAllChildren();
        }
        catch
        {
            return;
        }
        foreach (AutomationElement child in children)
        {
            string ct = SafeValue(() => child.ControlType.ToString(), "");
            if (ct.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sink.Add(child);
            }
            CollectButtons(child, sink, depth - 1);
        }
    }

    // -----------------------------------------------------------------
    // 反射访问层
    // 说明：FlaUI 5.0 的模式容器istory封装(IPattern<T>)在不同小版本间成员略有差异，
    //       这里统一走反射，避免把整个 PoC 绑死在某个具体 API 形态上。
    // -----------------------------------------------------------------
    private static object? ReflectPattern(AutomationElement element, string name)
    {
        PropertyInfo? property = element.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property != null)
        {
            object? value = property.GetValue(element);
            if (value != null) return value;
        }

        // element.Patterns.<Name>.Pattern
        object? patterns = element.GetType()
            .GetProperty("Patterns", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(element);
        if (patterns == null) return null;

        object? container = patterns.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(patterns);
        if (container == null) return null;

        bool supported = true;
        PropertyInfo? supportedProp = container.GetType().GetProperty("IsSupported");
        if (supportedProp != null)
        {
            supported = supportedProp.GetValue(container) is bool b && b;
        }
        if (!supported) return null;

        return container.GetType()
            .GetProperty("Pattern", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(container);
    }

    private static object? ReadValuePattern(AutomationElement element)
    {
        object? pattern = ReflectPattern(element, "Value");
        return pattern?.GetType().GetProperty("Value")?.GetValue(pattern);
    }

    private static object? AccessLegacy(AutomationElement element)
    {
        object? pattern = ReflectPattern(element, "LegacyIAccessible");
        return pattern?.GetType().GetProperty("Value")?.GetValue(pattern);
    }

    private static void InvokeMethod(object target, string methodName, params object[] args)
    {
        MethodInfo? method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance,
            null,
            args.Select(a => a.GetType()).ToArray(),
            null);

        if (method == null)
        {
            method = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length)
                ?? throw new InvalidOperationException($"未找到方法 {methodName}/{args.Length} 参数");
        }

        method.Invoke(target, args);
    }

    private static T SafeValue<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _automation?.Dispose();
        }
        catch
        {
            // 释放失败不影响主流程
        }
        _automation = null;
    }
}
