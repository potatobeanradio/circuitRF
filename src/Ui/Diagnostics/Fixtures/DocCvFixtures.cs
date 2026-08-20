using System;
using System.Globalization;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The C-V Editor, holding a real varactor table.
///
/// <para>The points are entered through the editor's own row collection and the fit is the editor's
/// own, so the preview curve in the figure is the polynomial the Apply button would write — not a
/// drawing of a curve.</para>
/// </summary>
public static class DocCvFixtures
{
    /// <summary>A hyperabrupt-varactor-shaped C(V): 2.4 pF at 0 V falling to 1.0 pF reverse-biased.</summary>
    private static readonly (double V, double C)[] Points =
    [
        (-4.0, 0.62e-12), (-3.0, 0.74e-12), (-2.0, 0.95e-12),
        (-1.0, 1.35e-12), (-0.5, 1.72e-12), (0.0, 2.40e-12),
    ];

    public static FigureScene Editor()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.NonlinearC, InstanceName = "C1" };
        comp.Parameters.Add(new EditableParameter { Name = "C0", Expression = "0", Unit = "F" });
        model.Components.Add(comp);

        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(new SchematicViewModel(model), comp);
        vm.CapacitanceUnit = "None";      // the table below is in SI farads
        vm.Rows.Clear();
        foreach (var (v, c) in Points)
            vm.Rows.Add(new CvRowViewModel(
                v.ToString("G15", CultureInfo.InvariantCulture),
                c.ToString("G15", CultureInfo.InvariantCulture),
                vm));
        vm.FitOrder = 3;
        vm.Validate();   // the rows were replaced wholesale; nothing else re-runs it

        if (vm.HasValidationErrors)
            throw new InvalidOperationException(
                "The C-V editor rejected the documentation's own table: " + vm.ValidationSummary);

        return new FigureScene(new NonlinearCvEditorView { DataContext = vm });
    }
}
