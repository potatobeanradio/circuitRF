// Export to Library… from Model Composer (brief vendorlib-17 R-vl17-2, D6): the toolbar button's state and its command.
// The cell, every refusal and the export are src/Design's (ModelCellFromModel, ModelLibraryExport); the dialog and the
// Messages lines are the workspace's, reached through the seam the window fills. Nothing about exporting is here.

using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    /// <summary>Publishes the saved document — its path and, for an active model, the <c>.cmodel</c> the Export step
    /// writes — through the workspace's Export to Library dialog. Null when the window has no workspace.</summary>
    public Func<string, string?, Task>? PublishToLibrary { get; set; }

    /// <summary>Why the button is greyed, or null when it is live (D6). The sentence is its tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportToLibraryTip))]
    [NotifyCanExecuteChangedFor(nameof(ExportToLibraryCommand))]
    private string? _exportToLibraryBlocked = "Save this document first; Export to Library publishes what is saved.";

    public string ExportToLibraryTip => ExportToLibraryBlocked is { } why
        ? $"Export to Library… — {why}"
        : "Export to Library… — publish this model as a part in a model library, for others to place";

    /// <summary>The <c>.cmodel</c> the button publishes: the Export step's path, or its default beside the document.</summary>
    private string? PublishedCmodelPath => !IsActive ? null
        : ExportPath.Trim().Length > 0 ? Path.GetFullPath(ExportPath.Trim())
        : DocumentPath is { } p ? ModelCellFromModel.DefaultCmodelPath(p) : null;

    /// <summary>D6, re-read from what is saved: an unsaved or dirty document; a model not exported, or exported before the
    /// document changed; a passive document with no Export step settings or no exported cell.</summary>
    public void RefreshExportToLibrary()
    {
        ExportToLibraryBlocked = PublishToLibrary is null ? "Export to Library needs an open workspace."
            : IsChoosingKind || DocumentPath is null ? "Save this document first; Export to Library publishes what is saved."
            : IsDirty ? "Save this document first; Export to Library publishes what is saved."
            : Blocked(DocumentPath, PublishedCmodelPath);

        static string? Blocked(string path, string? cmodel)
        {
            try { return ModelCellFromModel.BlockedReason(CmcFile.Read(path), path, cmodel); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CmcFormatException)
            { return $"{Path.GetFileName(path)} could not be read ({ex.Message})."; }
        }
    }

    partial void OnIsDirtyChanged(bool value) => RefreshExportToLibrary();
    partial void OnDocumentPathChanged(string? value) => RefreshExportToLibrary();

    private bool CanExportToLibrary() => ExportToLibraryBlocked is null;

    [RelayCommand(CanExecute = nameof(CanExportToLibrary))]
    private async Task ExportToLibrary()
    {
        RefreshExportToLibrary();
        if (ExportToLibraryBlocked is not null || PublishToLibrary is null || DocumentPath is null) return;
        await PublishToLibrary(DocumentPath, PublishedCmodelPath);
    }
}
