using global::System.Diagnostics;
using Microsoft.Win32;
using WinUse.Core.Capabilities;
using WinUse.Platform.Windows.Input;
using WinUse.Platform.Windows.System;

namespace WinUse.Platform.Windows.Capabilities;

internal sealed class MouseDrag : WindowsCapability
{
    public override string Id => "mouse.drag";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var args = context.Args;
        var fromX = args.RequireInt32("from_x");
        var fromY = args.RequireInt32("from_y");
        var toX = args.RequireInt32("to_x");
        var toY = args.RequireInt32("to_y");
        context.GetRequiredService<MouseInjector>()
            .Drag(fromX, fromY, toX, toY, args.GetString("button") ?? "left");
        return Ok(new { from = new[] { fromX, fromY }, to = new[] { toX, toY } });
    }
}

internal sealed class ProcessTerminate : WindowsCapability
{
    public override string Id => "process.terminate";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var pid = context.Args.RequireInt32("pid");
        var ok = context.GetRequiredService<ProcessService>().Terminate(pid);
        if (!ok)
        {
            return Fail("ELEMENT_NOT_FOUND", $"进程 {pid} 不存在或未能终止");
        }
        return Ok(new { pid, terminated = true });
    }
}

internal sealed class RegistryGet : WindowsCapability
{
    public override string Id => "registry.get";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var key = context.Args.RequireString("key");
        var name = context.Args.GetString("name") ?? "";
        var value = Registry.GetValue(key, name, null);
        if (value is null)
        {
            return Fail("ELEMENT_NOT_FOUND",
                $"注册表值不存在: {key} ! {name}");
        }
        return Ok(new
        {
            key,
            name,
            value,
            type = value.GetType().Name,
        });
    }
}

internal sealed class ShellExec : WindowsCapability
{
    public override string Id => "shell.exec";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var command = context.Args.RequireString("command");
        var argsText = context.Args.GetString("arguments");
        var workingDir = context.Args.GetString("working_directory");

        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = true, // shell 语义：解析 PATH/关联
        };
        if (argsText is { Length: > 0 })
        {
            psi.Arguments = argsText;
        }
        if (workingDir is { Length: > 0 })
        {
            psi.WorkingDirectory = workingDir;
        }
        var process = Process.Start(psi);
        return Ok(new { pid = process?.Id });
    }
}

internal sealed class SystemPower : WindowsCapability
{
    public override string Id => "system.power";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var action = context.Args.GetString("action");
        if (action is null)
        {
            // 查询：电源/电池状态。
            var charge = SystemInformation.PowerStatus;
            return Ok(new
            {
                battery = new
                {
                    online = charge.PowerLineStatus.ToString(),
                    charge_percent = (int)(charge.BatteryLifePercent * 100),
                    charging = charge.BatteryChargeStatus.ToString(),
                },
            });
        }

        // 动作：shutdown.exe（延迟可配，默认给 5 秒缓冲以便反悔）。
        var delay = context.Args.GetInt32("delay_seconds") ?? 5;
        var flag = action.ToLowerInvariant() switch
        {
            "shutdown" => "/s",
            "restart" => "/r",
            "logoff" => "/l",
            _ => throw new ArgumentException($"未知电源动作: {action}"),
        };
        var psi = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = $"{flag} /t {delay} /hybrid /f",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
        return Ok(new { action, scheduled = true, delay_seconds = delay });
    }
}
