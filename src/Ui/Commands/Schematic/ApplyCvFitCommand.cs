using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Atomically replaces NonlinearC coefficient params (C0…Cn) and the hidden CvData
/// param in one undoable step. One Apply = one undo step, keeping coefficient count
/// and the serialized table atomically consistent.
/// </summary>
internal sealed class ApplyCvFitCommand : IUiCommand
{
    private readonly SchematicEditModel      _model;
    private readonly EditableComponent       _comp;
    private readonly List<EditableParameter> _oldParams;
    private readonly List<EditableParameter> _newParams;

    public string Description => "Apply C-V fit";

    public ApplyCvFitCommand(
        SchematicEditModel model,
        EditableComponent  comp,
        double[]           coeffs,
        string             serializedCvData)
    {
        _model     = model;
        _comp      = comp;
        _oldParams = comp.Parameters.Select(p => p.Clone()).ToList();

        var other = comp.Parameters
            .Where(p => !IsCoeffParam(p.Name) && p.Name != "CvData")
            .Select(p => p.Clone())
            .ToList();

        _newParams = new List<EditableParameter>(other);
        for (int k = 0; k < coeffs.Length; k++)
        {
            // Preserve the user's ShowOnSchematic choice when the param already exists;
            // default C0 visible, higher-order terms hidden for newly added coefficients.
            var existing = comp.Parameters.FirstOrDefault(p => p.Name == $"C{k}");
            _newParams.Add(new EditableParameter
            {
                Name            = $"C{k}",
                Expression      = coeffs[k].ToString("G15", CultureInfo.InvariantCulture),
                Unit            = k == 0 ? "F" : "",
                ShowOnSchematic = existing?.ShowOnSchematic ?? (k == 0),
            });
        }

        // CvData stored as a quoted string-literal expression so the elaborator evaluates
        // it to Value.String(...) and the factory ignores it (reads only C0, C1, …).
        _newParams.Add(new EditableParameter
        {
            Name            = "CvData",
            Expression      = $"\"{serializedCvData}\"",
            Unit            = "",
            ShowOnSchematic = false,
        });
    }

    public void Execute() => Apply(_newParams);
    public void Undo()    => Apply(_oldParams);

    private void Apply(List<EditableParameter> src)
    {
        _comp.Parameters.Clear();
        foreach (var p in src)
            _comp.Parameters.Add(p.Clone());
        _model.NotifyChanged();
    }

    private static bool IsCoeffParam(string name)
        => name.Length >= 2 && name[0] == 'C' && int.TryParse(name[1..], out _);
}
