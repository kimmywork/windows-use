using global::Windows.Globalization;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;
using global::Windows.Storage.Streams;
using WinUse.Core.Capabilities;
using WinUse.Platform.Windows.Capture;
using WinUse.Platform.Windows.Windowing;

namespace WinUse.Platform.Windows.Capabilities;

/// <summary>
/// OCR（07 ocr.recognize）：Windows 内置 WinRT OCR（Windows.Media.Ocr），
/// 离线、无第三方依赖。输入：区域/窗口（缺省全屏）。
/// </summary>
internal sealed class OcrRecognize : WindowsCapability
{
    public override string Id => "ocr.recognize";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var capturer = context.GetRequiredService<ScreenCapturer>();
        var args = context.Args;

        ScreenCapturer.CaptureResult shot;
        if (args.Has("hwnd") || args.Has("window") || args.Has("title"))
        {
            var hwnd = context.GetRequiredService<WindowRefResolver>().Resolve(args);
            shot = capturer.CaptureWindow(hwnd);
        }
        else
        {
            shot = capturer.CaptureRegion(
                args.GetInt32("x"), args.GetInt32("y"),
                args.GetInt32("width"), args.GetInt32("height"));
        }

        var language = args.GetString("language");
        var result = Recognize(shot.Png, language).GetAwaiter().GetResult();
        return Ok(result);
    }

    private static async Task<object> Recognize(byte[] png, string? language)
    {
        OcrEngine? engine = language is { Length: > 0 } lang
            ? OcrEngine.TryCreateFromLanguage(new Language(lang))
            : OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            throw new InvalidOperationException(
                $"无法创建 OCR 引擎（语言 {language ?? "用户配置语言"} 缺少识别包）");
        }

        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter();
        writer.WriteBytes(png);
        stream.WriteAsync(writer.DetachBuffer()).GetResults();
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var ocr = await engine.RecognizeAsync(bitmap);

        return new
        {
            text = ocr.Text,
            language = engine.RecognizerLanguage.LanguageTag,
            lines = ocr.Lines.Select(l => new
            {
                text = l.Text,
                words = l.Words.Select(w => new
                {
                    text = w.Text,
                    rect = new[]
                    {
                        (int)Math.Round(w.BoundingRect.X),
                        (int)Math.Round(w.BoundingRect.Y),
                        (int)Math.Round(w.BoundingRect.Width),
                        (int)Math.Round(w.BoundingRect.Height),
                    },
                }),
            }),
        };
    }
}
