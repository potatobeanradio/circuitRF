using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;
using RfCore.Export;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Linked plots and controls page's figures (brief-ddl-8 R-ddl8-4): the shipped Loadpull example's own
/// <c>Linked Plots.cdd</c>, opened by the window over a fresh run of the example's two benches — so a figure is what
/// the example shows, and cannot drift from it.
///
/// <para><b>The runs land in a results folder of their own.</b> The example names its results <c>Loadpull.npy</c>
/// and <c>LoadpullPursuit.npy</c>, as Simulate would; the docs' shared results folder already holds a
/// <c>Loadpull.npy</c> from another fixture's grid, so sharing it would draw whichever ran last.</para>
/// </summary>
public static partial class DocDataDisplayFixtures
{
    private static readonly Lazy<(string Cdd, string Results)> LinkedExample = new(() =>
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the linked-plots figures have no display to open.");
        string ws = Path.Combine(root, "Loadpull");
        string cdd = Path.Combine(ws, "Linked Plots.cdd");
        if (!File.Exists(cdd))
            throw new InvalidOperationException($"The Loadpull example no longer ships '{cdd}', which the linked-plots figures draw.");
        string results = Path.Combine(Path.GetDirectoryName(DocRunData.ResultsRoot)!, "linked-results");
        Directory.CreateDirectory(results);
        foreach (string cell in new[] { "Loadpull", "LoadpullPursuit" })
        {
            string csch = Path.Combine(ws, cell, "schematic", cell + ".csch");
            var circuit = PreparedCircuit.FromSchematic(csch);
            if (circuit.ReadError is { } error)
                throw new InvalidOperationException($"The linked-plots figures could not read '{csch}': {error}");
            var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Measurements = true });
            var result = CircuitEvaluation.Execute(plan, null, true, [], null);
            if (result.Status != RunStatus.Success || result.GroupedResults is not { } ds)
                throw new InvalidOperationException(
                    $"The linked-plots figures' run of '{csch}' produced no result ({result.StatusMessage}), so its figure would be empty.");
            string path = Path.Combine(results, cell + ".npy");
            if (File.Exists(path)) File.Delete(path);
            DataSetExporter.Export(ds, path, ExportFormat.Npy);
        }
        return (cdd, results);
    });

    /// <summary>The example's display opened on tab <paramref name="tab"/>, its links resolved as on opening.</summary>
    private static DataDisplayDocumentViewModel LinkedDisplay(string tab)
    {
        var (cdd, results) = LinkedExample.Value;
        var vm = new DataDisplayDocumentViewModel();
        vm.Window.DataSourceLibrary.ResultsRootProvider = () => results;
        Await(vm.Window.LoadAllAsync(cdd));
        vm.Window.ActiveTab = vm.Window.Tabs.FirstOrDefault(t => t.Name == tab)
            ?? throw new InvalidOperationException($"'Linked Plots.cdd' has no tab named '{tab}'.");
        vm.Window.DataDisplay?.SelectOnly((PlotContainerViewModel?)null);
        return vm;
    }

    /// <summary>The Load tab: the contours with m1 between the measured loads, the detail plot at m1 and the table.</summary>
    public static FigureScene LinkedLoad()
        => new(new DataDisplayView { DataContext = Document(LinkedDisplay("Load"), "Linked Plots") });

    /// <summary>The Panel tab: its locked controls beside the plots they drive.</summary>
    public static FigureScene LinkedPanel()
        => new(new DataDisplayView { DataContext = Document(LinkedDisplay("Panel"), "Linked Plots") });

    /// <summary>
    /// The Panel tab's drive plot alone: Pout and Gt at m3, interpolated between the measured loads and drawn dashed,
    /// beside the same quantities at m4, which sits on a measured load.
    /// </summary>
    public static FigureScene LinkedInterpolated()
    {
        var vm = LinkedDisplay("Panel");
        var drive = vm.Window.DataDisplay!.Plots.First(c => c.PlotVM.Plot.Traces.Any(t => t.IsSynthesized));
        var view = new DataDisplayView { DataContext = Document(vm, "Linked Plots") };
        var crop = FigureCrop.Around(
            view, 1300, 760,
            c => c.GetVisualDescendants().OfType<PlotContainerView>().Where(p => ReferenceEquals(p.DataContext, drive)),
            pad: 6,
            describeWhatIsMissing:
                "This figure crops to the Panel tab's drive plot, the plot holding an interpolated (synthesised) trace. If "
              + "the example's display no longer has one, or plots stop being PlotContainerViews, this fixture must follow.");
        return new FigureScene(crop.Content) { AfterLayout = _ => crop.Apply() };
    }
}
