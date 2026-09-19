// The plane pair's own modes and its |Z| map, read off the netlist brief 14's extraction produced
// (docs/sonnet-briefs/brief-railrf-15-modes-and-maps.md R-rail15-1, R-rail15-2;
//  docs/design/railrf.md §4.5, §2.4 "The plane resonances and the impedance map").
//
// ── THIS FILE IS THE "SAME DISCRETISATION" CLAIM, MADE LITERAL ─────────────────────────────────
//
// §4.5: "The cavity modes come out of the same discretisation as a generalised eigenproblem on the
// loss-free system, so the mode list and the sweep cannot disagree about the structure."
//
// The only way to make that sentence true rather than aspirational is to build the eigenproblem
// out of the ELEMENTS THE SWEEP SOLVES, and that is all this file does. Every capacitance below is
// a PdnOriginKind.PlaneShunt capacitor the extraction stamped; every inductance is a
// PdnOriginKind.MeshEdge inductor it stamped. Nothing is recomputed from a stackup, an area or a
// cell size — a second reading of the geometry is exactly how the mode list and the curve would
// come to disagree, and it would disagree in the ninth digit on the boards where nobody checks.
//
// ── THE CAVITY CELL IS A PAIR OF CONDUCTOR CELLS, AND IT IS KEYED GEOMETRICALLY ────────────────
//
// The extraction meshes BOTH conductors (PdnMeshExtractor's header says why: that is how §4.1's
// factor of two on R is paid). A cavity cell is one cell of the rail and the cell of the reference
// underneath it, joined by §4.1's shunt branch — so the unknown is the potential ACROSS the pair,
// which is what a decoupling capacitor is connected across and what §4.5's eigenproblem is in.
//
// THE MAP IS KEYED ON PdnCellRef AND NOT ON THE NETLIST NODE NUMBER, and that is not a style
// choice. PdnAssembly TIES every cell of a port's pin field into ONE node (§4.3), so one node can
// be several cells and a node→cell map would be ambiguous exactly under the ports — which is
// exactly where §2.4 says the answer matters. A PdnCellRef is a layer and a grid index and is
// unambiguous everywhere.
//
// ── WHAT IS IN THE ANSWER AND WHAT IS NOT, STATED RATHER THAN DISCOVERED ───────────────────────
//
// The modes and the map are the PLANE PAIR's — its copper, its stackup, its shape. The decoupling
// parts, the sources and the load currents hanging on it are NOT in either of them.
//
// That is the claim §4.5 makes and the one §7 gates ("a uniform rectangular plane pair has
// closed-form modes"), and it is the one a rectangle can check. A "mode list" that moved when a
// capacitor was placed would be a different quantity with the same name and no closed form to check
// it against — and the parts' own answer already exists and is the |Z| CURVE (§2.4, brief 12). The
// answer says so in its notes rather than leaving a reader to work it out.
//
// ── AND THE MAP IS AT THE EXTRACTION'S OWN FREQUENCY, WHICH IS A REFUSAL AND NOT A PARAMETER ───
//
// §2.8: the extraction is FOR one frequency. Its copper resistances are that frequency's skin
// effect and its dielectric conductances are G = ωC·tan δ at that ω. Mapping at a different
// frequency would solve this frequency's LOSSES at that frequency's reactances — a smooth,
// plausible, wrong picture. So the map is at PdnProvenance.FrequencyHz and a caller who wants
// another frequency re-extracts, which is what the sweep already does per point.

using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.Pdn;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// What one mode reads at one observation port — <b>the deliverable of R-rail15-2</b>.
/// </summary>
/// <param name="PortIndex">The port's index in the rail's own load list.</param>
/// <param name="Name">What the report calls it — the anchor's own spelling, never a node number.</param>
/// <param name="Value">The mode's field at the port's worst cell, signed and against the mode's own
/// peak.</param>
/// <param name="Magnitude">Its magnitude, 0 … 1. <b>1 means this port IS where the mode is
/// worst</b>, which is §2.4's "a mode whose maximum sits on the load pin field".</param>
/// <param name="At">The cell it was read at, so the map can be pointed at it. Null where the port
/// resolved to no cell of the plane pair.</param>
public sealed record PdnModePortValue(
    int PortIndex, string Name, double Value, double Magnitude, PdnCellRef? At);

/// <summary>
/// One cavity mode of this board: where it resonates, its field, and what each declared observation
/// port sees of it.
/// </summary>
/// <param name="Index">0-based, lowest frequency first.</param>
/// <param name="FrequencyHz">Its resonant frequency.</param>
/// <param name="Field">The field, one value per cavity cell, normalised so the peak is +1. Indexed
/// alongside <see cref="PdnPlaneAnswer.Cells"/>.</param>
/// <param name="AtPorts">R-rail15-2 — one row per observation port, in the rail's own order.</param>
public sealed record PdnPlaneMode(
    int Index,
    double FrequencyHz,
    IReadOnlyList<double> Field,
    IReadOnlyList<PdnModePortValue> AtPorts)
{
    /// <summary>The port this mode is worst on, or null where it reaches none of them.</summary>
    public PdnModePortValue? Worst =>
        AtPorts.Count == 0 ? null : AtPorts.OrderByDescending(p => p.Magnitude).First();

    /// <summary>
    /// The sentence §2.4 asks for — <b>a frequency and a PLACE</b>.
    /// </summary>
    /// <remarks>
    /// <i>"A mode whose maximum sits on the load pin field is a problem; the same mode with its
    /// maximum in a corner is not."</i> So the row says which, and the corner case is said aloud
    /// rather than left as a blank column: a reader who is told only the frequency has been told
    /// the half that is not actionable.
    /// </remarks>
    public string Describe()
    {
        string f = PdnMask.Hertz(FrequencyHz);

        if (Worst is not { } worst)
            return $"{f} — this rail declares no observation port, so there is nothing to say about " +
                   "where this mode lands.";

        return worst.Magnitude >= 0.5
            ? $"{f} — its maximum is on {worst.Name}, at {worst.Magnitude:0.00} of the mode's own peak."
            : $"{f} — the nearest observation port, {worst.Name}, sees {worst.Magnitude:0.00} of it. " +
              "This mode peaks away from every declared port.";
    }
}

/// <summary>One cell of the |Z| map: the copper it is, and what the plane reads there.</summary>
/// <param name="Cell">The rail's own cell — what the map colours.</param>
/// <param name="OhmsMagnitude">|Z| from the driven port to this cell, in ohms.</param>
public readonly record struct PdnPlaneMapCell(PdnCellRef Cell, double OhmsMagnitude);

/// <summary>
/// Everything one plane-pair answer produced. <see cref="Refusal"/> non-null means NOTHING was
/// computed — the contract <see cref="PdnExtraction"/> already states.
/// </summary>
/// <param name="Refusal">Why nothing was computed, or null.</param>
/// <param name="Modes">§4.5's modes, lowest first.</param>
/// <param name="Cells">The rail cell each cavity index IS, so a field can be drawn.</param>
/// <param name="ImpedanceMap">§2.4's |Z| across the plane at <paramref name="MapFrequencyHz"/>, or
/// empty where it could not be computed.</param>
/// <param name="MapFrequencyHz">The frequency the map is at — the EXTRACTION's own (see this file's
/// header).</param>
/// <param name="MapPortName">The observation port the map is driven from.</param>
/// <param name="Pieces">How many galvanically separate pieces the plane pair is.</param>
/// <param name="CellSizeMetres">The mesh pitch this answer is of — <b>its OWN extraction's, never
/// the DC run's</b>. λ/20 binds in the cavity band and does not at DC, so the two are routinely
/// different and a map tiled at the wrong one is a map of the wrong squares.</param>
/// <param name="Notes">What this answer established that the document did not state.</param>
public sealed record PdnPlaneAnswer(
    string? Refusal,
    IReadOnlyList<PdnPlaneMode> Modes,
    IReadOnlyList<PdnCellRef> Cells,
    IReadOnlyList<PdnPlaneMapCell> ImpedanceMap,
    double MapFrequencyHz,
    string MapPortName,
    int Pieces,
    double CellSizeMetres,
    IReadOnlyList<string> Notes)
{
    internal static PdnPlaneAnswer Refused(string why) => new(why, [], [], [], 0, "", 0, 0, []);
}

/// <summary>§4.5's modes and §2.4's impedance map, off one extraction's own elements.</summary>
public static class PdnPlaneModes
{
    /// <summary>
    /// Computes the plane pair's modes and its |Z| map, or refuses and says why.
    /// </summary>
    /// <param name="pdn">The extraction. <b>Must carry §4.1's shunt branch</b> — a DC extraction
    /// does not (<see cref="PdnProvenance.ShuntBranchPresent"/>), and is refused by name.</param>
    /// <param name="modeCount">How many modes to report. Six is §7's own acceptance count.</param>
    /// <param name="mapPortIndex">Which observation port the |Z| map is driven from.</param>
    /// <param name="options">The solver's own ceiling. Null takes its defaults.</param>
    public static PdnPlaneAnswer Of(
        PdnNetlist pdn, int modeCount = 6, int mapPortIndex = 0, PdnModeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdn);

        if (!pdn.Provenance.ShuntBranchPresent)
            return PdnPlaneAnswer.Refused(
                "This extraction carries no plane-pair capacitance, so there is no cavity to find " +
                "modes in. At ω = 0 §4.1's shunt branch vanishes by construction — extract at the " +
                "top of the band the modes are wanted over, not at DC. " +
                (pdn.Provenance.PlaneCapacitanceFarads > 0
                    ? ""
                    : "The stackup also states no dielectric between this rail and its reference, " +
                      "which is the other way this happens."));

        var notes = new List<string>();
        if (Build(pdn, notes, out var system, out var cells, out var ports) is { } refusal)
            return PdnPlaneAnswer.Refused(refusal);

        // ── §4.5's eigenproblem, on the loss-free half of exactly these terms ──────────────────
        var set = PdnModeSolver.Solve(system, options ?? new PdnModeOptions { Count = modeCount });
        if (set.Refusal is { } modeRefusal) return PdnPlaneAnswer.Refused(modeRefusal);
        notes.AddRange(set.Notes);

        var modes = new List<PdnPlaneMode>(set.Modes.Count);
        foreach (var mode in set.Modes)
        {
            var atPorts = new List<PdnModePortValue>(ports.Count);
            foreach (var (index, name, portCells) in ports)
            {
                var reading = PdnFieldMap.ValueAtPort(mode.Field, portCells);
                atPorts.Add(new PdnModePortValue(
                    index, name,
                    reading?.Value ?? 0, reading?.Magnitude ?? 0,
                    reading is { } r ? cells[r.Cell] : null));
            }

            modes.Add(new PdnPlaneMode(mode.Index, mode.FrequencyHz, mode.Field, atPorts));
        }

        // ── §2.4's map, at the extraction's own frequency ──────────────────────────────────────
        var map = Array.Empty<PdnPlaneMapCell>();
        string mapPort = "";
        double mapHz = pdn.Provenance.FrequencyHz;

        if (ports.Count == 0)
        {
            notes.Add(
                "This rail declares no observation port, so there is nothing to drive the impedance " +
                "map from and none was computed. Add the load whose impedance you want — a load " +
                "with no current is an observation port, which is exactly this case.");
        }
        else
        {
            int which = Math.Clamp(mapPortIndex, 0, ports.Count - 1);
            var (_, name, portCells) = ports[which];
            mapPort = name;

            // The WHOLE pin field drives it, exactly as §4.3 ties it — see PdnFieldMap.Impedance
            // for why one cell of a field is the wrong drive.
            var z = PdnFieldMap.Impedance(system, portCells, mapHz, out string? mapRefusal);

            if (mapRefusal is { } why) notes.Add(why);
            else
            {
                map = new PdnPlaneMapCell[system.CellCount];
                for (int i = 0; i < system.CellCount; i++)
                    map[i] = new PdnPlaneMapCell(cells[i], z![i].Magnitude);
            }
        }

        notes.Add(
            "These modes and this map are the PLANE PAIR's own — its copper, its shape and its " +
            "stackup. The decoupling parts, the sources and the loads hanging on it are not in " +
            "either of them; their answer is the |Z| curve.");

        return new PdnPlaneAnswer(
            null, modes, cells, map, mapHz, mapPort, set.Pieces,
            pdn.Provenance.CellSizeMetres, notes);
    }

    // ── the cavity, out of the netlist's own elements ──────────────────────────────────────────

    /// <summary>
    /// Turns one extraction into <see cref="PdnCavitySystem"/> — see this file's header for why
    /// every term is read off a stamped element rather than recomputed.
    /// </summary>
    /// <returns>A refusal sentence, or null.</returns>
    private static string? Build(
        PdnNetlist pdn, List<string> notes,
        out PdnCavitySystem system, out List<PdnCellRef> cells,
        out List<(int Index, string Name, List<int> Cells)> ports)
    {
        system = null!;
        cells = [];
        ports = [];

        var components = pdn.Netlist.Components;

        // ── the cells: one per §4.1 shunt branch, keyed on the RAIL cell it is of ──────────────
        var index = new Dictionary<PdnCellRef, int>();
        var capacitance = new List<double>();
        var conductance = new List<double>();

        // The reference cell of each pair, so a reference-side mesh edge finds its cavity cell too.
        var referenceOf = new Dictionary<PdnCellRef, int>();
        int sharedReference = 0;

        foreach (var origin in pdn.Origins)
        {
            if (origin.Kind != PdnOriginKind.PlaneShunt) continue;
            if (origin.From is not { } rail || origin.To is not { } reference) continue;
            if (origin.ComponentIndex < 0 || origin.ComponentIndex >= components.Count) continue;

            if (!index.TryGetValue(rail, out int cell))
            {
                index[rail] = cell = cells.Count;
                cells.Add(rail);
                capacitance.Add(0);
                conductance.Add(0);
            }

            // A reference cell facing two rail layers belongs to two cavity cells and can only be
            // one of them here. Counted and stated: the reference-side copper of the second pair
            // then contributes no edge, which makes that pair's inductance the rail's half alone.
            if (!referenceOf.TryAdd(reference, cell) && referenceOf[reference] != cell)
                sharedReference++;

            var c = components[origin.ComponentIndex];
            if (c.ComponentType == "C" && c.Parameters.TryGetValue("C", out var farads))
                capacitance[cell] += farads.ToComplex().Real;
            else if (c.ComponentType == "R" && c.Parameters.TryGetValue("R", out var ohms) &&
                     ohms.ToComplex().Real > 0)
                conductance[cell] += 1.0 / ohms.ToComplex().Real;
        }

        if (cells.Count == 0)
            return "This extraction stamped no plane-pair capacitance at all, so there is no cavity. " +
                   "The rail's copper and its reference nowhere overlap — check the reference layer " +
                   "and the reference extent.";

        if (sharedReference > 0)
            notes.Add(
                $"{sharedReference} cell(s) of the reference conductor face more than one layer of " +
                "this rail. Each reference cell belongs to one plane pair here, so the second pair " +
                "carries the rail's half of §4.1's inductance and not the loop's — its own modes " +
                "will read high. A rail on one layer over its reference is the case this models.");

        // ── the edges: §4.1's LOOP terms, which are the two conductors' halves in series ───────
        var edgeIndex = new Dictionary<(int A, int B), int>();
        var edges = new List<PdnCavityEdge>();

        foreach (var origin in pdn.Origins)
        {
            if (origin.Kind != PdnOriginKind.MeshEdge) continue;
            if (origin.InductanceHenries is not { } henries || !(henries > 0)) continue;
            if (origin.From is not { } from || origin.To is not { } to) continue;

            if (!CellOf(from, index, referenceOf, out int a) ||
                !CellOf(to, index, referenceOf, out int b) || a == b) continue;

            var key = a < b ? (a, b) : (b, a);
            double r = origin.ResistanceOhms is { } ohms && ohms > 0 ? ohms : 0;

            if (edgeIndex.TryGetValue(key, out int at))
            {
                var had = edges[at];
                edges[at] = had with
                {
                    ResistanceOhms = had.ResistanceOhms + r,
                    InductanceHenries = had.InductanceHenries + henries,
                };
            }
            else
            {
                edgeIndex[key] = edges.Count;
                edges.Add(new PdnCavityEdge(key.Item1, key.Item2, r, henries));
            }
        }

        if (edges.Count == 0)
            return "This extraction stamped no inductance on the plane pair, so there is nothing " +
                   "for a wave to travel on. §4.1's L vanishes at ω = 0, which is what a DC " +
                   "extraction looks like — extract at the top of the band instead.";

        // ── the ports, as sets of cavity cells (§4.3 ties a pin field into one) ────────────────
        foreach (var port in pdn.Ports)
        {
            var portCells = new List<int>();
            foreach (var cell in port.Cells)
                if (CellOf(cell, index, referenceOf, out int c) && !portCells.Contains(c))
                    portCells.Add(c);

            ports.Add((port.Index, port.Name, portCells));
        }

        system = new PdnCavitySystem
        {
            CellCount = cells.Count,
            Edges = edges,
            CapacitanceFarads = capacitance,
            ConductanceSiemens = conductance,
        };

        return null;
    }

    /// <summary>The cavity cell a conductor cell belongs to, whichever conductor it is on.</summary>
    private static bool CellOf(
        PdnCellRef cell,
        Dictionary<PdnCellRef, int> rail,
        Dictionary<PdnCellRef, int> reference,
        out int index) =>
        cell.IsReference ? reference.TryGetValue(cell, out index) : rail.TryGetValue(cell, out index);
}
