// What the window's Export and Report buttons hand to a writer (owner, 2026-09-19).
//
// ── WHY THERE IS NOTHING HERE BUT ASSEMBLY ──────────────────────────────────────────────────────
//
// The buttons were disabled with a tooltip reading "Not wired yet", and the owner's question was
// simply "why not?". The honest answer was that every writer lived inside `src/Cli/Rail.cs`, which
// `src/Ui` may not reference — so wiring the button meant either moving the writers below the
// firewall or writing a second set. `RailExport` in `src/Design` is the first of those, and its
// header records why the second was never an option: two CSV writers agree today, drift the first
// time either is touched, and both keep producing a plausible file while they disagree.
//
// So what is in this file is the four things the WINDOW knows and the verb has to be told — the
// document, which rails were solved, which library resolved and where the artwork is — turned into
// the two arguments every writer takes. No format is spelled here and no sentence is composed here.

using System.Collections.Generic;
using System.IO;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// True once there is something to write. <b>The button is disabled rather than hidden</b>,
    /// with the reason on its tooltip, which is this window's rule everywhere else
    /// (<c>RunBlockedReason</c>) — a control that vanishes is a feature the user concludes does not
    /// exist.
    /// </summary>
    public bool CanExport => Current is not null;

    /// <summary>Why Export is refused, or empty — the disabled button's own tooltip line.</summary>
    public string ExportBlockedReason =>
        CanExport ? "" : "Nothing has been solved yet, so there are no numbers to write. Run the rail first.";

    /// <summary>The results the writers are about: every rail, in solve order.</summary>
    public IReadOnlyList<RailDcResult> ExportResults => Current?.Result.Rails ?? [];

    /// <summary>
    /// R-rail10-5's banner for what is currently on screen, or null with nothing solved.
    /// </summary>
    /// <remarks>
    /// <b>The model kind comes from the RESULT and not from a button</b> — <c>RailResultView</c>
    /// carries the two as one value for exactly this reason, and a report whose banner said
    /// "Accuracy" over the fast numbers is the failure §2.9 exists to prevent.
    /// </remarks>
    public RailProvenance? ExportProvenance() =>
        Current is { } current
            ? RailProvenance.Of(
                current.Kind,
                _document,
                _document.Rails,
                current.Result.Rails,
                PartLibrary,
                PartLibraryPath,
                Board?.ArtworkCellRef ?? "(none)",
                Board is { } b ? b.Technology.Name : "(none)")
            : null;

    /// <summary>The folder a report page resolves a placed cell's artwork against — the board's
    /// own, exactly as the verb passes the resolved board's.</summary>
    public string ExportBaseDir =>
        Board?.ArtworkCellRef is { Length: > 0 } clay ? Path.GetDirectoryName(clay) ?? "" : "";

    /// <summary>The file name a save dialog opens on, with no extension — the storage provider
    /// appends the chosen one, and supplying both spells it twice (the Match Designer's own
    /// finding).</summary>
    public string ExportSuggestedName =>
        _documentPath is { Length: > 0 } p ? Path.GetFileNameWithoutExtension(p)
        : _document.Name is { Length: > 0 } n ? n
        : "rail";
}
