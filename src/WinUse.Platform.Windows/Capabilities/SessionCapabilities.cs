using Microsoft.Win32;
using WinUse.Core.Capabilities;
using WinUse.Platform.Windows.Coordinates;
using WinUse.Platform.Windows.System;

namespace WinUse.Platform.Windows.Capabilities;

// ---- session.* ----

internal sealed class SessionHello : WindowsCapability
{
    public override string Id => "session.hello";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var declared = context.Args.GetString("declare_tier");
        var tiers = new List<string> { "core" };
        if (declared is { Length: > 0 })
        {
            foreach (var t in declared.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!tiers.Contains(t, StringComparer.OrdinalIgnoreCase))
                {
                    tiers.Add(t.ToLowerInvariant());
                }
            }
        }

        return Ok(new
        {
            protocol = new
            {
                name = "winuse",
                version = "1.0",
                jsonrpc = "2.0",
            },
            server = new
            {
                name = "WinUse",
                version = "0.1.0",
            },
            session_id = context.SessionId,
            declared_tiers = tiers,
            capabilities_endpoint = "/v1/invoke",
        });
    }
}

internal sealed class SessionCapabilities : WindowsCapability
{
    public override string Id => "session.capabilities";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var registry = context.GetRequiredService<CapabilityRegistry>();
        var visible = registry.ListVisible(context.GrantedTiers,
            context.Args.GetString("domain"));
        return Ok(new
        {
            count = visible.Length,
            capabilities = visible.Select(d => new
            {
                id = d.Id,
                since = d.Since,
                tier = d.Tier.ToString().ToLowerInvariant(),
                risk = d.Risk.ToString(),
                idempotency = d.Idempotency.ToString().ToLowerInvariant(),
                layers = d.Layers.Select(l => l.ToString().ToLowerInvariant()),
                description = d.Description,
            }),
        });
    }
}

internal sealed class SessionPreflight : WindowsCapability
{
    public override string Id => "session.preflight";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var probe = context.GetRequiredService<EnvironmentProbe>();
        var state = probe.Snapshot();
        var il = context.GetRequiredService<IntegrityLevelService>().GetOwn();

        return Ok(new
        {
            interactive = state.Interactive,
            session_locked = state.SessionLocked,
            secure_desktop = state.SecureDesktopActive,
            screensaver = state.ScreensaverActive,
            remote_session = state.IsRemoteSession,
            own_integrity_level = il.ToString(),
            last_input_ms_ago = state.LastInputMsAgo,
            ready = state.Interactive && !state.SecureDesktopActive,
        });
    }
}

internal sealed class SessionResume : WindowsCapability
{
    public override string Id => "session.resume";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        // M1：无可恢复的挂起动作（事务/挂起状态在后续版本落地）。
        return Ok(new
        {
            session_id = context.SessionId,
            pending_actions = Array.Empty<object>(),
            resumed = false,
        });
    }
}

// ---- system.* / display.* ----

internal sealed class SystemInfo : WindowsCapability
{
    public override string Id => "system.info";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var il = context.GetRequiredService<IntegrityLevelService>().GetOwn();
        var os = Environment.OSVersion;

        return Ok(new
        {
            os = new
            {
                platform = "Windows",
                version = os.Version.ToString(),
                os.Version.Major,
                os.Version.Minor,
                os.Version.Build,
                release = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "DisplayVersion", null)?.ToString(),
            },
            machine = Environment.MachineName,
            user = Environment.UserName,
            user_domain = Environment.UserDomainName,
            integrity_level = il.ToString(),
            processor_count = Environment.ProcessorCount,
            is_64bit_os = Environment.Is64BitOperatingSystem,
            clr_version = Environment.Version.ToString(),
        });
    }
}

internal sealed class DisplayList : WindowsCapability
{
    public override string Id => "display.list";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var snap = context.GetRequiredService<VirtualScreen>().Capture();
        return Ok(new
        {
            virtual_screen = new
            {
                origin = new[] { snap.OriginX, snap.OriginY },
                size = new[] { snap.Width, snap.Height },
                origin_is_negative = snap.OriginIsNegative,
            },
            monitors = snap.Monitors.Select(m => new
            {
                id = m.Id,
                bounds = new[] { m.X, m.Y, m.Width, m.Height },
                work_area = new[] { m.WorkX, m.WorkY, m.WorkWidth, m.WorkHeight },
                primary = m.Primary,
                dpi = m.Dpi,
                scale = m.Scale,
                device = m.DeviceName,
            }),
            count = snap.Monitors.Length,
        });
    }
}
