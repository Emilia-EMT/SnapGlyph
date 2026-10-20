using System.Globalization;
using SnapGlyph.Core;
using SkiaSharp;

namespace SnapGlyph.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = CommandOptions.Parse(args);
            var imagePath = Path.GetFullPath(options.ImagePath);
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);

            using var source = SKBitmap.Decode(imagePath)
                ?? throw new InvalidOperationException($"Could not decode image: {imagePath}");
            if (options.QrMode)
            {
                var qr = QrScanner.Scan(source);
                if (qr is null)
                {
                    Console.WriteLine("BARCODE_NOT_FOUND");
                    return 2;
                }

                Console.WriteLine(
                    $"format={qr.Format} rotation={qr.RotationDegrees} text={qr.Text}");
                return 0;
            }

            using var engine = new SnapGlyphEngine(
                options.ModelQuality);
            var result = engine.Recognize(
                source,
                options.Crop,
                options.TimeoutMs);

            foreach (var row in result.Candidates)
            {
                var marker = row == result.Best ? "BEST" : "    ";
                Console.WriteLine(
                    $"{marker} " +
                    $"score={row.Score.ToString("0.0000", CultureInfo.InvariantCulture)} " +
                    $"ratio={row.DigitRatio.ToString("0.000", CultureInfo.InvariantCulture)} " +
                    $"box=({row.Box.Left},{row.Box.Top},{row.Box.Width},{row.Box.Height}) " +
                    $"digits={row.Digits} raw={row.Text}");
            }

            foreach (var row in result.SuppressedRows.Where(row => row.Digits.Length > 0))
            {
                Console.WriteLine(
                    $"SUPPRESSED duplicate_of={row.DuplicateOf} " +
                    $"overlap={row.Overlap.ToString("0.000", CultureInfo.InvariantCulture)} " +
                    $"box=({row.Box.Left},{row.Box.Top},{row.Box.Width},{row.Box.Height}) " +
                    $"digits={row.Digits} raw={row.Text}");
            }

            Console.WriteLine(
                $"elapsed_ms={result.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} " +
                $"timed_out={result.TimedOut} " +
                $"threads=auto cpu_logical={Environment.ProcessorCount} " +
                $"detector_long_side=" +
                $"{(result.DetectorLongSide?.ToString(CultureInfo.InvariantCulture) ?? "standard")} " +
                $"model={result.ModelQuality}");
            return result.Best is not null ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal sealed class CommandOptions
{
    public required string ImagePath { get; init; }
    public SKRectI? Crop { get; init; }
    public OcrModelQuality ModelQuality { get; init; } =
        OcrModelQuality.Standard;
    public int TimeoutMs { get; init; } =
        SnapGlyphEngine.DefaultTimeoutMs;
    public bool QrMode { get; init; }

    public static CommandOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException(
                "Usage: SnapGlyph.Cli <image> [--crop x,y,w,h] [--model standard|medium] [--timeout ms] [--scan|--qr]");
        }

        string? cropText = null;
        var modelQuality = OcrModelQuality.Standard;
        var timeoutMs = SnapGlyphEngine.DefaultTimeoutMs;
        var qrMode = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--crop" when i + 1 < args.Length:
                    cropText = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    modelQuality = args[++i].ToLowerInvariant() switch
                    {
                        "standard" or "small" => OcrModelQuality.Standard,
                        "high" or "medium" => OcrModelQuality.HighAccuracy,
                        _ => throw new ArgumentException(
                            "--model must be standard or medium"),
                    };
                    break;
                case "--timeout" when i + 1 < args.Length:
                    timeoutMs = int.Parse(
                        args[++i],
                        CultureInfo.InvariantCulture);
                    break;
                case "--scan":
                case "--barcode":
                case "--qr":
                    qrMode = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        SKRectI? crop = null;
        if (cropText is not null)
        {
            var parts = cropText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => int.Parse(part, CultureInfo.InvariantCulture))
                .ToArray();
            if (parts.Length != 4)
            {
                throw new ArgumentException("--crop must be x,y,w,h");
            }
            crop = new SKRectI(parts[0], parts[1], parts[0] + parts[2], parts[1] + parts[3]);
        }

        return new CommandOptions
        {
            ImagePath = args[0],
            Crop = crop,
            ModelQuality = modelQuality,
            TimeoutMs = Math.Clamp(timeoutMs, 100, 30000),
            QrMode = qrMode,
        };
    }
}
