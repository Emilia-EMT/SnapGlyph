using OpenCvSharp;
using SkiaSharp;
using OcvPoint = OpenCvSharp.Point;
using OcvSize = OpenCvSharp.Size;

namespace SnapGlyph.App;

public sealed record DocumentBoundaryCandidate(
    SKPoint[] Corners,
    double AreaRatio,
    double Rectangularity,
    string Source);

public sealed class DocumentBoundaryDetector
{
    private const int AnalysisMaxSide = 1000;
    private const double MinimumAreaRatio = 0.15;
    private const double MaximumAreaRatio = 0.995;

    public DocumentBoundaryCandidate? Detect(SKBitmap source)
    {
        if (source.Width < 32 || source.Height < 32)
        {
            return null;
        }

        using var bgra = ImageTransforms.ToBgra(source);
        using var sourceMat = Mat.FromPixelData(
            bgra.Height,
            bgra.Width,
            MatType.CV_8UC4,
            bgra.GetPixels(),
            bgra.RowBytes);
        var scale = Math.Min(
            1.0,
            AnalysisMaxSide /
            (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(32, (int)Math.Round(source.Width * scale));
        var height = Math.Max(32, (int)Math.Round(source.Height * scale));
        using var small = new Mat();
        Cv2.Resize(
            sourceMat,
            small,
            new OcvSize(width, height),
            interpolation: InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGRA2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new OcvSize(5, 5), 0);

        var candidates = new List<DocumentBoundaryCandidate>();
        using (var bright = new Mat())
        {
            var otsuThreshold = Cv2.Threshold(
                blurred,
                bright,
                0,
                255,
                ThresholdTypes.Binary | ThresholdTypes.Otsu);
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new OcvSize(9, 9));
            Cv2.MorphologyEx(
                bright,
                bright,
                MorphTypes.Close,
                kernel,
                iterations: 2);
            CollectCandidates(
                bright,
                source,
                width,
                height,
                candidates);

            // Dark labels on a light machine body are common in the target
            // photos. Reuse the Otsu threshold at a few darker cut points so
            // the full label, not only its bright barcode sticker, is found.
            foreach (var ratio in new[] { 0.75, 0.90, 1.05 })
            {
                using var dark = new Mat();
                var threshold = Math.Clamp(
                    otsuThreshold * ratio,
                    30,
                    190);
                Cv2.Threshold(
                    blurred,
                    dark,
                    threshold,
                    255,
                    ThresholdTypes.BinaryInv);
                Cv2.MorphologyEx(
                    dark,
                    dark,
                    MorphTypes.Close,
                    kernel,
                    iterations: 2);
                CollectCandidates(
                    dark,
                    source,
                    width,
                    height,
                    candidates);
            }
        }

        using (var edges = new Mat())
        {
            Cv2.Canny(blurred, edges, 50, 150);
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new OcvSize(5, 5));
            Cv2.Dilate(edges, edges, kernel);
            CollectCandidates(
                edges,
                source,
                width,
                height,
                candidates);
        }

        return candidates
            .OrderByDescending(candidate =>
                candidate.AreaRatio * 0.8 +
                candidate.Rectangularity * 0.2)
            .FirstOrDefault();
    }

    public static DocumentBoundaryCandidate? FromModel(
        SKPoint[]? corners,
        SKBitmap source)
    {
        if (corners is not { Length: 4 })
        {
            return null;
        }

        var ordered = OrderQuad(
            corners
                .Select(point => new SKPoint(
                    Math.Clamp(point.X, 0, source.Width - 1),
                    Math.Clamp(point.Y, 0, source.Height - 1)))
                .ToArray());
        if (!TryValidate(
                ordered,
                source.Width,
                source.Height,
                out var areaRatio,
                out var rectangularity))
        {
            return null;
        }

        return new DocumentBoundaryCandidate(
            ordered,
            areaRatio,
            rectangularity,
            "AI 模型");
    }

    public static DocumentBoundaryCandidate? Choose(
        DocumentBoundaryCandidate? model,
        DocumentBoundaryCandidate? geometry)
    {
        if (model is not null && model.AreaRatio >= MinimumAreaRatio)
        {
            // A model that found only a small inner block is less trustworthy
            // than a large page/label contour.
            if (geometry is not null &&
                geometry.AreaRatio >= 0.75 &&
                model.AreaRatio < 0.45)
            {
                return geometry;
            }

            return model;
        }

        return geometry is not null &&
               geometry.AreaRatio >= MinimumAreaRatio
            ? geometry
            : null;
    }

    public static bool IsNearlyFullFrame(
        SKPoint[] corners,
        int width,
        int height)
    {
        var marginX = Math.Max(2f, width * 0.01f);
        var marginY = Math.Max(2f, height * 0.01f);
        return corners.All(point =>
            point.X <= marginX ||
            point.X >= width - 1 - marginX ||
            point.Y <= marginY ||
            point.Y >= height - 1 - marginY);
    }

    private static void CollectCandidates(
        Mat mask,
        SKBitmap source,
        int width,
        int height,
        List<DocumentBoundaryCandidate> candidates)
    {
        using var working = mask.Clone();
        Cv2.FindContours(
            working,
            out OcvPoint[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);
        var minimumArea =
            MinimumAreaRatio * width * height;
        foreach (var contour in contours
                     .OrderByDescending(contour => Cv2.ContourArea(contour))
                     .Take(10))
        {
            var contourArea = Cv2.ContourArea(contour);
            if (contourArea < minimumArea)
            {
                continue;
            }

            var perimeter = Cv2.ArcLength(contour, true);
            foreach (var epsilon in new[]
                     {
                         0.01, 0.015, 0.02, 0.03,
                         0.04, 0.05, 0.07, 0.09,
                     })
            {
                var approximation = Cv2.ApproxPolyDP(
                    contour,
                    epsilon * perimeter,
                    true);
                if (approximation.Length != 4 ||
                    !Cv2.IsContourConvex(approximation))
                {
                    continue;
                }

                var points = OrderQuad(
                    approximation
                        .Select(point => new SKPoint(point.X, point.Y))
                        .ToArray());
                if (!TryValidate(
                        points,
                        width,
                        height,
                        out var areaRatio,
                        out var rectangularity))
                {
                    continue;
                }

                var mapped = points
                    .Select(point => new SKPoint(
                        point.X * source.Width / (float)width,
                        point.Y * source.Height / (float)height))
                    .ToArray();
                candidates.Add(new DocumentBoundaryCandidate(
                    mapped,
                    areaRatio,
                    rectangularity,
                    "轮廓检测"));
                break;
            }
        }
    }

    private static bool TryValidate(
        SKPoint[] points,
        int width,
        int height,
        out double areaRatio,
        out double rectangularity)
    {
        areaRatio = 0;
        rectangularity = 0;
        if (points.Length != 4)
        {
            return false;
        }

        var area = Math.Abs(SignedArea(points));
        areaRatio = area / (width * (double)height);
        if (areaRatio < MinimumAreaRatio ||
            areaRatio > MaximumAreaRatio)
        {
            return false;
        }

        var edge01 = Distance(points[0], points[1]);
        var edge12 = Distance(points[1], points[2]);
        var edge23 = Distance(points[2], points[3]);
        var edge30 = Distance(points[3], points[0]);
        var minimumEdge =
            Math.Max(2.0, Math.Min(width, height) * 0.04);
        if (Math.Min(
                Math.Min(edge01, edge12),
                Math.Min(edge23, edge30)) < minimumEdge)
        {
            return false;
        }

        var averageWidth = (edge01 + edge23) / 2;
        var averageHeight = (edge30 + edge12) / 2;
        if (averageWidth < 2 || averageHeight < 2)
        {
            return false;
        }

        var aspect = Math.Max(averageWidth, averageHeight) /
                     Math.Min(averageWidth, averageHeight);
        if (aspect > 8)
        {
            return false;
        }

        rectangularity =
            area / (averageWidth * averageHeight);
        return rectangularity >= 0.55;
    }

    private static SKPoint[] OrderQuad(SKPoint[] points)
    {
        var topLeft = points.MinBy(point => point.X + point.Y);
        var bottomRight = points.MaxBy(point => point.X + point.Y);
        var topRight = points.MaxBy(point => point.X - point.Y);
        var bottomLeft = points.MinBy(point => point.X - point.Y);
        return [topLeft, topRight, bottomRight, bottomLeft];
    }

    private static double SignedArea(
        IReadOnlyList<SKPoint> points)
    {
        var area = 0.0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }

        return area / 2;
    }

    private static double Distance(
        SKPoint left,
        SKPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
