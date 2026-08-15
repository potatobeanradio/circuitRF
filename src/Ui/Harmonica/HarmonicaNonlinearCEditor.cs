using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// R7D §4 — reuses the schematic layer's Parameter Editor to edit a DUT capacitance's raw polynomial
/// coefficients (<c>C0, C1, …</c>), for a document (harmonicaRF) that has no schematic and no
/// <see cref="EditableComponent"/> of its own.
///
/// <para><b>Why the Parameter Editor and not the C-V curve-fit dialog.</b> The owner's own words: "Reuse
/// the Parameter Editor for the NonlinearC component." <c>NonlinearCvEditorViewModel</c> works from
/// sample (V, C) points and a least-squares fit — the wrong shape for a seed that is already raw
/// coefficients. <c>ParameterEditorView</c> shows <c>C0, C1, …</c> as ordinary editable rows AND
/// already offers "Add Group"/"Remove Top Group" for a <see cref="SymbolKind.NonlinearC"/> target
/// (<c>ComponentTypeRegistry.UserParamTemplate</c>'s own <c>C{0}</c> template, <c>FirstAddIndex: 1</c>)
/// — so raw coefficient add/remove/edit is already the generic mechanism this dialog gives any
/// NonlinearC component, with nothing new to build for it.</para>
///
/// <para><b>Host a detached one.</b> A throwaway <see cref="SchematicViewModel"/> with one
/// <see cref="EditableComponent"/> — <c>tests/Ui.Tests</c> already constructs
/// <see cref="SchematicViewModel"/> headlessly (e.g. <c>new SchematicViewModel(new
/// SchematicEditModel())</c>), confirming this is a supported construction rather than a hack against
/// the grain.</para>
///
/// <para><b>OK vs Cancel.</b> The Parameter Editor commits every edit LIVE, onto the throwaway
/// schematic's own undo stack — there is no separate Apply/Cancel step anywhere in that surface (every
/// other consumer of it, the schematic's own double-click dialog included, works the same way). So
/// this hosts it MODALLY and, once the window closes, reads back whatever <c>C0, C1, …</c> the
/// component carries at that point — there is no real "cancelled" state to distinguish here, only
/// "closed with today's C0…Cn on it". A caller that wants a true back-out (e.g. "Use Linear" reverting
/// a mistaken open) keeps its own snapshot of the capacitor from BEFORE calling this and simply does
/// not write the result back.</para>
/// </summary>
public static class HarmonicaNonlinearCEditor
{
    /// <summary>
    /// Shows the editor seeded with <paramref name="seedCoefficients"/> (C0…Cn, raw SI — F, F/V, …,
    /// the same spelling <see cref="CircuitRF.Harmonica.DutCapacitance.Coefficients"/> carries) and
    /// returns whatever coefficients the component carries when the (modal) dialog closes, in index
    /// order. Null when nothing at all could be read back (the throwaway component ended up with no
    /// C0 — degenerate, should not happen in practice since a row is always seeded with at least one).
    /// </summary>
    public static async Task<IReadOnlyList<double>?> EditAsync(
        Window owner, string instanceName, IReadOnlyList<double> seedCoefficients)
    {
        var editModel   = new SchematicEditModel();
        var schematicVm = new SchematicViewModel(editModel);

        var comp = new EditableComponent
        {
            InstanceName = instanceName,
            Symbol       = SymbolKind.NonlinearC,
        };
        for (int k = 0; k < seedCoefficients.Count; k++)
            comp.Parameters.Add(new EditableParameter
            {
                Name            = $"C{k}",
                Expression      = seedCoefficients[k].ToString("G17", CultureInfo.InvariantCulture),
                Unit            = k == 0 ? "F" : "",
                ShowOnSchematic = k == 0,
                Dimension       = k == 0 ? UnitDimension.Capacitance : UnitDimension.None,
            });
        if (comp.Parameters.Count == 0)
            comp.Parameters.Add(new EditableParameter
            {
                Name = "C0", Expression = "0", Unit = "F", ShowOnSchematic = true,
                Dimension = UnitDimension.Capacitance,
            });

        schematicVm.Execute(new PlaceComponentCommand(editModel, comp));

        var editorVm = new ParameterEditorViewModel();
        editorVm.SetTargetDirect(schematicVm, comp, showClose: true);

        var dialog = new ParameterEditorDialog
        {
            DataContext = editorVm,
            Title       = $"Edit {instanceName} — C(V)",
        };
        try
        {
            await dialog.ShowDialog(owner);
        }
        finally
        {
            editorVm.Dispose();
        }

        var coeffs = new List<double>();
        for (int k = 0; ; k++)
        {
            var p = comp.Parameters.FirstOrDefault(x => x.Name == $"C{k}");
            if (p is null || !TryReadFarads(p, out double v)) break;
            coeffs.Add(v);
        }
        return coeffs.Count > 0 ? (IReadOnlyList<double>)coeffs : null;
    }

    /// <summary>Reads one <c>Ck</c> row back to raw SI, the same <c>raw * Units.Scale(unit)</c> rule
    /// <c>ParameterEditorViewModel.ReadMklopfSiValue</c> already uses for the identical problem — the
    /// unit and the number are edited together by the row's own control, so whichever unit the user
    /// left it in still resolves to the true value.</summary>
    private static bool TryReadFarads(EditableParameter p, out double value)
    {
        value = 0.0;
        if (!double.TryParse(p.Expression, System.Globalization.NumberStyles.Float,
                             CultureInfo.InvariantCulture, out double raw))
            return false;
        double scale = Units.Scale(UnitNormalizer.ToEngineUnit(p.Unit)) ?? 1.0;
        value = raw * scale;
        return true;
    }
}
