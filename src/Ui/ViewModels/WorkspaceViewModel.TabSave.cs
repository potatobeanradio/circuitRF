using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Dock.Model.Controls;
using Dock.Model.Core;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Smith;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Save and Save As on a DOCUMENT TAB's own context menu (owner request, 2026-09-09) — the per-tab
/// half of what File ▸ Save has always done for the ACTIVE document.
///
/// <para><b>Why it is not just the File menu's command with a different parameter.</b> Every save
/// route in <c>WorkspaceViewModel</c> starts at <c>ResolveActiveDocumentForCommands()</c>: ⌘S means
/// "the document in front of the user". A tab menu means the tab that was right-clicked, which is
/// very often NOT the active one — right-clicking a background tab and choosing Save must save THAT
/// document, not the one on screen. So the entry points here take the dockable and nothing else,
/// and each one delegates to the per-document route that already exists. <b>No file is written
/// here</b>; a second copy of a save is how two spellings of "saved" start disagreeing (the Reveal
/// item's own history, RESOLVED.md §4).</para>
///
/// <para><b>Docked and floating both, which is the whole reason these take an
/// <see cref="IDockable"/>.</b> A torn-off document lives in a <c>CrfHostWindow</c> whose DataContext
/// is not this view model, so neither the menu nor the command can reach the workspace by walking the
/// visual tree — <see cref="WorkspaceOf"/> answers it from the dockable itself, and
/// <see cref="HostWindowOf"/> returns the FLOATING host as the dialog owner so a picker opens over the
/// window the user right-clicked in rather than over the shell behind it.</para>
///
/// <para><b>Neither item is gated by <c>CanExecute</c>, deliberately.</b> The menu is one shared
/// <c>ContextMenu</c> instance whose DataContext changes as tabs are right-clicked; whether Avalonia
/// re-queries a parameterized command's CanExecute at that moment depends on attachment order, and a
/// stale grey-out would make a dirty document unsaveable with no way to tell why. Visibility (a
/// converter, re-evaluated on DataContext change like the Reveal item's already is) hides what has no
/// route at all, and the two cases a gate would have covered are reported instead: nothing to save
/// says so, and a read-only document is routed to Save As with SL2 R-sl2-8's own wording — the rule
/// that offering a Save which cannot succeed is how a session's work gets lost.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    // ── Which workspace, and which window ─────────────────────────────────────

    /// <summary>
    /// The workspace <paramref name="dockable"/> belongs to, asked of the dockable rather than of the
    /// window it happens to be in — the one question <c>WorkspaceLocator</c> cannot answer, because a
    /// floating host's DataContext is the document, not the workspace.
    ///
    /// <para>The factory is the authority: one factory per view model, stamped onto every dockable it
    /// initializes, and it is the same instance for a torn-off document as for a docked one. The tree
    /// walk below it is the honest fallback for a dockable Dock never routed through
    /// <c>InitDockable</c>; it costs nothing on the small trees this is asked about, and it is what
    /// keeps a null answer meaning "no workspace has this document" rather than "the stamp was
    /// missing".</para>
    /// </summary>
    internal static WorkspaceViewModel? WorkspaceOf(IDockable? dockable)
    {
        if (dockable is null) return null;

        if (dockable.Factory is CircuitRfDockFactory own && own.Owner is { } owner) return owner;
        if ((dockable.Owner as IDock)?.Factory is CircuitRfDockFactory ownersFactory
            && ownersFactory.Owner is { } ownersOwner) return ownersOwner;

        foreach (var window in Views.WorkspaceLocator.AllWindows())
            if (window.DataContext is WorkspaceViewModel vm && vm.OwnsDockable(dockable)) return vm;

        return null;
    }

    /// <summary>True when <paramref name="dockable"/> is somewhere in this workspace's layout — the
    /// docked tree AND every floating window hanging off its root, since a torn-off document is still
    /// this workspace's document.</summary>
    internal bool OwnsDockable(IDockable dockable) => Layout is { } root && Holds(root, dockable);

    private static bool Holds(IDockable node, IDockable wanted)
    {
        if (ReferenceEquals(node, wanted)) return true;

        if (node is IDock dock && dock.VisibleDockables is { } children)
            foreach (var child in children)
                if (child is not null && Holds(child, wanted)) return true;

        if (node is IRootDock { Windows: { } windows })
            foreach (var window in windows)
                if (window?.Layout is { } floated && Holds(floated, wanted)) return true;

        return false;
    }

    /// <summary>
    /// The window a save started from this dockable should parent its dialogs to: the FLOATING host
    /// when the document is torn off, this workspace's shell otherwise. Same resolution
    /// <c>BringDockableWindowToFront</c> uses — a floating root carries its own <c>IDockWindow</c>,
    /// the shell's root does not.
    /// </summary>
    internal Window? HostWindowOf(IDockable dockable)
        => _factory.FindRoot(dockable) is IRootDock { Window.Host: Window host }
               ? host
               : Views.WorkspaceLocator.WindowFor(this);

    // ── What each document kind can do ────────────────────────────────────────

    /// <summary>
    /// True when this document kind has a Save route at all. The tab menu hides the item otherwise
    /// rather than showing it disabled — the Reveal item's own convention: an entry that does not
    /// apply is not an entry that is temporarily unavailable.
    ///
    /// <para>Three kinds answer false and all three are deliberate. <c>StubDocument</c> is the Welcome
    /// tab and has no file, ever. <c>CellParameterEditorDocument</c> writes its <c>.ccell</c> on every
    /// command it executes (<c>CellParameterEditModel.Save</c>), so it is never dirty and there is
    /// nothing a Save could do. <c>MarkdownDocument</c> is a README shown read-only: it holds the
    /// PARSED lines and not the source text, so there is nothing an edit could be applied to and
    /// nothing a Save could write — circuitRF is not a Markdown editor, and the user's own editor is
    /// where that file is changed.</para>
    /// </summary>
    internal static bool HasSaveRoute(IDockable? dockable) => dockable is
        SchematicDocument or SymbolEditorDocument or LayoutDocument or TechDocument or
        EmSetupDocument or DataDisplayDocument or WBondDocument or HarmonicaDocument or
        SmithChartDocument;

    /// <summary>
    /// True when this document kind can be written to a DIFFERENT file and followed there afterwards.
    /// Every kind with a Save route has one — the two that answer false here are the two that answer
    /// false above, for the reasons given there.
    ///
    /// <para><b><c>TechDocument</c> was withheld at first and the owner asked for it back the same
    /// day, correctly.</b> The hesitation was that a design resolves its technology through an
    /// explicit reference (a layout's <c>TechRef</c>, or the workspace's <c>DefaultTechRef</c>), so a
    /// Save As leaves every open layout resolving the ORIGINAL file — but that is exactly what a
    /// schematic's own Save As does to the cells that instantiate it, and circuitRF has always
    /// offered that. <c>OnTechSavedAs</c> says so in the Messages panel instead of hiding the menu
    /// item.</para>
    /// </summary>
    internal static bool HasSaveAsRoute(IDockable? dockable) => dockable is
        SchematicDocument or SymbolEditorDocument or LayoutDocument or TechDocument or
        EmSetupDocument or DataDisplayDocument or WBondDocument or HarmonicaDocument or
        SmithChartDocument;

    /// <summary>Unsaved work in THIS document — the same per-kind test <c>CanSaveAllDocuments</c>
    /// applies to the active one. A never-saved wBond or harmonicaRF document counts even when clean:
    /// it has no file yet, so its first Save is the one that creates it.</summary>
    private static bool HasUnsavedWork(IDockable dockable) => dockable switch
    {
        SchematicDocument d     => d.IsDirty,
        SymbolEditorDocument d  => d.IsDirty,
        LayoutDocument d        => d.IsDirty,
        TechDocument d          => d.IsDirty,
        EmSetupDocument d       => d.IsDirty,
        DataDisplayDocument d   => d.ViewModel.Window.HasUnsavedChanges(),
        WBondDocument d         => d.IsDirty || d.FilePath is null,
        HarmonicaDocument d     => d.IsDirty || d.FilePath is null,
        SmithChartDocument d    => d.IsDirty || d.FilePath is null,
        _                       => false,
    };

    // ── The two entry points ──────────────────────────────────────────────────

    /// <summary>
    /// The tab menu's Save. Saves the document that was right-clicked, wherever its tab lives.
    /// </summary>
    internal async Task SaveFromTabAsync(IDockable dockable)
    {
        if (!HasSaveRoute(dockable)) return;
        if (HostWindowOf(dockable) is not { } window) return;

        // SL2 R-sl2-7/-8: a read-only document's Save is a Save As, and it says why. Refusing here
        // instead would be a refusal the user can do nothing with at the one moment they are trying
        // to keep their work.
        if (IsDocumentReadOnly(dockable))
        {
            ReportReadOnlySaveAsRoute(dockable);
            if (HasSaveAsRoute(dockable)) await SaveAsFromTabAsync(dockable);
            return;
        }

        if (!HasUnsavedWork(dockable))
        {
            Messages.Info("Nothing to save.");
            return;
        }

        // RC-5 R-rc5-4a: about to write a design file into this workspace — the same note every other
        // save route makes, for the same reason (see SaveAllDocuments).
        NoteWorkspaceWrite();
        try
        {
            switch (dockable)
            {
                case SchematicDocument d:    await SaveSingleDocument(d, window);                break;
                case SymbolEditorDocument d: await SaveSingleSymbolDocument(d, window);           break;
                case LayoutDocument d:       await SaveSingleLayoutDocument(d, window);           break;
                case DataDisplayDocument d:  await SaveDataDisplayDoc(d, window);                 break;
                case WBondDocument d:        await SaveWBondDoc(d, window);                       break;
                case HarmonicaDocument d:    await SaveHarmonicaDoc(d, window, saveAs: false);    break;
                case SmithChartDocument d:   await SaveSmithChartDoc(d, window, saveAs: false);   break;
                case TechDocument d:         d.ViewModel.SaveCommand.Execute(null);               break;
                case EmSetupDocument d:      d.ViewModel.SaveCommand.Execute(null);               break;
            }
        }
        finally
        {
            AfterTabSave();
        }
    }

    /// <summary>
    /// The tab menu's Save As… — writes the right-clicked document to a file the user picks and
    /// follows it there, exactly as File ▸ Save … As does for the active one.
    /// </summary>
    internal async Task SaveAsFromTabAsync(IDockable dockable)
    {
        if (!HasSaveAsRoute(dockable)) return;
        if (HostWindowOf(dockable) is not { } window) return;

        NoteWorkspaceWrite();
        try
        {
            switch (dockable)
            {
                case SchematicDocument d:    await SaveSchematicAs(d, window);                    break;
                case SymbolEditorDocument d: await SaveSymbolAs(d, window);                       break;
                case LayoutDocument d:       await SaveLayoutAs(d, window);                       break;
                case DataDisplayDocument d:  await SaveDataDisplayDoc(d, window, saveAs: true);   break;
                case WBondDocument d:        await SaveWBondDoc(d, window, saveAs: true);         break;
                case HarmonicaDocument d:    await SaveHarmonicaDoc(d, window, saveAs: true);     break;
                case SmithChartDocument d:   await SaveSmithChartDoc(d, window, saveAs: true);    break;
                case TechDocument d:         await SaveTechAs(d, window);                         break;
                case EmSetupDocument d:      await SaveEmSetupAs(d, window);                      break;
            }
        }
        finally
        {
            AfterTabSave();
        }
    }

    /// <summary>
    /// What every single-document save does when it finishes: refresh the <c>.cws</c> silently (it
    /// carries the open-document list, and the user asked to save one file, not to be told about the
    /// workspace file) and re-evaluate File ▸ Save's own enabled state.
    /// </summary>
    private void AfterTabSave()
    {
        if (CurrentWorkspacePath is not null)
            WriteWorkspaceFile(CurrentWorkspacePath, silent: true);

        SaveAllDocumentsCommand.NotifyCanExecuteChanged();
    }

    // ── The two routes that did not exist as workspace-level methods yet ──────

    /// <summary>
    /// harmonicaRF's own Save/Save As, reached without the view.
    ///
    /// <para>The implementation is <see cref="HarmonicaDocumentSave"/>'s, shared with
    /// <c>HarmonicaView</c>'s File menu — harmonicaRF also runs as a standalone application with no
    /// workspace at all, so the route cannot live here, and a copy of it here would be a second
    /// spelling of ".charm is written like this".</para>
    /// </summary>
    private async Task SaveHarmonicaDoc(HarmonicaDocument doc, Window owner, bool saveAs)
    {
        if (await HarmonicaDocumentSave.RunAsync(doc, owner, this, saveAs) is { } error)
            Messages.Error(error);
    }

    /// <summary>
    /// A Smith Chart document's own Save/Save As.
    /// </summary>
    /// <remarks>
    /// The implementation is <see cref="SmithChartDocumentSave"/>'s, for the reason that type's own
    /// remarks give: a background tab's content is created on demand, so a route that started at the
    /// view would silently do nothing on the tab the user actually right-clicked. A refusal here is
    /// <c>SmithDesign.Refusal</c>'s sentence, raised BEFORE anything reaches the disk — a document
    /// that cannot be read back is a document that was never written.
    /// </remarks>
    private async Task SaveSmithChartDoc(SmithChartDocument doc, Window owner, bool saveAs = false)
    {
        if (await SmithChartDocumentSave.RunAsync(doc, owner, this, saveAs) is { } error)
            Messages.Error(error);
    }

    /// <summary>
    /// Writes an EM setup to a different <c>.cem</c> and follows it from then on. The picker is here
    /// rather than on the view model because everything under <c>src/Ui/Layout/</c> is framework-free;
    /// the VM takes a resolved path and does the I/O, and its <c>EmSetupSavedAs</c> event is what
    /// re-keys this workspace's open-document map. Called by the tab menu and by the EM setup editor's
    /// own Save As button, which is why it is not private to either.
    /// </summary>
    internal async Task SaveEmSetupAs(EmSetupDocument doc, Window owner)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "Save EM Setup As",
            SuggestedFileName   = Path.GetFileName(doc.ViewModel.FilePath),
            DefaultExtension    = "cem",
            ShowOverwritePrompt = true,
            FileTypeChoices     = [new FilePickerFileType("circuitRF EM Setup") { Patterns = ["*.cem"] }],
        });

        if (file?.TryGetLocalPath() is { Length: > 0 } path) doc.ViewModel.SaveAs(path);
    }

    /// <summary>
    /// Writes a technology to a different <c>.ctech</c> and follows it from then on. Same shape as
    /// <see cref="SaveEmSetupAs"/> and for the same reason — <c>src/Ui/Layout/</c> is framework-free,
    /// so the picker is here and the VM takes a resolved path — and its <c>TechSavedAs</c> event is
    /// what re-keys the open-document map, invalidates BOTH cache entries, and says which designs
    /// still point at the old file (<c>OnTechSavedAs</c>).
    /// </summary>
    private async Task SaveTechAs(TechDocument doc, Window owner)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "Save Technology As",
            SuggestedFileName   = Path.GetFileName(doc.FilePath),
            DefaultExtension    = "ctech",
            ShowOverwritePrompt = true,
            FileTypeChoices     = [new FilePickerFileType("circuitRF Technology") { Patterns = ["*.ctech"] }],
        });

        if (file?.TryGetLocalPath() is { Length: > 0 } path) doc.ViewModel.SaveAs(path);
    }
}
