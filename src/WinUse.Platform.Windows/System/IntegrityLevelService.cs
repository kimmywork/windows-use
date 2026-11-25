using System.Diagnostics;
using System.Runtime.InteropServices;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.System;

/// <summary>完整性级别（07 system.info、PoC C1/C7）。</summary>
public enum IntegrityLevelValue
{
    Unknown = 0,
    Low = 0x1000,
    Medium = 0x2000,
    High = 0x3000,
    System = 0x4000,
}

/// <summary>
/// IL 读取服务。PoC C7：约 45% 进程查不到 IL —— 任何调用都必须有失败路径，
/// 返回 <see cref="IntegrityLevelValue.Unknown"/>，禁止猜测。
/// </summary>
public sealed class IntegrityLevelService
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public IntegrityLevelValue GetOwn() => FromProcess(NativeMethods.GetCurrentProcess());

    public IntegrityLevelValue GetByProcessId(int processId)
    {
        var handle = NativeMethods.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return IntegrityLevelValue.Unknown; // C7：失败路径
        }
        try
        {
            return FromProcess(handle);
        }
        catch
        {
            return IntegrityLevelValue.Unknown;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static IntegrityLevelValue FromProcess(IntPtr processHandle)
    {
        if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out var token))
        {
            return IntegrityLevelValue.Unknown;
        }
        try
        {
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel,
                IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
            {
                return IntegrityLevelValue.Unknown;
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel,
                        buffer, needed, out _))
                {
                    return IntegrityLevelValue.Unknown;
                }

                // TOKEN_MANDATORY_LABEL 第一个字段 = SID_AND_ATTRIBUTES.Sid
                var sidPtr = Marshal.ReadIntPtr(buffer);
                if (sidPtr == IntPtr.Zero)
                {
                    return IntegrityLevelValue.Unknown;
                }

                var subAuthorityCountPtr = Marshal.ReadByte(sidPtr, 0); // SID revision byte
                var subAuthCount = Marshal.ReadByte(sidPtr, 1);          // SubAuthorityCount
                if (subAuthCount == 0)
                {
                    return IntegrityLevelValue.Unknown;
                }
                // SID layout: Revision(1) SubAuthCount(1) IdentifierAuthority(6) SubAuthority[count](4 each)
                var ridOffset = 8 + ((subAuthCount - 1) * 4);
                var rid = Marshal.ReadInt32(sidPtr, ridOffset);
                return rid switch
                {
                    0x1000 => IntegrityLevelValue.Low,
                    0x2000 => IntegrityLevelValue.Medium,
                    0x3000 => IntegrityLevelValue.High,
                    0x4000 => IntegrityLevelValue.System,
                    _ => IntegrityLevelValue.Unknown,
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
