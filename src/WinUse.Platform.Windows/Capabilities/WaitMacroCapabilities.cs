using WinUse.Core.Capabilities;
using WinUse.Core.Protocol;
using WinUse.Core.Scripts;
using WinUse.Core.Selector;
using WinUse.Platform.Windows.Dialogs;
using WinUse.Platform.Windows.System;
using WinUse.Platform.Windows.Uia;

namespace WinUse.Platform.Windows.Capabilities;

// ---- wait.* ----

internal sealed class WaitFor : WindowsCapability
{
    public override string Id => "wait.for";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var kind = (context.Args.GetString("kind") ?? "element").ToLowerInvariant();
        var timeoutMs = context.Args.GetInt32("timeout_ms") ?? 10000;
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            bool ready;
            switch (kind)
            {
                case "element":
                {
                    var selector = SelectorParser.Parse(context.Args)
                                   ?? throw new MissingParameterException("selector");
                    var scope = ScopeResolver.Resolve(context, selector);
                    var found = context.GetRequiredService<UiaService>()
                        .FindAsync(scope, selector, maxResults: 1).GetAwaiter().GetResult();
                    ready = found.Total > 0;
                    break;
                }
                case "window":
                {
                    var title = context.Args.RequireString("title");
                    ready = context.GetRequiredService<Windowing.WindowEnumerator>()
                        .Enumerate(titleContains: title, visibleOnly: true).Count > 0;
                    break;
                }
                case "file":
                {
                    var path = context.Args.RequireString("path");
                    ready = File.Exists(path) || Directory.Exists(path);
                    break;
                }
                case "process":
                {
                    var name = context.Args.RequireString("name");
                    ready = context.GetRequiredService<ProcessService>()
                        .List(name).Count > 0;
                    break;
                }
                default:
                    throw new ArgumentException($"未知 wait 目标类型: {kind}");
            }

            if (ready)
            {
                return Ok(new { waited = true, kind });
            }
            if (Environment.TickCount64 > deadline)
            {
                return Fail("TIMEOUT", $"{kind} 在 {timeoutMs}ms 内未出现",
                    new { kind, timeout_ms = timeoutMs });
            }
            Thread.Sleep(400);
        }
    }
}

internal sealed class WaitIdle : WindowsCapability
{
    public override string Id => "wait.idle";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var threshold = context.Args.GetInt32("threshold_ms") ?? 2000;
        var timeoutMs = context.Args.GetInt32("timeout_ms") ?? 30000;
        var probe = context.GetRequiredService<EnvironmentProbe>();
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            var idle = probe.Snapshot().LastInputMsAgo;
            if (idle >= threshold)
            {
                return Ok(new { idle_ms = idle });
            }
            if (Environment.TickCount64 > deadline)
            {
                return Fail("TIMEOUT", $"用户输入未在 {timeoutMs}ms 内空闲 {threshold}ms");
            }
            Thread.Sleep(300);
        }
    }
}

// ---- macro.* ----

internal sealed class MacroList : WindowsCapability
{
    public override string Id => "macro.list";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var store = context.GetRequiredService<ScriptStore>();
        var userScripts = store.List(out var loadErrors);

        var builtin = BuiltInMacros.All.Select(m => new
        {
            name = m.Id,
            title = m.Name,
            description = m.Description,
            builtin = true,
            steps = (int?)null,
            on_error = (string?)null,
        });

        var custom = userScripts.Select(s => new
        {
            name = s.Name,
            title = s.Title,
            description = s.Description,
            builtin = false,
            steps = (int?)s.Steps.Count,
            on_error = (string?)s.OnError,
        });

        var macros = builtin.Concat(custom).ToArray();
        return Ok(new
        {
            count = macros.Length,
            macros,
            load_errors = loadErrors.Select(e => new { file = e.File, error = e.Error }),
        });
    }
}

internal sealed class MacroRun : WindowsCapability, IDynamicRiskCapability
{
    public override string Id => "macro.run";

    /// <summary>
    /// 动态风险（07 §20.2：取宏内最高风险动作）。
    /// 用户脚本按各步描述符静态风险取最大值；内置宏与无法解析的情况保守取 R2。
    /// </summary>
    public Task<RiskLevel> AssessRiskAsync(CapabilityContext context, CancellationToken cancellationToken)
    {
        var name = context.Args.GetString("name");
        if (name is not null)
        {
            var store = context.GetRequiredService<ScriptStore>();
            if (store.Exists(name))
            {
                var script = store.Load(name);
                var registry = context.GetRequiredService<CapabilityRegistry>();
                var max = RiskLevel.R0;
                foreach (var step in script.Steps)
                {
                    if (registry.TryGetDescriptor(step.Capability, out var descriptor))
                    {
                        max = (RiskLevel)Math.Max((int)max, (int)descriptor.Risk);
                    }
                    else
                    {
                        return Task.FromResult(RiskLevel.R2);
                    }
                }
                return Task.FromResult(max);
            }
        }
        return Task.FromResult(RiskLevel.R2);
    }

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var name = context.Args.RequireString("name");

        // 用户脚本优先；未命中再走内置宏（07 §20：自定义宏与内置宏并列）。
        var store = context.GetRequiredService<ScriptStore>();
        if (store.Exists(name))
        {
            var script = store.Load(name);
            var outcome = context.GetRequiredService<ScriptRunner>()
                .RunAsync(script, CancellationToken.None).GetAwaiter().GetResult();
            return Ok(outcome);
        }

        var macro = BuiltInMacros.All.FirstOrDefault(m =>
            m.Id.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"未知宏: {name}（可用 macro.list 查询）");

        var executor = new MacroExecutor(context);
        var result = executor.Run(macro.Id);
        return Ok(result);
    }
}

/// <summary>内置宏执行器：组合既有能力（07 §20.2）。</summary>
internal sealed class MacroExecutor
{
    private readonly CapabilityContext _context;

    public MacroExecutor(CapabilityContext context)
    {
        _context = context;
    }

    public object Run(string name) => name.ToLowerInvariant() switch
    {
        "open_app_and_wait" => OpenAppAndWait(),
        "save_file_as" => SaveFileAs(),
        "fill_form" => FillForm(),
        "close_all_dialogs" => CloseAllDialogs(),
        "wait_ready" => WaitReady(),
        _ => throw new ArgumentException($"未知宏: {name}"),
    };

    private object OpenAppAndWait()
    {
        var path = _context.Args.RequireString("path");
        var pid = _context.GetRequiredService<ProcessService>()
            .Start(path, _context.Args.GetString("arguments"),
                _context.Args.GetString("working_directory"));
        var title = _context.Args.GetString("window_title");
        var deadline = Environment.TickCount64 + (_context.Args.GetInt32("timeout_ms") ?? 15000);
        while (Environment.TickCount64 < deadline)
        {
            var windows = _context.GetRequiredService<Windowing.WindowEnumerator>()
                .Enumerate(processId: pid, visibleOnly: true);
            if (windows.Count > 0 && (title is null ||
                windows.Any(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase))))
            {
                return new { pid, window = windows[0] };
            }
            Thread.Sleep(400);
        }
        throw new CapabilityResultException("TIMEOUT", "应用启动后窗口未出现");
    }

    private object SaveFileAs()
    {
        var path = _context.Args.RequireString("path");
        var dialogs = _context.GetRequiredService<DialogService>().DetectAsync()
            .GetAwaiter().GetResult();
        if (dialogs.Count == 0)
        {
            throw new CapabilityResultException("ELEMENT_NOT_FOUND",
                "未检测到另存为对话框，请先触发保存");
        }
        var hwnd = new IntPtr(dialogs[0].Hwnd);
        var uia = _context.GetRequiredService<UiaService>();
        var box = new Selector
        {
            ControlType = "combobox",
            Name = "文件名(N):",
            NameMatch = "prefix",
            NameAliases = new Dictionary<string, string> { ["en"] = "File name:" },
        };
        try
        {
            uia.SetValueAsync(hwnd, box, path).GetAwaiter().GetResult();
        }
        catch (ElementNotFoundException)
        {
            uia.SetValueAsync(hwnd, box with { ControlType = "edit" }, path)
                .GetAwaiter().GetResult();
        }

        var confirm = new Selector
        {
            ControlType = "button",
            Name = "保存(S):",
            NameMatch = "prefix",
            NameAliases = new Dictionary<string, string> { ["en"] = "Save" },
        };
        uia.ActAsync(hwnd, confirm, "invoke").GetAwaiter().GetResult();
        return new { saved = true, path };
    }

    private object FillForm()
    {
        var fields = _context.Args.Raw("fields")
                     ?? throw new MissingParameterException("fields");
        if (fields.ValueKind != global::System.Text.Json.JsonValueKind.Array)
        {
            throw new ArgumentException("fields 必须为数组");
        }
        var uia = _context.GetRequiredService<UiaService>();
        var scope = ScopeResolver.Resolve(_context, null);
        var done = new List<object>();
        foreach (var field in fields.EnumerateArray())
        {
            var selector = SelectorParser.Parse(field.TryGetProperty("selector", out var s) ? s : field)
                           ?? throw new ArgumentException("字段缺少 selector");
            var value = field.TryGetProperty("value", out var v) ? v.GetString() : null;
            uia.SetValueAsync(scope, selector, value ?? "").GetAwaiter().GetResult();
            done.Add(new { name = selector.Name, filled = true });
        }
        return new { filled = done.Count, fields = done };
    }

    private object CloseAllDialogs()
    {
        var service = _context.GetRequiredService<DialogService>();
        var closed = 0;
        for (var i = 0; i < 10; i++)
        {
            var dialogs = service.DetectAsync().GetAwaiter().GetResult();
            if (dialogs.Count == 0)
            {
                break;
            }
            // 优先点取消；点不到则发 WM_CLOSE。
            var hwnd = new IntPtr(dialogs[0].Hwnd);
            try
            {
                var cancel = new Selector
                {
                    ControlType = "button",
                    Name = "取消",
                    NameMatch = "exact",
                    NameAliases = new Dictionary<string, string> { ["en"] = "Cancel" },
                };
                _context.GetRequiredService<UiaService>()
                    .ActAsync(hwnd, cancel, "invoke").GetAwaiter().GetResult();
            }
            catch
            {
                _context.GetRequiredService<Windowing.ForegroundService>().Close(hwnd);
            }
            closed++;
            Thread.Sleep(300);
        }
        return new { closed, remaining = service.DetectAsync().GetAwaiter().GetResult().Count };
    }

    private object WaitReady()
    {
        var deadline = Environment.TickCount64 + (_context.Args.GetInt32("timeout_ms") ?? 20000);
        while (Environment.TickCount64 < deadline)
        {
            var env = _context.GetRequiredService<EnvironmentProbe>().Snapshot();
            var dialogs = _context.GetRequiredService<DialogService>()
                .DetectAsync().GetAwaiter().GetResult().Count;
            if (env.Interactive && dialogs == 0)
            {
                return new { ready = true };
            }
            Thread.Sleep(400);
        }
        throw new CapabilityResultException("TIMEOUT", "环境未就绪（仍有对话框或会话不可交互）");
    }
}

internal static class BuiltInMacros
{
    public sealed record MacroInfo(string Id, string Name, string Description, string[] Params);

    public static readonly MacroInfo[] All =
    {
        new("open_app_and_wait", "启动应用并等待窗口",
            "启动进程并轮询其顶层窗口出现", new[] { "path", "arguments", "window_title", "timeout_ms" }),
        new("save_file_as", "另存为",
            "在已打开的另存为对话框中填写路径并确认", new[] { "path" }),
        new("fill_form", "填写表单",
            "按 fields 顺序逐个 set_value", new[] { "fields" }),
        new("close_all_dialogs", "关闭全部对话框",
            "循环检测对话框并取消/关闭", Array.Empty<string>()),
        new("wait_ready", "等待就绪",
            "等待会话可交互且无对话框", new[] { "timeout_ms" }),
    };
}
