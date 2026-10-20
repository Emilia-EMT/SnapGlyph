using Microsoft.Win32;

namespace SnapGlyph.App;

internal static class StartupRegistration
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SnapGlyph";
    private const string LegacyValueName = "NumberOcr";
    private const string StartupArgument = "--autostart";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            RunKeyPath,
            writable: false);
        var current = key?.GetValue(ValueName) as string;
        return string.Equals(
                   current,
                   BuildCommand(),
                   StringComparison.OrdinalIgnoreCase) ||
               key?.GetValue(LegacyValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                RunKeyPath,
                writable: true)
                ?? throw new InvalidOperationException(
                    "无法打开当前用户的开机启动项。");
            key.SetValue(
                ValueName,
                BuildCommand(),
                RegistryValueKind.String);
            key.DeleteValue(
                LegacyValueName,
                throwOnMissingValue: false);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(
            RunKeyPath,
            writable: true);
        existing?.DeleteValue(ValueName, throwOnMissingValue: false);
        existing?.DeleteValue(
            LegacyValueName,
            throwOnMissingValue: false);
    }

    public static void MigrateLegacyRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                RunKeyPath,
                writable: false);
            if (key?.GetValue(LegacyValueName) is not string)
            {
                return;
            }

            SetEnabled(true);
        }
        catch
        {
            // Startup migration must not prevent the application from launching.
        }
    }

    private static string BuildCommand()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "无法确定当前程序路径。");
        }

        return $"\"{path}\" {StartupArgument}";
    }
}
