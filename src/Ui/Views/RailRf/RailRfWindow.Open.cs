// The Open button (owner, 2026-09-18) — the route into this window that is not an import.
//
// ── WHY IT IS NOT THE IMPORT BUTTON WITH A WIDER FILTER ─────────────────────────────────────────
//
// The import CREATES: it runs `GerberImportEntry.Run`, makes a cell, mints a technology from the
// artwork's own layers and lands all of it in a workspace. Open does none of that. It points the
// window at something that already exists — a `.crail` anywhere on disk, or a `.clay` in any
// workspace, whose own `TechRef` and ancestor workspace are what supply the stackup. Putting the two
// behind one button would mean one of them silently doing the other's work on the wrong file type.
//
// ── AND THE WALKS ARE STILL NOT HERE ────────────────────────────────────────────────────────────
//
// A `.crail` goes through `RailRfWindow.Show`, the same call the project tree's double-click makes,
// so a document opened from this button is the same window with the same dedup and the same loaded
// references. A `.clay` resolves its technology through `TechnologyResolver.ResolveForDocument` —
// `em`, `render` and the layout editor's own resolution, not a fourth one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    private void WireOpenButton() => OpenButton.Click += async (_, _) => await OpenDocumentAsync();

    /// <summary>
    /// Picks a <c>.crail</c> or a <c>.clay</c> and opens it.
    /// </summary>
    private async Task OpenDocumentAsync()
    {
        if (Vm is not { } vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "railRF — Open",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("railRF document or board layout")
                {
                    Patterns = ["*" + RailDocumentIo.Extension, "*.clay"],
                },
                new FilePickerFileType("railRF document") { Patterns = ["*" + RailDocumentIo.Extension] },
                new FilePickerFileType("Board layout")    { Patterns = ["*.clay"] },
                new FilePickerFileType("All Files")       { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;

        string chosen = files[0].Path.LocalPath;

        if (string.Equals(Path.GetExtension(chosen), RailDocumentIo.Extension,
                          StringComparison.OrdinalIgnoreCase))
        {
            OpenRailDocument(chosen);
            return;
        }

        OpenLayout(vm, chosen);
    }

    /// <summary>
    /// Opens a <c>.crail</c> through <see cref="Show"/>, and closes THIS window when it is the empty
    /// standalone one that was used to get here.
    /// </summary>
    /// <remarks>
    /// <b>Only when it is empty and unsaved.</b> Tools ▸ railRF opens a scratch window, Open is the
    /// first thing pressed in it, and leaving it behind means two windows of which one is the one that
    /// was wanted — but a window holding a document, or a board somebody imported, is work, and Open is
    /// not a command that discards work.
    /// </remarks>
    private void OpenRailDocument(string path)
    {
        RailDocument document;
        try { document = RailDocumentIo.LoadFromFile(path); }
        catch (Exception ex)
        {
            if (Vm is { } vm)
                vm.PendingImportRefusal = new RailRefusal(
                    $"'{Path.GetFileName(path)}' did not read: {ex.Message}", RailRefusalControl.None);
            return;
        }

        // AND NOT DIRTY, since the window learned to save (2026-09-19). The other three clauses
        // catch every kind of work this window used to be able to hold; a panel toggle or a Settings
        // edit in an otherwise empty scratch window is not caught by any of them, and closing it
        // would now also raise the unsaved-changes prompt in the middle of an Open — which is a
        // question about a window the user is in the act of replacing.
        bool disposable = Vm is { DocumentPath: null, Board: null, IsDirty: false } scratch
                       && scratch.Document.Rails.Count == 0;

        var opened = Show(document, path, this, out var notes);

        // The notes belong to the window that is now showing that document, NOT to this one — this
        // one is usually about to close. Joined into one sentence because the strip holds one
        // refusal, and each of these is a whole statement on its own.
        if (notes.Count > 0 && opened.Vm is { } target)
            target.PendingImportRefusal = new RailRefusal(
                string.Join(" ", notes), RailRefusalControl.None);

        if (disposable && !ReferenceEquals(opened, this)) Close();
    }

    /// <summary>
    /// Loads a bare <c>.clay</c> as this window's board — the artwork with no companion tables.
    /// </summary>
    /// <remarks>
    /// <b>The stackup is the LAYOUT's, never the open workspace's</b> — R-fgn-3's ancestor walk, which
    /// is the whole reason this is worth having: a board drawn in one workspace opens here against the
    /// technology that priced it, not against whichever workspace happens to be open.
    /// </remarks>
    private void OpenLayout(RailRfViewModel vm, string clay)
    {
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception ex)
        {
            vm.PendingImportRefusal = new RailRefusal(
                $"'{Path.GetFileName(clay)}' did not read: {ex.Message}", RailRefusalControl.None);
            return;
        }

        var (resolution, _) = TechnologyResolver.ResolveForDocument(
            view.TechRef, clay, null, new TechnologyCache());

        if (resolution.Tech is not { } tech)
        {
            vm.PendingImportRefusal = new RailRefusal(
                $"'{Path.GetFileName(clay)}' resolved no technology, so railRF has no stackup to price "
              + "its copper against — no thicknesses and no conductivities. Open it from a workspace "
              + "whose technology it references, or import the board instead.",
                RailRefusalControl.Stackup);
            return;
        }

        vm.Document.ArtworkCellRef = clay;
        var flattenNotes = new List<string>();
        var shapes = RailArtwork.FlattenedShapes(view, clay, tech, flattenNotes);

        // ── R-ab1-5b: THE SITE THAT TOOK NO PADS AT ALL ────────────────────────────────────────
        //
        // This is the whole reported gap, in one line. A bare `.clay` is precisely the board a user
        // DREW — its parts are footprint instances carrying designators, and every one of their lands
        // is inside one — and until this call it opened with `Pads = []`: no pick list, every refdes
        // anchor falling back to a coordinate, and every mounting inductance a typed one. There is no
        // companion netlist on this path by construction, so every pad here is the artwork's own.
        var resolvedPads = RailArtwork.PadsFor(view, clay, tech, null, null, shapes);
        foreach (string d in resolvedPads.Notes) if (!flattenNotes.Contains(d)) flattenNotes.Add(d);

        vm.Board = new RailBoardInputs
        {
            Shapes         = shapes,

            // The model this window just read, so the board reads in the `.clay`'s own display unit
            // rather than in DBU — the same line the `.crail` open needs, for the same reason.
            View           = view,
            Technology     = tech,
            TechPath       = resolution.ResolvedPath,
            DbuPerMicron   = view.DbuPerMicron,
            ArtworkCellRef = clay,
            Pads           = resolvedPads.Pads,
            NetPoints      = resolvedPads.NetPoints,
            Nets           = resolvedPads.Nets,
            NetOrigin      = resolvedPads.NetOrigin,
        };

        var openNotes = resolution.Diagnostics.Concat(flattenNotes).ToList();
        vm.PendingImportRefusal = openNotes.Count == 0
            ? null
            : new RailRefusal(string.Join(" ", openNotes), RailRefusalControl.Stackup);
    }
}
