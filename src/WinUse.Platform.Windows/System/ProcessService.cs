using System.Diagnostics;

namespace WinUse.Platform.Windows.System;

/// <summary>进程信息（07 §4）。</summary>
public sealed record ProcessInfo(int Pid, string Name, string? Path, int? ParentPid, string? IntegrityLevel);

/// <summary>进程服务：start / list / terminate（07 process.*、PoC §7 C7）。</summary>
public sealed class ProcessService
{
    private readonly IntegrityLevelService _integrity;

    public ProcessService(IntegrityLevelService integrity)
    {
        _integrity = integrity;
    }

    public int Start(string path, string? arguments, string? workingDirectory, bool elevated = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
        };
        if (!string.IsNullOrEmpty(arguments))
        {
            startInfo.Arguments = arguments;
        }
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }
        if (elevated)
        {
            // 提权必须走 ShellExecute + runas，会触发 UAC（NEEDS_HUMAN/UAC_OPEN）。
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("进程启动返回 null");
        return process.Id;
    }

    public IReadOnlyList<ProcessInfo> List(string? nameContains = null)
    {
        var result = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (nameContains is { Length: > 0 } nc &&
                    !name.Contains(nc, StringComparison.OrdinalIgnoreCase))
                {
                    p.Dispose();
                    continue;
                }

                string? path = null;
                try
                {
                    path = p.MainModule?.FileName;
                }
                catch
                {
                    // 访问受限（跨 IL）→ null，不失败
                }

                var il = _integrity.GetByProcessId(p.Id);
                result.Add(new ProcessInfo(p.Id, name, path, null,
                    il == IntegrityLevelValue.Unknown ? null : il.ToString()));
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }

    public bool Terminate(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // 进程不存在
        }
    }
}
