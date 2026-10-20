using System.Windows.Media.Imaging;
using SkiaSharp;

namespace SnapGlyph.App;

internal static class BitmapSourceUtil
{
    public static BitmapSource FromSkia(SKBitmap bitmap)
    {
        var normalized = bitmap.ColorType == SKColorType.Bgra8888
            ? bitmap
            : CopyToBgra(bitmap);
        try
        {
            var stride = normalized.RowBytes;
            var result = BitmapSource.Create(
                normalized.Width,
                normalized.Height,
                96,
                96,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                normalized.GetPixels(),
                stride * normalized.Height,
                stride);
            result.Freeze();
            return result;
        }
        finally
        {
            if (!ReferenceEquals(normalized, bitmap))
            {
                normalized.Dispose();
            }
        }
    }

    private static SKBitmap CopyToBgra(SKBitmap bitmap)
    {
        var result = new SKBitmap(
            new SKImageInfo(
                bitmap.Width,
                bitmap.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(bitmap, 0, 0);
        canvas.Flush();
        return result;
    }
}
