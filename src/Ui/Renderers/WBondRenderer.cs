using System;
using System.Collections.Generic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using CircuitRF.WBond;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>How a wire's line thickness is chosen (wbond.md WB22a).</summary>
public enum WireThicknessMode
{
    /// <summary>
    /// A constant screen width. The default, because at whole-package zoom a 1 mil wire is
    /// sub-pixel and would vanish.
    /// </summary>
    ConstantPixels,

    /// <summary>
    /// The wire's real diameter in design units, scaled with zoom like layout geometry.
    ///
    /// <para>This is what lets a user judge wire-to-wire and wire-to-pad clearance by eye before the
    /// DRC is run — the whole reason the owner asked for it.</para>
    /// </summary>
    TrueDiameter,
}

/// <summary>Colours and sizes for the wBond overlay. Projected from the theme, like every other renderer.</summary>
public sealed class WBondRenderTheme
{
    /// <summary>Ordinary wire.</summary>
    public SKColor Wire { get; init; }

    /// <summary>
    /// The INPUT end's dot — "a subtle distinct off-colour so the user knows which end is the start"
    /// (§6.2). It matters because the sign of every mutual depends on it (WB3).
    /// </summary>
    public SKColor InputEnd { get; init; }

    public SKColor Selected { get; init; }

    /// <summary>The translucent min/max band over an array's bound members (§6.2 idea 3).</summary>
    public SKColor Envelope { get; init; }

    /// <summary>A wire detached from its profile, drawn individually.</summary>
    public SKColor FreeWire { get; init; }

    public float DotRadiusPx { get; init; } = 3.0f;

    public float LineWidthPx { get; init; } = 1.5f;

    /// <summary>
    /// Projects the five wBond roles of the active colour theme into the SKColors this renderer
    /// draws with — the L2 "renderer tokens are a projection of the theme, never hardcoded" rule
    /// (docs/design/color-themes.md), which this theme was the last canvas in the application to be
    /// outside of.
    ///
    /// <para><b>This is what makes the variant matter.</b> Before it, both canvases drew
    /// <see cref="Fallback"/> in light and dark alike, so the selection accent was white on a
    /// near-white canvas in light mode — the owner's report, and not a tuning problem: nothing was
    /// reading the light palette at all.</para>
    /// </summary>
    public static WBondRenderTheme FromTheme(ColorTheme theme, ColorVariant variant)
    {
        ArgumentNullException.ThrowIfNull(theme);

        SKColor SK(string role)
        {
            var c = theme.Resolve(role, variant);
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return new WBondRenderTheme
        {
            Wire     = SK(ColorRole.WBondWire),
            InputEnd = SK(ColorRole.WBondWireStart),
            Selected = SK(ColorRole.WBondSelected),
            Envelope = SK(ColorRole.WBondEnvelope),
            FreeWire = SK(ColorRole.WBondFreeWire),
        };
    }

    /// <summary>
    /// A sensible default so a canvas can render without a theme wired up yet — the BUILT-IN dark
    /// palette, not a private copy of it, so "the fallback" and "the shipped dark theme" cannot drift
    /// apart the way they had.
    /// </summary>
    public static WBondRenderTheme Fallback { get; } =
        FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);

    /// <summary>The built-in light palette — the counterpart <see cref="Fallback"/> never had.</summary>
    public static WBondRenderTheme Light { get; } =
        FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
}

/// <summary>
/// Draws the wBond wire overlay — the layout view's X-Y projection and the profile view's span-Z
/// projection (wbond.md §6.1, §6.2).
///
/// <h3>An overlay, not a layout shape type (WB23 / D10)</h3>
/// <para>Wires are drawn in their own pass over the existing layout canvas. Nothing enters
/// <c>.clay</c>, no 3D shape type is added, and — the part that matters for performance — <b>a wire
/// drag must not invalidate the layout's path cache</b> (WB17). That is why this is a separate
/// <see cref="Draw"/> rather than shapes handed to <c>LayoutRenderer</c>.</para>
///
/// <h3>Cheap by construction</h3>
/// <para>600 wires × 6 segments is 3,600 lines and 4,200 dots — trivial for Skia in a batched pass.
/// The rendering risk in this editor was never the wires; it is the 500k-shape layout underneath,
/// which is already characterised and gated by the existing layout perf tests.</para>
/// </summary>
public static class WBondRenderer
{
    /// <summary>What one <see cref="Draw"/> call did, for the perf counters (WB17).</summary>
    /// <param name="WiresDrawn">Wires that produced at least one visible segment.</param>
    /// <param name="SegmentsDrawn">Line segments emitted.</param>
    /// <param name="DotsDrawn">Vertex dots emitted.</param>
    public readonly record struct Result(int WiresDrawn, int SegmentsDrawn, int DotsDrawn);

    /// <summary>
    /// Draws the layout-view (X-Y) projection of every wire.
    /// </summary>
    /// <param name="selection">Selected items, drawn in the accent colour. May be null.</param>
    /// <param name="thickness">
    /// <see cref="WireThicknessMode.TrueDiameter"/> draws each wire at its real diameter so its bulk
    /// can be judged against neighbouring geometry; the constant-pixel default keeps it visible when
    /// zoomed out over a whole package.
    /// </param>
    /// <param name="frameTransform">
    /// Maps a world-space wire point (nanometres) into the coordinate frame the canvas is currently
    /// showing — non-null only while the layout editor is pushed into a sub-cell (WB27). Null means
    /// world coordinates ARE the frame, and no per-point work is done.
    /// </param>
    /// <param name="opacity">
    /// Scales every colour's alpha. <see cref="WBondDescent.DimmedAlpha"/> while at depth, where the
    /// wires are a locked reference rather than something the user can edit.
    /// </param>
    /// <param name="dbuPerMicron">
    /// The HOST LAYOUT's resolution. A wire point is stored in nanometres; <see cref="LayoutViewport"/>
    /// works in the layout's own database units. The two coincide only at the 1,000 DBU/µm default —
    /// see <see cref="WBondSnap"/> for why this bridge is stated everywhere it is crossed rather than
    /// assumed.
    /// </param>
    public static Result Draw(
        SKCanvas canvas, WBondDesign design, LayoutViewport viewport, WBondRenderTheme theme,
        WireSelection? selection = null,
        WireThicknessMode thickness = WireThicknessMode.ConstantPixels,
        Func<long, long, (double X, double Y)>? frameTransform = null,
        byte opacity = 255,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(theme);   // LayoutViewport is a struct — no null check

        int wires = 0, segments = 0, dots = 0;
        int index = -1;

        // One place decides where a wire point lands on screen, so the base and descended cases can
        // never drift apart in their rounding.
        (float Sx, float Sy) Screen(Point3 p)
        {
            var (nx, ny) = frameTransform is null ? (p.X, (double)p.Y) : frameTransform(p.X, p.Y);
            return ((float)viewport.WorldToScreenX(NmToDbu(nx, dbuPerMicron)),
                    (float)viewport.WorldToScreenY(NmToDbu(ny, dbuPerMicron)));
        }

        using var linePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var dotPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        foreach (var wire in design.AllWires())
        {
            index++;
            if (wire.Points.Count < 2) continue;

            bool wholeSelected = selection?.Wires.Contains(index) == true;
            bool free = wire.ProfileBinding is null;

            linePaint.Color = Fade(wholeSelected ? theme.Selected : free ? theme.FreeWire : theme.Wire, opacity);
            linePaint.StrokeWidth = StrokeWidth(wire, viewport, theme, thickness, dbuPerMicron);

            var baseColor = linePaint.Color;

            for (int i = 1; i < wire.Points.Count; i++)
            {
                var (ax, ay) = Screen(wire.Points[i - 1]);
                var (bx, by) = Screen(wire.Points[i]);

                // A SEGMENT accent, which this view did not have at all: it coloured whole wires and
                // nothing finer, so clicking a segment in the profile view highlighted it there and
                // showed nothing here. The two views draw the same selection and must agree about it.
                linePaint.Color = SegmentSelected(selection, index, i - 1, wholeSelected)
                    ? Fade(theme.Selected, opacity)
                    : baseColor;

                canvas.DrawLine(ax, ay, bx, by, linePaint);
                segments++;
            }

            linePaint.Color = baseColor;

            for (int i = 0; i < wire.Points.Count; i++)
            {
                var (px, py) = Screen(wire.Points[i]);

                // The INPUT end gets its own colour. Rendering, but not decoration: the sign of every
                // mutual inductance depends on which end this is (WB3). A SELECTED point outranks it,
                // though — the accent is transient and says what the user is holding, and without this
                // picking the input foot lit up nothing at all. The end is still identifiable while
                // selected: it is still the wire's first dot.
                bool pointSelected = wholeSelected || PointSelected(selection, index, i);
                dotPaint.Color = pointSelected ? Fade(theme.Selected, opacity)
                               : i == 0 ? Fade(theme.InputEnd, opacity)
                               : linePaint.Color;

                canvas.DrawCircle(px, py, theme.DotRadiusPx, dotPaint);
                dots++;
            }

            wires++;
        }

        return new Result(wires, segments, dots);
    }

    /// <summary>
    /// Draws the profile view: span horizontally, z up, with the array envelope behind the wires.
    /// </summary>
    /// <param name="spanToScreen">Maps a projected span coordinate to screen x.</param>
    /// <param name="zToScreen">Maps a z coordinate (nanometres) to screen y.</param>
    /// <param name="azimuthRadians">
    /// The fixed plane to project onto, or null for AUTO (each wire on its own chord). Passed straight
    /// through to <see cref="ProfileProjection.Project"/> — the same value the canvas hit-tests and
    /// drags with, so what is drawn and what is grabbed cannot disagree.
    /// </param>
    public static Result DrawProfile(
        SKCanvas canvas, WBondDesign design, WBondRenderTheme theme,
        Func<double, float> spanToScreen, Func<double, float> zToScreen,
        ProfileProjection.SpanMode mode = ProfileProjection.SpanMode.Absolute,
        WireSelection? selection = null,
        IReadOnlyList<int>? visibleArrays = null,
        double? azimuthRadians = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(spanToScreen);
        ArgumentNullException.ThrowIfNull(zToScreen);

        int wires = 0, segments = 0, dots = 0;
        int flatIndex = -1;

        using var linePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = theme.LineWidthPx };
        using var dotPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var bandPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.Envelope };

        for (int a = 0; a < design.Arrays.Count; a++)
        {
            var array = design.Arrays[a];
            bool visible = visibleArrays is null || visibleArrays.Contains(a);

            var envelope = ProfileEnvelope.Build(array);

            if (visible && envelope.Bands.Count > 1)
                DrawBand(canvas, array, envelope, spanToScreen, zToScreen, mode, bandPaint, azimuthRadians);

            // §6.2 idea 3 is "ONE editable curve per array PLUS a translucent band", and the curve
            // half was missing. Without it an array whose members all share one shape draws a band of
            // zero thickness — min == max at every sample — so the whole array renders as nothing.
            // That is the ordinary case, not an edge case: a one-wire array, and any array mid-drag
            // once the quality ladder has collapsed its members onto their chords (WB15), both hit it,
            // which is why the profile view "sometimes disappears" while dragging in the layout view.
            int representative = visible && envelope.BoundWires.Count > 0 ? envelope.BoundWires[0] : -1;

            for (int w = 0; w < array.Wires.Count; w++)
            {
                flatIndex++;
                var wire = array.Wires[w];
                if (!visible || wire.Points.Count < 2) continue;

                bool wholeSelected = selection?.Wires.Contains(flatIndex) == true;

                // ── Nothing may APPEAR because it was selected ──────────────────────────────────
                //
                // A bound member is collapsed onto the array's one editable curve (§6.2 idea 3) only
                // when it genuinely PROJECTS onto it — when drawing it would put a second polyline on
                // exactly the pixels the representative already covers. Anything that projects
                // somewhere else is drawn unconditionally.
                //
                // The rule used to be "hidden unless the selection touches it", with no geometric
                // test at all, which is the owner's report (2026-08-16): "when wires with different
                // geometry are in the same group, if I use marquee select in the profile view, some
                // previously invisible wires become visible… I don't like having wires appear to
                // disappear depending on wire selection." Those members were represented by nothing —
                // the band spans min to max and says nothing about where the members between them
                // are — so selecting one was the only way to discover it existed.
                //
                // The selection is still consulted, but ONLY for a member that coincides: drawing it
                // then adds no curve anywhere, it recolours a curve already on screen, which is what
                // makes a marquee over a same-shape array visibly do something. So presence is a
                // function of geometry and colour is a function of selection, which is the split the
                // report is really asking for.
                //
                // §6.2's clutter rule survives intact where it applies: under AUTO every member of a
                // same-shape array projects onto the same chord, so it is still one curve plus a
                // band. Under a fixed plane, members at different positions project to different
                // places, and there is no honest picture in which they are one curve.
                bool touched = wholeSelected || Touches(selection, flatIndex);
                if (w != representative
                    && envelope.BoundWires.Contains(w)
                    && representative >= 0
                    && !touched
                    && ProjectsOnto(wire, array.Wires[representative], mode, azimuthRadians)) continue;

                linePaint.Color = wholeSelected ? theme.Selected
                                : w == representative ? theme.Wire
                                : theme.FreeWire;

                for (int i = 1; i < wire.Points.Count; i++)
                {
                    var p0 = ProfileProjection.Project(wire, i - 1, mode, azimuthRadians);
                    var p1 = ProfileProjection.Project(wire, i, mode, azimuthRadians);

                    var previous = linePaint.Color;
                    if (SegmentSelected(selection, flatIndex, i - 1, wholeSelected))
                        linePaint.Color = theme.Selected;

                    canvas.DrawLine(spanToScreen(p0.Span), zToScreen(p0.Z),
                                    spanToScreen(p1.Span), zToScreen(p1.Z), linePaint);
                    linePaint.Color = previous;
                    segments++;
                }

                for (int i = 0; i < wire.Points.Count; i++)
                {
                    var p = ProfileProjection.Project(wire, i, mode, azimuthRadians);

                    // Per-POINT accent, which this view did not have at all: only whole wires were
                    // ever highlighted, so an enclose marquee — whose whole job is catching some of a
                    // wire's vertices — appeared to select nothing.
                    bool pointSelected = wholeSelected || PointSelected(selection, flatIndex, i);

                    dotPaint.Color = pointSelected ? theme.Selected
                                   : i == 0 ? theme.InputEnd
                                   : linePaint.Color;

                    canvas.DrawCircle(spanToScreen(p.Span), zToScreen(p.Z), theme.DotRadiusPx, dotPaint);
                    dots++;
                }

                wires++;
            }
        }

        return new Result(wires, segments, dots);
    }

    /// <summary>
    /// Whether the segment from point <paramref name="index"/> to <paramref name="index"/>+1 draws
    /// accented.
    ///
    /// <para>Selected in its own right, or with BOTH endpoints selected — which is what an enclose
    /// marquee over part of a loop produces. One definition, used by both views: the layout view had
    /// none at all, so a segment picked in the profile view lit up there and nowhere else.</para>
    /// </summary>
    private static bool SegmentSelected(WireSelection? selection, int wire, int index, bool wholeSelected)
    {
        if (wholeSelected) return true;
        if (selection is null) return false;

        return selection.Segments.Contains(new SegmentRef(wire, index))
            || (PointSelected(selection, wire, index) && PointSelected(selection, wire, index + 1));
    }

    /// <summary>
    /// Whether <paramref name="wire"/> lands on top of <paramref name="representative"/> in THIS
    /// projection — the test that decides whether the array's one editable curve genuinely stands
    /// for it (§6.2 idea 3) or merely hides it.
    ///
    /// <para>Compared in projected (span, z) rather than in world coordinates, because the projection
    /// is exactly what differs: two wires 5 mil apart in y are the same curve under AUTO (each on its
    /// own chord) and two separate curves in the YZ plane. A rule stated in world coordinates would
    /// get one of those two wrong whichever way it was written.</para>
    /// </summary>
    private static bool ProjectsOnto(
        Wire wire, Wire representative, ProfileProjection.SpanMode mode, double? azimuthRadians)
    {
        if (ReferenceEquals(wire, representative)) return true;
        if (wire.Points.Count != representative.Points.Count) return false;

        for (int i = 0; i < wire.Points.Count; i++)
        {
            var a = ProfileProjection.Project(wire, i, mode, azimuthRadians);
            var b = ProfileProjection.Project(representative, i, mode, azimuthRadians);

            if (Math.Abs(a.Span - b.Span) > CoincidenceToleranceNm) return false;
            if (Math.Abs(a.Z - b.Z) > CoincidenceToleranceNm) return false;
        }

        return true;
    }

    /// <summary>
    /// How close two projected curves have to be to count as one. 1 µm — four hundredths of a mil,
    /// far below anything that can be seen at any usable zoom, and far above the ~1 nm quantisation
    /// every wBond transform lands on (§6.4's measured note).
    /// </summary>
    private const double CoincidenceToleranceNm = 1_000.0;

    /// <summary>Whether a selection touches this wire at ALL — whole, by point, or by segment.</summary>
    private static bool Touches(WireSelection? selection, int wire)
    {
        if (selection is null) return false;
        if (selection.Wires.Contains(wire)) return true;

        foreach (var p in selection.Points) if (p.Wire == wire) return true;
        foreach (var s in selection.Segments) if (s.Wire == wire) return true;

        return false;
    }


    private static bool PointSelected(WireSelection? selection, int wire, int index)
    {
        if (selection is null) return false;
        if (selection.Points.Contains(new PointRef(wire, index))) return true;

        // A selected SEGMENT carries both its endpoints — the same rule WireSelection.MovingPoints
        // applies when it decides what a drag moves, so the highlight cannot disagree with the move.
        return selection.Segments.Contains(new SegmentRef(wire, index))
            || selection.Segments.Contains(new SegmentRef(wire, index - 1));
    }

    /// <summary>
    /// Draws the wire being created as a dashed ghost (§6.4).
    ///
    /// <para><b>The ghost is the full generated loop, not a rubber-band line</b> — that is what the
    /// design asks for and it is the point of the gesture: what you are placing is a wire with a real
    /// profile, and a straight line between the two feet would show none of the loop clearance the
    /// user is actually judging as they choose where to land it.</para>
    /// </summary>
    public static void DrawGhostWire(
        SKCanvas canvas, Wire wire, LayoutViewport viewport, WBondRenderTheme theme,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(theme);

        if (wire.Points.Count < 2) return;

        using var dash = SKPathEffect.CreateDash([5f, 4f], 0f);
        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = theme.LineWidthPx,
            Color = theme.Wire.WithAlpha(0xC0),
            PathEffect = dash,
        };
        using var dot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        float Sx(long nm) => (float)viewport.WorldToScreenX(NmToDbu(nm, dbuPerMicron));
        float Sy(long nm) => (float)viewport.WorldToScreenY(NmToDbu(nm, dbuPerMicron));

        for (int i = 1; i < wire.Points.Count; i++)
            canvas.DrawLine(Sx(wire.Points[i - 1].X), Sy(wire.Points[i - 1].Y),
                            Sx(wire.Points[i].X), Sy(wire.Points[i].Y), line);

        for (int i = 0; i < wire.Points.Count; i++)
        {
            // The input end keeps its own colour even in the ghost — which end starts the wire is
            // what fixes the sign of every mutual it will have (WB3), so it is worth seeing BEFORE
            // committing rather than after.
            dot.Color = (i == 0 ? theme.InputEnd : theme.Wire).WithAlpha(0xC0);
            canvas.DrawCircle(Sx(wire.Points[i].X), Sy(wire.Points[i].Y), theme.DotRadiusPx, dot);
        }
    }

    /// <summary>
    /// Draws the selection marquee, in nanometre world coordinates.
    ///
    /// <para><b>Solid means enclose, dashed means crossing</b> (§6.3) — and the cue is load-bearing
    /// rather than decorative here, because the two modes select genuinely different things: a
    /// right-to-left crossing marquee promotes to the WHOLE wire for any wire with a point in the box,
    /// so a user who cannot tell which mode they are in cannot predict what they are about to select.
    /// The direction comes from the hand (press versus release x), not from a mode the user set.</para>
    ///
    /// <para><b>Colour, alpha, hairline stroke and dash period are all transcribed from
    /// <c>LayoutRenderer.DrawMarquee</c>, deliberately.</b> This box is drawn over the layout editor's
    /// own canvas, often in the same session as the layout editor's own marquee — a second selection
    /// rectangle that is a different colour or a different dash reads as a different KIND of
    /// selection. <paramref name="accent"/> is the layout theme's own <c>Selection</c> colour, handed
    /// down from the same theme object the layout underneath was drawn with, so the two cannot drift.
    /// </para>
    /// </summary>
    /// <param name="accent">
    /// The selection accent to draw in. Null falls back to the wBond theme's own selected colour, for
    /// callers with no layout theme in hand.
    /// </param>
    public static void DrawMarquee(
        SKCanvas canvas, LayoutViewport viewport, WBondRenderTheme theme,
        long startXNm, long startYNm, long currentXNm, long currentYNm,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
        SKColor? accent = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(theme);

        float x0 = (float)viewport.WorldToScreenX(NmToDbu(startXNm, dbuPerMicron));
        float y0 = (float)viewport.WorldToScreenY(NmToDbu(startYNm, dbuPerMicron));
        float x1 = (float)viewport.WorldToScreenX(NmToDbu(currentXNm, dbuPerMicron));
        float y1 = (float)viewport.WorldToScreenY(NmToDbu(currentYNm, dbuPerMicron));

        var rect = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
        bool crossing = currentXNm < startXNm;
        var colour = accent ?? theme.Selected;

        using var fill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = colour.WithAlpha(50),
        };
        canvas.DrawRect(rect, fill);

        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0,
            Color = colour.WithAlpha(255),
            PathEffect = crossing ? SKPathEffect.CreateDash([6f, 4f], 0f) : null,
        };
        canvas.DrawRect(rect, stroke);
        stroke.PathEffect?.Dispose();
    }

    private static void DrawBand(
        SKCanvas canvas, WireArray array, ProfileEnvelope.ArrayProfile envelope,
        Func<double, float> spanToScreen, Func<double, float> zToScreen,
        ProfileProjection.SpanMode mode, SKPaint paint, double? azimuthRadians)
    {
        // The band is expressed as height ABOVE THE CHORD, so it has to be lifted back onto the
        // chord to be drawn — otherwise a wire whose feet are at different z draws its band flat.
        var reference = array.Wires[envelope.BoundWires[0]];
        var start = reference.Points[0];
        var end = reference.Points[^1];

        // The band's own span coordinate is NORMALISED (0..1 along the chord); drawing it against an
        // absolute axis means mapping it onto the reference wire's OWN projected extent — both its
        // origin and its length, taken from the projection rather than from the geometry. Reading the
        // plain chord length instead would leave the band at full width in a plane the wires are
        // foreshortened in, and dropping the origin would pin it at span 0 while the curves sit
        // wherever they actually are — either way the band and the curves disagree about where the
        // wire is.
        double originNm = 0.0, chordNm = 1.0;
        if (mode == ProfileProjection.SpanMode.Absolute)
        {
            var first = ProfileProjection.Project(reference, 0, mode, azimuthRadians);
            var last = ProfileProjection.Project(reference, reference.Points.Count - 1, mode, azimuthRadians);
            originNm = first.Span;
            chordNm = last.Span - first.Span;
        }

        using var path = new SKPath();

        for (int i = 0; i < envelope.Bands.Count; i++)
        {
            var band = envelope.Bands[i];
            double chordZ = start.Z + (end.Z - start.Z) * band.Span;
            float x = spanToScreen(originNm + band.Span * chordNm);
            float y = zToScreen(chordZ + band.MaxHeightNm);

            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }

        for (int i = envelope.Bands.Count - 1; i >= 0; i--)
        {
            var band = envelope.Bands[i];
            double chordZ = start.Z + (end.Z - start.Z) * band.Span;
            path.LineTo(spanToScreen(originNm + band.Span * chordNm), zToScreen(chordZ + band.MinHeightNm));
        }

        path.Close();
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Stroke width in pixels.
    ///
    /// <para>True-diameter mode has a <b>1 px floor</b>: without it a 1 mil wire vanishes entirely at
    /// whole-package zoom, which is exactly when a user is most likely to be looking for it.</para>
    /// </summary>
    internal static float StrokeWidth(Wire wire, LayoutViewport viewport, WBondRenderTheme theme,
                                      WireThicknessMode mode,
                                      int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        if (mode == WireThicknessMode.ConstantPixels) return theme.LineWidthPx;

        // Zoom is device pixels per DBU, so the diameter has to be in DBU too.
        return (float)Math.Max(NmToDbu(wire.DiameterNm, dbuPerMicron) * viewport.Zoom, 1.0);
    }

    /// <summary>
    /// Nanometres to the host layout's database units — the layout viewport's world unit.
    ///
    /// <para><b>This is not decoration and it is not free.</b> At the 1,000 DBU/µm default the two
    /// units coincide exactly, so an implementation that simply passed nanometres through would look
    /// perfect on every default layout and put every wire ten times out of place on a 100 DBU/µm one.
    /// The same bridge, for the same reason, is in <see cref="WBondSnap"/>.</para>
    /// </summary>
    private static double NmToDbu(double nm, int dbuPerMicron) =>
        dbuPerMicron <= 0 ? nm : nm * dbuPerMicron / 1000.0;

    /// <summary>
    /// Scales a colour's alpha. Applied to every colour rather than to a layer paint so the dimmed
    /// pass at depth (WB27) keeps the input-end colour distinguishable — knowing which end starts the
    /// wire matters just as much when the wires are a locked reference.
    /// </summary>
    private static SKColor Fade(SKColor color, byte opacity) =>
        opacity >= 255 ? color : color.WithAlpha((byte)(color.Alpha * opacity / 255));
}
