using System.Drawing;
using System.Drawing.Imaging;
using WinUse.Platform.Windows.Coordinates;
using WinUse.Platform.Windows.Native;

namespace WinUse.Platform.Windows.Capture;

/// <summary>
/// 屏幕截图（07 §10、06 §5.5）。M1 基线：GDI CopyFromScreen（全屏/区域）
/// + PrintWindow（单窗口，PW_RENDERFULLCONTENT）。WGC 为 v1.5。
/// 坐标基准：物理像素 + VSC 负原点（PoC C2/C10）。
/// </summary>
public sealed class ScreenCapturer
{
    private readonly VirtualScreen _virtualScreen;

    public ScreenCapturer(VirtualScreen virtualScreen)
    {
        _virtualScreen = virtualScreen;
    }

    public sealed record CaptureResult(byte[] Png, int Width, int Height, string Method);

    public CaptureResult CaptureRegion(int? x, int? y, int? width, int? height)
    {
        var snap = _virtualScreen.Capture();
        var rx = x ?? snap.OriginX;
        var ry = y ?? snap.OriginY;
        var rw = width ?? snap.Width;
        var rh = height ?? snap.Height;

        using var bitmap = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(rx, ry, 0, 0, new Size(rw, rh), CopyPixelOperation.SourceCopy);
        }
        return new CaptureResult(ToPng(bitmap), rw, rh, "gdi_copy_from_screen");
    }

    public CaptureResult CaptureWindow(IntPtr hwnd)
    {
        if (NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity))
        {
            if (affinity is NativeMethods.WDA_MONITOR or NativeMethods.WDA_EXCLUDEFROMCAPTURE)
            {
                throw new CaptureProtectedException();
            }
        }

        var rect = Windowing.WindowEnumerator.ReadExtendedFrame(hwnd)
                   ?? Windowing.WindowEnumerator.ReadWindowRect(hwnd);
        var w = Math.Max(1, rect[2]);
        var h = Math.Max(1, rect[3]);

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                var ok = NativeMethods.PrintWindow(hwnd, hdc,
                    NativeMethods.PW_RENDERFULLCONTENT);
                if (!ok)
                {
                    // 兜底：BitBlt 窗口 DC。
                    var windowDc = NativeMethods.GetDC(hwnd);
                    try
                    {
                        NativeMethods.BitBlt(hdc, 0, 0, w, h, windowDc, 0, 0, NativeMethods.SRCCOPY);
                    }
                    finally
                    {
                        NativeMethods.ReleaseDC(hwnd, windowDc);
                    }
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        return new CaptureResult(ToPng(bitmap), w, h, "print_window");
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

public sealed class CaptureProtectedException : Exception
{
    public CaptureProtectedException()
        : base("目标窗口设置了显示亲和性（WDA_MONITOR/EXCLUDEFROMCAPTURE），画面受保护，禁止返回黑屏冒充成功")
    {
    }
}
