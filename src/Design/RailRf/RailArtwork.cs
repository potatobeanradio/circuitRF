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
            // ── THE DOCUMENT'S OWN READING, AND IT USED TO PASS NULL FOR ALL OF IT ────────────
            //
            // The origin, the units and the column names are three answers a HUMAN gave the import
            // dialog, and until 2026-09-22 none of them was written down. So a document imported
            // with all three answered, saved and opened again re-read the file with none — which
            // re-raised the origin refusal on a document that had already answered it, and on a
            // headerless file threw the user's column mapping away entirely. See
            // RailPlacementReading.
            var reading = document.Placement;
            var read = PlacementFile.ReadFile(
                path, dbuPerMicron, reading.Origin, reading.Units,
                delimiter: null, columns: reading.Columns);
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
    /// <param name="Nets">Every net name this board resolved, from all three claims together — what
    /// the pick list offers (R-ab2-4a). Sorted, distinct, ordinal.</param>
    /// <param name="NetOrigin">Which of the three claims contributed — what the strip says
    /// (R-ab2-4d).</param>
    /// <param name="Divergences">Where the board netlist and the artwork describe the same board
    /// differently (R-ab2-5). <b>Reported, never resolved</b>: the run proceeds on R-ab1-3a's
    /// precedence, because a stale export is an ordinary mid-design state and a tool that refused to
    /// solve on one would be a tool nobody runs mid-design.</param>
    /// <param name="Stamped">The copper partition this resolution named its pads against — empty
    /// where the board states no net on any shape. <b>Carried rather than rebuilt</b> (R-ab3-1a):
    /// the companion writers need the same answer for a VIA that the pads already took, and a
    /// second <c>CopperPieces.Build</c> over one board is a second partition that nothing
    /// compares. Its <c>Refusals</c> are also the writers' own refusal (R-ab3-2e) — a piece of
    /// copper carrying two names is a board nothing may be written for.</param>
    /// <param name="TurnedParts">The parts <see cref="Layout.Extraction.TurnedParts"/> read as placed at
    /// 180° to their schematic — their pads in <paramref name="Pads"/> already carry the reading.
    /// Read through <see cref="RailPadResolution.Turned"/>, which is never null.</param>
    /// <param name="Schematic">The schematic beside the artwork, as this resolution read it —
    /// carried for <see cref="Stamped"/>'s reason. A bill of materials wants each part's value,
    /// footprint and type off the same <c>.csch</c> the nets came from, and resolving it a second
    /// time is a second answer to "which schematic is this board's".</param>
    public sealed record RailPadResolution(
        System.Collections.Generic.IReadOnlyList<PlacedPin>      Pads,
        System.Collections.Generic.IReadOnlyList<PdnNetPoint> NetPoints,
        int                                                   FromBoardNetlist,
        int                                                   FromArtwork,
        System.Collections.Generic.IReadOnlyList<string>      Notes,
        System.Collections.Generic.IReadOnlyList<string>      Nets,
        PdnNetOrigin                                          NetOrigin,
        System.Collections.Generic.IReadOnlyList<PdnDivergence> Divergences,
        CopperPieces                                       Stamped,
        PdnSchematicNets                                      Schematic,
        System.Collections.Generic.IReadOnlyList<TurnedPart>? TurnedParts = null)
    {
        /// <summary>The two-terminal parts the copper reads as placed at 180° to their schematic —
        /// <see cref="Layout.Extraction.TurnedParts"/>. Never null.</summary>
        public System.Collections.Generic.IReadOnlyList<TurnedPart> Turned => TurnedParts ?? [];
    }

    /// <summary>
    /// The board's pads and their nets: the netlist's where it speaks, the artwork's everywhere
    /// else.
    /// </summary>
    /// <param name="view">The artwork as read, or null for a document that resolved none.</param>
    /// <param name="clayPath">Where it was read from — an instance's <c>CellRef</c> is relative to
    /// the layout folder holding it, and the SCHEMATIC beside it is resolved from here too
    /// (R-ab2-1a).</param>
    /// <param name="technology">The root stackup.</param>
    /// <param name="netlist">The board netlist, where one resolved.</param>
    /// <param name="portNamesOf">An override for the ports of the component a <c>SchematicId</c>
    /// names, in port order — R-ab1-4's join. Null takes the schematic's own answer, which is the
    /// ordinary path; a caller supplies this only to test the join against ports no schematic
    /// states.</param>
    /// <param name="shapes">The FLATTENED artwork, where the caller already has it — what the
    /// stamped-net partition is built over. A board whose parts are footprint cells keeps every land
    /// inside an instance, so partitioning the root's own shapes alone would leave a pad standing on
    /// nothing. Null falls back to <c>view.Shapes</c>, which is correct for a board with no
    /// instances and is the honest answer for one that has them and no flatten to hand.</param>
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
    /// <para><b>And the NET of an artwork pad is the schematic's binding, else the stamp</b> — the
    /// owner's rule of 2026-09-20, re-derived with a stamped fallback. Per PAD rather than per
    /// board, which is what makes a hand-placed instance beside schematic-owned ones take the
    /// stamped path: it carries no <c>SchematicId</c>, so nothing in the extraction is about it
    /// (R-ab2-1c).</para>
    /// </remarks>
    public static RailPadResolution PadsFor(
        LayoutView? view, string? clayPath, Technology? technology, BoardNetlist? netlist,
        Func<string, System.Collections.Generic.IReadOnlyList<string>>? portNamesOf = null,
        System.Collections.Generic.IReadOnlyList<LayoutShape>? shapes = null,
        LayoutUnit? displayUnit = null)
    {
        var notes = new System.Collections.Generic.List<string>();

        // Every sentence below names a place on the board, in the `.crail`'s own unit where the caller
        // has one (RailDocument.DisplayUnit) and in the layout's where it does not.
        RailLengthFormat? format = view is null ? null : RailLengthFormat.For(view, displayUnit);

        var fromNetlist = PdnBoardPads.PadsOf(netlist);
        var covered = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pad in fromNetlist)
            if (pad.Refdes is { Length: > 0 } r) covered.Add(r);

        // R-ab2-1. The schematic beside the artwork, not whatever workspace is open.
        var schematic = PdnSchematicNets.Resolve(clayPath);
        notes.AddRange(schematic.Notes);

        // R-ab2-2. The partition, over the FLATTENED copper, reading stated nets off the ROOT's own
        // shapes only — a land pattern is one cell shared by every placement of it, and a Net
        // stamped inside it would put thirteen capacitors on one net.
        //
        // Skipped outright where nothing was stamped, which is every board that has no stamps on it
        // — including every one that shipped before this brief. railRF only ever asks this partition
        // for a NAME, so with no name stated there is nothing to find, and the connectivity walk over
        // a whole flattened board is not a cost to pay for an answer that is known to be empty. (The
        // layout editor asks it a DIFFERENT question — "what is joined to this?" — and must not take
        // this short cut; see LayoutEditorViewModel.CopperPieces.)
        var stamped = view is null || !view.Shapes.Any(sh => sh.Net is { Length: > 0 })
            ? CopperPieces.Empty
            : CopperPieces.Build(shapes ?? view.Shapes, technology, view.Shapes, format);
        notes.AddRange(stamped.Refusals);

        var extents = new System.Collections.Generic.Dictionary<PlacedPin, long>();
        var origins = new System.Collections.Generic.List<PlacedPinOrigin>();
        IReadOnlyList<PlacedPin> artwork = view is null
            ? []
            : PlacedPins.Of(
                view, clayPath, technology,
                // R-lvs2-5c. railRF's own precedence rule, unchanged and now spelled out: the
                // schematic's binding where a schematic resolves, else the net stated on the copper.
                PinNaming.SchematicThenArtwork,
                portNamesOf ?? (id => schematic.For(id)?.PortNames ?? []),
                notes,
                id => schematic.For(id)?.Nets ?? [],
                stamped,
                extents,
                origins);

        // ── A TWO-TERMINAL PART AT 180° TO ITS SCHEMATIC (field report, 2026-09-22) ─────────────
        //
        // The schematic's binding above is by PIN NUMBER, and on a resistor, a capacitor or an
        // inductor the pin number is the one thing the board cannot show: an 0402 turned end for end
        // is the same picture. TurnedParts reads which way round each one sits from the copper — its
        // own header says why only those three kinds, and why only on strict evidence — and the
        // parts it turns are REPORTED, never absorbed, so the window can offer to turn them in the
        // layout. Before the divergence comparison, so a board netlist is compared against the
        // reading the run will actually use.
        IReadOnlyList<TurnedPart> turned = [];
        if (view is not null && technology is not null && schematic.Any
            && artwork.Count > 0 && origins.Count == artwork.Count)
        {
            // The kind by SchematicId — the same lookup LVS hands the same reader (brief LVS 16).
            var kindOf = TurnedParts.KindsFrom(view, schematic);

            if (TurnedParts.AnyCandidate(view, kindOf))
            {
                // The partition the stamps were read through where there is one — it is the same
                // copper, and a second connectivity walk of one board is a second answer nothing
                // compares. Built here only where the board states no net on any shape.
                var partition = stamped.Any ? stamped : CopperPieces.Build(shapes ?? view.Shapes, technology);
                var reading = TurnedParts.Read(artwork, origins, view, kindOf, partition);

                if (reading.Turned.Count > 0)
                {
                    for (int i = 0; i < artwork.Count; i++)
                        if (reading.Pads[i] != artwork[i] && extents.Remove(artwork[i], out long w))
                            extents[reading.Pads[i]] = w;

                    artwork = reading.Pads;
                    turned = reading.Turned;
                    notes.Add(TurnedParts.Sentence(turned));
                }
            }
        }

        // R-ab2-5. Computed BEFORE the precedence filter, which is the only reason the artwork's
        // reading of a part the netlist also names is worked out at all: R-ab1-3a discards those
        // pads and this comparison is what they are for. It is not LVS — it compares two files that
        // both claim to describe one board.
        // In the ARTWORK's own unit — these sentences name two places on a board and a distance
        // between them, and a reader has to be able to hold a ruler against them.
        var divergences = PdnBoardDivergence.Compare(
            fromNetlist, artwork, extents, format);
        foreach (var d in divergences) notes.Add(d.Sentence);

        // Each artwork pad's LAND layer rides on the pad itself (R-rail34-1), so a refdes anchor
        // seeds its land and the net points below say the same — after the divergence comparison,
        // which pairs netlist pads with these by value and a netlist pad states no layer. `artwork`
        // and `origins` are one-to-one by construction: a turned part's pads moved, and their lands'
        // layers did not.
        //
        // A footprint placed MIRRORED is soldered to the bottom (owner, 2026-09-24): its lands are
        // turned over to the far outer copper, as the board exporters already turn them. Without it
        // a bottom-side part's pads were looked for on top copper and the part was left out.
        if (origins.Count == artwork.Count)
        {
            var outer = RailPartSides.OuterCopper(technology);
            artwork = [.. artwork.Select((p, i) =>
            {
                var land = origins[i].Layer;
                if (outer is { } o && view is not null
                    && origins[i].Instance >= 0 && origins[i].Instance < view.Instances.Count
                    && view.Instances[origins[i].Instance].MirrorX)
                    land = RailPartSides.Flip(land, o);
                return p with { Layer = land };
            })];
        }

        // And a netlist pad takes its land from the ARTWORK's pad for the same part: the file is the
        // evidence for what a pad is and what it is called, the artwork for which copper it is on,
        // and a pad with no land attaches to every rail layer under its XY — which on a rail routed
        // on two layers ties a top pad to the far layer's copper beneath it. By refdes and pin, and
        // by refdes alone where the two spell pins differently but every land of the part is on one
        // layer (every surface-mount part). A pad that states its own land keeps it.
        fromNetlist = [.. fromNetlist.Select(p => p.Layer is null && LandFromArtwork(p, artwork) is { } land
            ? p with { Layer = land }
            : p)];

        var fromArtwork = artwork
            .Where(p => p.Refdes is not { Length: > 0 } r || !covered.Contains(r))
            .ToList();

        var pads = new System.Collections.Generic.List<PlacedPin>(fromNetlist.Count + fromArtwork.Count);
        pads.AddRange(fromNetlist);
        pads.AddRange(fromArtwork);

        // De-duplicated because both sources legitimately describe the same stitching via: the
        // netlist carries a record for it and the artwork carries the ViaShape itself. Two identical
        // seeds are redundant rather than wrong, and dropping the duplicate keeps the count on the
        // report equal to the number of distinct places a net was stated.
        var points = new System.Collections.Generic.List<PdnNetPoint>();
        var seen = new System.Collections.Generic.HashSet<PdnNetPoint>();
        foreach (var pt in PdnBoardPads.NetPointsOf(netlist)) if (seen.Add(pt)) points.Add(pt);
        // Each artwork pad's land layer is on the pad (above), so the walk seeds the land and not a
        // plane under it (PdnNetPoint.Layer).
        if (view is not null)
            foreach (var pt in PlacedPins.NetPointsOf(view, fromArtwork, stamped))
                if (seen.Add(pt)) points.Add(pt);

        // ── R-ab2-4a: the RESOLVED net set, which is what the pick list offers ──────────────────
        //
        // Not BoardNetlist.Nets alone. A drawn board with a schematic behind it has always had net
        // names and the window has never offered them — and then told the user, in a sentence that
        // nothing fails when it is wrong, that no board netlist had named any and to click the pour
        // instead.
        var nets = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
        var origin = PdnNetOrigin.None;

        if (netlist is { Refusal: null } n && n.Nets.Count > 0)
        {
            origin |= PdnNetOrigin.BoardNetlist;
            foreach (string net in n.Nets) nets.Add(net);
        }
        if (schematic.Nets.Count > 0)
        {
            origin |= PdnNetOrigin.Schematic;
            foreach (string net in schematic.Nets) nets.Add(net);
        }
        if (stamped.Nets.Count > 0)
        {
            origin |= PdnNetOrigin.Artwork;
            foreach (string net in stamped.Nets) nets.Add(net);
        }

        return new RailPadResolution(
            pads, points, fromNetlist.Count, fromArtwork.Count, notes, [.. nets], origin, divergences,
            stamped, schematic, turned);
    }

    /// <summary>The land layer the artwork gives <paramref name="pad"/>'s part at that pin, or at
    /// every pin where the pin names do not match and the part's lands are all on one layer.</summary>
    private static LayerKey? LandFromArtwork(PlacedPin pad, IReadOnlyList<PlacedPin> artwork)
    {
        if (pad.Refdes is not { Length: > 0 } refdes) return null;

        var mine = artwork
            .Where(a => a.Layer is not null && string.Equals(a.Refdes, refdes, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mine.Count == 0) return null;

        if (mine.FirstOrDefault(a => string.Equals(a.Pin, pad.Pin, StringComparison.OrdinalIgnoreCase))
            is { Layer: { } byPin })
            return byPin;

        var layers = mine.Select(a => a.Layer!.Value).Distinct().ToList();
        return layers.Count == 1 ? layers[0] : null;
    }

    // ── THE WRITING HALF OF THE SAME WALK (owner, 2026-09-21) ────────────────────────────────────
    //
    // Every reference a `.crail` carries is documented as document-relative — five times, on
    // `RailDocument`'s own fields, "so an archived workspace still resolves". Nothing wrote them
    // that way. The window set `ArtworkCellRef`, `BoardNetlistRef` and `PlacementRef` to the
    // ABSOLUTE path it had just opened or imported, and `SaveToFile` wrote that path out; only
    // `PartLibraryRef` was ever relativized, by the one site that happened to do it by hand.
    //
    // A document whose references are absolute resolves perfectly on the machine that wrote it and
    // on no other, which is why this survived: it fails for the recipient of a workspace, never for
    // its author. Found on a field report's own workspace, whose `.crail` names
    // `C:\Users\…\layout\board.clay`; opened anywhere else, RefPath.Resolve treats that string as a
    // relative name (it is not rooted off Windows), lands on a path that has never existed, and the
    // window says the artwork was not found — with the file sitting right beside the document.
    //
    // It is a WRITE-side rule rather than a read-side one on purpose. Reading stays tolerant —
    // RefPath.Resolve already accepts a rooted ref and wins over the base, so every `.crail` written
    // before this goes on opening on its author's machine — and what changes is that saving one puts
    // it right.

    /// <summary>
    /// Rewrites every reference this document carries so it is relative to where the document is
    /// about to be WRITTEN — the contract each of those fields states, applied at the one moment the
    /// destination is known.
    /// </summary>
    /// <param name="document">The document. Its reference fields are rewritten in place.</param>
    /// <param name="fromDocumentPath">Where the document's references are currently relative to —
    /// the path it was last read from or written to, or null for one that has never been either.
    /// <b>A relative reference with no base is left exactly as it is</b>: nothing here can know what
    /// it was relative to, and a guess would repoint it at a file nobody named.</param>
    /// <param name="toDocumentPath">Where it is being written.</param>
    /// <remarks>
    /// <b>Save and Save as… are one operation here</b>, which is what makes moving a document keep
    /// its board: an absolute reference needs no base at all, and a reference that was already
    /// relative is resolved against the OLD document's folder before being re-stated against the new
    /// one. Writing a `.crail` into another folder without that step would leave every reference
    /// pointing one directory hop away from the file it names.
    ///
    /// <para><b>Stored form, not native form</b> — <c>RefPath.ToStored</c>, so a Windows-authored
    /// document opens on macOS and Linux. A reference on another volume has no relative spelling and
    /// <c>Path.GetRelativePath</c> hands back the absolute path unchanged, which is the honest
    /// answer rather than a failure.</para>
    /// </remarks>
    public static void RebaseReferences(
        RailDocument document, string? fromDocumentPath, string toDocumentPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(toDocumentPath);

        string? fromDir = fromDocumentPath is { Length: > 0 } f
            ? Path.GetDirectoryName(Path.GetFullPath(f))
            : null;
        string toDir = Path.GetDirectoryName(Path.GetFullPath(toDocumentPath)) ?? "";

        document.ArtworkCellRef  = Rebased(document.ArtworkCellRef,  fromDir, toDir);
        document.TechnologyRef   = Rebased(document.TechnologyRef,   fromDir, toDir);
        document.PartLibraryRef  = Rebased(document.PartLibraryRef,  fromDir, toDir);
        document.BoardNetlistRef = Rebased(document.BoardNetlistRef, fromDir, toDir);
        document.PlacementRef    = Rebased(document.PlacementRef,    fromDir, toDir);
    }

    /// <summary>One reference, re-stated against <paramref name="toDir"/>.</summary>
    private static string? Rebased(string? reference, string? fromDir, string toDir)
    {
        if (reference is not { Length: > 0 } r) return reference;

        // A rooted reference carries its own base and is the whole point of this; a relative one
        // needs the document it was relative to, and with none it is left alone rather than guessed.
        string native = Core.RefPath.ToNative(r);
        if (!Path.IsPathRooted(native))
        {
            if (fromDir is not { Length: > 0 }) return reference;
            native = Path.Combine(fromDir, native);
        }

        try
        {
            return Core.RefPath.ToStored(Path.GetRelativePath(toDir, Path.GetFullPath(native)));
        }
        catch (ArgumentException)       { return reference; }
        catch (PathTooLongException)    { return reference; }
        catch (NotSupportedException)   { return reference; }
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
