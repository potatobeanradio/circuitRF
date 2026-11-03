// brief-em3d-28 R-em3d28-4e — the 2D chrome over the 3D pane: the AXIS INDICATOR in the lower-left
// corner (the toolbar and the A key turn it off), a SCALE BAR in the layout's display unit, the FDTD
// grid's smallest-cell labels (R-em3d28-3c), and the hover label naming what is under the cursor with
// its material values (R-em3d28-4b).
//
// brief-em3d-43 R-em3d43-5: Vertex mode's DOTS are drawn here too — the hovered candidate a dot, each
// selected vertex a larger one — because a dot that follows the cursor must cost a hover nothing on the
// GPU (gate 5), and the vertices of every object at once would be noise. B's status readout sits top left.
//
// brief-em3d-44 R-em3d44-5: the SNAP MARKER, one glyph per kind — a square on a vertex, a triangle on a
// midpoint, an × on an edge, a circle on a face centre and a small + on the grid — whenever a snap is in
// force, because a snap the user cannot see is one they cannot trust. It is the snap's own screen point,
// resolved from the frame's patch, so it costs a hover nothing either.
//
// brief-em3d-45: the DRAWING's chrome — a tool's rubber band, the points it has fixed, the document's construction
// polylines (never in the solved problem, so never in the scene), and a refused outline's crossing edges — is drawn
// here too, from world segments the editor hands over each frame (IViewer3DEditHost.FillDrawOverlay). A gesture
// in progress is therefore a few projected lines, never a document edit or an upload.
//
// brief-em3d-46: the MOVE GIZMO (three arrows in the axis indicator's colours and three plane squares, a constant
// size on screen, laid out by Render's GizmoGeometry so its hit test is the one tested headlessly), an operation's
// PIVOT cross, and a MEASUREMENT's line with a marker at each point — no extension lines, no arrowheads and no
// text in 3D: the numbers live on the card.
//
// It is drawn by Avalonia, in DIPs, from the camera alone — a redraw per presented frame is a handful of
// lines and a few text runs, and it touches no geometry. It takes no input: the pane under it does.

using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DOverlay : Control
{
    /// <summary>The axis indicator's arm length and its centre's inset from the corner, DIPs.</summary>
    public const double AxisArm = 30, AxisInset = 46;

    /// <summary>Half an axis letter (11 px text), and the reach of a click on the indicator: out to the far edge of a
    /// letter drawn on the ring.</summary>
    internal const double AxisLetterHalf = 7, AxisHitRadius = AxisArm + 8 + AxisLetterHalf;

    /// <summary>The scale bar aims for about this many DIPs.</summary>
    public const double ScaleBarTarget = 110;

    private static readonly IBrush XBrush = new SolidColorBrush(Color.FromRgb(220, 60, 60));
    private static readonly IBrush YBrush = new SolidColorBrush(Color.FromRgb(60, 170, 70));
    private static readonly IBrush ZBrush = new SolidColorBrush(Color.FromRgb(60, 110, 230));

    /// <summary>
    /// 3D editor bugs round 5 — CLIPPED to its own bounds, which are the pane's. Everything here is projected from the
    /// camera, and a projection has no reason to land inside the pane: a selected wire or edge running off screen, or the
    /// gizmo of an object panned half out of view, projects to points beyond the edge. An Avalonia control draws wherever
    /// its geometry goes unless it clips, so those strokes were painted over the object tree and the toolbar. The pane
    /// under it already clipped; the overlay never did.
    /// </summary>
    public Viewer3DOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    private Viewer3DViewModel? Vm => DataContext as Viewer3DViewModel;

    public override void Render(DrawingContext ctx)
    {
        // The silhouette at the GPU outline's two device pixels, whatever the display's scaling.
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (Vm is not { } vm) return;
        double now = _clock.Elapsed.TotalSeconds;
        _labels.BeginFrame(now - _lastFrame);
        _lastFrame = now;
        Paint(ctx, vm, Bounds.Width, Bounds.Height, _draw, picture: false, outlineWidth: 2 / Math.Max(1, scaling), labels: _labels);
        _labels.EndFrame();
        // A label still swinging to its new side asks for the next frame itself: once the camera stops, no frame is presented
        // to redraw the overlay, and the label would freeze mid-swing.
        if (_labels.Moving && !_framePending && TopLevel.GetTopLevel(this) is { } top)
        {
            _framePending = true;
            top.RequestAnimationFrame(_ => { _framePending = false; InvalidateVisual(); });
        }
    }

    /// <summary>The labels' springs (<see cref="Viewer3DLabelSpring"/>) and the clock that steps them.</summary>
    private readonly Viewer3DLabelSpring _labels = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrame;
    private bool _framePending;

    /// <summary>
    /// The overlay, <paramref name="w"/> × <paramref name="h"/> DIPs. <paramref name="picture"/> paints it for a PICTURE of
    /// the view (3D editor bugs round 6: Copy and Export Picture… carried the GPU's pixels alone, so the measurement, the
    /// axis indicator, the scale bar and an Edge- or Vertex-mode selection were missing): what the view SHOWS — those, the
    /// selection's outline and the measurement's numbers (the card is a control, not overlay) — and none of what the POINTER
    /// is doing (the hover label and ring, the snap marker, the move gizmo's handles, the cycle prompt). The field's legend
    /// is left to FieldPicture, which paints it as the export options say.
    /// </summary>
    internal static void Paint(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, Viewer3DDrawOverlay draw, bool picture,
                               double outlineWidth = 2, Viewer3DLabelSpring? labels = null)
    {
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        IBrush ink = dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(35, 38, 44));
        var inkPen = new Pen(ink, 1.5);
        var cam = vm.View.Camera;
        if (w < 10 || h < 10) return;

        if (vm.View.ShowAxisIndicator) AxisIndicator(ctx, cam, AxisIndicatorCentre(h), ink);
        if (vm.ShowScaleLegend) ScaleBar(ctx, cam, vm, w, h, inkPen, ink);

        foreach (var (text, at) in vm.GridLabels.Select(l => (l.Text, l.At)).Concat(vm.GridFeedLabels.Select(l => (l.Text, l.At))))
        {
            var (x, y, visible) = cam.Project(at, (float)w, (float)h);
            if (!visible) continue;
            Text(ctx, text, new Point(x + 6, y - 18), ink, 11, dark);
        }

        if (!picture)
        {
            double stackBottom = vm.FieldLegendVisible ? Legends(ctx, vm, w, h, ink, dark) : LegendTop;
            // brief-em3d-109 R-em3d109-4c — under the legend stack (alone in its corner without one); FieldPicture paints a picture's
            if (vm.FieldIndicator is { } indicator) Indicator(ctx, indicator, w, stackBottom, ink);
        }

        // brief-em3d-75 R-em3d75-4c — the hot spot: a ring at the maximum of what is drawn, its temperature and its object.
        // brief-em3d-96 — one per drawn temperature plot.
        if (vm.ShowField)
            foreach (var (hot, label) in vm.HotSpots)
            {
                if (cam.Project(hot.At, (float)w, (float)h) is not (var hx0, var hy0, true)) continue;
                var at = new Point(hx0, hy0);
                ctx.DrawEllipse(null, new Pen(dark ? Brushes.Black : Brushes.White, 4), at, 7, 7);
                ctx.DrawEllipse(null, new Pen(HotBrush, 2), at, 7, 7);
                ctx.DrawLine(new Pen(HotBrush, 1.5), new Point(at.X - 11, at.Y), new Point(at.X - 4, at.Y));
                ctx.DrawLine(new Pen(HotBrush, 1.5), new Point(at.X + 4, at.Y), new Point(at.X + 11, at.Y));
                Text(ctx, label, new Point(at.X + 12, at.Y - 20), ink, 12, dark);
            }

        // brief-sar-6 R-sar6-5 — a SAR plot's peak: the averaging cube as a wireframe (a ring for local SAR), and its value.
        if (vm.ShowField)
            foreach (var (corners, at, label) in vm.SarPeaks)
            {
                var halo = new Pen(dark ? Brushes.Black : Brushes.White, 4);
                var pen = new Pen(HotBrush, 1.75);
                if (corners is not null)
                {
                    var p = new (double X, double Y, bool Visible)[8];
                    for (int k = 0; k < 8; k++) p[k] = cam.Project(corners[k], (float)w, (float)h);
                    foreach (var line in new[] { halo, pen })
                        for (int a = 0; a < 8; a++)
                            for (int bit = 1; bit < 8; bit <<= 1)
                                if ((a & bit) == 0 && p[a].Visible && p[a | bit].Visible)
                                    ctx.DrawLine(line, new Point(p[a].X, p[a].Y), new Point(p[a | bit].X, p[a | bit].Y));
                }
                if (cam.Project(at, (float)w, (float)h) is not (var px, var py, true)) continue;
                if (corners is null)
                {
                    ctx.DrawEllipse(null, halo, new Point(px, py), 6, 6);
                    ctx.DrawEllipse(null, pen, new Point(px, py), 6, 6);
                }
                Text(ctx, label, new Point(px + 12, py - 20), ink, 12, dark);
            }

        // Vertex mode: a dot for the candidate, a larger one for each selected vertex.
        var accent = new SolidColorBrush(Color.FromRgb(255, 90, 255));
        var ring = new Pen(dark ? Brushes.Black : Brushes.White, 1.5);
        if (vm.SelectMode == CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Vertex)
        {
            foreach (var item in vm.Selection)
            {
                var (x, y, visible) = cam.Project(item.Point, (float)w, (float)h);
                if (visible) ctx.DrawEllipse(accent, ring, new Point(x, y), 6, 6);
            }
            if (!picture && vm.HoveredVertex is { } hv && cam.Project(hv, (float)w, (float)h) is (var hx, var hy, true))
                ctx.DrawEllipse(Brushes.Transparent, new Pen(accent, 2), new Point(hx, hy), 4, 4);
        }
        // brief-em3d-67 R-em3d67-3c — Edge mode: each selected edge, then the hovered one, as a thick polyline projected from
        // the camera each frame (no GPU buffer changes when the hover moves).
        if (vm.SelectMode == CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Edge)
        {
            var halo = new Pen(dark ? Brushes.Black : Brushes.White, 5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var selected = new Pen(accent, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            foreach (var item in vm.Selection)
                if (vm.EdgePoints(item) is { } pts) Polyline(ctx, vm, pts, w, h, halo, selected);
            if (!picture && vm.HoveredItem is { IsEdge: true } he && !vm.Selection.Contains(he) && vm.EdgePoints(he) is { } hp)
                Polyline(ctx, vm, hp, w, h, halo, new Pen(RubberBrush, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
        }
        if (picture) SelectionOutline(ctx, vm, w, h);
        Silhouette(ctx, vm, w, h, outlineWidth);
        if (!picture && vm.CycleText.Length > 0) Text(ctx, vm.CycleText, new Point(10, 8), ink, 12, dark);

        if (vm.EditHost is { } host)
        {
            draw.Clear();
            host.FillDrawOverlay(draw);
            Drawing(ctx, vm, draw, w, h, dark, labels);
        }

        Measurement(ctx, vm, w, h, dark);
        Dimension(ctx, vm, w, h, ink, dark);
        if (picture) MeasureTable(ctx, vm, h, ink, dark);
        if (picture) return;
        if (vm.GizmoNow() is { } gizmo) Gizmo(ctx, gizmo, vm.GizmoHover, vm.GizmoActive, dark);

        Tracking(ctx, vm, w, h, dark);
        if (vm.Snap.IsSnap) SnapMarker(ctx, vm.Snap.Kind, new Point(vm.Snap.ScreenX, vm.Snap.ScreenY), dark, SnapColour(vm.Scene, vm.Snap));

        if (vm.HoverText.Length > 0 && vm.View.CursorX >= 0)
            Text(ctx, vm.HoverText, new Point(vm.View.CursorX + 14, vm.View.CursorY + 14), ink, 12, dark);
    }

    /// <summary>fs_edge's colour: what the GPU outlines a selection in.</summary>
    private static readonly Color OutlineColour = Color.FromRgb(255, 89, 255);

    /// <summary>
    /// 3D editor bugs round 6 — an Object- or Face-mode selection's outline, for a picture. On screen the GPU draws it
    /// (Scene3DFramePlan's edge passes, a pixel apart) two DEVICE pixels wide, whatever the picture's multiple: at Copy's 4×
    /// that is half a pixel of the window, and in a picture pasted at the window's size it had all but vanished. Here it is
    /// the same lines at the screen's width in DIPs, so it scales with the picture. As fs_edge draws them: no depth test,
    /// nothing on the clipped side, and in Face mode only the edges of a selected face.
    /// </summary>
    private static void SelectionOutline(DrawingContext ctx, Viewer3DViewModel vm, double w, double h)
    {
        var mode = vm.SelectMode;
        if (mode is not (CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Object or CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Face)) return;
        var view = vm.View;
        var scene = vm.Scene;
        if (view.Selection.Length == 0 || scene.EdgeBatches.Length == 0) return;
        var cam = view.Camera;
        var pen = new Pen(new SolidColorBrush(OutlineColour), 2, lineCap: PenLineCap.Round);
        var clip = view.Clip.Enabled ? view.Clip.Equation : (Vector4?)null;
        int limit = Math.Min(view.Selection.Length, Scene3DFramePlan.SelectionLimit);
        var done = new HashSet<uint>();
        for (int k = 0; k < limit; k++)
        {
            uint id = view.Selection[k].Object;
            if (!view.IsDrawn(id) || !done.Add(id)) continue;
            foreach (var eb in scene.EdgeBatches)
            {
                if (eb.ObjectId != id) continue;
                var offset = eb.Element >= 0 && eb.Element < scene.Elements.Length ? scene.Elements[eb.Element].Offset : Vector3.Zero;
                int end = Math.Min(eb.FirstVertex + eb.VertexCount, scene.LineVertices.Length);
                for (int i = eb.FirstVertex; i + 1 < end; i += 2)
                {
                    var va = scene.LineVertices[i];
                    if (mode == CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Face && !OnSelectedFace(view, id, va.Face)) continue;
                    var vb = scene.LineVertices[i + 1];
                    var a = new Vector3(va.X, va.Y, va.Z) + offset;
                    var b = new Vector3(vb.X, vb.Y, vb.Z) + offset;
                    if (clip is { } c && !ClipSegment(c, ref a, ref b)) continue;
                    var (ax, ay, fa) = cam.Project(a, (float)w, (float)h);
                    var (bx, by, fb) = cam.Project(b, (float)w, (float)h);
                    if (fa && fb) ctx.DrawLine(pen, new Point(ax, ay), new Point(bx, by));
                }
            }
        }
    }

    /// <summary>
    /// An Object-mode selection's SILHOUETTE (Scene3DSilhouette): the outline of a curved surface from the eye, which its
    /// feature edges do not draw — a selected sphere's only outline. In fs_edge's colour and as it draws: no depth test,
    /// nothing on the clipped side; a moving object's follows its preview's copies, as its feature edges do.
    /// </summary>
    private static void Silhouette(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, double width)
    {
        var view = vm.View;
        if (vm.SelectMode != CircuitRF.Render.Scene3D.Edit.Scene3DSelectMode.Object || view.Selection.Length == 0) return;
        var scene = vm.Scene;
        var cam = view.Camera;
        var preview = view.Preview;
        var segments = new List<(Vector3 A, Vector3 B)>();
        int limit = Math.Min(view.Selection.Length, Scene3DFramePlan.SelectionLimit);
        var done = new HashSet<uint>();
        for (int k = 0; k < limit; k++)
        {
            uint id = view.Selection[k].Object;
            if (id < 1 || id > scene.Objects.Length || !view.IsDrawn(id) || !done.Add(id)) continue;
            if (preview is not null && preview.IsMoving(id))
            {
                foreach (var m in preview.Copies) CircuitRF.Render.Scene3D.Edit.Scene3DSilhouette.Collect(scene, id, cam, m, segments);
                if (!preview.KeepOriginal) continue;
            }
            CircuitRF.Render.Scene3D.Edit.Scene3DSilhouette.Collect(scene, id, cam, Matrix4x4.Identity, segments);
        }
        if (segments.Count == 0) return;
        var clip = view.Clip.Enabled ? view.Clip.Equation : (Vector4?)null;
        var g = new StreamGeometry();
        using (var c = g.Open())
            foreach (var (a0, b0) in segments)
            {
                var (a, b) = (a0, b0);
                if (clip is { } cp && !ClipSegment(cp, ref a, ref b)) continue;
                var (ax, ay, fa) = cam.Project(a, (float)w, (float)h);
                var (bx, by, fb) = cam.Project(b, (float)w, (float)h);
                if (!fa || !fb) continue;
                c.BeginFigure(new Point(ax, ay), false);
                c.LineTo(new Point(bx, by));
                c.EndFigure(false);
            }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(OutlineColour), width, lineCap: PenLineCap.Round), g);
    }

    /// <summary>fs_edge's Face-mode test: an edge vertex carries its two faces packed, low and high 16 bits.</summary>
    private static bool OnSelectedFace(Viewer3DViewState view, uint id, uint packed)
    {
        foreach (var item in view.Selection)
            if (item.Object == id && item.Face >= 0 && ((packed & 0xFFFF) == (uint)item.Face || (packed >> 16) == (uint)item.Face)) return true;
        return false;
    }

    /// <summary>The part of segment a–b the clip plane keeps (fs_edge discards where dot(n, p) + d &gt; 0); false for none.</summary>
    internal static bool ClipSegment(Vector4 plane, ref Vector3 a, ref Vector3 b)
    {
        var n = new Vector3(plane.X, plane.Y, plane.Z);
        float da = Vector3.Dot(n, a) + plane.W, db = Vector3.Dot(n, b) + plane.W;
        if (da > 0 && db > 0) return false;
        if (da <= 0 && db <= 0) return true;
        var cut = a + (b - a) * (da / (da - db));
        if (da > 0) a = cut; else b = cut;
        return true;
    }

    /// <summary>
    /// 3D editor bugs round 6 — the Measure card's numbers, for a picture, where the card sits (bottom left, above the axis
    /// indicator): the card is a control over the pane, not overlay, so a picture of the view never had it.
    /// </summary>
    private static void MeasureTable(DrawingContext ctx, Viewer3DViewModel vm, double h, IBrush ink, bool dark)
    {
        if (vm.MeasureReadout is not { } r) return;
        const double pad = 8, label = 64, col = 100, line = 17;
        var rows = new List<string[]> { new[] { "Measure", "x", "y", "z" } };
        foreach (var row in r.Rows) rows.Add([row.Heading, row.X.Text, row.Y.Text, row.Z.Text]);
        if (r.Distance is { } d) rows.Add([r.DistanceHeading, d.Text]);
        double bw = 2 * pad + label + 3 * col, bh = 2 * pad + line * rows.Count;
        double x0 = 12, y0 = h - 104 - bh;
        ctx.DrawRectangle(new SolidColorBrush(dark ? Color.FromArgb(242, 40, 42, 46) : Color.FromArgb(242, 252, 252, 253)),
                          new Pen(new SolidColorBrush(dark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0)), 1),
                          new Rect(x0, y0, bw, bh), 4, 4);
        for (int i = 0; i < rows.Count; i++)
        {
            double y = y0 + pad + i * line;
            for (int c = 0; c < rows[i].Length; c++)
            {
                bool heading = i == 0;
                var ft = new FormattedText(rows[i][c], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                           heading && c == 0 ? new Typeface(FontFamily.Default, weight: FontWeight.SemiBold) : Typeface.Default,
                                           heading && c > 0 ? 11 : 12, ink);
                double x = x0 + pad + (c == 0 ? 0 : label + (c - 1) * col);
                using (ctx.PushOpacity(heading && c > 0 ? 0.7 : 1)) ctx.DrawText(ft, new Point(x, y));
            }
        }
    }

    /// <summary>
    /// 3D editor bugs round 6 — the overlay for a picture <paramref name="pixelW"/> × <paramref name="pixelH"/> of a pane
    /// <paramref name="dipW"/> DIPs wide, painted at the picture's own resolution (its DPI is the picture's pixels per
    /// DIP), so a line is as thick and a label as large, relative to the view, as on screen. Premultiplied, rows top first,
    /// in the platform's byte order (<see cref="FieldPictureLayer.Bgra"/>), for FieldPicture to lay over the GPU's pixels.
    /// </summary>
    public static FieldPictureLayer PictureLayer(Viewer3DViewModel vm, double dipW, int pixelW, int pixelH)
    {
        double k = pixelW / Math.Max(1, dipW);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Avalonia.Vector(96 * k, 96 * k));
        using (var ctx = bitmap.CreateDrawingContext())
            Paint(ctx, vm, pixelW / k, pixelH / k, new Viewer3DDrawOverlay(), picture: true);
        var bytes = new byte[pixelW * pixelH * 4];
        unsafe
        {
            fixed (byte* p = bytes) bitmap.CopyPixels(new PixelRect(0, 0, pixelW, pixelH), (nint)p, bytes.Length, pixelW * 4);
        }
        return new FieldPictureLayer(bytes, bitmap.Format != Avalonia.Platform.PixelFormats.Rgba8888);
    }

    private readonly Viewer3DDrawOverlay _draw = new();

    private static readonly IBrush HotBrush = new SolidColorBrush(Color.FromRgb(255, 70, 40));
    private static readonly IBrush HeatBrush = new SolidColorBrush(Color.FromRgb(240, 120, 40));
    private static readonly IBrush ProbeBrush = new SolidColorBrush(Color.FromRgb(40, 200, 220));
    private static readonly IBrush RegionBrush = new SolidColorBrush(Color.FromRgb(170, 120, 230));
    /// <summary>brief-em3d-90 — a thermal symmetry plane: the colour the air box hatches a symmetry face in (Scene3DBuilder).</summary>
    private static readonly IBrush LumpedBrush = new SolidColorBrush(Color.FromRgb(200, 220, 60));
    private static readonly IBrush SymmetryBrush = new SolidColorBrush(Color.FromRgb(160, 80, 210));

    /// <summary>brief-em3d-67 — a world polyline, projected, drawn twice: a halo, then the stroke.</summary>
    private static void Polyline(DrawingContext ctx, Viewer3DViewModel vm, IReadOnlyList<CircuitRF.Engine.Em3d.Point3> pts, double w, double h,
                                 Pen halo, Pen stroke)
    {
        var cam = vm.View.Camera;
        var screen = new List<Point>(pts.Count);
        foreach (var p in pts)
        {
            var (x, y, front) = cam.Project(vm.Scene.ToLocal(p.X, p.Y, p.Z), (float)w, (float)h);
            if (!front) return;
            screen.Add(new Point(x, y));
        }
        if (screen.Count < 2) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(screen[0], false);
            for (int i = 1; i < screen.Count; i++) c.LineTo(screen[i]);
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, halo, g);
        ctx.DrawGeometry(null, stroke, g);
    }

    private static readonly IBrush RubberBrush = new SolidColorBrush(Color.FromRgb(255, 196, 40));
    private static readonly IBrush CrossingBrush = new SolidColorBrush(Color.FromRgb(235, 40, 40));
    /// <summary>The scene shader's hover cyan (0.2, 0.9, 1.0), for a tool's target face.</summary>
    private static readonly IBrush TargetBrush = new SolidColorBrush(Color.FromRgb(51, 230, 255));
    private static readonly IBrush TargetFill = new SolidColorBrush(Color.FromArgb(90, 51, 230, 255));
    /// <summary>brief-dfr31-3 — a field plot's plane: the selection's magenta, the fill faint enough to see the model through.</summary>
    private static readonly IBrush PlaneBrush = new SolidColorBrush(Color.FromRgb(255, 90, 255));
    private static readonly IBrush PlaneFill = new SolidColorBrush(Color.FromArgb(46, 255, 90, 255));

    /// <summary>brief-em3d-45 — the drawing's chrome: construction dashed, a selected polyline in the selection colour,
    /// the rubber band in amber over a halo, fixed points as dots, a crossing in red.</summary>
    private static void Drawing(DrawingContext ctx, Viewer3DViewModel vm, Viewer3DDrawOverlay d, double w, double h, bool dark,
                                Viewer3DLabelSpring? springs)
    {
        var cam = vm.View.Camera;
        var scene = vm.Scene;
        (Point P, bool Ok) Screen(CircuitRF.Engine.Em3d.Point3 p)
        {
            var (x, y, front) = cam.Project(scene.ToLocal(p.X, p.Y, p.Z), (float)w, (float)h);
            return (new Point(x, y), front);
        }
        void Lines(List<CircuitRF.Render.Scene3D.Edit.DrawSegment> segs, Pen pen)
        {
            foreach (var s in segs)
            {
                var (a, oa) = Screen(s.A);
                var (b, ob) = Screen(s.B);
                if (oa && ob) ctx.DrawLine(pen, a, b);
            }
        }
        // brief-dfr31-3 R-dfr31-3-1 — a field plot's cut plane, under the rest of the chrome: translucent in the selection's
        // colour, outlined, and labelled with its axis and offset at its first corner on screen.
        var planeLabels = new List<(Point At, string Text)>();
        foreach (var (corners, label) in d.FieldPlanes)
        {
            var pts = corners.Select(Screen).ToList();
            if (pts.Any(q => !q.Ok)) continue;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0].P, true);
                for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i].P);
                c.EndFigure(true);
            }
            ctx.DrawGeometry(PlaneFill, new Pen(PlaneBrush, 1.5), g);
            planeLabels.Add((pts.MinBy(q => q.P.Y + q.P.X * 1e-3).P, label));
        }
        DrawingLabels(ctx, planeLabels, w, h, dark, springs, "plane");
        var construction = new Pen(dark ? new SolidColorBrush(Color.FromArgb(200, 200, 205, 215)) : new SolidColorBrush(Color.FromArgb(200, 70, 75, 85)), 1.2)
        {
            DashStyle = new DashStyle([4, 3], 0),
        };
        Lines(d.Construction, construction);
        // brief-em3d-75 R-em3d75-1b — the thermal places: never solids, never faces to snap onto; drawn here only.
        Lines(d.HeatSources, new Pen(HeatBrush, 1.4));
        Lines(d.MeshRegions, new Pen(RegionBrush, 1.3) { DashStyle = new DashStyle([6, 4], 0) });
        Lines(d.Probes, new Pen(ProbeBrush, 1.6));
        Lines(d.SymmetryPlanes, new Pen(SymmetryBrush, 1.1));
        Lines(d.LumpedElements, new Pen(LumpedBrush, 1.6));            // brief-sar-3
        foreach (var p in d.ProbeMarks)
            if (Screen(p) is (var pp, true))
            {
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(pp.X, pp.Y - 5), true);
                    g.LineTo(new Point(pp.X + 5, pp.Y)); g.LineTo(new Point(pp.X, pp.Y + 5)); g.LineTo(new Point(pp.X - 5, pp.Y));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(ProbeBrush, new Pen(dark ? Brushes.Black : Brushes.White, 1), diamond);
            }
        // brief-em3d-48 R-em3d48-6b — a cell that resolves to nothing: its last known box, dashed, and its name.
        Lines(d.Missing, new Pen(CrossingBrush, 1.4) { DashStyle = new DashStyle([5, 4], 0) });
        var shown = new List<(Point At, string Text)>();
        foreach (var (at, text) in d.Labels)
            if (Screen(at) is (var lp, true)) shown.Add((lp, text));
        DrawingLabels(ctx, shown, w, h, dark, springs, "label");
        Lines(d.Selected, new Pen(new SolidColorBrush(Color.FromRgb(255, 90, 255)), 2));
        // The face a tool lands on, in the hover's cyan: only that face, never the whole object it belongs to.
        foreach (var rings in d.TargetFaces)
        {
            var g = new StreamGeometry();
            bool drawn = true;
            using (var c = g.Open())
            {
                c.SetFillRule(FillRule.EvenOdd);
                foreach (var ring in rings)
                {
                    if (ring.Count < 3) continue;
                    var pts = ring.Select(Screen).ToList();
                    if (pts.Any(q => !q.Ok)) { drawn = false; break; }
                    c.BeginFigure(pts[0].P, true);
                    for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i].P);
                    c.EndFigure(true);
                }
            }
            if (drawn) ctx.DrawGeometry(TargetFill, new Pen(TargetBrush, 1.6), g);
        }
        if (d.Rubber.Count > 0)
        {
            Lines(d.Rubber, new Pen(dark ? Brushes.Black : Brushes.White, 3.5, lineCap: PenLineCap.Round));
            Lines(d.Rubber, new Pen(RubberBrush, 1.5, lineCap: PenLineCap.Round));
        }
        foreach (var p in d.Fixed)
            if (Screen(p) is (var sp, true)) ctx.DrawEllipse(RubberBrush, new Pen(dark ? Brushes.Black : Brushes.White, 1), sp, 3, 3);
        Lines(d.Crossing, new Pen(CrossingBrush, 3, lineCap: PenLineCap.Round));
        // brief-em3d-46 R-em3d46-3a — a rotation's pivot: a small cross while the gesture lasts.
        foreach (var p in d.Pivots)
        {
            if (Screen(p) is not (var c, true)) continue;
            foreach (var stroke in new[] { new Pen(dark ? Brushes.Black : Brushes.White, 4), new Pen(RubberBrush, 1.8) })
            {
                ctx.DrawLine(stroke, new Point(c.X - 7, c.Y), new Point(c.X + 7, c.Y));
                ctx.DrawLine(stroke, new Point(c.X, c.Y - 7), new Point(c.X, c.Y + 7));
            }
        }
    }

    private static readonly IBrush MeasureBrush = new SolidColorBrush(Color.FromRgb(40, 200, 220));

    /// <summary>brief-em3d-46 R-em3d46-6e — a thin line between the measured points and a small marker at each.</summary>
    private static void Measurement(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, bool dark)
    {
        if (vm.MeasureP1 is not { } a) return;
        var cam = vm.View.Camera;
        (Point P, bool Ok) Screen(Viewer3DMeasurePoint p)
        {
            var (x, y, front) = cam.Project(vm.Scene.ToLocal(p.X, p.Y, p.Z), (float)w, (float)h);
            return (new Point(x, y), front);
        }
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 3);
        var pen = new Pen(MeasureBrush, 1.2) { DashStyle = vm.MeasureP2Fixed ? null : new DashStyle([5, 3], 0) };
        var (pa, oa) = Screen(a);
        if (vm.MeasureP2 is { } b && Screen(b) is (var pb, true) && oa)
        {
            ctx.DrawLine(halo, pa, pb);
            ctx.DrawLine(pen, pa, pb);
            ctx.DrawEllipse(MeasureBrush, new Pen(dark ? Brushes.Black : Brushes.White, 1), pb, 3.5, 3.5);
        }
        if (oa) ctx.DrawEllipse(MeasureBrush, new Pen(dark ? Brushes.Black : Brushes.White, 1), pa, 3.5, 3.5);
    }

    /// <summary>brief-sar-12 R-sar12-2 — the placed device's separation: a line from its nearest point to the face, ticks at both
    /// ends, and the distance beside its middle.</summary>
    private static void Dimension(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, IBrush ink, bool dark)
    {
        if (vm.Dimension is not { } d) return;
        var cam = vm.View.Camera;
        var (ax, ay, af) = cam.Project(vm.Scene.ToLocal(d.A.X, d.A.Y, d.A.Z), (float)w, (float)h);
        var (bx, by, bf) = cam.Project(vm.Scene.ToLocal(d.B.X, d.B.Y, d.B.Z), (float)w, (float)h);
        if (!af || !bf) return;
        var a = new Point(ax, ay);
        var b = new Point(bx, by);
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 3);
        var pen = new Pen(MeasureBrush, 1.2);
        ctx.DrawLine(halo, a, b);
        ctx.DrawLine(pen, a, b);
        // Ticks across the line at both ends, so a separation shorter than a few pixels still reads as a dimension.
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        var (nx, ny) = len > 1e-6 ? (-dy / len, dx / len) : (1.0, 0.0);
        foreach (var p in new[] { a, b }) ctx.DrawLine(pen, new Point(p.X - 6 * nx, p.Y - 6 * ny), new Point(p.X + 6 * nx, p.Y + 6 * ny));
        Text(ctx, d.Label, new Point(0.5 * (a.X + b.X) + 10 * nx + 6, 0.5 * (a.Y + b.Y) + 10 * ny), ink, 12, dark);
    }

    private static readonly IBrush GizmoHot = new SolidColorBrush(Color.FromRgb(255, 196, 40));

    /// <summary>brief-em3d-46 R-em3d46-5 — the move gizmo: arrows in the axis colours, plane squares tinted by their
    /// normal; the hovered handle in amber; during a drag only the active handle.</summary>
    private static void Gizmo(DrawingContext ctx, CircuitRF.Render.Scene3D.Edit.GizmoLayout g, CircuitRF.Render.Scene3D.Edit.GizmoHandle hover,
                              CircuitRF.Render.Scene3D.Edit.GizmoHandle active, bool dark)
    {
        IBrush[] axis = [XBrush, YBrush, ZBrush];
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 5, lineCap: PenLineCap.Round);
        var c = new Point(g.Centre.X, g.Centre.Y);
        bool dragging = active != CircuitRF.Render.Scene3D.Edit.GizmoHandle.None;
        for (int k = 0; k < 3; k++)
        {
            var h = CircuitRF.Render.Scene3D.Edit.GizmoHandle.PlaneX + k;
            if (!g.SquareUsable[k] || (dragging && active != h)) continue;
            var q = g.Squares[k];
            var geo = new StreamGeometry();
            using (var sc = geo.Open())
            {
                sc.BeginFigure(new Point(q[0].X, q[0].Y), true);
                for (int i = 1; i < 4; i++) sc.LineTo(new Point(q[i].X, q[i].Y));
                sc.EndFigure(true);
            }
            bool hot = hover == h || active == h;
            var col = ((SolidColorBrush)axis[k]).Color;
            ctx.DrawGeometry(new SolidColorBrush(hot ? Color.FromArgb(200, 255, 196, 40) : Color.FromArgb(90, col.R, col.G, col.B)),
                             new Pen(hot ? GizmoHot : axis[k], 1), geo);
        }
        for (int k = 0; k < 3; k++)
        {
            var h = CircuitRF.Render.Scene3D.Edit.GizmoHandle.AxisX + k;
            if (!g.AxisUsable[k] || (dragging && active != h)) continue;
            var tip = new Point(g.Tips[k].X, g.Tips[k].Y);
            var from = new Point(c.X + g.Directions[k].X * 6, c.Y + g.Directions[k].Y * 6);
            bool hot = hover == h || active == h;
            var pen = new Pen(hot ? GizmoHot : axis[k], hot ? 3.5 : 2.5, lineCap: PenLineCap.Round);
            ctx.DrawLine(halo, from, tip);
            ctx.DrawLine(pen, from, tip);
            // The arrowhead: a small triangle at the tip.
            var d = g.Directions[k];
            var n = new System.Numerics.Vector2(-d.Y, d.X);
            var head = new StreamGeometry();
            using (var sc = head.Open())
            {
                sc.BeginFigure(new Point(tip.X + d.X * 8, tip.Y + d.Y * 8), true);
                sc.LineTo(new Point(tip.X + n.X * 4, tip.Y + n.Y * 4));
                sc.LineTo(new Point(tip.X - n.X * 4, tip.Y - n.Y * 4));
                sc.EndFigure(true);
            }
            ctx.DrawGeometry(hot ? GizmoHot : axis[k], null, head);
        }
        if (!dragging) ctx.DrawEllipse(dark ? Brushes.WhiteSmoke : Brushes.DimGray, null, c, 3, 3);
    }

    /// <summary>Snap tracking's reference marks — a small plus at each acquired point — and, when the snap in force is on a
    /// tracking line, that line from its reference to the snap, dashed in its axis's colour.</summary>
    private static void Tracking(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, bool dark)
    {
        var refs = vm.Tracker.References;
        if (!vm.SnapTrackingActive || refs.Count == 0) return;
        var cam = vm.View.Camera;
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 3.5, lineCap: PenLineCap.Round);
        if (vm.Snap.Kind == CircuitRF.Render.Scene3D.Edit.Snap3DKind.Track)
        {
            var to = new Point(vm.Snap.ScreenX, vm.Snap.ScreenY);
            foreach (var line in new[] { vm.Tracker.LineA, vm.Tracker.LineB })
            {
                if (line is not { } l || l.Reference >= refs.Count) continue;
                var r = refs[l.Reference].Point.World;
                if (cam.Project(vm.Scene.ToLocal(r.X, r.Y, r.Z), (float)w, (float)h) is not (var rx, var ry, true)) continue;
                var from = new Point(rx, ry);
                var end = to;                                  // the line ends where the click will land
                var brush = l.Axis switch { 0 => XBrush, 1 => YBrush, _ => ZBrush };
                var pen = new Pen(brush, 1.5) { DashStyle = new DashStyle([5, 4], 0) };
                ctx.DrawLine(halo, from, end);
                ctx.DrawLine(pen, from, end);
            }
        }
        var mark = new Pen(new SolidColorBrush(TrackColour), 2, lineCap: PenLineCap.Round);
        const double m = 5;
        foreach (var r in refs)
        {
            if (cam.Project(vm.Scene.ToLocal(r.Point.World.X, r.Point.World.Y, r.Point.World.Z), (float)w, (float)h) is not (var x, var y, true)) continue;
            foreach (var pen in new[] { halo, mark })
            {
                ctx.DrawLine(pen, new Point(x - m, y), new Point(x + m, y));
                ctx.DrawLine(pen, new Point(x, y - m), new Point(x, y + m));
            }
        }
    }

    /// <summary>Snap tracking's colour — its reference marks and its snap glyph: a green no material or selection uses.</summary>
    internal static readonly Color TrackColour = Color.FromRgb(40, 200, 120);

    /// <summary>The snap marker's colour when there is no material to take one from: an amber no material or selection
    /// uses, over a contrasting halo.</summary>
    internal static readonly Color SnapAmber = Color.FromRgb(255, 176, 0);

    /// <summary>
    /// 3D editor bugs round 2 — the marker is drawn in the colour of what it snaps to, as the layout editor draws its
    /// marker in the snapped layer's colour: the object's own material colour, opaque (a translucent dielectric's alpha
    /// would make the glyph faint). Amber for the grid, for an object with no material (drawn wireframe), and for
    /// anything that is not a material's object — a port, a box face.
    /// </summary>
    internal static Color SnapColour(Scene3DModel scene, CircuitRF.Render.Scene3D.Edit.Snap3DResult snap)
    {
        if (snap.Kind == CircuitRF.Render.Scene3D.Edit.Snap3DKind.Track) return TrackColour;
        if (snap.Kind == CircuitRF.Render.Scene3D.Edit.Snap3DKind.Grid || scene.Object(snap.Object) is not { } o
            || o.Wireframe || o.Material is not { Length: > 0 })
            return SnapAmber;
        uint c = o.Rgba;
        return Color.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16));
    }

    /// <summary>brief-em3d-44 R-em3d44-5 — one glyph per kind, drawn twice: a halo, then the coloured stroke.</summary>
    private static void SnapMarker(DrawingContext ctx, CircuitRF.Render.Scene3D.Edit.Snap3DKind kind, Point p, bool dark, Color colour)
    {
        const double r = 6;
        var halo = new Pen(dark ? Brushes.Black : Brushes.White, 4, lineCap: PenLineCap.Round);
        var pen = new Pen(new SolidColorBrush(colour), 2, lineCap: PenLineCap.Round);
        foreach (var stroke in new[] { halo, pen })
        {
            switch (kind)
            {
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Vertex:
                    ctx.DrawRectangle(null, stroke, new Rect(p.X - r, p.Y - r, 2 * r, 2 * r));
                    break;
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Midpoint:
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(p.X, p.Y - r * 1.1), false);
                        c.LineTo(new Point(p.X + r, p.Y + r * 0.8));
                        c.LineTo(new Point(p.X - r, p.Y + r * 0.8));
                        c.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, stroke, g);
                    break;
                }
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Edge:
                    ctx.DrawLine(stroke, new Point(p.X - r, p.Y - r), new Point(p.X + r, p.Y + r));
                    ctx.DrawLine(stroke, new Point(p.X - r, p.Y + r), new Point(p.X + r, p.Y - r));
                    break;
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.FaceCentre:
                    ctx.DrawEllipse(null, stroke, p, r, r);
                    break;
                // brief-em3d-67 R-em3d67-3e — a circle's or an arc's centre: the face-centre marker with a dot in it.
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Centre:
                    ctx.DrawEllipse(null, stroke, p, r, r);
                    ctx.DrawEllipse(stroke.Brush, null, p, 1.8, 1.8);
                    break;
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Grid:
                    ctx.DrawLine(stroke, new Point(p.X - r * 0.6, p.Y), new Point(p.X + r * 0.6, p.Y));
                    ctx.DrawLine(stroke, new Point(p.X, p.Y - r * 0.6), new Point(p.X, p.Y + r * 0.6));
                    break;
                // Snap tracking — a point on a tracking line: a small diamond.
                case CircuitRF.Render.Scene3D.Edit.Snap3DKind.Track:
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(p.X, p.Y - r), false);
                        c.LineTo(new Point(p.X + r, p.Y));
                        c.LineTo(new Point(p.X, p.Y + r));
                        c.LineTo(new Point(p.X - r, p.Y));
                        c.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, stroke, g);
                    break;
                }
            }
        }
    }

    /// <summary>brief-em3d-29 R-em3d29-3c — the field's legend, top right: the quantity, a colour bar with the
    /// range at its ends, the range's percentile, the solution, and the phase when animated. brief-em3d-96 D3 — one per colour
    /// range, stacked down the right 8 DIPs apart, all one HELD width (the column's widest, so a drag moves none of them
    /// sideways); one that would run past the bottom is not drawn, and the last one drawn ends with "+N more".</summary>
    // brief-idle-power — a legend's brushes are made once, not on every render: the colour maps are three fixed instances, so
    // one immutable gradient each is exact, and an immutable brush is not a new composition resource per frame.
    private static readonly IBrush LegendFillDark = new ImmutableSolidColorBrush(Color.FromArgb(215, 28, 30, 34));
    private static readonly IBrush LegendFillLight = new ImmutableSolidColorBrush(Color.FromArgb(225, 250, 250, 252));
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ColorMap3D, IBrush> LegendBars = [];

    /// <summary>The legend's colour bar for <paramref name="map"/>: the same instance on every render.</summary>
    internal static IBrush LegendBar(ColorMap3D map) => LegendBars.GetValue(map, m =>
    {
        var stops = new GradientStops();
        foreach (var (t, r, gr, b) in m.Stops) stops.Add(new GradientStop(Color.FromRgb(r, gr, b), t));
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = stops,
        }.ToImmutable();
    });

    /// <summary>The legend stack's top, and its right edge's inset from the view's.</summary>
    private const double LegendTop = 12, LegendInset = 12;

    /// <summary>brief-em3d-109 R-em3d109-4b — the field style's indicator: small, in the legend's secondary text colour (its ink at
    /// FieldPicture.IndicatorAlpha), right-aligned under the stack that ends at <paramref name="top"/>.</summary>
    private static void Indicator(DrawingContext ctx, string text, double w, double top, IBrush ink)
    {
        var secondary = ink is ISolidColorBrush sc
            ? new ImmutableSolidColorBrush(Color.FromArgb(FieldPicture.IndicatorAlpha, sc.Color.R, sc.Color.G, sc.Color.B))
            : ink;
        var t = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, FieldPicture.IndicatorSize, secondary);
        ctx.DrawText(t, new Point(w - LegendInset - t.Width, top));
    }

    /// <summary>The legend stack; returns where it ends (the indicator's top).</summary>
    private static double Legends(DrawingContext ctx, Viewer3DViewModel vm, double w, double h, IBrush ink, bool dark)
    {
        var groups = vm.FieldLegendGroups;
        if (groups.Count == 0) return LegendTop;
        const double barW = 220, barH = 12, pad = 8, line = 16, gap = 8, top = LegendTop;
        var texts = groups.Select(g => g.Lines.Select(l => new FormattedText(l, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                                                              Typeface.Default, 12, ink)).ToList()).ToList();
        double widest = texts.Max(t => t.Count == 0 ? 0 : t.Max(x => x.Width));
        double bw = vm.HeldLegendWidth(Math.Max(barW, widest)) + 2 * pad;
        double Height(int k, bool more) => 2 * pad + barH + line * (texts[k].Count + 1 + (more ? 1 : 0));
        int shown = FieldPicture.StackCount([.. Enumerable.Range(0, groups.Count).Select(k => (float)Height(k, false))], (float)line,
                                            (float)gap, (float)top, (float)(h - top));
        var fill = dark ? LegendFillDark : LegendFillLight;
        double x0 = w - bw - LegendInset, y0 = top;
        for (int k = 0; k < shown; k++)
        {
            var g = groups[k];
            bool more = k == shown - 1 && shown < groups.Count;
            double bh = Height(k, more);
            ctx.FillRectangle(fill, new Rect(x0, y0, bw, bh), 4);
            double y = y0 + pad;
            if (texts[k].Count > 0) ctx.DrawText(texts[k][0], new Point(x0 + pad, y));
            y += line + 2;
            ctx.FillRectangle(LegendBar(g.Map), new Rect(x0 + pad, y, barW, barH));
            y += barH + 2;
            var lo = new FormattedText(FieldColorScale.G(g.Scale.Lo), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
            var hi = new FormattedText(FieldColorScale.G(g.Scale.Hi), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
            ctx.DrawText(lo, new Point(x0 + pad, y));
            ctx.DrawText(hi, new Point(x0 + pad + barW - hi.Width, y));
            y += line;
            for (int i = 1; i < texts[k].Count; i++, y += line) ctx.DrawText(texts[k][i], new Point(x0 + pad, y));
            if (more)
                ctx.DrawText(new FormattedText(FieldPicture.MoreLine(groups.Count - shown), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                               Typeface.Default, 12, ink), new Point(x0 + pad, y));
            y0 += bh + gap;
        }
        return shown > 0 ? y0 - gap + FieldPicture.IndicatorGap : top;
    }

    /// <summary>The axis indicator's centre in a view <paramref name="height"/> DIPs tall.</summary>
    public static Point AxisIndicatorCentre(double height) => new(AxisInset, height - AxisInset);

    /// <summary>
    /// 3D editor bugs round 2 — what a double-click on the axis indicator asks for, or null when <paramref name="p"/> is
    /// not on it. An axis (its arm or its letter) looks straight down that axis, the convention of the CAD tools' view
    /// triads: Z the Top view, Y the Front (the XZ plane — Front looks along +y), X the Right (the YZ plane). Asked again
    /// from that view it turns to the opposite one (Bottom, Back, Left) — an axis seen end-on is the dot at the ring's
    /// centre, so Top, double-clicked there, turns to Bottom. Anywhere else inside the ring is Isometric.
    /// <para>3D editor bugs round 3 — an arm in the view plane is drawn full length, its letter centred on the ring
    /// (AxisArm + 8), so half the letter lay outside the old reach (AxisArm + 10 from the centre, AxisArm + 12 along the
    /// arm): from the Right view a double-click on the Z letter asked for nothing. The reach now covers the whole letter.</para>
    /// </summary>
    public static StandardView3D? AxisIndicatorHit(in Camera3D cam, Point o, Point p)
    {
        var d = p - o;
        if (d.X * d.X + d.Y * d.Y > AxisHitRadius * AxisHitRadius) return null;
        var r = cam.Right; var u = cam.Up; var f = cam.Forward;
        Span<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        int best = -1;
        double bestDist = AxisLetterHalf, bestDepth = double.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            var dir = new Point(Vector3.Dot(r, axes[k]), -Vector3.Dot(u, axes[k]));
            double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            double dist;
            if (len < 0.2) dist = Math.Sqrt(d.X * d.X + d.Y * d.Y);   // seen end-on: a dot at the centre
            else
            {
                // Along the arm and out to its letter (drawn 8 DIPs past the tip): the distance to that segment. The
                // first 6 DIPs are left to an axis seen end-on, whose dot is there.
                double reach = AxisArm * len + 8 + AxisLetterHalf;
                var unit = new Point(dir.X / len, dir.Y / len);
                double t = Math.Clamp(d.X * unit.X + d.Y * unit.Y, 6, reach);
                double dx = d.X - unit.X * t, dy = d.Y - unit.Y * t;
                dist = Math.Sqrt(dx * dx + dy * dy);
            }
            double depth = Vector3.Dot(f, axes[k]);            // nearer the viewer wins a tie, as it is drawn on top
            if (dist < bestDist || (dist == bestDist && depth < bestDepth)) { best = k; bestDist = dist; bestDepth = depth; }
        }
        if (best < 0) return StandardView3D.Iso;
        // Already looking down this axis: the opposite view.
        var back = cam.Back;
        return best switch
        {
            0 => Vector3.Dot(back, Vector3.UnitX) > 0.999f ? StandardView3D.Left : StandardView3D.Right,
            1 => Vector3.Dot(back, -Vector3.UnitY) > 0.999f ? StandardView3D.Back : StandardView3D.Front,
            _ => Vector3.Dot(back, Vector3.UnitZ) > 0.999f ? StandardView3D.Bottom : StandardView3D.Top,
        };
    }

    private static void AxisIndicator(DrawingContext ctx, in Camera3D cam, Point o, IBrush ink)
    {
        var r = cam.Right; var u = cam.Up; var f = cam.Forward;
        Span<(Vector3 Axis, IBrush Brush, string Name)> axes =
            [(Vector3.UnitX, XBrush, "X"), (Vector3.UnitY, YBrush, "Y"), (Vector3.UnitZ, ZBrush, "Z")];
        // Farthest first, so the axis pointing at the viewer is drawn on top.
        Span<float> depth = [Vector3.Dot(f, axes[0].Axis), Vector3.Dot(f, axes[1].Axis), Vector3.Dot(f, axes[2].Axis)];
        Span<int> order = [0, 1, 2];
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                if (depth[order[j]] > depth[order[i]]) (order[i], order[j]) = (order[j], order[i]);
        ctx.DrawEllipse(null, new Pen(ink, 0.6) { DashStyle = DashStyle.Dot }, o, AxisArm + 8, AxisArm + 8);
        foreach (int k in order)
        {
            var (axis, brush, name) = axes[k];
            var tip = new Point(o.X + AxisArm * Vector3.Dot(r, axis), o.Y - AxisArm * Vector3.Dot(u, axis));
            ctx.DrawLine(new Pen(brush, 2.5, lineCap: PenLineCap.Round), o, tip);
            var ft = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
            var d = tip - o;
            double len = Math.Max(1e-6, Math.Sqrt(d.X * d.X + d.Y * d.Y));
            var at = tip + new Point(d.X / len * 8, d.Y / len * 8) - new Point(ft.Width / 2, ft.Height / 2);
            ctx.DrawText(ft, at);
        }
    }

    /// <summary>The scale bar's length, metres: <see cref="Em3dDrawingSheet.ScaleBarLength"/>, the rule `render --scale-bar` shares
    /// (brief-em3d-88).</summary>
    internal static double ScaleBarLength(double targetMetres, LayoutUnit unit, int dbuPerMicron)
        => Em3dDrawingSheet.ScaleBarLength(targetMetres, unit, dbuPerMicron);

    private static void ScaleBar(DrawingContext ctx, in Camera3D cam, Viewer3DViewModel vm, double w, double h, Pen pen, IBrush ink)
    {
        double perDip = cam.WorldPerPixel((float)h);
        if (!(perDip > 0) || double.IsInfinity(perDip)) return;
        double len = ScaleBarLength(ScaleBarTarget * perDip, vm.MeasureUnit, vm.MeasureDbuPerMicron);
        double dips = len / perDip;
        var right = new Point(w - 20, h - 22);
        var left = new Point(right.X - dips, right.Y);
        ctx.DrawLine(pen, left, right);
        ctx.DrawLine(pen, left + new Point(0, -5), left + new Point(0, 5));
        ctx.DrawLine(pen, right + new Point(0, -5), right + new Point(0, 5));
        var ft = new FormattedText(vm.FormatLength(len), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
        ctx.DrawText(ft, new Point((left.X + right.X - ft.Width) / 2, right.Y - ft.Height - 5));
        if (cam.Projection == Projection3D.Perspective)
        {
            var note = new FormattedText("at the orbit centre", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, ink);
            ctx.DrawText(note, new Point(right.X - note.Width, right.Y + 4));
        }
    }

    /// <summary>
    /// General designer feedback round 14 — the drawing's labels, placed by <see cref="Viewer3DLabelLayout"/> so none covers
    /// another; one moved away from its point is joined back to it by a thin leader line. The box and the text are
    /// <see cref="Text"/>'s. On screen each box follows its laid-out place on <paramref name="springs"/>, so a label changing
    /// side as the camera turns swings over rather than jumping; a picture (no springs) draws the laid-out place.
    /// </summary>
    private static void DrawingLabels(DrawingContext ctx, List<(Point At, string Text)> labels, double w, double h, bool dark,
                                      Viewer3DLabelSpring? springs, string channel)
    {
        if (labels.Count == 0) return;
        IBrush ink = dark ? Brushes.White : Brushes.Black;
        var texts = labels.Select(l => new FormattedText(l.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink)).ToList();
        var placed = Viewer3DLabelLayout.Place([.. labels.Select(l => l.At)], [.. texts.Select(t => new Size(t.Width + 8, t.Height + 6))], w, h);
        var boxes = springs is null
            ? placed
            : springs.Follow(channel, [.. labels.Select(l => l.At)], [.. labels.Select(l => l.Text)], [.. placed.Select(p => p.Box)])
                .Select((box, i) => (Box: box, Leader: !Viewer3DLabelLayout.Touches(box, labels[i].At))).ToArray();
        var leader = new Pen(dark ? new SolidColorBrush(Color.FromArgb(200, 220, 222, 228)) : new SolidColorBrush(Color.FromArgb(200, 60, 64, 72)), 1);
        var background = new SolidColorBrush(dark ? Color.FromArgb(200, 30, 32, 36) : Color.FromArgb(215, 250, 250, 252));
        for (int i = 0; i < labels.Count; i++)
            if (boxes[i].Leader) ctx.DrawLine(leader, labels[i].At, Viewer3DLabelLayout.NearestOnEdge(boxes[i].Box, labels[i].At));
        for (int i = 0; i < labels.Count; i++)
        {
            ctx.FillRectangle(background, boxes[i].Box, 3);
            ctx.DrawText(texts[i], boxes[i].Box.TopLeft + new Point(4, 3));
        }
    }

    private static void Text(DrawingContext ctx, string text, Point at, IBrush ink, double size, bool dark)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, ink);
        var box = new Rect(at.X - 4, at.Y - 3, ft.Width + 8, ft.Height + 6);
        ctx.FillRectangle(new SolidColorBrush(dark ? Color.FromArgb(200, 30, 32, 36) : Color.FromArgb(215, 250, 250, 252)), box, 3);
        ctx.DrawText(ft, at);
    }
}

/// <summary>
/// A toolbar glyph for a standard view: an isometric cube with the viewed face filled. The three faces
/// the iso view shows (top, front, right) are drawn with the cube's near corner; the three it hides
/// (bottom, back, left) with its far corner, so every face of the family is a distinct picture. No
/// Material icon says "look at this face", which is why this one is drawn (owner, 2026-09-25).
/// </summary>
public sealed class Viewer3DViewGlyph : Control
{
    public static readonly StyledProperty<StandardView3D> FaceProperty =
        AvaloniaProperty.Register<Viewer3DViewGlyph, StandardView3D>(nameof(Face));

    public StandardView3D Face { get => GetValue(FaceProperty); set => SetValue(FaceProperty, value); }

    static Viewer3DViewGlyph() => AffectsRender<Viewer3DViewGlyph>(FaceProperty, TextElement.ForegroundProperty);

    public Viewer3DViewGlyph() { Width = 16; Height = 16; }

    // The hexagon of an isometric cube in a 24-unit box; C is where the near (and far) corner lands.
    private static readonly Point T = new(12, 2), UR = new(21, 7), LR = new(21, 17), B = new(12, 22), LL = new(3, 17), UL = new(3, 7), C = new(12, 12);

    public override void Render(DrawingContext ctx)
    {
        var fg = TextElement.GetForeground(this) ?? Brushes.Gray;
        double s = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        Point P(Point p) => new(p.X * s, p.Y * s);
        Point[] face = Face switch
        {
            StandardView3D.Top    => [C, UL, T, UR],
            StandardView3D.Front  => [C, UL, LL, B],
            StandardView3D.Right  => [C, UR, LR, B],
            StandardView3D.Bottom => [C, LL, B, LR],
            StandardView3D.Back   => [C, T, UR, LR],
            StandardView3D.Left   => [C, T, UL, LL],
            _                     => [],
        };
        bool hidden = Face is StandardView3D.Bottom or StandardView3D.Back or StandardView3D.Left;
        if (face.Length > 0)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(face[0]), true);
                for (int i = 1; i < face.Length; i++) c.LineTo(P(face[i]));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(fg, null, g);
        }
        var pen = new Pen(fg, 1.6 * s, lineJoin: PenLineJoin.Round);
        Point[] hex = [T, UR, LR, B, LL, UL];
        for (int i = 0; i < 6; i++) ctx.DrawLine(pen, P(hex[i]), P(hex[(i + 1) % 6]));
        // The three edges from the near corner (visible faces) or the far corner (hidden faces).
        var dashed = new Pen(fg, 1.2 * s) { DashStyle = hidden ? new DashStyle([1.5, 1.5], 0) : null };
        foreach (var e in hidden ? new[] { T, LL, LR } : [UL, UR, B])
            ctx.DrawLine(dashed, P(C), P(e));
    }
}

/// <summary>
/// A toolbar glyph drawn from a path in Material's 24-unit box, filled with the inherited foreground, so it sits beside
/// the Material icons at their size, padding and weight and follows a button's colour as they do. For a shape the icon
/// set draws badly: its nearest cylinder is <c>Database</c>, a stack of discs (owner, 2026-10-03), and it has no sphere.
/// </summary>
public sealed class Viewer3DPathGlyph : Control
{
    /// <summary>An outlined cylinder: the top as a full ellipse, the sides, the bottom's front half; stroke 2, even-odd.
    /// Drawn in Skia as strokes and written back as exact arcs (a stroke's fill path comes out of Skia as one quadratic per
    /// quarter-ellipse, which is visibly flat at 64 px).</summary>
    public const string Cylinder =
        "M4,5.4 A8,3.6 0 0 1 20,5.4 L20,18.6 A8,3.6 0 0 1 4,18.6 Z " +
        "M6,5.4 A6,1.6 0 1 0 18,5.4 A6,1.6 0 1 0 6,5.4 Z " +
        "M6,7.78 A8,3.6 0 0 0 18,7.78 L18,18.6 A6,1.6 0 0 1 6,18.6 Z";

    /// <summary>brief-em3d-102 (owner decision D5) — an outlined sphere: the outline circle and the front half of its equator, at
    /// the icon set's 2-unit stroke, as exact arcs; even-odd, and tilted as the cylinder's caps are (ry 3.6 and 1.6). The band
    /// lies between two half-ellipses 2 units apart at its middle, wholly inside the ring's hole, and meets the ring's inner edge
    /// at its two ends — so nothing overlaps and even-odd cuts no notch. Not <c>CircleOutline</c>: that is the 2D circle, and would read as a disc.</summary>
    public const string Sphere =
        "M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 Z " +
        "M5,12 A7,7 0 1 0 19,12 A7,7 0 1 0 5,12 Z " +
        "M19,12 A7,3.6 0 0 1 5,12 A7,1.6 0 0 0 19,12 Z";

    /// <summary>Object mode: the Box tool's <c>CubeOutline</c> with a 3.2-unit square on each of its seven corners — the cube read
    /// as something whose vertices can be picked, subtle enough to stay the same object (owner, 2026-10-03). Skia's union of the
    /// two, rounded to 0.01; the squares cover the outline's rounded corners, so it is all straight lines.</summary>
    public const string ObjectMode =
        "M13.6 1.5L10.4 1.5L10.4 2.76L4.01 6.35L2.4 6.35L2.4 9.55L3 9.55L3 14.45L2.4 14.45L2.4 17.65L4.01 17.65L10.4 21.24" +
        "L10.4 22.5L13.6 22.5L13.6 21.24L19.99 17.65L21.6 17.65L21.6 14.45L21 14.45L21 9.55L21.6 9.55L21.6 6.35L19.99 6.35" +
        "L13.6 2.76L13.6 1.5Z" +
        "M11.02 4.7L12.98 4.7L17.96 7.5L13.33 10.1L10.67 10.1L6.04 7.5L11.02 4.7Z" +
        "M10.4 12.24L5.6 9.55L5 9.55L5 14.45L5.6 14.45L5.6 16.25L11 19.29L11 13.3L10.4 13.3L10.4 12.24Z" +
        "M13 13.3L13 19.29L18.4 16.25L18.4 14.45L19 14.45L19 9.55L18.4 9.55L13.6 12.24L13.6 13.3L13 13.3Z";

    /// <summary>The isometric view: the same cube with the three faces an isometric view shows FILLED, parted by 1.4-unit gaps
    /// along its inner edges so they read as three faces and not a hexagon (owner, 2026-10-03). The outline is
    /// <c>CubeOutline</c>'s, sharp-cornered.</summary>
    public const string Isometric =
        "M20.33 7.09L12 2L3.67 7.09L12 12.18L20.33 7.09Z" +
        "M21 8.32L12.7 13.39L12.7 21.57L21 16.5L21 8.32Z" +
        "M11.3 21.57L11.3 13.39L3 8.32L3 16.5L11.3 21.57Z";

    /// <summary>brief-sar-3 — an outlined ellipsoid: the sphere's glyph pressed flat (rx 9, ry 6), its equator as the sphere's
    /// is drawn, so the two read as one family.</summary>
    public const string Ellipsoid =
        "M3,12 A9,6 0 1 1 21,12 A9,6 0 1 1 3,12 Z " +
        "M5,12 A7,4 0 1 0 19,12 A7,4 0 1 0 5,12 Z " +
        "M19,12 A7,2.4 0 0 1 5,12 A7,1.2 0 0 0 19,12 Z";

    /// <summary>brief-sar-3 R-sar3-3 — a capacitor: two plates across a gap, a lead to each.</summary>
    public const string Capacitor =
        "M2,11 H9 V13 H2 Z M15,11 H22 V13 H15 Z M8,4 H10 V20 H8 Z M14,4 H16 V20 H14 Z";

    /// <summary>The constant a tool table names (<c>nameof(Cylinder)</c> …), or null for a Material icon's kind.</summary>
    public static string? Named(string name) => name switch
    {
        nameof(Cylinder) => Cylinder,
        nameof(Sphere) => Sphere,
        nameof(Ellipsoid) => Ellipsoid,
        nameof(Capacitor) => Capacitor,
        _ => null,
    };

    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<Viewer3DPathGlyph, string?>(nameof(Data));

    public string? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    private Geometry? _geometry;
    private string? _parsed;

    static Viewer3DPathGlyph() => AffectsRender<Viewer3DPathGlyph>(DataProperty, TextElement.ForegroundProperty);

    public Viewer3DPathGlyph() { Width = 16; Height = 16; }

    public override void Render(DrawingContext ctx)
    {
        if (string.IsNullOrEmpty(Data)) return;
        if (!ReferenceEquals(_parsed, Data)) { _geometry = Geometry.Parse(Data); _parsed = Data; }
        double s = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        using (ctx.PushTransform(Matrix.CreateScale(s, s)))
            ctx.DrawGeometry(TextElement.GetForeground(this) ?? Brushes.Gray, null, _geometry!);
    }
}
