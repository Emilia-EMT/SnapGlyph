using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using SnapGlyph.Core;

namespace SnapGlyph.App;

public sealed class HotkeyBinding
{
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }

    public bool IsValid =>
        Modifiers != ModifierKeys.None &&
        Key != Key.None &&
        Key != Key.Escape;

    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join("+", parts);
        }
    }

    public uint NativeModifiers
    {
        get
        {
            uint value = 0;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) value |= NativeMethods.ModAlt;
            if (Modifiers.HasFlag(ModifierKeys.Control)) value |= 0x0002;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) value |= NativeMethods.ModShift;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) value |= 0x0008;
            return value;
        }
    }

    public uint NativeKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public HotkeyBinding Copy() => new()
    {
        Modifiers = Modifiers,
        Key = Key,
    };
}

public sealed class HotkeySettings
{
    public const int CurrentSettingsVersion = 1;
    public const int DefaultNotificationDurationMs = 1200;
    public const int DefaultOcrTimeoutMs = 800;
    public const int MinimumOcrTimeoutMs = 100;
    public const int MaximumOcrTimeoutMs = 30000;
    public const string DefaultSelectionColor = "#00E5FF";
    public const string DefaultCrosshairColor = "#FFC107";

    public HotkeyBinding Screenshot { get; set; } =
        new() { Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Key = Key.A };

    public HotkeyBinding Ocr { get; set; } =
        new() { Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Key = Key.Z };

    public HotkeyBinding Transform { get; set; } =
        new() { Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Key = Key.W };

    public HotkeyBinding Qr { get; set; } =
        new() { Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Key = Key.Q };

    public bool ShowMainAfterScreenshot { get; set; }
    public bool ShowMainAfterOcr { get; set; }
    public bool ShowMainAfterQr { get; set; }
    public bool ShowSuppressedRows { get; set; }
    public int SettingsVersion { get; set; }
    public OcrModelQuality ModelQuality { get; set; } =
        OcrModelQuality.Standard;
    public int NotificationDurationMs { get; set; } =
        DefaultNotificationDurationMs;
    public int OcrTimeoutMs { get; set; } = DefaultOcrTimeoutMs;
    public string SelectionColorHex { get; set; } = DefaultSelectionColor;
    public string CrosshairColorHex { get; set; } = DefaultCrosshairColor;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnapGlyph",
        "hotkeys.json");

    public static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NumberOcr",
        "hotkeys.json");

    public HotkeySettings Copy() => new()
    {
        Screenshot = Screenshot.Copy(),
        Ocr = Ocr.Copy(),
        Transform = Transform.Copy(),
        Qr = Qr.Copy(),
        ShowMainAfterScreenshot = ShowMainAfterScreenshot,
        ShowMainAfterOcr = ShowMainAfterOcr,
        ShowMainAfterQr = ShowMainAfterQr,
        ShowSuppressedRows = ShowSuppressedRows,
        SettingsVersion = SettingsVersion,
        ModelQuality = ModelQuality,
        NotificationDurationMs = NotificationDurationMs,
        OcrTimeoutMs = OcrTimeoutMs,
        SelectionColorHex = SelectionColorHex,
        CrosshairColorHex = CrosshairColorHex,
    };

    public static HotkeySettings Load()
    {
        var candidates = new[]
        {
            new { Path = SettingsPath, IsLegacy = false },
            new { Path = LegacySettingsPath, IsLegacy = true },
        };
        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate.Path))
                {
                    continue;
                }

                var loaded = JsonSerializer.Deserialize<HotkeySettings>(
                    File.ReadAllText(candidate.Path));
                if (loaded is null ||
                    loaded.Screenshot?.IsValid != true ||
                    loaded.Ocr?.IsValid != true ||
                    loaded.Transform?.IsValid != true ||
                    loaded.Qr?.IsValid != true)
                {
                    continue;
                }

                var previousVersion = loaded.SettingsVersion;
                loaded.NotificationDurationMs = Math.Clamp(
                    loaded.NotificationDurationMs,
                    500,
                    15000);
                loaded.OcrTimeoutMs = Math.Clamp(
                    loaded.OcrTimeoutMs,
                    MinimumOcrTimeoutMs,
                    MaximumOcrTimeoutMs);
                if (!TryParseHexColor(
                        loaded.SelectionColorHex,
                        out _))
                {
                    loaded.SelectionColorHex = DefaultSelectionColor;
                }
                if (!TryParseHexColor(
                        loaded.CrosshairColorHex,
                        out _))
                {
                    loaded.CrosshairColorHex = DefaultCrosshairColor;
                }
                if (previousVersion < CurrentSettingsVersion)
                {
                    if (loaded.ModelQuality ==
                        OcrModelQuality.HighAccuracy)
                    {
                        loaded.ModelQuality =
                            OcrModelQuality.Standard;
                    }
                }
                loaded.SettingsVersion = CurrentSettingsVersion;

                if (candidate.IsLegacy ||
                    previousVersion < CurrentSettingsVersion)
                {
                    try
                    {
                        loaded.Save();
                    }
                    catch
                    {
                        // Reading settings should still succeed if the
                        // migration cannot be persisted.
                    }
                }
                return loaded;
            }
            catch
            {
                // Try the legacy path if the current file is corrupt.
            }
        }

        return new HotkeySettings();
    }

    public void Save()
    {
        SettingsVersion = CurrentSettingsVersion;
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            SettingsPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public Color GetSelectionColor() =>
        TryParseHexColor(SelectionColorHex, out var color)
            ? color
            : Color.FromRgb(0, 229, 255);

    public Color GetCrosshairColor() =>
        TryParseHexColor(CrosshairColorHex, out var color)
            ? color
            : Color.FromRgb(255, 193, 7);

    public static bool TryParseHexColor(
        string? text,
        out Color color)
    {
        color = Color.FromRgb(0, 229, 255);
        if (text is null ||
            text.Length != 7 ||
            text[0] != '#' ||
            !int.TryParse(
                text.AsSpan(1),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rgb))
        {
            return false;
        }

        color = Color.FromRgb(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
        return true;
    }
}
