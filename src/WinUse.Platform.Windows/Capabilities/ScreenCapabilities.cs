using global::System.Drawing;
using global::System.Drawing.Imaging;
using WinUse.Core.Capabilities;
using WinUse.Core.Selector;
using WinUse.Platform.Windows.Uia;

namespace WinUse.Platform.Windows.Capabilities;

internal sealed class ScreenCapture : WindowsCapability
{
    public override string Id => "screen.capture";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var capturer = context.GetRequiredService<Capture.ScreenCapturer>();
        var args = context.Args;

        Capture.ScreenCapturer.CaptureResult result;
        if (args.Has("hwnd") || args.Has("window") || args.Has("title"))
        {
            var hwnd = context.GetRequiredService<Windowing.WindowRefResolver>().Resolve(args);
            result = capturer.CaptureWindow(hwnd);
        }
        else
        {
            result = capturer.CaptureRegion(
                args.GetInt32("x"), args.GetInt32("y"),
                args.GetInt32("width"), args.GetInt32("height"));
        }

        return Ok(new
        {
            image_base64 = Convert.ToBase64String(result.Png),
            format = "png",
            width = result.Width,
            height = result.Height,
            method = result.Method,
        });
    }
}

internal sealed class ScreenAnnotate : WindowsCapability
{
    public override string Id => "screen.annotate";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var capturer = context.GetRequiredService<Capture.ScreenCapturer>();
        var uia = context.GetRequiredService<UiaService>();
        var selector = SelectorParser.Parse(context.Args);
        var scope = ScopeResolver.Resolve(context, selector);

        // 1) 截图（scope 为窗口时截窗口，否则全屏）
        var shot = scope == IntPtr.Zero
            ? capturer.CaptureRegion(null, null, null, null)
            : capturer.CaptureWindow(scope);

        // 2) 收集可标注元素
        var elementSelector = selector ?? new Selector
        {
            VisibleOnly = true,
            ControlType = null,
        };
        var found = uia.FindAsync(scope, elementSelector, maxResults: 60)
            .GetAwaiter().GetResult();
        var elements = found.Matches
            .Where(m => m.Rect is [var w, var h] && w > 4 && h > 4)
            .Take(40)
            .ToArray();

        // 3) 画框 + 编号
        using var bitmap = LoadBitmap(shot.Png);
        using var g = Graphics.FromImage(bitmap);
        var origin = scope == IntPtr.Zero
            ? new[] { 0, 0 }
            : (Windowing.WindowEnumerator.ReadExtendedFrame(scope)
               ?? Windowing.WindowEnumerator.ReadWindowRect(scope)) is { } r
                ? new[] { r[0], r[1] }
                : new[] { 0, 0 };

        var labels = new List<object>();
        using var pen = new Pen(Color.FromArgb(220, 255, 200, 40), 3);
        using var font = new Font("Segoe UI", 14f, FontStyle.Bold);
        using var bg = new SolidBrush(Color.FromArgb(230, 255, 200, 40));
        var index = 1;
        foreach (var e in elements)
        {
            var rect = new Rectangle(
                e.Rect[0] - origin[0], e.Rect[1] - origin[1],
                e.Rect[2], e.Rect[3]);
            g.DrawRectangle(pen, rect);
            g.FillRectangle(bg, rect.X, rect.Y - 26, 34, 26);
            g.DrawString(index.ToString(), font, Brushes.Black, rect.X + 4, rect.Y - 25);
            labels.Add(new
            {
                index,
                element_id = e.ElementId,
                name = e.Name,
                control_type = e.ControlType,
                center = e.Center,
            });
            index++;
        }

        byte[] png;
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }

        return Ok(new
        {
            image_base64 = Convert.ToBase64String(png),
            format = "png",
            width = shot.Width,
            height = shot.Height,
            elements = labels,
        });
    }

    private static Bitmap LoadBitmap(byte[] png)
    {
        using var ms = new MemoryStream(png);
        return new Bitmap(ms);
    }
}
