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
                Board = new RailBoardInputs
                {
                    Shapes         = view.Shapes,
                    Technology     = tech,
                    DbuPerMicron   = view.DbuPerMicron,
                    ArtworkCellRef = found.ClayPath,
                };
                break;
        }

        var library = RailArtwork.ResolvePartLibrary(
            _document, path, out string? libraryPath, out string? libraryError);
        if (libraryError is { Length: > 0 })
            notes.Add($"The part library '{libraryPath}' did not read: {libraryError}. Parts are "
                    + "reported from the rail's own rows, with no models attached.");
        else if (library is not null)
            PartLibrary = library;

        return notes;
    }
}
