// ResultModel.cs — 第二轮 B 的结构化结果模型。
// 多次运行（--ime cn / --ime en / --awareness pmv2 / --awareness unaware）的结果
// 会合并写入同一个 poc/poc-input-results.json 的 runs[] 数组中。

using System.Text.Json.Serialization;

namespace WinUse.PoC.Input;

internal sealed class ResultStore
{
    public int SchemaVersion { get; set; } = 1;
    public List<InputRun> Runs { get; set; } = new();
}

internal sealed class InputRun
{
    public string RunId { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public bool Is64BitProcess { get; set; }
    public string CommandLine { get; set; } = "";
    public string ImeArgument { get; set; } = "";
    public string Phases { get; set; } = "";
    public string RequestedAwareness { get; set; } = "";

    public EnvInfo Environment { get; set; } = new();
    public DpiState DefaultAwareness { get; set; } = new();
    public DpiState AppliedAwareness { get; set; } = new();
    public MonitorTopology Topology { get; set; } = new();
    public NotepadTarget Target { get; set; } = new();

    public List<InputCase> Cases { get; set; } = new();
    public List<CursorProbe> CursorProbes { get; set; } = new();

    public CleanupInfo Cleanup { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

internal sealed class EnvInfo
{
    public string InitialForegroundHwnd { get; set; } = "";
    public string InitialForegroundClass { get; set; } = "";
    public string InitialForegroundTitle { get; set; } = "";
    public int[] InitialCursor { get; set; } = Array.Empty<int>();
    public uint NotepadWindowDpi { get; set; }
    public uint SystemDpi { get; set; }
    public int InputLocaleStart { get; set; }
    public string InputLocaleStartName { get; set; } = "";
}

internal sealed class DpiState
{
    /// <summary>组别标签：default（未声明）/ pmv2 / unaware / system / pmv1</summary>
    public string Group { get; set; } = "";
    public string ThreadLabel { get; set; } = "";
    public int ProcessAwarenessValue { get; set; } = -999;
    public string ProcessAwarenessName { get; set; } = "";
    public int ThreadAwarenessValue { get; set; } = -999;
    public bool SetApiCalled { get; set; }
    public bool SetApiSucceeded { get; set; }
    public int SetApiWin32Error { get; set; }
    public string SetApiErrorText { get; set; } = "";
    public int VirtualX { get; set; }
    public int VirtualY { get; set; }
    public int VirtualWidth { get; set; }
    public int VirtualHeight { get; set; }
    public uint SystemDpi { get; set; }
    public uint NotepadDpi { get; set; }
}

internal sealed class MonitorRecord
{
    public int Index { get; set; }
    public bool IsPrimary { get; set; }
    public string Device { get; set; } = "";
    public int[] MonitorRect { get; set; } = Array.Empty<int>();
    public int[] WorkRect { get; set; } = Array.Empty<int>();
    public int EffectiveDpiX { get; set; }
    public int EffectiveDpiY { get; set; }
    public double ScalePercent { get; set; }
}

internal sealed class MonitorTopology
{
    public int MonitorCount { get; set; }
    public int SmCMonitors { get; set; }
    public int VirtualX { get; set; }
    public int VirtualY { get; set; }
    public int VirtualWidth { get; set; }
    public int VirtualHeight { get; set; }
    public bool NegativeOrigin { get; set; }
    public bool EligibleForPhase2 { get; set; }
    public string SkipReason { get; set; } = "";
    public List<MonitorRecord> Monitors { get; set; } = new();
}

internal sealed class NotepadTarget
{
    public int Pid { get; set; }
    public string MainWindowHwnd { get; set; } = "";
    public string MainWindowTitle { get; set; } = "";
    public string MainWindowClass { get; set; } = "";
    public string EditHwnd { get; set; } = "";
    public string EditClass { get; set; } = "";
    public int[] MainWindowRect { get; set; } = Array.Empty<int>();
    public bool SelfStarted { get; set; }
    public string LaunchDetail { get; set; } = "";
    public bool UiAutomationDocumentFound { get; set; } = false;
}

internal sealed class ImeContextRecord
{
    public int NotepadThreadLangId { get; set; }
    public string NotepadThreadLangName { get; set; } = "";
    public string LayoutDescription { get; set; } = "";
    public bool? ImmContextAcquired { get; set; }
    public bool? ImmOpenStatus { get; set; }
    public bool? ImmNativeMode { get; set; }
    public uint ImmConversionRaw { get; set; }
}

internal sealed class InputCase
{
    public string SampleId { get; set; } = "";
    public string SampleLabel { get; set; } = "";
    public string Strategy { get; set; } = "";
    public string StrategyName { get; set; } = "";
    public string ImeArgument { get; set; } = "";

    public bool Executed { get; set; }
    public string Status { get; set; } = "";
    public bool ForegroundGuardPassed { get; set; }
    public string FocusWindowClass { get; set; } = "";
    public string ClearMethod { get; set; } = "";
    public bool BaselineEmpty { get; set; }

    public int ExpectedLength { get; set; }
    public int ActualLength { get; set; }
    public bool LengthEqual { get; set; }
    public bool ExactMatch { get; set; }
    public int FirstMismatchIndex { get; set; } = -1;
    public string ExpectedSnippetAtMismatch { get; set; } = "";
    public string ActualSnippetAtMismatch { get; set; } = "";

    public double InjectMs { get; set; }
    public double ReadMs { get; set; }
    public string ReadMethod { get; set; } = "";
    public bool ReadSettled { get; set; }
    public string ActualValue { get; set; } = "";
    public string ExpectedValue { get; set; } = "";
    public string UiaValue { get; set; } = "";
    public bool? UiaAgreesWithWin32 { get; set; }

    public string InjectDetail { get; set; } = "";
    public uint EventsRequested { get; set; }
    public uint EventsInserted { get; set; }
    public int Win32Error { get; set; }

    public List<string> Anomalies { get; set; } = new();
    public ImeContextRecord Ime { get; set; } = new();
}

internal sealed class CursorProbe
{
    public string Group { get; set; } = "";
    public string Label { get; set; } = "";
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public int ActualX { get; set; }
    public int ActualY { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public bool ExactHit { get; set; }
    public double ElapsedMs { get; set; }
    public int MonitorIndexFromPoint { get; set; } = -1;
    public string MonitorRectFromPoint { get; set; } = "";
    public bool SetCursorPosSucceeded { get; set; }
}

internal sealed class CleanupInfo
{
    public bool NotepadClosed { get; set; }
    public string NotepadCloseMethod { get; set; } = "";
    public bool CursorRestored { get; set; }
    public int[] CursorAfterRestore { get; set; } = Array.Empty<int>();
    public bool ForegroundRestored { get; set; }
    public bool ClipboardRestored { get; set; }
    public string ClipboardRestoreDetail { get; set; } = "";
    public string Detail { get; set; } = "";
}
