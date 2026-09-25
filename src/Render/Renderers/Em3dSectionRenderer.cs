// brief-em3d-5 R-em3d5-2d — draws an Em3dScene: a section or an isometric outline of a 3D problem,
// with the air box and its faces, the ports, a material legend and a caption saying what the picture
// is and is not.
//
// NO SECOND PALETTE. A conductor is painted in its drawing layer's colour from the technology — the
// LayerDef colour LayoutRenderer paints it with, or FallbackPalette's for a key the technology does not
// define — which ObjectColours resolves. A bond wire has no drawing layer and takes the theme's own
// wBond wire colour. The chrome (background, ink, the dielectric base) is StackupRenderTheme's
// projection of the active ColorTheme, because a section IS a stackup cross-section with the lateral
// axis restored. Dielectrics are that base fill, turned in hue per material so two substrates can be
// told apart; the turn is keyed by the material's index in the PROBLEM, so a material keeps its
// colour from one view to the next.
//
// NO SECOND OUTPUT PATH. This draws onto whatever SKCanvas it is handed; the CLI hands it the same
// SVG/PDF/PNG surfaces every other `render` writes through (src/Cli/VectorPage.cs), and the SVG goes
// through SvgFontNormalizer there.
//
// Deterministic: nothing here reads a clock, a hash-ordered collection or the machine's fonts — the
// faces are SkiaFonts' embedded ones — so the same scene gives the same bytes (R-em3d5-2e).

using CircuitRF.Engine.Em3d;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>How an <see cref="Em3dScene"/> is painted.</summary>
/// <param name="ObjectColours">A conductor's colour by object name, from <see cref="Em3dSectionRenderer.ObjectColours"/>.</param>
/// <param name="Margin">The fraction of the drawing area left around the frame.</param>
/// <param name="Transparent">Leave the page unpainted. Air is then left unpainted too, so a plated
/// via's bore shows the barrel behind it rather than a hole.</param>
public sealed record Em3dRenderStyle(
    IReadOnlyDictionary<string, SKColor> ObjectColours,
    ColorTheme Theme, ColorVariant Variant, double Margin, bool Transparent);

/// <summary>Where everything goes on the page: the frame's device rectangle, the scale from metres
/// to device units, and the text metrics. One function computes it, so the picture and the report of
/// it (`render --json`'s zoom) cannot disagree.</summary>
public sealed record Em3dPageLayout(
    float FontSize, float LineHeight, float Pad, float LegendWidth, float LabelBand, float CaptionHeight,
    double Scale, double CentreU, double CentreV, SKRect Area, SKRect Frame)
{
    public SKPoint Map(Uv q) => new((float)(Area.MidX + (q.U - CentreU) * Scale),
                                    (float)(Area.MidY - (q.V - CentreV) * Scale));
}

public static class Em3dSectionRenderer
{
    /// <summary>The page layout for <paramref name="scene"/> on a <paramref name="width"/> ×
    /// <paramref name="height"/> page: a legend column on the right, a three-line caption below, a
    /// band for the face labels round the frame, and the frame fitted in what is left.</summary>
    public static Em3dPageLayout Layout(int width, int height, Em3dScene scene, double margin)
    {
        float fs      = (float)Math.Clamp(Math.Min(width, height) / 55.0, 8.0, 18.0);
        float lineH   = fs * 1.35f;
        float pad     = fs;
        float legendW = (float)Math.Clamp(width * 0.22, 8 * fs, 22 * fs);
        float band    = lineH * 1.3f;
        float captionH = 3 * lineH + pad;
        var area = new SKRect(pad + band, pad + band, width - legendW - pad - band, height - captionH - band);
        if (area.Width < 1) area.Right = area.Left + 1;
        if (area.Height < 1) area.Bottom = area.Top + 1;

        double fw = Math.Max(scene.FrameMax.U - scene.FrameMin.U, 1e-30);
        double fh = Math.Max(scene.FrameMax.V - scene.FrameMin.V, 1e-30);
        double scale = Math.Min(area.Width / fw, area.Height / fh) * (1 - 2 * Math.Clamp(margin, 0, 0.45));
        double cu = (scene.FrameMin.U + scene.FrameMax.U) / 2, cv = (scene.FrameMin.V + scene.FrameMax.V) / 2;
        var frame = new SKRect((float)(area.MidX - fw / 2 * scale), (float)(area.MidY - fh / 2 * scale),
                               (float)(area.MidX + fw / 2 * scale), (float)(area.MidY + fh / 2 * scale));
        return new Em3dPageLayout(fs, lineH, pad, legendW, band, captionH, scale, cu, cv, area, frame);
    }

    /// <summary>The smallest a port's projection is drawn across, in device units, so a sheet seen
    /// edge-on is still a visible, hatched mark.</summary>
    private const float MinPortWidth = 6f;

    /// <summary>Hatch spacing inside a port, device units.</summary>
    private const float HatchSpacing = 5f;

    /// <summary>
    /// A conductor's colour, by object name: its drawing layer's colour from <paramref name="tech"/>
    /// (or <see cref="FallbackPalette"/>'s for a key the technology does not define, exactly as the
    /// layout renderer resolves one), the theme's wBond wire colour for a bond wire, and the stackup
    /// edge ink for anything with neither.
    /// </summary>
    public static IReadOnlyDictionary<string, SKColor> ObjectColours(
        Em3dProblem problem, IReadOnlyDictionary<string, CircuitRF.Design.Layout.Em3d.Em3dObjectOrigin> origins,
        Technology? tech, ColorTheme theme, ColorVariant variant)
    {
        var wire = Sk(theme.Resolve(ColorRole.WBondWire, variant));
        var ink  = Sk(theme.Resolve(ColorRole.StackupBandEdge, variant));
        var map  = new Dictionary<string, SKColor>(StringComparer.Ordinal);

        SKColor For(string name)
        {
            if (!origins.TryGetValue(name, out var o)) return ink;
            if (o.DrawingLayer is { } key)
            {
                var def = tech?.Layers.FirstOrDefault(l => l.Key == key) ?? FallbackPalette.For(key);
                return new SKColor(def.Color.R, def.Color.G, def.Color.B);
            }
            return o.Kind == CircuitRF.Design.Layout.Em3d.Em3dObjectKind.Wire ? wire : ink;
        }

        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Conductor)) map[s.Name] = For(s.Name);
        foreach (var sh in problem.Sheets) map[sh.Name] = For(sh.Name);
        return map;
    }

    /// <summary>Paints <paramref name="scene"/> onto a <paramref name="width"/> × <paramref name="height"/> page.</summary>
    public static void Draw(SKCanvas canvas, int width, int height, Em3dScene scene, Em3dRenderStyle style)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(style);

        var st   = StackupRenderTheme.FromTheme(style.Theme, style.Variant);
        var port = Sk(style.Theme.Resolve(ColorRole.LayoutPCellPin, style.Variant));
        SKColor MaterialFill(string material)
        {
            int k = Math.Max(0, IndexOf(scene.DielectricMaterials, material));
            st.DielectricFill.ToHsv(out float h, out float s, out float v);
            // A floor on saturation, or a grey base turns in hue and stays grey: two substrates would
            // be the same fill.
            return SKColor.FromHsv((h + 47f * k) % 360f, Math.Max(s, 24f), v).WithAlpha(st.DielectricFill.Alpha);
        }
        SKColor Fill(string obj, Em3dRole role, string material) => role switch
        {
            Em3dRole.Conductor => style.ObjectColours.TryGetValue(obj, out var c) ? c : st.BandEdge,
            Em3dRole.Air       => st.Background,
            _                  => MaterialFill(material),
        };

        var page = Layout(width, height, scene, style.Margin);
        var (fs, lineH, pad, legendW, band, captionH, scale, frame) =
            (page.FontSize, page.LineHeight, page.Pad, page.LegendWidth, page.LabelBand, page.CaptionHeight,
             page.Scale, page.Frame);
        SKPoint Map(Uv q) => page.Map(q);

        if (!style.Transparent) canvas.Clear(st.Background);

        using var font  = new SKFont(SkiaFonts.PlexRegular, fs);
        using var bold  = new SKFont(SkiaFonts.PlexSemiBold, fs);
        using var small = new SKFont(SkiaFonts.PlexRegular, fs * 0.85f);
        using var fill   = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        using var text   = new SKPaint { IsAntialias = true, Color = st.LabelInk };

        // ── the geometry, clipped to the frame ─────────────────────────────────────────────────
        canvas.Save();
        canvas.ClipRect(frame);

        foreach (var r in scene.Regions)
        {
            if (r.Role == Em3dRole.Air && style.Transparent) continue;
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            if (r.CircleCentre is { } centre)
                path.AddCircle(Map(centre).X, Map(centre).Y, (float)(r.CircleRadius * scale));
            foreach (var ring in r.Rings)
                if (ring.Count >= 2) path.AddPoly([.. ring.Select(Map)], close: true);

            var colour = Fill(r.Object, r.Role, r.Material);
            fill.Color = colour;
            canvas.DrawPath(path, fill);
            if (r.Role != Em3dRole.Air)
            {
                stroke.Color = Darker(colour);
                stroke.StrokeWidth = 1f;
                canvas.DrawPath(path, stroke);
            }
        }

        foreach (var l in scene.Lines)
        {
            var colour = Fill(l.Object, l.Role, l.Material);
            if (l.IsSheet)
                stroke.StrokeWidth = scene.View.Kind == Em3dViewKind.Iso ? 1.25f : Math.Max(2f, (float)(l.WidthM * scale));
            else
                stroke.StrokeWidth = l.Role == Em3dRole.Conductor ? 1.25f : 1f;
            // A dielectric's outline is the theme's band edge, not a shade of its fill: a light fill's
            // darker shade vanishes into a dark background.
            stroke.Color = l.Role == Em3dRole.Air ? st.LabelInk.WithAlpha(0x60)
                         : l.Role == Em3dRole.Conductor ? colour : st.BandEdge;
            canvas.DrawLine(Map(l.A), Map(l.B), stroke);
        }
        canvas.Restore();

        // ── the air box: faces solid, frame cuts dashed ────────────────────────────────────────
        using (var dash = SKPathEffect.CreateDash([6f, 4f], 0))
        {
            foreach (var e in scene.BoxEdges)
            {
                stroke.Color = st.LabelInk.WithAlpha(e.Dashed ? (byte)0x90 : (byte)0xFF);
                stroke.StrokeWidth = e.Dashed ? 1f : 1.5f;
                stroke.PathEffect = e.Dashed ? dash : null;
                canvas.DrawLine(Map(e.A), Map(e.B), stroke);
            }
            stroke.PathEffect = null;
        }

        foreach (var f in scene.Faces.Where(f => f.Side != Em3dFaceSide.None))
        {
            string label = FaceText(f);
            switch (f.Side)
            {
                case Em3dFaceSide.Bottom:
                    canvas.DrawText(label, frame.MidX, frame.Bottom + lineH, SKTextAlign.Center, small, text); break;
                case Em3dFaceSide.Top:
                    canvas.DrawText(label, frame.MidX, frame.Top - lineH * 0.35f, SKTextAlign.Center, small, text); break;
                case Em3dFaceSide.Left:
                    canvas.Save();
                    canvas.RotateDegrees(-90, frame.Left - lineH * 0.35f, frame.MidY);
                    canvas.DrawText(label, frame.Left - lineH * 0.35f, frame.MidY, SKTextAlign.Center, small, text);
                    canvas.Restore();
                    break;
                case Em3dFaceSide.Right:
                    canvas.Save();
                    canvas.RotateDegrees(90, frame.Right + lineH * 0.35f, frame.MidY);
                    canvas.DrawText(label, frame.Right + lineH * 0.35f, frame.MidY, SKTextAlign.Center, small, text);
                    canvas.Restore();
                    break;
            }
        }

        // ── ports: hatched, numbered ───────────────────────────────────────────────────────────
        foreach (var p in scene.Ports)
        {
            using var path = PortPath(p.Outline.Select(Map).ToList());
            var bounds = path.Bounds;
            fill.Color = port.WithAlpha(0x30);
            canvas.DrawPath(path, fill);
            canvas.Save();
            canvas.ClipPath(path, antialias: true);
            stroke.Color = port;
            stroke.StrokeWidth = 1f;
            for (float d = -bounds.Height; d < bounds.Width; d += HatchSpacing)
                canvas.DrawLine(bounds.Left + d, bounds.Bottom, bounds.Left + d + bounds.Height, bounds.Top, stroke);
            canvas.Restore();
            stroke.StrokeWidth = 1.25f;
            canvas.DrawPath(path, stroke);
            text.Color = port;
            canvas.DrawText($"P{p.Number}", bounds.Right + fs * 0.3f, bounds.Top + fs * 0.9f, SKTextAlign.Left, bold, text);
            text.Color = st.LabelInk;
        }

        // ── the legend ─────────────────────────────────────────────────────────────────────────
        float lx = width - legendW, ly = pad + band;
        canvas.DrawText("Materials", lx, ly + fs, SKTextAlign.Left, bold, text);
        ly += lineH * 1.4f;
        var rows = new List<(string Material, SKColor Colour, Em3dRole Role)>();
        var seen = new HashSet<(string, SKColor)>();
        foreach (var (obj, role, material) in scene.Regions.Select(r => (r.Object, r.Role, r.Material))
                     .Concat(scene.Lines.Select(l => (l.Object, l.Role, l.Material))))
        {
            var colour = Fill(obj, role, material);
            if (seen.Add((material, colour))) rows.Add((material, colour, role));
        }
        int fit = (int)Math.Max(0, (height - captionH - ly - lineH * 2) / lineH);
        foreach (var (material, colour, role) in rows.Take(Math.Max(0, rows.Count > fit ? fit - 1 : fit)))
        {
            var swatch = SKRect.Create(lx, ly, fs, fs);
            fill.Color = colour;
            canvas.DrawRect(swatch, fill);
            stroke.Color = role == Em3dRole.Air ? st.LabelInk.WithAlpha(0x90) : Darker(colour);
            stroke.StrokeWidth = 1f;
            canvas.DrawRect(swatch, stroke);
            canvas.DrawText(role == Em3dRole.Air ? material + " (not filled)" : material,
                            lx + fs * 1.5f, ly + fs * 0.85f, SKTextAlign.Left, font, text);
            ly += lineH;
        }
        if (rows.Count > fit)
        {
            canvas.DrawText($"+{rows.Count - Math.Max(0, fit - 1)} more", lx, ly + fs * 0.85f, SKTextAlign.Left, font, text);
            ly += lineH;
        }
        if (scene.Ports.Count > 0)
        {
            ly += lineH * 0.4f;
            var swatch = SKRect.Create(lx, ly, fs, fs);
            using (var p = PortPath([new(swatch.Left, swatch.Top), new(swatch.Right, swatch.Top),
                                     new(swatch.Right, swatch.Bottom), new(swatch.Left, swatch.Bottom)]))
            {
                canvas.Save();
                canvas.ClipPath(p);
                stroke.Color = port;
                stroke.StrokeWidth = 1f;
                for (float d = -fs; d < fs; d += HatchSpacing)
                    canvas.DrawLine(swatch.Left + d, swatch.Bottom, swatch.Left + d + fs, swatch.Top, stroke);
                canvas.Restore();
                canvas.DrawPath(p, stroke);
            }
            canvas.DrawText("Port (its sheet, projected)", lx + fs * 1.5f, ly + fs * 0.85f, SKTextAlign.Left, font, text);
        }

        // ── the caption ────────────────────────────────────────────────────────────────────────
        float cy0 = height - captionH + lineH;
        canvas.DrawText(Title(scene), pad, cy0, SKTextAlign.Left, bold, text);
        canvas.DrawText(Convention(scene), pad, cy0 + lineH, SKTextAlign.Left, small, text);
        // Faces that read the same are said once — six absorbing faces at one distance are one fact.
        var unlabelled = scene.Faces.Where(f => f.Side == Em3dFaceSide.None)
                              .GroupBy(f => FaceText(f with { Face = "" }))
                              .Select(g => string.Join(", ", g.Select(f => f.Face)) + g.Key);
        canvas.DrawText("Air box: " + string.Join("  ·  ", unlabelled), pad, cy0 + 2 * lineH, SKTextAlign.Left, small, text);
    }

    /// <summary>The caption's first line: what this picture is.</summary>
    public static string Title(Em3dScene scene) => scene.View.Kind == Em3dViewKind.Iso
        ? "Isometric outline, viewed from +x +y +z"
        : $"{scene.View.Plane.ToUpperInvariant()} section at {scene.View.Axis} = {Em3dSectionScene.FormatLength(scene.At)}";

    /// <summary>The caption's second line: the rule the picture was drawn by — for an outline, that it
    /// hides nothing, because a wire-frame that looks like a shaded model misleads.</summary>
    public static string Convention(Em3dScene scene) => scene.View.Kind == Em3dViewKind.Iso
        ? "Silhouettes and sharp edges, orthographic. NO hidden-line removal: edges behind a surface are drawn too."
        : $"A solid is drawn where its bottom ≤ {scene.View.Axis} < its top; a sheet within " +
          $"{Em3dSectionScene.FormatLength(scene.SnapTolerance)} of its plane. Ports are drawn projected onto the plane.";

    private static string FaceText(Em3dSceneFace f)
    {
        string kind = f.Kind switch
        {
            Em3dBoundaryKind.Pec => "PEC", Em3dBoundaryKind.Pmc => "PMC",
            Em3dBoundaryKind.Symmetry => "symmetry", _ => "absorbing",
        };
        return f.BeyondM > 0
            ? $"{f.Face}: {kind}, {Em3dSectionScene.FormatLength(f.BeyondM)} beyond"
            : $"{f.Face}: {kind}";
    }

    /// <summary>A port's projected outline, widened to <see cref="MinPortWidth"/> across whichever
    /// device axis it is thinner than that on.</summary>
    private static SKPath PortPath(List<SKPoint> pts)
    {
        float x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        var path = new SKPath();
        if (x1 - x0 < MinPortWidth || y1 - y0 < MinPortWidth)
        {
            float mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
            if (x1 - x0 < MinPortWidth) { x0 = mx - MinPortWidth / 2; x1 = mx + MinPortWidth / 2; }
            if (y1 - y0 < MinPortWidth) { y0 = my - MinPortWidth / 2; y1 = my + MinPortWidth / 2; }
            path.AddRect(new SKRect(x0, y0, x1, y1));
        }
        else path.AddPoly([.. pts], close: true);
        return path;
    }

    private static int IndexOf(IReadOnlyList<string> list, string item)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == item) return i;
        return -1;
    }

    private static SKColor Darker(SKColor c)
        => new((byte)(c.Red * 0.6), (byte)(c.Green * 0.6), (byte)(c.Blue * 0.6), 0xFF);

    private static SKColor Sk(Rgba c) => new(c.R, c.G, c.B, c.A);
}
