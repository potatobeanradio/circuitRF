using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// HZN-5 R-hzn5-1 / R-hzn5-4 — saving a design held in a Hertzon project. File ▸ Save rewrites only what was edited
/// (<see cref="HznDesignSource.Save"/>); a save that cannot be traced back to the file is refused and says why; a file that changed
/// on disk is refused and the user chooses Reload, Save As .c3d or Cancel (there is no merge). Save As writes a native .c3d through
/// <see cref="HznConvert.ConvertDocument"/>, the function Convert to .c3d… calls.
/// </summary>
public partial class WorkspaceViewModel
{
    public const string HznNotSaved = "This design was not saved.";

    /// <summary>The refusal's dialog (the tab's title, the text); a test substitutes a recorder.</summary>
    internal Func<string, string, Task>? HznSaveRefusalDialog { get; set; }

    /// <summary>The changed-on-disk dialog (file, design, the unsaved edits); a test substitutes the answer.</summary>
    internal Func<string, string, IReadOnlyList<string>, Task<HznChangedChoice>>? HznChangedChoiceAsk { get; set; }

    private bool SaveHznDesign(C3dEditorDocument doc, HznDesignSource held)
    {
        if (doc.ViewModel.Save() is { } why)
        {
            if (held.LastSaveChangedOnDisk)
            {
                Messages.Error($"'{Path.GetFileName(held.FilePath)}' changed on disk since '{held.Design}' was opened. Nothing was written.", held.FilePath);
                _ = AnswerHznChangedOnDiskAsync(doc, held);
                return false;
            }
            Messages.Error($"{HznNotSaved} {why.Split('\n')[0]}", held.FilePath);
            if (why.Contains('\n')) _ = ShowHznSaveRefusalAsync(held.Title, why);
            return false;
        }
        foreach (string note in held.LastSaveNotes) Messages.Info(note, held.FilePath);
        Messages.Success("Saved", held.FilePath);
        return true;
    }

    private async Task ShowHznSaveRefusalAsync(string title, string text)
    {
        if (HznSaveRefusalDialog is { } recorder) { await recorder(title, text); return; }
        if (ResolveOwner(null) is { } owner) await TextConfirmDialog.AskAsync(owner, title, HznNotSaved, text, null);
    }

    private async Task AnswerHznChangedOnDiskAsync(C3dEditorDocument doc, HznDesignSource held)
    {
        var edits = held.EditSummary(doc.ViewModel.Document);
        var choice = HznChangedChoiceAsk is { } ask
            ? await ask(Path.GetFileName(held.FilePath), held.Design, edits)
            : ResolveOwner(null) is { } owner
                ? await HznChangedOnDiskDialog.AskAsync(owner, Path.GetFileName(held.FilePath), held.Design, edits)
                : HznChangedChoice.Cancel;
        switch (choice)
        {
            case HznChangedChoice.Reload:
                if (doc.ViewModel.Reload() is { } why) Messages.Error($"Could not reload '{held.Design}': {why}", held.FilePath);
                else Messages.Info($"'{held.Design}' was reloaded from '{Path.GetFileName(held.FilePath)}'; the unsaved edits were discarded.", held.FilePath);
                break;
            case HznChangedChoice.SaveAsC3d:
                if (ResolveOwner(null) is { } window) await SaveHznDesignAsC3dAsync(doc, held, window);
                break;
        }
    }

    /// <summary>Save As for a design held in a project: the document as it is now, edits included, written as a native .c3d in a cell.
    /// The tab stays on the project, whose file is not touched.</summary>
    private async Task SaveHznDesignAsC3dAsync(C3dEditorDocument doc, HznDesignSource held, Window owner)
    {
        string start = (CurrentWorkspacePath is null ? null : Path.GetDirectoryName(CurrentWorkspacePath)) ?? Path.GetDirectoryName(held.FilePath)!;
        if (await HznConvertDialog.AskAsync(owner, held.Design, start) is not { } answer) return;
        var copy = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc.ViewModel.Document));
        copy.TechRef = null;      // the converted view resolves the workspace's technology, as a converted file's does
        var result = await Task.Run(() => HznConvert.ConvertDocument(
            new HznConvertRequest(held.FilePath, held.Design, answer.CellDir, answer.AddMaterials, answer.ReplaceView),
            held.Design, copy, held.ProjectTechnology, [], GeometryKernel.Shared));
        ReportHznConversion(result);
    }
}
