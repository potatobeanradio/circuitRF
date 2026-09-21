using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.RailRf;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The railRF window, on the shipped <c>Power Rail</c> example.
/// </summary>
/// <remarks>
/// <b>The figures are of the example a reader can open</b>, not of a fixture built here. Every
/// number in them comes out of <see cref="RailDcRun"/> and <see cref="PdnSweep"/> on
/// <c>examples/Power Rail/</c>'s own <c>.crail</c>, <c>.clay</c>, <c>.ctech</c> and <c>.crlib</c> —
/// so a chapter that quotes a drop or a margin is quoting the same run the reader gets, and a
/// change that moves either one moves the picture too.
///
/// <para><b>The window is detached and its <c>Styles</c> carried across</b>, which is
/// <see cref="DocMatchFixtures"/>' own technique for its own reason: every size and weight in this
/// window is a style CLASS declared in <c>&lt;Window.Styles&gt;</c>, and a detached content control
/// is no longer a descendant of the window, so without the carry-across every classed control
/// silently falls back to the inherited default. Unlike the Match Designer, railRF does its wiring
/// in the CONSTRUCTOR and on <c>DataContextChanged</c> rather than on <c>OnLoaded</c>, so the tab
/// strips and the board overlay are bound before the window is taken apart.</para>
/// </remarks>
public static class DocRailFixtures
{
    private const string ExampleFolder = "Power Rail";
    private const string CellFolder = "Sensor board";

    /// <summary>The whole window, run, showing the DC answer and the drop map.</summary>
    public static FigureScene Window() => Scene(RailBoardOverlay.Drop, RailResultsTab.Dc);

    /// <summary>The board on the classification tab — which copper the fast model read as a trace,
    /// and which it meshed.</summary>
    public static FigureScene Classification() => Scene(RailBoardOverlay.Class, RailResultsTab.Dc);

    /// <summary>
    /// The frequency answers: the verdict against the target, the anti-resonances and the removal
    /// ranking.
    /// </summary>
    /// <remarks>
    /// <b>Selected, which is what a reader does</b> (R-rail18-6a). The strip used to set no card's
    /// visibility, so this figure had to scroll the one list past four DC cards to reach the mask
    /// verdict; the tab now partitions the column and selecting <c>frequency</c> IS the picture.
    /// </remarks>
    public static FigureScene Impedance() =>
        Scene(RailBoardOverlay.Drop, RailResultsTab.Frequency);

    // ── the scene ─────────────────────────────────────────────────────────────

    private static FigureScene Scene(RailBoardOverlay overlay, RailResultsTab tab)
    {
        var vm = Solved();
        vm.SelectedBoardOverlay = overlay;
        vm.SelectedResultsTab = tab;

        var window = new RailRfWindow { DataContext = vm };
        var content = window.Content as Control
            ?? throw new InvalidOperationException("RailRfWindow has no content control to capture.");

        if (window.Styles.Count == 0)
            throw new InvalidOperationException(
                "RailRfWindow declares no Styles. Every size and weight in these figures comes from "
              + "that block, so a capture without it is silently wrong rather than empty.");

        var styles = window.Styles.ToList();
        var resources = window.Resources;

        window.Content = null;
        window.Styles.Clear();

        foreach (var style in styles) content.Styles.Add(style);
        foreach (var kv in resources) content.Resources[kv.Key] = kv.Value;

        content.DataContext = vm;

        return new FigureScene(content)
        {
            AfterLayout = root =>
            {
                // The map theme and the plot theme are applied by the WINDOW's own handlers, which
                // are on a window that is no longer in the picture — so they are applied here,
                // after the generator has chosen the variant.
                var theme = ThemeService.CurrentVariant == ColorVariant.Dark
                    ? RenderTheme.Dark : RenderTheme.Light;
                vm.PlotHost.Theme = theme;
                foreach (var plot in root.GetVisualDescendants().OfType<PlotControl>())
                {
                    plot.PlotTheme = theme;
                    plot.InvalidateVisual();
                }

                foreach (var canvas in root.GetVisualDescendants().OfType<Controls.LayoutCanvas>())
                {
                    canvas.ZoomToFit();
                    canvas.InvalidateVisual();
                }

                UiArtworkGenerator.Pump();
            },
            Cleanup = vm.Dispose,
        };
    }

    /// <summary>
    /// A view model over the shipped example, run to completion synchronously.
    /// </summary>
    /// <remarks>
    /// <b>Inline post and inline off-thread run</b>, which is what <c>RailWindowTests</c> does and
    /// for the same reason: the Fast loop then completes before <c>RunCommand.Execute</c> returns,
    /// so the figure is of a window with a result in it rather than of one still solving.
    /// </remarks>
    private static RailRfViewModel Solved()
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException(
                "No examples/ tree beside the generator or above it, so the railRF figures have no "
              + "board. They are OF the shipped example on purpose — see this type's own remarks.");

        string cell = Path.Combine(root, ExampleFolder, CellFolder);
        string crail = Path.Combine(cell, CellFolder + ".crail");
        string clay = Path.Combine(cell, "layout", "Board.clay");
        string ctech = Path.Combine(root, ExampleFolder, "tech", "pcb-4layer-1p6mm.ctech");
        string crlib = Path.Combine(root, ExampleFolder, "parts", "decoupling.crlib");

        var document = RailDocumentIo.LoadFromFile(crail);
        var view = LayoutPersistence.LoadFromFile(clay);
        var technology = TechPersistence.LoadFromFile(ctech);

        // THE COMPANIONS, and without them the rail resolves to no copper at all: every anchor on
        // this document is a REFDES, and a refdes is a coordinate only once the board netlist has
        // said where its pads are. The window's own open reads them (RailRfViewModel.Open); an
        // ApplyImport bypasses that path, so they are read here through the same walk.
        var netlist = RailArtwork.ResolveBoardNetlist(
            document, crail, view.DbuPerMicron, out _, out _);
        var placement = RailArtwork.ResolvePlacement(
            document, crail, view.DbuPerMicron, out _, out _);

        var vm = new RailRfViewModel(document, crail)
        {
            PostToUi = a => a(),
            RunOffThread = (work, _) => System.Threading.Tasks.Task.FromResult(work()),
        };

        // R-ab1-5b: the one funnel, so the shipped figures are drawn off exactly the pads the window
        // and the verb resolve.
        var resolvedPads = RailArtwork.PadsFor(view, clay, technology, netlist);

        vm.ApplyImport(
            new RailImportOptions(),
            new RailBoardInputs
            {
                // Flattened, exactly as the window's own open does it: the parts on this board are
                // instances of footprint cells and their lands are inside them.
                Shapes = RailArtwork.FlattenedShapes(view, clay, technology),
                View = view,
                Technology = technology,
                DbuPerMicron = view.DbuPerMicron,
                ArtworkCellRef = clay,
                Pads = resolvedPads.Pads,
                NetPoints = resolvedPads.NetPoints,
                ReferenceNet = document.ReferenceNet,
            },
            placement: placement,
            netlist: netlist,
            library: PartLibraryIo.LoadFromFile(crlib));

        vm.ConfirmReference();
        vm.RunCommand.Execute(null);
        return vm;
    }
}
