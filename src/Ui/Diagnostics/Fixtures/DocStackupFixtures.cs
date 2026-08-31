using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// <b>The stackup, drawn in cross-section from a REAL technology</b> — the picture the Stackup
/// chapter is about.
///
/// <para>It is built from <see cref="StarterTechnologies.MmicGaAs"/> rather than from a hand-written
/// list of bands, for the reason every other figure in this folder is built from real objects: a
/// diagram that agrees only with itself would go on agreeing with itself after the shipped
/// technology changed. Every number in it — thickness, ε<sub>r</sub>, tanδ, which conductor is the
/// ground reference, which two layers a via spans — is read off the <c>Technology</c> the
/// application ships, so if that moves, the figure moves.</para>
///
/// <para><b>Heights are NOT to scale, and the caption says so.</b> A 3 µm metal beside a 100 µm
/// substrate is a 33:1 ratio: drawn honestly, every conductor in the picture would be a hairline,
/// including the one the whole chapter is about. Conductors get a fixed readable band and
/// dielectrics a compressed one; the real thickness is printed on every band, which is the number a
/// reader actually needs.</para>
/// </summary>
public static class DocStackupFixtures
{
    private const double Width       = 860;
    private const double BandLeft    = 24;
    private const double BandWidth   = 460;
    private const double ConductorH  = 26;
    private const double LabelLeft   = BandLeft + BandWidth + 18;

    private static double MicronsOf(long dbu) => dbu / (double)LayoutUnits.DefaultDbuPerMicron;

    /// <summary>An MMIC stackup: two signal metals, the thin-film capacitor module between them, a
    /// substrate, a backside ground plane, and the three vias that connect them.</summary>
    public static FigureScene MmicCrossSection() => CrossSection(StarterTechnologies.MmicGaAs());

    /// <summary>
    /// <b>The capacitor module on its own, MIM-7</b> — the same real technology, windowed to the
    /// bands between the two interconnect metals so the three things a reader of the MIM section
    /// needs are legible at reading size: the plate metal, the tied dielectric under it (with the
    /// tie marked), and the plate via's span.
    ///
    /// <para>A WINDOW on the full picture rather than a second, invented stack — every number is
    /// still read off the shipped <c>Technology</c>, and the substrate and ground plane below are
    /// named in the footer rather than redrawn. The full seven-band cross-section is
    /// <c>stackup-mmic</c>, in the same chapter.</para>
    /// </summary>
    public static FigureScene MimModuleCrossSection() => CrossSection(
        StarterTechnologies.MmicGaAs(),
        include: l => l.Name is "Metal2" or "Air" or "MIM Metal" or "MIM Dielectric" or "Metal1",
        height: 250,
        footer: "…then 100 µm of GaAs and the backside ground plane, unchanged by the module.");

    private static FigureScene CrossSection(
        Technology tech, Func<StackupLayer, bool>? include = null, double height = 352,
        string? footer = null)
    {
        var canvas = new Canvas { Width = Width, Height = height };

        var bands = tech.Stackup.Layers
            .Where(l => l.Kind != StackupKind.Via && (include is null || include(l))).ToList();
        var vias  = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).ToList();

        // ── Where each band lands, top to bottom (the order the stackup itself is written in) ────
        double y = 46;
        var top = new Dictionary<string, double>(StringComparer.Ordinal);
        var bot = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var band in bands)
        {
            double h = band.Kind == StackupKind.Conductor ? ConductorH : DielectricHeight(band);
            top[band.Name] = y;
            bot[band.Name] = y + h;

            canvas.Children.Add(Band(band, tech, y, h));
            foreach (var text in BandLabels(band, y, h)) canvas.Children.Add(text);
            y += h;
        }

        // ── The two boundary conditions, which are properties of the STACK rather than of a band ─
        //
        // A WINDOWED figure states neither: it is not showing the whole sandwich, so printing the
        // stack's terminations beside a slice of it would say something the picture does not show.
        if (include is null)
        {
            canvas.Children.Add(Note($"Top: {tech.Stackup.Top}"
                                   + (tech.Stackup.Top == BoundaryCondition.Open ? " — free space above" : ""),
                                     BandLeft, 22, bold: true));
            canvas.Children.Add(Note($"Bottom: {tech.Stackup.Bottom}", BandLeft, y + 8, bold: true));
        }
        if (footer is not null) canvas.Children.Add(Note(footer, BandLeft, y + 8, small: true));

        // ── The vias, drawn ACROSS the bands they span ──────────────────────────────────────────
        //
        // A via is a stackup entry like any other, but it is not a layer of the sandwich: it is a
        // connection between two of them, and drawing it as a band in the list is what makes people
        // read it as one. Each is drawn at its own x, spanning exactly the two conductors it names.
        // Spread across the RIGHT-HAND part of the band, never over the left where every band prints
        // its own name: three vias at the old 0.30 spacing put the third one straight through the
        // "Metal2"/"Air"/"MIM Metal" captions. The MMIC stackup grew a third via at MIM-2.
        int viaSlot = 0;
        foreach (var via in vias)
        {
            if (via.SpanFromLayer is not { } a || via.SpanToLayer is not { } b) continue;
            if (!top.ContainsKey(a) || !top.ContainsKey(b)) continue;

            double viaX = BandLeft + BandWidth * (0.68 - 0.18 * viaSlot);
            viaSlot++;

            double y0 = Math.Min(top[a], top[b]), y1 = Math.Max(bot[a], bot[b]);
            canvas.Children.Add(new Border
            {
                Width = 16, Height = y1 - y0,
                Background = new SolidColorBrush(Color.FromArgb(210, 120, 120, 128)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 60, 60, 68)),
                BorderThickness = new Thickness(1),
                [Canvas.LeftProperty] = viaX,
                [Canvas.TopProperty]  = y0,
            });
            canvas.Children.Add(Note(via.Name, viaX + 22, 0.5 * (y0 + y1) - 8, small: true));
        }

        return new FigureScene(canvas);
    }

    /// <summary>
    /// One band of the sandwich. A conductor takes its colour from the DRAWING layer it is bound to,
    /// so the picture and the layout editor agree about which metal is which; a dielectric is drawn
    /// as neutral fill, because it is not something anyone draws on.
    /// </summary>
    private static Control Band(StackupLayer band, Technology tech, double y, double h)
    {
        bool ground = band is { Kind: StackupKind.Conductor, IsGroundReference: true };

        var fill = band.Kind == StackupKind.Conductor
            ? Metal(band, tech)
            : new SolidColorBrush(Color.FromArgb(60, 120, 130, 145));

        return new Border
        {
            Width = BandWidth, Height = h,
            Background = fill,
            // The ground reference is the one band a reader has to be able to find, so it is the one
            // band with a heavy edge. Every other distinction in this picture is a label.
            BorderBrush = new SolidColorBrush(ground
                ? Color.FromArgb(255, 30, 130, 200)
                : Color.FromArgb(90, 110, 110, 120)),
            BorderThickness = new Thickness(ground ? 2.5 : 1),
            [Canvas.LeftProperty] = BandLeft,
            [Canvas.TopProperty]  = y,
        };
    }

    private static IBrush Metal(StackupLayer band, Technology tech)
    {
        foreach (var key in band.DrawingLayers)
            foreach (var def in tech.Layers)
                if (def.Key.Equals(key))
                    return new SolidColorBrush(Color.FromArgb(def.Color.A, def.Color.R, def.Color.G, def.Color.B));

        return new SolidColorBrush(Color.FromArgb(255, 190, 150, 90));
    }

    /// <summary>The band's own caption on the left, and what it is made of on the right.</summary>
    private static IEnumerable<Control> BandLabels(StackupLayer band, double y, double h)
    {
        double mid = y + 0.5 * h - 9;
        yield return Note(band.Name, BandLeft + 10, mid, bold: true, onBand: true);

        string spec = band.Kind == StackupKind.Conductor
            ? $"{Eng(MicronsOf(band.ThicknessDbu))} µm thick, σ = {band.SigmaSm:0.0e+0} S/m"
            : $"{Eng(MicronsOf(band.ThicknessDbu))} µm thick, εr = {band.Epsr:0.###}, tanδ = {band.TanD:0.####}";
        yield return Note(spec, LabelLeft, mid, small: true);

        if (band is { Kind: StackupKind.Conductor, IsGroundReference: true })
            yield return Note("◄ ground reference: every port's − terminal",
                              LabelLeft, mid + 15, small: true, accent: true);

        // MIM-7 — the tie, marked, because it is the one thing about this band a reader cannot infer
        // from the picture: it is drawn as a layer of the sandwich like any other, and it is the only
        // one that is not always there.
        if (band is { Kind: StackupKind.Dielectric, PresentWithLayer: { Length: > 0 } plate })
            // Kept short deliberately: the right-hand label column is BandWidth-limited, and a
            // sentence long enough to explain the tie would run off the canvas. The chapter's own
            // text explains it; this only has to be findable.
            yield return Note($"◄ patterned with '{plate}' — only in runs that analyse it",
                              LabelLeft, mid + 15, small: true, accent: true);
    }

    /// <summary>
    /// A dielectric's drawn height: compressed, bounded, and monotone in the real thickness — so a
    /// thicker layer still looks thicker, without a 33:1 ratio driving every metal to a hairline.
    /// </summary>
    private static double DielectricHeight(StackupLayer band)
    {
        double um = Math.Max(MicronsOf(band.ThicknessDbu), 0.1);
        return Math.Clamp(30 + 26 * Math.Log10(1 + um), 34, 96);
    }

    private static string Eng(double um) =>
        um >= 100 ? um.ToString("0", CultureInfo.InvariantCulture)
      : um >= 1   ? um.ToString("0.##", CultureInfo.InvariantCulture)
      :             um.ToString("0.###", CultureInfo.InvariantCulture);

    private static TextBlock Note(string text, double x, double y, bool bold = false, bool small = false,
                                  bool accent = false, bool onBand = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = small ? 11.5 : 13,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            [Canvas.LeftProperty] = x,
            [Canvas.TopProperty]  = y,
        };

        // A band's own name sits ON the metal, whose colour is the technology's and is the same in
        // both variants — so it takes a fixed dark ink rather than the theme's, which would vanish
        // into copper in the dark variant. Everything outside a band uses the theme's own foreground.
        if (onBand) t.Foreground = new SolidColorBrush(Color.FromArgb(255, 25, 25, 30));
        if (accent) t.Foreground = new SolidColorBrush(Color.FromArgb(255, 30, 130, 200));
        return t;
    }
}
