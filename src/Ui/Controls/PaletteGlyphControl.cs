using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Small Skia control that renders a SymbolKind's glyph only (no pins, no labels),
/// auto-scaled + centered in the control's bounds, via SchematicRenderer.DrawSymbol.
/// Used as the glyph surface inside a PaletteTile.
/// Transparent background — the hosting button supplies the tile background.
/// </summary>
public sealed class PaletteGlyphControl : Control
{
    // ── StyledProperties ─────────────────────────────────────────────────────

    public static readonly StyledProperty<SymbolKind> KindProperty =
        AvaloniaProperty.Register<PaletteGlyphControl, SymbolKind>(nameof(Kind));

    public static readonly StyledProperty<int> PortCountProperty =
        AvaloniaProperty.Register<PaletteGlyphControl, int>(nameof(PortCount));

    public static readonly StyledProperty<bool> MonochromeProperty =
        AvaloniaProperty.Register<PaletteGlyphControl, bool>(nameof(Monochrome));

    public SymbolKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>
    /// The palette entry's preset port count (0 = the plain generic tile — falls back to each
    /// variadic type's own default). Must match <see cref="PaletteItem.PortCount"/> exactly so the
    /// glyph shown in the palette (port count, lead count) matches what actually gets placed —
    /// owner report, 2026-07-29: Z1P/Z3P/S1P/S3P/SDD1/SDD3 tiles previously always rendered the
    /// 2-port glyph regardless of the entry's own preset count.
    /// </summary>
    public int PortCount
    {
        get => GetValue(PortCountProperty);
        set => SetValue(PortCountProperty, value);
    }

    /// <summary>When true, renders the glyph in neutral grey rather than the themed symbol color.</summary>
    public bool Monochrome
    {
        get => GetValue(MonochromeProperty);
        set => SetValue(MonochromeProperty, value);
    }

    static PaletteGlyphControl()
    {
        AffectsRender<PaletteGlyphControl>(KindProperty);
        AffectsRender<PaletteGlyphControl>(PortCountProperty);
        AffectsRender<PaletteGlyphControl>(MonochromeProperty);
    }

    private ColorTheme _activeTheme = ColorTheme.BuiltIn;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
        _activeTheme = ThemeService.Active;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _activeTheme = ThemeService.Active;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var variant   = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var theme     = SchematicRenderTheme.FromTheme(_activeTheme, variant);
        var effective = Monochrome ? theme.WithMonochrome(variant == ColorVariant.Light) : theme;
        context.Custom(new GlyphDrawOperation(new Rect(Bounds.Size), Kind, PortCount, effective));
    }

    // ── ICustomDrawOperation ─────────────────────────────────────────────────

    private sealed class GlyphDrawOperation : ICustomDrawOperation
    {
        private readonly Rect                 _bounds;
        private readonly SymbolKind           _kind;
        private readonly int                  _portCount;
        private readonly SchematicRenderTheme _theme;

        private const double Padding = 0.12; // 12% inset on each side

        internal GlyphDrawOperation(Rect bounds, SymbolKind kind, int portCount, SchematicRenderTheme theme)
        {
            _bounds    = bounds;
            _kind      = kind;
            _portCount = portCount;
            _theme     = theme;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect  Bounds   => _bounds;
        public bool  HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            Draw(lease.SkCanvas);
        }

        private void Draw(SKCanvas canvas)
        {
            // Do NOT canvas.Clear(SKColors.Transparent) here. SKCanvas.Clear uses Src blend mode, which
            // REPLACES the leased region with fully-transparent pixels — erasing the tile background that
            // Avalonia already composited behind this control. On macOS the opaque window backing masks the
            // hole; on Windows it punches straight through to the desktop (the reported transparency bug).
            // This control is a glyph-only overlay: draw the symbol on top of the existing composited content.
            if (_bounds.Width < 2 || _bounds.Height < 2) return;

            // Variadic types (ZPort/Sdd/Snp) need the ENTRY POINT's own preset port count, not the
            // always-2 default — otherwise Z1P/Z3P/SDD1/SDD3 tiles show the generic 2-port glyph
            // regardless of what the tile actually places (owner report, 2026-07-29).
            bool isVariadic = _kind is SymbolKind.ZPort or SymbolKind.Sdd or SymbolKind.Snp;
            int  n          = _portCount > 0 ? _portCount : 2;
            var prims = isVariadic
                ? BuiltInSymbols.Primitives(_kind, n).Primitives
                : BuiltInSymbols.Primitives(_kind).Primitives;
            if (prims.Count == 0) return;

            var (bbMinX, bbMinY, bbMaxX, bbMaxY) = SymbolGeometry.ComputeBb(prims);

            // ZPort/Sdd draw their port leads dynamically (SchematicRenderer.DrawVariadicPortLeads),
            // never baked into Primitives — the static bbox above covers only the body, so the pin
            // tips (drawn below) must be folded in here or the leads would run past the auto-fit
            // framing. Snp's own leads ARE baked into its primitive list already (BuildSnpSymbol), so
            // no expansion is needed there.
            bool drawsLeadsSeparately = _kind is SymbolKind.ZPort or SymbolKind.Sdd;
            var ports = drawsLeadsSeparately ? SymbolPortDefs.For(_kind, n) : [];
            foreach (var (_, lx, ly) in ports)
            {
                if (lx < bbMinX) bbMinX = lx; if (lx > bbMaxX) bbMaxX = lx;
                if (ly < bbMinY) bbMinY = ly; if (ly > bbMaxY) bbMaxY = ly;
            }

            // Guard: degenerate / empty bbox from an all-Text primitive list
            if (bbMaxX <= bbMinX || bbMaxY <= bbMinY)
            {
                bbMinX = -200; bbMinY = -200; bbMaxX = 200; bbMaxY = 200;
            }

            double worldW = bbMaxX - bbMinX;
            double worldH = bbMaxY - bbMinY;

            // Scale to fit the bbox in the tile with uniform padding on all sides
            double zoom = Math.Min(_bounds.Width / worldW, _bounds.Height / worldH)
                        * (1.0 - 2.0 * Padding);
            if (zoom <= 0) zoom = 0.01;

            // Pan so the glyph center maps to the tile center
            double cx   = (bbMinX + bbMaxX) * 0.5;
            double cy   = (bbMinY + bbMaxY) * 0.5;
            double panX = cx - _bounds.Width  / (2.0 * zoom);
            double panY = cy - _bounds.Height / (2.0 * zoom);

            SchematicRenderer.DrawSymbol(
                canvas, prims,
                compX: 0, compY: 0,
                rotation: SymbolRotation.R0, mirrorX: false,
                panX: panX, panY: panY, zoom: zoom,
                theme: _theme);

            // ZPort/Sdd's port leads are drawn dynamically by the real schematic renderer
            // (DrawVariadicPortLeads) — never baked into Primitives — so the palette glyph must draw
            // them too, or a Z/SDD tile shows a bare body with no lines to the pins at all (owner
            // report, 2026-07-29). Body edge is the same fixed ±90 DrawVariadicPortLeads itself uses.
            if (drawsLeadsSeparately)
            {
                using var leadPaint = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)Math.Max(1.0, zoom * 3), Color = _theme.SymbolLine,
                };
                const float bodyEdge = 90f;
                foreach (var (_, lx, ly) in ports)
                {
                    float innerX = lx < 0f ? -bodyEdge : bodyEdge;
                    var (ax, ay) = SchematicRenderer.LocalToPixel(lx, ly, 0, 0, SymbolRotation.R0, false, panX, panY, zoom);
                    var (bx, by) = SchematicRenderer.LocalToPixel(innerX, ly, 0, 0, SymbolRotation.R0, false, panX, panY, zoom);
                    canvas.DrawLine(ax, ay, bx, by, leadPaint);
                }
            }
        }

        public void Dispose() { }
    }
}
