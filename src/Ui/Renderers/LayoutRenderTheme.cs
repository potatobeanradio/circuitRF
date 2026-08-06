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

    /// <summary>Selection accent — outline drawn above every layer on a selected shape, and the
    /// marquee rectangle fill/stroke (L1c).</summary>
    public SKColor Selection { get; init; }

    /// <summary>A broken bitmap's placeholder box (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md) —
    /// mirrors <c>SchematicRenderTheme.Warning</c>, same role (<c>System.Warning</c>).</summary>
    public SKColor Warning { get; init; }

    /// <summary>brief-L5-followups-2.md §6: a PCell pin's screen-space dot + outward-direction tick
    /// overlay (R-L5g-13) — a color distinct from every layer color, so a pin marker never reads as
    /// copper.</summary>
    public SKColor PCellPin { get; init; }

    /// <summary>brief-L6-L7-em-ui.md R-em-15 — the three EM mesh overlay colours. Conductor and
    /// dielectric-interface segments are visibly different because they are different unknowns
    /// (free vs. bound charge); the truncation marker is the R-mom-10 quantity a user has to be able
    /// to see in order to trust the answer.</summary>
    public SKColor EmMeshConductor  { get; init; }
    public SKColor EmMeshInterface  { get; init; }
    public SKColor EmMeshTruncation { get; init; }

    /// <summary>brief-L8b D5 — the plan-view surface-mesh cell boundary. A separate role from the
    /// three above because both overlays exist at once: kernel A's mesh is a cross-section and draws
    /// as an inset, kernel B's is in the same (x, y) plane the canvas already draws.</summary>
    public SKColor PlanarMeshCell { get; init; }

    /// <summary>L5b DRC violation markers — see <see cref="ColorRole.LayoutDrcError"/>.</summary>
    public SKColor DrcError { get; init; }
    public SKColor DrcWarning { get; init; }
    public SKColor DrcWaived { get; init; }

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
            Selection       = SK(ColorRole.LayoutSelection),
            Warning         = SK(ColorRole.SystemWarning),
            PCellPin        = SK(ColorRole.LayoutPCellPin),
            EmMeshConductor  = SK(ColorRole.LayoutEmMeshConductor),
            EmMeshInterface  = SK(ColorRole.LayoutEmMeshInterface),
            EmMeshTruncation = SK(ColorRole.LayoutEmMeshTruncation),
            PlanarMeshCell   = SK(ColorRole.LayoutPlanarMeshCell),
            DrcError         = SK(ColorRole.LayoutDrcError),
            DrcWarning       = SK(ColorRole.LayoutDrcWarning),
            DrcWaived        = SK(ColorRole.LayoutDrcWaived),
        };
    }

    public static readonly LayoutRenderTheme Light = FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
    public static readonly LayoutRenderTheme Dark  = FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);
}
