using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SkiaSharp;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SnapGlyph.App;

internal sealed class CaptureOverlayWindow : Window
{
    private IReadOnlyList<CaptureCandidate> _candidates;
    private readonly Canvas _canvas;
    private readonly Rectangle _highlight;
    private readonly TextBlock _label;
    private readonly Line _verticalCrosshair;
    private readonly Line _horizontalCrosshair;
    private IReadOnlyList<CaptureCandidate> _atPointer = [];
    private int _index;
    private bool _dragging;
    private bool _rightPressed;
    private bool _finishing;
    private SKPointI _dragStart;
    private SKRectI? _dragRect;

    public CaptureOverlayWindow(
        CaptureWindowInfo target,
        IReadOnlyList<CaptureCandidate> candidates,
        Color selectionColor,
        Color crosshairColor)
    {
        _candidates = candidates;
        Target = target;

        Title = "屏幕选区";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Focusable = true;

        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)),
        };
        _canvas.Width = Width;
        _canvas.Height = Height;
        Content = _canvas;

        var crosshairBrush = new SolidColorBrush(
            Color.FromArgb(
                220,
                crosshairColor.R,
                crosshairColor.G,
                crosshairColor.B));
        _verticalCrosshair = new Line
        {
            Stroke = crosshairBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        _horizontalCrosshair = new Line
        {
            Stroke = crosshairBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_verticalCrosshair);
        _canvas.Children.Add(_horizontalCrosshair);

        _highlight = new Rectangle
        {
            Stroke = new SolidColorBrush(selectionColor),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(
                Color.FromArgb(
                    28,
                    selectionColor.R,
                    selectionColor.G,
                    selectionColor.B)),
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_highlight);

        _label = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(225, 32, 36, 43)),
            Padding = new Thickness(10, 7, 10, 7),
            FontSize = 13,
        };
        _canvas.Children.Add(_label);

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            UpdateAt(CurrentCursorScreenPoint());
            UpdateCrosshair(PointFromScreen(CurrentCursorScreenPoint()));
        };
        MouseMove += (_, e) =>
        {
            UpdateCrosshair(e.GetPosition(this));
            if (_dragging)
            {
                UpdateDragRect();
            }
            else
            {
                UpdateAt(CurrentCursorScreenPoint());
            }
        };
        MouseWheel += (_, e) =>
        {
            if (!_dragging && _atPointer.Count > 0)
            {
                _index = (_index + (e.Delta > 0 ? 1 : -1) + _atPointer.Count) % _atPointer.Count;
                SelectCurrent();
                e.Handled = true;
            }
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            _dragStart = ToSKPointI(CurrentCursorScreenPoint());
            _dragRect = null;
            CaptureMouse();
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            ReleaseMouseCapture();
            UpdateDragRect();
            if (_dragRect is { Width: >= 4, Height: >= 4 } rect)
            {
                Selected = new CaptureCandidate(rect, "手动矩形区域", "Region");
                Finish(true);
                return;
            }

            _dragRect = null;
            if (_atPointer.Count > 0)
            {
                Selected = _atPointer[_index];
                Finish(true);
                return;
            }

            Finish(false);
        };
        PreviewMouseRightButtonDown += (_, e) =>
        {
            _rightPressed = true;
            CaptureMouse();
            e.Handled = true;
        };
        PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_rightPressed)
            {
                return;
            }

            _rightPressed = false;
            ReleaseMouseCapture();
            Finish(false);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Finish(false);
            }
        };
    }

    public CaptureWindowInfo Target { get; }
    public CaptureCandidate? Selected { get; private set; }

    public void UpdateCandidates(IReadOnlyList<CaptureCandidate> candidates)
    {
        _candidates = candidates;
        if (!_dragging)
        {
            UpdateAt(CurrentCursorScreenPoint());
        }
    }

    private static Point CurrentCursorScreenPoint()
    {
        NativeMethods.GetCursorPos(out var point);
        return new Point(point.X, point.Y);
    }

    private static SKPointI ToSKPointI(Point point) =>
        new((int)Math.Round(point.X), (int)Math.Round(point.Y));

    private void Finish(bool accepted)
    {
        if (_finishing)
        {
            return;
        }

        _finishing = true;
        ReleaseMouseCapture();
        DialogResult = accepted;
    }

    private void UpdateDragRect()
    {
        var current = ToSKPointI(CurrentCursorScreenPoint());
        _dragRect = new SKRectI(
            Math.Min(_dragStart.X, current.X),
            Math.Min(_dragStart.Y, current.Y),
            Math.Max(_dragStart.X, current.X),
            Math.Max(_dragStart.Y, current.Y));
        if (_dragRect is { Width: >= 1, Height: >= 1 } rect)
        {
            RenderHighlight(rect);
            _label.Text =
                $"矩形选区  {rect.Width}×{rect.Height}\n" +
                "松开左键确认，右键取消";
            _label.Visibility = Visibility.Visible;
        }
    }

    private void UpdateCrosshair(Point point)
    {
        _verticalCrosshair.X1 = point.X;
        _verticalCrosshair.X2 = point.X;
        _verticalCrosshair.Y1 = 0;
        _verticalCrosshair.Y2 = Height;
        _horizontalCrosshair.X1 = 0;
        _horizontalCrosshair.X2 = Width;
        _horizontalCrosshair.Y1 = point.Y;
        _horizontalCrosshair.Y2 = point.Y;
    }

    private void UpdateAt(Point screenPoint)
    {
        _atPointer = _candidates
            .Where(candidate => candidate.ScreenRect.Contains(
                (int)Math.Round(screenPoint.X),
                (int)Math.Round(screenPoint.Y)))
            .OrderBy(candidate => candidate.Area)
            .ToArray();
        _index = 0;
        SelectCurrent();
    }

    private void SelectCurrent()
    {
        if (_atPointer.Count == 0)
        {
            _highlight.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Collapsed;
            return;
        }

        _index = Math.Clamp(_index, 0, _atPointer.Count - 1);
        var candidate = _atPointer[_index];
        RenderHighlight(candidate.ScreenRect);

        _label.Text =
            $"{candidate.Kind}  ·  {candidate.Name}\n" +
            $"深度 {_index + 1}/{_atPointer.Count}  ·  {candidate.ScreenRect.Width}×{candidate.ScreenRect.Height}  ·  " +
            "滚轮切换，左键确认，右键取消";
        _label.Visibility = Visibility.Visible;
    }

    private void RenderHighlight(SKRectI screenRect)
    {
        var topLeft = PointFromScreen(new Point(screenRect.Left, screenRect.Top));
        var bottomRight = PointFromScreen(new Point(screenRect.Right, screenRect.Bottom));
        var x = Math.Min(topLeft.X, bottomRight.X);
        var y = Math.Min(topLeft.Y, bottomRight.Y);
        var w = Math.Abs(bottomRight.X - topLeft.X);
        var h = Math.Abs(bottomRight.Y - topLeft.Y);

        _highlight.Visibility = Visibility.Visible;
        _highlight.Width = w;
        _highlight.Height = h;
        Canvas.SetLeft(_highlight, x);
        Canvas.SetTop(_highlight, y);
        Canvas.SetLeft(_label, Math.Max(12, x));
        Canvas.SetTop(_label, Math.Max(12, y - 58));
    }
}
