using System.Diagnostics;
using System.IO;
using SnapGlyph.App;
using SkiaSharp;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: TransformSmoke <image> <output-prefix> [--ai] [--orientation] [--auto [--repeat N]]");
    return 2;
}

using var source = SKBitmap.Decode(args[0])
    ?? throw new InvalidOperationException("Could not decode image.");
if (args.Skip(2).Any(argument => argument == "--ai"))
{
    using var detector = new DocAlignerDetector();
    var aiCorners = detector.Detect(source);
    if (aiCorners is null)
    {
        Console.WriteLine("AI no document detected");
        return 3;
    }

    Console.WriteLine(
        string.Join(
            " ",
            aiCorners.Select((point, index) =>
                $"{index}=({point.X:0.#},{point.Y:0.#})")));
    return 0;
}

if (args.Skip(2).Any(argument => argument == "--orientation"))
{
    using var detector = new DocOrientationDetector();
    var orientation = detector.Detect(source);
    if (orientation is null)
    {
        Console.WriteLine("orientation not detected");
        return 3;
    }

    Console.WriteLine(
        $"rotation={orientation.RotationDegrees} " +
        $"confidence={orientation.Confidence:0.0000} " +
        $"margin={orientation.Margin:0.0000}");
    return 0;
}

if (args.Skip(2).Any(argument => argument == "--auto"))
{
    var repeat = 1;
    var repeatIndex = Array.IndexOf(args, "--repeat");
    if (repeatIndex >= 0 &&
        repeatIndex + 1 < args.Length)
    {
        repeat = Math.Max(1, int.Parse(args[repeatIndex + 1]));
    }

    using var aligner = new DocAlignerDetector();
    using var orientationDetector = new DocOrientationDetector();
    var boundaryDetector = new DocumentBoundaryDetector();
    var process = Process.GetCurrentProcess();
    process.Refresh();
    var before = process.WorkingSet64;
    var total = Stopwatch.StartNew();
    AutoDocumentCorrection? last = null;
    var failures = 0;
    for (var iteration = 0; iteration < repeat; iteration++)
    {
        try
        {
            var correction = AutoDocumentCorrector.Correct(
                source,
                aligner,
                orientationDetector,
                boundaryDetector);
            if (correction is null)
            {
                failures++;
                continue;
            }

            last?.Bitmap.Dispose();
            last = correction;
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"iteration {iteration} failed: {ex.Message}");
        }
    }
    total.Stop();
    if (last is null)
    {
        Console.WriteLine(
            $"AI no reliable correction iterations={repeat} failures={failures}");
        return 3;
    }

    Save(last.Bitmap, $"{args[1]}-auto.png");
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    process.Refresh();
    Console.WriteLine(
        $"boundary={last.BoundarySource ?? "none"} " +
        $"perspective={last.AppliedPerspective} " +
        $"rotation={last.AppliedRotationDegrees} " +
        $"confidence={last.OrientationConfidence:0.0000} " +
        $"margin={last.OrientationMargin:0.0000} " +
        $"iterations={repeat} failures={failures} " +
        $"avg_ms={total.Elapsed.TotalMilliseconds / repeat:0.0} " +
        $"working_set_mb={process.WorkingSet64 / 1024d / 1024d:0.0} " +
        $"before_mb={before / 1024d / 1024d:0.0} " +
        $"size={last.Bitmap.Width}x{last.Bitmap.Height}");
    last.Bitmap.Dispose();
    return 0;
}

using var rotated = ImageTransforms.Rotate(source, 17);
Save(rotated, $"{args[1]}-rotated.png");

var corners = new[]
{
    new SKPoint(8, 10),
    new SKPoint(rotated.Width - 12, 4),
    new SKPoint(rotated.Width - 4, rotated.Height - 8),
    new SKPoint(12, rotated.Height - 3),
};
using var perspective = ImageTransforms.Perspective(rotated, corners);
Save(perspective, $"{args[1]}-rotated-perspective.png");
Console.WriteLine($"OK {source.Width}x{source.Height} -> {rotated.Width}x{rotated.Height} -> {perspective.Width}x{perspective.Height}");
return 0;

static void Save(SKBitmap bitmap, string path)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
}
