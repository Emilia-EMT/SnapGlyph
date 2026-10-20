using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;
using SkiaSharp;
using ZXingCpp;
using CvPoint2f = OpenCvSharp.Point2f;
using CvSize = OpenCvSharp.Size;

namespace SnapGlyph.Core;

public sealed record QrScanResult(
    string Text,
    string Format,
    int RotationDegrees);

public static class QrScanner
{
    private const int TimeBudgetMs = 500;

    // Expand the bitmask into concrete formats. Passing the aggregate value
    // itself as one array item has triggered native errors in ZXingCpp.
    private static readonly BarcodeFormats SupportedFormats =
        BarcodeFormats.List(BarcodeFormat.AllReadable);

    public static QrScanResult? Scan(SKBitmap source)
    {
        var stopwatch = Stopwatch.StartNew();
        return ScanPass(
            source,
            SupportedFormats,
            true,
            stopwatch);
    }

    private static QrScanResult? ScanPass(
        SKBitmap source,
        BarcodeFormats formats,
        bool useOpenCvQrFallback,
        Stopwatch stopwatch)
    {
        if (BudgetExceeded(stopwatch))
        {
            return null;
        }

        var direct = Decode(source, formats, useOpenCvQrFallback);
        if (direct is not null)
        {
            return new QrScanResult(
                direct.Text,
                direct.Format,
                0);
        }

        var maxSide = Math.Max(source.Width, source.Height);
        if (maxSide >= 600)
        {
            if (BudgetExceeded(stopwatch))
            {
                return null;
            }
            using var reduced75 = Prepare(source, 0.75);
            var decoded75 = DecodeWithZxing(
                reduced75,
                formats,
                Binarizer.FixedThreshold,
                useColor: true);
            if (decoded75 is not null)
            {
                return new QrScanResult(
                    decoded75.Text,
                    decoded75.Format,
                    0);
            }
        }

        if (BudgetExceeded(stopwatch))
        {
            return null;
        }
        using var prepared = Prepare(source);
        var preparedDirect = Decode(
            prepared,
            formats,
            useOpenCvQrFallback);
        if (preparedDirect is not null)
        {
            return new QrScanResult(
                preparedDirect.Text,
                preparedDirect.Format,
                0);
        }

        if (useOpenCvQrFallback)
        {
            if (BudgetExceeded(stopwatch))
            {
                return null;
            }
            var linear = DecodeLinearWithOpenCvGeometry(source, formats);
            if (linear is not null)
            {
                return new QrScanResult(
                    linear.Text,
                    linear.Format,
                    0);
            }
        }

        if (maxSide >= 1000)
        {
            if (BudgetExceeded(stopwatch))
            {
                return null;
            }
            using var reduced50 = Prepare(source, 0.5);
            var decoded50 = DecodeWithZxing(
                reduced50,
                formats,
                Binarizer.FixedThreshold,
                useColor: true);
            if (decoded50 is not null)
            {
                return new QrScanResult(
                    decoded50.Text,
                    decoded50.Format,
                    0);
            }
        }

        foreach (var upscale in new[] { 2.0, 3.0 })
        {
            if (BudgetExceeded(stopwatch))
            {
                return null;
            }
            using var enlarged = Prepare(source, upscale);
            var decoded = DecodeWithZxing(enlarged, formats);
            if (decoded is not null)
            {
                return new QrScanResult(
                    decoded.Text,
                    decoded.Format,
                    0);
            }
        }

        // Fine-angle search near upright. Coarse 90-degree orientation is
        // already handled by ZXingCpp's TryRotate option.
        for (var step = 1; step <= 12; step++)
        {
            foreach (var angle in new[] { step * 4, -step * 4 })
            {
                if (BudgetExceeded(stopwatch))
                {
                    return null;
                }
                using var rotated = ImageTransformsForScanner.Rotate(
                    prepared,
                    angle);
                var decoded = Decode(
                    rotated,
                    formats,
                    useOpenCvQrFallback);
                if (decoded is null)
                {
                    continue;
                }

                return new QrScanResult(
                    decoded.Text,
                    decoded.Format,
                    angle);
            }
        }

        return null;
    }

    private static bool BudgetExceeded(Stopwatch stopwatch)
    {
        return stopwatch.ElapsedMilliseconds >= TimeBudgetMs;
    }

    private static DecodedBarcode? DecodeLinearWithOpenCvGeometry(
        SKBitmap bitmap,
        BarcodeFormats formats)
    {
        if (bitmap.Width < 32 || bitmap.Height < 16)
        {
            return null;
        }

        try
        {
            using var bgra = Mat.FromPixelData(
                bitmap.Height,
                bitmap.Width,
                MatType.CV_8UC4,
                bitmap.Bytes);
            using var bgr = new Mat();
            Cv2.CvtColor(
                bgra,
                bgr,
                ColorConversionCodes.BGRA2BGR);
            using var detector = new BarcodeDetector();
            detector.DetectAndDecode(
                bgr,
                out CvPoint2f[] points,
                out string[] _,
                out string[] _);
            if (points.Length < 4)
            {
                return null;
            }

            using var warped =
                ImageTransformsForScanner.PerspectiveCrop(
                    bitmap,
                    points.Take(4).ToArray());
            var direct = DecodeWithZxing(warped, formats);
            if (direct is not null)
            {
                return direct;
            }

            using var padded = Prepare(warped);
            return DecodeWithZxing(padded, formats);
        }
        catch
        {
            return null;
        }
    }

    private static DecodedBarcode? Decode(
        SKBitmap bitmap,
        BarcodeFormats formats,
        bool useOpenCvQrFallback)
    {
        var zxing = DecodeWithZxing(bitmap, formats);
        if (zxing is not null)
        {
            return zxing;
        }

        if (!useOpenCvQrFallback)
        {
            return null;
        }

        var text = DecodeQrWithOpenCv(bitmap);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new DecodedBarcode(text, "QRCode");
    }

    private static DecodedBarcode? DecodeWithZxing(
        SKBitmap bitmap,
        BarcodeFormats formats,
        Binarizer binarizer = Binarizer.LocalAverage,
        bool useColor = false)
    {
        try
        {
            using var converted = bitmap.Copy(
                useColor
                    ? SKColorType.Bgra8888
                    : SKColorType.Gray8);
            if (converted is null ||
                converted.Width < 16 ||
                converted.Height < 16)
            {
                return null;
            }

            var image = new ImageView(
                converted.GetPixels(),
                converted.Width,
                converted.Height,
                useColor ? ImageFormat.BGRA : ImageFormat.Lum,
                converted.RowBytes);
            using var reader = new BarcodeReader
            {
                Formats = formats,
                TryHarder = true,
                TryRotate = true,
                TryDownscale = true,
                TryInvert = true,
                Binarizer = binarizer,
            };
            var barcodes = reader.From(image);
            try
            {
                foreach (var barcode in barcodes)
                {
                    if (barcode.IsValid &&
                        !string.IsNullOrWhiteSpace(barcode.Text))
                    {
                        return new DecodedBarcode(
                            barcode.Text,
                            NormalizeFormat(barcode.Format));
                    }
                }
                return null;
            }
            finally
            {
                foreach (var barcode in barcodes)
                {
                    barcode.Dispose();
                }
            }
        }
        catch
        {
            // The OpenCV fallback remains available when native loading fails.
            return null;
        }
    }

    private static string? DecodeQrWithOpenCv(SKBitmap bitmap)
    {
        if (bitmap.Width < 16 || bitmap.Height < 16)
        {
            return null;
        }

        try
        {
            using var bgra = Mat.FromPixelData(
                bitmap.Height,
                bitmap.Width,
                MatType.CV_8UC4,
                bitmap.Bytes);
            using var bgr = new Mat();
            Cv2.CvtColor(
                bgra,
                bgr,
                ColorConversionCodes.BGRA2BGR);
            using var detector = new QRCodeDetector();
            using var straight = new Mat();
            var text = detector.DetectAndDecode(
                bgr,
                out CvPoint2f[] _,
                straight);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : text;
        }
        catch
        {
            // ZXing remains available if the OpenCV native runtime is absent.
            return null;
        }
    }

    private static string NormalizeFormat(BarcodeFormat format)
    {
        if (format == BarcodeFormat.QRCode ||
            format == BarcodeFormat.QRCodeModel2)
        {
            return "QRCode";
        }
        if (format == BarcodeFormat.MicroQRCode)
        {
            return "MicroQRCode";
        }
        if (format == BarcodeFormat.DataMatrix)
        {
            return "DataMatrix";
        }
        if (format == BarcodeFormat.Aztec)
        {
            return "Aztec";
        }
        if (format == BarcodeFormat.PDF417)
        {
            return "PDF417";
        }
        return format.ToString();
    }

    private static SKBitmap Prepare(
        SKBitmap source,
        double upscale = 1.0)
    {
        var maxSide = Math.Max(source.Width, source.Height);
        var scale = (maxSide < 480
            ? Math.Min(4.0, 720.0 / maxSide)
            : Math.Min(1.0, 1600.0 / maxSide)) * upscale;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var padding = Math.Clamp(
            (int)Math.Round(Math.Min(width, height) * 0.1),
            16,
            64);
        var output = new SKBitmap(
            new SKImageInfo(
                width + padding * 2,
                height + padding * 2,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));
        using var image = SKImage.FromBitmap(source);
        using var canvas = new SKCanvas(output);
        using var paint = new SKPaint
        {
            IsAntialias = true,
        };
        canvas.Clear(SKColors.White);
        canvas.DrawImage(
            image,
            new SKRect(0, 0, source.Width, source.Height),
            new SKRect(
                padding,
                padding,
                padding + width,
                padding + height),
            scale > 1
                ? upscale > 1
                    ? new SKSamplingOptions(
                        new SKCubicResampler(1f / 3f, 1f / 3f))
                    : new SKSamplingOptions(
                        SKFilterMode.Nearest,
                        SKMipmapMode.None)
                : new SKSamplingOptions(
                    new SKCubicResampler(1f / 3f, 1f / 3f)),
            paint);
        canvas.Flush();
        return output;
    }

    private static class ImageTransformsForScanner
    {
        public static SKBitmap Clone(SKBitmap source)
        {
            var output = new SKBitmap(source.Info);
            using var canvas = new SKCanvas(output);
            canvas.DrawBitmap(source, 0, 0);
            return output;
        }

        public static SKBitmap Rotate(SKBitmap source, float degrees)
        {
            var radians = degrees * Math.PI / 180.0;
            var width = Math.Max(
                1,
                (int)Math.Ceiling(
                    Math.Abs(source.Width * Math.Cos(radians)) +
                    Math.Abs(source.Height * Math.Sin(radians))));
            var height = Math.Max(
                1,
                (int)Math.Ceiling(
                    Math.Abs(source.Width * Math.Sin(radians)) +
                    Math.Abs(source.Height * Math.Cos(radians))));
            var output = new SKBitmap(
                new SKImageInfo(
                    width,
                    height,
                    SKColorType.Bgra8888,
                    SKAlphaType.Premul));
            using var image = SKImage.FromBitmap(source);
            using var canvas = new SKCanvas(output);
            using var paint = new SKPaint
            {
                IsAntialias = true,
            };
            canvas.Clear(SKColors.White);
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            var sampling = Math.Abs(degrees % 90f) < 0.01f
                ? new SKSamplingOptions(
                    SKFilterMode.Nearest,
                    SKMipmapMode.None)
                : new SKSamplingOptions(
                    new SKCubicResampler(1f / 3f, 1f / 3f));
            canvas.DrawImage(
                image,
                new SKRect(0, 0, source.Width, source.Height),
                sampling,
                paint);
            canvas.Flush();
            return output;
        }

        public static SKBitmap PerspectiveCrop(
            SKBitmap source,
            IReadOnlyList<CvPoint2f> points)
        {
            var ordered = OrderPoints(points);
            var width = Math.Max(
                8,
                (int)Math.Max(
                    Distance(ordered[0], ordered[1]),
                    Distance(ordered[3], ordered[2])));
            var height = Math.Max(
                8,
                (int)Math.Max(
                    Distance(ordered[0], ordered[3]),
                    Distance(ordered[1], ordered[2])));
            using var bgra = Mat.FromPixelData(
                source.Height,
                source.Width,
                MatType.CV_8UC4,
                source.Bytes);
            using var output = new Mat();
            var matrix = Cv2.GetPerspectiveTransform(
                ordered,
                [
                    new CvPoint2f(0, 0),
                    new CvPoint2f(width - 1, 0),
                    new CvPoint2f(width - 1, height - 1),
                    new CvPoint2f(0, height - 1),
                ]);
            Cv2.WarpPerspective(
                bgra,
                output,
                matrix,
                new CvSize(width, height),
                InterpolationFlags.Cubic,
                BorderTypes.Constant,
                Scalar.All(255));
            var result = new SKBitmap(
                new SKImageInfo(
                    width,
                    height,
                    SKColorType.Bgra8888,
                    SKAlphaType.Premul));
            var bytes = new byte[
                checked(output.Rows * output.Cols * output.ElemSize())];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            Marshal.Copy(bytes, 0, result.GetPixels(), bytes.Length);
            return result;
        }

        private static CvPoint2f[] OrderPoints(
            IReadOnlyList<CvPoint2f> points)
        {
            var sums = points.Select(point => point.X + point.Y).ToArray();
            var differences = points
                .Select(point => point.X - point.Y)
                .ToArray();
            return
            [
                points[Array.IndexOf(sums, sums.Min())],
                points[Array.IndexOf(differences, differences.Min())],
                points[Array.IndexOf(sums, sums.Max())],
                points[Array.IndexOf(differences, differences.Max())],
            ];
        }

        private static double Distance(CvPoint2f left, CvPoint2f right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private sealed record DecodedBarcode(
        string Text,
        string Format);
}
