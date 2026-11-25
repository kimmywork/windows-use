using System.Diagnostics;
using System.Runtime.InteropServices;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.System;

/// <summary>会话/环境状态（07 session.preflight、06 §5.6）。</summary>
public sealed record EnvironmentState(
    bool SessionLocked,
    bool SecureDesktopActive,
    bool ScreensaverActive,
    bool IsRemoteSession,
    bool Interactive,
    string? UacPrompt,
    long LastInputMsAgo);

/// <summary>
/// 环境探测：锁屏 / UAC 安全桌面 / 屏保 / RDP / 空闲时间。
/// 前置层遇 UAC → NEEDS_HUMAN(UAC_OPEN)；动作层遇安全桌面 → SECURE_DESKTOP。
/// </summary>
public sealed class EnvironmentProbe
{
    public EnvironmentState Snapshot()
    {
        var locked = ReadSessionLocked();
        var (secureDesktop, uac) = ReadSecureDesktop();
        var screensaver = ReadScreensaver();
        var remote = ReadIsRemote();
        var idle = ReadIdleMs();

        return new EnvironmentState(
            locked, secureDesktop, screensaver, remote,
            Interactive: !locked && !secureDesktop,
            uac ? "UAC_OPEN" : null,
            idle);
    }

    private static bool ReadSessionLocked()
    {
        try
        {
            if (!NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero,
                    NativeMethods.WTS_CURRENT_SESSION, NativeMethods.WTSSessionInfo,
                    out var buffer, out _))
            {
                return false;
            }
            try
            {
                var info = Marshal.PtrToStructure<NativeMethods.WTSINFO>(buffer);
                return info.State == NativeMethods.WTS_SESSIONSTATE_LOCKED;
            }
            finally
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    private static (bool SecureDesktop, bool Uac) ReadSecureDesktop()
    {
        // consent.exe = UAC 安全桌面；LogonUI = 锁屏/凭据 UI。
        var consent = Process.GetProcessesByName("consent");
        var secure = consent.Length > 0;
        foreach (var p in consent)
        {
            p.Dispose();
        }
        return (secure, secure);
    }

    private static bool ReadScreensaver()
    {
        var running = 0;
        return NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETSCREENSAVERRUNNING, 0, ref running, 0)
            && running != 0;
    }

    private static bool ReadIsRemote()
    {
        try
        {
            if (!NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero,
                    NativeMethods.WTS_CURRENT_SESSION, NativeMethods.WTSWinStationName,
                    out var buffer, out _))
            {
                return false;
            }
            try
            {
                var name = Marshal.PtrToStringUni(buffer) ?? "";
                return name.StartsWith("rdp", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("rdp-tcp", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    private static long ReadIdleMs()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return -1;
        }
        return NativeMethods.GetTickCount() - info.DwTime;
    }
}
