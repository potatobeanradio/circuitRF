// Save and Save as… (owner, 2026-09-19).
//
// Until this the window could OPEN a `.crail` and never write one back — see
// RailRfViewModel.Save.cs for what that meant. The writing itself is one line of
// RailDocumentIo; what is here is the path, the picker, the refusals and the per-document
// bookkeeping a new path drags with it.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow : ICrfDocumentWindow
{
    /// <summary>Gives the view model's two commands the half that knows there is a file picker.
    /// The BUTTONS bind to the same commands, so the glyph and the gesture cannot drift.</summary>
    private void InstallSaveHook(RailRfViewModel vm) =>
        vm.SaveRequested = saveAs => _ = SaveAsync(saveAs);

    /// <summary>
    /// Writes the document — to where it came from, or to a path the user picks.
    /// </summary>
    /// <remarks>
    /// <b>A window that has never been saved saves AS</b>, rather than refusing or inventing a
    /// path: <c>Tools ▸ railRF</c> makes exactly that window, and an import fills it, so the first
    /// save of real work is the ordinary case here rather than the exception.
    ///
    /// <para><b>The document is validated on the way out</b> — <c>RailDocumentIo.SaveToFile</c>'s
    /// own rule, and the reason a half-built rail refuses rather than writing a file whose only
    /// symptom is that it will not open next week. The sentence is the reader's, shown where every
    /// other railRF refusal is shown.</para>
    /// </remarks>
    private async Task SaveAsync(bool saveAs)
    {
        if (Vm is not { } vm) return;

        string? path = saveAs ? null : vm.DocumentPath;

        if (path is null or { Length: 0 })
        {
            if (StorageProvider is not { } sp) return;

            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the railRF document",
                // NO extension on the suggested name — Avalonia appends DefaultExtension itself and
                // supplying both spells it twice (the export path's own note).
                SuggestedFileName = SuggestedName(vm),
                DefaultExtension = RailDocumentIo.Extension.TrimStart('.'),
                FileTypeChoices =
                [
                    new FilePickerFileType("railRF document")
                        { Patterns = ["*" + RailDocumentIo.Extension] },
                ],
            });
            if (file is null) return;
            path = file.Path.LocalPath;
        }

        // ONE WINDOW PER DOCUMENT, and Save as… is the one gesture that can break it. Writing over
        // a `.crail` another window is holding would leave two working copies of one file, each
        // about to overwrite the other — the rule Show has kept since brief 7, stated here as a
        // refusal rather than discovered later as lost work.
        if (HeldByAnotherWindow(path))
        {
            vm.Refusal = new RailRefusal(
                $"{Path.GetFileName(path)} is already open in another railRF window. Two windows on " +
                "one document would write it from two working copies, so save this one under a " +
                "different name — or close the other window first.",
                RailRefusalControl.None);
            return;
        }

        try
        {
            RailDocumentIo.SaveToFile(path, vm.Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or NotSupportedException)
        {
            vm.Refusal = new RailRefusal(
                $"The document did not save: {ex.Message}", RailRefusalControl.None);
            return;
        }

        AdoptPath(path);
        vm.NoteSaved(path);
    }

    /// <summary>What the picker opens on — the document's own name, then the file it came from.</summary>
    private static string SuggestedName(RailRfViewModel vm) =>
        vm.Document.Name is { Length: > 0 } n ? n
        : vm.DocumentPath is { Length: > 0 } p ? Path.GetFileNameWithoutExtension(p)
        : "board";

    private bool HeldByAnotherWindow(string path) =>
        Open.TryGetValue(Path.GetFullPath(path), out var other) && !ReferenceEquals(other, this);

    /// <summary>
    /// Re-keys this window in the per-document table, which is what makes <c>Show</c> raise THIS
    /// window the next time that path is opened rather than building a second one on the same file.
    /// </summary>
    /// <remarks>
    /// The standalone window starts in no table at all (<see cref="ShowStandalone"/>: "a standalone
    /// window writes no document at all until it is saved"), so its first save is also its
    /// registration.
    /// </remarks>
    private void AdoptPath(string path)
    {
        string key = Path.GetFullPath(path);
        if (string.Equals(_openKey, key, StringComparison.OrdinalIgnoreCase)) return;

        if (_openKey is { } old) Open.Remove(old);
        _openKey = key;
        Open[key] = this;
    }

    // ── Closing a window closes a document, so it asks ───────────────────────────────────────

    private bool _closeConfirmed;

    /// <summary>
    /// Asks before a window with unsaved work goes away.
    /// </summary>
    /// <remarks>
    /// <b>wBond's own shape, and the same dialog</b> — one window per document means closing the
    /// window IS closing the document, and a Save the window has only just acquired would be worth
    /// little if the ordinary way out of the window still discarded everything silently.
    ///
    /// <para>The close is CANCELLED and re-issued rather than blocked: the answer comes from a modal
    /// that cannot be awaited inside the synchronous <c>OnClosing</c>. And a cancelled save PICKER
    /// cancels the close too, or "Save" would quietly behave as "Don't Save".</para>
    ///
    /// <para><b>This is not the only way out of the window</b> — quitting circuitRF does not close
    /// this window, it ends the process, and it once did so without asking. See
    /// <see cref="ICrfDocumentWindow"/> for that half.</para>
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeConfirmed || Vm is not { IsDirty: true })
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = ReissueCloseAsync();
    }

    /// <summary>Asks, then closes — the half of the old <c>ConfirmCloseAsync</c> that belongs to the
    /// window's own close box and to File ▸ Close, and NOT to the quit, which closes its windows
    /// itself once every one of them has answered.</summary>
    private async Task ReissueCloseAsync()
    {
        if (await ConfirmCloseAsync()) Close();
        else (Avalonia.Application.Current as App)?.AbortQuit();
    }

    /// <summary>
    /// <inheritdoc cref="ICrfDocumentWindow.ConfirmCloseAsync"/>
    /// </summary>
    /// <remarks>
    /// <b>It settles the document and stops.</b> It used to close the window itself, which is what
    /// the close box wants and what a quit must not have: the quit asks every window before closing
    /// any of them, so a window that closed as it answered would already be gone by the time a later
    /// one was cancelled (<see cref="ICrfDocumentWindow"/>).
    ///
    /// <para>A cancelled save PICKER cancels the close too, or "Save" would quietly behave as
    /// "Don't Save".</para>
    /// </remarks>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (_closeConfirmed) return true;
        if (Vm is not { IsDirty: true } vm) return true;

        string name = vm.DocumentPath is { Length: > 0 } p
            ? Path.GetFileName(p)
            : "this railRF document";

        var answer = await new SaveChangesDialog(
            $"Save changes to {name} before closing?",
            saveLabel:     "Save",
            dontSaveLabel: "Don't Save",
            cancelLabel:   "Cancel",
            title:         "Unsaved Changes").ShowDialog<SaveChangesResult>(this);

        if (answer == SaveChangesResult.Cancel) return false;

        if (answer == SaveChangesResult.Save)
        {
            await SaveAsync(saveAs: false);
            if (vm.IsDirty) return false;
        }

        _closeConfirmed = true;
        return true;
    }
}
