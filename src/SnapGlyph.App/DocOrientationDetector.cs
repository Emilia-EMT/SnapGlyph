using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace SnapGlyph.App;

public sealed record DocOrientationResult(
    int RotationDegrees,
    float Confidence,
    float Margin);

public sealed class DocOrientationDetector : IDisposable
{
    private const int CropSize = 224;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StandardDeviation = [0.229f, 0.224f, 0.225f];
    private readonly InferenceSession _session;

    public DocOrientationDetector()
    {
        var modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "models",
            "docaligner",
            "PP-LCNet_x1_0_doc_ori.onnx");
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                "未找到文档方向模型。",
                modelPath);
        }

        using var options = new SessionOptions
        {
            IntraOpNumThreads = 0,
            InterOpNumThreads = 0,
            EnableCpuMemArena = false,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        _session = new InferenceSession(modelPath, options);
    }

    public DocOrientationResult? Detect(SKBitmap source)
    {
        var shortSide = Math.Min(source.Width, source.Height);
        if (shortSide < 16)
        {
            return null;
        }

        var scale = 256f / shortSide;
        var resizedWidth = Math.Max(
            CropSize,
            (int)Math.Ceiling(source.Width * scale));
        var resizedHeight = Math.Max(
            CropSize,
            (int)Math.Ceiling(source.Height * scale));
        using var resized = source.Resize(
            new SKImageInfo(
                resizedWidth,
                resizedHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul),
            SKFilterQuality.High);
        if (resized is null)
        {
            return null;
        }

        var left = (resizedWidth - CropSize) / 2;
        var top = (resizedHeight - CropSize) / 2;
        var pixels = resized.Bytes;
        var tensor = new DenseTensor<float>(
            new[] { 1, 3, CropSize, CropSize });
        for (var y = 0; y < CropSize; y++)
        {
            for (var x = 0; x < CropSize; x++)
            {
                var offset =
                    ((y + top) * resizedWidth + x + left) * 4;
                // The Paddle classifier uses OpenCV/BGR channel order.
                tensor[0, 0, y, x] =
                    (pixels[offset] / 255f - Mean[0]) /
                    StandardDeviation[0];
                tensor[0, 1, y, x] =
                    (pixels[offset + 1] / 255f - Mean[1]) /
                    StandardDeviation[1];
                tensor[0, 2, y, x] =
                    (pixels[offset + 2] / 255f - Mean[2]) /
                    StandardDeviation[2];
            }
        }

        using var results = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("x", tensor),
        ]);
        var logits = results[0].AsTensor<float>().ToArray();
        if (logits.Length < 4)
        {
            return null;
        }

        var bestIndex = 0;
        for (var i = 1; i < 4; i++)
        {
            if (logits[i] > logits[bestIndex])
            {
                bestIndex = i;
            }
        }

        var maxLogit = logits[bestIndex];
        var denominator = 0d;
        var probabilities = new double[4];
        for (var i = 0; i < 4; i++)
        {
            probabilities[i] = Math.Exp(logits[i] - maxLogit);
            denominator += probabilities[i];
        }

        for (var i = 0; i < 4; i++)
        {
            probabilities[i] /= denominator;
        }

        var confidence = (float)probabilities[bestIndex];
        var runnerUp = 0d;
        for (var i = 0; i < 4; i++)
        {
            if (i != bestIndex)
            {
                runnerUp = Math.Max(runnerUp, probabilities[i]);
            }
        }

        return new DocOrientationResult(
            bestIndex * 90,
            confidence,
            (float)(probabilities[bestIndex] - runnerUp));
    }

    public void Dispose() => _session.Dispose();
}
