using Microsoft.Extensions.DependencyInjection;
using WinUse.Core.Audit;
using WinUse.Core.Capabilities;
using WinUse.Core.Dispatch;
using WinUse.Core.IdempotencyFlow;
using WinUse.Core.Policy;
using WinUse.Core.Scripts;
using WinUse.Platform.Windows.Capture;
using WinUse.Platform.Windows.Coordinates;
using WinUse.Platform.Windows.Dialogs;
using WinUse.Platform.Windows.Input;
using WinUse.Platform.Windows.System;
using WinUse.Platform.Windows.Uia;
using WinUse.Platform.Windows.Windowing;
using Capabilities = WinUse.Platform.Windows.Capabilities;

namespace WinUse.Host;

/// <summary>
/// 服务与能力注册（05 §7）：Core 策略/编排 + Windows 平台服务 + 52 个能力实现。
/// </summary>
internal static class ServiceConfiguration
{
    public static IServiceCollection AddWinUse(this IServiceCollection services)
    {
        AddCore(services);
        AddPlatformServices(services);
        AddCapabilities(services);
        return services;
    }

    private static void AddCore(IServiceCollection services)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUse");
        var auditDir = Path.Combine(dataDir, "audit");
        var scriptsDir = Path.Combine(dataDir, "scripts");

        services.AddSingleton(new PathPolicy());
        services.AddSingleton<RiskClassifier>();
        services.AddSingleton<IdempotencyStore>();
        services.AddSingleton(new AuditWriter(auditDir));
        services.AddSingleton<SessionState>();
        services.AddSingleton(new ScriptStore(scriptsDir));

        // M1 无头确认：需要确认的动作默认拒绝；托盘确认窗落地后替换。
        services.AddSingleton<IApprovalHandler, HeadlessApprovalHandler>();
        services.AddSingleton<ApprovalGate>();

        services.AddSingleton(sp =>
        {
            var descriptors = CapabilityCatalog.Load();
            var implementations = sp.GetServices<ICapability>();
            return new CapabilityRegistry(descriptors, implementations);
        });

        services.AddSingleton<CapabilityDispatcher>();
        services.AddSingleton<ScriptRunner>();
    }

    private static void AddPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<VirtualScreen>();
        services.AddSingleton(sp => new UiaIsolationRunner(maxConcurrent: 8));
        services.AddSingleton<UiaService>();

        services.AddSingleton<WindowEnumerator>();
        services.AddSingleton<WindowRefResolver>();
        services.AddSingleton<ForegroundService>();
        services.AddSingleton<WindowPlacer>();

        services.AddSingleton<MouseInjector>();
        services.AddSingleton<KeyboardInjector>();
        services.AddSingleton<ClipboardGuard>();
        services.AddSingleton<TextInputStrategy>();

        services.AddSingleton<ScreenCapturer>();

        services.AddSingleton<IntegrityLevelService>();
        services.AddSingleton<EnvironmentProbe>();
        services.AddSingleton<ProcessService>();

        services.AddSingleton<DialogService>();
    }

    private static void AddCapabilities(IServiceCollection services)
    {
        foreach (var type in CapabilityTypes)
        {
            services.AddSingleton(typeof(ICapability), type);
        }
    }

    /// <summary>
    /// 全部 52 个已实现能力（46 core + 6 advanced），顺序与 docs/07 §0.7 对应。
    /// </summary>
    private static readonly Type[] CapabilityTypes =
    {
        // session / system / display
        typeof(Capabilities.SessionHello),
        typeof(Capabilities.SessionCapabilities),
        typeof(Capabilities.SessionPreflight),
        typeof(Capabilities.SessionResume),
        typeof(Capabilities.SystemInfo),
        typeof(Capabilities.DisplayList),

        // process / window
        typeof(Capabilities.ProcessStart),
        typeof(Capabilities.ProcessList),
        typeof(Capabilities.WindowList),
        typeof(Capabilities.WindowFind),
        typeof(Capabilities.WindowFocus),
        typeof(Capabilities.WindowClose),

        // element
        typeof(Capabilities.ElementFind),
        typeof(Capabilities.ElementTree),
        typeof(Capabilities.ElementTextGet),
        typeof(Capabilities.ElementInvoke),
        typeof(Capabilities.ElementSetValue),
        typeof(Capabilities.ElementToggle),
        typeof(Capabilities.ElementScroll),

        // input / clipboard
        typeof(Capabilities.MouseMove),
        typeof(Capabilities.MouseClick),
        typeof(Capabilities.MouseScroll),
        typeof(Capabilities.KeyboardType),
        typeof(Capabilities.KeyboardPress),
        typeof(Capabilities.KeyboardHotkey),
        typeof(Capabilities.ClipboardGet),
        typeof(Capabilities.ClipboardSet),

        // screen / ocr
        typeof(Capabilities.ScreenCapture),
        typeof(Capabilities.ScreenAnnotate),
        typeof(Capabilities.OcrRecognize),

        // file
        typeof(Capabilities.FileList),
        typeof(Capabilities.FileStat),
        typeof(Capabilities.FileExists),
        typeof(Capabilities.FileRead),
        typeof(Capabilities.FileWrite),
        typeof(Capabilities.FileCopy),
        typeof(Capabilities.FileMove),
        typeof(Capabilities.FileMkdir),
        typeof(Capabilities.FileDelete),

        // dialog / filedialog
        typeof(Capabilities.DialogDetect),
        typeof(Capabilities.DialogButtonClick),
        typeof(Capabilities.FileDialogSetPath),
        typeof(Capabilities.FileDialogConfirm),

        // wait / macro
        typeof(Capabilities.WaitFor),
        typeof(Capabilities.WaitIdle),
        typeof(Capabilities.MacroList),
        typeof(Capabilities.MacroRun),

        // advanced
        typeof(Capabilities.MouseDrag),
        typeof(Capabilities.ProcessTerminate),
        typeof(Capabilities.RegistryGet),
        typeof(Capabilities.ShellExec),
        typeof(Capabilities.SystemPower),
    };
}
