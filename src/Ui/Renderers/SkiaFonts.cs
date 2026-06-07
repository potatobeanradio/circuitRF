using System;
using Avalonia.Platform;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Lazy-loaded SKTypeface instances sourced from the embedded font assets.
/// Use these in all custom Skia renderers so that plot text is consistent and
/// independent of the host OS font stack.
///
/// Standard Avalonia controls are unaffected — they resolve fonts through the
/// FontFamily resources registered in App.axaml (DejaVuSans, IBMPlexSans), not
/// through this class.
///
/// avares paths:
///   DejaVu Sans   — avares://CircuitRF.Ui/Assets/Fonts/DejaVuSans*.ttf
///   IBM Plex Sans — avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-*.ttf
/// </summary>
internal static class SkiaFonts
{
    // ── DejaVu Sans ───────────────────────────────────────────────────────────
    // Preferred renderer font: covers a wide Unicode range, battle-tested in
    // scientific/engineering plots.
    private static readonly Lazy<SKTypeface> _dejaVuRegular =
        new(() => Load("Assets/Fonts/DejaVuSans.ttf"));

    private static readonly Lazy<SKTypeface> _dejaVuBold =
        new(() => Load("Assets/Fonts/DejaVuSans-Bold.ttf"));

    private static readonly Lazy<SKTypeface> _dejaVuOblique =
        new(() => Load("Assets/Fonts/DejaVuSans-Oblique.ttf"));

    private static readonly Lazy<SKTypeface> _dejaVuBoldOblique =
        new(() => Load("Assets/Fonts/DejaVuSans-BoldOblique.ttf"));

    public static SKTypeface DejaVuRegular     => _dejaVuRegular.Value;
    public static SKTypeface DejaVuBold        => _dejaVuBold.Value;
    public static SKTypeface DejaVuOblique     => _dejaVuOblique.Value;
    public static SKTypeface DejaVuBoldOblique => _dejaVuBoldOblique.Value;

    // ── IBM Plex Sans (static — SkiaSharp does not support variable fonts) ────
    // Clean, modern typeface; use for UI-adjacent overlay text and data labels.
    private static readonly Lazy<SKTypeface> _plexRegular =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Regular.ttf"));

    private static readonly Lazy<SKTypeface> _plexBold =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Bold.ttf"));

    private static readonly Lazy<SKTypeface> _plexSemiBold =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-SemiBold.ttf"));

    private static readonly Lazy<SKTypeface> _plexItalic =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Italic.ttf"));

    private static readonly Lazy<SKTypeface> _plexLight =
        new(() => Load("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Light.ttf"));

    public static SKTypeface PlexRegular  => _plexRegular.Value;
    public static SKTypeface PlexBold     => _plexBold.Value;
    public static SKTypeface PlexSemiBold => _plexSemiBold.Value;
    public static SKTypeface PlexItalic   => _plexItalic.Value;
    public static SKTypeface PlexLight    => _plexLight.Value;

    // ── Helper ────────────────────────────────────────────────────────────────
    private static SKTypeface Load(string assetRelativePath)
    {
        using var stream = AssetLoader.Open(
            new Uri($"avares://CircuitRF.Ui/{assetRelativePath}"));
        return SKTypeface.FromStream(stream) ?? SKTypeface.Default;
    }
}
