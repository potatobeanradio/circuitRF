// brief-em3d-28 R-em3d28-4e — the 2D chrome over the 3D pane: the AXIS INDICATOR in the lower-left
// corner (the toolbar and the A key turn it off), a SCALE BAR in the layout's display unit, the FDTD
// grid's smallest-cell labels (R-em3d28-3c), and the hover label naming what is under the cursor with
// its material values (R-em3d28-4b).
//
// It is drawn by Avalonia, in DIPs, from the camera alone — a redraw per presented frame is a handful of
// lines and a few text runs, and it touches no geometry. It takes no input: the pane under it does.

using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DOverlay : Control
{
    /// <summary>The axis indicator's arm length and its centre's inset from the corner, DIPs.</summary>
    public const double AxisArm = 30, AxisInset = 46;

    /// <summary>The scale bar aims for about this many DIPs.</summary>
    public const double ScaleBarTarget = 110;

    private static readonly IBrush XBrush = new SolidColorBrush(Color.FromRgb(220, 60, 60));
    private static readonly IBrush YBrush = new SolidColorBrush(Color.FromRgb(60, 170, 70));
    private static readonly IBrush ZBrush = new SolidColorBrush(Color.FromRgb(60, 110, 230));

    public Viewer3DOverlay() => IsHitTestVisible = false;

    private Viewer3DViewModel? Vm => DataContext as Viewer3DViewModel;

    public override void Render(DrawingContext ctx)
    {
        if (Vm is not { } vm) return;
        bool dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        IBrush ink = dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(35, 38, 44));
        var inkPen = new Pen(ink, 1.5);
        var cam = vm.View.Camera;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 10) return;

        if (vm.View.ShowAxisIndicator) AxisIndicator(ctx, cam, new Point(AxisInset, h - AxisInset), ink);
        ScaleBar(ctx, cam, vm, w, h, inkPen, ink);

        foreach (var label in vm.GridLabels)
        {
            var (x, y, visible) = cam.Project(label.At, (float)w, (float)h);
            if (!visible) continue;
            Text(ctx, label.Text, new Point(x + 6, y - 18), ink, 11, dark);
        }

        if (vm.FieldLegendVisible) Legend(ctx, vm, w, ink, dark);

        if (vm.HoverText.Length > 0 && vm.View.CursorX >= 0)
            Text(ctx, vm.HoverText, new Point(vm.View.CursorX + 14, vm.View.CursorY + 14), ink, 12, dark);
    }

    /// <summary>brief-em3d-29 R-em3d29-3c — the field's legend, top right: the quantity, a colour bar with the
    /// range at its ends, the range's percentile, the solution, and the phase when animated.</summary>
    private static void Legend(DrawingContext ctx, Viewer3DViewModel vm, double w, IBrush ink, bool dark)
    {
        var lines = vm.FieldLegendLines();
        if (lines.Count == 0 || vm.FieldScale is not { } range) return;
        const double barW = 220, barH = 12, pad = 8, line = 16;
        var texts = lines.Select(l => new FormattedText(l, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, ink)).ToList();
        double bw = Math.Max(barW, texts.Max(t => t.Width)) + 2 * pad;
        double bh = 2 * pad + barH + line * (lines.Count + 1);
        double x0 = w - bw - 12, y0 = 12;
        ctx.FillRectangle(new SolidColorBrush(dark ? Color.FromArgb(215, 28, 30, 34) : Color.FromArgb(225, 250, 250, 252)),
                          new Rect(x0, y0, bw, bh), 4);
        double y = y0 + pad;
        ctx.DrawText(texts[0], new Point(x0 + pad, y));
        y += line + 2;
        var stops = new GradientStops();
        foreach (var (t, r, g, b) in vm.FieldMap.Stops) stops.Add(new GradientStop(Color.FromRgb(r, g, b), t));
        var bar = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = stops,
        };
        ctx.FillRectangle(bar, new Rect(x0 + pad, y, barW, barH));
        y += barH + 2;
        var lo = new FormattedText(FieldColorScale.G(range.Lo), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
        var hi = new FormattedText(FieldColorScale.G(range.Hi), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
        ctx.DrawText(lo, new Point(x0 + pad, y));
        ctx.DrawText(hi, new Point(x0 + pad + barW - hi.Width, y));
        y += line;
        for (int i = 1; i < texts.Count; i++, y += line) ctx.DrawText(texts[i], new Point(x0 + pad, y));
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

    private static void ScaleBar(DrawingContext ctx, in Camera3D cam, Viewer3DViewModel vm, double w, double h, Pen pen, IBrush ink)
    {
        double perDip = cam.WorldPerPixel((float)h);
        if (!(perDip > 0) || double.IsInfinity(perDip)) return;
        double target = ScaleBarTarget * perDip, p10 = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double len = new[] { 1.0, 2.0, 5.0, 10.0 }.Select(m => m * p10).Last(v => v <= target * 1.4);
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
