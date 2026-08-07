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
    /// <summary>
    /// Test-only override (set only by <c>CircuitRF.Ui.Tests</c>, via <c>InternalsVisibleTo</c>) —
    /// the SAME seam <see cref="LayoutTextOutline.TestOverrideTypeface"/> already established, applied
    /// one level lower so it covers EVERY renderer rather than only the label-flatten path.
    ///
    /// <para><b>Why it has to exist.</b> <see cref="Load"/> goes through
    /// <c>Avalonia.Platform.AssetLoader</c>, which needs a live Avalonia app host; in this project's
    /// headless test run it throws <c>InvalidOperationException: Unable to locate
    /// 'Avalonia.Platform.IAssetLoader'</c> (measured directly, not assumed). Every Skia renderer in
    /// this codebase draws SOME text, so without this seam a full-plot render cannot be exercised —
    /// or measured — headlessly at all.</para>
    ///
    /// <para><b>Production never sets it</b>, so every real frame still draws with the exact embedded
    /// typeface it always did. A test sets it to <c>SKTypeface.Default</c>, the one typeface
    /// guaranteed loadable with no asset system. Note the consequence for any COST measurement taken
    /// through it: glyph metrics and rasterization cost belong to the substituted face, not to IBM
    /// Plex — close enough for a frame-budget number, and it must be said rather than implied.</para>
    /// </summary>
    internal static SKTypeface? TestOverrideTypeface;

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

    public static SKTypeface DejaVuRegular     => TestOverrideTypeface ?? _dejaVuRegular.Value;
    public static SKTypeface DejaVuBold        => TestOverrideTypeface ?? _dejaVuBold.Value;
    public static SKTypeface DejaVuOblique     => TestOverrideTypeface ?? _dejaVuOblique.Value;
    public static SKTypeface DejaVuBoldOblique => TestOverrideTypeface ?? _dejaVuBoldOblique.Value;

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

    public static SKTypeface PlexRegular  => TestOverrideTypeface ?? _plexRegular.Value;
    public static SKTypeface PlexBold     => TestOverrideTypeface ?? _plexBold.Value;
    public static SKTypeface PlexSemiBold => TestOverrideTypeface ?? _plexSemiBold.Value;
    public static SKTypeface PlexItalic   => TestOverrideTypeface ?? _plexItalic.Value;
    public static SKTypeface PlexLight    => TestOverrideTypeface ?? _plexLight.Value;

    // ── Helper ────────────────────────────────────────────────────────────────
    private static SKTypeface Load(string assetRelativePath)
    {
        using var stream = AssetLoader.Open(
            new Uri($"avares://CircuitRF.Ui/{assetRelativePath}"));
        return SKTypeface.FromStream(stream) ?? SKTypeface.Default;
    }
}
