using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Views.Match;

/// <summary>
/// The Designer's network pane: a <b>read-only circuitRF schematic</b> of the match network, drawn by
/// <see cref="SchematicRenderer"/> itself, with scroll-wheel zoom and drag-to-pan.
/// </summary>
/// <remarks>
/// <b>Why the real renderer and not a second drawing of the same circuit</b> (owner, 2026-08-19: the
/// pane "looks very different than a regular circuitRF schematic — host a virtual schematic view…
/// that way the network schematic look and feel will always be linked to a circuitRF schematic").
/// Everything visual here — the grid, the glyphs, the three label roles and their colours, the
/// connected-pin markers, the LOD fade — comes from the editor's own renderer through
/// <see cref="MatchSchematicModel"/>, so none of it can drift out of step with a schematic page.
///
/// <para><b>Nothing is editable and there is no selection.</b> No overlay is passed to the renderer,
/// no hit-testing is done, and no pointer gesture reaches the design: the only two the control
/// accepts are the two the owner asked for. The Designer edits a network through its own panes; a
/// click on this one must never be able to move a component that has no persisted position.</para>
/// </remarks>
public sealed class MatchSchematicCanvas : Control
{
    /// <summary>The ladder to project and draw.</summary>
    public static readonly StyledProperty<MatchLadderLayout?> LayoutProperty =
        AvaloniaProperty.Register<MatchSchematicCanvas, MatchLadderLayout?>(nameof(Layout));

    /// <inheritdoc cref="LayoutProperty"/>
    public MatchLadderLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    private const double MinZoom = 0.005;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 1.15;

    private SchematicModel _model = MatchSchematicModel.Empty;
    private MatchLadderLayout? _layout;
    private double _panX, _panY, _zoom = 1.0;
    private bool _fitted;
    private Point? _dragFrom;
    private ColorTheme _theme = ColorTheme.BuiltIn;

    /// <summary>Builds a canvas that redraws itself whenever the active colour theme changes.</summary>
    public MatchSchematicCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _theme = ThemeService.Active;
        ThemeService.ThemeChanged += OnThemeChanged;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _theme = ThemeService.Active;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LayoutProperty) return;

        var next = Layout;

        // A rebuild that produced the same SHAPE — the same elements in the same places — keeps the
        // user's zoom and pan. Dragging a transform's N rebuilds the ladder on every frame, and a
        // pane that re-fitted itself each time would swim under the pointer. A structural change
        // (an element added, removed or moved) re-fits, because the old viewport is then framing a
        // circuit that no longer exists.
        bool sameShape = SameShape(_layout, next);
        _layout = next;
        _model = MatchSchematicModel.Build(next);
        if (!sameShape) _fitted = false;
        InvalidateVisual();
    }

    private static bool SameShape(MatchLadderLayout? a, MatchLadderLayout? b)
    {
        if (a is null || b is null) return false;
        if (a.Elements.Count != b.Elements.Count) return false;
        for (int i = 0; i < a.Elements.Count; i++)
        {
            var (x, y) = (a.Elements[i], b.Elements[i]);
            if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal)
                || x.IsShunt != y.IsShunt || x.Type != y.Type
                || Math.Abs(x.X - y.X) > 0.5 || Math.Abs(x.Y - y.Y) > 0.5)
                return false;
        }
        return true;
    }

    /// <summary>Frames the whole network, the way the editor's own Zoom to Fit does.</summary>
    public void ZoomToFit()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        double worldW = Math.Max(_model.BbMaxX - _model.BbMinX, 1);
        double worldH = Math.Max(_model.BbMaxY - _model.BbMinY, 1);
        const double pad = 0.04;
        _zoom = Math.Clamp(Math.Min(Bounds.Width / worldW, Bounds.Height / worldH) * (1.0 - 2 * pad),
                           MinZoom, MaxZoom);
        _panX = _model.BbMinX - (Bounds.Width  - worldW * _zoom) / (2 * _zoom);
        _panY = _model.BbMinY - (Bounds.Height - worldH * _zoom) / (2 * _zoom);
        _fitted = true;
        InvalidateVisual();
    }

    // ── The two gestures ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;

        // Zoom about the pointer, not about the pane's centre: the point under the cursor is the one
        // the user is looking at, and it must not move.
        var p = e.GetPosition(this);
        double wx = p.X / _zoom + _panX, wy = p.Y / _zoom + _panY;
        double next = Math.Clamp(_zoom * (e.Delta.Y > 0 ? ZoomStep : 1.0 / ZoomStep), MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < double.Epsilon) return;
        _zoom = next;
        _panX = wx - p.X / _zoom;
        _panY = wy - p.Y / _zoom;
        _fitted = true;
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        var to = e.GetPosition(this);
        _panX -= (to.X - from.X) / _zoom;
        _panY -= (to.Y - from.Y) / _zoom;
        _dragFrom = to;
        _fitted = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragFrom = null;
    }

    /// <summary>Double-click re-frames — the way back from a pan that lost the drawing.</summary>
    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        ZoomToFit();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_fitted) ZoomToFit();
    }

    // ── Render ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 4 || Bounds.Height < 4) return;
        if (!_fitted) ZoomToFit();

        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        context.Custom(new Op(new Rect(Bounds.Size), _model, _layout,
                              _panX, _panY, _zoom,
                              SchematicRenderTheme.FromTheme(_theme, variant), _theme, variant));
    }

    /// <summary>
    /// Draws the schematic, then the two things a schematic has no way to say about a match network:
    /// which elements the external terminations already supply, and which values came out unbuildable.
    /// </summary>
    private sealed class Op(
        Rect bounds, SchematicModel model, MatchLadderLayout? layout,
        double panX, double panY, double zoom,
        SchematicRenderTheme render, ColorTheme theme, ColorVariant variant) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var l = lease.Lease();
            var canvas = l.SkCanvas;

            SchematicRenderer.Draw(canvas, (bounds.Width, bounds.Height), model, null,
                                   panX, panY, zoom, render);
            if (layout is null) return;

            DrawRoles(canvas);
            DrawBrackets(canvas);
        }

        private SKColor Role(string role)
        {
            var c = theme.Resolve(role, variant);
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        private (float X, float Y) P(double wx, double wy) =>
            ((float)((wx - panX) * zoom), (float)((wy - panY) * zoom));

        /// <summary>
        /// <b>Absorbed</b> elements are washed towards the background — the schematic renderer has one
        /// symbol colour and no notion of an element this component does not contain, so the dimming
        /// is applied over the finished glyph rather than by drawing it a second time in another
        /// colour. <b>Out-of-range</b> values get a warning-coloured box around the glyph: the
        /// capacitor is a perfectly ordinary capacitor, its VALUE is the unbuildable part, so the mark
        /// says "look at this one" rather than recolouring the symbol.
        /// </summary>
        private void DrawRoles(SKCanvas canvas)
        {
            using var wash = new SKPaint
            {
                IsAntialias = false,
                Style = SKPaintStyle.Fill,
                Color = Role(ColorRole.SchematicBackground).WithAlpha(150),
            };
            using var bad = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.2, zoom * 10),
                Color = Role(ColorRole.MatchNegative),
            };

            foreach (var e in layout!.Elements)
            {
                if (e.Role is not (MatchElementRole.Absorbed or MatchElementRole.OutOfRange)) continue;

                var comp = model.Components.FirstOrDefault(
                    c => string.Equals(c.Id, e.Name, StringComparison.Ordinal));
                if (comp is null) continue;

                // The GLYPH box, never the full one: FullBb is inflated by a deliberately generous
                // per-character LABEL WIDTH ESTIMATE (SchematicComponent.LabelWidthFor, floored at
                // 500 world units), which is right for culling and four times too wide for a mark
                // the user is meant to read as being about ONE component.
                float pad = (float)(zoom * 20);
                var (ax, ay) = P(comp.GlyphBbMinX, comp.GlyphBbMinY);
                var (bx, by) = P(comp.GlyphBbMaxX, comp.GlyphBbMaxY);
                var box = new SKRect(ax - pad, ay - pad, bx + pad, by + pad);

                if (e.Role == MatchElementRole.Absorbed) canvas.DrawRect(box, wash);
                else canvas.DrawRoundRect(box, pad, pad, bad);
            }
        }

        /// <summary>The transform brackets, beneath the products they act on (match.md §9.3).</summary>
        private void DrawBrackets(SKCanvas canvas)
        {
            if (layout!.Brackets.Count == 0) return;

            var colour = Role(ColorRole.MatchBracket);
            using var pen = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 12), Color = colour,
            };
            using var text = new SKPaint { IsAntialias = true, Color = colour };
            using var font = new SKFont(SkiaFonts.PlexRegular, (float)Math.Max(6.0, zoom * 90));

            foreach (var b in layout.Brackets)
            {
                double y = MatchLadderLayout.BracketY + b.Row * MatchLadderLayout.BracketRowPitch;
                var (x0, y0) = P(b.X0, y);
                var (x1, _)  = P(b.X1, y);
                var (_, yUp) = P(b.X0, y - 60);
                canvas.DrawLine(x0, y0, x1, y0, pen);
                canvas.DrawLine(x0, y0, x0, yUp, pen);
                canvas.DrawLine(x1, y0, x1, yUp, pen);

                var (cx, cy) = P((b.X0 + b.X1) / 2, y + 120);
                canvas.DrawText(b.Label, cx, cy, SKTextAlign.Center, font, text);
            }
        }
    }
}
