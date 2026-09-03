// Hands CircuitRF.Design the four embedded IBM Plex faces a label is rendered and flattened with.
//
// LayoutTextOutline moved to CircuitRF.Design when the interchange stack crossed the UI firewall, so
// that `circuitrf convert` can flatten a label to polygons with no Avalonia in the process. It kept
// the glyph arithmetic (SkiaSharp is allowed across the wall) and gave up only the ONE thing that is
// genuinely Avalonia's: SkiaFonts loads its .ttf assets through Avalonia's AssetLoader, which needs a
// live app host. That half stays here, and installs itself.
//
// A MODULE INITIALIZER rather than a call from App.Initialize, deliberately: it runs before any type
// in this assembly is touched, so there is no startup ordering to get wrong and no second entry point
// (the standalone harmonicaRF and wBond binaries are this same assembly with a different Main) to
// remember. The unset fallback is SKTypeface.Default, which is what a headless process gets and what
// every label-carrying export reports.

using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.Renderers;

internal static class UiTypefaceInstaller
{
    [ModuleInitializer]
    internal static void Install() => LayoutTextOutline.TypefaceSource = style => style switch
    {
        // Condensed intentionally maps to the Light weight — matching SchematicRenderer's own
        // TextPrimitive.FontStyle mapping exactly, not a typo. This is the mapping that used to sit
        // inside LayoutTextOutline.ResolveTypeface and it has not changed.
        LabelFontStyle.Bold      => SkiaFonts.PlexBold,
        LabelFontStyle.Italic    => SkiaFonts.PlexItalic,
        LabelFontStyle.Condensed => SkiaFonts.PlexLight,
        _                        => SkiaFonts.PlexRegular,
    };
}
