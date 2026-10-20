using System.IO;
using System.Windows.Media.Imaging;

namespace SnapGlyph.App;

internal static class AppIcon
{
    public static BitmapFrame? Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (!File.Exists(path))
        {
            return null;
        }

        var frame = BitmapFrame.Create(
            new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
