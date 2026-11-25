using System.Windows.Forms;

namespace WinUse.Platform.Windows.Input;

/// <summary>
/// 剪贴板守卫（06 §6 策略③、PoC C17）：操作前保存原内容，操作后恢复。
/// 所有剪贴板访问在专用 STA 线程进行。
/// </summary>
public sealed class ClipboardGuard
{
    /// <summary>保存当前剪贴板文本（可能为 null）。</summary>
    public string? SaveText() => StaClipboard(() =>
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            return null;
        }
    });

    public void SetText(string text) => StaClipboard(() =>
    {
        Clipboard.SetText(text, TextDataFormat.UnicodeText);
    });

    public string? GetText() => StaClipboard(() =>
        Clipboard.ContainsText() ? Clipboard.GetText() : null);

    /// <summary>恢复保存的内容；null 表示清空。</summary>
    public void Restore(string? saved) => StaClipboard(() =>
    {
        if (saved is null)
        {
            Clipboard.Clear();
        }
        else
        {
            Clipboard.SetText(saved, TextDataFormat.UnicodeText);
        }
    });

    /// <summary>设置文本 → Ctrl+V 粘贴 → 读回由调用方完成；这里仅保证设置成功。</summary>
    public bool TrySetTextWithRetry(string text, int attempts = 3)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                SetText(text);
                return GetText() == text;
            }
            catch
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }

    private static void StaClipboard(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000);
        if (error is not null)
        {
            throw error;
        }
    }

    private static T StaClipboard<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000);
        if (error is not null)
        {
            throw error;
        }
        return result;
    }
}
