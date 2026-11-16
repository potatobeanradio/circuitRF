// brief-paa-9 R-paa9-8 — the Phased Array tool's renderers: the aperture, the (u,v) map, the scan map, the principal cuts and a small line
// chart. Framework-free (SkiaSharp only), so the window, UiSnapshot and DocGen draw them with the same code. Each takes a plain scene of
// numbers and returns, where the window hit-tests, the layout it drew with — pixels to elements, pixels to direction cosines — so a click
// is answered by the same arithmetic that placed what was clicked.

using CircuitRF.Render.DataDisplay;
using SkiaSharp;

namespace CircuitRF.Render.PhasedArray;

/// <summary>Colour scales: a perceptually ordered sequential one, and a cyclic one for phase.</summary>
public static class ArrayColorMap
{
    // viridis at nine stops
    private static readonly (byte R, byte G, byte B)[] Sequential =
    [
        (68, 1, 84), (71, 44, 122), (59, 81, 139), (44, 113, 142), (33, 144, 141), (39, 173, 129), (92, 200, 99), (170, 220, 50), (253, 231, 37),
    ];

    /// <summary>The sequential colour at <paramref name="t"/> ∈ [0, 1].</summary>
    public static SKColor At(double t)
    {
        if (double.IsNaN(t)) return SKColors.Transparent;
        t = Math.Clamp(t, 0, 1) * (Sequential.Length - 1);
        int i = Math.Min((int)t, Sequential.Length - 2);
        double f = t - i;
        var (a, b) = (Sequential[i], Sequential[i + 1]);
        return new SKColor((byte)(a.R + f * (b.R - a.R)), (byte)(a.G + f * (b.G - a.G)), (byte)(a.B + f * (b.B - a.B)));
    }

    /// <summary>The cyclic colour at <paramref name="t"/> (period 1): a hue wheel at moderate lightness.</summary>
    public static SKColor Cyclic(double t)
    {
        if (double.IsNaN(t)) return SKColors.Transparent;
        double h = (t - Math.Floor(t)) * 360;
        return SKColor.FromHsl((float)h, 65, 52);
    }
}

/// <summary>R-paa9-2 — the aperture: one cell per element at its position, coloured by a value.</summary>
public sealed record ApertureScene(int Rows, int Cols, double Dx, double Dy, double[] X, double[] Y, double[] Values, bool[] Off,
                                   double Min, double Max, bool Cyclic, string Title, string Unit)
{
    public IReadOnlySet<int> Selected { get; init; } = new HashSet<int>();

    /// <summary>The element under the pointer, 0-based; −1 for none.</summary>
    public int Hover { get; init; } = -1;

    /// <summary>A rectangle being dragged, in pixels.</summary>
    public SKRect? Marquee { get; init; }
}

/// <summary>Where an aperture was drawn: metres to pixels.</summary>
public sealed record ApertureLayout(double Scale, double CentreX, double CentreY, double Half, double[] X, double[] Y)
{
    /// <summary>The element whose cell holds pixel (<paramref name="px"/>, <paramref name="py"/>), 0-based, or −1.</summary>
    public int HitTest(double px, double py)
    {
        int best = -1;
        double bestD = double.PositiveInfinity;
        for (int e = 0; e < X.Length; e++)
        {
            double dx = Math.Abs(CentreX + X[e] * Scale - px), dy = Math.Abs(CentreY - Y[e] * Scale - py);
            if (dx <= Half && dy <= Half && dx + dy < bestD) { best = e; bestD = dx + dy; }
        }
        return best;
    }

    /// <summary>The elements whose centres lie in <paramref name="rect"/>, 0-based.</summary>
    public IEnumerable<int> Within(SKRect rect)
    {
        for (int e = 0; e < X.Length; e++)
            if (rect.Contains((float)(CentreX + X[e] * Scale), (float)(CentreY - Y[e] * Scale))) yield return e;
    }
}

/// <summary>R-paa9-3 — the (u,v) map: a grid of realized gain in dB, and what is drawn over it.</summary>
/// <param name="Db">[iv·Nu + iu] at u = (iu − (Nu−1)/2)·Du, v = ((Nv−1)/2 − iv)·Dv; NaN outside the visible circle.</param>
public sealed record UvScene(float[] Db, int Nu, int Nv, double Du, double Dv, double TopDb, double RangeDb, string Unit)
{
    public (double U, double V)? Beam { get; init; }
    public bool BeamDraggable { get; init; }
    public IReadOnlyList<(double U, double V)> GratingLobes { get; init; } = [];

    /// <summary>sin of the scan limit; 0 hides it.</summary>
    public double ScanLimitSin { get; init; }

    /// <summary>brief-pch-9 R-pch9-7 — a channel's rays at this end (u, v) with their size, 0 (weakest drawn) to 1 (strongest).</summary>
    public IReadOnlyList<(double U, double V, double Size)> Rays { get; init; } = [];

    /// <summary>brief-pch-9 — the codebook's beams at this end, and the one the channel selects.</summary>
    public IReadOnlyList<(double U, double V)> CodebookBeams { get; init; } = [];
    public (double U, double V)? SelectedBeam { get; init; }
}

/// <summary>Where a (u,v) picture was drawn.</summary>
public sealed record UvLayout(double CentreX, double CentreY, double Radius)
{
    public (double U, double V) ToUv(double px, double py) => ((px - CentreX) / Radius, (CentreY - py) / Radius);
    public (float X, float Y) ToPixel(double u, double v) => ((float)(CentreX + u * Radius), (float)(CentreY - v * Radius));
}

/// <summary>R-paa9-5 — a scan map: one value per steering direction (θ₀, φ₀), each a cell in (u,v).</summary>
public sealed record ScanScene(double[] ThetaDeg, double[] PhiDeg, double[] Values, double ThetaStepDeg, double PhiStepDeg, double Min, double Max,
                               string Title, string Unit, bool Reversed);

/// <summary>R-paa9-4 — one principal cut's traces, dB over the beam's peak.</summary>
public sealed record CutTraces(string Plane, double[] OffsetDeg, double[] CoDb, double[] CrossDb, double[] IsotropicDb);

/// <summary>A series for the line chart.</summary>
public sealed record ChartSeries(double[] X, double[] Y, SKColor Color, string Label, bool Markers = false, bool Dashed = false);

/// <summary>brief-paa-9 — the Phased Array renderers.</summary>
public static class ArrayRenderers
{
    public static SKColor Accent(RenderTheme t) => t.DarkMode ? new SKColor(100, 190, 255) : new SKColor(0, 110, 200);
    public static SKColor Second(RenderTheme t) => t.DarkMode ? new SKColor(255, 170, 80) : new SKColor(210, 100, 0);
    private static SKColor Dim(RenderTheme t) => t.DarkMode ? new SKColor(150, 150, 150, 170) : new SKColor(120, 120, 120, 150);
    private static SKColor Warn(RenderTheme t) => t.DarkMode ? new SKColor(255, 110, 100) : new SKColor(200, 30, 30);

    private static SKFont Font(float size) => new(SkiaFonts.DejaVuRegular, size);

    private static SKPaint Stroke(SKColor c, float w, bool dashed = false) => new()
    {
        Color = c, StrokeWidth = w, Style = SKPaintStyle.Stroke, IsAntialias = true,
        PathEffect = dashed ? SKPathEffect.CreateDash([5, 4], 0) : null,
    };

    private static SKPaint Fill(SKColor c) => new() { Color = c, Style = SKPaintStyle.Fill, IsAntialias = true };

    private static void Text(SKCanvas c, string s, float x, float y, SKColor color, float size = 11, SKTextAlign align = SKTextAlign.Left)
    {
        using var f = Font(size);
        using var p = Fill(color);
        c.DrawText(s, x, y, align, f, p);
    }

    /// <summary>A vertical colour bar at <paramref name="r"/>, <paramref name="min"/> at the bottom.</summary>
    private static void ColorBar(SKCanvas c, SKRect r, double min, double max, bool cyclic, string unit, RenderTheme theme, bool reversed = false)
    {
        int n = Math.Max(2, (int)r.Height);
        for (int i = 0; i < n; i++)
        {
            double t = 1 - i / (double)(n - 1);
            using var p = Fill(cyclic ? ArrayColorMap.Cyclic(t) : ArrayColorMap.At(reversed ? 1 - t : t));
            c.DrawRect(r.Left, r.Top + i * r.Height / n, r.Width, r.Height / n + 1, p);
        }
        using var border = Stroke(theme.BorderColor, 1);
        c.DrawRect(r, border);
        string F(double v) => Math.Abs(max - min) < 10 ? v.ToString("0.##") : v.ToString("0");
        Text(c, F(max), r.Right + 4, r.Top + 9, theme.TextColor, 10);
        Text(c, F(min), r.Right + 4, r.Bottom, theme.TextColor, 10);
        Text(c, unit, r.Right + 4, r.MidY + 4, theme.TextColor, 10);
    }

    // ── the aperture ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Draws the aperture in <paramref name="bounds"/>, seen from the front (+z): rows down y from the top, columns along x from the
    /// left. An element switched off is drawn hollow.</summary>
    public static ApertureLayout DrawAperture(SKCanvas c, SKRect bounds, ApertureScene s, RenderTheme theme)
    {
        using (var bg = Fill(theme.BackgroundColor)) c.DrawRect(bounds, bg);
        Text(c, s.Title, bounds.Left + 8, bounds.Top + 16, theme.TextColor, 12);
        var plot = new SKRect(bounds.Left + 8, bounds.Top + 24, bounds.Right - 64, bounds.Bottom - 8);
        if (s.X.Length == 0 || plot.Width < 10 || plot.Height < 10) return new ApertureLayout(1, plot.MidX, plot.MidY, 0, [], []);
        double minX = s.X.Min(), maxX = s.X.Max(), minY = s.Y.Min(), maxY = s.Y.Max();
        double cell = Math.Min(s.Dx > 0 ? s.Dx : 1, s.Dy > 0 ? s.Dy : 1);
        if (s.Cols == 1) cell = s.Dy > 0 ? s.Dy : cell;
        if (s.Rows == 1) cell = s.Dx > 0 ? s.Dx : cell;
        double w = maxX - minX + cell, h = maxY - minY + cell;
        double scale = Math.Min(plot.Width / w, plot.Height / h);
        double cx = plot.MidX - (minX + maxX) / 2 * scale, cy = plot.MidY + (minY + maxY) / 2 * scale;
        double half = 0.5 * cell * scale;
        float side = (float)Math.Max(1, 0.86 * cell * scale);
        var layout = new ApertureLayout(scale, cx, cy, half, s.X, s.Y);
        double span = s.Max - s.Min;
        using var hollow = Stroke(theme.TextColor, Math.Max(1f, side / 8));
        using var selected = Stroke(Accent(theme), Math.Max(1.5f, side / 6));
        using var hover = Stroke(theme.TextColor, Math.Max(1.5f, side / 6));
        for (int e = 0; e < s.X.Length; e++)
        {
            float x = (float)(cx + s.X[e] * scale), y = (float)(cy - s.Y[e] * scale);
            var r = new SKRect(x - side / 2, y - side / 2, x + side / 2, y + side / 2);
            if (s.Off[e]) { c.DrawRect(r, hollow); continue; }
            double v = s.Values[e];
            double t = double.IsNaN(v) ? double.NaN : s.Cyclic ? (v - s.Min) / (span == 0 ? 1 : span) : span == 0 ? 1 : (v - s.Min) / span;
            using (var p = Fill(double.IsNaN(t) ? Dim(theme) : s.Cyclic ? ArrayColorMap.Cyclic(t) : ArrayColorMap.At(t))) c.DrawRect(r, p);
            if (s.Selected.Contains(e)) c.DrawRect(r, selected);
        }
        if (s.Hover >= 0 && s.Hover < s.X.Length)
        {
            float x = (float)(cx + s.X[s.Hover] * scale), y = (float)(cy - s.Y[s.Hover] * scale);
            c.DrawRect(new SKRect(x - side / 2 - 1, y - side / 2 - 1, x + side / 2 + 1, y + side / 2 + 1), hover);
        }
        if (s.Marquee is { } m)
        {
            using var fill = Fill(Accent(theme).WithAlpha(40));
            using var line = Stroke(Accent(theme), 1, dashed: true);
            c.DrawRect(m, fill);
            c.DrawRect(m, line);
        }
        ColorBar(c, new SKRect(bounds.Right - 54, plot.Top + 10, bounds.Right - 42, plot.Bottom - 10), s.Min, s.Max, s.Cyclic, s.Unit, theme);
        return layout;
    }

    // ── the (u,v) map ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Draws the (u,v) map: realized gain over the top <see cref="UvScene.RangeDb"/> dB, the unit circle of real space, the scan
    /// limit, the grating lobes for the current steer (red inside real space) and the beam marker (hollow when it cannot be dragged).</summary>
    /// <summary>brief-pch-9 — the radius (px) of the weakest and the strongest ray drawn on the (u,v) map.</summary>
    public const double RayMinRadius = 2, RayMaxRadius = 7;

    public static UvLayout DrawUvMap(SKCanvas c, SKRect bounds, UvScene s, RenderTheme theme)
    {
        using (var bg = Fill(theme.BackgroundColor)) c.DrawRect(bounds, bg);
        var plot = new SKRect(bounds.Left + 22, bounds.Top + 8, bounds.Right - 64, bounds.Bottom - 22);
        double radius = Math.Min(plot.Width, plot.Height) / 2 / 1.08;
        var layout = new UvLayout(plot.MidX, plot.MidY, radius);
        if (s.Nu > 1 && s.Nv > 1 && s.Db.Length == s.Nu * s.Nv)
        {
            var info = new SKImageInfo(s.Nu, s.Nv, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bmp = new SKBitmap(info);
            var pixels = new SKColor[s.Nu * s.Nv];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = float.IsNaN(s.Db[i]) ? SKColors.Transparent : ArrayColorMap.At((s.Db[i] - (s.TopDb - s.RangeDb)) / s.RangeDb);
            bmp.Pixels = pixels;
            using var img = SKImage.FromBitmap(bmp);
            double halfU = (s.Nu - 1) / 2.0 * s.Du + s.Du / 2, halfV = (s.Nv - 1) / 2.0 * s.Dv + s.Dv / 2;
            var dest = new SKRect((float)(layout.CentreX - halfU * radius), (float)(layout.CentreY - halfV * radius),
                                  (float)(layout.CentreX + halfU * radius), (float)(layout.CentreY + halfV * radius));
            c.Save();
            using var clip = new SKPath();
            clip.AddCircle((float)layout.CentreX, (float)layout.CentreY, (float)radius);
            c.ClipPath(clip, antialias: true);
            c.DrawImage(img, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            c.Restore();
        }
        using var axis = Stroke(theme.GridColor, 1);
        c.DrawLine((float)(layout.CentreX - radius * 1.05), (float)layout.CentreY, (float)(layout.CentreX + radius * 1.05), (float)layout.CentreY, axis);
        c.DrawLine((float)layout.CentreX, (float)(layout.CentreY - radius * 1.05), (float)layout.CentreX, (float)(layout.CentreY + radius * 1.05), axis);
        using var circle = Stroke(theme.TextColor, 1.5f);
        c.DrawCircle((float)layout.CentreX, (float)layout.CentreY, (float)radius, circle);
        Text(c, "u", (float)(layout.CentreX + radius * 1.05), (float)layout.CentreY - 4, theme.TextColor, 11);
        Text(c, "v", (float)layout.CentreX + 4, (float)(layout.CentreY - radius * 1.04), theme.TextColor, 11);
        if (s.ScanLimitSin > 0 && s.ScanLimitSin < 1)
        {
            using var limit = Stroke(theme.TextColor, 1, dashed: true);
            c.DrawCircle((float)layout.CentreX, (float)layout.CentreY, (float)(radius * s.ScanLimitSin), limit);
        }
        foreach (var (u, v) in s.GratingLobes)
        {
            var (x, y) = layout.ToPixel(u, v);
            if (!plot.Contains(x, y)) continue;
            bool visible = u * u + v * v <= 1;
            using var p = Stroke(visible ? Warn(theme) : theme.TextColor, visible ? 2.2f : 1.2f);
            c.DrawLine(x - 5, y - 5, x + 5, y + 5, p);
            c.DrawLine(x - 5, y + 5, x + 5, y - 5, p);
        }
        // brief-pch-9 — the codebook's beams (small light dots), the rays (discs sized by power), then the channel's selected beam on top
        if (s.CodebookBeams.Count > 0)
        {
            using var dot = Fill(new SKColor(255, 255, 255, 190));
            using var rim = Stroke(new SKColor(0, 0, 0, 120), 0.8f);
            foreach (var (u, v) in s.CodebookBeams)
            {
                var (x, y) = layout.ToPixel(u, v);
                c.DrawCircle(x, y, 2.2f, dot);
                c.DrawCircle(x, y, 2.2f, rim);
            }
        }
        if (s.Rays.Count > 0)
        {
            using var fill = Fill(Second(theme).WithAlpha(200));
            using var edge = Stroke(theme.DarkMode ? SKColors.Black : SKColors.White, 0.8f);
            foreach (var (u, v, size) in s.Rays.OrderBy(r => r.Size))
            {
                var (x, y) = layout.ToPixel(u, v);
                float r = (float)(RayMinRadius + (RayMaxRadius - RayMinRadius) * Math.Clamp(size, 0, 1));
                c.DrawCircle(x, y, r, fill);
                c.DrawCircle(x, y, r, edge);
            }
        }
        if (s.SelectedBeam is { } sb)
        {
            var (x, y) = layout.ToPixel(sb.U, sb.V);
            using var fill = Fill(Accent(theme));
            using var edge = Stroke(SKColors.White, 2f);
            c.DrawCircle(x, y, 5.5f, fill);
            c.DrawCircle(x, y, 5.5f, edge);
        }
        if (s.Beam is { } b)
        {
            var (x, y) = layout.ToPixel(b.U, b.V);
            using var ring = Stroke(SKColors.White, 3);
            using var inner = Stroke(theme.DarkMode ? SKColors.Black : new SKColor(20, 20, 20), 1.5f, dashed: !s.BeamDraggable);
            c.DrawCircle(x, y, 8, ring);
            c.DrawCircle(x, y, 8, inner);
            if (s.BeamDraggable)
            {
                c.DrawLine(x - 12, y, x - 4, y, inner);
                c.DrawLine(x + 4, y, x + 12, y, inner);
                c.DrawLine(x, y - 12, x, y - 4, inner);
                c.DrawLine(x, y + 4, x, y + 12, inner);
            }
        }
        ColorBar(c, new SKRect(bounds.Right - 54, plot.Top + 10, bounds.Right - 42, plot.Bottom - 10), s.TopDb - s.RangeDb, s.TopDb, false, s.Unit, theme);
        return layout;
    }

    /// <summary>Draws a scan map: each steering direction a cell of the (u,v) disk, out to the furthest θ₀ scanned.</summary>
    public static UvLayout DrawScanMap(SKCanvas c, SKRect bounds, ScanScene s, RenderTheme theme)
    {
        using (var bg = Fill(theme.BackgroundColor)) c.DrawRect(bounds, bg);
        Text(c, s.Title, bounds.Left + 8, bounds.Top + 16, theme.TextColor, 12);
        var plot = new SKRect(bounds.Left + 8, bounds.Top + 24, bounds.Right - 64, bounds.Bottom - 8);
        double radius = Math.Min(plot.Width, plot.Height) / 2 / 1.04;
        var layout = new UvLayout(plot.MidX, plot.MidY, radius);
        double span = s.Max - s.Min;
        for (int i = 0; i < s.Values.Length; i++)
        {
            double t0 = Math.Max(0, s.ThetaDeg[i] - s.ThetaStepDeg / 2), t1 = s.ThetaDeg[i] + s.ThetaStepDeg / 2;
            double v = s.Values[i];
            double tt = span == 0 ? 1 : (v - s.Min) / span;
            using var p = Fill(double.IsFinite(v) ? ArrayColorMap.At(s.Reversed ? 1 - tt : tt) : Dim(theme));
            using var path = new SKPath();
            double r1 = Math.Sin(t1 * Math.PI / 180) * radius, r0 = Math.Sin(t0 * Math.PI / 180) * radius;
            if (s.ThetaDeg[i] == 0) path.AddCircle((float)layout.CentreX, (float)layout.CentreY, (float)r1);
            else
            {
                double p0 = s.PhiDeg[i] - s.PhiStepDeg / 2, p1 = s.PhiDeg[i] + s.PhiStepDeg / 2;
                int n = 8;
                for (int k = 0; k <= n; k++)
                {
                    double a = (p0 + (p1 - p0) * k / n) * Math.PI / 180;
                    var pt = new SKPoint((float)(layout.CentreX + r1 * Math.Cos(a)), (float)(layout.CentreY - r1 * Math.Sin(a)));
                    if (k == 0) path.MoveTo(pt); else path.LineTo(pt);
                }
                for (int k = n; k >= 0; k--)
                {
                    double a = (p0 + (p1 - p0) * k / n) * Math.PI / 180;
                    path.LineTo((float)(layout.CentreX + r0 * Math.Cos(a)), (float)(layout.CentreY - r0 * Math.Sin(a)));
                }
                path.Close();
            }
            c.DrawPath(path, p);
        }
        using var circle = Stroke(theme.TextColor, 1);
        c.DrawCircle((float)layout.CentreX, (float)layout.CentreY, (float)radius, circle);
        ColorBar(c, new SKRect(bounds.Right - 54, plot.Top + 10, bounds.Right - 42, plot.Bottom - 10), s.Min, s.Max, false, s.Unit, theme, s.Reversed);
        return layout;
    }

    // ── the cuts ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Draws the two principal cuts side by side over ±90° from the peak, co-polar solid, cross-polar dashed, the isotropic array
    /// factor dimmed beneath; <paramref name="polar"/> draws each on a polar grid instead.</summary>
    public static void DrawCuts(SKCanvas c, SKRect bounds, IReadOnlyList<CutTraces> cuts, bool polar, double floorDb, RenderTheme theme)
    {
        using (var bg = Fill(theme.BackgroundColor)) c.DrawRect(bounds, bg);
        if (cuts.Count == 0) return;
        float w = bounds.Width / cuts.Count;
        for (int k = 0; k < cuts.Count; k++)
        {
            var panel = new SKRect(bounds.Left + k * w, bounds.Top, bounds.Left + (k + 1) * w, bounds.Bottom);
            if (polar) PolarCut(c, panel, cuts[k], floorDb, theme); else RectCut(c, panel, cuts[k], floorDb, theme);
        }
        Text(c, "— co-polar   - - cross-polar   — isotropic array factor (dimmed)", bounds.Right - 10, bounds.Top + 12, theme.TextColor, 10, SKTextAlign.Right);
    }

    private static void RectCut(SKCanvas c, SKRect panel, CutTraces cut, double floor, RenderTheme theme)
    {
        var plot = new SKRect(panel.Left + 40, panel.Top + 36, panel.Right - 10, panel.Bottom - 34);
        Text(c, char.ToUpperInvariant(cut.Plane[0]) + cut.Plane[1..] + " cut", panel.Left + 40, panel.Top + 30, theme.TextColor, 12);
        using var grid = Stroke(theme.MinorGridColor, 1);
        using var border = Stroke(theme.BorderColor, 1);
        for (int deg = -90; deg <= 90; deg += 30)
        {
            float x = plot.Left + (float)((deg + 90) / 180.0) * plot.Width;
            c.DrawLine(x, plot.Top, x, plot.Bottom, grid);
            Text(c, $"{deg}°", x, plot.Bottom + 13, theme.TextColor, 10, SKTextAlign.Center);
        }
        for (double db = 0; db >= floor; db -= 10)
        {
            float y = plot.Top + (float)(-db / -floor) * plot.Height;
            c.DrawLine(plot.Left, y, plot.Right, y, grid);
            Text(c, $"{db:0}", plot.Left - 4, y + 4, theme.TextColor, 10, SKTextAlign.Right);
        }
        c.DrawRect(plot, border);
        void Trace(double[] y, SKColor color, float width, bool dashed)
        {
            using var path = new SKPath();
            bool pen = false;
            for (int i = 0; i < cut.OffsetDeg.Length; i++)
            {
                double o = cut.OffsetDeg[i];
                if (o < -90 || o > 90 || !double.IsFinite(y[i])) { pen = false; continue; }
                float px = plot.Left + (float)((o + 90) / 180) * plot.Width;
                float py = plot.Top + (float)(Math.Min(-floor, -y[i]) / -floor) * plot.Height;
                if (pen) path.LineTo(px, py); else path.MoveTo(px, py);
                pen = true;
            }
            using var p = Stroke(color, width, dashed);
            c.Save();
            c.ClipRect(plot);
            c.DrawPath(path, p);
            c.Restore();
        }
        Trace(cut.IsotropicDb, Dim(theme), 1.2f, false);
        Trace(cut.CrossDb, Second(theme), 1.3f, true);
        Trace(cut.CoDb, Accent(theme), 1.8f, false);
        Text(c, "angle from the peak", plot.MidX, plot.Bottom + 26, theme.TextColor, 10, SKTextAlign.Center);
    }

    private static void PolarCut(SKCanvas c, SKRect panel, CutTraces cut, double floor, RenderTheme theme)
    {
        Text(c, char.ToUpperInvariant(cut.Plane[0]) + cut.Plane[1..] + " cut", panel.Left + 10, panel.Top + 30, theme.TextColor, 12);
        float cx = panel.MidX, cy = panel.MidY + 14;
        float r = Math.Min(panel.Width, panel.Height - 56) / 2 - 8;
        using var grid = Stroke(theme.MinorGridColor, 1);
        for (double db = 0; db >= floor; db -= 10)
            c.DrawCircle(cx, cy, (float)(r * (1 - db / floor)), grid);
        for (int deg = -180; deg < 180; deg += 30)
        {
            double a = deg * Math.PI / 180;
            c.DrawLine(cx, cy, cx + (float)(r * Math.Sin(a)), cy - (float)(r * Math.Cos(a)), grid);
        }
        void Trace(double[] y, SKColor color, float width, bool dashed)
        {
            using var path = new SKPath();
            bool pen = false;
            for (int i = 0; i < cut.OffsetDeg.Length; i++)
            {
                if (!double.IsFinite(y[i])) { pen = false; continue; }
                double rr = r * Math.Max(0, 1 - Math.Max(floor, y[i]) / floor);
                double a = cut.OffsetDeg[i] * Math.PI / 180;
                float px = cx + (float)(rr * Math.Sin(a)), py = cy - (float)(rr * Math.Cos(a));
                if (pen) path.LineTo(px, py); else path.MoveTo(px, py);
                pen = true;
            }
            using var p = Stroke(color, width, dashed);
            c.DrawPath(path, p);
        }
        Trace(cut.IsotropicDb, Dim(theme), 1.2f, false);
        Trace(cut.CrossDb, Second(theme), 1.3f, true);
        Trace(cut.CoDb, Accent(theme), 1.8f, false);
    }

    // ── a line chart ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A tick format with enough decimals that four ticks over <paramref name="span"/> read differently.</summary>
    private static string Format(double span)
    {
        int digits = Math.Clamp((int)Math.Ceiling(-Math.Log10(Math.Abs(span) / 4)) + 1, 0, 6);
        return digits == 0 ? "0" : "0." + new string('0', digits);
    }

    /// <summary>A small line chart with axes fitted to the series.</summary>
    public static void DrawChart(SKCanvas c, SKRect bounds, string title, string xLabel, string yLabel, IReadOnlyList<ChartSeries> series, RenderTheme theme)
    {
        using (var bg = Fill(theme.BackgroundColor)) c.DrawRect(bounds, bg);
        Text(c, title, bounds.Left + 50, bounds.Top + 15, theme.TextColor, 12);
        var plot = new SKRect(bounds.Left + 50, bounds.Top + 24, bounds.Right - 12, bounds.Bottom - 36);
        var xs = series.SelectMany(s => s.X).Where(double.IsFinite).ToArray();
        var ys = series.SelectMany(s => s.Y).Where(double.IsFinite).ToArray();
        using var border = Stroke(theme.BorderColor, 1);
        c.DrawRect(plot, border);
        if (xs.Length == 0 || ys.Length == 0) return;
        double x0 = xs.Min(), x1 = xs.Max(), y0 = ys.Min(), y1 = ys.Max();
        if (x1 - x0 < 1e-12) { x0 -= 1; x1 += 1; }
        if (y1 - y0 < 1e-9) { y0 -= 0.5; y1 += 0.5; }
        double pad = 0.08 * (y1 - y0);
        y0 -= pad; y1 += pad;
        float X(double x) => plot.Left + (float)((x - x0) / (x1 - x0)) * plot.Width;
        float Y(double y) => plot.Bottom - (float)((y - y0) / (y1 - y0)) * plot.Height;
        using var grid = Stroke(theme.MinorGridColor, 1);
        for (int i = 0; i <= 4; i++)
        {
            double yv = y0 + (y1 - y0) * i / 4, xv = x0 + (x1 - x0) * i / 4;
            c.DrawLine(plot.Left, Y(yv), plot.Right, Y(yv), grid);
            c.DrawLine(X(xv), plot.Top, X(xv), plot.Bottom, grid);
            Text(c, yv.ToString(Format(y1 - y0)), plot.Left - 4, Y(yv) + 4, theme.TextColor, 10, SKTextAlign.Right);
            Text(c, xv.ToString(Format(x1 - x0)), X(xv), plot.Bottom + 13, theme.TextColor, 10, SKTextAlign.Center);
        }
        Text(c, xLabel, plot.MidX, plot.Bottom + 28, theme.TextColor, 10, SKTextAlign.Center);
        c.Save();
        c.RotateDegrees(-90, bounds.Left + 12, plot.MidY);
        Text(c, yLabel, bounds.Left + 12, plot.MidY, theme.TextColor, 10, SKTextAlign.Center);
        c.Restore();
        float ly = plot.Top + 14;
        foreach (var s in series)
        {
            using var path = new SKPath();
            bool pen = false;
            using var dot = Fill(s.Color);
            for (int i = 0; i < s.X.Length; i++)
            {
                if (!double.IsFinite(s.Y[i])) { pen = false; continue; }
                if (pen) path.LineTo(X(s.X[i]), Y(s.Y[i])); else path.MoveTo(X(s.X[i]), Y(s.Y[i]));
                pen = true;
                if (s.Markers) c.DrawCircle(X(s.X[i]), Y(s.Y[i]), 2.5f, dot);
            }
            using var p = Stroke(s.Color, 1.6f, s.Dashed);
            c.DrawPath(path, p);
            c.DrawLine(plot.Right - 150, ly - 4, plot.Right - 130, ly - 4, p);
            Text(c, s.Label, plot.Right - 126, ly, theme.TextColor, 10);
            ly += 14;
        }
    }
}
