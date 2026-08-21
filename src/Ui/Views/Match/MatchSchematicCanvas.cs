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
/// <para><b>There is no selection and nothing can be MOVED.</b> No overlay is passed to the renderer
/// and no gesture can reposition a component that has no persisted position to reposition. What the
/// pane does accept, since 2026-08-20, is a double-click on a VALUE label — which opens the schematic
/// editor's own inline text box over it (<see cref="Controls.SchematicInlineEditBox"/>) and hands the
/// typed value to the Designer. The canvas only says WHICH label was hit and WHERE it is on screen;
/// what a value means and what changing it costs is the view-model's, in
/// <c>MatchDesignerViewModel.InlineEdit.cs</c>.</para>
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
    /// <remarks>
    /// <b>The pointer keeps the ordinary arrow</b> (owner, 2026-08-20: "don't use mouse cross hair
    /// cursor in the Match Designer schematic; stick to regular arrow icon"). The four-way
    /// <c>SizeAll</c> this replaces advertised a move gesture the pane does not have — nothing here
    /// is editable and nothing can be dragged but the view itself.
    /// </remarks>
    public MatchSchematicCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
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
        if (!Fit()) return;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The framing arithmetic on its own, <b>with no <c>InvalidateVisual</c> in it</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported crash, 2026-08-20</b> — changing a PI network to a T threw
    /// <c>InvalidOperationException: Visual was invalidated during the render pass</c>, from
    /// <c>ZoomToFit</c>, called by <c>Render</c>. A structural change clears <c>_fitted</c>, the next
    /// <c>Render</c> re-fitted, and the fit invalidated the visual it was in the middle of drawing —
    /// which Avalonia refuses outright rather than silently re-entering. The fix is not to guard the
    /// call site: it is that a fit performed FROM the render pass has nothing to request, since the
    /// frame it would ask for is the frame being drawn. So the arithmetic lives here and the two
    /// callers differ only in whether they ask for a repaint afterwards.
    ///
    /// <para>Returns false when the control has no usable size yet, so <c>_fitted</c> stays clear and
    /// the next frame tries again.</para>
    /// </remarks>
    private bool Fit()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return false;
        double worldW = Math.Max(_model.BbMaxX - _model.BbMinX, 1);
        double worldH = Math.Max(_model.BbMaxY - _model.BbMinY, 1);
        const double pad = 0.04;
        _zoom = Math.Clamp(Math.Min(Bounds.Width / worldW, Bounds.Height / worldH) * (1.0 - 2 * pad),
                           MinZoom, MaxZoom);
        _panX = _model.BbMinX - (Bounds.Width  - worldW * _zoom) / (2 * _zoom);
        _panY = _model.BbMinY - (Bounds.Height - worldH * _zoom) / (2 * _zoom);
        _fitted = true;
        return true;
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
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Starts a pan — <b>on the MIDDLE button only</b>, exactly as a schematic page does.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"panning in the schematic view should work using center mouse
    /// button (just like regular schematic), not the left mouse button."</i>
    /// <c>SchematicCanvas</c> pans on <c>IsMiddleButtonPressed</c> and reserves the left button for
    /// what the pointer is aimed AT; this pane now agrees, which is what frees the left button for
    /// the double-click that opens a label's inline editor. The right button still belongs to the
    /// context menu — a right-drag that also panned slid the drawing under the menu the user was
    /// aiming at, which is the bug that made this a left-only gesture in the first place.
    /// </remarks>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return;
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
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
        ViewportChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// Double-click on a value label opens its inline editor; anywhere else it re-frames — the way
    /// back from a pan that lost the drawing.
    /// </summary>
    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (HitLabel(e.GetPosition(this)) is { } hit)
            LabelDoubleTapped?.Invoke(this, hit);
        else
            ZoomToFit();
        e.Handled = true;
    }

    // ── Label hit-testing, for the inline editor ──────────────────────────────

    /// <summary>Raised when a component label is double-clicked. The HOST decides what is editable.</summary>
    public event EventHandler<MatchSchematicLabelHit>? LabelDoubleTapped;

    /// <summary>
    /// Raised after any zoom, pan or re-fit, so an open editor can follow the label it sits on.
    /// </summary>
    public event EventHandler? ViewportChanged;

    /// <summary>
    /// The label under one point in control-local pixels, or null.
    /// </summary>
    /// <remarks>
    /// The geometry is <see cref="MatchSchematicLabels"/>'s — pure, and asserted directly by test.
    /// All this adds is the two things only a live control knows: the world-to-screen mapping, and
    /// whether the renderer is drawing labels at this zoom at all.
    /// </remarks>
    public MatchSchematicLabelHit? HitLabel(Point p)
    {
        // Zoomed far enough out, the renderer draws no labels. Offering a click target for text that
        // is not on screen would open an editor over nothing.
        if (!SchematicRenderer.LabelsVisibleAt(_zoom)) return null;

        // The pick tolerance is a SCREEN distance converted back to world units. A label row is about
        // nine pixels tall at the zoom this pane frames a whole network at, and a world-unit-only band
        // is a target the user has to fight — see MatchSchematicLabels.HitTest. Zoomed in, PickPixels
        // divided by a large zoom is a fraction of a world unit and the tolerance vanishes.
        double tolerance = PickPixels / _zoom;
        return ToScreen(MatchSchematicLabels.HitTest(
            _model, p.X / _zoom + _panX, p.Y / _zoom + _panY, tolerance));
    }

    /// <summary>How far off a label the pointer may be and still pick it, screen pixels.</summary>
    private const double PickPixels = 4.0;

    /// <summary>
    /// Re-derives one label's on-screen anchor at the CURRENT zoom and pan — what an open editor needs
    /// after the view moves under it. Null when that label is no longer in the drawing.
    /// </summary>
    public MatchSchematicLabelHit? AnchorFor(string componentId, int row) =>
        ToScreen(MatchSchematicLabels.Locate(_model, componentId, row));

    private MatchSchematicLabelHit? ToScreen(MatchLabelHit? hit) =>
        hit is null
            ? null
            : new MatchSchematicLabelHit(
                hit.ComponentId, hit.Row, hit.Text,
                ScreenX: (hit.BaseX + hit.PrefixWidth - _panX) * _zoom,
                ScreenY: (hit.BaselineY - _panY) * _zoom,
                FontSize: Controls.SchematicInlineEditBox.FontSizeAt(_zoom));

    // ── Render ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 4 || Bounds.Height < 4) return;
        if (!_fitted) Fit();   // never ZoomToFit() — see Fit()'s own note on the render-pass crash

        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        context.Custom(new Op(new Rect(Bounds.Size), _model, _layout,
                              _panX, _panY, _zoom,
                              SchematicRenderTheme.FromTheme(_theme, variant), _theme, variant));
    }

    /// <summary>
    /// Draws the schematic, then the two things a schematic has no way to say about a match network:
    /// which values came out unbuildable, and which transform produced which run of elements.
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

            DrawOutOfRange(canvas);
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
        /// <b>Out-of-range</b> values get a warning-coloured box around the glyph: the capacitor is a
        /// perfectly ordinary capacitor, its VALUE is the unbuildable part, so the mark says "look at
        /// this one" rather than recolouring the symbol.
        /// </summary>
        /// <remarks>
        /// <b>Absorbed elements are no longer washed out here</b> (owner, 2026-08-20: "do not render
        /// any component as dimmed"). This method used to paint a semi-transparent background-coloured
        /// rectangle over an absorbed glyph; what an absorbed element is now reads off its POSITION —
        /// it is always the one beside its termination — and off the pane's own legend, which names
        /// them.
        /// </remarks>
        private void DrawOutOfRange(SKCanvas canvas)
        {
            using var bad = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.2, zoom * 10),
                Color = Role(ColorRole.MatchNegative),
            };

            foreach (var e in layout!.Elements)
            {
                if (e.Role != MatchElementRole.OutOfRange) continue;

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
                canvas.DrawRoundRect(new SKRect(ax - pad, ay - pad, bx + pad, by + pad), pad, pad, bad);
            }
        }

        /// <summary>
        /// The transform braces, beneath the products they act on (match.md §9.3) — a real curly
        /// brace, not three straight lines.
        /// </summary>
        /// <remarks>
        /// <b>Owner, 2026-08-20:</b> "the transform curly brace rendering needs aesthetic improvement.
        /// It needs to have smooth 'curl' at its left and right edge locations. It also needs a stem
        /// from the center of its horizontal line to its rendered text transform name."
        ///
        /// <para>What is drawn is one continuous under-brace path: a quarter-turn UP at each end
        /// (towards the elements the brace is about), a straight run along the top, and a
        /// quarter-turn each side into a centre tip pointing DOWN — then the stem from that tip to the
        /// label. Quadratic segments with the corner itself as the control point are what make each
        /// turn tangent to both the vertical and the horizontal, which is the whole difference between
        /// a brace and a bracket. The path is built in WORLD units and mapped point by point, so the
        /// brace scales with the drawing exactly as the glyphs do.</para>
        ///
        /// <para><b>Two colours, and they are different roles</b> — the brace is
        /// <c>Schematic.ParameterNameText</c> and the N1/N2 label is <c>Schematic.ComponentNameText</c>,
        /// read off the bracket record rather than chosen here.</para>
        /// </remarks>
        private void DrawBrackets(SKCanvas canvas)
        {
            if (layout!.Brackets.Count == 0) return;

            using var pen = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeWidth = (float)Math.Max(1.0, zoom * 12),
            };
            using var text = new SKPaint { IsAntialias = true };
            using var font = new SKFont(SkiaFonts.PlexRegular, (float)Math.Max(6.0, zoom * 90));

            foreach (var b in layout.Brackets)
            {
                double y = MatchLadderLayout.BracketY + b.Row * MatchLadderLayout.BracketRowPitch;

                var outline = MatchBraceGeometry.Outline(
                    b.X0, b.X1, y, MatchLadderLayout.BraceCurl);
                if (outline.Count == 0) continue;

                pen.Color = Role(b.ColorRoleKey);

                using var path = new SKPath();
                foreach (var step in outline)
                {
                    var (ex, ey) = P(step.X, step.Y);
                    switch (step.Kind)
                    {
                        case MatchBraceStepKind.Move: path.MoveTo(ex, ey); break;
                        case MatchBraceStepKind.Line: path.LineTo(ex, ey); break;
                        default:
                            var (cx, cy) = P(step.CX, step.CY);
                            path.QuadTo(cx, cy, ex, ey);
                            break;
                    }
                }
                canvas.DrawPath(path, pen);

                if (MatchBraceGeometry.Stem(b.X0, b.X1, y, MatchLadderLayout.BraceCurl,
                                            MatchLadderLayout.BraceStem,
                                            MatchLadderLayout.BraceLabelDrop) is not { } s)
                    continue;

                var (sx, sy0) = P(s.X, s.Y0);
                var (_,  sy1) = P(s.X, s.Y1);
                canvas.DrawLine(sx, sy0, sx, sy1, pen);

                text.Color = Role(b.LabelColorRoleKey);
                var (lx, ly) = P(s.X, s.LabelBaselineY);
                canvas.DrawText(b.Label, lx, ly, SKTextAlign.Center, font, text);
            }
        }
    }
}

/// <summary>
/// One component label in the Designer's network pane, as the canvas found it.
/// </summary>
/// <param name="ComponentId">The projected component's id — an element's ladder name, or "Termination 1".</param>
/// <param name="Row">Which label row: 0 type, 1 instance name, 2 the value.</param>
/// <param name="Text">The EDITABLE part of the row — "1.53 nH", not "L = 1.53 nH".</param>
/// <param name="ScreenX">Where that part's text starts, in the canvas's own pixels.</param>
/// <param name="ScreenY">Its Skia baseline, in the same pixels.</param>
/// <param name="FontSize">The point size the row is drawn at, at the current zoom.</param>
public sealed record MatchSchematicLabelHit(
    string ComponentId, int Row, string Text, double ScreenX, double ScreenY, double FontSize);
