using OpenCvSharp;
using SkiaSharp;

namespace SnapGlyph.App;

public sealed record AutoDocumentCorrection(
    SKBitmap Bitmap,
    bool AppliedPerspective,
    int AppliedRotationDegrees,
    float OrientationConfidence,
    float OrientationMargin,
    string? BoundarySource);

public static class AutoDocumentCorrector
{
    public static AutoDocumentCorrection? Correct(
        SKBitmap source,
        DocAlignerDetector aligner,
        DocOrientationDetector orientationDetector,
        DocumentBoundaryDetector boundaryDetector,
        int perspectivePadding = 8)
    {
        var working = ImageTransforms.Clone(source);
        try
        {
            var appliedPerspective = false;
            string? boundarySource = null;
            var areaModel = DocumentBoundaryDetector.FromModel(
                aligner.Detect(working, InterpolationFlags.Area),
                working);
            var cubicModel = DocumentBoundaryDetector.FromModel(
                aligner.Detect(working, InterpolationFlags.Cubic),
                working);
            var modelBoundary = areaModel;
            if (cubicModel is not null &&
                (modelBoundary is null ||
                 cubicModel.AreaRatio > modelBoundary.AreaRatio))
            {
                // Area resampling is better for full-page photos; cubic
                // preserves small label edges. Only accept a cubic-only
                // result when it is large enough to be a real boundary.
                if (modelBoundary is not null ||
                    cubicModel.AreaRatio >= 0.30)
                {
                    modelBoundary = cubicModel;
                }
            }
            var geometryBoundary = boundaryDetector.Detect(working);
            var boundary = DocumentBoundaryDetector.Choose(
                modelBoundary,
                geometryBoundary);
            if (boundary is not null &&
                !(boundary.AreaRatio > 0.985 &&
                  DocumentBoundaryDetector.IsNearlyFullFrame(
                      boundary.Corners,
                      working.Width,
                      working.Height)))
            {
                var warped = ImageTransforms.PerspectiveKeepFullImage(
                    working,
                    boundary.Corners,
                    perspectivePadding);
                if (warped is not null)
                {
                    working.Dispose();
                    working = warped;
                    appliedPerspective = true;
                    boundarySource = boundary.Source;
                }
            }

            var orientation = orientationDetector.Detect(working);
            var appliedRotation = 0;
            const float reliableOrientation = 0.32f;
            const float reliableOrientationMargin = 0.08f;
            if (orientation is
                {
                    RotationDegrees: not 0,
                    Confidence: >= reliableOrientation,
                    Margin: >= reliableOrientationMargin,
                })
            {
                appliedRotation = orientation.RotationDegrees;
                var rotated = ImageTransforms.Rotate(
                    working,
                    -appliedRotation);
                working.Dispose();
                working = rotated;
            }

            if (!appliedPerspective && appliedRotation == 0)
            {
                working.Dispose();
                return null;
            }

            var result = new AutoDocumentCorrection(
                working,
                appliedPerspective,
                appliedRotation,
                orientation?.Confidence ?? 0,
                orientation?.Margin ?? 0,
                boundarySource);
            working = null!;
            return result;
        }
        finally
        {
            working?.Dispose();
        }
    }
}
