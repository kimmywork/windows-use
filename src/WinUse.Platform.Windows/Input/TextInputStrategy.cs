using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using WinUse.Platform.Windows.Native;
using WinUse.Platform.Windows.Uia;

namespace WinUse.Platform.Windows.Input;

/// <summary>
/// 文本输入策略链（06 §6，PoC C14–C17）：
/// ① ValuePattern.SetValue → ② KEYEVENTF_UNICODE（线程布局代码页判定 + 代理对排除）
/// → ③ 剪贴板 + Ctrl+V（ClipboardGuard）。每级读回校验；全失败 → VERIFY_FAILED。
/// </summary>
public sealed class TextInputStrategy
{
    private readonly KeyboardInjector _keyboard;
    private readonly ClipboardGuard _clipboard;
    private readonly UiaService _uia;

    public TextInputStrategy(KeyboardInjector keyboard, ClipboardGuard clipboard, UiaService uia)
    {
        _keyboard = keyboard;
        _clipboard = clipboard;
        _uia = uia;
    }

    public sealed record InputOutcome(bool Success, string Method, string? FinalText, string? FailureReason);

    public async Task<InputOutcome> InputAsync(IntPtr rootHwnd, Core.Selector.Selector? selector,
        string text, CancellationToken ct = default)
    {
        // ① ValuePattern
        if (selector is not null)
        {
            try
            {
                await _uia.SetValueAsync(rootHwnd, selector, text, ct);
                if (await ReadbackMatchesAsync(text, ct))
                {
                    return new InputOutcome(true, "value_pattern", null, null);
                }
            }
            catch (PatternUnsupportedException)
            {
                // 落到 ②
            }
            catch (ElementNotFoundException)
            {
                throw;
            }
            catch
            {
                // SetValue 其他失败 → 落到 ②
            }
        }

        // ② KEYEVENTF_UNICODE
        if (!text.Any(char.IsSurrogate))
        {
            var canEncode = await TargetLayoutCanEncodeAsync(text, ct);
            if (canEncode)
            {
                try
                {
                    _keyboard.TypeUnicode(text);
                    await Task.Delay(120, ct);
                    if (await ReadbackMatchesAsync(text, ct))
                    {
                        return new InputOutcome(true, "unicode", null, null);
                    }
                }
                catch (SurrogateNotSupportedException)
                {
                    // 落到 ③
                }
            }
        }

        // ③ 剪贴板 + 粘贴
        var saved = _clipboard.SaveText();
        try
        {
            if (!_clipboard.TrySetTextWithRetry(text))
            {
                return new InputOutcome(false, "clipboard", null, "剪贴板写入失败");
            }
            _keyboard.Hotkey(new[] { "ctrl" }, "v");
            await Task.Delay(180, ct);
            if (await ReadbackMatchesAsync(text, ct))
            {
                return new InputOutcome(true, "clipboard", null, null);
            }
            return new InputOutcome(false, "clipboard", null, "粘贴后读回不匹配");
        }
        finally
        {
            await Task.Delay(50, ct);
            try
            {
                _clipboard.Restore(saved);
            }
            catch
            {
                // 恢复失败不影响主结论，但应在审计中体现；此处吞掉并由调用方感知风险。
            }
        }
    }

    private async Task<bool> ReadbackMatchesAsync(string expected, CancellationToken ct)
    {
        var actual = await _uia.GetFocusedTextAsync(ct);
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }
        var normActual = actual.Replace("\r\n", "\n").TrimEnd();
        var normExpected = expected.Replace("\r\n", "\n");
        return normActual.EndsWith(normExpected, StringComparison.Ordinal)
            || normActual.Contains(normExpected, StringComparison.Ordinal);
    }

    /// <summary>
    /// 判定**焦点控件所在线程**键盘布局对应代码页能否编码文本（PoC C14：由目标线程布局决定，非进程 ACP）。
    /// </summary>
    private async Task<bool> TargetLayoutCanEncodeAsync(string text, CancellationToken ct)
    {
        try
        {
            var focused = await _uia.GetFocusedElementAsync(ct);
            var controlHwnd = new IntPtr(focused.NativeHandle);
            var threadId = controlHwnd != IntPtr.Zero
                ? NativeMethods.GetWindowThreadProcessId(controlHwnd, out _)
                : 0u;
            var hkl = NativeMethods.GetKeyboardLayout(threadId);
            var langId = (int)(hkl.ToInt64() & 0xFFFF);
            if (langId == 0)
            {
                return false; // 读不到布局 → 不冒静默失败的风险
            }
            var codePage = new CultureInfo(langId).TextInfo.ANSICodePage;
            var encoding = Encoding.GetEncoding(codePage,
                new EncoderExceptionFallback(), new DecoderExceptionFallback());
            encoding.GetByteCount(text); // 不可编码即抛
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch
        {
            return false; // 任何探测失败：不预判，保守拒绝该级
        }
    }
}
