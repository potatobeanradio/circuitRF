using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Ui.Views.Content;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Views.Layout;
using CircuitRF.Ui.Views.WBond;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// wBond figures, all built on ONE design: four arrays — <c>G1</c>, <c>G2</c>, <c>D1</c>, <c>D2</c> —
/// carrying ten wires between them.
///
/// <para>One design across the symbol, the editor and the S-parameters is the point: a reader who
/// follows the chapter sees the same four arrays in the schematic, in the layout and on the plot, so
/// "the symbol's pins are the arrays" is something the figures demonstrate rather than assert.</para>
/// </summary>
public static class DocWBondFixtures
{
    private const double MilPerWire = 6.0;

    /// <summary>
    /// A gate-side and drain-side bond arrangement of the shape a packaged device actually has: two
    /// two-wire gate arrays and two three-wire drain arrays, 1 mil gold, 20 mil loop, feet at two
    /// different heights (a die pad up to a substrate lead), which is the case level feet hide.
    ///
    /// <para><b>The wires fly NORTH-SOUTH</b> — along +y — while the gate and drain sides stay left
    /// and right, which is how a packaged device is read (owner, 2026-08-20). Drawn along the
    /// reading axis the bonds sat end-on and told the reader nothing; across it the array structure
    /// is what you see. Two consequences, both load-bearing: the profile must be taken in YZ rather
    /// than XZ (see <see cref="Profile"/>), and the four arrays are spread along x so the design is
    /// wider than it is tall — a tall, narrow design in a landscape editor window fits to a few
    /// per cent of its width and reads as a set of hairlines.</para>
    /// </summary>
    public static WBondDesign FourArrayDesign()
    {
        var design = new WBondDesign();

        Add("G1", xMil:   0, wires: 2, yMil: 0);
        Add("G2", xMil:  30, wires: 2, yMil: 0);
        Add("D1", xMil: 140, wires: 3, yMil: 0);
        Add("D2", xMil: 170, wires: 3, yMil: 0);

        return design;

        void Add(string name, double xMil, int wires, double yMil)
        {
            var array = new WireArray { Name = name };
            for (int w = 0; w < wires; w++)
            {
                double x = xMil + w * MilPerWire;
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(x, yMil, 4),          // die pad, 4 mil above the plane
                    Point3.Mils(x, yMil + 40, 1),     // substrate lead, 1 mil above it
                    WBondUnits.ToNm(1.0, WBondUnit.Mil),
                    WireMaterials.Default.Name,
                    loopHeightNm: WBondUnits.ToNm(20.0, WBondUnit.Mil)));
            }
            design.Arrays.Add(array);
        }
    }

    // ── The schematic side ────────────────────────────────────────────────────

    /// <summary>
    /// The generated schematic symbol for that design — one pin pair per array, named after it.
    ///
    /// <para>A wBond's symbol is not a built-in: it is generated from the design the component
    /// carries, which is exactly why this figure has to place a real component rather than draw a
    /// four-array glyph.</para>
    /// </summary>
    public static FigureScene Symbol()
    {
        var model = new SchematicEditModel();
        model.Components.Add(WBondPlacement.BuildCarrying(FourArrayDesign(), "W1"));

        // Zoom to fit AFTER layout — the capture was clipping the symbol's top and bottom pin rows
        // (owner, 2026-08-20). Fitting is a viewport operation, so it can only be asked of a canvas
        // that has been arranged. This figure is also what found SchematicCanvas.ZoomToFit fitting to
        // a hit-test envelope rather than to what is drawn; see DrawnExtent there.
        var view = new SchematicView { DataContext = new SchematicDocument("wBond", new SchematicViewModel(model)) };
        return new FigureScene(view) { AfterLayout = DocFixtures.ZoomSchematicToFit };
    }

    // ── The workspace views ───────────────────────────────────────────────────
    //
    // These are the views a WORKSPACE produces (owner, 2026-08-20: "don't use the App window
    // version — use the views that get generated from a new workspace"). In a workspace a wBond is
    // the wire layer of a layout cell: the Layout Editor draws and edits the wires, and the Wire
    // Profile and Array Inductance dock panels follow whichever layout is active. The standalone
    // wBond application is a second surface over the same editor and is not what these show.

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>
    /// The layout cell the wires live in.
    ///
    /// <para><b>It carries no artwork, on purpose.</b> It used to draw a bond pad under every wire
    /// foot — twenty small Top Copper squares — and in the figure they read as the subject rather
    /// than as the surface the wires fly over (owner, 2026-08-20: "remove the Top Copper rectangles,
    /// they are too distracting"). The wires themselves are what these figures are about, and a wBond
    /// in a workspace is the wire layer of a layout cell whether or not that cell has copper on it.</para>
    /// </summary>
    private static LayoutView PadArtwork()
    {
        var tech = StarterTechnologies.Pcb2Layer();

        return new LayoutView
        {
            DbuPerMicron = Dbu,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };
    }

    /// <summary>A layout editor on a wirebond cell, wires attached exactly as the workspace attaches them.</summary>
    private static LayoutEditorViewModel LayoutWithWires()
    {
        var vm = new LayoutEditorViewModel(PadArtwork()) { Technology = StarterTechnologies.Pcb2Layer() };
        vm.AttachWireDesign(FourArrayDesign(), "arrays.wBond");
        if (vm.WireEditor is null)
            throw new InvalidOperationException(
                "Attaching a wire design to the layout editor left it with no wire editor, so every "
              + "wBond figure below would show artwork with no wires on it.");
        return vm;
    }

    /// <summary>
    /// The Layout Editor with the four arrays' ten wires on it.
    ///
    /// <para>Zoomed to fit after layout. With no copper in the cell there is no shape extent to open
    /// on, and the wires — an overlay by design — do not drive the initial view; the capture came
    /// back as ten hairlines in the middle of an empty board.
    /// <c>LayoutCanvas.ZoomToFitInternal</c> unions the overlay's own bounds, which is exactly the
    /// case this needs.</para>
    /// </summary>
    public static FigureScene LayoutWires()
        => new(new LayoutEditorView { DataContext = new LayoutDocument("Bond arrays", LayoutWithWires()) })
           { AfterLayout = DocFixtures.ZoomLayoutToFit };

    /// <summary>
    /// The Wire Profile dock panel — the side view, where loop height and span are what you see and
    /// what alt-drag scales.
    /// </summary>
    public static FigureScene Profile()
    {
        var layout = LayoutWithWires();

        // These wires fly along +y, so the profile has to be taken in the YZ plane; in XZ they would
        // be drawn edge-on and the figure would show ten vertical sticks. Picking the plane for the
        // geometry is what a user does; the fixture does the same thing through the same commit path.
        if (!layout.WireEditor!.CommitProfileAxisText(ProfileAxisSetting.YzLabel))
            throw new InvalidOperationException(
                "The Wire Profile view refused 'YZ' as a plane, so the profile figure would be drawn "
              + "down the wires' own axis.");

        var tool = new ViewModels.Dock.WBondProfileTool();
        tool.SetActiveLayout(layout);
        return new FigureScene(new WBondProfileToolView { DataContext = tool });
    }

    /// <summary>The Array Inductance dock panel, computed from those same ten wires.</summary>
    public static FigureScene InductancePanel()
    {
        var tool = new ViewModels.Dock.WBondInductanceTool();
        tool.SetActiveLayout(LayoutWithWires());
        return new FigureScene(new WBondInductanceToolView { DataContext = tool });
    }

    // ── The S-parameters ──────────────────────────────────────────────────────

    /// <summary>
    /// A rectangular plot of the design's own exported Touchstone.
    ///
    /// <para><b>The file is exported here, by the shipped exporter, and then read back through the
    /// ordinary Touchstone data-source path.</b> Nothing is drawn from an array of numbers this
    /// fixture made up: what the plot shows is what a user gets from Export Touchstone… on this
    /// design.</para>
    /// </summary>
    public static FigureScene SParameters()
    {
        string path = ExportTouchstone();

        var vm = new DataDisplayDocumentViewModel();
        var lib = vm.Window.DataSourceLibrary;
        lib.KnownTouchstoneProvider = () => [path];
        lib.RefreshAvailableDataSources();
        DocDataDisplayFixtures.Await(lib.SelectDataSourceAsync(path));

        if (lib.SelectedEntry is null)
            throw new InvalidOperationException(
                $"The exported wBond Touchstone '{path}' was written but the data-source library did "
              + "not accept it, so the figure would be an empty axis frame.");

        var display = vm.Window.DataDisplay
            ?? throw new InvalidOperationException("The Data Display document has no active tab.");
        var plot = display.Plots.FirstOrDefault()
            ?? throw new InvalidOperationException("The Data Display seeded no plot to configure.");

        plot.Inspector.PlotType = PlotType.Rect;
        plot.Width  = 620;
        plot.Height = 380;

        // Two traces: one array's own through response, and the coupling into the array beside it.
        // Both are picked BY LABEL, so a change in what the exporter publishes fails the docs build
        // rather than quietly drawing a different curve.
        plot.Inspector.AddTraceCommand.Execute(null);
        DocDataDisplayFixtures.PickSignal(plot.Inspector.Traces[0], "S(2,1)");

        if (plot.Inspector.AddTraceCommand.CanExecute(null))
        {
            plot.Inspector.AddTraceCommand.Execute(null);
            DocDataDisplayFixtures.PickSignal(plot.Inspector.Traces[^1], "S(3,1)");
        }

        if (plot.Inspector.Traces.Count == 0)
            throw new InvalidOperationException("The wBond S-parameter plot ended up with no traces.");

        return new FigureScene(new DataDisplayView
        {
            DataContext = DocDataDisplayFixtures.Document(vm, "Bond arrays"),
        });
    }

    /// <summary>Export the four-array design, terminal basis, lumped, 0.1–20 GHz.</summary>
    private static string ExportTouchstone()
    {
        var dir = Path.Combine(DocRunData.ResultsRoot);
        Directory.CreateDirectory(dir);
        string basePath = Path.Combine(dir, "wbond-arrays");

        var result = WBondTouchstoneExport.Export(
            FourArrayDesign(),
            new WBondTouchstoneExport.Options(
                Z0Ohms: 50.0, StartHz: 1e8, StopHz: 2e10, Points: 201),
            basePath);

        string written = result.WrittenPaths.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "WBondTouchstoneExport.Export wrote no file for the documentation's own design.");
        return written;
    }
}
