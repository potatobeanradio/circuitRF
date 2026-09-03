using Dock.Model.Core;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// SL2 (brief-shared-library-2-read-only-workspaces.md): circuitRF noticing that a workspace's files
/// cannot be written, and behaving accordingly — <b>before</b> the user has done the work, not after.
///
/// <para>Nothing here ENFORCES anything. The share's own permissions are the enforcement (R-sl-2, and
/// a product-level permission model on top of a filesystem that already has one is two sources of
/// truth); this is entirely about the product telling the truth about them. Every failure SL2
/// addresses was already non-silent — <c>FileAccessDiagnostics</c> has turned an
/// <c>UnauthorizedAccessException</c> into a readable sentence for a year — and every one of them was
/// LATE. Lateness is the whole complaint: a refusal before the work is a supported state, and a
/// failure after it is lost work.</para>
///
/// <para>The discovery itself is <see cref="WorkspaceWritability"/>, in <c>src/Design</c>; this file
/// is the behaviour that hangs off it.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// R-sl2-11: true when the currently open workspace's own root cannot be written. A workspace in
    /// this state is entirely usable — browsing it, reading a schematic, pushing into a hierarchy,
    /// seeing what a cell's parameters are, and (R-sl2-12) resolving the kits a referenced cell needs
    /// are all READ operations, and they are the whole point of the corporate-library workflow. What
    /// it does not do is write (R-sl2-5).
    /// </summary>
    public bool IsCurrentWorkspaceReadOnly
        => CurrentWorkspacePath is { } cws
           && WorkspaceWritability.IsReadOnly(Path.GetDirectoryName(cws));

    /// <summary>
    /// R-sl2-11: said ONCE, at open, and never as a refusal. Posted after the "Opened" line so the
    /// two read as one event. Deliberately Info, not Warning — §5A.4's rule that an unusual-but-fine
    /// state never gets an error colour applies to the message list exactly as it applies to the
    /// chrome band, and a read-only library is the intended, desirable shape of this workflow.
    /// </summary>
    private void ReportWorkspaceReadOnlyIfNeeded(string cwsPath)
    {
        if (!WorkspaceWritability.IsReadOnly(Path.GetDirectoryName(cwsPath))) return;

        Messages.Info(
            $"'{Path.GetFileName(Path.GetDirectoryName(cwsPath))}' is read-only on this machine, so " +
            "it is open for reading. Nothing about this session is recorded back into it, and edits " +
            "to its documents save with Save As into a workspace you can write.");
    }

    // ── The per-document question (R-sl2-4/-7) ───────────────────────────────

    /// <summary>
    /// R-sl2-4: the file a saveable document would actually write, or null for a scratch document
    /// that has never been saved. The switch mirrors <c>WriteWorkspaceFile</c>'s own active-document
    /// switch exactly — the same set of saveable document types, reached the same way — so a document
    /// kind added to one and not the other is a visible omission rather than a silent one.
    /// </summary>
    internal static string? DocumentFilePath(IDockable? dockable) => dockable switch
    {
        SchematicDocument d           => d.FilePath,
        SymbolEditorDocument d        => d.ViewModel.CurrentSymbolPath,
        CellParameterEditorDocument d => d.ViewModel.EditModel.CcellPath,
        DataDisplayDocument d         => d.FilePath,
        LayoutDocument d              => d.FilePath,
        TechDocument d                => d.FilePath,
        EmSetupDocument d             => d.FilePath,
        WBondDocument d               => d.FilePath,
        _                             => null,
    };

    /// <summary>
    /// R-sl2-4/-7: true when this document's own directory cannot be written — the question that
    /// actually decides whether Save can succeed. It is asked of the DOCUMENT's directory rather than
    /// of the open workspace because the two are routinely different: a document open from a
    /// read-only library sits in a workspace this window did not open at all, and a workspace can be
    /// writable while one cell folder inside it is not.
    ///
    /// <para>A scratch document (no path yet) is never read-only — it saves through a picker, which
    /// asks the filesystem its own question about wherever the user points it.</para>
    /// </summary>
    internal static bool IsDocumentReadOnly(IDockable? dockable)
        => WorkspaceWritability.IsDocumentReadOnly(DocumentFilePath(dockable));

    /// <summary>
    /// R-sl2-7's sentence, or null when the document is writable. It names the WORKSPACE the file
    /// belongs to rather than the directory, because that is the thing the user recognises and the
    /// thing they would have to ask the librarian about — and it names the way forward in the same
    /// breath, since a refusal that does not say what to do instead is just the late failure moved
    /// earlier.
    /// </summary>
    internal static string? ReadOnlyDocumentReason(IDockable? dockable)
    {
        if (DocumentFilePath(dockable) is not { Length: > 0 } path) return null;
        if (!WorkspaceWritability.IsDocumentReadOnly(path)) return null;

        string name = Path.GetFileNameWithoutExtension(path);
        return WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(path)) is { } root
            ? $"'{name}' belongs to '{Path.GetFileName(root)}', which is read-only on this machine. " +
              "Save a copy into your own workspace instead."
            : $"'{name}' is in a folder that cannot be written. Save a copy somewhere else instead.";
    }

    /// <summary>
    /// R-sl2-7: the reason Save is disabled for the ACTIVE document, or null when it is not. Bound by
    /// the menu item's tooltip, so the greyed-out item states its own reason rather than leaving the
    /// user to guess — the same "disabled with a reason" convention §3/R13a already applies to
    /// "Nothing to save."
    /// </summary>
    public string? ActiveDocumentReadOnlyReason
        => ReadOnlyDocumentReason(ResolveActiveDocumentForCommands());

    /// <summary>
    /// The Save menu item's tooltip. The item has always carried the static "Nothing to save." — the
    /// §3/R13a convention that a disabled item states its own reason — and R-sl2-7 adds the second
    /// reason it can now be disabled for. Without this the item greys out on a library document with
    /// a tooltip that is simply false ("Nothing to save" on a document full of unsaved edits), which
    /// is worse than no tooltip.
    /// </summary>
    public string SaveMenuTooltip => ActiveDocumentReadOnlyReason ?? "Nothing to save.";

    /// <summary>
    /// Re-raises the two menu-facing read-only properties. Called from the same places that already
    /// refresh <c>SaveAllDocumentsCommand</c>'s CanExecute — the active document changing is what
    /// changes both answers, and there is no property-change source behind a filesystem probe.
    /// </summary>
    private void RefreshReadOnlyMenuState()
    {
        OnPropertyChanged(nameof(ActiveDocumentReadOnlyReason));
        OnPropertyChanged(nameof(SaveMenuTooltip));
        OnPropertyChanged(nameof(IsCurrentWorkspaceReadOnly));
    }

    // ── Creating INTO an unwritable place (R-sl2-13) ─────────────────────────

    /// <summary>
    /// R-sl2-13: the one rule behind all three "create a workspace here" refusals — File ▸ New
    /// Workspace, Save Workspace As, and the save-plan dialog's own workspace step. Returns the
    /// refusal sentence when <paramref name="parentDir"/> cannot be written, or null when it can.
    ///
    /// <para>It is one function rather than three checks because the three sites fail in the same
    /// way and it is a way worth failing only once: <c>SavePlanExecutor</c> runs AFTER a plan the
    /// user has confirmed, and it creates the workspace folder and its <c>.cws</c> before it creates
    /// any cell — so discovering the parent is unwritable inside the executor means a confirmed plan
    /// that cannot be carried out AND a half-made workspace on disk to clean up. The directory is
    /// named because "somewhere you picked" is not something a user can act on.</para>
    /// </summary>
    internal static string? UnwritableParentRefusal(string? parentDir, string whatWasBeingCreated)
        => WorkspaceWritability.IsReadOnly(parentDir)
            ? $"{whatWasBeingCreated} — '{parentDir}' is read-only on this machine. Choose a location you can write to."
            : null;

    /// <summary>
    /// R-sl2-7/-8: says why a Save became a Save As (or, in a sweep, why it did not happen at all).
    /// Info rather than Warning for the same reason the open notice is: a read-only library document
    /// is a normal, supported, desirable state, and colouring it as a problem would teach users that
    /// the workflow the whole series exists to support is going wrong.
    /// </summary>
    private void ReportReadOnlySaveAsRoute(IDockable doc, bool sweep = false)
    {
        if (ReadOnlyDocumentReason(doc) is not { } reason) return;
        Messages.Info(sweep ? reason + " It was left open and unsaved." : reason);
    }
}
