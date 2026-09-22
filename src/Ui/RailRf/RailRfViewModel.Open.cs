// What OPENING a `.crail` fills in, as against what importing one does.
//
// ── THE GAP THIS CLOSES ──────────────────────────────────────────────────────────────────────────
//
// Until 2026-09-18 opening a `.crail` from the project tree constructed the view model and stopped.
// The rails, the ports and the target came up; the BOARD did not, because only the import dialog ever
// set `Board`, and neither `ArtworkCellRef` nor `PartLibraryRef` was resolved by anything on the
// window side. So a document whose artwork is on disk, named in its own text and openable by path
// came up saying "No board yet. Import one" — and `circuitrf rail` answered the very same file. The
// shipped Power Rail example is exactly that shape and its README says the window opens with the
// board already loaded, which is how it was found from the outside.
//
// ── AND WHY THE WALKS ARE NOT IN THIS FILE ───────────────────────────────────────────────────────
//
// They are `RailArtwork`'s, in `src/Design`, and the verb goes through the same call. A second copy
// here would be a second answer to "which file does this reference mean" — the divergence the CLI
// chapter's own rule is against, and the one that is invisible until someone compares two surfaces on
// one document. What is HERE is the application-side half: turning a resolution into the window's
// own `RailBoardInputs`, and handing the caller the sentences to post.

using System.Collections.Generic;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// Resolves the artwork, the stackup and the part library this document NAMES, and loads them.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here is a refusal that stops the window opening.</b> A `.crail` whose artwork has
    /// moved still opens — with its rails, its ports and its target — and says so; that is the same
    /// rule the layout editor keeps for a technology it cannot resolve. What would be wrong is opening
    /// it silently with no board, which is what used to happen when the artwork was right there.
    ///
    /// <para>It is a no-op on a document with no path (a standalone window), because every reference a
    /// <c>.crail</c> carries is relative to the <c>.crail</c> and there is nothing to be relative
    /// to.</para>
    /// </remarks>
    /// <returns>What could not be resolved, in the caller's words to post. Empty on success, and
    /// empty for a document that names nothing — naming no artwork is an ordinary state.</returns>
    public IReadOnlyList<string> LoadDocumentReferences()
    {
        if (_documentPath is not { Length: > 0 } path) return [];

        var notes = new List<string>();

        var found = RailArtwork.Resolve(_document, path, null, new TechnologyCache());
        switch (found.Outcome)
        {
            case RailArtworkOutcome.NoArtworkRef:
                break;   // nothing was named; the window says "import a board" and that is correct

            case RailArtworkOutcome.NotFound:
                notes.Add($"The artwork '{found.Detail}' this document names was not found "
                        + $"({found.ClayPath ?? "no layout view"}). Import the board again, or fix the reference.");
                break;

            case RailArtworkOutcome.Unreadable:
                notes.Add($"The artwork '{found.ClayPath}' did not read: {found.Detail}");
                break;

            case RailArtworkOutcome.NoTechnology:
                // railRF prices copper against a stackup, so this is not the layout editor's
                // "draws on the fallback palette" — there are no thicknesses and no conductivities,
                // and a board loaded without them would refuse at Run with a less useful sentence.
                notes.Add($"'{found.ClayPath}' resolved no technology, so railRF has no stackup to "
                        + "price its copper against. The board was not loaded.");
                break;

            case RailArtworkOutcome.Resolved when found is { View: { } view, Technology: { } tech }:
                foreach (string d in found.Diagnostics) notes.Add(d);

                // ── THE COMPANIONS, AND WHY THEY ARE READ HERE AND NOT AT IMPORT ─────────────
                //
                // These are what make a REFDES mean anything: with no pads, every source and load
                // anchor has to be a coordinate and PdnMountingLoopExtractor can compute no
                // mounting loop for any part on any board. Both degrade to the typed path in
                // silence, which is why they are resolved on OPEN rather than only in the import
                // session — an import that read a netlist and was then saved used to lose it.
                //
                // The netlist's units are cross-checked against the ARTWORK's extent (R-gi5-10),
                // so it is read after the artwork and in the artwork's own resolution.
                var netlist = RailArtwork.ResolveBoardNetlist(
                    _document, path, view.DbuPerMicron, out string? netlistPath, out string? netlistError);
                // THE SAME SENTENCE THE IMPORT SAYS, and that is the point of routing it through
                // RailImportReport rather than writing one here (field report, 2026-09-22). A
                // designer who imported a file that is not a board netlist, saved, and opened the
                // document again got a SHORTER and less useful message on the second surface than on
                // the first — the import names which family of file the row wants and this did not.
                // One document, one answer.
                if (netlistError is { Length: > 0 })
                    notes.Add($"The board netlist '{netlistPath}' did not read: "
                            + $"{RailImportReport.RefusalTail(netlistError)} Every port anchored by "
                            + "refdes is unresolved and no mounting loop can be computed; typed "
                            + "values are unaffected.");
                else if (netlist is not null)
                    BoardNetlist = netlist;

                var placed = RailArtwork.ResolvePlacement(
                    _document, path, view.DbuPerMicron, out string? placedPath, out string? placedError);
                if (placedError is { Length: > 0 })
                    notes.Add($"The placement table '{placedPath}' did not read: {placedError}. The "
                            + "parts table's Position column is empty.");
                else if (placed is not null)
                    Placement = placed;

                foreach (string d in netlist?.Diagnostics ?? []) notes.Add(d);

                // The EXTRACTION reads the flattened artwork; the picture below reads the hierarchy.
                // A board whose parts are footprint cells keeps every land inside an instance, so the
                // unflattened read is a board with the rail's copper on it and not one capacitor land.
                var flattenNotes = new List<string>();
                var shapes = RailArtwork.FlattenedShapes(view, found.ClayPath, tech, flattenNotes);
                foreach (string d in flattenNotes) notes.Add(d);

                // R-ab1-5b. Through the ONE funnel, which is what makes a board the user DREW
                // resolve its own pads here and in the verb and in the bare-`.clay` open, all three
                // out of one answer. It applies R-ab1-3's per-refdes precedence, so a document that
                // ships an `.ipc` is unchanged.
                // `shapes` rather than `view.Shapes`: brief 2's stamped-net partition is built
                // over the FLATTENED copper, because a board whose parts are footprint cells keeps
                // every land inside an instance and a pad standing on nothing takes no name.
                var resolvedPads = RailArtwork.PadsFor(
                    view, found.ClayPath, tech, netlist, null, shapes);
                // An instance that does not resolve contributes neither geometry nor pads, and both
                // walks report it with the SAME sentence (R-ab1-1c) — so it is said once.
                foreach (string d in resolvedPads.Notes) if (!notes.Contains(d)) notes.Add(d);

                Board = new RailBoardInputs
                {
                    Shapes         = shapes,

                    // THE LAYOUT ITSELF, and not only its shapes (owner, 2026-09-20). `View` is what
                    // carries the artwork's DISPLAY UNIT, and without it `RailLengthFormat` falls back
                    // to raw DBU — so a `.crail` opened from the project tree read every coordinate,
                    // every length and the mesh cell in database units, and a rail made by clicking
                    // the pour was NAMED `rail at (30058230, 12324568) DBU` in the saved document.
                    // Only the live-artwork swap ever set this, and that swap needs a layout SESSION
                    // on the same `.clay`, which the ordinary open does not have.
                    View           = view,
                    Technology     = tech,
                    TechPath       = found.TechnologyPath,
                    DbuPerMicron   = view.DbuPerMicron,
                    ArtworkCellRef = found.ClayPath,
                    Pads           = resolvedPads.Pads,
                    NetPoints      = resolvedPads.NetPoints,
                    Nets           = resolvedPads.Nets,
                    NetOrigin      = resolvedPads.NetOrigin,
                    ReferenceNet   = _document.ReferenceNet,
                };
                break;
        }

        var library = RailArtwork.ResolvePartLibrary(
            _document, path, out string? libraryPath, out string? libraryError);
        if (libraryError is { Length: > 0 })
            notes.Add($"The part library '{libraryPath}' did not read: {libraryError}. Parts are "
                    + "reported from the rail's own rows, with no models attached.");
        else if (library is not null)
        {
            PartLibrary = library;
            PartLibraryPath = libraryPath;
        }

        return notes;
    }
}
