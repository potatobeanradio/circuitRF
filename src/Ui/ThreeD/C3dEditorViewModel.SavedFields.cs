// brief-dfr32-3 R-dfr32-3-2 — Delete Saved Fields on a setup's card: the run folder's field directories go to the Trash, the mesh,
// the Touchstone and the .npy stay, the field plots are read again (and say why they are empty), and the status line and Messages
// say how many bytes were freed. The work is PalaceFieldFiles.Delete (src/Design); this is the 3D view's door to it.

using CircuitRF.Design.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dSetupItem
{
    /// <summary>brief-dfr32-3 — the setup's run folder holds saved fields: Delete Saved Fields is offered on the card.</summary>
    [ObservableProperty] private bool _hasSavedFields;
}

public sealed partial class C3dEditorViewModel
{
    /// <summary>How a field directory reaches the Trash: null on success, else why not. A test seam; the app's is the system
    /// Trash (never a hard delete).</summary>
    public Func<string, string?> MoveToTrash { get; set; } = DefaultMoveToTrash;

    internal static string? DefaultMoveToTrash(string path)
        => Schematic.SystemTrash.TryMoveToTrash(path, out string? error) ? null : error ?? "the Trash refused it";

    /// <summary>The Palace run folder of embedded setup <paramref name="setupName"/>, or null (no results folder, no such setup).</summary>
    public string? SavedFieldsRunDirectory(string setupName)
    {
        if (ResultsRootProvider?.Invoke() is not { } root) return null;
        if (C3dSetups.Read(Document).FirstOrDefault(s => s.Name == setupName)?.Setup is not { IsThermal: false } setup) return null;
        return FieldPlotResolver.RunDirectories(C3dSetups.ForRun(setup, RunPath), root).Palace;
    }

    /// <summary>Whether embedded setup <paramref name="setupName"/>'s run folder holds saved fields now.</summary>
    public bool HasSavedFields(string setupName)
        => SavedFieldsRunDirectory(setupName) is { } dir && PalaceFieldFiles.Directories(dir).Count > 0;

    /// <summary>
    /// Delete Saved Fields: moves setup <paramref name="setupName"/>'s field directories to the Trash. The status line and
    /// Messages say how many bytes were freed (or why nothing moved); the field plots are read again.
    /// </summary>
    public PalaceFieldDeletion? DeleteSavedFields(string setupName)
    {
        if (SavedFieldsRunDirectory(setupName) is not { } dir)
        {
            StatusMessage = $"Setup {setupName} has no run folder to delete fields from.";
            return null;
        }
        var result = PalaceFieldFiles.Delete(dir, setupName, MoveToTrash);
        StatusMessage = result.Sentence;
        PostMessage?.Invoke(result.Sentence, result.Failed.Count > 0);
        RunFinished();                             // every plot reads the run again: empty now, and saying why
        RefreshSavedFields();
        return result;
    }

    /// <summary>Each card's <see cref="C3dSetupItem.HasSavedFields"/>, from its run folder as it is now.</summary>
    public void RefreshSavedFields()
    {
        foreach (var item in SetupItems) item.HasSavedFields = !item.IsExternal && item.Refusal is null && HasSavedFields(item.Name);
    }
}
