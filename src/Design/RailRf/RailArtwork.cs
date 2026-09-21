// The walks that turn a `.crail`'s own references into files — the artwork, the stackup and the part
// library.
//
// ── WHY THIS IS ONE FUNCTION AND NOT TWO ────────────────────────────────────────────────────────
//
// `circuitrf rail` resolved all three from the document and the WINDOW resolved none of them: opening
// a `.crail` from the project tree constructed the view model and stopped, so the command line
// answered a file the window had to be pointed at, and a document whose board is on disk and named in
// its own text opened with no board in it. That gap was recorded in `src/Ui/RESOLVED.md` and found
// again from the outside, on the shipped Power Rail example, whose README says the window opens with
// the board already loaded (owner, 2026-09-18).
//
// The fix is not "make the window do what the verb does" — that is the second copy the CLI chapter's
// own rule is against. It is to put the WALKS here, where neither surface owns them, and to leave
// each caller its own reporting: the verb prints to stderr and exits, the window posts to Messages
// and shows a refusal in its strip. What must not differ is which file each reference lands on.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>Why a <c>.crail</c>'s artwork did not resolve, or that it did.</summary>
public enum RailArtworkOutcome
{
    /// <summary>The artwork, the layout view and the technology are all in hand.</summary>
    Resolved,

    /// <summary>The document names no artwork at all.</summary>
    NoArtworkRef,

    /// <summary>The reference resolved to a path holding no layout view.</summary>
    NotFound,

    /// <summary>The layout view is there and did not read.</summary>
    Unreadable,

    /// <summary>The artwork read and no technology resolved for it.</summary>
    NoTechnology,
}

/// <summary>
/// What one resolution produced. <see cref="Outcome"/> is the only thing a caller has to branch on;
/// the rest is what it found on the way, and it is populated as far as it got so a refusal can name
/// the file it was looking at.
/// </summary>
/// <param name="Outcome">Resolved, or which walk stopped.</param>
/// <param name="ClayPath">The layout view the artwork reference landed on, where one was found.</param>
/// <param name="View">The artwork, as the layout editor reads it.</param>
/// <param name="Technology">The stackup.</param>
/// <param name="TechnologyPath">Where the stackup came from.</param>
/// <param name="TechnologySource">Which of the two technology walks produced it.</param>
/// <param name="WorkspaceCwsPath">The <c>.cws</c> the technology walk landed on, or null.</param>
/// <param name="Diagnostics">The technology resolution's own warnings. Returned, never posted.</param>
/// <param name="Detail">The message from an <see cref="RailArtworkOutcome.Unreadable"/>, or the
/// reference as written for the other refusals.</param>
public sealed record RailArtworkResolution(
    RailArtworkOutcome            Outcome,
    string?                       ClayPath,
    LayoutView?                   View,
    Technology?                   Technology,
    string?                       TechnologyPath,
    TechResolutionSource          TechnologySource,
    string?                       WorkspaceCwsPath,
    System.Collections.Generic.IReadOnlyList<string> Diagnostics,
    string?                       Detail);

/// <summary>
/// The document-relative walks a <c>.crail</c>'s references take. Framework-free, side-effect-free,
/// and it posts nothing — see this file's header for why it exists at all.
/// </summary>
public static class RailArtwork
{
    /// <summary>
    /// Resolves <see cref="RailDocument.ArtworkCellRef"/> and the stackup that prices it.
    /// </summary>
    /// <param name="document">The document. Its references are relative to
    /// <paramref name="documentPath"/>, which is the rule every circuitRF reference follows.</param>
    /// <param name="documentPath">Where the <c>.crail</c> is.</param>
    /// <param name="clayHint">A layout the caller already settled on, which skips the artwork walk
    /// entirely — the CLI's <c>--board</c>. Null on the ordinary path.</param>
    /// <param name="cache">The technology cache to load through.</param>
    /// <remarks>
    /// <b>The technology walk starts from a different file than the artwork walk</b>, and that is
    /// deliberate: a <c>TechnologyRef</c> on the <c>.crail</c> is relative to the <c>.crail</c>, and
    /// with none stated the layout's own reference and its own ancestor workspace are what the layout
    /// editor would use. Both are reported, because which one produced the answer is the part a
    /// caller cannot otherwise see.
    /// </remarks>
    public static RailArtworkResolution Resolve(
        RailDocument document, string documentPath, string? clayHint, TechnologyCache cache)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentPath);
        ArgumentNullException.ThrowIfNull(cache);

        string? clay = clayHint;

        if (clay is null)
        {
            if (document.ArtworkCellRef is not { Length: > 0 } artwork)
                return Stopped(RailArtworkOutcome.NoArtworkRef, null, null);

            clay = Core.RefPath.Resolve(Path.GetDirectoryName(Path.GetFullPath(documentPath))!, artwork);

            // The reference may name a CELL folder rather than the view file inside it, which is what
            // "a reference to a CELL in the workspace" means in RailDocument's own words.
            if (Directory.Exists(clay)) clay = LayoutOf(clay);

            if (clay is null || !File.Exists(clay))
                return Stopped(RailArtworkOutcome.NotFound, clay, artwork);
        }

        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception ex) { return Stopped(RailArtworkOutcome.Unreadable, clay, ex.Message); }

        var (resolution, cws) = document.TechnologyRef is { Length: > 0 } techRef
            ? TechnologyResolver.ResolveForDocument(techRef, documentPath, null, cache)
            : TechnologyResolver.ResolveForDocument(view.TechRef, clay, null, cache);

        // R-rail10-3's shape: railRF prices copper against a stackup, so a board with no technology is
        // not a degraded picture the way an orphan `.clay` is for `render` — it is an answer with no
        // thicknesses and no conductivities in it.
        return new RailArtworkResolution(
            resolution.Tech is null ? RailArtworkOutcome.NoTechnology : RailArtworkOutcome.Resolved,
            clay, view, resolution.Tech, resolution.ResolvedPath, resolution.Source, cws,
            resolution.Diagnostics, null);
    }

    /// <summary>
    /// Reads the part library <see cref="RailDocument.PartLibraryRef"/> names, or null where it names
    /// none. Document-relative, like every other reference a <c>.crail</c> carries.
    /// </summary>
    /// <param name="path">Where the reference landed, or null where the document names none. Set even
    /// when the read failed, because a refusal that cannot name the file it tried is not actionable.</param>
    /// <param name="error">The reader's own message, where it named a library and did not read it.</param>
    public static PartLibrary? ResolvePartLibrary(
        RailDocument document, string documentPath, out string? path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentPath);

        path = null;
        error = null;
        if (document.PartLibraryRef is not { Length: > 0 } r) return null;

        path = Core.RefPath.Resolve(Path.GetDirectoryName(Path.GetFullPath(documentPath))!, r);
        try { return PartLibraryIo.LoadFromFile(path); }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>
    /// Reads the board netlist <see cref="RailDocument.BoardNetlistRef"/> names, or null where it
    /// names none. Document-relative, like every other reference a <c>.crail</c> carries.
    /// </summary>
    /// <remarks>
    /// <b>It needs the artwork's DBU resolution</b>, which is the one argument the part library's
    /// walk does not take: this format carries its own units AND its own resolution, and the two
    /// inch resolutions in circulation differ by a factor of ten (R-gi5-10). <c>BoardNetlistFile</c>
    /// cross-checks its reading against the artwork's extent, and it can only do that in the
    /// artwork's own units — so resolve the artwork FIRST and pass its <c>DbuPerMicron</c>.
    /// </remarks>
    /// <param name="dbuPerMicron">The artwork's own resolution.</param>
    /// <param name="path">Where the reference landed, or null where the document names none. Set even
    /// when the read failed, because a refusal that cannot name the file it tried is not actionable.</param>
    /// <param name="error">The reader's own message, where it named a netlist and did not read it.</param>
    public static BoardNetlist? ResolveBoardNetlist(
        RailDocument document, string documentPath, int dbuPerMicron,
        out string? path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentPath);

        path = null;
        error = null;
        if (document.BoardNetlistRef is not { Length: > 0 } r) return null;

        path = Core.RefPath.Resolve(Path.GetDirectoryName(Path.GetFullPath(documentPath))!, r);
        try
        {
            var read = BoardNetlistFile.ReadFile(path, dbuPerMicron);
            // A refusal is carried on the RESULT rather than thrown, and it must not be swallowed
            // here: a netlist that did not read leaves every refdes unresolvable, and the report
            // that follows would otherwise look exactly like one for a board that never had a
            // netlist at all.
            if (read is { Refusal: { Length: > 0 } why }) { error = why; return null; }
            return read;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>
    /// Reads the placement file <see cref="RailDocument.PlacementRef"/> names, or null where it names
    /// none. Document-relative.
    /// </summary>
    /// <remarks>
    /// <b>An unstated origin is carried, not defaulted.</b> <c>PlacementFile</c> reports
    /// <c>PlacementOriginEvidence.Unstated</c> where the file declares no origin, and the caller is
    /// what asks — the import dialog asks a user, and a headless caller reports it. Three quarters of
    /// a millimetre on an 0402 is the difference between landing on the part's own pad and on its
    /// neighbour's, so nothing here picks one.
    /// </remarks>
    public static PlacementTable? ResolvePlacement(
        RailDocument document, string documentPath, int dbuPerMicron,
        out string? path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentPath);

        path = null;
        error = null;
        if (document.PlacementRef is not { Length: > 0 } r) return null;

        path = Core.RefPath.Resolve(Path.GetDirectoryName(Path.GetFullPath(documentPath))!, r);
        try
        {
            var read = PlacementFile.ReadFile(path, dbuPerMicron, null);
            if (read is { Refusal: { Length: > 0 } why }) { error = why; return null; }
            return read;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>
    /// The artwork the EXTRACTION reads: <paramref name="view"/>'s own shapes plus every shape its
    /// INSTANCES contribute, in the root's coordinate frame.
    /// </summary>
    /// <remarks>
    /// <b>railRF used to read <c>view.Shapes</c> and nothing else</b>, which was harmless only for as
    /// long as no board it was pointed at held an instance. A board whose parts are footprint CELLS —
    /// which is what a board drawn in circuitRF now is — has its every land inside an instance, so the
    /// unflattened read is a board with the rail's copper on it and not one capacitor land: the run
    /// completes, the answer is wrong, and nothing says so. It is flattened through
    /// <see cref="LayoutDesignFlatten"/>, which is the same flatten Gerber, DRC and
    /// <c>circuitrf check</c> already use, rather than a second walk of the instance tree.
    ///
    /// <para><b>The VIEW is left alone.</b> The picture railRF draws is the hierarchy — that is where
    /// a reference designator lives (brief-footprint-4b) and it is a LIVE view of a <c>.clay</c>
    /// somebody may have open. This returns a separate list for
    /// <c>RailBoardInputs.Shapes</c>, whose own contract already says "flattened to shapes in
    /// DBU".</para>
    /// </remarks>
    /// <param name="view">The artwork as read.</param>
    /// <param name="clayPath">Where it was read from — an instance's <c>CellRef</c> is relative to the
    /// layout folder holding it, so with no path nothing can be resolved and the root's own shapes are
    /// returned unchanged.</param>
    /// <param name="technology">The root stackup, which is what a land pattern's layer keys are
    /// reconciled against.</param>
    /// <param name="notes">Appended with one sentence per instance that did not resolve. An
    /// unresolved instance contributes NO geometry, and a board silently missing a part's lands is
    /// exactly the failure this function exists to prevent.</param>
    public static IReadOnlyList<LayoutShape> FlattenedShapes(
        LayoutView view, string? clayPath, Technology? technology,
        System.Collections.Generic.List<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Instances.Count == 0 || clayPath is not { Length: > 0 }) return view.Shapes;

        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath)) ?? "";
        string cellDir = Path.GetDirectoryName(layoutDir) ?? layoutDir;

        var flat = LayoutDesignFlatten.Flatten(view, cellDir, technology, null, null);

        if (flat.ExceedsCeiling)
        {
            notes?.Add(
                $"'{Path.GetFileName(clayPath)}' holds more instance geometry than the flatten ceiling " +
                "allows, so its parts' lands were not read. The rail's own copper was.");
            return view.Shapes;
        }

        foreach (string u in flat.UnresolvedInstances) notes?.Add(u);
        foreach (var pendingCell in flat.PendingCrossTechMappings.Keys)
            notes?.Add($"The cell at '{pendingCell}' is drawn on another technology and its layers " +
                       "need reconciling, so it contributed no geometry to this board.");

        return flat.Shapes;
    }

    // ── R-ab1-5: ONE funnel for the pads ─────────────────────────────────────────────────────────
    //
    // Five places construct the board inputs and four of them took pads from PdnBoardPads; the bare
    // `.clay` one took NONE AT ALL, which is the whole reported gap in one line. That is
    // RebuildAvailableNets' scar verbatim — a derived list only one of two writers refreshed was
    // wrong on the other path, silently — so the resolution goes HERE, beside FlattenedShapes, where
    // neither surface owns it, and every site calls it. The same move this file itself was created
    // by, for the same reason.

    /// <summary>
    /// What one board's pads are, and where each of them came from.
    /// </summary>
    /// <param name="Pads">The board netlist's, plus the artwork's for every part the netlist did not
    /// name.</param>
    /// <param name="NetPoints">What the connectivity walk can be seeded from, de-duplicated across
    /// the two sources — an identical point from both is one seed, not two.</param>
    /// <param name="FromBoardNetlist">How many pads the netlist stated.</param>
    /// <param name="FromArtwork">How many were computed off the artwork.</param>
    /// <param name="Notes">What could not be read. <b>Returned, never posted</b>.</param>
    public sealed record RailPadResolution(
        System.Collections.Generic.IReadOnlyList<PdnPad>      Pads,
        System.Collections.Generic.IReadOnlyList<PdnNetPoint> NetPoints,
        int                                                   FromBoardNetlist,
        int                                                   FromArtwork,
        System.Collections.Generic.IReadOnlyList<string>      Notes);

    /// <summary>
    /// The board's pads: the netlist's where it speaks, the artwork's everywhere else.
    /// </summary>
    /// <param name="view">The artwork as read, or null for a document that resolved none.</param>
    /// <param name="clayPath">Where it was read from — an instance's <c>CellRef</c> is relative to
    /// the layout folder holding it.</param>
    /// <param name="technology">The root stackup.</param>
    /// <param name="netlist">The board netlist, where one resolved.</param>
    /// <param name="portNamesOf">The ports of the component a <c>SchematicId</c> names, in port
    /// order — R-ab1-4's join. Null until brief 2 supplies it.</param>
    /// <remarks>
    /// <b>The board netlist wins, PER REFDES</b> (R-ab1-3a, R-ab1-3b). A netlist is written by the
    /// tool that made the board and is evidence about it; artwork-derived pads are circuitRF's own
    /// reading of the same board, so where the netlist speaks it is the statement of record. Per
    /// refdes rather than per board, because whole-file precedence would throw away a correct pad
    /// for one part because a DIFFERENT part was missing from a different file — and the
    /// two-missing-parts case is the ordinary one on a board somebody edited after exporting.
    ///
    /// <para><b>A refused netlist contributes nothing</b> (R-ab1-3c) and the artwork then supplies
    /// everything: <c>BoardNetlist.Refusal</c> non-null means nothing was read and nothing may be
    /// used, which is the contract that type states.</para>
    ///
    /// <para><b>Where both name a refdes and they disagree about position, this takes the netlist
    /// and says nothing</b> (R-ab1-3d). Reporting that divergence is brief 2's; it is not LVS and
    /// nothing here ranks one reading against the other.</para>
    /// </remarks>
    public static RailPadResolution PadsFor(
        LayoutView? view, string? clayPath, Technology? technology, BoardNetlist? netlist,
        Func<string, System.Collections.Generic.IReadOnlyList<string>>? portNamesOf = null)
    {
        var notes = new System.Collections.Generic.List<string>();

        var fromNetlist = PdnBoardPads.PadsOf(netlist);
        var covered = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pad in fromNetlist)
            if (pad.Refdes is { Length: > 0 } r) covered.Add(r);

        var fromArtwork = view is null
            ? []
            : PdnLayoutPads.PadsOf(view, clayPath, technology, portNamesOf, notes)
                           .Where(p => p.Refdes is not { Length: > 0 } r || !covered.Contains(r))
                           .ToList();

        var pads = new System.Collections.Generic.List<PdnPad>(fromNetlist.Count + fromArtwork.Count);
        pads.AddRange(fromNetlist);
        pads.AddRange(fromArtwork);

        // De-duplicated because both sources legitimately describe the same stitching via: the
        // netlist carries a record for it and the artwork carries the ViaShape itself. Two identical
        // seeds are redundant rather than wrong, and dropping the duplicate keeps the count on the
        // report equal to the number of distinct places a net was stated.
        var points = new System.Collections.Generic.List<PdnNetPoint>();
        var seen = new System.Collections.Generic.HashSet<PdnNetPoint>();
        foreach (var pt in PdnBoardPads.NetPointsOf(netlist)) if (seen.Add(pt)) points.Add(pt);
        if (view is not null)
            foreach (var pt in PdnLayoutPads.NetPointsOf(view, fromArtwork)) if (seen.Add(pt)) points.Add(pt);

        return new RailPadResolution(pads, points, fromNetlist.Count, fromArtwork.Count, notes);
    }

    /// <summary>The primary layout view of a cell folder, or null where it holds none.</summary>
    public static string? LayoutOf(string cellDir)
    {
        if (!Directory.Exists(cellDir)) return null;
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        return primary.ResolvedName is { Length: > 0 } name
            ? Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name)
            : null;
    }

    private static RailArtworkResolution Stopped(RailArtworkOutcome outcome, string? clay, string? detail) =>
        new(outcome, clay, null, null, null, TechResolutionSource.None, null, [], detail);
}
