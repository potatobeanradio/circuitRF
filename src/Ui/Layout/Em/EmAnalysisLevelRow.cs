// L9d/D5 — one selectable conductor level in the planar analysis.
//
// Framework-free like everything else under src/Ui/Layout/Em (R-em-1): it raises a plain event rather
// than reaching for a command, so the whole level-selection behaviour is testable without a document,
// a canvas or a workspace.

using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

public sealed partial class EmAnalysisLevelRow : ObservableObject
{
    public EmAnalysisLevelRow(string name) => Name = name;

    /// <summary>The stackup conductor entry's own name — the identity a <c>.cem</c> stores.</summary>
    public string Name { get; }

    [ObservableProperty] private bool _isIncluded;

    /// <summary>Raised after <see cref="IsIncluded"/> changes, so the editor can commit one undoable
    /// snapshot per toggle rather than one per keystroke-equivalent.</summary>
    public event Action<EmAnalysisLevelRow>? Toggled;

    partial void OnIsIncludedChanged(bool value) => Toggled?.Invoke(this);
}
