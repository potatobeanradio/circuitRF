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
using CircuitRF.Render.Scene3D.Fields;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>How an <see cref="Em3dScene"/> is painted.</summary>
/// <param name="ObjectColours">A conductor's colour by object name, from <see cref="Em3dSectionRenderer.ObjectColours"/>.</param>
/// <param name="Margin">The fraction of the drawing area left around the frame.</param>
/// <param name="Transparent">Leave the page unpainted. Air is then left unpainted too, so a plated
/// via's bore shows the barrel behind it rather than a hole.</param>
/// <param name="TextAsPaths">Every label drawn as its glyphs' outlines rather than as text (<see cref="Em3dText"/>): an
/// SVG that looks the same wherever it is opened, at the cost of text a reader can select. Off for `render`.</param>
public sealed record Em3dRenderStyle(
    IReadOnlyDictionary<string, SKColor> ObjectColours,
    ColorTheme Theme, ColorVariant Variant, double Margin, bool Transparent, bool TextAsPaths = false)
{
    /// <summary>brief-em3d-88 Q2 — each solid labelled with its material, where the label fits inside its cut.</summary>
    public bool Labels { get; init; }

    /// <summary>brief-em3d-88 Q2 — the page is the section alone: no legend column, no label bands, no caption; a field's
    /// legend is inset in the frame's top-right corner (<see cref="Em3dSectionRenderer.Layout"/>'s <c>tight</c>).</summary>
    public bool Tight { get; init; }

    /// <summary>brief-em3d-88 — the 3D view's axis indicator, in the frame's bottom-left corner.</summary>
    public bool Axes { get; init; }

    /// <summary>brief-em3d-88 — the 3D view's scale bar in the frame's bottom-right corner, rounded and labelled in this unit
    /// (the document's display unit); null for none. A section only: an isometric outline has no one scale.</summary>
    public (CircuitRF.Design.Layout.LayoutUnit Unit, int DbuPerMicron)? ScaleBar { get; init; }

    /// <summary>brief-em3d-92 — each object's transparency by name: a region's fill is painted at the alpha it gives, as the 3D
    /// view and the drawing export paint it. Outlines stay opaque; an isometric outline, which fills nothing, is unchanged.</summary>
    public IReadOnlyDictionary<string, CircuitRF.Render.Scene3D.Scene3DTransparency>? ObjectTransparency { get; init; }
}

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

public static partial class Em3dSectionRenderer
{
    /// <summary>The page layout for <paramref name="scene"/> on a <paramref name="width"/> ×
    /// <paramref name="height"/> page: a legend column on the right, a three-line caption below, a
    /// band for the face labels round the frame, and the frame fitted in what is left.</summary>
    /// <para>brief-em3d-88 — <paramref name="tight"/>: the frame alone fills the page (less the margin), with no legend column,
    /// no band and no caption.</para>
    public static Em3dPageLayout Layout(int width, int height, Em3dScene scene, double margin, bool tight = false)
    {
        float fs      = (float)Math.Clamp(Math.Min(width, height) / 55.0, 8.0, 18.0);
        float lineH   = fs * 1.35f;
        float pad     = tight ? 0 : fs;
        float legendW = tight ? 0 : (float)Math.Clamp(width * 0.22, 8 * fs, 22 * fs);
        float band    = tight ? 0 : lineH * 1.3f;
        float captionH = tight ? 0 : 3 * lineH + pad;
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
    /// layout renderer resolves one), the theme's wBond wire colour for a bond wire, and for anything with
    /// neither, its material's colour when the technology defines the material (a flattened layout's
    /// copper, a drawn box: <see cref="CircuitRF.Design.ThreeD.C3dMaterialRole.ImpliedColour"/> when it
    /// states none, the Materials editor's swatch), else the stackup edge ink.
    /// </summary>
    public static IReadOnlyDictionary<string, SKColor> ObjectColours(
        Em3dProblem problem, IReadOnlyDictionary<string, CircuitRF.Design.Layout.Em3d.Em3dObjectOrigin> origins,
        Technology? tech, ColorTheme theme, ColorVariant variant)
    {
        var wire = Sk(theme.Resolve(ColorRole.WBondWire, variant));
        var ink  = Sk(theme.Resolve(ColorRole.StackupBandEdge, variant));
        var map  = new Dictionary<string, SKColor>(StringComparer.Ordinal);

        SKColor For(string name, string material)
        {
            origins.TryGetValue(name, out var o);
            if (o?.DrawingLayer is { } key)
            {
                var def = tech?.Layers.FirstOrDefault(l => l.Key == key) ?? FallbackPalette.For(key);
                return new SKColor(def.Color.R, def.Color.G, def.Color.B);
            }
            if (o?.Kind == CircuitRF.Design.Layout.Em3d.Em3dObjectKind.Wire) return wire;
            if (tech?.FindMaterial(material) is { } m)
            {
                var (r, g, b) = m.Color is { } hex && Rgba.TryParseHex(hex, out var stated)
                    ? (stated.R, stated.G, stated.B)
                    : CircuitRF.Design.ThreeD.C3dMaterialRole.ImpliedColour(CircuitRF.Design.ThreeD.C3dMaterialRole.Implied(m));
                return new SKColor(r, g, b);
            }
            return ink;
        }

        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Conductor)) map[s.Name] = For(s.Name, s.Material);
        foreach (var sh in problem.Sheets) map[sh.Name] = For(sh.Name, sh.Material);
        return map;
    }

    /// <summary>Paints <paramref name="scene"/> onto a <paramref name="width"/> × <paramref name="height"/> page — with
    /// <paramref name="field"/> (brief-em3d-84) under it: the field first, then the regions as outlines (a conductor, which
    /// carries no field, still filled), then the ports and the box, so the geometry reads over the field; and the field's
    /// legend where the materials' would be, since no dielectric is filled to key.
    /// <para>brief-em3d-88 — a TEMPERATURE (the layer's <see cref="Em3dFieldLayer.Thermal"/>) is a thermal page: the wires
    /// painted from their T(s) over the slice, every metal outlined and none filled (a metal carries a temperature), no port,
    /// the thermal boundaries on the frame instead of the air box's faces, and a caption naming the setup, the point and the
    /// plane.</para></summary>
    public static void Draw(SKCanvas canvas, int width, int height, Em3dScene scene, Em3dRenderStyle style, Em3dFieldLayer? field = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(style);
        DrawPage(canvas, width, height, scene, style, field);
        if (style.Axes || style.ScaleBar is not null) DrawChrome(canvas, Layout(width, height, scene, style.Margin, style.Tight), scene, style);
    }

    /// <summary>The 3D view's scale bar aims for about this many of its DIPs (Viewer3DOverlay.ScaleBarTarget).</summary>
    private const double ScaleBarTargetDips = 110;

    /// <summary>
    /// brief-em3d-88 — the 3D view's own chrome (Em3dDrawingSheet.DrawChrome, which its vector export paints too) in the frame's
    /// corners: the axis indicator bottom left, looking along the section's normal, and the scale bar bottom right — each on a
    /// light panel, since a field's corner is often its darkest colour. A DIP of the view is the page's text size over 11,
    /// so the chrome is in proportion to the page's words.
    /// </summary>
    private static void DrawChrome(SKCanvas canvas, Em3dPageLayout page, Em3dScene scene, Em3dRenderStyle style)
    {
        double k = page.FontSize / 11.0;
        (double, string)? bar = null;
        if (style.ScaleBar is { } unit && scene.View.Kind != Em3dViewKind.Iso && page.Scale > 0)
        {
            double m = Em3dDrawingSheet.ScaleBarLength(ScaleBarTargetDips * k / page.Scale, unit.Unit, unit.DbuPerMicron);
            bar = (m, CircuitRF.Design.Layout.Em.EmLengthFormat.For(unit.Unit, unit.DbuPerMicron)(m));
        }
        var chrome = new Em3dPictureChrome { PagePerDip = k, AxisIndicator = style.Axes, ScaleBar = bar };
        var st = StackupRenderTheme.FromTheme(style.Theme, style.Variant);
        canvas.Save();
        canvas.Translate(page.Frame.Left, page.Frame.Top);
        Em3dDrawingSheet.DrawChrome(canvas, page.Frame.Width, page.Frame.Height, page.Scale, Looking(scene.View), chrome, style.TextAsPaths,
                               new SKColor(250, 250, 252, 215));
        canvas.Restore();
    }

    /// <summary>Which way a view looks, for the axis indicator: a section along its normal (x, y and z in the picture's own
    /// right-handed sense), the isometric outline from +x +y +z (Em3dSectionScene.Project's axes).</summary>
    private static Em3dProjection? Looking(Em3dView view) => view.Kind switch
    {
        Em3dViewKind.SectionZ => new Em3dProjection(new(0, 0, 1), new(1, 0, 0), new(0, 1, 0), "section z"),
        Em3dViewKind.SectionY => new Em3dProjection(new(0, -1, 0), new(1, 0, 0), new(0, 0, 1), "section y"),
        Em3dViewKind.SectionX => new Em3dProjection(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), "section x"),
        Em3dViewKind.Iso => new Em3dProjection(new(1 / Math.Sqrt(3), 1 / Math.Sqrt(3), 1 / Math.Sqrt(3)),
                                               new(Math.Cos(Math.PI / 6), -Math.Cos(Math.PI / 6), 0), new(-0.5, -0.5, 1), "iso"),
        // brief-dfr34-3 — seen from the line's right: its direction to the right, z up, toward the viewer their cross product
        Em3dViewKind.SectionLine when view.Line is { } l => new Em3dProjection(new(l.Direction.Y, -l.Direction.X, 0),
                                               new(l.Direction.X, l.Direction.Y, 0), new(0, 0, 1), "section"),
        _ => view.Projection,
    };

    private static void DrawPage(SKCanvas canvas, int width, int height, Em3dScene scene, Em3dRenderStyle style, Em3dFieldLayer? field)
    {
        // brief-em3d-89 — a Surfaces or Faces plot is a picture of surfaces in depth, not a section: its own page
        if (field?.Surface is not null) { DrawSurfacePage(canvas, width, height, scene, style, field); return; }

        var st   = StackupRenderTheme.FromTheme(style.Theme, style.Variant);
        var port = Sk(style.Theme.Resolve(ColorRole.LayoutPCellPin, style.Variant));
        SKColor MaterialFill(string material) => DielectricFill(st, scene.DielectricMaterials, material);
        SKColor Fill(string obj, Em3dRole role, string material) => role switch
        {
            Em3dRole.Conductor => style.ObjectColours.TryGetValue(obj, out var c) ? c : st.BandEdge,
            Em3dRole.Air       => st.Background,
            _                  => MaterialFill(material),
        };

        bool thermal = field?.Thermal is not null;
        var page = Layout(width, height, scene, style.Margin, style.Tight);
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

        if (field is not null)
        {
            Em3dSectionField.Draw(canvas, field, Map);
            Em3dSectionField.DrawWires(canvas, field, Map);
        }

        // brief-em3d-101 — an outline's image sheets, in the fill's slot: under every line.
        if (field is null && scene.Images.Count > 0) Em3dSceneImages.Draw(canvas, scene.Images, Map, style.ObjectTransparency);

        foreach (var r in scene.Regions)
        {
            if (r.Role == Em3dRole.Air && (style.Transparent || field is not null)) continue;
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            if (r.CircleCentre is { } centre)
                path.AddCircle(Map(centre).X, Map(centre).Y, (float)(r.CircleRadius * scale));
            foreach (var ring in r.Rings)
                if (ring.Count >= 2) path.AddPoly([.. ring.Select(Map)], close: true);

            var colour = Fill(r.Object, r.Role, r.Material);
            fill.Color = r.Role != Em3dRole.Air && style.ObjectTransparency?.TryGetValue(r.Object, out var see) == true
                ? colour.WithAlpha(see.Alpha(colour.Alpha)) : colour;
            if (field is null || r.Role == Em3dRole.Conductor && !thermal) canvas.DrawPath(path, fill);
            if (r.Role != Em3dRole.Air)
            {
                stroke.Color = field is not null && (r.Role != Em3dRole.Conductor || thermal) ? st.LabelInk : Darker(colour);
                stroke.StrokeWidth = thermal && r.Role == Em3dRole.Conductor ? 1.25f : 1f;
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
        if (style.Labels) DrawMaterialLabels(canvas, scene, page, style, st, small);
        // brief-sar-6 R-sar6-5 — a SAR plot's peak cube, over the outlines
        if (field is not null) Em3dSectionField.DrawMarkers(canvas, field, Map, fs * 0.85f);
        canvas.Restore();

        if (thermal)
        {
            // unclipped: a boundary is very often ON the frame (a flange's bottom is the content's)
            DrawBoundaryLines(canvas, field!.Thermal!, Map);
            DrawBoundaryLabels(canvas, field!.Thermal!, page, style, st, small, Map);
            // no port, no air box: a thermal run solves the solids alone
            if (field.Legend.Count > 0) PaintLegend(canvas, width, height, page, style, field);
            if (!style.Tight) DrawLines(canvas, field.Thermal!.Caption, style, height, captionH, lineH, pad, bold, small, text);
            return;
        }

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

        foreach (var f in scene.Faces.Where(f => f.Side != Em3dFaceSide.None && !style.Tight))
        {
            string label = FaceText(f);
            switch (f.Side)
            {
                case Em3dFaceSide.Bottom:
                    Em3dText.Draw(canvas, style.TextAsPaths, label, frame.MidX, frame.Bottom + lineH, SKTextAlign.Center, small, text); break;
                case Em3dFaceSide.Top:
                    Em3dText.Draw(canvas, style.TextAsPaths, label, frame.MidX, frame.Top - lineH * 0.35f, SKTextAlign.Center, small, text); break;
                case Em3dFaceSide.Left:
                    canvas.Save();
                    canvas.RotateDegrees(-90, frame.Left - lineH * 0.35f, frame.MidY);
                    Em3dText.Draw(canvas, style.TextAsPaths, label, frame.Left - lineH * 0.35f, frame.MidY, SKTextAlign.Center, small, text);
                    canvas.Restore();
                    break;
                case Em3dFaceSide.Right:
                    canvas.Save();
                    canvas.RotateDegrees(90, frame.Right + lineH * 0.35f, frame.MidY);
                    Em3dText.Draw(canvas, style.TextAsPaths, label, frame.Right + lineH * 0.35f, frame.MidY, SKTextAlign.Center, small, text);
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
            Em3dText.Draw(canvas, style.TextAsPaths, $"P{p.Number}", bounds.Right + fs * 0.3f, bounds.Top + fs * 0.9f, SKTextAlign.Left, bold, text);
            text.Color = st.LabelInk;
        }

        // ── the legend ─────────────────────────────────────────────────────────────────────────
        if (field is not null)
        {
            if (field.Legend.Count > 0) PaintLegend(canvas, width, height, page, style, field);
            if (!style.Tight) DrawCaption(canvas, scene, style, height, captionH, lineH, pad, bold, small, text);
            return;
        }
        if (style.Tight) return;
        float lx = width - legendW, ly = pad + band;
        Em3dText.Draw(canvas, style.TextAsPaths, "Materials", lx, ly + fs, SKTextAlign.Left, bold, text);
        ly += lineH * 1.4f;
        var rows = new List<(string Material, SKColor Colour, Em3dRole Role)>();
        var seen = new HashSet<(string, SKColor)>();
        foreach (var (obj, role, material) in scene.Regions.Select(r => (r.Object, r.Role, r.Material))
                     .Concat(scene.Lines.Select(l => (l.Object, l.Role, l.Material))))
        {
            // brief-em3d-101 — an image sheet with no material keys nothing: its picture is its own legend.
            if (material.Length == 0) continue;
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
            Em3dText.Draw(canvas, style.TextAsPaths, role == Em3dRole.Air ? material + " (not filled)" : material,
                            lx + fs * 1.5f, ly + fs * 0.85f, SKTextAlign.Left, font, text);
            ly += lineH;
        }
        if (rows.Count > fit)
        {
            Em3dText.Draw(canvas, style.TextAsPaths, $"+{rows.Count - Math.Max(0, fit - 1)} more", lx, ly + fs * 0.85f, SKTextAlign.Left, font, text);
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
            Em3dText.Draw(canvas, style.TextAsPaths, "Port (its sheet, projected)", lx + fs * 1.5f, ly + fs * 0.85f, SKTextAlign.Left, font, text);
        }

        DrawCaption(canvas, scene, style, height, captionH, lineH, pad, bold, small, text);
    }

    /// <summary>A field's legend — Export picture's own painter: sized to the legend column, or (<see cref="Em3dRenderStyle.Tight"/>)
    /// inset in the frame's top-right corner at the page's text size.</summary>
    private static void PaintLegend(SKCanvas canvas, int width, int height, Em3dPageLayout page, Em3dRenderStyle style, Em3dFieldLayer field)
    {
        bool dark = style.Variant == ColorVariant.Dark;
        if (!style.Tight)
        {
            // its bar is 220 units wide at scale 1
            FieldPicture.Paint(canvas, width, height, Math.Min(page.FontSize / 12f, (page.LegendWidth - page.Pad) / 236f), field.Legend,
                               field.Map, field.Scale, null, dark, SkiaFonts.PlexRegular);
            return;
        }
        canvas.Save();
        canvas.Translate(page.Frame.Right - width, page.Frame.Top);
        FieldPicture.Paint(canvas, width, height, Math.Min(page.FontSize / 12f, page.Frame.Width * 0.4f / 236f), field.Legend,
                           field.Map, field.Scale, null, dark, SkiaFonts.PlexRegular);
        canvas.Restore();
    }

    // ── brief-em3d-88: a thermal page's boundaries ───────────────────────────────────────────

    /// <summary>A fixed-temperature face is blue and a convection face green — the 3D editor's tints (R-em3d75-2).</summary>
    private static SKColor BoundaryColour(CircuitRF.Design.Layout.Em.ThermalBoundaryKind kind)
        => kind == CircuitRF.Design.Layout.Em.ThermalBoundaryKind.FixedT ? new SKColor(60, 120, 235) : new SKColor(60, 185, 95);

    private static void DrawBoundaryLines(SKCanvas canvas, Em3dThermalPage page, Func<Uv, SKPoint> map)
    {
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, StrokeCap = SKStrokeCap.Round };
        foreach (var b in page.Boundaries)
        {
            line.Color = BoundaryColour(b.Kind);
            foreach (var (a, c) in b.Segments) canvas.DrawLine(map(a), map(c), line);
        }
    }

    /// <summary>Each placed boundary's label: in the band beside the frame side its cut is nearest, level with it — or, on a
    /// tight page with no band, inside the frame beside the cut.</summary>
    private static void DrawBoundaryLabels(SKCanvas canvas, Em3dThermalPage page, Em3dPageLayout layout, Em3dRenderStyle style,
                                           StackupRenderTheme st, SKFont font, Func<Uv, SKPoint> map)
    {
        var frame = layout.Frame;
        using var ink = new SKPaint { IsAntialias = true };
        foreach (var b in page.Boundaries.Where(b => b.Segments.Count > 0))
        {
            var pts = b.Segments.SelectMany(s => new[] { map(s.A), map(s.B) }).ToList();
            float x0 = Math.Max(frame.Left, pts.Min(p => p.X)), x1 = Math.Min(frame.Right, pts.Max(p => p.X));
            float y0 = Math.Max(frame.Top, pts.Min(p => p.Y)), y1 = Math.Min(frame.Bottom, pts.Max(p => p.Y));
            if (x0 > x1 || y0 > y1) continue;                                   // outside the frame
            float mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
            float dl = mx - frame.Left, dr = frame.Right - mx, dt = my - frame.Top, db = frame.Bottom - my;
            float near = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            float lineH = layout.LineHeight;
            ink.Color = style.Tight ? BoundaryColour(b.Kind) : st.LabelInk;
            float w = font.MeasureText(b.Label);
            float cx = Math.Clamp(mx, frame.Left + w / 2, frame.Right - w / 2);
            if (style.Tight)
            {
                // inside, on the frame's side of the cut
                float y = near == db ? y0 - lineH * 0.4f : y1 + lineH * 0.9f;
                Halo(canvas, style, b.Label, cx, Math.Clamp(y, frame.Top + lineH, frame.Bottom - lineH * 0.3f), SKTextAlign.Center, font, ink, st.Background);
                continue;
            }
            if (near == db) Em3dText.Draw(canvas, style.TextAsPaths, b.Label, cx, frame.Bottom + lineH, SKTextAlign.Center, font, ink);
            else if (near == dt) Em3dText.Draw(canvas, style.TextAsPaths, b.Label, cx, frame.Top - lineH * 0.35f, SKTextAlign.Center, font, ink);
            else
            {
                bool left = near == dl;
                float x = left ? frame.Left - lineH * 0.35f : frame.Right + lineH * 0.35f;
                float cy = Math.Clamp(my, frame.Top + w / 2, frame.Bottom - w / 2);
                canvas.Save();
                canvas.RotateDegrees(left ? -90 : 90, x, cy);
                Em3dText.Draw(canvas, style.TextAsPaths, b.Label, x, cy, SKTextAlign.Center, font, ink);
                canvas.Restore();
            }
        }
    }

    // ── brief-em3d-88 Q2: material labels ────────────────────────────────────────────────────

    /// <summary>
    /// Each solid's material, written once per object on its largest cut, at the point of that cut furthest from its edges —
    /// and only where the words fit inside it and clear of the labels already placed, so a label never lies across the
    /// boundary it names. Ink over a halo of the page's background, legible over any colour of a field.
    /// </summary>
    private static void DrawMaterialLabels(SKCanvas canvas, Em3dScene scene, Em3dPageLayout page, Em3dRenderStyle style,
                                           StackupRenderTheme st, SKFont font)
    {
        using var ink = new SKPaint { IsAntialias = true, Color = st.LabelInk };
        var placed = new List<SKRect>();
        float h = font.Size;
        var best = new Dictionary<string, (Em3dSceneRegion Region, double Area)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var r in scene.Regions.Where(r => r.Role != Em3dRole.Air && !r.IsSheet))
        {
            double area = r.CircleCentre is not null ? Math.PI * r.CircleRadius * r.CircleRadius : r.Rings.Sum(g => Math.Abs(Em3dSectionField.SignedArea(g)));
            if (!best.TryGetValue(r.Object, out var b)) order.Add(r.Object);
            if (!best.TryGetValue(r.Object, out b) || area > b.Area) best[r.Object] = (r, area);
        }
        foreach (string obj in order.OrderByDescending(o => best[o].Area))
        {
            var r = best[obj].Region;
            string label = r.Material;
            if (label.Length == 0) continue;
            float w = font.MeasureText(label);
            if (Spot(r, page, w, h) is not { } at) continue;
            var box = new SKRect(at.X - w / 2 - 2, at.Y - h * 0.8f, at.X + w / 2 + 2, at.Y + h * 0.3f);
            if (placed.Any(p => p.IntersectsWith(box))) continue;
            placed.Add(box);
            Halo(canvas, style, label, at.X, at.Y + h * 0.3f, SKTextAlign.Center, font, ink, st.Background);
        }
    }

    /// <summary>Where a label <paramref name="w"/> × <paramref name="h"/> (device units) sits inside <paramref name="r"/>: the grid
    /// point furthest from its edges, when the label's box fits there; null otherwise.</summary>
    private static SKPoint? Spot(Em3dSceneRegion r, Em3dPageLayout page, float w, float h)
    {
        if (r.CircleCentre is { } c)
        {
            var p = page.Map(c);
            float rad = (float)(r.CircleRadius * page.Scale);
            return Math.Sqrt(w * w / 4 + h * h / 4) <= rad ? p : null;
        }
        var rings = r.Rings.Where(g => g.Count >= 3).Select(g => g.Select(page.Map).ToArray()).ToList();
        if (rings.Count == 0) return null;
        float x0 = rings.Min(g => g.Min(p => p.X)), x1 = rings.Max(g => g.Max(p => p.X));
        float y0 = rings.Min(g => g.Min(p => p.Y)), y1 = rings.Max(g => g.Max(p => p.Y));
        x0 = Math.Max(x0, page.Frame.Left); x1 = Math.Min(x1, page.Frame.Right);
        y0 = Math.Max(y0, page.Frame.Top); y1 = Math.Min(y1, page.Frame.Bottom);
        if (x1 - x0 < w || y1 - y0 < h) return null;
        const int N = 24;
        SKPoint? found = null;
        float bestD = 0;
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
            {
                var q = new SKPoint(x0 + (x1 - x0) * i / N, y0 + (y1 - y0) * j / N);
                if (!Inside(rings, q)) continue;
                // every corner of the label's box inside, and its middle clear of the edges by half its height
                if (!Inside(rings, new(q.X - w / 2, q.Y - h / 2)) || !Inside(rings, new(q.X + w / 2, q.Y - h / 2)) ||
                    !Inside(rings, new(q.X - w / 2, q.Y + h / 2)) || !Inside(rings, new(q.X + w / 2, q.Y + h / 2))) continue;
                float d = EdgeDistance(rings, q);
                if (d >= h / 2 && d > bestD) { bestD = d; found = q; }
            }
        return found;
    }

    private static bool Inside(List<SKPoint[]> rings, SKPoint q)
    {
        bool inside = false;
        foreach (var g in rings)
            for (int i = 0, j = g.Length - 1; i < g.Length; j = i++)
                if ((g[i].Y > q.Y) != (g[j].Y > q.Y) && q.X < (g[j].X - g[i].X) * (q.Y - g[i].Y) / (g[j].Y - g[i].Y) + g[i].X) inside = !inside;
        return inside;
    }

    private static float EdgeDistance(List<SKPoint[]> rings, SKPoint q)
    {
        float best = float.MaxValue;
        foreach (var g in rings)
            for (int i = 0, j = g.Length - 1; i < g.Length; j = i++)
            {
                float ex = g[i].X - g[j].X, ey = g[i].Y - g[j].Y, l2 = ex * ex + ey * ey;
                float t = l2 > 0 ? Math.Clamp(((q.X - g[j].X) * ex + (q.Y - g[j].Y) * ey) / l2, 0, 1) : 0;
                float dx = q.X - (g[j].X + t * ex), dy = q.Y - (g[j].Y + t * ey);
                best = Math.Min(best, MathF.Sqrt(dx * dx + dy * dy));
            }
        return best;
    }

    /// <summary>Text over a halo of <paramref name="halo"/>, so it reads over a field of any colour.</summary>
    private static void Halo(SKCanvas canvas, Em3dRenderStyle style, string label, float x, float y, SKTextAlign align, SKFont font,
                             SKPaint ink, SKColor halo)
    {
        using var back = new SKPaint { IsAntialias = true, Color = halo.WithAlpha(0xE0), Style = SKPaintStyle.Stroke, StrokeWidth = font.Size * 0.3f,
                                       StrokeJoin = SKStrokeJoin.Round };
        Em3dText.Draw(canvas, style.TextAsPaths, label, x, y, align, font, back);
        Em3dText.Draw(canvas, style.TextAsPaths, label, x, y, align, font, ink);
    }

    /// <summary>brief-em3d-88 — a caption of given lines: the first bold, the rest small.</summary>
    private static void DrawLines(SKCanvas canvas, IReadOnlyList<string> lines, Em3dRenderStyle style, int height, float captionH, float lineH,
                                  float pad, SKFont bold, SKFont small, SKPaint text)
    {
        float cy0 = height - captionH + lineH;
        for (int i = 0; i < Math.Min(3, lines.Count); i++)
            Em3dText.Draw(canvas, style.TextAsPaths, lines[i], pad, cy0 + i * lineH, SKTextAlign.Left, i == 0 ? bold : small, text);
    }

    // ── the caption ────────────────────────────────────────────────────────────────────────────
    private static void DrawCaption(SKCanvas canvas, Em3dScene scene, Em3dRenderStyle style, int height, float captionH, float lineH,
                                    float pad, SKFont bold, SKFont small, SKPaint text)
    {
        float cy0 = height - captionH + lineH;
        Em3dText.Draw(canvas, style.TextAsPaths, Title(scene), pad, cy0, SKTextAlign.Left, bold, text);
        Em3dText.Draw(canvas, style.TextAsPaths, Convention(scene), pad, cy0 + lineH, SKTextAlign.Left, small, text);
        // Faces that read the same are said once — six absorbing faces at one distance are one fact.
        var unlabelled = scene.Faces.Where(f => f.Side == Em3dFaceSide.None)
                              .GroupBy(f => FaceText(f with { Face = "" }))
                              .Select(g => string.Join(", ", g.Select(f => f.Face)) + g.Key);
        Em3dText.Draw(canvas, style.TextAsPaths, "Air box: " + string.Join("  ·  ", unlabelled), pad, cy0 + 2 * lineH, SKTextAlign.Left, small, text);
    }

    /// <summary>A dielectric's fill: the stackup's dielectric base, turned in hue by the material's index in
    /// <paramref name="dielectrics"/> (the problem's order), so a material keeps its colour from one view to the next.</summary>
    internal static SKColor DielectricFill(StackupRenderTheme st, IReadOnlyList<string> dielectrics, string material)
    {
        int k = Math.Max(0, IndexOf(dielectrics, material));
        st.DielectricFill.ToHsv(out float h, out float s, out float v);
        // A floor on saturation, or a grey base turns in hue and stays grey: two substrates would
        // be the same fill.
        return SKColor.FromHsv((h + 47f * k) % 360f, Math.Max(s, 24f), v).WithAlpha(st.DielectricFill.Alpha);
    }

    /// <summary>The caption's first line: what this picture is.</summary>
    public static string Title(Em3dScene scene) => scene.View.Kind == Em3dViewKind.Iso
        ? "Isometric outline, viewed from +x +y +z"
        : scene.View.Kind == Em3dViewKind.Projection
        ? $"{scene.View.Projection?.Name ?? "Outline"} outline, orthographic"
        : scene.View.Kind == Em3dViewKind.SectionLine && scene.View.Line is { } line
        ? $"Vertical section through {Em3dSectionScene.LineText(line)}"
        : $"{scene.View.Plane.ToUpperInvariant()} section at {scene.View.Axis} = {Em3dSectionScene.FormatLength(scene.At)}";

    /// <summary>The caption's second line: the rule the picture was drawn by — for an outline, that it
    /// hides nothing, because a wire-frame that looks like a shaded model misleads.</summary>
    public static string Convention(Em3dScene scene) => scene.View.Kind == Em3dViewKind.Iso
        ? "Silhouettes and sharp edges, orthographic. NO hidden-line removal: edges behind a surface are drawn too."
        : scene.View.Kind == Em3dViewKind.Projection
        ? "Silhouettes and sharp edges, orthographic."
        : scene.View.Kind == Em3dViewKind.SectionLine
        ? "Across: the distance along the line from its first point; up: z. Ports are drawn projected onto the plane."
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
    internal static SKPath PortPath(List<SKPoint> pts)
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

    internal static int IndexOf(IReadOnlyList<string> list, string item)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == item) return i;
        return -1;
    }

    internal static SKColor Darker(SKColor c)
        => new((byte)(c.Red * 0.6), (byte)(c.Green * 0.6), (byte)(c.Blue * 0.6), 0xFF);

    internal static SKColor Sk(Rgba c) => new(c.R, c.G, c.B, c.A);
}
