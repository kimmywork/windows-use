using System.Runtime.InteropServices;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Input;

/// <summary>
/// 键盘注入器（L1 INPUT，07 §6、06 §6）。
/// 键名 → VK；Unicode 文本走 KEYEVENTF_UNICODE（代理对静态排除，PoC C16）。
/// </summary>
public sealed class KeyboardInjector
{
    public void Press(string keyName, bool down)
    {
        var (vk, extended) = ResolveKey(keyName);
        var scan = (ushort)NativeMethods.MapVirtualKeyW((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
        var input = new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.KEYBDINPUT
                {
                    WVk = (ushort)vk,
                    WScan = scan,
                    DwFlags = (down ? 0u : NativeMethods.KEYEVENTF_KEYUP) | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
                },
            },
        };
        Send(input);
    }

    public void Tap(string keyName)
    {
        Press(keyName, true);
        Thread.Sleep(10);
        Press(keyName, false);
    }

    public void Hotkey(string[] modifiers, string mainKey)
    {
        foreach (var m in modifiers)
        {
            Press(m, true);
            Thread.Sleep(10);
        }
        Thread.Sleep(20);
        Press(mainKey, true);
        Thread.Sleep(20);
        Press(mainKey, false);
        foreach (var m in modifiers.Reverse())
        {
            Thread.Sleep(10);
            Press(m, false);
        }
    }

    /// <summary>
    /// 逐 UTF-16 码元 Unicode 注入。含代理对（BMP 外字符/emoji）直接拒绝（PoC C16）。
    /// </summary>
    public void TypeUnicode(string text)
    {
        foreach (var codeUnit in text)
        {
            if (char.IsSurrogate(codeUnit))
            {
                throw new SurrogateNotSupportedException(
                    $"字符 U+{(int)codeUnit:X4} 属于代理对，KEYEVENTF_UNICODE 必然失败（PoC C16），请改用剪贴板方案");
            }
            var input = new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    Ki = new NativeMethods.KEYBDINPUT
                    {
                        WScan = (ushort)codeUnit,
                        DwFlags = NativeMethods.KEYEVENTF_UNICODE,
                    },
                },
            };
            Send(input);
        }
    }

    public static (int Vk, bool Extended) ResolveKey(string keyName)
    {
        var name = keyName.Trim().ToLowerInvariant();
        if (name.Length == 1)
        {
            char c = name[0];
            if (c is >= 'a' and <= 'z')
            {
                return (char.ToUpperInvariant(c), false);
            }
            if (c is >= '0' and <= '9')
            {
                return (c, false);
            }
            // 标点：VkKeyScanW 给出 VK 与 shift 状态。
            var ks = VkKeyScanW(c);
            if ((short)ks != -1)
            {
                return (ks & 0xFF, false);
            }
        }

        int vk = name switch
        {
            "enter" or "return" => NativeMethods.VK_RETURN,
            "esc" or "escape" => NativeMethods.VK_ESCAPE,
            "tab" => NativeMethods.VK_TAB,
            "space" or "spacebar" => NativeMethods.VK_SPACE,
            "backspace" or "back" => NativeMethods.VK_BACK,
            "delete" or "del" => NativeMethods.VK_DELETE,
            "insert" or "ins" => NativeMethods.VK_INSERT,
            "home" => NativeMethods.VK_HOME,
            "end" => NativeMethods.VK_END,
            "pageup" or "pgup" => NativeMethods.VK_PRIOR,
            "pagedown" or "pgdn" => NativeMethods.VK_NEXT,
            "left" or "arrowleft" => NativeMethods.VK_LEFT,
            "right" or "arrowright" => NativeMethods.VK_RIGHT,
            "up" or "arrowup" => NativeMethods.VK_UP,
            "down" or "arrowdown" => NativeMethods.VK_DOWN,
            "ctrl" or "control" => NativeMethods.VK_CONTROL,
            "alt" => NativeMethods.VK_MENU,
            "shift" => NativeMethods.VK_SHIFT,
            "win" or "windows" or "meta" or "super" => NativeMethods.VK_LWIN,
            "leftctrl" or "ctrlleft" => NativeMethods.VK_LCONTROL,
            "rightctrl" or "ctrlright" => NativeMethods.VK_RCONTROL,
            "leftalt" or "altleft" => NativeMethods.VK_LMENU,
            "rightalt" or "altright" => NativeMethods.VK_RMENU,
            "leftshift" or "shiftleft" => NativeMethods.VK_LSHIFT,
            "rightshift" or "shiftright" => NativeMethods.VK_RSHIFT,
            _ when name.StartsWith("f") && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24 =>
                NativeMethods.VK_F1 + (fn - 1),
            _ => throw new ArgumentException($"无法识别的键名: {keyName}"),
        };

        // 方向键/Insert/Delete 等在增强键区。
        var extended = vk is NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT or NativeMethods.VK_UP
            or NativeMethods.VK_DOWN or NativeMethods.VK_DELETE or NativeMethods.VK_INSERT
            or NativeMethods.VK_HOME or NativeMethods.VK_END or NativeMethods.VK_PRIOR or NativeMethods.VK_NEXT
            or NativeMethods.VK_RCONTROL or NativeMethods.VK_RMENU;
        return (vk, extended);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    private static void Send(params NativeMethods.INPUT[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput 仅插入 {sent}/{inputs.Length}（Win32Error={Marshal.GetLastWin32Error()}）");
        }
    }
}

public sealed class SurrogateNotSupportedException : Exception
{
    public SurrogateNotSupportedException(string message) : base(message)
    {
    }
}
