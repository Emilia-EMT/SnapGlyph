namespace SnapGlyph.App;

internal static class AppIdentity
{
    public const string Name = "SnapGlyph";

    public static string Version { get; } =
        typeof(AppIdentity).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";

    public static string WindowTitle => $"{Name} {Version}";
}
