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

    public static readonly StyledProperty<bool> MonochromeProperty =
        AvaloniaProperty.Register<PaletteGlyphControl, bool>(nameof(Monochrome));

    public SymbolKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
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
        context.Custom(new GlyphDrawOperation(new Rect(Bounds.Size), Kind, effective));
    }

    // ── ICustomDrawOperation ─────────────────────────────────────────────────

    private sealed class GlyphDrawOperation : ICustomDrawOperation
    {
        private readonly Rect                 _bounds;
        private readonly SymbolKind           _kind;
        private readonly SchematicRenderTheme _theme;

        private const double Padding = 0.12; // 12% inset on each side

        internal GlyphDrawOperation(Rect bounds, SymbolKind kind, SchematicRenderTheme theme)
        {
            _bounds = bounds;
            _kind   = kind;
            _theme  = theme;
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

            var prims = BuiltInSymbols.Primitives(_kind).Primitives;
            if (prims.Count == 0) return;

            var (bbMinX, bbMinY, bbMaxX, bbMaxY) = SymbolGeometry.ComputeBb(prims);

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
        }

        public void Dispose() { }
    }
}
