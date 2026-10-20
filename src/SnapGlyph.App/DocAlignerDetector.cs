using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using SkiaSharp;
using OcvSize = OpenCvSharp.Size;

namespace SnapGlyph.App;

public sealed class DocAlignerDetector : IDisposable
{
    private const int InputSize = 256;
    private readonly InferenceSession _session;

    public DocAlignerDetector()
    {
        var modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "models",
            "docaligner",
            "lcnet050_p_multi_decoder_l3_d64_256_fp32.onnx");
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

    public SKPoint[]? Detect(
        SKBitmap source,
        InterpolationFlags interpolation = InterpolationFlags.Area)
    {
        if (source.Width < 16 || source.Height < 16)
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
        using var resizedMat = new Mat();
        Cv2.Resize(
            sourceMat,
            resizedMat,
            new OcvSize(InputSize, InputSize),
            interpolation: interpolation);
        if (resizedMat.Empty())
        {
            return null;
        }

        var pixels = new byte[InputSize * InputSize * 4];
        Marshal.Copy(
            resizedMat.Data,
            pixels,
            0,
            pixels.Length);
        var tensor = new DenseTensor<float>(
            new[] { 1, 3, InputSize, InputSize });
        for (var y = 0; y < InputSize; y++)
        {
            for (var x = 0; x < InputSize; x++)
            {
                var offset = (y * InputSize + x) * 4;
                // DocAligner's official preprocessing uses OpenCV BGR input.
                tensor[0, 0, y, x] = pixels[offset] / 255f;
                tensor[0, 1, y, x] = pixels[offset + 1] / 255f;
                tensor[0, 2, y, x] = pixels[offset + 2] / 255f;
            }
        }

        var inputValues = new[]
        {
            NamedOnnxValue.CreateFromTensor("img", tensor),
        };
        using var results = _session.Run(inputValues);
        var hasObject = results
            .First(result => result.Name == "has_obj")
            .AsTensor<float>()[0];
        if (hasObject <= 0.5f)
        {
            return null;
        }

        var points = results
            .First(result => result.Name == "points")
            .AsTensor<float>()
            .ToArray();
        if (points.Length < 8)
        {
            return null;
        }

        return
        [
            new SKPoint(points[0] * source.Width, points[1] * source.Height),
            new SKPoint(points[2] * source.Width, points[3] * source.Height),
            new SKPoint(points[4] * source.Width, points[5] * source.Height),
            new SKPoint(points[6] * source.Width, points[7] * source.Height),
        ];
    }

    public void Dispose() => _session.Dispose();
}
