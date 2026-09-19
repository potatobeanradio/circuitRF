using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// Writing a <c>.csmith</c> to disk — the ONE implementation of it.
/// </summary>
/// <remarks>
/// A static rather than a method on <c>WorkspaceViewModel</c>, on <c>HarmonicaDocumentSave</c>'s own
/// reasoning: the document TAB's Save must reach a document whose view may not be realized (a
/// background tab's content is created on demand), so a route that starts at the view is a route that
/// silently does nothing on the tab the user actually right-clicked. The workspace is optional because
/// <c>R-smith4-1</c>'s scratch document needs none — it is only wanted to put a newly-saved file into
/// the project tree.
///
/// <para><b>The write is <see cref="SmithDesignIo.SaveToFile"/>'s</b>, which validates on the way out:
/// §7 — <i>a document that cannot be read back is a document that was never written</i>. So a refusal
/// here is a refusal BEFORE anything reaches the disk, and the alternative is a file whose only symptom
/// is that it will not open next week.</para>
/// </remarks>
internal static class SmithChartDocumentSave
{
    /// <summary>The picker's own file type, so a Save As does not offer to write a <c>.csmith</c> under
    /// some other extension.</summary>
    internal static FilePickerFileType FileType { get; } =
        new("circuitRF Smith Chart") { Patterns = ["*" + SmithDesignIo.Extension] };

    /// <summary>
    /// Saves <paramref name="doc"/>, asking for a path when it has none (or always, for
    /// <paramref name="saveAs"/>).
    /// </summary>
    /// <returns>
    /// Null when the document was written or the user cancelled the picker, and the failure's own
    /// sentence when the write itself was refused — reported by the caller, because a refusal about an
    /// ill-formed design belongs in the Messages panel and not in a dialog nobody asked for.
    /// </returns>
    internal static async Task<string?> RunAsync(
        SmithChartDocument doc, TopLevel top, WorkspaceViewModel? workspace, bool saveAs)
    {
        string? path = saveAs ? null : doc.FilePath;
        if (path is null)
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title               = saveAs ? "Save Smith Chart As" : "Save Smith Chart",
                DefaultExtension    = SmithDesignIo.Extension.TrimStart('.'),
                ShowOverwritePrompt = true,
                SuggestedFileName   = doc.FilePath is { } p
                                          ? Path.GetFileName(p)
                                          : doc.Title?.TrimStart('•', ' ') + SmithDesignIo.Extension,
                FileTypeChoices     = [FileType],
            });
            if (file is null) return null;      // cancelled — not a failure
            path = file.Path.LocalPath;
        }

        return Write(doc, path, workspace);
    }

    /// <summary>
    /// The write itself, with no dialog anywhere near it — what <see cref="RunAsync"/> does once it has
    /// a path, and what the gate drives directly with no display.
    /// </summary>
    internal static string? Write(SmithChartDocument doc, string path, WorkspaceViewModel? workspace)
    {
        try
        {
            SmithDesignIo.SaveToFile(path, doc.ViewModel.Design);
            doc.OnSavedToPath(path);

            // A .csmith saved into an open workspace appears in the tree with no reload. Null
            // standalone, where there is no tree to refresh and nothing to register.
            workspace?.NotifySmithSaved(doc, path);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
