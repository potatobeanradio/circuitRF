// ONE projection, three tables that cannot disagree — brief-authored-board-3-companion-writers.md
// R-ab3-2c, whose reason is footprint 5 R-fp5-2's governing one: THEY HAVE TO AGREE, AND
// HAND-EDITING THREE FILES IS HOW THAT GOES WRONG SILENTLY.
//
// ── WHY THERE IS A FOURTH FILE BESIDE THE THREE WRITERS ────────────────────────────────────────
//
// Each writer takes what briefs 1 and 2 produce and serialises it. Something has to PRODUCE it
// once, and it cannot be any of the three: a `netlist` invocation writing all three tables would
// otherwise project the board once per table, and two projections of one board are exactly the
// drift this series exists to prevent. It also cannot be the CLI verb, because the layout editor's
// File ▸ Export rows call the same functions and byte identity between the two is the gate
// (R-ab3-3b) — that test exists because the two drifting is invisible.
//
// ── IT IS NOT A RAILRF INPUT PATH ──────────────────────────────────────────────────────────────
//
// Overview §0, restated by this brief's own scope: a user analysing their own board never needs
// these files, and if anything in railRF starts requiring one, briefs 1 and 2 were implemented
// wrongly. What is here buys a board that can LEAVE — to a fab, to an assembly house, to another
// tool — and retires the script that writes the one shipped example.
//
// ── A PARTIAL TABLE IS NEVER WRITTEN (R-ab3-2e) ────────────────────────────────────────────────
//
// `BoardNetlist` and `PlacementTable` both state, on the way IN, that a non-null refusal means
// nothing was read and nothing may be used. That contract applies symmetrically on the way out: a
// half-written `.ipc` is worse than none, because it reads as a complete statement about a board
// and is one about part of it. So the refusal is computed BEFORE any byte is written, and a run
// that refuses leaves the file system untouched.

using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>
/// One board, projected once: its pads, its vias, its placements and its parts.
/// </summary>
/// <param name="Refusal">Non-null means NOTHING MAY BE WRITTEN — the writers' half of the contract
/// the readers state. The three tables are empty when it is set.</param>
/// <param name="Notes">What could not be read, and what was thin. <b>Returned, never posted</b> —
/// this project's own rule.</param>
/// <param name="HasSchematic">Whether a schematic resolved beside the artwork. False is the thin
/// case (R-ab3-2d): three tables are still written, with no net names and no values in them, and
/// the caller says so before it writes (R-ab3-3c).</param>
public sealed record BoardProjection(
    string ClayPath,
    string BoardName,
    int DbuPerMicron,
    IReadOnlyList<PlacedPin> Pads,
    IReadOnlyList<ViaShape> Vias,
    IReadOnlyList<PlacementEntry> Placements,
    IReadOnlyList<BomEntry> Parts,
    Technology? Technology,
    CopperPieces Stamped,
    bool HasSchematic,
    string? Refusal,
    IReadOnlyList<string> Notes)
{
    /// <summary>Whether any pad came out carrying a net name. False is what R-ab3-3c's dialog line
    /// is about — a placement file with no nets in it is a perfectly useful placement file, and a
    /// netlist with none is a thin one rather than a broken one.</summary>
    public bool AnyNetNamed
    {
        get
        {
            foreach (var pad in Pads) if (pad.Net is { Length: > 0 }) return true;
            foreach (var via in Vias) if (via.Net is { Length: > 0 }) return true;
            return false;
        }
    }

    /// <summary>The sentence a caller prints before it writes, or empty where there is nothing to
    /// warn about. <b>Said once, here</b> — the verb's stderr line and the dialog's own are the
    /// same statement about the same board, and two spellings of it is the divergence nobody
    /// notices until they are compared (<c>PlacedPinSummary</c>'s reason, one table along).</summary>
    public string ThinnessSummary
    {
        get
        {
            if (Refusal is { Length: > 0 }) return "";
            if (!HasSchematic && !AnyNetNamed)
                return "This board resolves no schematic and states no net on its copper, so the " +
                       "netlist will carry no net names and the bill of materials no values — only " +
                       "the reference designator and the footprint each placement carries.";
            if (!HasSchematic)
                return "This board resolves no schematic, so the bill of materials will carry no " +
                       "values: only the reference designator and the footprint each placement " +
                       "carries. Its net names come from the copper's own stamps.";
            if (!AnyNetNamed)
                return "No pad on this board resolved a net name, so the netlist will carry none.";
            return "";
        }
    }
}

public static class BoardCompanions
{
    /// <summary>
    /// Projects one board. <b>Every fact comes from briefs 1 and 2</b> — <c>RailArtwork.PadsFor</c>
    /// for the pads and their nets, <c>LayoutView.Instances</c> for the placements, and the
    /// schematic that same call already resolved for the parts.
    /// </summary>
    /// <param name="view">The artwork as read.</param>
    /// <param name="clayPath">Where it was read from. Every reference on it — the footprint cells,
    /// the schematic beside it — is relative to this, which is why a projection of an unsaved board
    /// is not a thing that can be asked for.</param>
    /// <param name="tech">The root stackup. Null is allowed and costs the via access codes, which
    /// only a stackup can state.</param>
    public static BoardProjection Project(LayoutView view, string clayPath, Technology? tech)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(clayPath);

        string full = Path.GetFullPath(clayPath);
        string layoutDir = Path.GetDirectoryName(full) ?? "";
        string board = Path.GetFileNameWithoutExtension(full);
        var notes = new List<string>();

        BoardProjection Refuse(string sentence) => new(
            full, board, view.DbuPerMicron, [], [], [], [], tech, CopperPieces.Empty, false,
            sentence, notes);

        // R-ab3-2e, first refusal. LayoutDesignFlatten's own predicate, not a second one: a board
        // whose geometry was truncated must not come back with a confident netlist over it, and the
        // pad projection already refuses it for exactly that reason (R-ab1-5d).
        if (LayoutDesignFlatten.ExceedsCeiling(view, layoutDir))
            return Refuse(
                $"'{Path.GetFileName(full)}' holds more instance geometry than the flatten ceiling " +
                "allows, so its parts' pads could not be read and nothing was written. A netlist " +
                "over geometry the run never saw is worse than no netlist.");

        var shapes = RailArtwork.FlattenedShapes(view, full, tech, notes);
        var resolved = RailArtwork.PadsFor(view, full, tech, null, null, shapes);
        notes.AddRange(resolved.Notes);

        // R-ab3-2e, second refusal. A connected piece of copper carrying two different net names is
        // either a short or a wrong label, and both readings are things a user must see — so
        // brief 2 gives that piece NO name, and a table written over it would state the board's
        // connectivity while being silent about the one place it is in doubt.
        if (resolved.Stamped.Refusals.Count > 0)
            return Refuse(
                $"'{Path.GetFileName(full)}' has copper that carries two different net names, so " +
                "nothing was written: " + string.Join(" ", resolved.Stamped.Refusals));

        var schematic = resolved.Schematic;
        var placements = PlacementWriter.EntriesOf(view);
        var parts = new List<BomEntry>();

        foreach (var inst in view.Instances)
        {
            if (inst.DisplayRefDes is not { Length: > 0 } refdes) continue;

            // R-ab3-1e. The schematic's own statement where there is one, and where there is not,
            // the CELL THE PLACEMENT REFERENCES — which is the whole of what a board with no
            // schematic behind it knows about its parts, and is R-ab3-2d's "thin, and honest about
            // being thin".
            var part = schematic.For(inst.SchematicId);
            parts.Add(new BomEntry(
                refdes,
                part?.Value,
                part?.Footprint ?? PlacementWriter.CellName(inst.CellRef),
                part?.TypeName));
        }

        return new BoardProjection(
            full, board, view.DbuPerMicron,
            resolved.Pads, [.. view.Shapes.OfType<ViaShape>()], placements, parts,
            tech, resolved.Stamped, schematic.Any, null, notes);
    }

    /// <summary>The board netlist for a projection, as text.</summary>
    public static string BoardNetlistTextOf(BoardProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return BoardNetlistWriter.Write(
            projection.Pads, projection.Vias, projection.DbuPerMicron, projection.Technology,
            // A via with no stamp of its own takes the name of the piece it is part of — R-ab2-2e,
            // asked of the SAME partition the pads were named against rather than of a second one.
            via => projection.Stamped.NameAt(via.X, via.Y, null),
            new BoardNetlistWriteOptions(Job: projection.BoardName));
    }

    /// <summary>The placement table for a projection, as text.</summary>
    public static string PlacementTextOf(BoardProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return PlacementWriter.Write(
            projection.Placements, projection.DbuPerMicron,
            new PlacementWriteOptions(Board: projection.BoardName));
    }

    /// <summary>The bill of materials for a projection, as text.</summary>
    public static string BomTextOf(BoardProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return BomWriter.Write(projection.Parts, new BomWriteOptions(Board: projection.BoardName));
    }

    /// <summary>
    /// Writes whichever of the three <paramref name="paths"/> names, from ONE projection.
    /// </summary>
    /// <remarks>
    /// <b>Every text is produced before the first file is opened.</b> A run that writes the netlist
    /// and then fails on the placement has left a board with two files that disagree about it,
    /// which is the state this whole series exists to make unreachable.
    /// </remarks>
    public static void Write(
        BoardProjection projection, string? ipcPath, string? placementPath, string? bomPath)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Refusal is { Length: > 0 })
            throw new InvalidOperationException(projection.Refusal);

        string? ipc = ipcPath is { Length: > 0 } ? BoardNetlistTextOf(projection) : null;
        string? place = placementPath is { Length: > 0 } ? PlacementTextOf(projection) : null;
        string? bom = bomPath is { Length: > 0 } ? BomTextOf(projection) : null;

        if (ipc is not null) WriteText(ipcPath!, ipc);
        if (place is not null) WriteText(placementPath!, place);
        if (bom is not null) WriteText(bomPath!, bom);
    }

    /// <summary>No byte order mark, and LF line endings as the writers produced them — a file whose
    /// bytes differ by platform is a file the byte-identity gate cannot hold (R-ab3-3b), and
    /// <c>BomFile</c>'s reader reports a byte order mark it had to strip.</summary>
    private static void WriteText(string path, string text)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
    }
}
