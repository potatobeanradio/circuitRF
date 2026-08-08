using System;
using System.Collections.Generic;
using CircuitRF.Ui.Layout;
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

    /// <summary>A sensible default so a canvas can render without a theme wired up yet.</summary>
    public static WBondRenderTheme Fallback => new()
    {
        Wire = new SKColor(0xE0, 0xC0, 0x60),
        InputEnd = new SKColor(0x60, 0xC0, 0xE0),
        Selected = new SKColor(0xFF, 0xFF, 0xFF),
        Envelope = new SKColor(0xE0, 0xC0, 0x60, 0x40),
        FreeWire = new SKColor(0xE0, 0x80, 0x60),
    };
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

            for (int i = 1; i < wire.Points.Count; i++)
            {
                var (ax, ay) = Screen(wire.Points[i - 1]);
                var (bx, by) = Screen(wire.Points[i]);

                canvas.DrawLine(ax, ay, bx, by, linePaint);
                segments++;
            }

            for (int i = 0; i < wire.Points.Count; i++)
            {
                var (px, py) = Screen(wire.Points[i]);

                // The INPUT end gets its own colour. Rendering, but not decoration: the sign of every
                // mutual inductance depends on which end this is (WB3).
                bool pointSelected = wholeSelected || selection?.Points.Contains(new PointRef(index, i)) == true;
                dotPaint.Color = i == 0 ? Fade(theme.InputEnd, opacity)
                               : pointSelected ? Fade(theme.Selected, opacity)
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
    public static Result DrawProfile(
        SKCanvas canvas, WBondDesign design, WBondRenderTheme theme,
        Func<double, float> spanToScreen, Func<double, float> zToScreen,
        ProfileProjection.SpanMode mode = ProfileProjection.SpanMode.Absolute,
        WireSelection? selection = null,
        IReadOnlyList<int>? visibleArrays = null)
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
                DrawBand(canvas, array, envelope, spanToScreen, zToScreen, mode, bandPaint);

            for (int w = 0; w < array.Wires.Count; w++)
            {
                flatIndex++;
                var wire = array.Wires[w];
                if (!visible || wire.Points.Count < 2) continue;

                // BOUND members are represented by the band; only the free ones are drawn
                // individually. That is the clutter answer — one curve plus a band, not 200 curves.
                if (envelope.BoundWires.Contains(w)) continue;

                bool selected = selection?.Wires.Contains(flatIndex) == true;
                linePaint.Color = selected ? theme.Selected : theme.FreeWire;

                for (int i = 1; i < wire.Points.Count; i++)
                {
                    var p0 = ProfileProjection.Project(wire, i - 1, mode);
                    var p1 = ProfileProjection.Project(wire, i, mode);

                    canvas.DrawLine(spanToScreen(p0.Span), zToScreen(p0.Z),
                                    spanToScreen(p1.Span), zToScreen(p1.Z), linePaint);
                    segments++;
                }

                for (int i = 0; i < wire.Points.Count; i++)
                {
                    var p = ProfileProjection.Project(wire, i, mode);
                    dotPaint.Color = i == 0 ? theme.InputEnd : linePaint.Color;
                    canvas.DrawCircle(spanToScreen(p.Span), zToScreen(p.Z), theme.DotRadiusPx, dotPaint);
                    dots++;
                }

                wires++;
            }
        }

        return new Result(wires, segments, dots);
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
    /// </summary>
    public static void DrawMarquee(
        SKCanvas canvas, LayoutViewport viewport, WBondRenderTheme theme,
        long startXNm, long startYNm, long currentXNm, long currentYNm,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(theme);

        float x0 = (float)viewport.WorldToScreenX(NmToDbu(startXNm, dbuPerMicron));
        float y0 = (float)viewport.WorldToScreenY(NmToDbu(startYNm, dbuPerMicron));
        float x1 = (float)viewport.WorldToScreenX(NmToDbu(currentXNm, dbuPerMicron));
        float y1 = (float)viewport.WorldToScreenY(NmToDbu(currentYNm, dbuPerMicron));

        var rect = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
        bool crossing = currentXNm < startXNm;

        using var fill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = theme.Selected.WithAlpha(0x22),
        };
        canvas.DrawRect(rect, fill);

        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f,
            Color = theme.Selected,
            PathEffect = crossing ? SKPathEffect.CreateDash([4f, 4f], 0f) : null,
        };
        canvas.DrawRect(rect, stroke);
        stroke.PathEffect?.Dispose();
    }

    private static void DrawBand(
        SKCanvas canvas, WireArray array, ProfileEnvelope.ArrayProfile envelope,
        Func<double, float> spanToScreen, Func<double, float> zToScreen,
        ProfileProjection.SpanMode mode, SKPaint paint)
    {
        // The band is expressed as height ABOVE THE CHORD, so it has to be lifted back onto the
        // chord to be drawn — otherwise a wire whose feet are at different z draws its band flat.
        var reference = array.Wires[envelope.BoundWires[0]];
        var start = reference.Points[0];
        var end = reference.Points[^1];

        double chordNm = 1.0;
        if (mode == ProfileProjection.SpanMode.Absolute)
        {
            double dx = WBondUnits.ToMetres(end.X - start.X);
            double dy = WBondUnits.ToMetres(end.Y - start.Y);
            chordNm = Math.Sqrt(dx * dx + dy * dy) * WBondUnits.NmPerMetre;
        }

        using var path = new SKPath();

        for (int i = 0; i < envelope.Bands.Count; i++)
        {
            var band = envelope.Bands[i];
            double chordZ = start.Z + (end.Z - start.Z) * band.Span;
            float x = spanToScreen(band.Span * chordNm);
            float y = zToScreen(chordZ + band.MaxHeightNm);

            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }

        for (int i = envelope.Bands.Count - 1; i >= 0; i--)
        {
            var band = envelope.Bands[i];
            double chordZ = start.Z + (end.Z - start.Z) * band.Span;
            path.LineTo(spanToScreen(band.Span * chordNm), zToScreen(chordZ + band.MinHeightNm));
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
