using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using RapidOcrNet;
using SkiaSharp;
using CvSize = OpenCvSharp.Size;

namespace SnapGlyph.Core;

public enum OcrModelQuality
{
    Standard = 0,
    HighAccuracy = 1,
}

public sealed class SnapGlyphEngine : IDisposable
{
    public const int DefaultTimeoutMs = 800;

    private readonly RapidOcr _ocr = new();

    public SnapGlyphEngine(
        OcrModelQuality modelQuality = OcrModelQuality.Standard)
    {
        ModelQuality = modelQuality;
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel =
                GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            // 0 lets ONNX Runtime size its pools from the available CPU. Pinning
            // every logical processor oversubscribes small text-line inferences
            // and is measurably slower on hyper-threaded CPUs.
            IntraOpNumThreads = 0,
            InterOpNumThreads = 0,
        };
        // The CPU arena retains large detector workspaces after a full-image
        // OCR. Disabling it trades about 8% full-page latency for a much lower
        // process working set; line OCR latency is effectively unchanged.
        sessionOptions.EnableCpuMemArena = false;
        _ocr.InitModels(
            modelQuality == OcrModelQuality.HighAccuracy
                ? RapidOcrModelSet.PPOCRv6Medium
                : RapidOcrModelSet.PPOCRv6Small,
            sessionOptions);
    }

    public OcrModelQuality ModelQuality { get; }

    public SnapGlyphResult Recognize(
        SKBitmap source,
        SKRectI? crop = null,
        int timeoutMs = DefaultTimeoutMs)
    {
        var budgetMs = Math.Clamp(
            timeoutMs - 50,
            100,
            60000);
        using var cancellation = new CancellationTokenSource(
            budgetMs);
        return RecognizeCore(
            source,
            crop,
            allowRefinement: true,
            preferLongerRetry: false,
            cancellation.Token);
    }

    private SnapGlyphResult RecognizeCore(
        SKBitmap source,
        SKRectI? crop,
        bool allowRefinement,
        bool preferLongerRetry,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var cropRect = crop ?? new SKRectI(
            0,
            0,
            source.Width,
            source.Height);
        var shortSide = Math.Min(cropRect.Width, cropRect.Height);
        var longSide = Math.Max(cropRect.Width, cropRect.Height);
        var isLineLike =
            shortSide > 0 &&
            shortSide <= 400 &&
            longSide / (double)shortSide >= 3.0;
        if (!isLineLike)
        {
            if (crop is null &&
                allowRefinement &&
                OcrImage.HasDenseLowerText(source))
            {
                var fast = TryFastFullImageWindows(
                    source,
                    cropRect,
                    stopwatch,
                    cancellationToken);
                if (fast is not null &&
                    IsReliableNumber(fast, cropRect))
                {
                    return fast;
                }
            }

            var single = RecognizePass(
                source,
                crop,
                0,
                stopwatch,
                cancellationToken);
            if (IsReliableNumber(single, cropRect))
            {
                return single;
            }

            return allowRefinement &&
                   single.Candidates.Any(
                       row => row.Digits.Length > 0)
                ? RefineWithBands(
                    source,
                    cropRect,
                    single,
                    stopwatch,
                    fullImage: crop is null,
                    cancellationToken)
                : single;
        }

        var expansion = Math.Clamp(
            (int)Math.Round(shortSide * 0.12),
            6,
            24);
        var expandedCrop = ExpandCrop(
            cropRect,
            source.Width,
            source.Height,
            expansion);
        var native = RecognizePass(
            source,
            expandedCrop,
            0,
            stopwatch,
            cancellationToken);
        var padding = Math.Clamp(
            (int)Math.Round(shortSide * 0.08),
            4,
            24);
        var padded = RecognizePass(
            source,
            expandedCrop,
            padding,
            stopwatch,
            cancellationToken);
        var chosen = ChooseBetterPass(
            native,
            padded,
            stopwatch.Elapsed);
        if (IsReliableNumber(chosen, expandedCrop))
        {
            return chosen;
        }

        if (allowRefinement)
        {
            var beforeRefinement = chosen.Best;
            chosen = RefineWithBands(
                source,
                cropRect,
                chosen,
                stopwatch,
                fullImage: false,
                cancellationToken);
            if (IsReliableNumber(chosen, expandedCrop) &&
                IsBetterNumber(beforeRefinement, chosen.Best))
            {
                return chosen;
            }
        }

        var scale = Math.Clamp(
            (int)Math.Ceiling(120.0 / shortSide),
            2,
            4);
        using var upscaled = ScaleCrop(
            source,
            expandedCrop,
            scale);
        var retryPadding = Math.Clamp(
            (int)Math.Round(upscaled.Height * 0.05),
            4,
            16);
        var retryNative = RecognizePass(
            upscaled,
            null,
            0,
            stopwatch,
            cancellationToken);
        var retryPadded = RecognizePass(
            upscaled,
            null,
            retryPadding,
            stopwatch,
            cancellationToken);
        var retry = ChooseBetterPass(
            retryNative,
            retryPadded,
            stopwatch.Elapsed);
        return ChooseBetterPass(
            chosen,
            retry,
            stopwatch.Elapsed,
            preferLongerRetry || allowRefinement);
    }

    private SnapGlyphResult RefineWithBands(
        SKBitmap source,
        SKRectI cropRect,
        SnapGlyphResult chosen,
        Stopwatch stopwatch,
        bool fullImage,
        CancellationToken cancellationToken)
    {
        var result = chosen;
        foreach (var band in BuildBands(cropRect, fullImage))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var equalized = TryEnhancedPass(
                source,
                band,
                OcrImage.EnhancementMode.Equalize,
                cancellationToken);
            result = ChooseBetterPass(
                result,
                equalized,
                stopwatch.Elapsed,
                preferLonger: true);
            if (IsRefinedEnough(result, band))
            {
                break;
            }
            var best = equalized.Candidates.FirstOrDefault(
                row => row.IsObviousNumber);
            if (!equalized.Candidates.Any(
                    row => row.Digits.Length > 0))
            {
                var gray = TryEnhancedPass(
                    source,
                    band,
                    OcrImage.EnhancementMode.Gray,
                    cancellationToken);
                result = ChooseBetterPass(
                    result,
                    gray,
                    stopwatch.Elapsed,
                    preferLonger: true);
            }

            if (!result.Candidates.Any(row => row.IsObviousNumber))
            {
                var sharpened = TryEnhancedPass(
                    source,
                    band,
                    OcrImage.EnhancementMode.Sharpen,
                    cancellationToken);
                result = ChooseBetterPass(
                    result,
                    sharpened,
                    stopwatch.Elapsed,
                    preferLonger: true);
            }
            if (IsRefinedEnough(result, band))
            {
                break;
            }
        }

        return result;
    }

    private SnapGlyphResult? TryFastFullImageWindows(
        SKBitmap source,
        SKRectI cropRect,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        SnapGlyphResult? result = null;
        foreach (var band in BuildBands(
                     cropRect,
                     fullImage: true).Take(2))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var pass = TryEnhancedPass(
                source,
                band,
                OcrImage.EnhancementMode.Equalize,
                cancellationToken,
                includePadding: false);
            result = result is null
                ? pass
                : ChooseBetterPass(
                    result,
                    pass,
                    stopwatch.Elapsed,
                    preferLonger: true);
            if (result.Best is
                {
                    IsObviousNumber: true,
                    Score: >= 0.95f,
                } &&
                IsReliableNumber(result, cropRect))
            {
                break;
            }
        }

        return result;
    }

    private static bool IsRefinedEnough(
        SnapGlyphResult result,
        SKRectI band)
    {
        return result.Best is
               {
                   IsObviousNumber: true,
                   Score: >= 0.995f,
               } best &&
               best.Box.Width >= band.Width * 0.75f;
    }

    private SnapGlyphResult TryEnhancedPass(
        SKBitmap source,
        SKRectI crop,
        OcrImage.EnhancementMode mode,
        CancellationToken cancellationToken,
        bool includePadding = true)
    {
        using var baseCrop = OcrImage.CropOnWhite(
            source,
            crop,
            0);
        using var enhanced = OcrImage.EnhanceForOcr(
            baseCrop,
            2.0,
            1200,
            mode);
        var passStopwatch = Stopwatch.StartNew();
        var native = RecognizePass(
            enhanced,
            null,
            0,
            passStopwatch,
            cancellationToken);
        if (!includePadding)
        {
            return native;
        }

        var padding = Math.Clamp(
            (int)Math.Round(enhanced.Height * 0.05),
            4,
            16);
        var padded = RecognizePass(
            enhanced,
            null,
            padding,
            passStopwatch,
            cancellationToken);
        return ChooseBetterPass(
            native,
            padded,
            passStopwatch.Elapsed,
            preferLonger: true);
    }

    private static IEnumerable<SKRectI> BuildBands(
        SKRectI crop,
        bool fullImage)
    {
        if (fullImage)
        {
            var windows = new[]
            {
                (Left: 0.20, Width: 0.65, Top: 0.76, Height: 0.09),
                (Left: 0.22, Width: 0.64, Top: 0.78, Height: 0.08),
                (Left: 0.18, Width: 0.68, Top: 0.75, Height: 0.10),
            };
            foreach (var window in windows)
            {
                var left = crop.Left +
                           (int)Math.Round(
                               crop.Width * window.Left);
                var right = Math.Min(
                    crop.Right,
                    left + (int)Math.Round(
                        crop.Width * window.Width));
                var top = crop.Top +
                          (int)Math.Round(
                              crop.Height * window.Top);
                var bottom = Math.Min(
                    crop.Bottom,
                    top + (int)Math.Round(
                        crop.Height * window.Height));
                if (bottom - top >= 80)
                {
                    yield return new SKRectI(
                        left,
                        top,
                        right,
                        bottom);
                }
            }
            yield break;
        }

        var bottomHeight = Math.Min(120, crop.Height);
        var middleHeight = Math.Min(140, crop.Height);
        var middleTop =
            crop.Top + (crop.Height - middleHeight) / 2;
        var lowerMiddleHeight = Math.Min(130, crop.Height);
        var lowerMiddleTop =
            crop.Top + (int)Math.Round(crop.Height * 0.27);
        var bottomBand = new SKRectI(
            crop.Left,
            crop.Bottom - bottomHeight,
            crop.Right,
            crop.Bottom);
        var middleBand = new SKRectI(
            crop.Left,
            middleTop,
            crop.Right,
            middleTop + middleHeight);
        var lowerMiddleBand = new SKRectI(
            crop.Left,
            lowerMiddleTop,
            crop.Right,
            Math.Min(
                crop.Bottom,
                lowerMiddleTop + lowerMiddleHeight));
        static SKRectI Trim(
            SKRectI band,
            double leftFraction,
            double rightFraction)
        {
            var left = band.Left +
                       (int)Math.Round(
                           band.Width * leftFraction);
            var right = band.Right -
                        (int)Math.Round(
                            band.Width * rightFraction);
            return new SKRectI(
                left,
                band.Top,
                right,
                band.Bottom);
        }

        var bottomFull = Trim(bottomBand, 0, 0);
        var middleFocused = Trim(middleBand, 0.04, 0);
        var lowerMiddleFocused = Trim(
            lowerMiddleBand,
            0.126,
            0.053);
        if (crop.Height <= 180)
        {
            yield return middleFocused;
            yield return bottomFull;
            yield return lowerMiddleFocused;
        }
        else if (crop.Height <= 220)
        {
            yield return bottomFull;
            yield return middleFocused;
            yield return lowerMiddleFocused;
        }
        else
        {
            yield return lowerMiddleFocused;
            yield return middleFocused;
            yield return bottomFull;
        }
    }

    private static bool IsReliableNumber(
        SnapGlyphResult result,
        SKRectI crop)
    {
        var best = result.Candidates.FirstOrDefault(
            row => row.IsObviousNumber);
        return best is not null &&
               best.Box.Width >= crop.Width * 0.45f;
    }

    private static bool IsBetterNumber(
        OcrRow? before,
        OcrRow? after)
    {
        if (after is null || !after.IsObviousNumber)
        {
            return false;
        }
        if (before is null)
        {
            return true;
        }

        return after.Digits.Length > before.Digits.Length ||
               (after.Score > before.Score + 0.02f &&
                after.Digits.Length >= before.Digits.Length);
    }

    public void Dispose() => _ocr.Dispose();

    private SnapGlyphResult RecognizePass(
        SKBitmap source,
        SKRectI? crop,
        int padding,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        using var image = OcrImage.CropOnWhite(
            source,
            crop,
            padding);
        var options = SelectDetectionOptions(image.Width, image.Height);
        RapidOcrNet.OcrResult result;
        try
        {
            result = _ocr.Detect(
                image,
                options,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CreateTimedOutResult(totalStopwatch);
        }
        var rows = result.TextBlocks
            .Select((block, index) => OcrRow.FromBlock(block, index))
            .ToArray();
        var (kept, suppressed) = OcrDeduplicator.Deduplicate(rows);
        var candidates = kept
            .Where(row => row.Digits.Length > 0)
            .OrderByDescending(row => row.IsObviousNumber
                ? row.Digits.Length
                : 0)
            .ThenByDescending(row => row.Rank)
            .ThenByDescending(row => row.Score)
            .ToArray();

        return new SnapGlyphResult
        {
            AllRows = rows,
            KeptRows = kept,
            SuppressedRows = suppressed,
            Candidates = candidates,
            Best = candidates.FirstOrDefault(),
            Elapsed = totalStopwatch.Elapsed,
            DetectorLongSide = options.ImgResize > 0
                ? options.ImgResize
                : null,
            ModelQuality = ModelQuality,
            TimedOut = false,
        };
    }

    private SnapGlyphResult CreateTimedOutResult(
        Stopwatch totalStopwatch)
    {
        return new SnapGlyphResult
        {
            AllRows = [],
            KeptRows = [],
            SuppressedRows = [],
            Candidates = [],
            Best = null,
            Elapsed = totalStopwatch.Elapsed,
            DetectorLongSide = null,
            ModelQuality = ModelQuality,
            TimedOut = true,
        };
    }

    private static SnapGlyphResult ChooseBetterPass(
        SnapGlyphResult native,
        SnapGlyphResult padded,
        TimeSpan elapsed,
        bool preferLonger = false)
    {
        var nativeBest = native.Candidates.FirstOrDefault(
            row => row.IsObviousNumber);
        var paddedBest = padded.Candidates.FirstOrDefault(
            row => row.IsObviousNumber);
        var lengthAdvantage = 0;
        var scoreSlack = preferLonger ? 0.08 : 0.02;
        var minimumScore = preferLonger ? 0.88 : 0.90;

        SnapGlyphResult chosen;
        if (native.TimedOut && !padded.TimedOut)
        {
            chosen = padded;
        }
        else if (padded.TimedOut && !native.TimedOut)
        {
            chosen = native;
        }
        else if (nativeBest is null)
        {
            chosen = paddedBest is null
                ? MoreText(native, padded)
                : padded;
        }
        else if (paddedBest is null)
        {
            chosen = native;
        }
        else if (paddedBest.Digits.Length >
                     nativeBest.Digits.Length + lengthAdvantage &&
                 paddedBest.Score >= minimumScore &&
                 paddedBest.Score >= nativeBest.Score - scoreSlack)
        {
            chosen = padded;
        }
        else if (nativeBest.Digits.Length >
                     paddedBest.Digits.Length + lengthAdvantage &&
                 nativeBest.Score >= minimumScore &&
                 nativeBest.Score >= paddedBest.Score - scoreSlack)
        {
            chosen = native;
        }
        else
        {
            chosen = paddedBest.Score > nativeBest.Score
                ? padded
                : native;
        }

        return new SnapGlyphResult
        {
            AllRows = chosen.AllRows,
            KeptRows = chosen.KeptRows,
            SuppressedRows = chosen.SuppressedRows,
            Candidates = chosen.Candidates,
            Best = chosen.Best,
            Elapsed = elapsed,
            DetectorLongSide = chosen.DetectorLongSide,
            ModelQuality = chosen.ModelQuality,
            TimedOut = native.TimedOut || padded.TimedOut,
        };
    }

    private static SnapGlyphResult MoreText(
        SnapGlyphResult left,
        SnapGlyphResult right)
    {
        var leftChars = left.KeptRows.Sum(row => row.Text.Length);
        var rightChars = right.KeptRows.Sum(row => row.Text.Length);
        return rightChars > leftChars ? right : left;
    }

    private static RapidOcrOptions SelectDetectionOptions(
        int width,
        int height)
    {
        var longSide = Math.Max(width, height);
        var shortSide = Math.Min(width, height);
        var aspectRatio = longSide / (double)Math.Max(1, shortSide);

        // Full-page photos otherwise go through the detector at native size.
        // A 1400 px long side keeps the target number row legible while
        // materially reducing CPU time on 12 MP-class inputs.
        if (longSide > 1400)
        {
            return RapidOcrOptions.PPOCRv6 with
            {
                ImgResize = 1400,
                LimitSideLen = 0,
                MaxSideLen = 1400,
                RecMaxDegreeOfParallelism = -1,
            };
        }

        // PPOCRv6 normally upscales the short side to 736. A wide text line
        // then becomes a multi-thousand-pixel detector input. Keep line-like
        // selections at native resolution and cap only large crops.
        if (aspectRatio < 3.0 || shortSide > 400)
        {
            return RapidOcrOptions.PPOCRv6 with
            {
                RecMaxDegreeOfParallelism = -1,
            };
        }

        var detectorLongSide = Math.Clamp(shortSide * 12, 960, 1200);
        return RapidOcrOptions.PPOCRv6 with
        {
            ImgResize = detectorLongSide,
            LimitSideLen = 0,
            MaxSideLen = Math.Max(detectorLongSide, 1200),
            RecMaxDegreeOfParallelism = -1,
        };
    }

    private static SKRectI ExpandCrop(
        SKRectI crop,
        int sourceWidth,
        int sourceHeight,
        int expansion)
    {
        return new SKRectI(
            Math.Max(0, crop.Left - expansion),
            Math.Max(0, crop.Top - expansion),
            Math.Min(sourceWidth, crop.Right + expansion),
            Math.Min(sourceHeight, crop.Bottom + expansion));
    }

    private static SKBitmap ScaleCrop(
        SKBitmap source,
        SKRectI crop,
        int scale)
    {
        var width = checked(crop.Width * scale);
        var height = checked(crop.Height * scale);
        var output = new SKBitmap(
            width,
            height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var image = SKImage.FromBitmap(source);
        using var canvas = new SKCanvas(output);
        using var paint = new SKPaint
        {
            IsAntialias = true,
        };
        canvas.Clear(SKColors.White);
        canvas.DrawImage(
            image,
            new SKRect(
                crop.Left,
                crop.Top,
                crop.Right,
                crop.Bottom),
            new SKRect(0, 0, width, height),
            new SKSamplingOptions(
                new SKCubicResampler(1f / 3f, 1f / 3f)),
            paint);
        canvas.Flush();
        return output;
    }
}

public sealed class SnapGlyphResult
{
    public required IReadOnlyList<OcrRow> AllRows { get; init; }
    public required IReadOnlyList<OcrRow> KeptRows { get; init; }
    public required IReadOnlyList<OcrRow> SuppressedRows { get; init; }
    public required IReadOnlyList<OcrRow> Candidates { get; init; }
    public OcrRow? Best { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int? DetectorLongSide { get; init; }
    public required OcrModelQuality ModelQuality { get; init; }
    public bool TimedOut { get; init; }
}

public sealed class OcrRow
{
    public required string Text { get; init; }
    public required float Score { get; init; }
    public required SKRectI Box { get; init; }
    public int Index { get; init; }
    public int? DuplicateOf { get; set; }
    public double Overlap { get; set; }
    public int[] Members { get; set; } = [];

    public string Digits => new(Text.Where(IsAsciiDigit).ToArray());

    public bool IsObviousNumber =>
        Score >= 0.80 &&
        DigitRatio >= 0.80;

    public double DigitRatio
    {
        get
        {
            var visible = Text.Where(ch => !char.IsWhiteSpace(ch)).ToArray();
            return visible.Length == 0 ? 0 : (double)Digits.Length / visible.Length;
        }
    }

    public double Rank => Score * DigitRatio;

    public static OcrRow FromBlock(TextBlock block, int index)
    {
        var points = block.BoxPoints;
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        var score = block.CharScores is { Length: > 0 }
            ? block.CharScores.Average()
            : block.BoxScore;

        return new OcrRow
        {
            Text = block.Text,
            Score = score,
            Box = new SKRectI(left, top, right, bottom),
            Index = index,
        };
    }

    private static bool IsAsciiDigit(char ch) => ch is >= '0' and <= '9';
}

public static class OcrImage
{
    public enum EnhancementMode
    {
        Gray,
        Equalize,
        Sharpen,
    }

    public static SKBitmap CropOnWhite(
        SKBitmap source,
        SKRectI? crop,
        int padding = 0)
    {
        var rect = crop ?? new SKRectI(0, 0, source.Width, source.Height);
        if (rect.Left < 0 || rect.Top < 0 || rect.Right > source.Width ||
            rect.Bottom > source.Height || rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(crop), "Crop is outside the image");
        }

        padding = Math.Max(0, padding);
        var outputWidth = checked(rect.Width + padding * 2);
        var outputHeight = checked(rect.Height + padding * 2);
        var output = new SKBitmap(
            outputWidth,
            outputHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(
            source,
            rect,
            new SKRect(
                padding,
                padding,
                padding + rect.Width,
                padding + rect.Height));
        canvas.Flush();
        return output;
    }

    public static SKBitmap EnhanceForOcr(
        SKBitmap source,
        double scale,
        int maxLongSide,
        EnhancementMode mode)
    {
        var bgra = source.ColorType == SKColorType.Bgra8888
            ? source
            : ToBgra(source);
        try
        {
            using var input = Mat.FromPixelData(
                bgra.Height,
                bgra.Width,
                MatType.CV_8UC4,
                bgra.GetPixels(),
                bgra.RowBytes);
            using var gray = new Mat();
            Cv2.CvtColor(
                input,
                gray,
                ColorConversionCodes.BGRA2GRAY);
            using var processed = new Mat();
            if (mode == EnhancementMode.Sharpen)
            {
                using var clahe = Cv2.CreateCLAHE(
                    2.0,
                    new CvSize(8, 8));
                using var enhanced = new Mat();
                clahe.Apply(gray, enhanced);
                using var blurred = new Mat();
                Cv2.GaussianBlur(
                    enhanced,
                    blurred,
                    new CvSize(0, 0),
                    1.0);
                Cv2.AddWeighted(
                    enhanced,
                    1.6,
                    blurred,
                    -0.6,
                    0,
                    processed);
            }
            else if (mode == EnhancementMode.Equalize)
            {
                Cv2.EqualizeHist(gray, processed);
            }
            else
            {
                gray.CopyTo(processed);
            }

            var upscaledWidth = Math.Max(
                1,
                (int)Math.Round(bgra.Width * scale));
            var upscaledHeight = Math.Max(
                1,
                (int)Math.Round(bgra.Height * scale));
            using var upscaled = new Mat();
            Cv2.Resize(
                processed,
                upscaled,
                new CvSize(upscaledWidth, upscaledHeight),
                interpolation: InterpolationFlags.Cubic);

            using var capped = new Mat();
            var outputGray = upscaled;
            var longSide = Math.Max(
                upscaledWidth,
                upscaledHeight);
            if (longSide > maxLongSide)
            {
                var capScale = maxLongSide / (double)longSide;
                Cv2.Resize(
                    upscaled,
                    capped,
                    new CvSize(
                        Math.Max(
                            1,
                            (int)Math.Round(
                                upscaledWidth * capScale)),
                        Math.Max(
                            1,
                            (int)Math.Round(
                                upscaledHeight * capScale))),
                    interpolation: InterpolationFlags.Area);
                outputGray = capped;
            }

            using var output = new Mat();
            Cv2.CvtColor(
                outputGray,
                output,
                ColorConversionCodes.GRAY2BGRA);
            return FromBgraMat(output);
        }
        finally
        {
            if (!ReferenceEquals(bgra, source))
            {
                bgra.Dispose();
            }
        }
    }

    public static bool HasDenseLowerText(SKBitmap source)
    {
        if (source.Width < 64 || source.Height < 96)
        {
            return false;
        }

        var bgra = source.ColorType == SKColorType.Bgra8888
            ? source
            : ToBgra(source);
        try
        {
            using var input = Mat.FromPixelData(
                bgra.Height,
                bgra.Width,
                MatType.CV_8UC4,
                bgra.GetPixels(),
                bgra.RowBytes);
            var width = 400;
            var height = Math.Max(
                1,
                (int)Math.Round(
                    source.Height * (width / (double)source.Width)));
            using var small = new Mat();
            Cv2.Resize(
                input,
                small,
                new CvSize(width, height),
                interpolation: InterpolationFlags.Area);
            using var gray = new Mat();
            Cv2.CvtColor(
                small,
                gray,
                ColorConversionCodes.BGRA2GRAY);
            using var edges = new Mat();
            Cv2.Sobel(
                gray,
                edges,
                MatType.CV_32F,
                0,
                1,
                3);
            using var absolute = new Mat();
            Cv2.ConvertScaleAbs(edges, absolute);
            using var rowEnergy = new Mat();
            Cv2.Reduce(
                absolute,
                rowEnergy,
                ReduceDimension.Column,
                ReduceTypes.Sum,
                MatType.CV_32F);
            var total = 0d;
            for (var y = 0; y < height; y++)
            {
                total += rowEnergy.At<float>(y, 0);
            }
            var overallMean = total / height;
            if (overallMean <= 0)
            {
                return false;
            }

            var lowerStart = (int)Math.Round(height * 0.68);
            var lowerEnd = (int)Math.Round(height * 0.92);
            if (lowerEnd <= lowerStart)
            {
                return false;
            }

            var lowerTotal = 0d;
            var lowerMax = 0d;
            for (var y = lowerStart; y < lowerEnd; y++)
            {
                var value = rowEnergy.At<float>(y, 0);
                lowerTotal += value;
                lowerMax = Math.Max(lowerMax, value);
            }
            var lowerMean = lowerTotal / (lowerEnd - lowerStart);
            return lowerMean >= overallMean * 0.95 ||
                   lowerMax >= overallMean * 1.8;
        }
        finally
        {
            if (!ReferenceEquals(bgra, source))
            {
                bgra.Dispose();
            }
        }
    }

    private static SKBitmap ToBgra(SKBitmap source)
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

    private static SKBitmap FromBgraMat(Mat source)
    {
        var result = new SKBitmap(
            new SKImageInfo(
                source.Width,
                source.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));
        var byteCount = checked(
            source.Rows * source.Cols * source.ElemSize());
        var bytes = new byte[byteCount];
        Marshal.Copy(source.Data, bytes, 0, bytes.Length);
        Marshal.Copy(
            bytes,
            0,
            result.GetPixels(),
            bytes.Length);
        return result;
    }
}

public static class OcrDeduplicator
{
    private const double MinimumHorizontalOverlap = 0.70;
    private const double MinimumAreaOverlap = 0.35;

    public static (OcrRow[] Kept, OcrRow[] Suppressed) Deduplicate(
        IReadOnlyList<OcrRow> rows)
    {
        if (rows.Count == 0)
        {
            return ([], []);
        }

        var parent = Enumerable.Range(0, rows.Count).ToArray();
        var overlapByPair = new Dictionary<(int, int), double>();

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        void Union(int left, int right)
        {
            var a = Find(left);
            var b = Find(right);
            if (a != b)
            {
                parent[b] = a;
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                var areaOverlap = AreaOverlap(rows[i].Box, rows[j].Box);
                if (HorizontalOverlap(rows[i].Box, rows[j].Box) >= MinimumHorizontalOverlap &&
                    areaOverlap >= MinimumAreaOverlap)
                {
                    Union(i, j);
                    overlapByPair[(i, j)] = areaOverlap;
                }
            }
        }

        var clusters = Enumerable.Range(0, rows.Count)
            .GroupBy(Find)
            .Select(group => group.ToArray())
            .ToArray();
        var kept = new List<OcrRow>();
        var suppressed = new List<OcrRow>();

        foreach (var indices in clusters)
        {
            var bestIndex = indices
                .OrderByDescending(index => rows[index].Score)
                .ThenByDescending(index => rows[index].Rank)
                .First();
            var best = rows[bestIndex];
            best.Members = indices.Select(index => rows[index].Index).ToArray();
            kept.Add(best);

            foreach (var index in indices.Where(index => index != bestIndex))
            {
                var row = rows[index];
                row.DuplicateOf = best.Index;
                row.Overlap = overlapByPair
                    .Where(pair => indices.Contains(pair.Key.Item1) && indices.Contains(pair.Key.Item2))
                    .Select(pair => pair.Value)
                    .DefaultIfEmpty(0)
                    .Max();
                suppressed.Add(row);
            }
        }

        return (
            kept.OrderByDescending(row => row.Rank).ToArray(),
            suppressed.OrderByDescending(row => row.Score).ToArray());
    }

    private static (int Width, int Height) Intersection(SKRectI left, SKRectI right)
    {
        var x = Math.Max(left.Left, right.Left);
        var y = Math.Max(left.Top, right.Top);
        return (
            Math.Max(0, Math.Min(left.Right, right.Right) - x),
            Math.Max(0, Math.Min(left.Bottom, right.Bottom) - y));
    }

    private static double AreaOverlap(SKRectI left, SKRectI right)
    {
        var (width, height) = Intersection(left, right);
        if (width == 0 || height == 0)
        {
            return 0;
        }

        var smallerArea = Math.Min(
            (double)left.Width * left.Height,
            (double)right.Width * right.Height);
        return smallerArea == 0 ? 0 : width * height / smallerArea;
    }

    private static double HorizontalOverlap(SKRectI left, SKRectI right)
    {
        var (width, _) = Intersection(left, right);
        return width / (double)Math.Max(1, Math.Min(left.Width, right.Width));
    }
}
