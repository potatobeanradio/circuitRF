using System.Linq;
using Avalonia.Controls;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Analyses;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.Views.Palette;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The two surfaces the schematic editor is driven FROM rather than the canvas itself: the Library
/// Palette a component is dragged out of, and the analyses a schematic is simulated by.
///
/// <para>Both follow <see cref="DocFixtures"/>'s rule — a real, shipped document, read through the
/// application's own loader. The analyses figures are built on the shipped
/// <c>FET_Harmonic_Balance_Sweep</c> test bench with a DC operating point added ahead of its HB,
/// which is the ordinary two-analysis test bench a reader is about to build.</para>
/// </summary>
public static class DocSchematicFixtures
{
    /// <summary>The shipped template the analyses figures are configured from.</summary>
    public const string AnalysesTemplateId = "FET_Harmonic_Balance_Sweep";

    // ── Library Palette ───────────────────────────────────────────────────────

    /// <summary>
    /// The Library Palette, showing the <b>All</b> category — every built-in component, in the
    /// palette's own pinned order.
    /// </summary>
    /// <remarks>
    /// Captured at the width the default dock layout gives it (the left column is 20 % of the
    /// window, so ~280 px on an ordinary display), because the tile grid is a WrapPanel whose
    /// column count is a function of that width: a figure captured wider would show a number of
    /// columns the reader's own palette never has.
    /// </remarks>
    public static FigureScene LibraryPalette()
        => new(new PaletteToolView { DataContext = new PaletteTool() });

    // ── Analyses ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The Setup Analyses window — the real dialog's own content, carrying a DC operating point and
    /// a harmonic-balance drive sweep.
    /// </summary>
    public static FigureScene SetupAnalyses()
    {
        var vm = AnalysesVm(out _);

        // The dialog itself, so the figure is the window a reader opens rather than a re-creation
        // of it: its content is lifted out (a Window cannot be hosted inside another Window) and
        // handed back its DataContext, which detaching would otherwise take with it.
        var dialog = new SetupAnalysesDialog(vm);
        var body = (Control)dialog.Content!;
        dialog.Content = null;
        body.DataContext = vm;
        return new FigureScene(body);
    }

    /// <summary>
    /// The analysis editor opened on that test bench's harmonic-balance analysis — the dialog every
    /// analysis is configured in, showing a real HB configuration rather than its defaults.
    /// </summary>
    public static FigureScene HbAnalysisEditor()
    {
        var listVm = AnalysesVm(out var schematicVm);
        _ = listVm;

        var hb = schematicVm.EditModel.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var vm = new AnalysisEditorViewModel(schematicVm.EditModel, hb);

        var dialog = new AnalysisEditorDialog(vm, isEdit: true);
        var body = (Control)dialog.Content!;
        dialog.Content = null;
        body.DataContext = vm;
        return new FigureScene(body);
    }

    /// <summary>
    /// The shipped HB test bench with a DC operating point inserted ahead of it, bound to the same
    /// <see cref="AnalysesListViewModel"/> the dock panel and the modal both use.
    /// </summary>
    private static AnalysesListViewModel AnalysesVm(out SchematicViewModel schematicVm)
    {
        var model = ShippedSchematicTemplates.Load(AnalysesTemplateId);
        model.Analyses.Insert(0, new DcAnalysis("DC1"));

        schematicVm = new SchematicViewModel(model);
        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(schematicVm, AnalysesTemplateId);
        return vm;
    }
}
