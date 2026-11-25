using System.Runtime.InteropServices;
using WinUse.Platform.Windows.Coordinates;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Input;

/// <summary>
/// 鼠标注入器（L1 INPUT，07 §5、06 §5.3）。
/// 绝对坐标按 VSC 归一到 0..65535；支持负原点/多屏（PoC C10/C11）。
/// </summary>
public sealed class MouseInjector
{
    private readonly VirtualScreen _virtualScreen;

    public MouseInjector(VirtualScreen virtualScreen)
    {
        _virtualScreen = virtualScreen;
    }

    public void Move(int x, int y)
    {
        var snap = _virtualScreen.Capture();
        var input = new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                Mi = new NativeMethods.MOUSEINPUT
                {
                    Dx = NormalizeX(snap, x),
                    Dy = NormalizeY(snap, y),
                    DwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE,
                },
            },
        };
        Send(input);
    }

    public void Click(int x, int y, string button = "left", int clickCount = 1)
    {
        Move(x, y);
        Thread.Sleep(30);
        for (var i = 0; i < clickCount; i++)
        {
            SendButton(button, down: true);
            Thread.Sleep(12);
            SendButton(button, down: false);
            Thread.Sleep(40);
        }
    }

    public void Wheel(int x, int y, int clicks, bool horizontal = false)
    {
        Move(x, y);
        Thread.Sleep(20);
        var input = new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                Mi = new NativeMethods.MOUSEINPUT
                {
                    MouseData = (uint)(clicks * NativeMethods.WHEEL_DELTA),
                    DwFlags = horizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL,
                },
            },
        };
        Send(input);
    }

    public void Drag(int fromX, int fromY, int toX, int toY, string button = "left")
    {
        Move(fromX, fromY);
        Thread.Sleep(50);
        SendButton(button, down: true);
        Thread.Sleep(120);

        // 分段移动，模拟真实拖拽（双屏间隙也按 VSC 连续插值）。
        const int steps = 20;
        for (var i = 1; i <= steps; i++)
        {
            var x = fromX + ((toX - fromX) * i / steps);
            var y = fromY + ((toY - fromY) * i / steps);
            Move(x, y);
            Thread.Sleep(8);
        }
        Thread.Sleep(120);
        SendButton(button, down: false);
    }

    private static void SendButton(string button, bool down)
    {
        var (downFlag, upFlag) = button.ToLowerInvariant() switch
        {
            "right" => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
            "middle" => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
            _ => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP),
        };
        var input = new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                Mi = new NativeMethods.MOUSEINPUT { DwFlags = down ? downFlag : upFlag },
            },
        };
        Send(input);
    }

    private static int NormalizeX(VirtualScreen.Snapshot snap, int x) =>
        (int)Math.Round((x - snap.OriginX) * 65535.0 / Math.Max(1, snap.Width));

    private static int NormalizeY(VirtualScreen.Snapshot snap, int y) =>
        (int)Math.Round((y - snap.OriginY) * 65535.0 / Math.Max(1, snap.Height));

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
