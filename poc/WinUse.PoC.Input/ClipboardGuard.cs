// ClipboardGuard.cs — 原生剪贴板读写 + 尽力而为的用户剪贴板还原。
//
// 安全考量：方案 B 必须占用剪贴板。启动前若用户剪贴板里有文本（CF_UNICODE），
// 先备份；收尾时对同样占位 East 的用户文本做还原（非文本格式无法还原，如实记录）。

using System.Runtime.InteropServices;

namespace WinUse.PoC.Input;

internal sealed class ClipboardGuard
{
    private readonly bool _verbose;
    private string? _backupText;
    private bool _hadUnicodeText;
    private bool _triedBackup;
    private bool _backupFailed;

    public ClipboardGuard(bool verbose = true)
    {
        _verbose = verbose;
    }

    /// <summary>读取当前剪贴板的 CF_UNICODE 文本（读数失败返回 null）。</summary>
    public bool TryBackup(out string detail)
    {
        detail = "";
        if (_triedBackup) return _hadUnicodeText;
        _triedBackup = true;

        if (!Native.OpenClipboard(IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            _backupFailed = true;
            detail = $"备份失败：OpenClipboard 返回 false，Win32Error={err}";
            if (_verbose) Console.WriteLine("    [剪贴板] " + detail);
            return false;
        }

        try
        {
            if (!Native.IsClipboardFormatAvailable(Native.CF_UNICODE))
            {
                detail = "当前剪贴板无 CF_UNICODE 文本（可能是图片/文件等非文本数据）";
                return false;
            }

            IntPtr handle = Native.GetClipboardData(Native.CF_UNICODE);
            if (handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                detail = $"GetClipboardData 失败，Win32Error={err}";
                return false;
            }

            IntPtr ptr = Native.GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                detail = "GlobalLock 失败";
                return false;
            }

            try
            {
                string? text = Marshal.PtrToStringUni(ptr);
                _hadUnicodeText = !string.IsNullOrEmpty(text);
                _backupText = text ?? string.Empty;
                detail = _hadUnicodeText
                    ? $"已备份用户剪贴板文本（{Diff.Preview(_backupText, 16)}）"
                    : "剪贴板 CF_UNICODE 为空";
                return _hadUnicodeText;
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>把 Unicode 文本写入剪贴板。</summary>
    public bool SetUnicodeText(string text, out string detail)
    {
        detail = "";
        string value = text ?? string.Empty;

        if (!Native.OpenClipboard(IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            detail = $"OpenClipboard 失败，Win32Error={err}";
            return false;
        }

        try
        {
            if (!Native.EmptyClipboard())
            {
                int err = Marshal.GetLastWin32Error();
                detail = $"EmptyClipboard 失败，Win32Error={err}";
                return false;
            }

            nuint bytes = (nuint)((value.Length + 1) * 2);
            IntPtr hMem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero)
            {
                detail = "GlobalAlloc 失败";
                return false;
            }

            IntPtr ptr = Native.GlobalLock(hMem);
            if (ptr == IntPtr.Zero)
            {
                Native.GlobalFree(hMem);
                detail = "GlobalLock 失败";
                return false;
            }

            Marshal.Copy(value.ToCharArray(), 0, ptr, value.Length);
            Marshal.WriteInt16(ptr, value.Length * 2, 0);
            Native.GlobalUnlock(hMem);

            // SetClipboardData 成功后内存归系统所有，不能再 GlobalFree。
            IntPtr ret = Native.SetClipboardData(Native.CF_UNICODE, hMem);
            if (ret == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Native.GlobalFree(hMem);
                detail = $"SetClipboardData 失败，Win32Error={err}";
                return false;
            }

            detail = $"已写入 CF_UNICODE（{value.Length} 字符）";
            return true;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>收尾：尽量把用户的剪贴板文本放回去。</summary>
    public bool TryRestore(out string detail)
    {
        detail = "";
        if (!_triedBackup)
        {
            detail = "未做过备份，不还原";
            return false;
        }
        if (_backupFailed)
        {
            detail = "备份阶段失败，放弃还原以免误覆盖";
            return false;
        }
        if (!_hadUnicodeText)
        {
            detail = "原剪贴板无文本，跳过还原（仅清空由 SetUnicodeText 内部完成，此处不再改动用户数据）";
            return false;
        }

        bool ok = SetUnicodeText(_backupText ?? string.Empty, out string setDetail);
        detail = ok ? "已还原用户剪贴板文本" : "还原失败：" + setDetail;
        return ok;
    }
}
