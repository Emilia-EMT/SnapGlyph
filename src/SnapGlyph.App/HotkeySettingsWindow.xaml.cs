using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnapGlyph.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SnapGlyph.App;

public partial class HotkeySettingsWindow : Window
{
    private readonly HotkeySettings _settings;
    private string? _recording;

    public HotkeySettingsWindow(HotkeySettings current)
    {
        InitializeComponent();
        Icon = AppIcon.Load();
        _settings = current.Copy();
        ShowAfterScreenshotBox.IsChecked = _settings.ShowMainAfterScreenshot;
        ShowAfterOcrBox.IsChecked = _settings.ShowMainAfterOcr;
        ShowAfterQrBox.IsChecked = _settings.ShowMainAfterQr;
        ShowSuppressedRowsBox.IsChecked = _settings.ShowSuppressedRows;
        StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled();
        ModelHighAccuracyRadio.IsChecked =
            _settings.ModelQuality == OcrModelQuality.HighAccuracy;
        ModelStandardRadio.IsChecked =
            _settings.ModelQuality == OcrModelQuality.Standard;
        NotificationDurationBox.Text =
            (_settings.NotificationDurationMs / 1000.0)
            .ToString("0.0#", CultureInfo.CurrentCulture);
        OcrTimeoutBox.Text = _settings.OcrTimeoutMs.ToString(
            CultureInfo.InvariantCulture);
        SelectionColorBox.Text = _settings.SelectionColorHex;
        CrosshairColorBox.Text = _settings.CrosshairColorHex;
        UpdateColorPreviews();
        RefreshBoxes();
    }

    public HotkeySettings Settings => _settings;

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot })
        {
            return;
        }

        _recording = slot;
        StatusText.Text = "请按下新的组合键...";
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_recording is not null)
            {
                _recording = null;
                StatusText.Text = "已取消录制。";
            }
            else
            {
                DialogResult = false;
            }
            e.Handled = true;
            return;
        }

        if (_recording is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            StatusText.Text = "快捷键必须包含至少一个修饰键。";
            e.Handled = true;
            return;
        }

        var binding = new HotkeyBinding
        {
            Modifiers = modifiers,
            Key = key,
        };
        if (!binding.IsValid)
        {
            StatusText.Text = "这个组合键不可用。";
            e.Handled = true;
            return;
        }

        switch (_recording)
        {
            case "Screenshot":
                _settings.Screenshot = binding;
                break;
            case "Ocr":
                _settings.Ocr = binding;
                break;
            case "Transform":
                _settings.Transform = binding;
                break;
            case "Qr":
                _settings.Qr = binding;
                break;
        }
        _recording = null;
        RefreshBoxes();
        StatusText.Text = $"已录制：{binding.Display}";
        e.Handled = true;
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new HotkeySettings();
        _settings.Screenshot = defaults.Screenshot;
        _settings.Ocr = defaults.Ocr;
        _settings.Transform = defaults.Transform;
        _settings.Qr = defaults.Qr;
        _settings.ShowMainAfterScreenshot = false;
        _settings.ShowMainAfterOcr = false;
        _settings.ShowMainAfterQr = false;
        _settings.ShowSuppressedRows = false;
        _settings.ModelQuality = OcrModelQuality.Standard;
        _settings.NotificationDurationMs =
            HotkeySettings.DefaultNotificationDurationMs;
        _settings.OcrTimeoutMs =
            HotkeySettings.DefaultOcrTimeoutMs;
        _settings.SelectionColorHex =
            HotkeySettings.DefaultSelectionColor;
        _settings.CrosshairColorHex =
            HotkeySettings.DefaultCrosshairColor;
        ShowAfterScreenshotBox.IsChecked = false;
        ShowAfterOcrBox.IsChecked = false;
        ShowAfterQrBox.IsChecked = false;
        ShowSuppressedRowsBox.IsChecked = false;
        ModelStandardRadio.IsChecked = true;
        NotificationDurationBox.Text =
            (_settings.NotificationDurationMs / 1000.0)
            .ToString("0.0#", CultureInfo.CurrentCulture);
        OcrTimeoutBox.Text = _settings.OcrTimeoutMs.ToString(
            CultureInfo.InvariantCulture);
        SelectionColorBox.Text = _settings.SelectionColorHex;
        CrosshairColorBox.Text = _settings.CrosshairColorHex;
        RefreshBoxes();
        StatusText.Text = "已恢复默认设置，点击“保存”生效。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var displays = new[]
        {
            _settings.Screenshot.Display,
            _settings.Ocr.Display,
            _settings.Transform.Display,
            _settings.Qr.Display,
        };
        if (displays.Distinct(StringComparer.OrdinalIgnoreCase).Count() != displays.Length)
        {
            StatusText.Text = "四个快捷键不能重复。";
            return;
        }

        if (!TryReadDuration(out var durationMs))
        {
            StatusText.Text = "通知时长必须是 0.5–15 秒之间的数字。";
            NotificationDurationBox.Focus();
            return;
        }

        if (!TryReadOcrTimeout(out var ocrTimeoutMs))
        {
            StatusText.Text =
                "OCR 超时必须是 100–30000 毫秒之间的整数。";
            OcrTimeoutBox.Focus();
            return;
        }

        if (!HotkeySettings.TryParseHexColor(
                SelectionColorBox.Text.Trim(),
                out var selectionColor))
        {
            StatusText.Text = "选区颜色格式应为 #RRGGBB，例如 #00E5FF。";
            SelectionColorBox.Focus();
            return;
        }

        if (!HotkeySettings.TryParseHexColor(
                CrosshairColorBox.Text.Trim(),
                out var crosshairColor))
        {
            StatusText.Text = "十字线颜色格式应为 #RRGGBB，例如 #FFC107。";
            CrosshairColorBox.Focus();
            return;
        }

        _settings.ShowMainAfterScreenshot = ShowAfterScreenshotBox.IsChecked == true;
        _settings.ShowMainAfterOcr = ShowAfterOcrBox.IsChecked == true;
        _settings.ShowMainAfterQr = ShowAfterQrBox.IsChecked == true;
        _settings.ShowSuppressedRows =
            ShowSuppressedRowsBox.IsChecked == true;
        _settings.ModelQuality = ModelStandardRadio.IsChecked == true
            ? OcrModelQuality.Standard
            : OcrModelQuality.HighAccuracy;
        _settings.NotificationDurationMs = durationMs;
        _settings.OcrTimeoutMs = ocrTimeoutMs;
        _settings.SelectionColorHex =
            $"#{selectionColor.R:X2}{selectionColor.G:X2}{selectionColor.B:X2}";
        _settings.CrosshairColorHex =
            $"#{crosshairColor.R:X2}{crosshairColor.G:X2}{crosshairColor.B:X2}";
        try
        {
            StartupRegistration.SetEnabled(
                StartWithWindowsBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"开机自启动设置失败：{ex.Message}";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void RefreshBoxes()
    {
        ScreenshotBox.Text = _settings.Screenshot.Display;
        OcrBox.Text = _settings.Ocr.Display;
        TransformBox.Text = _settings.Transform.Display;
        QrBox.Text = _settings.Qr.Display;
    }

    private void SelectionColorBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        UpdateColorPreviews();
    }

    private void CrosshairColorBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        UpdateColorPreviews();
    }

    private void UpdateColorPreviews()
    {
        if (SelectionColorPreview is null ||
            CrosshairColorPreview is null)
        {
            return;
        }

        ApplyColorPreview(
            SelectionColorBox?.Text,
            SelectionColorPreview,
            Color.FromRgb(45, 25, 28));
        ApplyColorPreview(
            CrosshairColorBox?.Text,
            CrosshairColorPreview,
            Color.FromRgb(45, 25, 28));
    }

    private static void ApplyColorPreview(
        string? text,
        Border preview,
        Color invalidBackground)
    {
        if (HotkeySettings.TryParseHexColor(
                text?.Trim(),
                out var color))
        {
            preview.Background = new SolidColorBrush(color);
            preview.BorderBrush =
                new SolidColorBrush(Color.FromRgb(107, 119, 133));
        }
        else
        {
            preview.Background = new SolidColorBrush(invalidBackground);
            preview.BorderBrush =
                new SolidColorBrush(Color.FromRgb(217, 83, 79));
        }
    }

    private bool TryReadDuration(out int durationMs)
    {
        durationMs = 0;
        var text = NotificationDurationBox.Text.Trim();
        if (!double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out var seconds) &&
            !double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out seconds))
        {
            return false;
        }

        if (seconds < 0.5 || seconds > 15)
        {
            return false;
        }

        durationMs = (int)Math.Round(seconds * 1000);
        return true;
    }

    private bool TryReadOcrTimeout(out int timeoutMs)
    {
        timeoutMs = 0;
        var text = OcrTimeoutBox.Text.Trim();
        if (!int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out timeoutMs) &&
            !int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out timeoutMs))
        {
            return false;
        }

        return timeoutMs >= HotkeySettings.MinimumOcrTimeoutMs &&
               timeoutMs <= HotkeySettings.MaximumOcrTimeoutMs;
    }
}
