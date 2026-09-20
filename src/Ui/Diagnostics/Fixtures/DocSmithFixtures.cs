using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Design.Smith;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Smith;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Content;
using CircuitRF.Ui.Views.Smith;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Smith Chart document's figures (<c>brief-smith-11-docs-and-example.md</c> <c>R-smith11-2</c>).
/// </summary>
/// <remarks>
/// <b>The window figures are of the example a reader can open</b> — <c>examples/Smith Chart/</c>'s own
/// <c>Gate match.csmith</c>, read from disk by <see cref="SmithDesignIo"/> — which is
/// <see cref="DocRailFixtures"/>' rule and it is the rule for its reason: a chapter that quotes an
/// impedance is then quoting the same evaluation the reader gets, and a change that moves either one
/// moves the picture too.
///
/// <para><b>The three-trajectory figures are NOT</b>, and that is deliberate rather than a shortcut.
/// The shipped example is a two-element L match on purpose (the brief's own words: <i>a demonstration
/// of the idea, not of every element type</i>), and the chapter's opening figure has to show what a
/// per-element trajectory IS — which takes three curves of three different shapes, a constant-resistance
/// arc, a constant-conductance arc and a rotation about the line's own Z₀. So that one design is built
/// here, in code, from the same <see cref="SmithDesign"/> the document format holds.</para>
///
/// <para><b>No window frame and no detaching.</b> Unlike railRF and the Match Designer, the Smith Chart
/// is a docked DOCUMENT and its view is a <c>UserControl</c> — its <c>&lt;UserControl.Styles&gt;</c>
/// block is on the captured control itself, so there is nothing to carry across. A <c>Window</c>
/// subclass appearing in <c>src/Ui/Views/Smith/</c> would mean <c>R-smith4-1</c> had been missed, and
/// this fixture would be the second place it showed.</para>
/// </remarks>
public static class DocSmithFixtures
{
    private const string ExampleFolder = "Smith Chart";
    private const string DocumentName  = "Gate match.csmith";

    // ── the shipped example ──────────────────────────────────────────────────

    /// <summary>The whole document on the shipped example: two trajectories, three load points with
    /// their conjugate targets, the swept band, the network strip and the status strip.</summary>
    public static FigureScene Window() => Document(Example());

    /// <summary>The network strip alone, drawn generator-left.</summary>
    public static FigureScene NetworkStrip() => Strip(Example(), mirrored: false);

    /// <summary>The same strip mirrored — the generator on the right, the cascade growing left.</summary>
    public static FigureScene NetworkStripMirrored() => Strip(Example(), mirrored: true);

    /// <summary>
    /// The shipped example's network after <b>Copy</b>, in the schematic editor where it lands.
    /// </summary>
    /// <remarks>
    /// <b>Built by <see cref="SmithSchematicCopy.Build"/> — the projection the clipboard hands over</b>,
    /// not by a second layout pass that agrees today. What the figure shows is therefore what a paste
    /// produces, including the two <c>TermG</c>s the copy terminates the walk with.
    /// </remarks>
    public static FigureScene CopiedSchematic()
    {
        var design = Example();
        var model  = SmithSchematicCopy.Build(design, ExampleDirectory());
        var view   = new SchematicView
        {
            DataContext = new SchematicDocument("Matched input", new SchematicViewModel(model)),
        };
        return new FigureScene(view) { AfterLayout = ZoomSchematic };
    }

    // ── the three-trajectory teaching design ─────────────────────────────────

    /// <summary>Three elements, three trajectory shapes, three grippers — Q arcs off.</summary>
    public static FigureScene Trajectories() => Document(ThreeElements(q: false));

    /// <summary>The same design with the constant-Q pair drawn.</summary>
    public static FigureScene ConstantQ() => Document(ThreeElements(q: true));

    // ── scenes ───────────────────────────────────────────────────────────────

    private static FigureScene Document(SmithDesign design)
    {
        var vm  = new SmithChartViewModel(design) { DocumentDirectory = ExampleDirectory() };
        var doc = new SmithChartDocument(Path.GetFileNameWithoutExtension(DocumentName), vm);

        var view = new SmithChartView { DataContext = doc };

        return new FigureScene(view)
        {
            AfterLayout = root =>
            {
                // The plot theme is applied by the view's own ActualThemeVariantChanged handler, which
                // has not necessarily fired by the time the generator has chosen the variant — so it
                // is applied here as well, exactly as DocRailFixtures does and for the same reason.
                var theme = ThemeService.CurrentVariant == ColorVariant.Dark
                    ? RenderTheme.Dark : RenderTheme.Light;
                vm.PlotHost.Theme = theme;
                foreach (var plot in root.GetVisualDescendants().OfType<PlotControl>())
                {
                    plot.PlotTheme = theme;
                    plot.InvalidateVisual();
                }

                FitStrip(root);
                UiArtworkGenerator.Pump();
            },
        };
    }

    private static FigureScene Strip(SmithDesign design, bool mirrored)
    {
        design.View.MirrorNetwork = mirrored;

        var vm = new SmithChartViewModel(design) { DocumentDirectory = ExampleDirectory() };

        // The canvas on its own, inside a border of the pane colour the window puts behind it — the
        // strip is the subject, and the generator column and the chart above it are not.
        var canvas = new SmithNetworkCanvas { ViewModel = vm };
        var host = new Border
        {
            Background = Avalonia.Media.Brushes.Transparent,
            Padding    = new Avalonia.Thickness(8),
            Child      = canvas,
        };

        return new FigureScene(host)
        {
            AfterLayout = _ => { canvas.ZoomToFit(); canvas.InvalidateVisual(); UiArtworkGenerator.Pump(); },
        };
    }

    private static void FitStrip(Control root)
    {
        foreach (var canvas in root.GetVisualDescendants().OfType<SmithNetworkCanvas>())
        {
            canvas.ZoomToFit();
            canvas.InvalidateVisual();
        }
    }

    private static void ZoomSchematic(Control root)
    {
        var canvas = root.GetVisualDescendants().OfType<SchematicCanvas>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The copied-schematic figure found no SchematicCanvas, so it would have been an "
              + "empty frame with chrome round it.");
        canvas.ZoomToFit();
        canvas.InvalidateVisual();
    }

    // ── the designs ──────────────────────────────────────────────────────────

    private static string ExampleDirectory()
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException(
                "No examples/ tree beside the generator or above it, so the Smith Chart figures have "
              + "no document. They are OF the shipped example on purpose — see this type's remarks.");
        return Path.Combine(root, ExampleFolder);
    }

    private static SmithDesign Example()
        => SmithDesignIo.LoadFromFile(Path.Combine(ExampleDirectory(), DocumentName));

    /// <summary>
    /// A series L, a shunt C and a series line — the three trajectory shapes in one walk.
    /// </summary>
    /// <remarks>
    /// The L and the C are the ordinary L match that takes 15 − j25 Ω to 50 Ω at 2 GHz, so the walk
    /// is in the middle of the chart when the line starts. The line is then a 75 Ω QUARTER WAVE,
    /// which takes 50 Ω to 75²/50 = 112.5 Ω along a half circle that is plainly centred on 75 Ω and not
    /// on the chart's own middle — the thing §3.3 says a tool with a fixed 50 Ω line could not have
    /// drawn at all. A shorter line was tried first and is the wrong figure: a 20° arc from 50 Ω is
    /// about forty pixels long and the load-point label sits on top of it, so the third curve the
    /// figure exists to show is the one a reader cannot see.
    ///
    /// <para>Q is 1.53 because that is |X|/R at the corner between the first two arcs
    /// (22.9 / 15.0 = 1.525), so the constant-Q figure's arcs pass through the joint whose Q they are
    /// describing rather than through empty chart.</para>
    /// </remarks>
    private static SmithDesign ThreeElements(bool q)
    {
        var d = new SmithDesign { Name = "Three elements" };
        d.Chart.Z0Ohm = 50.0;

        // ONE ROW, so the design frequency — the table's median — is 2 GHz, which is what the L,
        // the C and the line below are all quoted at. See SmithDesign.DesignFrequencyHz.
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 15.0, -25.0));

        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.L, Placement = SmithPlacement.Series, Name = "L1",
            ActiveParameter = SmithParameter.L,
            Values = new SmithElementValues { LHenry = 3.81e-9 },
        });
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.C, Placement = SmithPlacement.Shunt, Name = "C1",
            ActiveParameter = SmithParameter.C,
            Values = new SmithElementValues { CFarad = 2.43e-12 },
        });
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.Tline, Placement = SmithPlacement.Series, Name = "TL1",
            ActiveParameter = SmithParameter.ElectricalLength,
            Values = new SmithElementValues
            {
                Z0Ohm = 75.0, ElectricalLengthDeg = 90.0, ReferenceFrequencyHz = 2.0e9,
            },
        });

        d.ConstantQ.Enabled = q;
        d.ConstantQ.Q       = 1.53;

        if (d.Refusal() is { } r)
            throw new InvalidOperationException("The three-trajectory figure design is not well formed: " + r);

        return d;
    }
}
