using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.Views.Drc;
using CircuitRF.Ui.Views.Layout;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The two surfaces that CHECK something rather than draw it: the layout design-rule check, and the
/// report an imported kit produces.
/// </summary>
public static class DocVerifyFixtures
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    // ── DRC ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A layout with two deliberate rule breaks, checked, with the violations panel beside it.
    ///
    /// <para>The artwork is purpose-drawn rather than borrowed from the layout-editor figure: that
    /// one is a microstrip run on a PCB process whose 6-mil rules it breaks in dozens of places at
    /// once, and a panel listing forty identical hits shows what the check FEELS like without showing
    /// what it DOES. This one is on the MMIC starter process (4 µm minimum width and spacing) and
    /// breaks each of those rules exactly once — a 2 µm neck, and a 2 µm gap.</para>
    ///
    /// <para>The layout and the panel are composed side by side because they must correspond: a
    /// violation list beside a different layout is a picture of nothing. That is also how the
    /// application arranges them — the DRC panel is a dock tool beside the document.</para>
    /// </summary>
    public static FigureScene DrcViolations()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var metal = tech.Layers.FirstOrDefault(l => l.Name == "Metal1")?.Key
            ?? throw new InvalidOperationException(
                "The MMIC starter technology no longer declares a 'Metal1' layer. It declares: "
              + string.Join(", ", tech.Layers.Select(l => l.Name)) + ".");

        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };

        // A healthy 10 µm feed, a 2 µm neck that breaks minimum width, and a second trace 2 µm away
        // from the first that breaks minimum spacing.
        view.Shapes.Add(new RectShape { Layer = metal, X1 = Um(0),  Y1 = Um(0),  X2 = Um(40), Y2 = Um(10) });
        view.Shapes.Add(new RectShape { Layer = metal, X1 = Um(40), Y1 = Um(4),  X2 = Um(70), Y2 = Um(6)  });
        view.Shapes.Add(new RectShape { Layer = metal, X1 = Um(70), Y1 = Um(0),  X2 = Um(110), Y2 = Um(10) });
        view.Shapes.Add(new RectShape { Layer = metal, X1 = Um(0),  Y1 = Um(12), X2 = Um(110), Y2 = Um(22) });

        var vm = new LayoutEditorViewModel(view) { Technology = tech };
        var result = vm.RunDrc();
        if (result.Violations.Count == 0)
            throw new InvalidOperationException(
                "The DRC figure's artwork produced NO violations, so the figure would show an empty "
              + "panel captioned as a list of hits. Either the starter process's Metal1 rules or the "
              + "sample geometry has changed.");

        var tool = new DrcTool();
        tool.SetActiveLayout(vm);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,360"),
        };
        var editor = new LayoutEditorView { DataContext = new LayoutDocument("Metal1 check", vm) };
        var panel  = new DrcToolView { DataContext = tool };
        Grid.SetColumn(editor, 0);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(editor);
        grid.Children.Add(panel);

        return new FigureScene(grid) { AfterLayout = DocFixtures.ZoomLayoutToFit };
    }

    // ── PDK import ────────────────────────────────────────────────────────────

    /// <summary>
    /// The report an import produces, on a kit that came in cleanly.
    ///
    /// <para><b>The kit is invented, and it says so.</b> Real kits are licensed and none is in this
    /// repository, so there is nothing to import for real at documentation time. What IS real is the
    /// report type and the dialog that renders it: the figure is built by handing a
    /// <see cref="PdkImportReport"/> — the very object <c>PdkImporter</c> returns — to the dialog's
    /// own body builder. A fixture that redrew the layout instead would be a picture of a second
    /// implementation.</para>
    /// </summary>
    public static FigureScene PdkImportReport()
    {
        var report = new PdkImportReport
        {
            RootPath = "/kits/AcmeRF-GaAs150/pdk",
            KitName  = "AcmeRF GaAs-150",
            Status   = PdkImportStatus.Imported,
        };

        foreach (var (id, name, category, pins) in Parts())
            report.Parts.Add(new PdkPart(id, name, category, PinCount: pins));

        report.Add(new PdkAsset("layers/acme150.ctech", PdkAssetKind.LayerTechnology,
                                PdkAssetSupport.Supported, "Layer technology",
                                "12 layers, 9 design-rule checks"));
        report.Add(new PdkAsset("symbols/acme150.symlib", PdkAssetKind.SymbolLibrary,
                                PdkAssetSupport.Supported, "Symbol library",
                                "8 symbols, pins and bodies read"));
        report.Add(new PdkAsset("pcells/acme150.py", PdkAssetKind.LayoutArtwork,
                                PdkAssetSupport.Supported, "Python PCell",
                                "8 generators, parameters resolved"));
        report.Add(new PdkAsset("models/acme150.osdi", PdkAssetKind.ModelData,
                                PdkAssetSupport.Supported, "Compiled model library",
                                "runs in the device worker"));
        report.Add(new PdkAsset("cells/acme150.cdl", PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "Subcircuit netlist",
                                "8 cells, model cards resolved"));

        report.Info("Corners: typical, slow, fast — typical is the kit's own default.",
                    "Choose one per test bench in the Analyses panel.");
        report.Info("All 8 parts carry both symbol and layout artwork.");

        return new FigureScene(PdkImportReportDialog.Body(report));
    }


    /// <summary>
    /// <b>Manage PDKs</b> — the surface a workspace's kit references are added, removed, revealed and
    /// validated from, showing one healthy kit.
    ///
    /// <para>A kit reads as healthy only when its folder exists AND the registry holds parts for it,
    /// so the fixture makes both true and then puts both back: a temporary folder, a registry entry,
    /// and a <see cref="FigureScene.Cleanup"/> that removes the kit again. <b>The registry is process
    /// state</b> — leaving the invented kit in it would put eight fictional parts in the Library
    /// Palette figure captured later in the same run, which is the kind of contamination that shows
    /// up as a mystery in an unrelated picture.</para>
    /// </summary>
    public static FigureScene ManagePdks()
    {
        const string provider = "AcmeRF GaAs-150";
        const string kitPath  = "/kits/AcmeRF-GaAs150";

        // The row is supplied rather than discovered, and that is the whole point: the real path
        // reads the FILESYSTEM, so a figure built from a temp directory would carry this machine's
        // /var/folders/... into a committed SVG and change it on every run. The kit is invented
        // anyway — the same one the import report shows — so there is nothing on disk to describe.
        var rows = new List<PdkReferenceManager.RefStatus>
        {
            new(provider, kitPath, kitPath, PdkReferenceManager.RefState.Ok, Parts().Length, ""),
        };

        var context = new ManagePdksDialog.Context(
            WorkspaceRootDir: "/designs/PowerAmp",
            Refs: [new CwsPdkRef { Provider = provider, Path = kitPath }],
            PlacedPartRefs: ["pdk://" + provider + "/phemt_2f50"],
            Save: () => { },
            Reveal: _ => { },
            Loaded: () => { },
            Report: (_, _) => { });

        return new FigureScene(ManagePdksDialog.Body(context, rows));
    }

    private static (string Id, string Name, string Category, int Pins)[] Parts() =>
    [
        ("phemt_2f50",  "PHEMT 2×50 µm",     "Active",  3),
        ("phemt_4f75",  "PHEMT 4×75 µm",     "Active",  3),
        ("mim_cap",     "MIM capacitor",     "Passive", 2),
        ("nires",       "NiCr resistor",     "Passive", 2),
        ("spiral_ind",  "Spiral inductor",   "Passive", 2),
        ("via_gnd",     "Through-substrate via", "Interconnect", 1),
        ("mlin",        "Microstrip line",   "Interconnect", 2),
        ("mtee",        "Microstrip tee",    "Interconnect", 3),
    ];
}
