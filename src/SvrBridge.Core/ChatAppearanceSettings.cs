namespace SvrBridge.Core;

/// <summary>
/// The persisted, renderer-independent appearance of the chat workspace.
/// The defaults intentionally reproduce the pre-appearance WPF colours and
/// leave every new effect off, so upgrading does not alter a chat window.
/// </summary>
public sealed record ChatAppearanceSettings(
    string BackgroundHex,
    string TextHex,
    string AccentHex,
    string GlowHex,
    double GlowOpacity,
    int GlowSizePixels,
    string BackgroundImagePath,
    double BackgroundImageOpacity)
{
    /// <summary>Enough room for a soft halo without growing the 80px move-tab gutter.</summary>
    public const int MaximumGlowSizePixels = 48;

    public const int MinimumGlowSizePixels = 0;

    public static readonly ChatAppearanceSettings Default = new(
        "#E6121824", // Existing 230/255 dark card fill.
        "#E1E6F0",   // Existing message body colour.
        "#3B82F6",   // Existing dashboard/control blue before its per-use alpha.
        "#3B82F6",
        0,
        0,
        "",
        0);

    public static readonly ChatAppearanceSettings Retrowave = new(
        "#E1A0802C",
        "#FFF3FF",
        "#FF4FD8",
        "#A855F7",
        0.72,
        30,
        "",
        0);

    public static readonly ChatAppearanceSettings Matrix = new(
        "#EE020A05",
        "#D7FFE2",
        "#21E36B",
        "#21E36B",
        0.62,
        24,
        "",
        0);

    public double SafeGlowOpacity => ClampUnit(GlowOpacity);
    public int SafeGlowSizePixels => GlowSizePixels <= 0
        ? 0
        : Math.Clamp(GlowSizePixels, MinimumGlowSizePixels, MaximumGlowSizePixels);
    public double SafeBackgroundImageOpacity => ClampUnit(BackgroundImageOpacity);
    public string SafeBackgroundHex => SafeColour(BackgroundHex, Default.BackgroundHex);
    public string SafeTextHex => SafeColour(TextHex, Default.TextHex);
    public string SafeAccentHex => SafeColour(AccentHex, Default.AccentHex);
    public string SafeGlowHex => SafeColour(GlowHex, Default.GlowHex);
    public string SafeBackgroundImagePath => BackgroundImagePath?.Trim() ?? "";

    public ChatAppearanceSettings Sanitised() => this with
    {
        BackgroundHex = SafeBackgroundHex,
        TextHex = SafeTextHex,
        AccentHex = SafeAccentHex,
        GlowHex = SafeGlowHex,
        GlowOpacity = SafeGlowOpacity,
        GlowSizePixels = SafeGlowSizePixels,
        BackgroundImagePath = SafeBackgroundImagePath,
        BackgroundImageOpacity = SafeBackgroundImageOpacity
    };

    public static ChatAppearanceSettings ForPreset(ChatAppearancePreset preset) => preset switch
    {
        ChatAppearancePreset.Retrowave => Retrowave,
        ChatAppearancePreset.Matrix => Matrix,
        _ => Default
    };

    private static double ClampUnit(double value) => double.IsFinite(value)
        ? Math.Clamp(value, 0d, 1d)
        : 0d;

    private static string SafeColour(string? candidate, string fallback) =>
        IsHexColour(candidate) ? candidate!.Trim() : fallback;

    /// <summary>Supports #RRGGBB and #AARRGGBB, the forms WPF accepts here.</summary>
    public static bool IsHexColour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Length is not (7 or 9) || trimmed[0] != '#') return false;
        return trimmed[1..].All(Uri.IsHexDigit);
    }
}

public enum ChatAppearancePreset
{
    Default,
    Retrowave,
    Matrix
}
