using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Views.Layout;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// Layout-editor figures: a layout with real artwork on it, and the geometry-snap glyphs.
/// </summary>
public static class DocLayoutFixtures
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    // ── A small, real layout ──────────────────────────────────────────────────

    /// <summary>
    /// A short microstrip run with a mitred bend and a ground via — the smallest piece of artwork
    /// that still has every feature the snap glyphs point at: corners, edge midpoints, a centroid, a
    /// crossing, and a via.
    /// </summary>
    internal static LayoutView Artwork()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var top   = Layer(tech, "Top Copper");
        var drill = Layer(tech, "Drill");

        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };

        // The through line, its mitred corner, and the stub that crosses it.
        view.Shapes.Add(new RectShape    { Layer = top, X1 = Um(0),  Y1 = Um(0),  X2 = Um(240), Y2 = Um(60) });
        view.Shapes.Add(new PolygonShape { Layer = top, Xy = [Um(240), Um(0), Um(300), Um(0), Um(300), Um(180),
                                                              Um(240), Um(180), Um(240), Um(60)] });
        view.Shapes.Add(new RectShape    { Layer = top, X1 = Um(120), Y1 = Um(-90), X2 = Um(160), Y2 = Um(150) });
        view.Shapes.Add(new ViaShape     { Layer = drill, X = Um(270), Y = Um(140),
                                           PadSize = Um(40), DrillSize = Um(20) });

        // A declared connection point, so the highest-priority glyph has something to point at.
        view.Pins.Add(new LayoutPin { Name = "in", X = Um(0), Y = Um(30), WidthDbu = Um(60),
                                      OutwardDeg = 180, Layer = top });
        return view;
    }

    /// <summary>A named layer of the starter technology, or a hard failure naming what it does have.</summary>
    private static LayerKey Layer(Technology tech, string name)
    {
        foreach (var l in tech.Layers) if (l.Name == name) return l.Key;
        throw new InvalidOperationException(
            $"The PCB starter technology no longer declares a '{name}' layer. It declares: "
          + string.Join(", ", tech.Layers.Select(l => l.Name)) + ".");
    }

    private static LayoutEditorViewModel EditorVm(LayoutView view)
        => new(view) { Technology = StarterTechnologies.Pcb2Layer() };

    // ── The artwork the SNAP GLYPHS are queried against ───────────────────────

    /// <summary>
    /// The same run, drawn as ONE polygon, with no via.
    ///
    /// <para><b>Why this is separate from <see cref="Artwork"/>.</b> The layout-editor figure wants a
    /// layout with things in it — a via, overlapping shapes, the ordinary texture of artwork. The
    /// snap strip wants the opposite: six small panels in which the only thing a reader should look
    /// at is a glyph. Drawn as three overlapping rectangles the crossing region shades darker, and a
    /// via draws a filled bullseye bigger than any of the glyphs — both were pulling the eye away
    /// from the thing the figure is of (owner, 2026-08-20).</para>
    ///
    /// <para><b>The pad is a SECOND shape, and it has to be.</b> A snap Intersection is where two
    /// DIFFERENT shapes' edges cross (<c>LayoutSnapQuery.AddIntersectionCandidates</c>), so a single
    /// unioned polygon cannot produce one and this fixture's own check would fail. The pad therefore
    /// abuts the trunk exactly — its top edge lies ON the trunk's bottom edge — which puts its two
    /// side-edge endpoints on that edge and yields a genuine intersection with <b>zero overlapping
    /// area</b>, so it still reads as one piece of metal.</para>
    /// </summary>
    private static LayoutView SnapArtwork()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var top  = Layer(tech, "Top Copper");

        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };

        // The through line, the stub and the corner, as one outline.
        view.Shapes.Add(new PolygonShape
        {
            Layer = top,
            Xy = [Um(0), Um(0), Um(120), Um(0), Um(120), Um(-90), Um(160), Um(-90),
                  Um(160), Um(0), Um(300), Um(0), Um(300), Um(180), Um(240), Um(180),
                  Um(240), Um(60), Um(160), Um(60), Um(160), Um(150), Um(120), Um(150),
                  Um(120), Um(60), Um(0), Um(60)],
        });

        // The abutting pad: top edge exactly on the trunk's bottom edge at y = 0.
        view.Shapes.Add(new RectShape { Layer = top, X1 = Um(40), Y1 = Um(-50), X2 = Um(90), Y2 = Um(0) });

        view.Pins.Add(new LayoutPin { Name = "in", X = Um(0), Y = Um(30), WidthDbu = Um(60),
                                      OutwardDeg = 180, Layer = top });
        return view;
    }

    /// <summary>The layout editor with real artwork open, rather than an empty canvas.</summary>
    public static FigureScene LayoutEditorWithArtwork() => new(new LayoutEditorView
    {
        DataContext = new LayoutDocument("Microstrip bend", EditorVm(Artwork())),
    });

    // ── Ruler annotations ─────────────────────────────────────────────────────

    /// <summary>
    /// The same artwork with three RULERS on it — the three things a ruler is actually used for.
    ///
    /// <para>One measures a trace width across the trunk, one the diagonal clearance between the stub
    /// and the via (the measurement a Manhattan document most wants and cannot take with the grid),
    /// and one carries a CAPTION and its Δx/Δy components. The third is deliberately
    /// <see cref="RulerSizeMode.Scaled"/>, because Fixed and Scaled are the one ruler property a
    /// reader has to see the difference between.</para>
    ///
    /// <para>Every ruler here is a <see cref="RulerAnnotation"/> in <c>LayoutView.Rulers</c> — never a
    /// shape — so this figure is also the only place the documentation shows that collection at all.
    /// </para>
    /// </summary>
    internal static LayoutView RulerArtwork()
    {
        var view = Artwork();

        // Across the trunk: a plain width measurement, the commonest use of the tool.
        view.Rulers.Add(new RulerAnnotation
        {
            X1 = Um(60), Y1 = Um(0), X2 = Um(60), Y2 = Um(60),
        });

        // The diagonal gap between the stub's top-right corner and the bend's lower-left one —
        // free-angle, which is the measurement a Manhattan document most wants and cannot express as
        // artwork. Both endpoints sit well inside the frame the canvas's own initial fit produces,
        // which is why this measures the bend rather than the via out at the right edge.
        view.Rulers.Add(new RulerAnnotation
        {
            X1 = Um(160), Y1 = Um(150), X2 = Um(240), Y2 = Um(60),
            ShowComponents = true,
            Caption = "stub to bend",
        });

        // Scaled text, so the figure shows both size modes at once.
        view.Rulers.Add(new RulerAnnotation
        {
            X1 = Um(0), Y1 = Um(-40), X2 = Um(300), Y2 = Um(-40),
            SizeMode = RulerSizeMode.Scaled,
            TextHeightDbu = Um(9),
        });

        return view;
    }

    /// <summary>The layout editor with rulers placed over the artwork.</summary>
    public static FigureScene LayoutRulers()
    {
        var view = RulerArtwork();
        if (view.Rulers.Count == 0)
            throw new InvalidOperationException(
                "The ruler figure's own view carries no rulers, so the picture would be of the plain "
              + "layout editor with a misleading caption under it.");

        return new FigureScene(new LayoutEditorView
        {
            DataContext = new LayoutDocument("Microstrip bend", EditorVm(view)),
        });
    }

    // ── The geometry-snap glyphs ──────────────────────────────────────────────

    /// <summary>
    /// Every snap glyph, drawn by the real renderer from a real query.
    ///
    /// <para>Each panel puts the cursor at a point that produces that one feature kind, runs the
    /// production <c>LayoutSnapQuery</c>, and hands the winning candidate to the canvas — so the
    /// glyphs here are the glyphs the editor draws, not six approximations of them. A cursor point
    /// that stops producing the kind it is meant to demonstrate <b>fails the docs build</b>, which is
    /// the only way a figure like this can be kept honest.</para>
    /// </summary>
    public static FigureScene SnapGlyphs()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        foreach (var (kind, at, caption) in SnapProbes())
            panel.Children.Add(SnapPanel(kind, at, caption));

        return new FigureScene(panel);
    }

    /// <summary>
    /// One cursor position per feature kind, chosen against <see cref="SnapArtwork"/>.
    /// The intersection probe sits where the pad's side edge meets the trunk's bottom edge.
    /// </summary>
    private static IEnumerable<(SnapFeatureKind Kind, (long X, long Y) At, string Caption)> SnapProbes()
    {
        yield return (SnapFeatureKind.Pin,            (Um(2),   Um(32)),  "Pin");
        yield return (SnapFeatureKind.CornerEndpoint, (Um(302), Um(178)), "Corner / endpoint");
        yield return (SnapFeatureKind.Intersection,   (Um(40),  Um(1)),   "Intersection");
        yield return (SnapFeatureKind.Midpoint,       (Um(300), Um(90)),  "Midpoint");
        yield return (SnapFeatureKind.Centroid,       (Um(65),  Um(-25)), "Centroid");
        yield return (SnapFeatureKind.Nearest,        (Um(200), Um(2)),   "Nearest");
    }

    private static Control SnapPanel(SnapFeatureKind kind, (long X, long Y) at, string caption)
    {
        var vm = EditorVm(SnapArtwork());
        var counters = new SnapQueryCounters();
        var candidates = LayoutSnapQuery.FindCandidates(
            vm.Model, vm.Technology, baseDir: "", at.X, at.Y,
            tolDbu: Um(6), includeIntersections: true,
            excludeShapeIndices: null, excludeInstanceIndices: null, ref counters);

        // Cast to nullable BEFORE the search. SnapFeatureKind.Pin is enum value 0, so a
        // FirstOrDefault miss returns a default SnapCandidate whose Kind IS Pin, at world (0, 0) —
        // which passed the check below and drew a glyph in the wrong place, silently. It did exactly
        // that on the first run of this fixture.
        var chosen = candidates.Cast<SnapCandidate?>().FirstOrDefault(c => c!.Value.Kind == kind);
        if (chosen is null)
            throw new InvalidOperationException(
                $"The snap-glyph figure asked for a {kind} candidate at ({at.X}, {at.Y}) and the query "
              + $"returned [{string.Join(", ", candidates.Select(c => c.Kind))}]. Either the probe point "
              + "or the sample artwork has drifted; a figure captioned '" + caption + "' showing some "
              + "other glyph would be worse than no figure at all.");

        vm.SetOverlaySnapMarker(chosen.Value);

        var canvas = new LayoutCanvas { ViewModel = vm, Width = 150, Height = 150, ClipToBounds = true };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                    Child = canvas,
                },
                // The caption is width-clamped to the canvas. Left to size itself, "Corner /
                // endpoint" is wider than the 150 px panel and stretches its whole column, which
                // pushed the sixth glyph off the right edge of the figure.
                new TextBlock
                {
                    Text = caption,
                    FontSize = 11,
                    Width = 150,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
            },
        };
    }

    // ── The EM Setup panel, pointed at real artwork ───────────────────────────

    /// <summary>
    /// The bend of the MoM chapter's worked example, with a port label on each end.
    ///
    /// <para>Separate from <see cref="Artwork"/> because the two figures want different things: the
    /// snap glyphs need a crossing and a via to point at, and the EM panel needs a geometry the
    /// planar extractor accepts with two unambiguous ports. Sharing one would compromise both.</para>
    /// </summary>
    private static LayoutView BendArtwork()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var top  = Layer(tech, "Top Copper");

        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };

        // A 600 um run in x, a right-angle corner, and a 600 um run in y. 500 um wide, which is
        // roughly 50 ohm on the starter PCB stackup.
        view.Shapes.Add(new RectShape { Layer = top, X1 = Um(0),    Y1 = Um(0), X2 = Um(1100), Y2 = Um(500) });
        view.Shapes.Add(new RectShape { Layer = top, X1 = Um(600),  Y1 = Um(0), X2 = Um(1100), Y2 = Um(1100) });

        view.Shapes.Add(new LabelShape
        {
            Layer = top, X = Um(0), Y = Um(250), Text = "1", Height = Um(120),
            IsPort = true, PortDirection = LayoutRotation.R0,
        });
        view.Shapes.Add(new LabelShape
        {
            Layer = top, X = Um(850), Y = Um(1100), Text = "2", Height = Um(120),
            IsPort = true, PortDirection = LayoutRotation.R270,
        });
        return view;
    }

    /// <summary>
    /// The EM Setup panel with a layout actually resolved behind it: the kernel choice and its
    /// reason, the cross-section readback, the resolved ports and the technology's stackup are all
    /// the panel's own, computed from the artwork above rather than typed into a mock.
    /// </summary>
    public static FigureScene EmSetupWithLayout()
    {
        var setup = new EmSetup
        {
            Name      = "bend",
            LayoutRef = "bend.clay",
        };
        var vm = new EmSetupEditorViewModel("bend.cem", setup);
        var view = BendArtwork();
        var tech = StarterTechnologies.Pcb2Layer();
        vm.ResolveLayout = _ => new EmLayoutSource("bend.clay", view, tech, Dbu);
        vm.BuildActiveMesh(null);   // Refresh + mesh only. No solve: this is the cheap answer.

        if (vm.SelectedKernelName.Length == 0)
            throw new InvalidOperationException(
                "The EM Setup figure resolved no kernel for its own artwork. A panel figure showing "
              + "an unresolved setup would document the empty state, not the populated one.");

        return new FigureScene(new EmSetupEditorView
        {
            DataContext = new EmSetupDocument("bend", vm, "bend.cem"),
        });
    }

    // ── Port placement: what a correct EM port setup actually looks like ──────
    //
    // Two figures, one per port type, both drawn by the REAL renderer from real artwork and real
    // port labels. The metal is a genuine MKLOPF, generated by the shipping PCell rather than
    // approximated with a polygon, so the widths a reader sees are the widths that generator
    // produces for those impedances on that stackup.

    /// <summary>
    /// The Klopfenstein taper the port figures are drawn on.
    ///
    /// <para><b>25 → 75 Ω over 10 mm, and every one of those numbers is chosen for LEGIBILITY rather
    /// than for realism.</b> The port marker's bar spans the port's own width and its arrow is
    /// 0.66 × that width (clamped by the conductor's length), so a part whose length dwarfs its
    /// widths renders two marks too small to read — which is exactly what a 50 → 100 Ω taper over
    /// 20 mm, the ordinary case, would give. A wide low-impedance end, a genuinely narrower high one,
    /// and an aspect ratio near 2:1 puts both markers on screen at a size a reader can compare.</para>
    /// </summary>
    private static LayoutView KlopfTaper()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };

        var result = MKlopfPCell.Generate(
            new Dictionary<string, PCellValue>
            {
                ["Z1"] = PCellValue.Real(25.0),
                ["Z2"] = PCellValue.Real(75.0),
                ["L"]  = PCellValue.Real(10e-3),
            },
            tech, PCellLayerSelection.Default);

        if (result.Shapes.Count == 0)
            throw new InvalidOperationException(
                "The MKLOPF generator produced no artwork for the port figures. A port figure over an "
              + "empty canvas would document nothing at all.");

        foreach (var s in result.Shapes) view.Shapes.Add(s);
        return view;
    }

    /// <summary>The taper's own bounding box, which is where the ports have to land. Measured from
    /// the artwork rather than assumed from the generator's parameters — the whole point of using the
    /// real PCell is that its widths are its own.</summary>
    private static Bbox Extent(LayoutView view)
    {
        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        if (bb.IsEmpty)
            throw new InvalidOperationException("The port figure's artwork has no extent to place ports on.");
        return bb;
    }

    /// <summary>
    /// A port label, placed and pointed the way the Port tool places one.
    ///
    /// <para><b>The direction is STATED rather than left to be inferred</b>, because that is what the
    /// tool writes and what these figures are teaching. An inferred direction is drawn identically —
    /// the difference is only in whether the run's notes say "inferred" — so nothing about the
    /// picture depends on the choice.</para>
    /// </summary>
    private static LabelShape PortLabel(LayerKey layer, string text, long x, long y,
                                        LayoutRotation direction, long heightDbu)
        => new()
        {
            Layer = layer, X = x, Y = y, Text = text, Height = heightDbu,
            IsPort = true, PortDirection = direction,
        };

    /// <summary>
    /// <b>Edge ports, one at each end of a Klopfenstein taper</b> — the setup the overwhelming
    /// majority of EM runs have, drawn so a reader can see what "placed correctly" looks like.
    ///
    /// <para>Each label sits at the CENTRE of its own end face, which is the placement that resolves
    /// without a guess: at a corner it is equally close to two edges, and that is refused by name
    /// rather than guessed, because guessing reverses the direction of current into the structure.
    /// The two directions point INWARD, at each other — current flows into the structure through
    /// both ports, which is what an edge port means on both ends of a two-port.</para>
    /// </summary>
    public static FigureScene EdgePortsOnATaper()
    {
        var view = KlopfTaper();
        var tech = StarterTechnologies.Pcb2Layer();
        var top  = Layer(tech, "Top Copper");
        var bb  = Extent(view);
        long yc = (bb.MinY + bb.MaxY) / 2;
        long h  = Um(900);      // small, for the reason the gap fixture's own note gives

        view.Shapes.Add(PortLabel(top, "1", bb.MinX, yc, LayoutRotation.R0,   h));
        view.Shapes.Add(PortLabel(top, "2", bb.MaxX, yc, LayoutRotation.R180, h));

        return Framed(new LayoutDocument("Klopfenstein taper", EditorVm(view)), 560, 420);
    }

    /// <summary>
    /// <b>An internal delta-gap port in the middle of the same taper, with the two edge ports still
    /// on its ends</b> — so the two marks are side by side and a reader can tell them apart without
    /// being told which is which.
    ///
    /// <para>All three ports are shown deliberately. An internal port is almost never the only port
    /// on a structure: power still has to get in and out somewhere, and the thing a reader needs to
    /// see is the CONTRAST — an edge port's bar-and-serifs at a boundary against a gap's two bracketed
    /// bars facing each other across a break in the middle.</para>
    ///
    /// <para><b>The gap's own mark only appears because this fixture sets
    /// <c>InternalPortMarks</c>.</b> The port type lives in the <c>.cem</c>, never on the label, so a
    /// layout on its own cannot know it — the EM Setup editor publishes the anchors and the renderer
    /// is told. That is the same path the live application takes, which is why this figure is a
    /// picture of the real thing rather than of a fixture-only drawing mode.</para>
    /// </summary>
    public static FigureScene InternalGapPortOnATaper()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var top  = Layer(tech, "Top Copper");

        // ── A UNIFORM LINE, NOT THE TAPER, AND THE REASON IS WHAT THE FIGURE IS FOR ──────────────
        //
        // On the taper the three marks are three different widths — port 1's is the wide end, port
        // 2's the narrow one, the gap's whatever the flank is where it lands — and the edge arrow,
        // scaled to a 2.9 mm-wide port, runs straight through the gap's own brackets. All of that is
        // the renderer being CORRECT, and all of it works against the one thing this figure has to
        // show: that the two MARKS are different from each other. Holding the width constant is what
        // isolates the comparison.
        //
        // It is also the truer example. Breaking a uniform trace to drop a series component into it
        // is what an internal port is actually for; a gap partway down a taper is a thing you can do
        // rather than a thing anyone does.
        var (view, xc, yc) = SeriesGapLine();
        var vm = EditorVm(view);
        vm.InternalPortMarks = [(xc, yc, PlanarPortKind.InternalDeltaGap)];

        return Framed(new LayoutDocument("Series gap", vm), 880, 300);
    }

    /// <summary>
    /// <b>An internal SHUNT port on a ground via at the middle of the same line</b>, with the two
    /// edge ports still on its ends — the third mark, drawn beside the two it has to be told apart
    /// from.
    ///
    /// <para>A via is drawn under it, which is the case where the user has one: the port drives that
    /// via rather than a built one. The port does NOT require it — placed on bare metal the solver
    /// builds the path itself — but a figure of one is the better picture of the two, because the
    /// via is the thing a reader has to be told the port is standing on when it is there. It is the
    /// same line the gap figures use, for the same reason those two share theirs: hold everything
    /// constant except the mark being read.</para>
    ///
    /// <para><b>The mark is deliberately not oriented by the trace</b>, and the figure is where that
    /// is easiest to see: an edge port's bar and a gap's brackets both say which way current
    /// crosses a plane IN the layout, while an internal port's current leaves the plane altogether. A
    /// ring and a ground symbol say that without claiming a direction the port does not have.</para>
    /// </summary>
    public static FigureScene InternalPortOnALine()
    {
        var tech  = StarterTechnologies.Pcb2Layer();
        var drill = Layer(tech, "Drill");

        var (view, xc, yc) = SeriesGapLine();
        view.Shapes.Insert(1, new ViaShape { Layer = drill, X = xc, Y = yc,
                                             PadSize = Um(700), DrillSize = Um(360) });

        var vm = EditorVm(view);
        vm.InternalPortMarks = [(xc, yc, PlanarPortKind.Internal)];

        return Framed(new LayoutDocument("Internal via", vm), 880, 300);
    }

    /// <summary>
    /// The artwork both gap figures use — a 50 Ω line with edge ports at its ends and a gap port at
    /// its centre. Shared so the pair differ in exactly ONE thing: whether a mesh exists.
    ///
    /// <para>The LENGTH is chosen against the figure's own frame rather than against realism: a
    /// port's marker and its label both stick out past the metal, so a part whose aspect ratio
    /// matches the canvas's crops the outermost port. Three widths of slack. The label HEIGHT is
    /// small for the second half of the same reason — a label's text is drawn outward from its anchor
    /// and is NOT in the bounding box the canvas frames its content by.</para>
    /// </summary>
    private static (LayoutView View, long Xc, long Yc) SeriesGapLine()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var top  = Layer(tech, "Top Copper");

        long w = Um(2900), len = Um(9000);   // ~3:1, which is what two edge arrows plus a gap need
        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };
        view.Shapes.Add(new RectShape { Layer = top, X1 = 0, Y1 = 0, X2 = len, Y2 = w });

        long yc = w / 2, xc = len / 2, h = Um(380);

        view.Shapes.Add(PortLabel(top, "1", 0,   yc, LayoutRotation.R0,   h));
        view.Shapes.Add(PortLabel(top, "2", len, yc, LayoutRotation.R180, h));
        view.Shapes.Add(PortLabel(top, "3", xc,  yc, LayoutRotation.R0,   h));

        return (view, xc, yc);
    }

    /// <summary>
    /// <b>The same gap, with the surface mesh computed</b> — so the break is drawn at the width the
    /// solver will actually use instead of at the legibility fraction.
    ///
    /// <para>The mesh is built by the real mesher from the real problem, not mocked: the whole claim
    /// the figure makes is that those two brackets land on the mesh's own gridlines, and a fabricated
    /// mesh would make the picture agree with itself and with nothing else.</para>
    /// </summary>
    public static FigureScene InternalGapPortAtMeshWidth()
    {
        var (view, xc, yc) = SeriesGapLine();
        var vm = EditorVm(view);
        vm.InternalPortMarks = [(xc, yc, PlanarPortKind.InternalDeltaGap)];

        var tech    = StarterTechnologies.Pcb2Layer();
        var planar  = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 20e9);
        if (!planar.Ok) throw new InvalidOperationException(
            "The mesh-width port figure could not extract its own artwork: " + planar.Refusal);

        var report = SurfaceMesher.Mesh(planar.Problem!, PlanarMeshSettings.Default);
        if (report.Mesh.Cells.Count == 0) throw new InvalidOperationException(
            "The mesh-width port figure meshed to nothing, so it would show a gap at its default "
          + "width and caption it as the mesh's — which is the one thing it must not do.");

        vm.PlanarMeshReport = report;
        vm.ShowPlanarMesh   = true;

        return Framed(new LayoutDocument("Series gap", vm), 880, 300);
    }

    /// <summary>
    /// <b>The bare canvas, not the whole editor</b>, framed on its own artwork.
    ///
    /// <para>Two reasons, and the first is a hard constraint rather than a preference. <b>The layout
    /// editor VIEW does not fit in a figure.</b> Its toolbar wraps and its status bar runs past the
    /// right edge at 900 px and again at 1100, so every capture of it cropped the right-hand side —
    /// which for these figures is precisely where port 2 is. The chrome is documented by the
    /// <c>layout-editor</c> figure already; repeating it here costs the thing this figure is of.</para>
    ///
    /// <para>The second is that a port marker is small and the metal around it is the context. All
    /// the width goes to the artwork.</para>
    ///
    /// <para><b>The viewport is COMPUTED here and set, rather than asked for.</b> Neither
    /// <c>ZoomToFit</c> nor <c>ZoomToRegion</c> frames these reliably: Zoom to Fit reads
    /// <c>Bounds</c>, which during a headless capture is not the size the canvas is finally arranged
    /// at, so it consistently framed slightly too tight and cropped port 2 — measured at four
    /// different part lengths, cropped at every one. <c>ZoomToRegion</c> is worse, because it grows a
    /// "degenerate" region to a multiple of the layout's own SNAP step, which on a 1 mil PCB snap is
    /// larger than these parts and zooms them to a dot.
    ///
    /// <para>The size is known here — this fixture set it — so the arithmetic is done against the
    /// number rather than against a property that may not have caught up yet. <c>SetViewport</c> is
    /// the same public seam the View menu's zoom commands write through.</para></para>
    /// </summary>
    /// <param name="marginX">
    /// Slack at each end ALONG the ports' own axis, as a fraction of the artwork's extent. Generous,
    /// and separate from <paramref name="marginY"/> for a reason: an edge port's arrow approaches its
    /// reference plane from OUTSIDE the metal, so the marker sticks out along this axis by a
    /// fraction of the port's width — while across it, only the bar's own overhang does. One margin
    /// for both either crops the arrows or shrinks the part to fit slack it does not need.
    /// </param>
    /// <param name="marginY">Slack across that axis. Small: nothing reaches far this way.</param>
    private static FigureScene Framed(LayoutDocument doc, int width, int height,
                                      double marginX = 0.30, double marginY = 0.10)
    {
        var canvas = new LayoutCanvas
        {
            ViewModel = doc.ViewModel, Width = width, Height = height, ClipToBounds = true,
        };
        var framed = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            Child = canvas,
        };

        var bb = Bbox.Empty;
        foreach (var s in doc.ViewModel.Model.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        if (bb.IsEmpty)
            throw new InvalidOperationException("A port figure was built on artwork with no extent.");

        double worldW = (bb.MaxX - bb.MinX) * (1 + 2 * marginX);
        double worldH = (bb.MaxY - bb.MinY) * (1 + 2 * marginY);
        double zoom   = Math.Min(width / worldW, height / worldH);

        double cx = 0.5 * (bb.MinX + bb.MaxX), cy = 0.5 * (bb.MinY + bb.MaxY);
        var vp = new LayoutViewport(
            cx - width / (2.0 * zoom), cy - height / (2.0 * zoom), zoom, width, height);

        // Set at construction AND after layout: the canvas frames its own content once, on the
        // first layout pass at which its bounds become valid, and which of the two writes lands last
        // depends on an ordering this fixture cannot see. Writing the same viewport twice is
        // idempotent, so it does not have to know.
        // Set at construction AND after layout: the canvas frames its own content once, on the
        // first layout pass at which its bounds become valid, and which of the two writes lands last
        // depends on an ordering this fixture cannot see. Writing the same viewport twice is
        // idempotent, so it does not have to know.
        canvas.SetViewport(vp);
        return new FigureScene(framed) { AfterLayout = _ => canvas.SetViewport(vp) };
    }
}
