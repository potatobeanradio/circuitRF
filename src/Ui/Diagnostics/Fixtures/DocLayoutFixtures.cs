using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
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
}
