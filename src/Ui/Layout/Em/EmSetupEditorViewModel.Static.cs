// brief-em3d-22 R-em3d22-5b — the panel's Problem picker and, for a static problem, its terminal table.
//
// NOTHING THAT AFFECTS THE ANSWER LIVES ONLY HERE (R-em-11): the picker writes Problem3D, the table
// writes Terminals3D and the ground box Ground3D, each through the same undoable CommitEdit every other
// control uses. The table is staged text: a row is written when its box loses focus, and a row whose
// name and net are both blank is not written at all.

using System.Collections.ObjectModel;
using CircuitRF.Engine.Em3d;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>One row of the Problem picker.</summary>
public sealed record Em3dProblemChoice(Em3dProblemType Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the terminal table, as typed.</summary>
public sealed partial class Em3dTerminalRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _net = "";
    [ObservableProperty] private string _source = "";
}

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>What a 3D setup solves.</summary>
    public static IReadOnlyList<Em3dProblemChoice> Problem3DChoices { get; } =
    [
        new(Em3dProblemType.Driven,        "Driven (S-parameters)"),
        new(Em3dProblemType.Electrostatic, "Electrostatic (C matrix)"),
        new(Em3dProblemType.Magnetostatic, "Magnetostatic (L matrix)"),
        new(Em3dProblemType.Eigenmode,     "Eigenmode (resonances and Q)"),
    ];

    [ObservableProperty] private Em3dProblemChoice _problem3DChoice = Problem3DChoices[0];

    /// <summary>The terminal table's rows, in matrix order.</summary>
    public ObservableCollection<Em3dTerminalRow> TerminalRows { get; } = [];

    [ObservableProperty] private string _ground3DText = "";

    /// <summary>True when the terminal table is shown: a static problem on a 3D setup.</summary>
    public bool IsStaticSetup => Is3DSetup && Problem3DChoice.Value is Em3dProblemType.Electrostatic or Em3dProblemType.Magnetostatic;

    /// <summary>True when the table's Source column is read (magnetostatic only).</summary>
    public bool IsMagnetostaticSetup => Is3DSetup && Problem3DChoice.Value == Em3dProblemType.Magnetostatic;

    public string Problem3DDescription => Problem3DChoice.Value switch
    {
        Em3dProblemType.Electrostatic =>
            "The capacitance matrix between the terminals below, referred to the ground. Palace only; the sweep " +
            "and the port impedances are kept but not used. Every conductor must be in a terminal or be the ground.",
        Em3dProblemType.Magnetostatic =>
            "The inductance matrix between the terminals below, each driven through the port named as its source. " +
            "Palace only; conductors are surfaces, so this is the external (RF) inductance, without the internal term.",
        Em3dProblemType.Eigenmode =>
            "The structure's resonant frequencies and Q: the number of modes asked for, above the target. Palace only; " +
            "a lumped port is its resistance, a load, so Q is loaded (the table also takes the ports' share out).",
        _ => "S-parameters over the sweep, with a port at each port label — lumped unless the table below makes it a wave port.",
    };

    partial void OnProblem3DChoiceChanged(Em3dProblemChoice value)
    {
        RaiseStaticVisibility();
        if (_suppressCommit) return;
        if (value.Value == Working.Problem3D) return;
        var before = SnapshotJson();
        Working.Problem3D = value.Value;
        CommitEdit(before, "Change 3D problem");
        Refresh();
    }

    private void RaiseStaticVisibility()
    {
        OnPropertyChanged(nameof(IsStaticSetup));
        OnPropertyChanged(nameof(IsMagnetostaticSetup));
        OnPropertyChanged(nameof(Problem3DDescription));
        OnPropertyChanged(nameof(RadiationPatternDisabledReason));
        OnPropertyChanged(nameof(ReferenceInputPowerEnabled));
        RaiseEigenVisibility();
    }

    private void SyncStaticFields()
    {
        Problem3DChoice = Problem3DChoices.First(c => c.Value == Working.Problem3D);
        TerminalRows.Clear();
        foreach (var t in Working.Terminals3D)
            TerminalRows.Add(new Em3dTerminalRow { Name = t.Name, Net = t.Net, Source = t.Source ?? "" });
        Ground3DText = Working.Ground3D;
        SyncEigenFields();
        RaiseStaticVisibility();
    }

    /// <summary>Writes the table and the ground box as they now read: one undoable edit, or none when
    /// nothing changed.</summary>
    public void CommitTerminals()
    {
        var before = SnapshotJson();
        Working.Terminals3D = [.. TerminalRows
            .Where(r => r.Name.Trim().Length > 0 || r.Net.Trim().Length > 0)
            .Select(r => new EmTerminal3D(r.Name.Trim(), r.Net.Trim(), r.Source.Trim() is { Length: > 0 } s ? s : null))];
        Working.Ground3D = Ground3DText.Trim();
        if (SnapshotJson() == before) return;
        CommitEdit(before, "Change 3D terminals");
    }

    [RelayCommand]
    private void AddTerminal()
    {
        TerminalRows.Add(new Em3dTerminalRow { Name = $"T{TerminalRows.Count + 1}" });
        CommitTerminals();
    }

    [RelayCommand]
    private void RemoveTerminal(Em3dTerminalRow? row)
    {
        if (row is null || !TerminalRows.Remove(row)) return;
        CommitTerminals();
    }
}
