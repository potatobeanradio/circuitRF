// The setup panel's Notes and Component ports sections open and close, and the document keeps which: a long list of notes or
// of parts no longer pushes the rest of the panel down every time it opens. Notes opens by default; Component ports opens by
// default only when the setup names a component port. A toggle is an edit of the setup like any other, so it is saved with the
// .cem or the .c3d and undone by Undo; no run reads it (EmSetup.NotesExpanded).

using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Ui.Layout.Em;

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>Whether the Notes section is open.</summary>
    [ObservableProperty] private bool _notesExpanded = true;

    /// <summary>Whether the Component ports section is open.</summary>
    [ObservableProperty] private bool _componentPortsExpanded;

    partial void OnNotesExpandedChanged(bool value)
    {
        if (_suppressCommit || IsReadOnly || value == (Working.NotesExpanded ?? true)) return;
        var before = SnapshotJson();
        Working.NotesExpanded = value;
        CommitEdit(before, value ? "Expand Notes" : "Collapse Notes");
    }

    partial void OnComponentPortsExpandedChanged(bool value)
    {
        if (_suppressCommit || IsReadOnly || value == ComponentPortsOpenByDefault(Working)) return;
        var before = SnapshotJson();
        Working.ComponentPortsExpanded = value;
        CommitEdit(before, value ? "Expand Component ports" : "Collapse Component ports");
    }

    /// <summary>The Component ports section's state when the setup states none: open when it names a component port.</summary>
    private static bool ComponentPortsOpenByDefault(EmSetup setup) => setup.ComponentPortsExpanded ?? setup.ComponentPorts.Count > 0;

    /// <summary>Both sections as the setup states them, without committing (called with commits suppressed).</summary>
    private void ShowSections()
    {
        NotesExpanded = Working.NotesExpanded ?? true;
        ComponentPortsExpanded = ComponentPortsOpenByDefault(Working);
    }
}
