using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Tiny live C–V shape preview: bordered box + polyline of (V→X, C→Y) autoscaled
/// to the control bounds.  No axes or labels — shape only.  Mirrors PaletteGlyphControl.
/// </summary>
public sealed class CvShapePreview : Control
{
    public static readonly StyledProperty<IReadOnlyList<CvPoint>?> PointsProperty =
        AvaloniaProperty.Register<CvShapePreview, IReadOnlyList<CvPoint>?>(nameof(Points));

    public IReadOnlyList<CvPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    static CvShapePreview()
    {
        AffectsRender<CvShapePreview>(PointsProperty);
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
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var theme   = SchematicRenderTheme.FromTheme(_activeTheme, variant);
        context.Custom(new DrawOperation(new Rect(Bounds.Size), Points, theme));
    }

    // ── ICustomDrawOperation ─────────────────────────────────────────────────

    private sealed class DrawOperation : ICustomDrawOperation
    {
        private readonly Rect                    _bounds;
        private readonly IReadOnlyList<CvPoint>? _points;
        private readonly SchematicRenderTheme    _theme;

        internal DrawOperation(Rect bounds, IReadOnlyList<CvPoint>? points, SchematicRenderTheme theme)
        {
            _bounds = bounds;
            _points = points;
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
            float w = (float)_bounds.Width;
            float h = (float)_bounds.Height;
            if (w < 4 || h < 4) return;

            // Border box using a muted wire color
            var borderColor = _theme.Wire.WithAlpha(100);
            using var borderPaint = new SKPaint
            {
                IsAntialias = false,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                Color       = borderColor,
            };
            canvas.DrawRect(new SKRect(0.5f, 0.5f, w - 0.5f, h - 0.5f), borderPaint);

            // Need ≥2 distinct-V points for a polyline
            var pts = _points;
            if (pts is null || pts.Count < 2) return;

            double vMin = pts.Min(p => p.V);
            double vMax = pts.Max(p => p.V);
            double cMin = pts.Min(p => p.C);
            double cMax = pts.Max(p => p.C);

            if (vMax <= vMin) return; // degenerate V range

            // Expand zero-width C range so a flat line stays visible
            if (cMax <= cMin) { cMin -= 1; cMax += 1; }

            const float pad = 3f;
            float drawW = w - 2 * pad;
            float drawH = h - 2 * pad;

            float MapX(double v) => pad + (float)((v - vMin) / (vMax - vMin) * drawW);
            // higher C → smaller y (i.e. top of box)
            float MapY(double c) => pad + drawH - (float)((c - cMin) / (cMax - cMin) * drawH);

            using var linePaint = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                Color       = _theme.ParameterNameText,
            };

            using var path = new SKPath();
            path.MoveTo(MapX(pts[0].V), MapY(pts[0].C));
            for (int i = 1; i < pts.Count; i++)
                path.LineTo(MapX(pts[i].V), MapY(pts[i].C));

            canvas.DrawPath(path, linePaint);
        }

        public void Dispose() { }
    }
}
