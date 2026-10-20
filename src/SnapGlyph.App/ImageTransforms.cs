using System.Runtime.InteropServices;
using OpenCvSharp;
using SkiaSharp;
using OcvPoint2f = OpenCvSharp.Point2f;
using OcvSize = OpenCvSharp.Size;

namespace SnapGlyph.App;

public static class ImageTransforms
{
    public static SKBitmap Clone(SKBitmap source)
    {
        var copy = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(copy);
        canvas.DrawBitmap(source, 0, 0);
        return copy;
    }

    public static SKBitmap ResizeForPreview(SKBitmap source, int maxSide)
    {
        var scale = Math.Min(
            1.0,
            maxSide / (double)Math.Max(source.Width, source.Height));
        if (scale >= 0.999)
        {
            return Clone(source);
        }

        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return source.Resize(
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            SKFilterQuality.High)
            ?? Clone(source);
    }

    public static SKBitmap Rotate(SKBitmap source, float degrees)
    {
        if (Math.Abs(degrees) < 0.01f)
        {
            return Clone(source);
        }

        var radians = degrees * Math.PI / 180.0;
        var width = (int)Math.Ceiling(
            Math.Abs(source.Width * Math.Cos(radians)) +
            Math.Abs(source.Height * Math.Sin(radians)));
        var height = (int)Math.Ceiling(
            Math.Abs(source.Width * Math.Sin(radians)) +
            Math.Abs(source.Height * Math.Cos(radians)));
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);

        var output = new SKBitmap(
            width,
            height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = IsQuarterTurn(degrees)
                ? SKFilterQuality.None
                : SKFilterQuality.High,
        };
        canvas.Clear(SKColors.White);
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, paint);
        canvas.Flush();
        return output;
    }

    private static bool IsQuarterTurn(float degrees)
    {
        var normalized = Math.Abs(degrees % 90f);
        return normalized < 0.001f || normalized > 89.999f;
    }

    public static SKBitmap Perspective(
        SKBitmap source,
        IReadOnlyList<SKPoint> corners,
        int padding = 0)
    {
        if (corners.Count != 4)
        {
            throw new ArgumentException("Perspective needs four corners.", nameof(corners));
        }

        ValidateQuad(corners);
        var width = (int)Math.Max(
            Distance(corners[0], corners[1]),
            Distance(corners[3], corners[2]));
        var height = (int)Math.Max(
            Distance(corners[0], corners[3]),
            Distance(corners[1], corners[2]));
        width = Math.Max(width, 2);
        height = Math.Max(height, 2);

        var normalized = source.ColorType == SKColorType.Bgra8888
            ? source
            : ToBgra(source);
        try
        {
            using var input = Mat.FromPixelData(
                normalized.Height,
                normalized.Width,
                MatType.CV_8UC4,
                normalized.Bytes);
            using var output = new Mat();
            var sourcePoints = corners
                .Select(point => new OcvPoint2f(point.X, point.Y))
                .ToArray();
            var destinationPoints = new[]
            {
                new OcvPoint2f(0, 0),
                new OcvPoint2f(width - 1, 0),
                new OcvPoint2f(width - 1, height - 1),
                new OcvPoint2f(0, height - 1),
            };
            var matrix = Cv2.GetPerspectiveTransform(sourcePoints, destinationPoints);
            Cv2.WarpPerspective(
                input,
                output,
                matrix,
                new OcvSize(width, height),
                InterpolationFlags.Cubic,
                BorderTypes.Constant,
                new Scalar(255, 255, 255, 255));
            var warped = FromBgraMat(output);
            if (padding <= 0)
            {
                return warped;
            }

            var padded = new SKBitmap(
                warped.Width + padding * 2,
                warped.Height + padding * 2,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            using (var canvas = new SKCanvas(padded))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(warped, padding, padding);
                canvas.Flush();
            }
            warped.Dispose();
            return padded;
        }
        finally
        {
            if (!ReferenceEquals(normalized, source))
            {
                normalized.Dispose();
            }
        }
    }

    /// <summary>
    /// Applies the perspective described by the detected document corners while
    /// keeping every pixel of the original image. The output canvas is sized
    /// from the transformed full-image corners, so the detected quad is used
    /// only to estimate the homography, never as a crop rectangle.
    /// </summary>
    public static SKBitmap? PerspectiveKeepFullImage(
        SKBitmap source,
        IReadOnlyList<SKPoint> corners,
        int padding = 0)
    {
        if (corners.Count != 4)
        {
            return null;
        }

        try
        {
            ValidateQuad(corners);
        }
        catch
        {
            return null;
        }

        var width = Math.Max(
            2,
            (int)Math.Max(
                Distance(corners[0], corners[1]),
                Distance(corners[3], corners[2])));
        var height = Math.Max(
            2,
            (int)Math.Max(
                Distance(corners[0], corners[3]),
                Distance(corners[1], corners[2])));
        var normalized = source.ColorType == SKColorType.Bgra8888
            ? source
            : ToBgra(source);
        try
        {
            using var input = Mat.FromPixelData(
                normalized.Height,
                normalized.Width,
                MatType.CV_8UC4,
                normalized.Bytes);
            var sourceDocument = corners
                .Select(point => new OcvPoint2f(point.X, point.Y))
                .ToArray();
            var baseDestination = new[]
            {
                new OcvPoint2f(0, 0),
                new OcvPoint2f(width - 1, 0),
                new OcvPoint2f(width - 1, height - 1),
                new OcvPoint2f(0, height - 1),
            };
            using var baseMatrix = Cv2.GetPerspectiveTransform(
                sourceDocument,
                baseDestination);
            var sourceFrame = new[]
            {
                new SKPoint(0, 0),
                new SKPoint(source.Width - 1, 0),
                new SKPoint(source.Width - 1, source.Height - 1),
                new SKPoint(0, source.Height - 1),
            };
            var transformedFrame = sourceFrame
                .Select(point => TransformPoint(baseMatrix, point))
                .ToArray();
            if (transformedFrame.Any(point =>
                    !float.IsFinite(point.X) ||
                    !float.IsFinite(point.Y)))
            {
                return null;
            }

            var minX = transformedFrame.Min(point => point.X);
            var minY = transformedFrame.Min(point => point.Y);
            var maxX = transformedFrame.Max(point => point.X);
            var maxY = transformedFrame.Max(point => point.Y);
            var canvasWidth =
                (int)Math.Ceiling(maxX - minX) + padding * 2 + 1;
            var canvasHeight =
                (int)Math.Ceiling(maxY - minY) + padding * 2 + 1;
            if (canvasWidth < 2 ||
                canvasHeight < 2 ||
                canvasWidth > 20000 ||
                canvasHeight > 20000 ||
                (long)canvasWidth * canvasHeight >
                (long)source.Width * source.Height * 16)
            {
                return null;
            }

            var offsetX = -minX + padding;
            var offsetY = -minY + padding;
            var destination = new[]
            {
                new OcvPoint2f(offsetX, offsetY),
                new OcvPoint2f(offsetX + width - 1, offsetY),
                new OcvPoint2f(
                    offsetX + width - 1,
                    offsetY + height - 1),
                new OcvPoint2f(offsetX, offsetY + height - 1),
            };
            using var matrix = Cv2.GetPerspectiveTransform(
                sourceDocument,
                destination);
            using var output = new Mat();
            Cv2.WarpPerspective(
                input,
                output,
                matrix,
                new OcvSize(canvasWidth, canvasHeight),
                InterpolationFlags.Cubic,
                BorderTypes.Constant,
                new Scalar(255, 255, 255, 255));
            return FromBgraMat(output);
        }
        finally
        {
            if (!ReferenceEquals(normalized, source))
            {
                normalized.Dispose();
            }
        }
    }

    private static SKPoint TransformPoint(Mat matrix, SKPoint point)
    {
        var values = new double[9];
        Marshal.Copy(matrix.Data, values, 0, values.Length);
        var x = values[0] * point.X +
                values[1] * point.Y +
                values[2];
        var y = values[3] * point.X +
                values[4] * point.Y +
                values[5];
        var w = values[6] * point.X +
                values[7] * point.Y +
                values[8];
        if (Math.Abs(w) < 1e-9)
        {
            return new SKPoint(float.NaN, float.NaN);
        }

        return new SKPoint(
            (float)(x / w),
            (float)(y / w));
    }

    private static SKBitmap FromBgraMat(Mat source)
    {
        var result = new SKBitmap(
            new SKImageInfo(
                source.Width,
                source.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));
        var byteCount = checked(source.Rows * source.Cols * source.ElemSize());
        var bytes = new byte[byteCount];
        Marshal.Copy(source.Data, bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, result.GetPixels(), bytes.Length);
        return result;
    }

    public static SKBitmap ToBgra(SKBitmap source)
    {
        var result = new SKBitmap(
            new SKImageInfo(
                source.Width,
                source.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return result;
    }

    private static void ValidateQuad(IReadOnlyList<SKPoint> corners)
    {
        var minEdge = Math.Max(
            2.0,
            Math.Min(
                Distance(corners[0], corners[1]),
                Math.Min(
                    Distance(corners[1], corners[2]),
                    Math.Min(
                        Distance(corners[2], corners[3]),
                        Distance(corners[3], corners[0])))) * 0.02);
        for (var i = 0; i < 4; i++)
        {
            if (Distance(corners[i], corners[(i + 1) % 4]) < minEdge)
            {
                throw new InvalidOperationException("四角之间距离过近，无法生成透视。");
            }
        }

        double? sign = null;
        for (var i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            var c = corners[(i + 2) % 4];
            var cross =
                (b.X - a.X) * (c.Y - b.Y) -
                (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 0.01)
            {
                throw new InvalidOperationException("四角必须保持为凸四边形，不能重叠成一条线。");
            }

            var current = Math.Sign(cross);
            if (sign is null)
            {
                sign = current;
            }
            else if (sign.Value != current)
            {
                throw new InvalidOperationException("四角顺序已交叉；请保持左上、右上、右下、左下。");
            }
        }
    }

    private static double Distance(SKPoint left, SKPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
