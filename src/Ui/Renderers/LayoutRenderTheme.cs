using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// SKColor token bundle for the layout canvas's chrome (background, grid, rulers, cursor
/// indicator). Layer colors themselves are NOT here — they are literal <c>Rgba</c> on
/// <c>LayerDef</c>/<c>FallbackPalette</c> (docs/design/layout-view.md §2.2), converted to SKColor
/// per-shape by <see cref="LayoutRenderer"/> directly. Mirrors <see cref="SchematicRenderTheme"/>'s
/// projection pattern (L2).
/// </summary>
public sealed class LayoutRenderTheme
{
    public SKColor Background      { get; init; }
    public SKColor GridMinor       { get; init; }
    public SKColor GridMajor       { get; init; }
    public SKColor RulerBackground { get; init; }
    public SKColor RulerText       { get; init; }
    public SKColor RulerTick       { get; init; }
    public SKColor CursorIndicator { get; init; }

    public static LayoutRenderTheme FromTheme(ColorTheme theme, ColorVariant variant)
    {
        SKColor SK(string role)
        {
            var c = theme.Resolve(role, variant);
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return new LayoutRenderTheme
        {
            Background      = SK(ColorRole.LayoutBackground),
            GridMinor       = SK(ColorRole.LayoutGridMinor),
            GridMajor       = SK(ColorRole.LayoutGridMajor),
            RulerBackground = SK(ColorRole.LayoutRulerBackground),
            RulerText       = SK(ColorRole.LayoutRulerText),
            RulerTick       = SK(ColorRole.LayoutRulerTick),
            CursorIndicator = SK(ColorRole.LayoutCursorIndicator),
        };
    }

    public static readonly LayoutRenderTheme Light = FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
    public static readonly LayoutRenderTheme Dark  = FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);
}
