using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SnapGlyph.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon? _ownedIcon;
    private readonly Func<int> _toastDurationMs;

    public TrayIcon(
        Action show,
        Action screenshot,
        Action ocr,
        Action qr,
        Func<int> toastDurationMs,
        Action settings,
        Action exit)
    {
        _toastDurationMs = toastDurationMs;
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("打开主窗口", null, (_, _) => show());
        _menu.Items.Add("截图", null, (_, _) => screenshot());
        _menu.Items.Add("OCR", null, (_, _) => ocr());
        _menu.Items.Add("扫码（条码 / 二维码）", null, (_, _) => qr());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("设置", null, (_, _) => settings());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exit());

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        _ownedIcon = File.Exists(iconPath) ? new Drawing.Icon(iconPath) : null;
        _notify = new Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? Drawing.SystemIcons.Application,
            Text = AppIdentity.WindowTitle,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => show();
    }

    public void ShowMessage(string title, string message)
    {
        ToastWindow.ShowBriefly(
            title,
            message,
            _toastDurationMs());
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }
}
