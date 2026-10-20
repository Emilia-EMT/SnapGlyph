using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;
using Point = System.Windows.Point;

namespace SnapGlyph.App;

public partial class TransformEditorWindow : Window
{
    private readonly SKBitmap _original;
    private readonly DispatcherTimer _previewTimer;
    private DocAlignerDetector? _docAligner;
    private DocOrientationDetector? _docOrientation;
    private SKBitmap _current;
    private SKBitmap? _previewSource;
    private SKPoint[] _corners = [];
    private double _displayScale = 1;
    private double _imageLeft;
    private double _imageTop;
    private double _pendingAngle;
    private bool _suppressAngle;
    private bool _perspectiveDirty;
    private bool _previewBusy;
    private bool _autoAlignBusy;
    private bool _ocrSelectionMode;
    private bool _ocrSelecting;
    private bool _ocrSecondPointMode;
    private Point _ocrDragStart;
    private SKRectI? _ocrDragRect;
    private long _previewVersion;

    public TransformEditorWindow(
        SKBitmap source,
        Color selectionColor)
    {
        InitializeComponent();
        Icon = AppIcon.Load();
        var selectionBrush = new SolidColorBrush(selectionColor);
        OcrSelectionRect.Stroke = selectionBrush;
        OcrSelectionRect.Fill = new SolidColorBrush(
            Color.FromArgb(
                34,
                selectionColor.R,
                selectionColor.G,
                selectionColor.B));
        OcrModeBanner.BorderBrush = selectionBrush;
        _original = ImageTransforms.Clone(source);
        _current = ImageTransforms.Clone(source);
        _previewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35),
        };
        _previewTimer.Tick += PreviewTimer_Tick;
        Loaded += (_, _) => DisplayCurrent();
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _previewVersion++;
            OcrInputSurface.ReleaseMouseCapture();
            _original.Dispose();
            _current.Dispose();
            _previewSource?.Dispose();
            _docAligner?.Dispose();
            _docOrientation?.Dispose();
        };
    }

    public SKBitmap? Result { get; private set; }

    public Func<SKBitmap, Task>? OcrSelectionHandler { get; set; }

    public void ReportOcrStatus(string status)
    {
        EditorStatus.Text = status;
    }

    public void BeginOcrSelection()
    {
        if (!IsLoaded)
        {
            return;
        }
        if (_ocrSelectionMode)
        {
            EditorStatus.Text = "OCR 选区模式已启用，请在左侧图片上拖拽文字区域。";
            Focus();
            return;
        }

        try
        {
            var needsCommit =
                (_perspectiveDirty && PerspectiveCheck.IsChecked == true) ||
                Math.Abs(AngleSlider.Value) >= 0.01;
            if (needsCommit)
            {
                _previewTimer.Stop();
                ReplaceCurrent(BuildEditedBitmap());
            }

            EnterOcrSelectionMode();
            EditorStatus.Text = needsCommit
                ? "已自动应用当前旋转/透视。请在调整后的图片上拖拽文字区域；Esc 取消。"
                : "请在左侧图片上拖拽只包含文字的区域；Esc 取消。";
        }
        catch (Exception ex)
        {
            EditorStatus.Text = $"无法进入 OCR 框选：{ex.Message}";
        }
    }

    private void BeginOcrSelection_Click(object sender, RoutedEventArgs e)
    {
        BeginOcrSelection();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_ocrSelectionMode)
        {
            ExitOcrSelectionMode();
            EditorStatus.Text = "已取消 OCR 框选。";
            e.Handled = true;
            return;
        }

        if (_autoAlignBusy)
        {
            EditorStatus.Text = "AI 自动校正正在进行，请稍候。";
            e.Handled = true;
            return;
        }

        e.Handled = true;
        Close();
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        ReplaceCurrent(ImageTransforms.Rotate(_current, -90));
    }

    private void RotateRight_Click(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        ReplaceCurrent(ImageTransforms.Rotate(_current, 90));
    }

    private async void AutoAlign_Click(object sender, RoutedEventArgs e)
    {
        if (_autoAlignBusy)
        {
            return;
        }

        _autoAlignBusy = true;
        AutoAlignButton.IsEnabled = false;
        EditorStatus.Text = "AI 正在判断文档边界与文字方向...";
        try
        {
            _docAligner ??= await Task.Run(
                () => new DocAlignerDetector());
            _docOrientation ??= await Task.Run(
                () => new DocOrientationDetector());
            var boundaryDetector = new DocumentBoundaryDetector();
            using var snapshot = ImageTransforms.Clone(_current);
            var aligner = _docAligner;
            var orientationDetector = _docOrientation;
            var corrected = await Task.Run(
                () => AutoDocumentCorrector.Correct(
                    snapshot,
                    aligner,
                    orientationDetector,
                    boundaryDetector));
            if (corrected is null)
            {
                EditorStatus.Text =
                    "AI 未找到可靠的文档边界或方向；为避免误裁剪，图片未改动。请手动拖四角。";
                return;
            }

            _previewTimer.Stop();
            ReplaceCurrent(corrected.Bitmap);
            var changes = new List<string>();
            if (corrected.AppliedPerspective)
            {
                changes.Add(
                    $"透视校正（{corrected.BoundarySource}，不裁剪）");
            }
            if (corrected.AppliedRotationDegrees != 0)
            {
                changes.Add($"方向 {corrected.AppliedRotationDegrees}°");
            }
            var confidenceText = corrected.AppliedRotationDegrees != 0
                ? $"；方向置信度 {corrected.OrientationConfidence:0.00}"
                : "；方向保持不变";
            EditorStatus.Text =
                $"AI 已完成{string.Join("和", changes)}" +
                $"{confidenceText}。" +
                "可继续手动微调、框选 OCR 或完成。";
        }
        catch (Exception ex)
        {
            EditorStatus.Text = $"AI 自动校正失败：{ex.Message}";
        }
        finally
        {
            _autoAlignBusy = false;
            AutoAlignButton.IsEnabled = true;
        }
    }

    private void ApplyRotation_Click(object sender, RoutedEventArgs e)
    {
        if (Math.Abs(AngleSlider.Value) < 0.01)
        {
            return;
        }

        try
        {
            _previewTimer.Stop();
            var hadPerspective =
                _perspectiveDirty &&
                PerspectiveCheck.IsChecked == true;
            ReplaceCurrent(BuildEditedBitmap());
            EditorStatus.Text = hadPerspective
                ? "旋转与透视已一起应用。可以继续调整，或点击“完成”。"
                : "旋转已应用。可以继续调整透视，或点击“完成”。";
        }
        catch (Exception ex)
        {
            EditorStatus.Text = $"应用旋转失败：{ex.Message}";
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        ReplaceCurrent(ImageTransforms.Clone(_original));
        PerspectiveCheck.IsChecked = true;
    }

    private void AngleSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (AngleLabel is null || _suppressAngle)
        {
            return;
        }

        AngleLabel.Text = $"{e.NewValue:0}°";
        _pendingAngle = e.NewValue;
        RequestLivePreview();
    }

    private void EditorCanvas_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        UpdateCanvasLayout();
    }

    private void PerspectiveCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (EditorCanvas is null)
        {
            return;
        }

        UpdateOverlayVisibility();
        RequestLivePreview();
        if (PerspectiveCheck.IsChecked == true)
        {
            EditorStatus.Text =
                "拖动四角到目标边界；角点可以拉出图片。按 Ctrl+Shift+Z 在画布上框选 OCR。";
        }
        else
        {
            EditorStatus.Text =
                "透视已暂时关闭；点“完成”时不会应用四角调整。按 Ctrl+Shift+Z 在画布上框选 OCR。";
        }
    }

    private void Corner_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (PerspectiveCheck.IsChecked != true ||
            sender is not Thumb thumb ||
            _displayScale <= 0 ||
            !int.TryParse(thumb.Tag?.ToString(), out var index) ||
            index < 0 ||
            index >= _corners.Length)
        {
            return;
        }

        var handleSize = thumb.ActualWidth > 0 ? thumb.ActualWidth : thumb.Width;
        var half = handleSize / 2;
        const double edgePadding = 6;
        var sourceX =
            _corners[index].X + e.HorizontalChange / _displayScale;
        var sourceY =
            _corners[index].Y + e.VerticalChange / _displayScale;
        var minX = (edgePadding + half - _imageLeft) / _displayScale;
        var maxX =
            (EditorCanvas.ActualWidth - edgePadding - half - _imageLeft) /
            _displayScale;
        var minY = (edgePadding + half - _imageTop) / _displayScale;
        var maxY =
            (EditorCanvas.ActualHeight - edgePadding - half - _imageTop) /
            _displayScale;
        if (maxX < minX)
        {
            maxX = minX;
        }
        if (maxY < minY)
        {
            maxY = minY;
        }

        _corners[index] = new SKPoint(
            (float)Math.Clamp(sourceX, minX, maxX),
            (float)Math.Clamp(sourceY, minY, maxY));
        _perspectiveDirty = true;
        UpdateHandlePositions();
        UpdateQuad();
        RequestLivePreview();
        EditorStatus.Text =
            $"正在调整四角：图片坐标 " +
            $"{_corners[index].X:0}, {_corners[index].Y:0}";
    }

    private void ApplyPerspective_Click(object sender, RoutedEventArgs e)
    {
        ApplyPerspectiveInternal();
    }

    private bool ApplyPerspectiveInternal()
    {
        if (PerspectiveCheck.IsChecked != true)
        {
            EditorStatus.Text = "请先勾选“四角透视”。";
            return false;
        }

        if (!_perspectiveDirty)
        {
            EditorStatus.Text = "四角仍位于原图边界，没有需要应用的透视。";
            return true;
        }

        try
        {
            _previewTimer.Stop();
            var hadRotation = Math.Abs(AngleSlider.Value) >= 0.01;
            var transformed = BuildEditedBitmap();
            ReplaceCurrent(transformed);
            EditorStatus.Text = hadRotation
                ? "旋转与透视已一起应用。可以继续调整，或点击“完成”。"
                : "透视已应用。可以继续调整，或点击“完成”。";
            return true;
        }
        catch (Exception ex)
        {
            EditorStatus.Text = $"透视失败：{ex.Message}";
            return false;
        }
    }

    private void UpdateOverlayVisibility()
    {
        if (QuadPolygon is null)
        {
            return;
        }

        var visible = PerspectiveCheck.IsChecked == true;
        QuadPolygon.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        foreach (var thumb in new[] { Corner0, Corner1, Corner2, Corner3 })
        {
            thumb.Visibility = visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private SKPoint[] GetCornersInPreview()
    {
        if (_previewSource is null)
        {
            return [];
        }

        var scaleX = _previewSource.Width / (float)_current.Width;
        var scaleY = _previewSource.Height / (float)_current.Height;
        return _corners
            .Select(point => new SKPoint(
                point.X * scaleX,
                point.Y * scaleY))
            .ToArray();
    }

    private void DisplayCurrent()
    {
        _previewTimer.Stop();
        _previewVersion++;
        _pendingAngle = 0;
        _previewSource?.Dispose();
        _previewSource = ImageTransforms.ResizeForPreview(_current, 900);
        _corners =
        [
            new SKPoint(0, 0),
            new SKPoint(_current.Width, 0),
            new SKPoint(_current.Width, _current.Height),
            new SKPoint(0, _current.Height),
        ];
        _perspectiveDirty = false;

        PreviewImage.Source = BitmapSourceUtil.FromSkia(_current);
        _suppressAngle = true;
        AngleSlider.Value = 0;
        AngleLabel.Text = "0°";
        _suppressAngle = false;

        UpdateCanvasLayout();
        UpdateOverlayVisibility();
        ShowLivePreview(_previewSource);
        LivePreviewInfo.Text =
            $"当前图片 · {_current.Width}×{_current.Height}";
        EditorStatus.Text =
            "拖动四角到目标边界；角点可以拉出图片。按 Ctrl+Shift+Z 在画布上框选 OCR。";
    }

    private void UpdateCanvasLayout()
    {
        if (_previewSource is null ||
            EditorCanvas.ActualWidth < 2 ||
            EditorCanvas.ActualHeight < 2)
        {
            return;
        }

        var viewportWidth = EditorCanvas.ActualWidth;
        var viewportHeight = EditorCanvas.ActualHeight;
        var paddingX = Math.Clamp(viewportWidth * 0.11, 48, 118);
        var paddingY = Math.Clamp(viewportHeight * 0.11, 48, 118);
        var availableWidth = Math.Max(40, viewportWidth - paddingX * 2);
        var availableHeight = Math.Max(40, viewportHeight - paddingY * 2);
        var fitScale = Math.Min(
            availableWidth / _current.Width,
            availableHeight / _current.Height);
        _displayScale = Math.Max(0.01, Math.Min(2.2, fitScale));

        var displayWidth = _current.Width * _displayScale;
        var displayHeight = _current.Height * _displayScale;
        _imageLeft = (viewportWidth - displayWidth) / 2;
        _imageTop = (viewportHeight - displayHeight) / 2;

        ImageFrame.Width = displayWidth;
        ImageFrame.Height = displayHeight;
        Canvas.SetLeft(ImageFrame, _imageLeft);
        Canvas.SetTop(ImageFrame, _imageTop);
        PreviewImage.Width = displayWidth;
        PreviewImage.Height = displayHeight;
        Canvas.SetLeft(PreviewImage, _imageLeft);
        Canvas.SetTop(PreviewImage, _imageTop);
        OcrInputSurface.Width = viewportWidth;
        OcrInputSurface.Height = viewportHeight;

        UpdateHandlePositions();
        UpdateQuad();
    }

    private void UpdateHandlePositions()
    {
        var thumbs = new[] { Corner0, Corner1, Corner2, Corner3 };
        for (var i = 0; i < thumbs.Length && i < _corners.Length; i++)
        {
            var thumb = thumbs[i];
            var handleSize = thumb.Width > 0 ? thumb.Width : 32;
            Canvas.SetLeft(
                thumb,
                _imageLeft + _corners[i].X * _displayScale - handleSize / 2);
            Canvas.SetTop(
                thumb,
                _imageTop + _corners[i].Y * _displayScale - handleSize / 2);
        }
    }

    private void UpdateQuad()
    {
        if (_corners.Length != 4)
        {
            return;
        }

        QuadPolygon.Points = new PointCollection(
            _corners.Select(point => new Point(
                _imageLeft + point.X * _displayScale,
                _imageTop + point.Y * _displayScale)));
    }

    private void EnterOcrSelectionMode()
    {
        _ocrSelectionMode = true;
        _ocrSelecting = false;
        _ocrSecondPointMode = false;
        _ocrDragRect = null;
        OcrSelectionRect.Visibility = Visibility.Collapsed;
        OcrModeBanner.Visibility = Visibility.Visible;
        OcrInputSurface.Visibility = Visibility.Visible;
        PerspectiveCheck.IsEnabled = false;
        QuadPolygon.Visibility = Visibility.Collapsed;
        foreach (var thumb in new[] { Corner0, Corner1, Corner2, Corner3 })
        {
            thumb.Visibility = Visibility.Collapsed;
        }

        Focus();
    }

    private void ExitOcrSelectionMode()
    {
        if (!_ocrSelectionMode)
        {
            return;
        }

        _ocrSelectionMode = false;
        _ocrSelecting = false;
        _ocrSecondPointMode = false;
        _ocrDragRect = null;
        OcrInputSurface.ReleaseMouseCapture();
        OcrInputSurface.Visibility = Visibility.Collapsed;
        OcrSelectionRect.Visibility = Visibility.Collapsed;
        OcrModeBanner.Visibility = Visibility.Collapsed;
        PerspectiveCheck.IsEnabled = true;
        UpdateOverlayVisibility();
    }

    private void EditorCanvas_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_ocrSelectionMode || _previewSource is null)
        {
            return;
        }

        if (!_ocrSecondPointMode)
        {
            _ocrDragStart = ClampCanvasToImage(
                e.GetPosition(OcrInputSurface));
            _ocrDragRect = null;
            OcrSelectionRect.Visibility = Visibility.Collapsed;
        }
        _ocrSelecting = true;
        OcrInputSurface.CaptureMouse();
        e.Handled = true;
    }

    private void EditorCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_ocrSelecting)
        {
            return;
        }

        UpdateOcrDrag(e.GetPosition(OcrInputSurface));
        e.Handled = true;
    }

    private void UpdateOcrDrag(Point canvasPoint)
    {
        var current = ClampCanvasToImage(canvasPoint);
        var displayLeft = Math.Min(_ocrDragStart.X, current.X);
        var displayTop = Math.Min(_ocrDragStart.Y, current.Y);
        var displayRight = Math.Max(_ocrDragStart.X, current.X);
        var displayBottom = Math.Max(_ocrDragStart.Y, current.Y);
        var startImage = CanvasToImagePoint(_ocrDragStart);
        var endImage = CanvasToImagePoint(current);
        var left = Math.Min(startImage.X, endImage.X);
        var top = Math.Min(startImage.Y, endImage.Y);
        var right = Math.Max(startImage.X, endImage.X);
        var bottom = Math.Max(startImage.Y, endImage.Y);
        var rect = new SKRectI(
            Math.Clamp(
                (int)Math.Floor(left),
                0,
                Math.Max(0, _current.Width - 1)),
            Math.Clamp(
                (int)Math.Floor(top),
                0,
                Math.Max(0, _current.Height - 1)),
            Math.Clamp(
                (int)Math.Ceiling(right),
                1,
                _current.Width),
            Math.Clamp(
                (int)Math.Ceiling(bottom),
                1,
                _current.Height));
        if (rect.Width < 1 || rect.Height < 1)
        {
            return;
        }

        _ocrDragRect = rect;
        OcrSelectionRect.Visibility = Visibility.Visible;
        OcrSelectionRect.Width = displayRight - displayLeft;
        OcrSelectionRect.Height = displayBottom - displayTop;
        Canvas.SetLeft(OcrSelectionRect, displayLeft);
        Canvas.SetTop(OcrSelectionRect, displayTop);
        EditorStatus.Text =
            $"OCR 选区 {rect.Width}×{rect.Height}；松开鼠标开始识别。";
    }

    private async void EditorCanvas_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_ocrSelecting)
        {
            return;
        }

        _ocrSelecting = false;
        var releasePoint = e.GetPosition(OcrInputSurface);
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            releasePoint = OcrInputSurface.PointFromScreen(
                new Point(cursor.X, cursor.Y));
        }
        UpdateOcrDrag(releasePoint);
        OcrInputSurface.ReleaseMouseCapture();
        e.Handled = true;
        var selection = _ocrDragRect;
        if (selection is not { Width: >= 4, Height: >= 4 })
        {
            if (!_ocrSecondPointMode)
            {
                _ocrSelecting = false;
                _ocrSecondPointMode = true;
                OcrSelectionRect.Visibility = Visibility.Visible;
                OcrSelectionRect.Width = 2;
                OcrSelectionRect.Height = 2;
                Canvas.SetLeft(
                    OcrSelectionRect,
                    _ocrDragStart.X - 1);
                Canvas.SetTop(
                    OcrSelectionRect,
                    _ocrDragStart.Y - 1);
                EditorStatus.Text =
                    "已设置选区起点；请点击或拖到对角位置完成框选，Esc 取消。";
                return;
            }

            ExitOcrSelectionMode();
            EditorStatus.Text = selection is { } tiny
                ? $"OCR 选区过小（{tiny.Width}×{tiny.Height}），已取消。"
                : "没有取得有效选区，已取消。";
            return;
        }

        ExitOcrSelectionMode();
        var crop = CropCurrent(selection.Value);
        var handler = OcrSelectionHandler;
        if (handler is null)
        {
            crop.Dispose();
            EditorStatus.Text = "没有可用的 OCR 处理器。";
            return;
        }

        EditorStatus.Text =
            $"正在识别画布选区 {selection.Value.Width}×{selection.Value.Height}...";
        try
        {
            await handler(crop);
        }
        catch (Exception ex)
        {
            EditorStatus.Text = $"画布选区 OCR 失败：{ex.Message}";
        }
        finally
        {
            crop.Dispose();
        }
    }

    private void EditorCanvas_MouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_ocrSelectionMode)
        {
            return;
        }

        e.Handled = true;
        ExitOcrSelectionMode();
        EditorStatus.Text = "已取消 OCR 框选。";
    }

    private Point ClampCanvasToImage(Point canvasPoint)
    {
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            return canvasPoint;
        }

        var relative = OcrInputSurface.TranslatePoint(
            canvasPoint,
            PreviewImage);
        relative.X = Math.Clamp(
            relative.X,
            0,
            PreviewImage.ActualWidth);
        relative.Y = Math.Clamp(
            relative.Y,
            0,
            PreviewImage.ActualHeight);
        return PreviewImage.TranslatePoint(relative, OcrInputSurface);
    }

    private Point CanvasToImagePoint(Point canvasPoint)
    {
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
        {
            return new Point();
        }

        var relative = OcrInputSurface.TranslatePoint(
            canvasPoint,
            PreviewImage);
        return new Point(
            Math.Clamp(
                relative.X / PreviewImage.ActualWidth * _current.Width,
                0,
                _current.Width),
            Math.Clamp(
                relative.Y / PreviewImage.ActualHeight * _current.Height,
                0,
                _current.Height));
    }

    private SKBitmap CropCurrent(SKRectI selection)
    {
        var left = Math.Clamp(selection.Left, 0, Math.Max(0, _current.Width - 1));
        var top = Math.Clamp(selection.Top, 0, Math.Max(0, _current.Height - 1));
        var right = Math.Clamp(selection.Right, left + 1, _current.Width);
        var bottom = Math.Clamp(selection.Bottom, top + 1, _current.Height);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var result = new SKBitmap(
            new SKImageInfo(
                width,
                height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(
            _current,
            new SKRect(left, top, right, bottom),
            new SKRect(0, 0, width, height));
        canvas.Flush();
        return result;
    }

    private void RequestLivePreview()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (!_previewTimer.IsEnabled)
        {
            _previewTimer.Start();
        }
    }

    private async void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        if (_previewSource is null)
        {
            return;
        }
        if (_previewBusy)
        {
            _previewTimer.Start();
            return;
        }

        _previewBusy = true;
        var version = ++_previewVersion;
        var previewSource = ImageTransforms.Clone(_previewSource);
        var angle = _pendingAngle;
        var applyPerspective =
            PerspectiveCheck.IsChecked == true &&
            _perspectiveDirty;
        var corners = applyPerspective
            ? GetCornersInPreview()
            : [];
        try
        {
            var rendered = await Task.Run(() => BuildLivePreview(
                previewSource,
                (float)angle,
                applyPerspective,
                corners));
            if (version != _previewVersion || !IsLoaded)
            {
                rendered.Bitmap.Dispose();
                return;
            }

            ShowLivePreview(rendered.Bitmap);
            LivePreviewInfo.Text = rendered.Description;
            rendered.Bitmap.Dispose();
        }
        catch (Exception ex)
        {
            LivePreviewInfo.Text = $"预览失败：{ex.Message}";
        }
        finally
        {
            _previewBusy = false;
            if (version != _previewVersion)
            {
                RequestLivePreview();
            }
        }
    }

    private static PreviewRender BuildLivePreview(
        SKBitmap source,
        float angle,
        bool applyPerspective,
        IReadOnlyList<SKPoint> corners)
    {
        try
        {
            SKBitmap working;
            if (applyPerspective)
            {
                working = ImageTransforms.Perspective(source, corners);
            }
            else
            {
                working = ImageTransforms.Clone(source);
            }

            var description = "透视预览";
            if (Math.Abs(angle) >= 0.1f)
            {
                var rotated = ImageTransforms.Rotate(working, angle);
                working.Dispose();
                working = rotated;
                description = applyPerspective
                    ? $"透视 + 旋转 {angle:0}° 预览"
                    : $"旋转 {angle:0}° 预览";
            }
            else if (!applyPerspective)
            {
                description = "当前图片";
            }

            return new PreviewRender(
                working,
                $"{description} · {working.Width}×{working.Height}");
        }
        finally
        {
            source.Dispose();
        }
    }

    private void ShowLivePreview(SKBitmap bitmap)
    {
        LivePreviewImage.Source = BitmapSourceUtil.FromSkia(bitmap);
    }

    private void ReplaceCurrent(SKBitmap next)
    {
        _current.Dispose();
        _current = next;
        DisplayCurrent();
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _previewTimer.Stop();
            Result = BuildEditedBitmap();
            Close();
        }
        catch (Exception ex)
        {
            EditorStatus.Text = $"无法完成变换：{ex.Message}";
        }
    }

    private SKBitmap BuildEditedBitmap()
    {
        var result = ImageTransforms.Clone(_current);
        try
        {
            if (_perspectiveDirty && PerspectiveCheck.IsChecked == true)
            {
                var perspective = ImageTransforms.Perspective(
                    result,
                    _corners);
                result.Dispose();
                result = perspective;
            }
            if (Math.Abs(AngleSlider.Value) >= 0.01)
            {
                var rotated = ImageTransforms.Rotate(
                    result,
                    (float)AngleSlider.Value);
                result.Dispose();
                result = rotated;
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed record PreviewRender(
        SKBitmap Bitmap,
        string Description);

}
