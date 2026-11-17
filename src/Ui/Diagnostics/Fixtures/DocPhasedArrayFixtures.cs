using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Results;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Ui.Views.PhasedArray;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Phased Antenna Array page's figures (<c>brief-paa-10-docs-and-example.md</c> R-paa10-1): the Phased Array tool window and the
/// Data Display of the shipped <i>Phased Array</i> example.
/// </summary>
/// <remarks>
/// <b>Every figure is of the example a reader can open</b> — <c>examples/Phased Array/</c>, read from disk — which is
/// <see cref="DocSmithFixtures"/>' rule for its reason: a page that quotes a sidelobe level is quoting the evaluation the reader gets.
///
/// <para><b>The tool runs with <see cref="ImmediatePhasedArrayHost"/></b>, so every frame, the scan map and the frequency study are done
/// before the capture, and its frame time is left out of the status line while <see cref="UiArtworkGenerator.HeadlessCapture"/> is set
/// (a figure never draws a number that measures the machine). <b>The Data Display figures simulate the bench</b> the way Simulate does —
/// every enabled analysis, the measures, the grouped result written by <see cref="ResultsWriter"/> as <c>&lt;cell&gt;.npy</c> — into a
/// scratch folder beside a copy of the example's own <c>.cdd</c>, and open that copy, so a display edit in the example moves the figure.</para>
///
/// <para><b>The window is detached and its <c>Styles</c> carried across</b>, as <see cref="DocRailFixtures"/> does: the tool's button and
/// label sizes are style classes in <c>&lt;Window.Styles&gt;</c>, which a detached content control would otherwise lose.</para>
/// </remarks>
public static class DocPhasedArrayFixtures
{
    private const string ExampleFolder = "Phased Array";
    private const string Transmit = "1 Transmit";

    /// <summary>The Beam page on the Transmit bench: the aperture by amplitude (the Taylor taper), the beam at 30° in (u,v), the cuts.</summary>
    public static FigureScene ToolBeam() => Tool(Transmit, ApertureQuantity.Amplitude, ArrayPage.Beam);

    /// <summary>The same, the aperture coloured by each element's active reflection.</summary>
    public static FigureScene ToolGamma() => Tool(Transmit, ApertureQuantity.GammaActive, ArrayPage.Beam);

    /// <summary>The Scan maps page: realized gain and worst |Γ_act| over every scan direction.</summary>
    public static FigureScene ToolScan() => Tool(Transmit, ApertureQuantity.Amplitude, ArrayPage.ScanMaps);

    /// <summary>The Tapers and bits page.</summary>
    public static FigureScene ToolTapers() => Tool(Transmit, ApertureQuantity.Amplitude, ArrayPage.TapersAndBits);

    /// <summary>The Frequency page: where the beam points against frequency, phase shifters against delays.</summary>
    public static FigureScene ToolFrequency() => Tool(Transmit, ApertureQuantity.Amplitude, ArrayPage.Frequency);

    /// <summary>The Transmit bench's display after Simulate: cuts against <c>theta0</c>, realized gain, |Γ_act| per element.</summary>
    public static FigureScene TransmitDisplay() => Display(Transmit, Transmit);

    /// <summary>The brute-force bench's display after Simulate, the loadpull bench run first for its contours.</summary>
    public static FigureScene BruteForceDisplay() => Display("5 Brute Force", "4 PA Loadpull", "5 Brute Force");

    // ── the tool ─────────────────────────────────────────────────────────────

    private static FigureScene Tool(string cell, ApertureQuantity quantity, ArrayPage page)
    {
        string path = SchematicOf(cell);
        var (model, _, _) = SchematicPersistence.LoadFromFile(path);
        var schematic = new SchematicViewModel(model);
        var instance = model.Components.FirstOrDefault(c => c.Symbol == SymbolKind.PhasedArray)
            ?? throw new InvalidOperationException($"'{cell}' holds no Phased Antenna Array, so the tool figures would have nothing to open on.");
        var vm = new PhasedArrayToolViewModel(schematic, instance, _ => PreparedCircuit.FromSchematic(path), new ImmediatePhasedArrayHost());
        if (vm.Frame is null)
            throw new InvalidOperationException($"The Phased Array tool did not evaluate '{cell}': {vm.Status}");
        vm.Quantity = quantity;
        if (page == ArrayPage.ScanMaps) vm.StartScan();
        if (page == ArrayPage.Frequency) vm.RunFrequency();
        vm.Page = page;

        var window = new PhasedArrayWindow { DataContext = vm };
        var content = window.Content as Control
            ?? throw new InvalidOperationException("PhasedArrayWindow has no content control to capture.");
        var styles = window.Styles.ToList();
        var resources = window.Resources;
        window.Content = null;
        window.Styles.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        foreach (var kv in resources) content.Resources[kv.Key] = kv.Value;
        content.DataContext = vm;

        return new FigureScene(content)
        {
            AfterLayout = _ => UiArtworkGenerator.Pump(),
            Cleanup = vm.Dispose,
        };
    }

    // ── the Data Display ─────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="display"/>'s <c>.cdd</c>, opened from a scratch folder holding a copy of it and the results of
    /// <paramref name="run"/>, each simulated as Simulate does and written as <c>&lt;cell&gt;.npy</c>.
    /// </summary>
    private static FigureScene Display(string display, params string[] run)
    {
        string dir = Path.Combine(DocRunData.ResultsRoot, "phased-array");
        Directory.CreateDirectory(dir);
        foreach (string cell in run) Simulate(cell, dir);
        string cdd = Path.Combine(dir, display + ".cdd");
        File.Copy(Path.Combine(ExampleDirectory(), display + ".cdd"), cdd, overwrite: true);

        var vm = new DataDisplayDocumentViewModel();
        // the results land in <dir>/results, as a workspace's do, and the display finds them there as it does in a workspace
        vm.Window.DataSourceLibrary.ResultsRootProvider = () => ResultsWriter.ResultsDirectory(dir);
        DocDataDisplayFixtures.Await(vm.Window.LoadAllAsync(cdd));
        return new FigureScene(new DataDisplayView { DataContext = DocDataDisplayFixtures.Document(vm, display) });
    }

    private static void Simulate(string cell, string dir)
    {
        string path = SchematicOf(cell);
        var circuit = PreparedCircuit.FromSchematic(path);
        if (circuit.ReadError is { } error) throw new InvalidOperationException($"'{cell}' did not extract: {error}");
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest());
        var result = CircuitEvaluation.Execute(plan);
        if (result.GroupedResults is not { } grouped || result.Errors.Count > 0)
            throw new InvalidOperationException($"'{cell}' did not simulate for its figure: {result.StatusMessage}");
        var written = ResultsWriter.WriteRun(dir, cell, grouped, null);
        if (written.Error is { } writeError) throw new InvalidOperationException($"'{cell}': {writeError}");
    }

    private static string SchematicOf(string cell) => Path.Combine(ExampleDirectory(), cell, "schematic", cell + ".csch");

    private static string ExampleDirectory()
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException(
                "No examples/ tree beside the generator or above it, so the Phased Array figures have no bench. They are OF the shipped " +
                "example on purpose — see this type's remarks.");
        return Path.Combine(root, ExampleFolder);
    }
}
