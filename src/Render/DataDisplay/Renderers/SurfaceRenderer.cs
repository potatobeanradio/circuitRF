// ================================================================
//  SurfaceRenderer.cs  —  ANT-10: the 3D pattern surface, in Skia,
//  below the firewall
//
//  §1: "triangulate the (θ, φ) grid ANT-4 already produces, transform,
//  sort by depth, fill." The transform and the sort are PatternMesh's;
//  everything here is the canvas — the layout, the ground disc, the
//  scene axes, the colour bar and the sentences.
//
//  IT DRAWS IN src/Render FOR ONE REASON: `Cli render` and `Cli plot`
//  hand the same Plot to the same composer, so the headless picture is
//  this code's picture rather than a second renderer's that would drift
//  invisibly (RND-1). That is also why nothing in this file reads a
//  control, a view model or a theme resource — it takes a Plot, a canvas
//  size and a RenderTheme, exactly as TableRenderer does.
//
//  NO LIGHTING AND NO PERSPECTIVE (§3/§7). Colour carries the value and
//  nothing else; a shaded surface would encode the same number twice, in
//  two scales, one of which has no legend.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public static class SurfaceRenderer
{
    /// <summary>Half-extent of the scene in world units: the ground disc is radius 1 and the axes
    /// reach past it, so the box has to hold the arrows and their letters.</summary>
    private const double SceneHalfExtent = 1.28;

    /// <summary>How far the scene axes reach, in world units.</summary>
    private const double AxisLength = 1.16;

    /// <summary>
    /// <b>The smallest the legend's numbers are allowed to get, in pixels.</b> Everything else on
    /// this canvas stops being drawn below <c>baseSize &lt; 4</c> — a rule about when TEXT is worth
    /// the ink — and the colour bar was gated on it too, so zooming the Data Display out far enough
    /// silently removed the one thing that says whether a colour is the peak or the floor (owner,
    /// 2026-09-11). The bar is drawn at every size now, and its numbers stop shrinking here rather
    /// than vanishing: small is legible once the user zooms back in, absent is a picture whose
    /// meaning changed.
    /// </summary>
    private const float MinLegendTextPx = 4f;

    /// <summary>The facet budget an INTERACTING frame decimates to. §6: draw the full grid on
    /// release and a decimated one while the pointer is down, and say so. The endpoints of both
    /// angle axes are kept whatever the stride, so the silhouette and the peak direction do not
    /// move between the two.</summary>
    private const int InteractiveFacetBudget = 6000;

    /// <summary>
    /// What one frame actually drew. Returned so a test can assert the GEOMETRY §6 asks about — the
    /// peak direction, the nulls, the symmetry, the depth order — without reading pixels, and so the
    /// frame cost can be measured without a timing test in the suite.
    /// </summary>
    public sealed record Frame(
        IReadOnlyList<PatternFacet> Facets,
        SKPoint                     Origin,
        float                       WorldToCanvas,
        int                         Stride,
        PolarPatternScale?          Scale)
    {
        /// <summary>A scene point in canvas pixels — the mapping the whole frame was drawn with.</summary>
        public SKPoint ToCanvas(double sceneX, double sceneY) =>
            new((float)(Origin.X + sceneX * WorldToCanvas),
                (float)(Origin.Y - sceneY * WorldToCanvas));
    }

    /// <summary>
    /// <b>Where the scene, the caption block and the colour bar are</b> — the arithmetic
    /// <see cref="Draw"/> lays a frame out with, in one place so a HIT TEST can ask the same
    /// question the drawing answered. Two copies of it would be a pointer that rotates a pattern it
    /// is not over, which is invisible until someone drags.
    ///
    /// <para>Top: the title. Bottom: the caption lines. Right: the colour bar with its numbers. The
    /// square that is left is the scene. Laid out from the canvas directly, as <c>TableRenderer</c>
    /// lays out its columns — a Surface3D plot has no world window for a viewport to be a fraction
    /// of (<c>Plot.SetAxesViewport</c> gives it the whole canvas and says why).</para>
    /// </summary>
    private readonly record struct SceneLayout(
        float TitleH, float CapH, float BarW, float LegendTextPx,
        float Left, float Top, float Right, float Bottom,
        float CentreX, float CentreY, float Side, float WorldToCanvas);

    private static SceneLayout LayoutOf((double W, double H) canvasSize, Plot plot,
                                        int captionCount, PolarPatternScale? scale, string title)
    {
        float w  = (float)canvasSize.W;
        float h  = (float)canvasSize.H;
        float lw = AxesRenderer.LineWidth(canvasSize);
        float baseSize = (float)(plot.Axes.FontSizeLabel * lw);

        float titleH = string.IsNullOrEmpty(title) ? 0f : (float)(plot.Axes.FontSizeLabel * 1.4 * lw) * 1.6f;
        float capH   = baseSize >= 4f && captionCount > 0
            ? captionCount * baseSize * 1.25f + 2f * lw : 0f;
        float legendSize = Math.Max(baseSize, MinLegendTextPx);
        float barW   = (scale is null || !plot.SurfaceShowLegend)
            ? 0f : ColourBarTotalWidth(scale, legendSize, lw);

        float left   = 2f * lw;
        float top    = titleH + 2f * lw;
        float right  = Math.Max(left + 1f, w - barW - 2f * lw);
        float bottom = Math.Max(top  + 1f, h - capH - 2f * lw);

        float side = Math.Min(right - left, bottom - top);
        return new SceneLayout(titleH, capH, barW, legendSize, left, top, right, bottom,
                               (left + right) / 2f, (top + bottom) / 2f, side,
                               (float)(side / 2.0 / SceneHalfExtent));
    }

    /// <summary>
    /// What goes under the scene — <b>nothing, since 2026-09-11</b>.
    ///
    /// <para>It held two things and both have left. <see cref="PatternCaption"/> stopped writing
    /// sentences under a pattern (see its own note), and the trace IDENTITY — which is here because
    /// a surface plot has no <c>PlotLabelStrips</c>, those being <c>IsComplex()</c>-only — is now the
    /// plot's TITLE instead, which is what was asked for on 2026-09-11: the caption removed, its text
    /// becoming the plot's DEFAULT title, and the user's own axes title overriding it. Which is the
    /// better place for it: a title is where a reader looks for what a picture is OF, and the one
    /// thing that made the
    /// bottom the obvious spot — the scene's z axis is drawn at the top and a label there collides
    /// with the letter — does not apply to the title block, which is above the scene rather than
    /// over it.</para>
    ///
    /// <para>The method stays because the layout still has a block for it, and because
    /// <see cref="HitsPattern"/> and <see cref="Draw"/> have to agree about its height whatever it
    /// holds.</para>
    /// </summary>
    private static List<string> Captions(Plot plot, Func<Trace, string?>? aliasFor,
                                         bool? alwaysShowSource)
    {
        _ = aliasFor; _ = alwaysShowSource;
        return [.. PatternCaption.Lines(plot)];
    }

    /// <summary>
    /// <b>A 3D pattern's title: the user's own, or the trace's identity when they have not set
    /// one.</b> The identity comes through the SAME <c>TraceLabeler</c> a label strip uses, so the
    /// surface and a cut beside it cannot spell one trace two ways.
    ///
    /// <para><c>CustomTitleOn</c> is what decides, not whether the text is empty — so a user who
    /// sets an EMPTY title gets no title, which is the only way to turn the line off and would be
    /// unreachable if a blank fell back to the default.</para>
    /// </summary>
    private static string TitleOf(Plot plot, Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        => plot.CustomTitleOn
            ? plot.Title
            : TraceIdentity(plot, aliasFor, alwaysShowSource) ?? "";

    /// <summary>
    /// <b>Whether a point on the canvas is ON the pattern rather than on the furniture around it</b>
    /// — the title, the caption line under the scene, or the colour bar down the right.
    ///
    /// <para>Reported 2026-09-11: a 3D pattern plot could not be dragged anywhere in the Data
    /// Display because every press rotated it, and the ask was to rotate only for a press near the
    /// pattern itself. A press anywhere on a Surface3D plot started a rotate and marked itself
    /// handled, so the press never reached the container that moves the plot and there was no way
    /// to pick the plot up at all. The scene square is the rotate zone and everything outside it is
    /// an ordinary press.</para>
    ///
    /// <para><b>False when the plot resolved no surface</b>, which the same report asked for
    /// explicitly: with no pattern drawn, every part of the plot is furniture and the whole of it
    /// drags. Still false now that an EMPTY plot draws its ground disc and axes — those are the
    /// frame of reference for a pattern that is not there yet, and a plot a user has just placed is
    /// a plot they are about to move.</para>
    ///
    /// <para>The square rather than the lobe silhouette, deliberately. The ground disc and the
    /// scene axes are the surface's own frame of reference and turning the object by them is what a
    /// reader expects; and a pattern with a deep null has canvas inside its own outline that
    /// belongs to it, which a silhouette test would hand to the drag.</para>
    /// </summary>
    public static bool HitsPattern((double W, double H) canvasSize, Plot plot, double x, double y,
                                   Func<Trace, string?>? aliasFor = null,
                                   bool? alwaysShowSource = null)
    {
        if (plot is null) return false;
        var (grid, _) = FirstSurface(plot);
        if (grid is null) return false;

        var lay = LayoutOf(canvasSize, plot, Captions(plot, aliasFor, alwaysShowSource).Count,
                           plot.PatternScale, TitleOf(plot, aliasFor, alwaysShowSource));
        float half = lay.Side / 2f;
        return x >= lay.CentreX - half && x <= lay.CentreX + half
            && y >= lay.CentreY - half && y <= lay.CentreY + half;
    }

    // ================================================================
    //  Draw
    // ================================================================

    public static Frame Draw(SKCanvas canvas, (double W, double H) canvasSize, Plot plot,
                             PlotDetail detail, RenderTheme theme,
                             Func<Trace, string?>? aliasFor = null,
                             bool? alwaysShowSource = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(plot);

        float w  = (float)canvasSize.W;
        float h  = (float)canvasSize.H;
        float lw = AxesRenderer.LineWidth(canvasSize);
        var   cam   = plot.SurfaceCamera;
        var   scale = plot.PatternScale;

        using var text = new SKPaint { Color = theme.TextColor, IsAntialias = true };

        float baseSize = (float)(plot.Axes.FontSizeLabel * lw);

        // What goes under the scene — see Captions, which the hit test reads too — and the title,
        // which is the trace's own identity unless the user has set one (TitleOf).
        var captions = Captions(plot, aliasFor, alwaysShowSource);
        string title = TitleOf(plot, aliasFor, alwaysShowSource);

        // ---- Layout ---------------------------------------------------
        var lay = LayoutOf(canvasSize, plot, captions.Count, scale, title);
        float titleH = lay.TitleH, capH = lay.CapH, barW = lay.BarW;
        float legendSize = lay.LegendTextPx;
        float sceneL = lay.Left, sceneT = lay.Top, sceneR = lay.Right, sceneB = lay.Bottom;
        float cx = lay.CentreX, cy = lay.CentreY, s = lay.WorldToCanvas;
        var   origin = new SKPoint(cx, cy);

        SKPoint P(double sx, double sy) => new((float)(cx + sx * s), (float)(cy - sy * s));

        // ---- Title ----------------------------------------------------
        if (titleH > 0f)
        {
            float ts = (float)(plot.Axes.FontSizeLabel * 1.4 * lw);
            using var tFont = new SKFont(plot.CustomTitleBold ? SkiaFonts.PlexBold : SkiaFonts.PlexRegular, ts);
            using var tFall = new SKFont(plot.CustomTitleBold ? SkiaFonts.DejaVuBold : SkiaFonts.DejaVuRegular, ts);
            float avail = w - 4f * lw;
            float meas  = RendererText.MeasureTextWithFallback(title, tFont, tFall);
            if (meas > avail && meas > 0f)
            {
                tFont.Size = Math.Max(ts * (avail / meas), ts * 0.5f);
                tFall.Size = tFont.Size;
                meas = RendererText.MeasureTextWithFallback(title, tFont, tFall);
            }
            RendererText.DrawLeftTextWithFallback(canvas, title, (w - meas) / 2f,
                                                  titleH * 0.62f, tFont, tFall, text);
        }

        // ---- The surface ----------------------------------------------
        var (grid, trace) = FirstSurface(plot);
        int stride = 1;
        PatternFacet[] facets = [];

        if (grid is not null && scale is not null)
        {
            stride = StrideFor(grid, detail);
            facets = MeshCache.Build(grid, scale, cam, stride);
        }

        bool fromAbove = cam.ElevationDeg >= 0;

        // ---- Whether the scene's own furniture is drawn ------------------------------------
        //
        //  The disc and the axes are drawn whenever there is nothing to READ in their place. An
        //  EMPTY 3D plot is the case that matters (owner, 2026-09-11): a plot dropped on the Data
        //  Display with no trace on it yet used to be a blank rectangle, so the three checkboxes
        //  that were already on — Axes, Ground, Legend — showed nothing until a trace resolved, and
        //  the plot itself was invisible to someone placing it. It draws its empty scene now, which
        //  is also what says WHICH WAY the camera is pointing before any data arrives.
        //
        //  The one case that still stands them down is a trace that REFUSED: its sentence is drawn
        //  in the middle of the scene, and a sentence that must be read ends up struck through by an
        //  axis arm. A refusal is text in the scene's place; an empty plot is the scene.
        bool refused = FirstRefusal(plot) is { Length: > 0 };
        bool scene   = grid is not null || !refused;

        if (scene && plot.SurfaceShowGroundDisc && fromAbove) DrawGroundDisc(canvas, cam, P, s, lw, theme);
        if (scene && plot.SurfaceShowAxes)                     DrawSceneAxes  (canvas, cam, P, lw, theme);

        DrawFacets(canvas, facets, P, scale, plot.SurfaceColorMap);

        if (scene && plot.SurfaceShowGroundDisc && !fromAbove) DrawGroundDisc(canvas, cam, P, s, lw, theme);

        // The letters go on last, whatever they sit over. An axis arm disappearing behind a lobe is
        // correct and readable; a LETTER half behind one is neither.
        if (scene && plot.SurfaceShowAxes && baseSize >= 4f)
            DrawAxisLabels(canvas, cam, P, baseSize, theme);

        // ---- Colour bar, trace labels, captions -----------------------
        // Drawn whenever the plot asks for one, at every canvas size — see MinLegendTextPx. It is
        // OUTSIDE the `baseSize >= 4f` block below for exactly that reason.
        if (barW > 0f && scale is not null)
            DrawColourBar(canvas, scale, plot.SurfaceColorMap,
                          new SKRect(w - barW, sceneT, w - 2f * lw, sceneB), legendSize, lw, theme);

        if (baseSize >= 4f)
        {
            DrawCaptions(canvas, captions, w, h - capH, baseSize, lw, theme);

            // A trace that resolved no grid says WHY, in the middle of the empty scene. R-rnd4-4's
            // rule: an empty picture that exports cleanly is indistinguishable from a measurement
            // that came back empty, so the refusal is drawn where the surface would have been.
            if (grid is null && FirstRefusal(plot) is { Length: > 0 } refusal)
                DrawWrapped(canvas, refusal, sceneL + 4f * lw, cy - baseSize, baseSize, theme,
                            maxWidth: sceneR - sceneL - 8f * lw);
        }

        return new Frame(facets, origin, s, stride, scale);
    }

    // ================================================================
    //  Pieces
    // ================================================================

    /// <summary>
    /// The first trace on the plot that actually resolved a grid, and that trace.
    ///
    /// <para><b>One surface per plot, on purpose.</b> Two opaque surfaces in one scene occlude each
    /// other and neither can be read; the comparison a user wants between two patterns is two cuts
    /// on one polar plot, which ANT-7 already draws. A second surface trace is left resolved and
    /// undrawn rather than refused, so switching the plot back to Polar restores both curves.</para>
    /// </summary>
    private static (PatternSurfaceGrid? Grid, Trace? Trace) FirstSurface(Plot plot)
    {
        foreach (var t in plot.Traces)
            if (t.SurfaceGrid is { ThetaCount: > 1, PhiCount: > 1 } g) return (g, t);
        return (null, null);
    }

    /// <summary>
    /// §6's decimation. <b>Derived from the grid's own size, not from the detail level alone</b> —
    /// a 30° × 45° grid is 8 facets and decimating it would turn a coarse pattern into a triangle,
    /// so the stride only ever rises on a grid that is over the budget.
    /// </summary>
    internal static int StrideFor(PatternSurfaceGrid grid, PlotDetail detail)
    {
        if (detail == PlotDetail.Full) return 1;
        long cells = (long)Math.Max(1, grid.ThetaCount - 1) * Math.Max(1, grid.PhiCount);
        long facets = cells * 2;
        if (facets <= InteractiveFacetBudget) return 1;
        return (int)Math.Max(1, Math.Ceiling(Math.Sqrt((double)facets / InteractiveFacetBudget)));
    }

    /// <summary>
    /// The surface itself. <b>Two ways to put the same triangles on the canvas, chosen by where the
    /// frame is going</b> — see <see cref="PlotDocumentScope"/> for the measurements and for why the
    /// choice is not a parameter.
    /// </summary>
    private static void DrawFacets(SKCanvas canvas, IReadOnlyList<PatternFacet> facets,
                                   Func<double, double, SKPoint> P,
                                   PolarPatternScale? scale, ContourColorMap map)
    {
        if (facets.Count == 0 || scale is null) return;
        double span = scale.SpanDb;
        if (!(span > 0)) return;

        if (PlotDocumentScope.IsActive) DrawFacetsAsPaths(canvas, facets, P, scale, span, map);
        else                            DrawFacetsAsMesh (canvas, facets, P, scale, span, map);
    }

    /// <summary>
    /// <b>A document's surface: one antialiased filled path per triangle.</b> Exact, resolution
    /// independent, and the only form a vector device records — an SVG or a PDF built from the mesh
    /// below comes back with an empty page.
    /// </summary>
    private static void DrawFacetsAsPaths(SKCanvas canvas, IReadOnlyList<PatternFacet> facets,
                                          Func<double, double, SKPoint> P,
                                          PolarPatternScale scale, double span, ContourColorMap map)
    {
        // One paint, recoloured per facet. A fresh SKPaint per triangle is 32,000 allocations a
        // frame at the 1° × 1° grid the brief sizes this on.
        using var fill = new SKPaint { Style = SKPaintStyle.StrokeAndFill, IsAntialias = true };
        using var path = new SKPath();

        foreach (var f in facets)
        {
            double t = (f.Db - scale.FloorDb) / span;
            fill.Color = ContourColormaps.Sample(map, t);
            // A HAIRLINE STROKE IN THE FILL'S OWN COLOUR, and it is not decoration: two antialiased
            // triangles sharing an edge each cover about half of the boundary pixel, so the
            // background shows through between them as a lighter seam over the whole surface. The
            // stroke closes it without drawing a wireframe on top of the data.
            fill.StrokeWidth = 0.6f;

            path.Reset();
            path.MoveTo(P(f.X0, f.Y0));
            path.LineTo(P(f.X1, f.Y1));
            path.LineTo(P(f.X2, f.Y2));
            path.Close();
            canvas.DrawPath(path, fill);
        }
    }

    /// <summary>
    /// <b>A live frame's surface: the whole mesh in ONE <c>DrawVertices</c> call.</b> Same triangles,
    /// same depth order, same colours — three vertices per facet all carrying that facet's colour, so
    /// the shading stays flat per triangle rather than becoming a Gouraud blend across the surface.
    ///
    /// <para><b>Antialiasing is off and the hairline stroke is gone with it.</b> Neither is a loss
    /// here: the seam the stroke existed to close is an ANTIALIASING artefact — two AA triangles each
    /// covering half of a shared boundary pixel — and a non-AA mesh tiles its shared edges exactly,
    /// with no background showing through anywhere. What it costs is a slightly harder silhouette,
    /// which is the trade a frame makes and a document does not.</para>
    ///
    /// <para><b>The paint is WHITE under <see cref="SKBlendMode.Modulate"/>, and it has to be.</b>
    /// <c>DrawVertices</c> combines each vertex colour with the paint's shader through that mode; with
    /// no shader the paint's own colour stands in, so the default black paint multiplies the whole
    /// surface to black — which is what it draws, silently, if this is left off.</para>
    /// </summary>
    private static void DrawFacetsAsMesh(SKCanvas canvas, IReadOnlyList<PatternFacet> facets,
                                         Func<double, double, SKPoint> P,
                                         PolarPatternScale scale, double span, ContourColorMap map)
    {
        int n = facets.Count;
        var pts  = MeshCache.RentPoints(n * 3);
        var cols = MeshCache.RentColors(n * 3);

        for (int i = 0; i < n; i++)
        {
            var f = facets[i];
            var c = ContourColormaps.Sample(map, (f.Db - scale.FloorDb) / span);
            int k = i * 3;
            pts[k]     = P(f.X0, f.Y0);
            pts[k + 1] = P(f.X1, f.Y1);
            pts[k + 2] = P(f.X2, f.Y2);
            cols[k] = cols[k + 1] = cols[k + 2] = c;
        }

        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, pts, cols);
        using var paint    = new SKPaint { Color = SKColors.White, IsAntialias = false };
        canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
    }

    /// <summary>
    /// <b>The last mesh this thread built, and the scratch arrays it drew with.</b>
    ///
    /// <para>A pan or a zoom of the Data Display redraws every plot on it without changing anything
    /// about the pattern — same grid, same scale, same camera — and re-projecting and re-sorting
    /// 64,000 facets to get the identical answer is 3.8 ms of every one of those frames. The key is
    /// everything <see cref="PatternMesh.Build"/> reads: the grid by reference (it is rebuilt only
    /// when the trace resolves), the scale by value (it is a record), the camera and the stride.</para>
    ///
    /// <para><b>Thread-local, and one entry.</b> The screen draws on the compositor thread and an
    /// export draws on its own, so a shared slot would be two threads evicting each other's mesh on
    /// every frame — and a lock around it would put the export's 50 ms path inside the frame's
    /// budget. One entry is enough because a plot draws one surface (<c>FirstSurface</c>).</para>
    /// </summary>
    private static class MeshCache
    {
        [ThreadStatic] private static PatternSurfaceGrid? _grid;
        [ThreadStatic] private static PolarPatternScale?  _scale;
        [ThreadStatic] private static PatternCamera       _camera;
        [ThreadStatic] private static int                 _stride;
        [ThreadStatic] private static PatternFacet[]?     _facets;

        [ThreadStatic] private static SKPoint[]? _points;
        [ThreadStatic] private static SKColor[]? _colors;

        public static PatternFacet[] Build(PatternSurfaceGrid grid, PolarPatternScale scale,
                                           PatternCamera camera, int stride)
        {
            if (_facets is { } hit && ReferenceEquals(_grid, grid) && _stride == stride
                && _camera.Equals(camera) && _scale == scale)
                return hit;

            var built = PatternMesh.Build(grid, scale, camera, stride);
            _grid = grid; _scale = scale; _camera = camera; _stride = stride; _facets = built;
            return built;
        }

        /// <summary>
        /// Scratch for one frame's vertices, kept between frames — 64,000 facets is 2.3 MB of
        /// SKPoint and SKColor per frame, which at 60 fps is a GC's worth of garbage for arrays that
        /// are overwritten in full every time.
        ///
        /// <para><b>Reused only at the EXACT length.</b> <c>SKVertices.CreateCopy</c> takes the whole
        /// array, not a count, so an over-long buffer left from a bigger mesh would draw its stale
        /// tail as triangles. The length is a function of the facet count, which does not change
        /// while a gesture is in flight, so the exact-match rule still hits on every frame that
        /// matters.</para>
        /// </summary>
        public static SKPoint[] RentPoints(int n)
            => _points is { } p && p.Length == n ? p : _points = new SKPoint[n];

        public static SKColor[] RentColors(int n)
            => _colors is { } c && c.Length == n ? c : _colors = new SKColor[n];
    }

    /// <summary>
    /// <b>§4's visual half.</b> The ground plane at θ = 90°, as a disc — simultaneously the horizon
    /// and the statement that nothing is modelled below it. Translucent, so the scene axes lying in
    /// its own plane stay visible through it.
    /// </summary>
    private static void DrawGroundDisc(SKCanvas canvas, PatternCamera cam,
                                       Func<double, double, SKPoint> P, float s, float lw,
                                       RenderTheme theme)
    {
        using var path = new SKPath();
        const int N = 180;
        for (int i = 0; i <= N; i++)
        {
            double a = i * 2 * Math.PI / N;
            var (x, y, _) = cam.Project(Math.Cos(a), Math.Sin(a), 0);
            var p = P(x, y);
            if (i == 0) path.MoveTo(p); else path.LineTo(p);
        }
        path.Close();

        using var fill = new SKPaint
        {
            Style       = SKPaintStyle.Fill,
            IsAntialias = true,
            Color       = RenderTheme.WithOpacity(theme.GridColor, theme.DarkMode ? 0.22 : 0.16),
        };
        using var ring = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeWidth = lw * 0.9f,
            Color       = RenderTheme.WithOpacity(theme.GridColor, 0.85),
        };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, ring);
    }

    /// <summary>
    /// x, y and z on the layout's own orientation, so the lobe can be related to the artwork — "a
    /// pattern with no axes is a shape" (§3). Drawn BEFORE the surface, so an arm that runs behind a
    /// lobe is hidden by it, which is the depth cue the picture is otherwise short of.
    /// </summary>
    private static void DrawSceneAxes(SKCanvas canvas, PatternCamera cam,
                                      Func<double, double, SKPoint> P, float lw,
                                      RenderTheme theme)
    {
        using var paint = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeWidth = lw * 0.9f,
            Color       = RenderTheme.WithOpacity(theme.TickColor, 0.75),
        };
        var o = P(0, 0);
        foreach (var (dx, dy, dz) in AxisDirections)
        {
            var (x, y, _) = cam.Project(dx * AxisLength, dy * AxisLength, dz * AxisLength);
            canvas.DrawLine(o, P(x, y), paint);
        }
    }

    private static void DrawAxisLabels(SKCanvas canvas, PatternCamera cam,
                                       Func<double, double, SKPoint> P, float fontSize,
                                       RenderTheme theme)
    {
        using var font  = new SKFont(SkiaFonts.PlexBold, fontSize);
        using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
        for (int i = 0; i < AxisDirections.Length; i++)
        {
            var (dx, dy, dz) = AxisDirections[i];
            var (x, y, _) = cam.Project(dx * (AxisLength + 0.09), dy * (AxisLength + 0.09), dz * (AxisLength + 0.09));
            var p = P(x, y);
            string name = AxisNames[i];
            float tw = font.MeasureText(name);
            canvas.DrawText(name, p.X - tw / 2f, p.Y + fontSize * 0.35f, SKTextAlign.Left, font, paint);
        }
    }

    private static readonly (double X, double Y, double Z)[] AxisDirections =
        [(1, 0, 0), (0, 1, 0), (0, 0, 1)];

    private static readonly string[] AxisNames = ["x", "y", "z"];

    // ---- The colour bar --------------------------------------------

    private static float ColourBarTotalWidth(PolarPatternScale scale, float fontSize, float lw)
    {
        using var font = new SKFont(SkiaFonts.PlexRegular, fontSize);
        float widest = font.MeasureText(scale.RingLabel(scale.ReferenceDb, withUnit: true));
        foreach (double r in scale.RingsDb)
            widest = Math.Max(widest, font.MeasureText(scale.RingLabel(r, withUnit: false)));
        widest = Math.Max(widest, font.MeasureText(scale.RingLabel(scale.FloorDb, withUnit: false)));
        return fontSize * 1.1f + widest + 5f * lw;
    }

    /// <summary>
    /// <b>§5 — the scale shown with its floor and its reference.</b> The ramp runs from the floor at
    /// the bottom to the reference at the top, ticked on ANT-7's own ring lattice and labelled in
    /// ANT-7's own words, so the surface and the polar cut beside it read on one scale in one
    /// spelling.
    /// </summary>
    private static void DrawColourBar(SKCanvas canvas, PolarPatternScale scale, ContourColorMap map,
                                      SKRect box, float fontSize, float lw, RenderTheme theme)
    {
        float barW = fontSize * 1.1f;
        float top = box.Top + fontSize, bottom = box.Bottom - fontSize;
        if (bottom - top < 4f) return;
        var bar = new SKRect(box.Left, top, box.Left + barW, bottom);

        // Painted as horizontal bands rather than an SKShader gradient: a 13-stop piecewise-linear
        // ramp and a Skia gradient interpolate in different spaces, and a legend whose colours are
        // not the SURFACE's colours is a legend that lies by a hair at every step.
        int bands = Math.Max(2, (int)Math.Ceiling(bar.Height));
        using var band = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        for (int i = 0; i < bands; i++)
        {
            double t = 1.0 - (i + 0.5) / bands;      // top of the bar is the reference
            band.Color = ContourColormaps.Sample(map, t);
            float y0 = bar.Top + bar.Height * i / bands;
            float y1 = bar.Top + bar.Height * (i + 1) / bands;
            canvas.DrawRect(new SKRect(bar.Left, y0, bar.Right, y1 + 0.5f), band);
        }

        using var edge = new SKPaint
        {
            Style = SKPaintStyle.Stroke, IsAntialias = true,
            StrokeWidth = lw * 0.7f, Color = theme.BorderColor,
        };
        canvas.DrawRect(bar, edge);

        using var font  = new SKFont(SkiaFonts.PlexRegular, fontSize);
        using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
        using var tick  = new SKPaint
        {
            Style = SKPaintStyle.Stroke, IsAntialias = true,
            StrokeWidth = lw * 0.7f, Color = RenderTheme.WithOpacity(theme.TickColor, 0.8),
        };

        float span = (float)scale.SpanDb;
        void Label(double db, bool unit)
        {
            float f = span > 0 ? (float)((scale.ReferenceDb - db) / span) : 0f;
            float y = bar.Top + bar.Height * Math.Clamp(f, 0f, 1f);
            canvas.DrawLine(bar.Right, y, bar.Right + 2f * lw, y, tick);
            canvas.DrawText(scale.RingLabel(db, unit), bar.Right + 3.5f * lw,
                            y + fontSize * 0.35f, SKTextAlign.Left, font, paint);
        }

        // The unit rides the top label only — the same rule the polar rings follow, and for the same
        // reason: one fact repeated down a legend is still one fact.
        Label(scale.ReferenceDb, unit: true);
        foreach (double r in scale.RingsDb)
            if (Math.Abs(r - scale.ReferenceDb) > 1e-9) Label(r, unit: false);
        Label(scale.FloorDb, unit: false);
    }

    // ---- Labels and captions ---------------------------------------

    /// <summary>
    /// Which pattern is drawn — the cube, the pinned frequency, the cut, the port — in the label
    /// strip's own language. <b>Only the surface actually DRAWN is named</b>; a second resolved
    /// trace is left undrawn (see <c>FirstSurface</c>) and naming it would claim it is in the
    /// picture.
    /// </summary>
    private static string? TraceIdentity(Plot plot, Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
    {
        var drawn = plot.Traces.Where(t => t.SurfaceGrid is { ThetaCount: > 1, PhiCount: > 1 }).ToList();
        if (drawn.Count == 0) return null;
        return TraceLabeler.ComputeMinimalLabels(drawn, alwaysShowSource ?? false, aliasFor)[0];
    }

    private static string? FirstRefusal(Plot plot) =>
        plot.Traces.Select(t => t.ExpressionError).FirstOrDefault(e => !string.IsNullOrEmpty(e));

    private static void DrawCaptions(SKCanvas canvas, IReadOnlyList<string> captions,
                                     float canvasW, float top, float fontSize, float lw,
                                     RenderTheme theme)
    {
        if (captions.Count == 0) return;
        using var font  = new SKFont(SkiaFonts.PlexRegular, fontSize);
        using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
        float lineH = fontSize * 1.25f;
        float avail = canvasW - 4f * lw;

        for (int i = 0; i < captions.Count; i++)
        {
            // Shrink-to-fit rather than clip, exactly as the polar caption does: half of
            // "normalised — outer ring = peak …" is a claim about an absolute level.
            font.Size = fontSize;
            float meas = font.MeasureText(captions[i]);
            if (meas > avail && meas > 0f) font.Size = Math.Max(fontSize * (avail / meas), fontSize * 0.5f);
            float cw = font.MeasureText(captions[i]);
            canvas.DrawText(captions[i], (canvasW - cw) / 2f, top + lineH * (i + 0.85f),
                            SKTextAlign.Left, font, paint);
        }
    }

    private static void DrawWrapped(SKCanvas canvas, string message, float x, float y,
                                    float fontSize, RenderTheme theme, float maxWidth)
    {
        using var font  = new SKFont(SkiaFonts.PlexRegular, fontSize);
        using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
        var words = message.Split(' ');
        var line  = new System.Text.StringBuilder();
        float ly  = y;
        foreach (var word in words)
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (maxWidth > 0 && font.MeasureText(candidate) > maxWidth && line.Length > 0)
            {
                canvas.DrawText(line.ToString(), x, ly, SKTextAlign.Left, font, paint);
                ly += fontSize * 1.25f;
                line.Clear().Append(word);
            }
            else line.Clear().Append(candidate);
        }
        if (line.Length > 0) canvas.DrawText(line.ToString(), x, ly, SKTextAlign.Left, font, paint);
    }
}
