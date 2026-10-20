using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SnapGlyph.Core;
using SkiaSharp;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace SnapGlyph.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<CandidateRow> _candidateRows = [];
    private SnapGlyphEngine? _engine;
    private SKBitmap? _source;
    private SKRectI? _selection;
    private Point _dragStart;
    private bool _dragging;
    private bool _panningImage;
    private bool _fitImageToViewport = true;
    private Point _panStart;
    private double _panStartOffsetX;
    private double _panStartOffsetY;
    private double _imageZoom = 1;
    private double _imageOffsetX;
    private double _imageOffsetY;
    private bool _busy;
    private bool _captureInProgress;
    private bool _editorOcrBusy;
    private SnapGlyphResult? _lastOcrResult;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private HotkeySettings _hotkeys = HotkeySettings.Load();
    private bool _hotkeysRegistered;
    private TransformEditorWindow? _activeTransformWindow;
    private TrayIcon? _tray;
    private bool _isExiting;
    private const int ScreenshotHotKeyId = 0x4F4C;
    private const int OcrHotKeyId = 0x4F4D;
    private const int TransformHotKeyId = 0x4F4E;
    private const int QrHotKeyId = 0x4F4F;
    private const double ImageViewPadding = 28;
    private const double MinimumImageZoom = 0.02;
    private const double MaximumImageZoom = 8;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppIdentity.WindowTitle;
        Icon = AppIcon.Load();
        ApplyUserPreferences();
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        CandidateList.ItemsSource = _candidateRows;
        var startupImage = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(File.Exists);
        if (startupImage is not null)
        {
            Loaded += async (_, _) => await LoadImageAsync(startupImage);
        }
        SourceInitialized += OnSourceInitialized;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            CleanupHotKey();
            _tray?.Dispose();
            _tray = null;
            _engine?.Dispose();
            _source?.Dispose();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        ForegroundTracker.Start(_hwnd);
        _tray = new TrayIcon(
            ShowMainWindow,
            () => Dispatcher.BeginInvoke(async () => await StartCaptureAsync()),
            () => Dispatcher.BeginInvoke(async () => await HandleOcrHotkeyAsync()),
            () => Dispatcher.BeginInvoke(async () => await StartQrScanAsync()),
            () => _hotkeys.NotificationDurationMs,
            () => Dispatcher.BeginInvoke(OpenHotkeySettings),
            ExitApplication);
        if (!TryRegisterHotkeys(_hotkeys))
        {
            HintText.Text = "快捷键注册失败；请打开“快捷键”修改冲突组合。";
        }
        else
        {
            UpdateHotkeyHint();
        }
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == NativeMethods.WmHotKey)
        {
            switch (wParam.ToInt32())
            {
                case ScreenshotHotKeyId:
                    handled = true;
                    Dispatcher.BeginInvoke(async () => await StartCaptureAsync());
                    break;
                case OcrHotKeyId:
                    handled = true;
                    Dispatcher.BeginInvoke(async () => await HandleOcrHotkeyAsync());
                    break;
                case TransformHotKeyId:
                    handled = true;
                    Dispatcher.BeginInvoke(async () => await HandleTransformHotkeyAsync());
                    break;
                case QrHotKeyId:
                    handled = true;
                    Dispatcher.BeginInvoke(async () => await StartQrScanAsync());
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void CleanupHotKey()
    {
        if (_hotkeysRegistered)
        {
            UnregisterHotkeys();
        }
        ForegroundTracker.Stop();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _tray?.ShowMessage(
            AppIdentity.Name,
            "程序已留在后台。可用快捷键继续截图、OCR 或扫码。");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _busy)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
    }

    private bool TryRegisterHotkeys(HotkeySettings settings)
    {
        UnregisterHotkeys();
        var bindings = new[]
        {
            (ScreenshotHotKeyId, settings.Screenshot),
            (OcrHotKeyId, settings.Ocr),
            (TransformHotKeyId, settings.Transform),
            (QrHotKeyId, settings.Qr),
        };
        foreach (var (id, binding) in bindings)
        {
            if (!NativeMethods.RegisterHotKey(
                    _hwnd,
                    id,
                    binding.NativeModifiers | NativeMethods.ModNoRepeat,
                    binding.NativeKey))
            {
                NativeMethods.UnregisterHotKey(_hwnd, ScreenshotHotKeyId);
                NativeMethods.UnregisterHotKey(_hwnd, OcrHotKeyId);
                NativeMethods.UnregisterHotKey(_hwnd, TransformHotKeyId);
                NativeMethods.UnregisterHotKey(_hwnd, QrHotKeyId);
                StatusText.Text = $"快捷键 {binding.Display} 注册失败，可能已被其他程序占用。";
                return false;
            }
        }

        _hotkeysRegistered = true;
        _hotkeys = settings;
        return true;
    }

    private void UnregisterHotkeys()
    {
        if (!_hotkeysRegistered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_hwnd, ScreenshotHotKeyId);
        NativeMethods.UnregisterHotKey(_hwnd, OcrHotKeyId);
        NativeMethods.UnregisterHotKey(_hwnd, TransformHotKeyId);
        NativeMethods.UnregisterHotKey(_hwnd, QrHotKeyId);
        _hotkeysRegistered = false;
    }

    private void UpdateHotkeyHint()
    {
        HintText.Text =
            $"截图 {_hotkeys.Screenshot.Display} · OCR {_hotkeys.Ocr.Display} · " +
            $"透视 {_hotkeys.Transform.Display} · 扫码 {_hotkeys.Qr.Display}；" +
            "拖拽矩形可手动选区。";
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await StartCaptureAsync();
    }

    private async void QrButton_Click(object sender, RoutedEventArgs e)
    {
        await StartQrScanAsync();
    }

    private async Task StartCaptureAsync(bool runOcrAfter = false)
    {
        if (_busy || _captureInProgress)
        {
            return;
        }

        _captureInProgress = true;
        try
        {
            await RunCaptureAsync(runOcrAfter);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"截图失败：{ex.Message}";
        }
        finally
        {
            _captureInProgress = false;
            SetBusy(false, null);
        }
    }

    private async Task RunCaptureAsync(bool runOcrAfter)
    {
        var capture = await SelectCaptureAsync();
        if (capture is null)
        {
            StatusText.Text = "截图已取消。";
            return;
        }

        var selected = capture.Candidate;
        ReplaceSource(
            capture.Bitmap,
            $"截图：{selected.Kind} {selected.Name}");
        if (runOcrAfter)
        {
            SetBusy(false, null);
            await RunOcrAsync(_hotkeys.ShowMainAfterOcr);
        }
        else
        {
            try
            {
                Clipboard.SetImage(BitmapSourceUtil.FromSkia(capture.Bitmap));
                StatusText.Text =
                    $"截图已复制到剪贴板：{selected.Kind}（{selected.ScreenRect.Width}×{selected.ScreenRect.Height}）。";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"截图成功，但写入剪贴板失败：{ex.Message}";
            }

            if (_hotkeys.ShowMainAfterScreenshot)
            {
                ShowMainWindow();
            }
            else if (!IsVisible)
            {
                _tray?.ShowMessage(
                    "截图已复制",
                    $"{selected.ScreenRect.Width}×{selected.ScreenRect.Height} 图片已进入剪贴板。");
            }
        }
    }

    private async Task<CaptureSelection?> SelectCaptureAsync()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var targetHandle =
            foreground == IntPtr.Zero ||
            NativeMethods.IsCurrentProcessWindow(foreground)
                ? ForegroundTracker.LastExternal
                : foreground;
        if (targetHandle == IntPtr.Zero ||
            NativeMethods.IsCurrentProcessWindow(targetHandle))
        {
            throw new InvalidOperationException("没有可截取的前台目标窗口。");
        }

        var target = CaptureEnumerator.GetWindowInfo(targetHandle);
        if (target.Handle == IntPtr.Zero ||
            target.Bounds.Width <= 0 ||
            target.Bounds.Height <= 0)
        {
            throw new InvalidOperationException("没有可截取的前台目标窗口。");
        }

        SetBusy(true, "正在枚举前台控件...");
        var candidates = CaptureEnumerator.EnumerateFast(target);
        var overlay = new CaptureOverlayWindow(
            target,
            candidates,
            _hotkeys.GetSelectionColor(),
            _hotkeys.GetCrosshairColor());
        overlay.Loaded += (_, _) =>
            _ = UpdateOverlayCandidatesAsync(overlay, target);
        var dialogResult = overlay.ShowDialog();
        if (dialogResult != true || overlay.Selected is not { } selected)
        {
            return null;
        }

        SetBusy(true, "正在截取选中控件...");
        var bitmap = await Task.Run(() =>
            selected.Kind == "Region"
                ? CaptureService.CaptureScreen(selected.ScreenRect)
                : CaptureService.Capture(target.Handle, selected.ScreenRect));
        return new CaptureSelection(bitmap, selected);
    }

    private static async Task UpdateOverlayCandidatesAsync(
        CaptureOverlayWindow overlay,
        CaptureWindowInfo target)
    {
        try
        {
            var candidates = await Task.Run(() => CaptureEnumerator.Enumerate(target));
            if (overlay.IsLoaded)
            {
                _ = overlay.Dispatcher.BeginInvoke(
                    new Action(() => overlay.UpdateCandidates(candidates)));
            }
        }
        catch
        {
            // Fast HWND candidates remain usable if UIA is unavailable.
        }
    }

    private async Task HandleOcrHotkeyAsync()
    {
        if (_activeTransformWindow is
            {
                IsVisible: true,
                IsActive: true,
            } transform)
        {
            transform.BeginOcrSelection();
            return;
        }

        await StartCaptureAsync(runOcrAfter: true);
    }

    private async Task StartQrScanAsync()
    {
        if (_busy || _captureInProgress)
        {
            return;
        }

        _captureInProgress = true;
        try
        {
            await RunQrScanAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"扫码失败：{ex.Message}";
            _tray?.ShowMessage("扫码失败", ex.Message);
        }
        finally
        {
            _captureInProgress = false;
            SetBusy(false, null);
        }
    }

    private async Task RunQrScanAsync()
    {
        var capture = await SelectCaptureAsync();
        if (capture is null)
        {
            StatusText.Text = "扫码选区已取消。";
            return;
        }

        var selected = capture.Candidate;
        ReplaceSource(
            capture.Bitmap,
            $"扫码截图：{selected.Kind} {selected.Name}");
        SetBusy(true, "正在识别条码 / 二维码及旋转角度...");
        var stopwatch = Stopwatch.StartNew();
        var result = await Task.Run(
            () => QrScanner.Scan(capture.Bitmap));
        stopwatch.Stop();
        if (result is null)
        {
            ResultHeader.Text = "扫码结果";
            ResultHintText.Text = "未找到条码或二维码，可缩小选区后重试。";
            CandidateList.SelectedIndex = -1;
            _candidateRows.Clear();
            NumberBox.Text = string.Empty;
            CopyButton.IsEnabled = false;
            SuppressedList.ItemsSource = null;
            StatusText.Text =
                $"未识别到条码或二维码。已尝试原图、放大及 24 个旋转角度，" +
                $"用时 {stopwatch.ElapsedMilliseconds} ms。";
            FooterText.Text =
                $"扫码 {stopwatch.ElapsedMilliseconds} ms · 未找到";
            if (_hotkeys.ShowMainAfterQr)
            {
                ShowMainWindow();
            }
            else if (!IsVisible)
            {
                _tray?.ShowMessage(
                    "未识别到条码或二维码",
                    "可以缩小选区或换一个更清晰的角度后重试。");
            }
            return;
        }

        var clipboardCopied = true;
        string? clipboardError = null;
        try
        {
            Clipboard.SetText(result.Text);
        }
        catch (Exception ex)
        {
            clipboardCopied = false;
            clipboardError = ex.Message;
        }

        ShowQrResult(result, stopwatch.ElapsedMilliseconds);
        if (!clipboardCopied)
        {
            StatusText.Text += $" 剪贴板写入失败：{clipboardError}";
        }

        if (_hotkeys.ShowMainAfterQr)
        {
            ShowMainWindow();
        }
        else if (!IsVisible)
        {
            _tray?.ShowMessage(
                "扫码内容已复制",
                result.Text.Length > 80
                    ? result.Text[..80] + "..."
                    : result.Text);
        }
    }

    private void ShowQrResult(
        QrScanResult result,
        long elapsedMilliseconds)
    {
        _lastOcrResult = null;
        _candidateRows.Clear();
        CandidateList.SelectedIndex = -1;
        SuppressedList.ItemsSource = null;
        ResultHeader.Text = "扫码结果";
        ResultHintText.Text = "扫码内容已复制到剪贴板。";
        NumberBox.Text = result.Text;
        CopyButton.IsEnabled = result.Text.Length > 0;
        StatusText.Text =
            $"已识别 {result.Format}，多角度校正 {result.RotationDegrees}°，" +
            "文字已复制到剪贴板。";
        FooterText.Text =
            $"扫码 {elapsedMilliseconds} ms · {result.Format} · " +
            $"校正 {result.RotationDegrees}°";
    }

    private async Task RunTransformOcrAsync(
        TransformEditorWindow transform,
        SKBitmap bitmap)
    {
        if (_editorOcrBusy)
        {
            transform.ReportOcrStatus("已有 OCR 任务正在进行，请稍候。");
            return;
        }

        _editorOcrBusy = true;
        try
        {
            transform.ReportOcrStatus("正在识别画布选区...");
            await EnsureEngineAsync();
            var engine = _engine ?? throw new InvalidOperationException(
                "OCR 引擎初始化失败。");
            var result = await Task.Run(() =>
                engine.Recognize(
                    bitmap,
                    null,
                    _hotkeys.OcrTimeoutMs));
            if (result.TimedOut)
            {
                transform.ReportOcrStatus(
                    $"OCR 超过 {_hotkeys.OcrTimeoutMs} ms，已停止且未复制结果。");
                return;
            }
            var display = ShowResult(result);
            if (string.IsNullOrWhiteSpace(display.Text))
            {
                transform.ReportOcrStatus(
                    $"画布选区 {bitmap.Width}×{bitmap.Height} 没有识别到文字。");
                return;
            }

            try
            {
                Clipboard.SetText(display.Text);
                transform.ReportOcrStatus(
                    $"画布选区 OCR 已复制到剪贴板：{display.Text.Length} 字符，" +
                    $"{result.Elapsed.TotalMilliseconds:0} ms" +
                    $"{(result.DetectorLongSide is { } cap ? $"，单行检测上限 {cap}px" : string.Empty)}" +
                    $" · {(result.ModelQuality == OcrModelQuality.HighAccuracy ? "高精度" : "标准")}。");
                _tray?.ShowMessage(
                    "画布选区 OCR 已复制",
                    display.Text.Length > 80
                        ? display.Text[..80] + "..."
                        : display.Text);
            }
            catch (Exception ex)
            {
                transform.ReportOcrStatus(
                    $"识别完成，但剪贴板写入失败：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            transform.ReportOcrStatus($"画布选区 OCR 失败：{ex.Message}");
            _tray?.ShowMessage("画布选区 OCR 失败", ex.Message);
        }
        finally
        {
            _editorOcrBusy = false;
        }
    }

    private Task HandleTransformHotkeyAsync()
    {
        if (_activeTransformWindow is { } transform)
        {
            transform.Activate();
            return Task.CompletedTask;
        }

        if (_source is null)
        {
            StatusText.Text = "还没有图片；先截图或打开图片后再旋转/透视。";
            return Task.CompletedTask;
        }

        OpenTransformEditor();
        return Task.CompletedTask;
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        OpenHotkeySettings();
    }

    private void OpenHotkeySettings()
    {
        if (_busy)
        {
            return;
        }

        ShowMainWindow();
        var old = _hotkeys;
        var dialog = new HotkeySettingsWindow(_hotkeys)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!TryRegisterHotkeys(dialog.Settings))
        {
            TryRegisterHotkeys(old);
            return;
        }

        var modelChanged = old.ModelQuality != _hotkeys.ModelQuality;
        if (modelChanged)
        {
            _engine?.Dispose();
            _engine = null;
        }

        try
        {
            _hotkeys.Save();
            ApplyUserPreferences();
            UpdateHotkeyHint();
            StatusText.Text = modelChanged
                ? "设置已保存；识别模型将在下次识别时重新载入。"
                : "快捷键已更新并保存。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"快捷键已生效，但保存失败：{ex.Message}";
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await LoadImageAsync(dialog.FileName);
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await LoadImageAsync(files[0]);
        }
    }

    private async Task LoadImageAsync(string path)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在载入图片...");
            var source = await Task.Run(() => SKBitmap.Decode(path));
            if (source is null)
            {
                throw new InvalidOperationException("无法读取图片。");
            }

            ReplaceSource(
                source,
                $"{Path.GetFileName(path)}  {source.Width}×{source.Height}");
            OcrButton.IsEnabled = true;
            EditButton.IsEnabled = true;
            ClearSelectionButton.IsEnabled = true;
            StatusText.Text = "拖拽一个只包含号码的矩形，再点击“识别文字”。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "载入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            FooterText.Text = "载入失败";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void ImageViewport_MouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_source is null || _busy)
        {
            return;
        }

        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panningImage = true;
            _panStart = e.GetPosition(ImageViewport);
            _panStartOffsetX = _imageOffsetX;
            _panStartOffsetY = _imageOffsetY;
            _fitImageToViewport = false;
            ImageViewport.CaptureMouse();
            ImageViewport.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            FitImageToViewport();
            e.Handled = true;
            return;
        }

        var imagePoint = ViewportToImage(
            e.GetPosition(ImageViewport));
        if (!IsInsideImage(imagePoint))
        {
            return;
        }

        _dragStart = ClampToImage(imagePoint);
        _dragging = true;
        _selection = null;
        SelectionRect.Visibility = Visibility.Visible;
        UpdateSelectionVisual();
        ImageViewport.CaptureMouse();
        e.Handled = true;
    }

    private void ImageViewport_MouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (_panningImage)
        {
            var current = e.GetPosition(ImageViewport);
            _imageOffsetX =
                _panStartOffsetX + current.X - _panStart.X;
            _imageOffsetY =
                _panStartOffsetY + current.Y - _panStart.Y;
            ClampImageOffsets();
            UpdateImageLayout();
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            return;
        }

        var currentImage = ClampToImage(
            ViewportToImage(e.GetPosition(ImageViewport)));
        var left = Math.Min(_dragStart.X, currentImage.X);
        var top = Math.Min(_dragStart.Y, currentImage.Y);
        var right = Math.Max(_dragStart.X, currentImage.X);
        var bottom = Math.Max(_dragStart.Y, currentImage.Y);
        _selection = new SKRectI(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(right),
            (int)Math.Round(bottom));
        UpdateSelectionVisual();
        e.Handled = true;
    }

    private void ImageViewport_MouseUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_panningImage &&
            e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panningImage = false;
            ImageViewport.ReleaseMouseCapture();
            ImageViewport.Cursor = Cursors.Cross;
            e.Handled = true;
            return;
        }

        if (!_dragging || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragging = false;
        ImageViewport.ReleaseMouseCapture();
        if (_selection is { Width: >= 3, Height: >= 3 })
        {
            StatusText.Text =
                $"选区 {_selection.Value.Width}×{_selection.Value.Height}，准备识别。";
        }
        else
        {
            _selection = null;
            SelectionRect.Visibility = Visibility.Collapsed;
        }
        e.Handled = true;
    }

    private void ImageViewport_MouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (_source is null || _busy)
        {
            return;
        }

        var pointer = e.GetPosition(ImageViewport);
        var imageX = (pointer.X - _imageOffsetX) / _imageZoom;
        var imageY = (pointer.Y - _imageOffsetY) / _imageZoom;
        var factor = Math.Pow(1.15, e.Delta / 120.0);
        var nextZoom = Math.Clamp(
            _imageZoom * factor,
            MinimumImageZoom,
            MaximumImageZoom);
        if (Math.Abs(nextZoom - _imageZoom) < 0.0001)
        {
            return;
        }

        _imageZoom = nextZoom;
        _imageOffsetX = pointer.X - imageX * _imageZoom;
        _imageOffsetY = pointer.Y - imageY * _imageZoom;
        _fitImageToViewport = false;
        ClampImageOffsets();
        UpdateImageLayout();
        e.Handled = true;
    }

    private void ImageViewport_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (ImageViewport.ActualWidth <= 0 ||
            ImageViewport.ActualHeight <= 0)
        {
            return;
        }

        if (_fitImageToViewport)
        {
            FitImageToViewport();
            return;
        }

        _imageOffsetX += (e.NewSize.Width - e.PreviousSize.Width) / 2;
        _imageOffsetY += (e.NewSize.Height - e.PreviousSize.Height) / 2;
        ClampImageOffsets();
        UpdateImageLayout();
    }

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null || _busy)
        {
            return;
        }

        await RunOcrAsync();
    }

    private async Task EnsureEngineAsync()
    {
        var quality = _hotkeys.ModelQuality;
        if (_engine is { } engine &&
            engine.ModelQuality == quality)
        {
            return;
        }

        _engine?.Dispose();
        _engine = null;
        _engine = await Task.Run(
            () => new SnapGlyphEngine(quality));
    }

    private async Task RunOcrAsync(bool showMainAfter = true)
    {
        if (_source is null || _busy)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在识别...");
            var source = _source;
            var selection = _selection;
            await EnsureEngineAsync();
            var engine = _engine ?? throw new InvalidOperationException(
                "OCR 引擎初始化失败。");
            var result = await Task.Run(
                () => engine.Recognize(
                    source,
                    selection,
                    _hotkeys.OcrTimeoutMs));
            if (result.TimedOut)
            {
                _lastOcrResult = null;
                _candidateRows.Clear();
                NumberBox.Text = string.Empty;
                CopyButton.IsEnabled = false;
                SuppressedList.ItemsSource = null;
                ResultHeader.Text = "识别超时";
                ResultHintText.Text =
                    $"已达到 {_hotkeys.OcrTimeoutMs} ms 上限，本次结果未复制。请缩小选区，或在设置中调整超时时间。";
                StatusText.Text =
                    $"OCR 超过 {_hotkeys.OcrTimeoutMs} ms，已停止；本次结果未复制。";
                FooterText.Text =
                    $"OCR {result.Elapsed.TotalMilliseconds:0} ms · 超时";
                if (showMainAfter)
                {
                    ShowMainWindow();
                }
                else if (!IsVisible)
                {
                    _tray?.ShowMessage(
                        "OCR 超时",
                        "请缩小选区或改用标准模型后重试。");
                }
                return;
            }

            var display = ShowResult(result);
            if (!string.IsNullOrWhiteSpace(display.Text))
            {
                try
                {
                    Clipboard.SetText(display.Text);
                    StatusText.Text += " 已复制到剪贴板。";
                }
                catch (Exception ex)
                {
                    StatusText.Text += $" 剪贴板写入失败：{ex.Message}";
                }
            }
            if (showMainAfter)
            {
                ShowMainWindow();
            }
            else if (!IsVisible)
            {
                _tray?.ShowMessage(
                    "OCR 已复制",
                    display.Text.Length > 80
                        ? display.Text[..80] + "..."
                        : display.Text);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "识别失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "识别失败。";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private OcrDisplay ShowResult(SnapGlyphResult result)
    {
        _lastOcrResult = result;
        var display = OcrPresenter.Present(result, GetSelectedMode());
        _candidateRows.Clear();
        foreach (var row in display.Rows)
        {
            _candidateRows.Add(new CandidateRow(
                row,
                display.EffectiveMode == OcrMode.Number));
        }

        CandidateList.SelectedIndex = -1;
        NumberBox.Text = display.Text;
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(NumberBox.Text);
        ResultHeader.Text = display.EffectiveMode == OcrMode.Number
            ? "识别号码"
            : "识别文字";
        ResultHintText.Text = display.EffectiveMode == OcrMode.Number
            ? "识别结果已复制；可在下方候选行中切换。"
            : "识别文字已复制到剪贴板。";
        SuppressedList.ItemsSource = result.SuppressedRows
            .Where(row => row.Digits.Length > 0)
            .Select(row => new SuppressedRow(row))
            .ToArray();
        StatusText.Text = display.EffectiveMode == OcrMode.Number
            ? $"自动/号码模式：识别到 {result.Candidates.Count} 个号码行，{result.SuppressedRows.Count} 个重复框已抑制。"
            : $"文字模式：{display.Rows.Count} 行文字，{result.SuppressedRows.Count} 个重复框已抑制。";
        FooterText.Text =
            $"OCR {result.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms" +
            $"{(result.DetectorLongSide is { } cap ? $" · 单行检测 {cap}px" : string.Empty)}" +
            $" · {(result.ModelQuality == OcrModelQuality.HighAccuracy ? "高精度" : "标准")}";
        if (display.EffectiveMode == OcrMode.Number && _candidateRows.Count > 0)
        {
            CandidateList.SelectedIndex = 0;
        }
        return display;
    }

    private void ShowMainWindow()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private OcrMode GetSelectedMode() => ModeComboBox.SelectedIndex switch
    {
        1 => OcrMode.Number,
        2 => OcrMode.Text,
        _ => OcrMode.Auto,
    };

    private void ModeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_lastOcrResult is not null)
        {
            ShowResult(_lastOcrResult);
        }
    }

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is CandidateRow row)
        {
            NumberBox.Text = row.DisplayText;
            CopyButton.IsEnabled = NumberBox.Text.Length > 0;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(NumberBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(NumberBox.Text);
            FooterText.Text = "号码已复制（只复制单行结果）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null || _busy)
        {
            return;
        }

        OpenTransformEditor();
    }

    private void OpenTransformEditor()
    {
        if (_source is null || _busy)
        {
            return;
        }

        if (_activeTransformWindow is { } active)
        {
            active.Activate();
            return;
        }

        var editor = new TransformEditorWindow(
            _source,
            _hotkeys.GetSelectionColor())
        {
            Owner = this,
        };
        editor.OcrSelectionHandler = bitmap =>
            RunTransformOcrAsync(editor, bitmap);
        _activeTransformWindow = editor;
        IsEnabled = false;
        editor.Closed += (_, _) =>
        {
            if (_activeTransformWindow == editor)
            {
                _activeTransformWindow = null;
            }
            IsEnabled = true;

            if (editor.Result is { } transformed)
            {
                ReplaceSource(
                    transformed,
                    $"已编辑 {transformed.Width}×{transformed.Height}");
                StatusText.Text = "旋转/透视已应用。现在可以框选号码行重新识别。";
                ShowMainWindow();
            }
            else
            {
                editor.Result?.Dispose();
            }
        };
        editor.Show();
        editor.Activate();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _selection = null;
        SelectionRect.Visibility = Visibility.Collapsed;
        StatusText.Text = "选区已清除；再次拖拽以框选号码。";
    }

    private void ReplaceSource(SKBitmap source, string footer)
    {
        _source?.Dispose();
        _source = source;
        _selection = null;
        SelectionRect.Visibility = Visibility.Collapsed;
        ImageView.Source = BitmapSourceUtil.FromSkia(source);
        _fitImageToViewport = true;
        FitImageToViewport();
        _candidateRows.Clear();
        _lastOcrResult = null;
        NumberBox.Text = string.Empty;
        SuppressedList.ItemsSource = null;
        CopyButton.IsEnabled = false;
        OcrButton.IsEnabled = true;
        EditButton.IsEnabled = true;
        ClearSelectionButton.IsEnabled = true;
        FooterText.Text = footer;
        ResultHintText.Text =
            "在左侧拖拽只包含号码或文字的区域，再点击“识别文字”；未框选时会识别整张图，也可直接按 Ctrl+Shift+Z。";
    }

    private void FitImageToViewport()
    {
        if (_source is null)
        {
            ViewInfoText.Text = "未载入图片";
            return;
        }

        var viewportWidth = ImageViewport.ActualWidth;
        var viewportHeight = ImageViewport.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            _fitImageToViewport = true;
            return;
        }

        var availableWidth =
            Math.Max(1, viewportWidth - ImageViewPadding * 2);
        var availableHeight =
            Math.Max(1, viewportHeight - ImageViewPadding * 2);
        var fitZoom = Math.Min(
            availableWidth / _source.Width,
            availableHeight / _source.Height);
        _imageZoom = Math.Clamp(
            Math.Min(1.0, fitZoom),
            MinimumImageZoom,
            1.0);
        _imageOffsetX =
            (viewportWidth - _source.Width * _imageZoom) / 2;
        _imageOffsetY =
            (viewportHeight - _source.Height * _imageZoom) / 2;
        _fitImageToViewport = true;
        UpdateImageLayout();
    }

    private void UpdateImageLayout()
    {
        if (_source is null)
        {
            return;
        }

        var viewportWidth = ImageViewport.ActualWidth;
        var viewportHeight = ImageViewport.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        ImageCanvas.Width = viewportWidth;
        ImageCanvas.Height = viewportHeight;
        var displayWidth = _source.Width * _imageZoom;
        var displayHeight = _source.Height * _imageZoom;
        ImageFrame.Width = displayWidth;
        ImageFrame.Height = displayHeight;
        Canvas.SetLeft(ImageFrame, _imageOffsetX);
        Canvas.SetTop(ImageFrame, _imageOffsetY);
        ImageView.Width = displayWidth;
        ImageView.Height = displayHeight;
        Canvas.SetLeft(ImageView, _imageOffsetX);
        Canvas.SetTop(ImageView, _imageOffsetY);
        UpdateSelectionVisual();
        ViewInfoText.Text =
            $"{_imageZoom * 100:0.#}% · {_source.Width}×{_source.Height}" +
            (_fitImageToViewport ? " · 适应" : string.Empty);
    }

    private void UpdateSelectionVisual()
    {
        if (_source is null || _selection is not { } selection)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        var left = _imageOffsetX + selection.Left * _imageZoom;
        var top = _imageOffsetY + selection.Top * _imageZoom;
        SelectionRect.Visibility = Visibility.Visible;
        SelectionRect.Width =
            Math.Max(0, selection.Width * _imageZoom);
        SelectionRect.Height =
            Math.Max(0, selection.Height * _imageZoom);
        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
    }

    private void ApplySelectionColor()
    {
        var color = _hotkeys.GetSelectionColor();
        SelectionRect.Stroke = new SolidColorBrush(color);
        SelectionRect.Fill = new SolidColorBrush(
            Color.FromArgb(34, color.R, color.G, color.B));
    }

    private void ApplyUserPreferences()
    {
        ApplySelectionColor();
        if (SuppressedExpander is not null)
        {
            SuppressedExpander.Visibility =
                _hotkeys.ShowSuppressedRows
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void ClampImageOffsets()
    {
        if (_source is null)
        {
            return;
        }

        var viewportWidth = ImageViewport.ActualWidth;
        var viewportHeight = ImageViewport.ActualHeight;
        var displayWidth = _source.Width * _imageZoom;
        var displayHeight = _source.Height * _imageZoom;
        _imageOffsetX = displayWidth <= viewportWidth
            ? (viewportWidth - displayWidth) / 2
            : Math.Clamp(
                _imageOffsetX,
                viewportWidth - displayWidth,
                0);
        _imageOffsetY = displayHeight <= viewportHeight
            ? (viewportHeight - displayHeight) / 2
            : Math.Clamp(
                _imageOffsetY,
                viewportHeight - displayHeight,
                0);
    }

    private Point ViewportToImage(Point point) => new(
        (point.X - _imageOffsetX) / _imageZoom,
        (point.Y - _imageOffsetY) / _imageZoom);

    private bool IsInsideImage(Point imagePoint) =>
        _source is not null &&
        imagePoint.X >= 0 &&
        imagePoint.Y >= 0 &&
        imagePoint.X <= _source.Width &&
        imagePoint.Y <= _source.Height;

    private Point ClampToImage(Point point)
    {
        var width = _source?.Width ?? 0;
        var height = _source?.Height ?? 0;
        return new Point(
            Math.Clamp(point.X, 0, width),
            Math.Clamp(point.Y, 0, height));
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        OpenButton.IsEnabled = !busy;
        CaptureButton.IsEnabled = !busy;
        QrButton.IsEnabled = !busy;
        ShortcutButton.IsEnabled = !busy;
        EditButton.IsEnabled = !busy && _source is not null;
        OcrButton.IsEnabled = !busy && _source is not null;
        ClearSelectionButton.IsEnabled = !busy && _source is not null;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
    }

    private sealed record CaptureSelection(
        SKBitmap Bitmap,
        CaptureCandidate Candidate);
}

public sealed class CandidateRow
{
    public CandidateRow(OcrRow row, bool showDigits)
    {
        Row = row;
        DisplayText = showDigits ? row.Digits : row.Text;
    }

    public OcrRow Row { get; }
    public string DisplayText { get; }
    public string ScoreText =>
        $"score {Row.Score:0.0000} · 数字占比 {Row.DigitRatio:0.000}";
}

public sealed class SuppressedRow
{
    public SuppressedRow(OcrRow row)
    {
        Row = row;
    }

    public OcrRow Row { get; }
    public string SuppressedText =>
        $"{Row.Digits}   overlap {Row.Overlap:0.000}  (duplicate of #{Row.DuplicateOf})";
}
