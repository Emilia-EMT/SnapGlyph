using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnapGlyph.App;

internal sealed class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _closeTimer;

    private ToastWindow(
        string title,
        string message,
        TimeSpan duration)
    {
        Title = $"{AppIdentity.Name} 通知";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => HideFromAltTab();

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(244, 28, 32, 39)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 86, 99)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 11, 14, 11),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Foreground = new SolidColorBrush(
                            Color.FromRgb(243, 245, 247)),
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 0, 4),
                    },
                    new TextBlock
                    {
                        Text = message.Length > 120
                            ? message[..120] + "..."
                            : message,
                        Foreground = new SolidColorBrush(
                            Color.FromRgb(198, 205, 214)),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        Loaded += (_, _) => PositionInWorkArea();
        _closeTimer = new DispatcherTimer
        {
            Interval = duration,
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };
        _closeTimer.Start();
    }

    public static void ShowBriefly(
        string title,
        string message,
        int durationMs = HotkeySettings.DefaultNotificationDurationMs)
    {
        var duration = TimeSpan.FromMilliseconds(
            Math.Clamp(durationMs, 500, 15000));
        var app = Application.Current;
        if (app is null)
        {
            return;
        }
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(
                new Action(() => ShowBriefly(
                    title,
                    message,
                    durationMs)));
            return;
        }

        try
        {
            _current?.Close();
            var toast = new ToastWindow(title, message, duration);
            _current = toast;
            toast.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, toast))
                {
                    _current = null;
                }
            };
            toast.Show();
        }
        catch
        {
            // Notification UI must never block OCR or screenshot completion.
        }
    }

    private void PositionInWorkArea()
    {
        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 16;
        Top = workArea.Bottom - ActualHeight - 16;
    }

    private void HideFromAltTab()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExToolWindow |
                 NativeMethods.WsExNoActivate;
        style &= ~NativeMethods.WsExAppWindow;
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new IntPtr(style));
    }
}
