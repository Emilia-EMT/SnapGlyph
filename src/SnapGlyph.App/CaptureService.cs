using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SkiaSharp;

namespace SnapGlyph.App;

public static class CaptureService
{
    public static SKBitmap Capture(IntPtr target, SKRectI selected)
    {
        if (target == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(target, out var windowRect))
        {
            throw new InvalidOperationException("目标窗口不可用。");
        }

        var window = windowRect.ToSkiaRect();
        var crop = Intersect(window, selected);
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            throw new InvalidOperationException("选区不在目标窗口内。");
        }

        using var full = new Bitmap(
            window.Width,
            window.Height,
            PixelFormat.Format32bppArgb);
        var printed = false;
        using (var graphics = Graphics.FromImage(full))
        {
            var hdc = graphics.GetHdc();
            try
            {
                printed = NativeMethods.PrintWindow(target, hdc, 2);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        if (!printed || IsMostlyEmpty(full))
        {
            // The overlay is already closed before this fallback is used.
            using var screen = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(screen);
            graphics.CopyFromScreen(
                crop.Left,
                crop.Top,
                0,
                0,
                new Size(crop.Width, crop.Height),
                CopyPixelOperation.SourceCopy);
            return ToSkia(screen);
        }

        using var cropped = full.Clone(
            new Rectangle(
                crop.Left - window.Left,
                crop.Top - window.Top,
                crop.Width,
                crop.Height),
            PixelFormat.Format32bppArgb);
        return ToSkia(cropped);
    }

    public static SKBitmap CaptureScreen(SKRectI selected)
    {
        if (selected.Width <= 0 || selected.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selected), "选区为空。");
        }

        using var bitmap = new Bitmap(
            selected.Width,
            selected.Height,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                selected.Left,
                selected.Top,
                0,
                0,
                new Size(selected.Width, selected.Height),
                CopyPixelOperation.SourceCopy);
        }
        return ToSkia(bitmap);
    }

    private static SKRectI Intersect(SKRectI left, SKRectI right) =>
        new(
            Math.Max(left.Left, right.Left),
            Math.Max(left.Top, right.Top),
            Math.Min(left.Right, right.Right),
            Math.Min(left.Bottom, right.Bottom));

    private static bool IsMostlyEmpty(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 8))
        {
            for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 8))
            {
                var color = bitmap.GetPixel(x, y);
                if (color.A != 0 && (color.R != 0 || color.G != 0 || color.B != 0))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static SKBitmap ToSkia(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        return SKBitmap.Decode(stream.ToArray())
            ?? throw new InvalidOperationException("截图转换失败。");
    }
}
