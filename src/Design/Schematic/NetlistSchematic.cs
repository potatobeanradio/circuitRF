using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Schematic;

/// <summary>What <see cref="NetlistSchematic.Build"/> made of a netlist.</summary>
/// <param name="Schematic">The drawing, or null when anything was refused — a drawing that leaves
/// out one instance is a different circuit, so it is never handed back half made.</param>
/// <param name="Refusals">Why the netlist cannot be drawn, one sentence per cause. Empty on success.</param>
/// <param name="Notes">What the drawing does not carry, or carries differently, that a reader should
/// hear. Never a reason to refuse.</param>
public sealed record NetlistSchematicResult(
    SchematicEditModel?   Schematic,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Notes)
{
    /// <summary>Instances an exact hint was given for and could not be drawn at — their pins would land on another
    /// instance's — and which were placed automatically instead (brief-img-10 R-im10-4).</summary>
    public IReadOnlyList<string> PlacementFallbacks { get; init; } = [];
}

/// <summary>Where one instance is drawn, exactly: its origin in schematic units (snapped to the grid by the drawing)
/// and its orientation.</summary>
public readonly record struct ExactInstance(double X, double Y, SymbolRotation Rotation, bool MirrorX);

/// <summary>
/// A drawing given rather than arranged (brief-img-10 R-im10-4, overview D12): each hinted instance at its position and
/// orientation, and each hinted net along its own orthogonal routes, in schematic units. A net with routes is wired with
/// them, each pin they do not reach re-joined with at most one dog-leg; anything not hinted is arranged as before.
/// </summary>
public sealed record ExactPlacement
{
    public IReadOnlyDictionary<string, ExactInstance> Instances { get; init; } = new Dictionary<string, ExactInstance>();

    /// <summary>Per net, by the name the netlist gives it, its wire runs — each a polyline.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<(double X, double Y)>>> Routes { get; init; } =
        new Dictionary<string, IReadOnlyList<IReadOnlyList<(double X, double Y)>>>();

    /// <summary>Place the instances and nothing else — no ground symbol, wire or net label: the caller draws the wiring
    /// itself, as a picture draws it (<see cref="Routes"/> is then unused).</summary>
    public bool PlaceOnly { get; init; }

    /// <summary>Snap each hinted origin and route vertex to <see cref="NetlistSchematic.Grid"/>. False keeps them
    /// exactly as given — a schematic imported from a source drawn on a finer grid (brief-schimp-1 R-si1-6), whose
    /// pins at odd multiples of 50 a snap would move off its own wires. The caller then puts every point on the grid
    /// the drawing is extracted with (<see cref="SchematicEditModel.GridSize"/>).</summary>
    public bool SnapToGrid { get; init; } = true;

    /// <summary>Keep a hinted instance where it is even when its glyph overlaps another's: only a pin landing on
    /// another net's pin sends it to the automatic arrangement. An imported drawing's geometry is the source's
    /// (R-si1-5), and two glyphs drawn touching in it are not a wrong reading.</summary>
    public bool KeepOverlappingGlyphs { get; init; }

    /// <summary>Instances drawn as a cell's own symbol rather than refused for having none (brief-schimp-1 R-si1-4):
    /// by instance name, the cell reference to store and the symbol's pins, local, in port order — one per net the
    /// netlist line binds.</summary>
    public IReadOnlyDictionary<string, ExactCell> Cells { get; init; } = new Dictionary<string, ExactCell>();

    /// <summary>Nets drawn without a net label when one connected group of their wires reaches every pin — a net the
    /// source never named, whose name here is only the netlist's handle for it.</summary>
    public IReadOnlySet<string> QuietNets { get; init; } = new HashSet<string>();

    /// <summary>Pins (instance name, pin index) joined to their net by a net label on the pin rather than a dog-leg — a
    /// hidden power pin, or a port added beside the drawing.</summary>
    public IReadOnlySet<(string Instance, int Pin)> JoinByLabel { get; init; } = new HashSet<(string, int)>();

    /// <summary>Wire every net with <see cref="SchematicAutoRouter"/>, as an arranged drawing is, rather than along
    /// <see cref="Routes"/> (unused then): the placement is given and the wiring is not — a board's parts sit where its
    /// copper puts them, and its copper runs are not schematic wires (R-dfr22-2-2).</summary>
    public bool AutoRoute { get; init; }
}

/// <summary>Where one instance sits on a board, in the board's own units with y up, and — when it has a direction
/// there — which way its pin 1 → pin 2 runs, in degrees on the board's axes (R-dfr22-2-2).</summary>
public readonly record struct BoardHint(long X, long Y, double? AxisDeg = null);

/// <summary>A cell instance an <see cref="ExactPlacement"/> draws: the <c>CellRef</c> it stores and its symbol's pins,
/// local, in port order.</summary>
public sealed record ExactCell(string CellRef, IReadOnlyList<(float X, float Y)> Pins);

/// <summary>
/// A drawn schematic from a netlist (brief-agent-authoring-overview.md AA-6): every instance placed
/// as its built-in symbol, the signal path left to right with the ports at its ends, shunt elements
/// dropped below the net they hang from, a ground symbol on every terminal on net <c>0</c>, every
/// net wired orthogonally, and every net LABELLED with the name the netlist gave it.
///
/// <para><b>The round trip is the contract.</b> The schematic extracts — through
/// <see cref="SchematicCircuit"/>, the extraction Simulate performs — to the same instances, nets
/// and values the netlist states. That is why every net carries a label rather than only the ones
/// a person would label: a net's NAME is part of the circuit (a measurement reads <c>V(out)</c>),
/// and an unlabelled wire extracts under an invented one. The only parameter a drawing adds is the
/// port count a variadic symbol reads its pins from (<c>NumPorts</c> on an SDD or a Z-port,
/// <c>Pins</c> on a Verilog-A device), and only when the line left it to be inferred from its nets.</para>
///
/// <para><b>Geometry is connectivity here</b>, so the wiring is <see cref="SchematicAutoRouter"/>'s,
/// the router the SPICE subcircuit import already draws with: it knows which crossings join two nets
/// and which do not. A terminal it cannot reach is connected by a net label, which is a real
/// connection — the drawing suffers, the circuit does not, and the result says so.</para>
///
/// <para><b>What it refuses</b> rather than approximates: a netlist that defines cells (a drawing of
/// a hierarchy needs a cell folder per definition, which this does not write), an instance whose
/// type has no schematic symbol, an instance whose net count its symbol cannot carry, and a wirebond,
/// whose pins come from the design file it references rather than from a built-in symbol.</para>
///
/// <para>Nothing here touches the disk. The caller decides where the drawing goes, which is also what
/// decides how a relative file reference in it resolves — see the CLI's own note about that.</para>
/// </summary>
public static class NetlistSchematic
{
    private const double P = SchematicAutoRouter.P;

    /// <summary>Free wire between a net region's edge and the next pin along the main line.</summary>
    private const double Lead = 400;

    /// <summary>Centre-to-centre spacing of the elements hanging from one net.</summary>
    private const double HangerPitch = 800;

    /// <summary>How far below the main line a hanging element's upper pin sits, so its wire Ts onto
    /// the line with a vertex rather than lying along it.</summary>
    private const double HangerDrop = 300;

    /// <summary>How far above the main line an element bridging two path nets sits.</summary>
    private const double BridgeRise = 900;

    /// <summary>Spacing of successive rows of bridging elements above the line.</summary>
    private const double BridgeRowPitch = 700;

    /// <summary>Clearance kept between the hanging elements and the rows of everything else.</summary>
    private const double RowGap = 400;

    /// <summary>Slot width of the rows "everything else" is placed in.</summary>
    private const double Slot = 400;

    private const string Ground = "0";

    /// <summary>The kinds a netlist line never draws as: sentinels, and a placeholder that would
    /// assert nothing about the part.</summary>
    private static readonly HashSet<SymbolKind> NotDrawable =
    [
        SymbolKind.Ground, SymbolKind.Pin, SymbolKind.Var, SymbolKind.Meas,
        SymbolKind.Generic, SymbolKind.SpiceModel,
    ];

    /// <summary>Several kinds share one engine component; the drawing takes the first of each
    /// group whose pins can carry the line's nets.</summary>
    private static readonly SymbolKind[] Preferred =
    [
        SymbolKind.Term, SymbolKind.TermG,
        SymbolKind.Tuner, SymbolKind.LoadTuner, SymbolKind.SourceTuner,
        SymbolKind.Mixer, SymbolKind.MixerD,
        SymbolKind.Switch, SymbolKind.SwitchD,
        SymbolKind.OpAmp, SymbolKind.OpAmp5,
        SymbolKind.Coupler, SymbolKind.Hybrid90, SymbolKind.Hybrid180,
    ];

    /// <summary>The kinds a path may end on when the netlist has fewer than two ports: the things
    /// that drive or terminate a circuit.</summary>
    private static readonly HashSet<SymbolKind> Fixtures =
    [
        SymbolKind.Term, SymbolKind.TermG, SymbolKind.Tuner, SymbolKind.LoadTuner,
        SymbolKind.SourceTuner, SymbolKind.P1Tone, SymbolKind.PnTone, SymbolKind.ToneSource,
        SymbolKind.CurrentToneSource, SymbolKind.Vdc, SymbolKind.ZPort,
    ];

    // ─────────────────────────────────────────────────────────────────────────
    //  One placed thing
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class Item
    {
        public required EditableComponent    Comp  { get; init; }

        /// <summary>Per symbol pin, the net — <c>"0"</c> for ground, rewritten to a private name
        /// once that pin has a ground symbol of its own.</summary>
        public required string[]             Nets  { get; init; }

        public required (float X, float Y)[] Local { get; init; }

        public bool Placed { get; set; }

        public IEnumerable<string> SignalNets
            => Nets.Where(n => n != Ground).Distinct(StringComparer.Ordinal);

        public (double X, double Y) Pin(int k)
            => SchematicGeometry.LocalToWorld(Local[k].X, Local[k].Y,
                   Comp.X, Comp.Y, Comp.Rotation, Comp.MirrorX);

        /// <summary>Pin offset from the component origin under the current rotation.</summary>
        public (double X, double Y) Offset(int k)
            => SchematicGeometry.LocalToWorld(Local[k].X, Local[k].Y, 0, 0, Comp.Rotation, Comp.MirrorX);

        /// <summary>The glyph's box relative to the origin, under the current rotation — measured
        /// off the renderer itself, so a keep-out is the symbol that is DRAWN.</summary>
        public (double MinX, double MinY, double MaxX, double MaxY) Box { get; set; }

        /// <summary>The drawn glyph's lowest point below the origin — what the renderer hangs the
        /// label block from.</summary>
        public double GlyphBottom { get; set; }

        /// <summary><see cref="Box"/> with the label block in it — what spacing is decided on, and
        /// what a wire is kept out of, so no wire is drawn through a value.</summary>
        public (double MinX, double MinY, double MaxX, double MaxY) Full { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Entry point
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws <paramref name="tb"/>. <paramref name="lib"/> is consulted only to refuse a hierarchy.
    /// </summary>
    /// <param name="schematicDirectory">Where the drawing will be written, when known. The netlist
    /// reader resolves a Touchstone <c>File</c> to an absolute path; a schematic stores it relative to
    /// the base it is read against (<see cref="SchematicCircuit.ReferenceBaseOf"/>: the workspace root
    /// when this folder is inside one, else this folder), so it is rewritten relative to that. The
    /// technology reference stays relative to this folder itself. Null leaves both absolute.</param>
    /// <param name="hints">Where each instance's copper is in the artwork it was recognised from, by instance
    /// name (brief-artsch-6 R-as6-3) — a layout point, y up. With hints, the elements hanging from one net of
    /// the main line are ordered along the artwork's path, and a two-pin one whose artwork lies on the left of
    /// the path's travel (above a line drawn left to right) is drawn ABOVE the line. Null draws exactly as
    /// before, byte for byte.</param>
    /// <param name="artworkSides">With hints: whether a hanger is drawn on the side of the line its artwork is on.
    /// False keeps the artwork's ORDER along the line and draws every hanger below it.</param>
    /// <param name="exact">The drawing as a picture drew it (brief-img-10 R-im10-4): each hinted instance where it says,
    /// unless its pins would land on another's — then that one alone is arranged automatically and named in
    /// <see cref="NetlistSchematicResult.PlacementFallbacks"/>. Null draws exactly as before, byte for byte.</param>
    /// <summary>
    /// A <c>.cnl</c> on disk, drawn: read as written with <see cref="CircuitRF.Core.Netlist.CnlReader"/> — NOT with the
    /// technology binding a run uses, which would write the workspace's substrate into each line a second time — and
    /// handed to <see cref="Build"/>. The one function <c>netlist --to-schematic</c> and a plugin's <c>schematic</c>
    /// view both call (brief-plg-3 §4), so the two cannot draw one netlist two ways. Throws when the file cannot be
    /// read; a netlist that reads and cannot be drawn is a result with <see cref="NetlistSchematicResult.Refusals"/>.
    /// </summary>
    /// <param name="schematicDirectory">Where the drawing will be saved, which decides how a relative file reference
    /// in it is written. Null is the netlist's own folder.</param>
    public static NetlistSchematicResult FromFile(string cnlPath, string? schematicDirectory = null)
    {
        string full = System.IO.Path.GetFullPath(cnlPath);
        var (lib, tb) = CircuitRF.Core.Netlist.CnlReader.ReadFile(full);
        return Build(lib, tb, schematicDirectory ?? System.IO.Path.GetDirectoryName(full));
    }

    /// <param name="existingCells">brief-icp-9 — cells the netlist defines that already exist as cell folders, by cell name:
    /// an instance of one is drawn as that cell's instance (its <c>CellRef</c> and its symbol's pins) rather than refused,
    /// and its definition in the netlist is the one the cell's own schematic extracts to.</param>
    public static NetlistSchematicResult Build(Library lib, TestBench tb, string? schematicDirectory = null,
                                               IReadOnlyDictionary<string, (long X, long Y)>? hints = null,
                                               bool artworkSides = true, ExactPlacement? exact = null,
                                               IReadOnlyDictionary<string, ExactCell>? existingCells = null)
    {
        ArgumentNullException.ThrowIfNull(lib);
        ArgumentNullException.ThrowIfNull(tb);

        var refusals = new List<string>();
        var notes    = new List<string>();

        var undrawable = lib.Cells.Where(c => existingCells?.ContainsKey(c.Name) != true).ToList();
        if (undrawable.Count > 0)
            refusals.Add(
                $"The netlist defines {undrawable.Count} cell(s) ({string.Join(", ", undrawable.Select(c => c.Name))}). "
              + "A drawing of a hierarchy needs a cell folder for each definition, which this does not "
              + "write; draw each definition as a netlist of its own, or flatten it first.");

        var items = new List<Item>();
        foreach (var inst in tb.Instances)
        {
            if (lib.Find(inst.Reference) is not null)
            {
                if (existingCells?.GetValueOrDefault(inst.Reference) is not { } existing) continue;   // already refused above
                if (existing.Pins.Count == inst.NetBindings.Count) items.Add(CellItem(inst, existing));
                else refusals.Add($"{inst.InstanceName}: its cell's symbol has {existing.Pins.Count} pin(s) and the line binds "
                                + $"{inst.NetBindings.Count} net(s).");
                continue;
            }
            if (exact is not null && exact.Cells.TryGetValue(inst.InstanceName, out var cell))
            {
                if (cell.Pins.Count == inst.NetBindings.Count) items.Add(CellItem(inst, cell));
                else refusals.Add($"{inst.InstanceName}: its cell's symbol has {cell.Pins.Count} pin(s) and the line binds "
                                + $"{inst.NetBindings.Count} net(s).");
                continue;
            }
            if (MapInstance(inst, schematicDirectory, out string? why) is { } item) items.Add(item);
            else refusals.Add(why!);
        }

        if (refusals.Count > 0) return new NetlistSchematicResult(null, refusals, notes);

        var model = new SchematicEditModel();
        foreach (var it in items) model.Components.Add(it.Comp);

        var fallbacks = new List<string>();
        var placed = Place(items, notes, hints, artworkSides, exact, fallbacks);
        if (exact is not { PlaceOnly: true })
        {
            var grounds = PlaceGrounds(model, placed);
            if (exact is null or { AutoRoute: true }) Wire(model, placed, grounds, notes);
            else WireExact(model, placed, grounds, exact, notes);
        }
        PlaceDirectives(model, tb);

        // A loadpull's grid and a pursuit's output grid come back from the reader absolute, as a part's File does, and are
        // stored relative to the same base: copied verbatim, the drawing named a folder of the machine it was drawn on.
        string? fileBase = FileBase(schematicDirectory);
        foreach (var a in tb.Analyses) model.Analyses.Add(RebasePaths(a, fileBase));

        // The netlist's own technology becomes the schematic's (R-as6-1), relative to where it is written.
        if (tb.Technology is { Length: > 0 } technology)
            model.TechRef = schematicDirectory is not null && System.IO.Path.IsPathRooted(technology)
                ? SchematicTechnology.StoredRef(technology, schematicDirectory)
                : technology;
        if (tb.Tuning is { IsEmpty: false } tuning) model.Tuning = tuning.Clone();

        if (tb.Functions.Count > 0)
            notes.Add(
                $"{tb.Functions.Count} user function(s) ({string.Join(", ", tb.Functions.Select(f => f.Name))}) "
              + "are not carried: a schematic has nowhere to declare one.");
        if (tb.RawDirectives.Count > 0)
            notes.Add(
                $"{tb.RawDirectives.Count} directive(s) the netlist reader stores verbatim are not carried: "
              + string.Join("; ", tb.RawDirectives.Select(r => $"{r.Kind} {r.RawLine}".Trim())) + ".");

        return new NetlistSchematicResult(model, refusals, notes) { PlacementFallbacks = fallbacks };
    }

    /// <summary>
    /// The pins <paramref name="inst"/> is drawn with — each one's offset from the component origin under
    /// <paramref name="rotation"/> and <paramref name="mirrorX"/>, and the net on it — or null when the line cannot be
    /// drawn. What an exact hint is fitted against (brief-img-10 R-im10-4), so the fit and the drawing use one symbol.
    /// </summary>
    public static IReadOnlyList<(double X, double Y, string Net)>? PinOffsets(Instance inst, SymbolRotation rotation, bool mirrorX)
    {
        ArgumentNullException.ThrowIfNull(inst);
        if (MapInstance(inst, null, out _) is not { } it) return null;
        it.Comp.Rotation = rotation;
        it.Comp.MirrorX = mirrorX;
        return [.. Enumerable.Range(0, it.Local.Length).Select(k => { var (x, y) = it.Offset(k); return (x, y, it.Nets[k]); })];
    }

    /// <summary>The schematic grid every drawn point is snapped to.</summary>
    public const double Grid = P;

    // ─────────────────────────────────────────────────────────────────────────
    //  As on the board (R-dfr22-2-2)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The clearance kept between the closest pair of glyphs, labels included, when the board is scaled.</summary>
    private const double BoardClearance = 2 * P;

    /// <summary>The clearance <see cref="Compact"/> keeps between glyphs: twice <see cref="BoardClearance"/>, which leaves the
    /// router a lane past each part. With <see cref="JoinedReach"/> at 2 cells as well the sheet takes half the area but a
    /// bias chain's wire loops round its decoupling part and more nets end up joined by label (design note §7.3.1).</summary>
    private const double CompactClearance = 4 * P;

    /// <summary>How much of their board offset two instances that share a net keep through <see cref="Compact"/>: all of
    /// it up to this, so a part beside a junction stays beside it rather than level with what it hangs from.</summary>
    private const double JoinedReach = 6 * P;

    /// <summary>The longest a board's drawing is let grow: a pair of anchors almost on top of each other (a via beside
    /// its pad) would otherwise scale the sheet without limit. A pair still too close falls back, and is reported.</summary>
    private const double BoardMaxExtent = 40_000;

    /// <summary>
    /// R-dfr22-2-2 — "As on the board": every hinted instance where the board has it, as an <see cref="ExactPlacement"/>
    /// <see cref="Build"/> draws with <see cref="PlaceExact"/>, so a placement that would land on another falls back
    /// per instance exactly as a picture's does. One scale, the smallest that keeps the closest pair of glyphs
    /// (labels included, measured as drawn) apart, then each axis squeezed shut in board order (<see cref="Compact"/>);
    /// y flipped; the drawing framed on the hinted instances themselves, so a
    /// recognition of a selection is placed relative to that selection. A two-pin instance with a direction is turned to
    /// it, to the nearest quarter turn; anything else turns its pins towards what they connect to on the board. Nothing
    /// is wired here: the placement asks for <see cref="ExactPlacement.AutoRoute"/>.
    /// </summary>
    public static ExactPlacement BoardPlacement(TestBench tb, IReadOnlyDictionary<string, BoardHint> hints)
    {
        ArgumentNullException.ThrowIfNull(tb);
        ArgumentNullException.ThrowIfNull(hints);

        var items = new List<(Item It, BoardHint H)>();
        foreach (var inst in tb.Instances)
            if (hints.TryGetValue(inst.InstanceName, out var h) && MapInstance(inst, null, out _) is { Local.Length: > 0 } it)
                items.Add((it, h));
        if (items.Count == 0) return new ExactPlacement { AutoRoute = true };

        // Where each signal net's instances are on the board, so a pin can be turned towards what it joins.
        var onNet = new Dictionary<string, List<(Item It, BoardHint H)>>(StringComparer.Ordinal);
        foreach (var (it, h) in items)
            foreach (var n in it.SignalNets)
                (onNet.TryGetValue(n, out var l) ? l : onNet[n] = []).Add((it, h));
        foreach (var (it, h) in items)
        {
            if (h.AxisDeg is { } deg && it.Local.Length == 2) TurnTo(it, deg);
            else TurnTowards(it, h, onNet);
            BoxOf(it);
        }

        double scale = BoardScale(items);
        double[] xs = [.. items.Select(i => i.H.X * scale)], ys = [.. items.Select(i => -i.H.Y * scale)];
        double[] ws = [.. items.Select(i => i.It.Full.MaxX - i.It.Full.MinX)], hs = [.. items.Select(i => i.It.Full.MaxY - i.It.Full.MinY)];
        // Which axes each pair is apart along on the board — along one unless it is at least twice as far apart the other
        // way, so a diagonal pair is apart along both — and which pairs share a signal net.
        var apartX = new bool[items.Count, items.Count];
        var apartY = new bool[items.Count, items.Count];
        var joined = new bool[items.Count, items.Count];
        for (int i = 0; i < items.Count; i++)
            for (int j = 0; j < items.Count; j++)
            {
                if (i == j) continue;
                double dx = Math.Abs(items[i].H.X - items[j].H.X), dy = Math.Abs(items[i].H.Y - items[j].H.Y);
                apartX[i, j] = 2 * dx >= dy;
                apartY[i, j] = 2 * dy >= dx;
                joined[i, j] = items[i].It.SignalNets.Intersect(items[j].It.SignalNets).Any();
            }
        // Each axis squeezed against the board's own spread the other way, parting only the pairs the board has apart
        // along it, so neither axis is favoured and a part straight below another stays below it; then y once more against the
        // squeezed x, parting every pair that would overlap — the pass that guarantees no two glyphs do.
        var (sx, sy) = (Compact(xs, ws, ys, hs, apartX, joined), Compact(ys, hs, xs, ws, apartY, joined));
        (xs, ys) = (sx, Compact(sy, hs, sx, ws, null, joined, apartY));
        for (int k = 0; k < items.Count; k++)
        {
            var (it, _) = items[k];
            // The glyph's centre on the board point: the origin is offset by the glyph's own centre.
            double cx = xs[k], cy = ys[k];
            it.Comp.X = Snap(cx - (it.Box.MinX + it.Box.MaxX) / 2);
            it.Comp.Y = Snap(cy - (it.Box.MinY + it.Box.MaxY) / 2);
        }
        Straighten([.. items.Select(i => i.It)]);

        var placed = new Dictionary<string, ExactInstance>(StringComparer.Ordinal);
        foreach (var (it, _) in items)
            placed[it.Comp.InstanceName] = new ExactInstance(it.Comp.X, it.Comp.Y, it.Comp.Rotation, it.Comp.MirrorX);
        return new ExactPlacement { Instances = placed, AutoRoute = true };
    }

    /// <summary>
    /// One axis of the board squeezed shut (designer report, 2026-10-21: one close pair of parts sets the scale, and
    /// every long line and bare stretch of board grew with it until the sheet ran into <see cref="BoardMaxExtent"/>).
    /// Items are taken in their board order along the axis, and each goes as far back as it can: no further back than
    /// the one before it, so the board's order along the axis is kept, and <see cref="CompactClearance"/> clear of every earlier item it
    /// would overlap across the axis — of those <paramref name="parted"/> marks, or of all when it is null. An item
    /// <paramref name="joined"/> to an earlier one it is mostly apart from along this axis (<paramref name="apart"/>,
    /// else <paramref name="parted"/>) also stays a lead's length past it, so what is wired together keeps its
    /// direction and as much of its board offset as <see cref="JoinedReach"/> allows. Returns glyph centres from 0; <paramref name="along"/> holds the board centres, which give the order,
    /// and <paramref name="across"/> the current centres the other way.
    /// </summary>
    private static double[] Compact(double[] along, double[] size, double[] across, double[] acrossSize,
                                    bool[,]? parted, bool[,] joined, bool[,]? apart = null)
    {
        var order = Enumerable.Range(0, along.Length).OrderBy(k => along[k]).ToArray();
        var at = new double[along.Length];
        for (int n = 0; n < order.Length; n++)
        {
            int i = order[n];
            double pos = n == 0 ? 0 : at[order[n - 1]];
            for (int m = 0; m < n; m++)
            {
                int j = order[m];
                if (joined[i, j] && (apart ?? parted)![i, j] && along[i] > along[j])
                    pos = Math.Max(pos, at[j] + Math.Clamp(along[i] - along[j], 2 * P, JoinedReach));
                if (parted is not null && !parted[i, j]) continue;
                if (Math.Abs(across[i] - across[j]) >= (acrossSize[i] + acrossSize[j]) / 2 + CompactClearance) continue;
                pos = Math.Max(pos, at[j] + (size[i] + size[j]) / 2 + CompactClearance);
            }
            at[i] = pos;
        }
        return at;
    }

    /// <summary>
    /// What a scaled board leaves a cell or two off: the router reaches a pin only along its lead, so two pins that face
    /// each other a row apart are joined by a dog-leg or not at all, and a pin a row short of the wire it joins is reached
    /// by a loop. Two passes, a grid cell at a time: pins facing each other on one net are lined up (each instance moved
    /// once per axis, in the netlist's order, which walks the board from port 1); then a pin whose lead points at a
    /// pin of its net less than two cells away — four, when the two face each other off to one side and meet by a
    /// jog — has its instance moved back to make the room.
    /// </summary>
    private static void Straighten(List<Item> items)
    {
        var pins = items.SelectMany(it => Enumerable.Range(0, it.Local.Length)
                                                    .Where(k => it.Nets[k] != Ground)
                                                    .Select(k => (It: it, K: k))).ToList();
        var lockedX = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        var lockedY = new HashSet<Item>(ReferenceEqualityComparer.Instance);

        foreach (var net in pins.Select(p => p.It.Nets[p.K]).Distinct(StringComparer.Ordinal).ToList())
        {
            var on = pins.Where(p => p.It.Nets[p.K] == net).ToList();
            for (int i = 0; i < on.Count; i++)
                for (int j = i + 1; j < on.Count; j++)
                {
                    var (a, ka) = on[i];
                    var (b, kb) = on[j];
                    if (ReferenceEquals(a, b)) continue;
                    var (ux, uy) = Outward(a, ka);
                    var (vx, vy) = Outward(b, kb);
                    if (ux != -vx || uy != -vy) continue;   // not facing along one axis
                    var (pa, pb) = (a.Pin(ka), b.Pin(kb));
                    if ((pb.X - pa.X) * ux + (pb.Y - pa.Y) * uy < 2 * P) continue;
                    bool alongX = ux != 0;   // facing along x: lined up in y
                    double off = alongX ? pb.Y - pa.Y : pb.X - pa.X;
                    if (off == 0 || Math.Abs(off) > 2 * P) { Lock(a, b, alongX); continue; }
                    var locked = alongX ? lockedY : lockedX;
                    if (!locked.Contains(b)) Shift(b, alongX ? 0 : -off, alongX ? -off : 0);
                    else if (!locked.Contains(a)) Shift(a, alongX ? 0 : off, alongX ? off : 0);
                    Lock(a, b, alongX);
                }
        }

        foreach (var (it, k) in pins)
        {
            var (ux, uy) = Outward(it, k);
            var p = it.Pin(k);
            double short_ = 0;
            foreach (var (o, ko) in pins)
            {
                if (ReferenceEquals(o, it) || o.Nets[ko] != it.Nets[k]) continue;
                var at = o.Pin(ko);
                double d = (at.X - p.X) * ux + (at.Y - p.Y) * uy, side = Math.Abs((at.X - p.X) * uy - (at.Y - p.Y) * ux);
                // Two leads facing each other off to one side meet by a jog, which needs a row clear of both keep-outs.
                var (vx, vy) = Outward(o, ko);
                double need = vx == -ux && vy == -uy && side > 0 ? 4 * P : 2 * P;
                if (d > 0 && d < need) short_ = Math.Max(short_, need - d);
            }
            if (short_ > 0) Shift(it, -ux * short_, -uy * short_);
        }

        void Lock(Item a, Item b, bool alongX)
        {
            var locked = alongX ? lockedY : lockedX;
            locked.Add(a);
            locked.Add(b);
        }
        static void Shift(Item it, double dx, double dy)
        {
            it.Comp.X = Snap(it.Comp.X + dx);
            it.Comp.Y = Snap(it.Comp.Y + dy);
        }
    }

    /// <summary>A two-pin item turned so pin 1 → pin 2 runs along <paramref name="deg"/> (board axes, y up), to the
    /// nearest quarter turn — R0 on a tie.</summary>
    private static void TurnTo(Item it, double deg)
    {
        double rad = deg * Math.PI / 180;
        var (tx, ty) = (Math.Cos(rad), -Math.Sin(rad));   // the schematic's y runs down
        SymbolRotation best = SymbolRotation.R0;
        double bestScore = double.MinValue;
        foreach (var rot in new[] { SymbolRotation.R0, SymbolRotation.R90, SymbolRotation.R180, SymbolRotation.R270 })
        {
            it.Comp.Rotation = rot;
            it.Comp.MirrorX = false;
            var (ax, ay) = it.Offset(0);
            var (bx, by) = it.Offset(1);
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            double score = len > 0 ? ((bx - ax) * tx + (by - ay) * ty) / len : 0;
            if (score > bestScore + 1e-9) { bestScore = score; best = rot; }
        }
        it.Comp.Rotation = best;
    }

    /// <summary>An item with no direction of its own (a port, a via, a tee, a bend) turned — and mirrored, where that
    /// reads better — so each signal pin points at the board position of what its net joins. R0 on a tie.</summary>
    private static void TurnTowards(Item it, BoardHint h, Dictionary<string, List<(Item It, BoardHint H)>> onNet)
    {
        var wanted = new List<(int Pin, double X, double Y)>();
        for (int k = 0; k < it.Nets.Length; k++)
        {
            if (it.Nets[k] == Ground || !onNet.TryGetValue(it.Nets[k], out var others)) continue;
            var far = others.Where(o => !ReferenceEquals(o.It, it)).ToList();
            if (far.Count == 0) continue;
            double dx = far.Average(o => (double)o.H.X) - h.X, dy = -(far.Average(o => (double)o.H.Y) - h.Y);
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0) wanted.Add((k, dx / len, dy / len));
        }

        (SymbolRotation Rotation, bool Mirror) best = (SymbolRotation.R0, false);
        double bestScore = double.MinValue;
        foreach (bool mirror in new[] { false, true })
            foreach (var rot in new[] { SymbolRotation.R0, SymbolRotation.R90, SymbolRotation.R180, SymbolRotation.R270 })
            {
                it.Comp.Rotation = rot;
                it.Comp.MirrorX = mirror;
                var pins = Enumerable.Range(0, it.Local.Length).Select(it.Offset).ToList();
                double mx = pins.Average(p => p.X), my = pins.Average(p => p.Y);
                double score = 0;
                foreach (var (k, wx, wy) in wanted)
                {
                    double px = pins[k].X - mx, py = pins[k].Y - my, len = Math.Sqrt(px * px + py * py);
                    if (len > 0) score += (px * wx + py * wy) / len;
                }
                if (score > bestScore + 1e-9) { bestScore = score; best = (rot, mirror); }
            }
        it.Comp.Rotation = best.Rotation;
        it.Comp.MirrorX = best.Mirror;
    }

    /// <summary>Schematic units per board unit: the least that puts every pair of glyphs — labels included, centred on
    /// their board points — a clearance apart along x or along y, capped at <see cref="BoardMaxExtent"/>.</summary>
    private static double BoardScale(List<(Item It, BoardHint H)> items)
    {
        double scale = 0;
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
            {
                var (a, ha) = items[i];
                var (b, hb) = items[j];
                double dx = Math.Abs(ha.X - hb.X), dy = Math.Abs(ha.Y - hb.Y);
                double needX = (a.Full.MaxX - a.Full.MinX + b.Full.MaxX - b.Full.MinX) / 2 + BoardClearance;
                double needY = (a.Full.MaxY - a.Full.MinY + b.Full.MaxY - b.Full.MinY) / 2 + BoardClearance;
                double pair = Math.Min(dx > 0 ? needX / dx : double.PositiveInfinity, dy > 0 ? needY / dy : double.PositiveInfinity);
                if (double.IsFinite(pair)) scale = Math.Max(scale, pair);
            }
        double extent = Math.Max(items.Max(i => i.H.X) - items.Min(i => i.H.X), items.Max(i => i.H.Y) - items.Min(i => i.H.Y));
        if (extent > 0) scale = scale > 0 ? Math.Min(scale, BoardMaxExtent / extent) : BoardMaxExtent / extent / 4;
        return scale > 0 ? scale : 1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Netlist line → component
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The component a netlist line draws as, with its parameters and the net on each pin — or null
    /// with the reason. The kind is the one whose <see cref="ComponentTypeRegistry.EngineReference"/>
    /// IS the line's type, so this is the inverse of the extractor's own mapping and not a second table.
    /// </summary>
    private static Item? MapInstance(Instance inst, string? schematicDirectory, out string? why)
    {
        why = null;
        string reference = inst.Reference;

        if (reference.Equals("WBond", StringComparison.OrdinalIgnoreCase))
        {
            why = $"{inst.InstanceName}: a wirebond's pins come from the design file it references, not "
                + "from a built-in symbol, so it cannot be drawn from its netlist line.";
            return null;
        }

        var candidates = Enum.GetValues<SymbolKind>()
            .Where(k => !NotDrawable.Contains(k)
                        && string.Equals(ComponentTypeRegistry.EngineReference(k), reference,
                                         StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The multi-tone spellings are one tile each; the extractor picks them off NumFreqs.
        if (reference.Equals("V_nTone", StringComparison.OrdinalIgnoreCase)) candidates = [SymbolKind.ToneSource];
        if (reference.Equals("I_nTone", StringComparison.OrdinalIgnoreCase)) candidates = [SymbolKind.CurrentToneSource];

        // A short code the netlist also accepts (`Term` for a port).
        if (candidates.Count == 0 && ComponentTypeRegistry.TryParseCode(reference, out var coded, out _)
            && !NotDrawable.Contains(coded))
            candidates = [coded];

        if (candidates.Count == 0)
        {
            why = $"{inst.InstanceName}: '{reference}' has no schematic symbol, so it cannot be drawn.";
            return null;
        }

        candidates = [.. candidates.OrderBy(k => Array.IndexOf(Preferred, k) is var i and >= 0 ? i : int.MaxValue)];

        string? lastWhy = null;
        foreach (var kind in candidates)
        {
            var comp = NewComponent(inst, kind, schematicDirectory);
            if (PinNets(comp, inst, out lastWhy) is { } nets)
                return new Item { Comp = comp, Nets = nets, Local = LocalPins(comp) };
        }

        why = $"{inst.InstanceName}: {lastWhy}";
        return null;
    }

    /// <summary>A cell instance drawn as its cell's symbol (brief-schimp-1 R-si1-4): the placeholder kind interactive
    /// placement uses, with the <c>CellRef</c> deciding the artwork, and the pins the caller read off that symbol.</summary>
    private static Item CellItem(Instance inst, ExactCell cell)
    {
        var comp = new EditableComponent
        {
            InstanceName = inst.InstanceName, Symbol = SymbolKind.Generic, CellRef = cell.CellRef,
            ShowTypeLabel = true, ShowInstanceName = true,
        };
        // brief-icp-9 — the instance's own overrides are its parameters, as the line states them.
        foreach (var o in inst.Overrides)
            comp.Parameters.Add(new EditableParameter { Name = o.Name, Expression = o.Expression, Unit = o.Unit ?? "" });
        return new Item { Comp = comp, Nets = [.. inst.NetBindings], Local = [.. cell.Pins] };
    }

    /// <summary>A value longer than this is left off the drawing — it is still the component's
    /// parameter, it simply does not fit beside the symbol.</summary>
    private const int LongestShownValue = 22;

    private static EditableComponent NewComponent(Instance inst, SymbolKind kind, string? schematicDirectory)
    {
        var info = ComponentTypeRegistry.Get(kind);
        var comp = new EditableComponent
        {
            InstanceName     = inst.InstanceName,
            Symbol           = kind,
            ShowTypeLabel    = info.DefaultShowTypeLabel,
            ShowInstanceName = info.DefaultShowInstanceName,
        };

        // Parameters FIRST: a variadic symbol reads its own pin count off one of them.
        var template = kind == SymbolKind.Tline && inst.Overrides.Any(o => o.Name == "L")
            ? TlinEntryConversion.PhysicalTemplate
            : ComponentTypeRegistry.DefaultParameters(kind, 0);
        foreach (var o in inst.Overrides)
        {
            var t = template.FirstOrDefault(d => d.Name == o.Name);
            string expr = o.Expression;
            // Every part's File, not only an SnP block's: the reader hands a ComposedFET's .cmodel back absolute too, and
            // the extraction resolves each one against the same base.
            if (o.Name.Equals("File", StringComparison.OrdinalIgnoreCase))
                expr = RelativeFile(expr, FileBase(schematicDirectory));
            // A layer NAME is stored bare in a schematic — the extraction reads it raw and quotes it as it writes the
            // .cnl — so the .cnl's quotes are its line syntax, not part of the name ("Top Copper (1 oz)").
            if (IsLayerName(o.Name) && expr.Length >= 2 && expr[0] == '"' && expr[^1] == '"')
                expr = expr[1..^1];
            // A footprint is drawn by its own third label as the case code (FootprintLabelText), never as a row.
            bool show = o.Name.Equals(ArtworkParameters.FootprintName, StringComparison.OrdinalIgnoreCase) ? false
                      : t.Name is not null ? t.ShowOnSchematic
                      : inst.Overrides.Count(x => !x.Name.Equals(ArtworkParameters.FootprintName, StringComparison.OrdinalIgnoreCase)) <= 2;
            comp.Parameters.Add(new EditableParameter
            {
                Name            = o.Name,
                Expression      = expr,
                Unit            = o.Unit ?? "",
                // The palette's own choice of what a placed part shows; a part the palette says
                // nothing about shows its values only when there are few enough to read.
                ShowOnSchematic = show && o.Name.Length + expr.Length + (o.Unit?.Length ?? 0) <= LongestShownValue,
                Dimension       = t.Name is not null ? t.Dimension : UnitDimension.None,
            });
        }

        // An EM Field Data line states no port count. The record does, when it can be read; otherwise its nets are all there is.
        if (kind == SymbolKind.EmFieldData)
            comp.Parameters.Insert(0, new EditableParameter
            {
                Name = "NumPorts", ShowOnSchematic = false,
                Expression = (CircuitRF.Core.Devices.EmFieldDataRecord.ReadPortNames(FileOf(inst))?.Count ?? inst.NetBindings.Count)
                             .ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        if (inst.RefNetBinding is not null && kind is SymbolKind.Snp or SymbolKind.Deembed
            && !comp.Parameters.Any(p => p.Name == "RefNode"))
            comp.Parameters.Add(new EditableParameter { Name = "RefNode", Expression = "true", ShowOnSchematic = false });

        return comp;
    }

    private static string? FileOf(Instance inst)
        => inst.Overrides.FirstOrDefault(o => o.Name.Equals("File", StringComparison.OrdinalIgnoreCase))?.Expression.Trim('"');

    private static bool IsLayerName(string parameter) =>
        parameter is "SignalLayer" or "GroundReference" || ViaSubstrateInjection.LayerParams.Contains(parameter)
        || parameter is TestPadInjection.PadLayerParam or TestPadInjection.ViaFromParam or TestPadInjection.ClearLayersParam
        || parameter == MmicPassiveInjection.FilmParam;

    /// <summary>The folder a schematic in <paramref name="schematicDirectory"/> resolves a <c>File</c> against —
    /// <see cref="SchematicCircuit.ReferenceBaseOf"/>'s rule, taken from the folder because the drawing is not
    /// written yet. Its own folder would be wrong inside a workspace: a test bench in <c>&lt;cell&gt;/schematic/</c>
    /// stored <c>../../results/…</c>, which the extraction then resolved above the workspace.</summary>
    private static string? FileBase(string? schematicDirectory)
    {
        if (schematicDirectory is null) return null;
        string own = System.IO.Path.GetFullPath(schematicDirectory);
        return CircuitRF.Design.Workspace.WorkspaceRootFinder.WorkspaceDirOf(own) ?? own;
    }

    /// <summary>An analysis whose file paths the reader made absolute, with them relative to <paramref name="dir"/>; any
    /// other analysis as it is. Rebuilt through the .csch's own analysis record, which is how the drawing stores it.</summary>
    private static Analysis RebasePaths(Analysis a, string? dir)
    {
        if (dir is null) return a;
        string? Rel(string? p) => p is { Length: > 0 } && System.IO.Path.IsPathRooted(p)
            ? System.IO.Path.GetRelativePath(dir, p).Replace('\\', '/') : p;
        switch (a)
        {
            case LoadpullAnalysis lp when Rel(lp.GridPath) != lp.GridPath:
            {
                var dto = AnalysisSerialization.ToDto(a);
                dto.LpGridPath = Rel(lp.GridPath);
                return AnalysisSerialization.FromDto(dto) ?? a;
            }
            case LoadpullPursuitAnalysis lpp when Rel(lpp.OutputGridPath) != lpp.OutputGridPath:
            {
                var dto = AnalysisSerialization.ToDto(a);
                dto.LppOutputGridPath = Rel(lpp.OutputGridPath);
                return AnalysisSerialization.FromDto(dto) ?? a;
            }
            default:
                return a;
        }
    }

    /// <summary>A quoted absolute path, relative to <paramref name="dir"/> — what a schematic stores,
    /// and what the extraction's own reader resolves back to the same absolute path.</summary>
    private static string RelativeFile(string expr, string? dir)
    {
        if (dir is null || expr.Length < 2 || expr[0] != '"' || expr[^1] != '"') return expr;
        string path = expr[1..^1];
        if (!System.IO.Path.IsPathRooted(path)) return expr;
        return "\"" + System.IO.Path.GetRelativePath(dir, path).Replace('\\', '/') + "\"";
    }

    private static (float X, float Y)[] LocalPins(EditableComponent comp)
        => [.. (comp.IsSnpBox ? comp.GetEffectiveSnpPortDefs() : SymbolPortDefs.For(comp.Symbol, comp.PortCount))
               .Select(p => (p.LocalX, p.LocalY))];

    /// <summary>
    /// The net on each of <paramref name="comp"/>'s pins, or null when its symbol cannot carry the
    /// line's nets — the exact inverse of each special case in <c>NetExtractor.EmitInstance</c>.
    /// </summary>
    private static string[]? PinNets(EditableComponent comp, Instance inst, out string? why)
    {
        why = null;
        var nets = inst.NetBindings;
        var kind = comp.Symbol;

        // A variadic box whose line left its width to be inferred from the nets: state it, because
        // the symbol has no other way to know how many pins to draw.
        if (ComponentTypeRegistry.PortCountParameter(kind) is { } countParam
            && kind is not (SymbolKind.Switch or SymbolKind.SwitchD or SymbolKind.WBond)
            && !comp.Parameters.Any(p => p.Name == countParam))
        {
            int n = kind is SymbolKind.Sdd or SymbolKind.ZPort ? nets.Count / 2
                  : kind is SymbolKind.Combiner ? nets.Count / 2 - 1
                  : nets.Count;
            if (n >= 1)
                comp.Parameters.Add(new EditableParameter
                {
                    Name = countParam, Expression = n.ToString(CultureInfo.InvariantCulture), ShowOnSchematic = false,
                });
        }

        int pins = LocalPins(comp).Length;

        // One pin on the symbol, a second net hard-wired to ground by the extractor.
        if (kind is SymbolKind.TermG or SymbolKind.ViaGnd
                 or SymbolKind.Tuner or SymbolKind.LoadTuner or SymbolKind.SourceTuner)
        {
            if (nets.Count == 2 && nets[1] == Ground) return [nets[0]];
            why = $"'{inst.Reference}' draws one pin with its reference on ground; this line binds "
                + $"{string.Join(" ", nets)}.";
            return null;
        }

        // N pins for N ports, every port's minus on ground.
        if (NetExtractor.GroundReferencedPortBlocks.Contains(kind))
        {
            if (nets.Count == 2 * pins && Enumerable.Range(0, pins).All(i => nets[2 * i + 1] == Ground))
                return [.. Enumerable.Range(0, pins).Select(i => nets[2 * i])];
            why = $"'{inst.Reference}' as drawn has {pins} pin(s), each port's minus on ground; this "
                + $"line binds {nets.Count} net(s).";
            return null;
        }

        if (kind is SymbolKind.Snp or SymbolKind.Deembed && inst.RefNetBinding is { } refNet)
        {
            if (nets.Count + 1 == pins) return [.. nets, refNet];
        }
        else if (nets.Count == pins) return [.. nets];
        // An EM Field Data line may spell its ground reference as a trailing net, and the block draws no reference pin.
        else if (kind == SymbolKind.EmFieldData && nets.Count == pins + 1 && nets[pins] == Ground) return [.. nets.Take(pins)];

        why = $"'{inst.Reference}' binds {nets.Count} net(s) and its symbol has {pins} pin(s).";
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Placement
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lays every item down, and returns them in the order the router should see them — path first,
    /// so each main-line net is seeded by a path pin and drawn as one straight run that everything
    /// else then Ts onto.
    /// </summary>
    private static List<Item> Place(List<Item> items, List<string> notes,
                                    IReadOnlyDictionary<string, (long X, long Y)>? hints = null, bool artworkSides = true,
                                    ExactPlacement? exact = null, List<string>? fallbacks = null)
    {
        var order = new List<Item>();
        if (items.Count == 0) return order;

        var withPins = items.Where(i => i.Local.Length > 0).ToList();

        // ── As a picture drew it (R-im10-4): the hinted ones where they are, the rest arranged below ──
        if (exact is not null) PlaceExact(withPins, exact, order, fallbacks ?? []);

        // ── The signal path ──────────────────────────────────────────────────
        var (path, start, end) = exact is not null ? (new List<Step>(), (Item?)null, (Item?)null) : FindPath(withPins);

        var pathNets = new List<string>();
        var junction = new Dictionary<string, double>(StringComparer.Ordinal);   // net → main-line x

        var hangers = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        bool IsHanger(Item it)
            => !it.Placed && it.Local.Length is 1 or 2 && it.SignalNets.Count() == 1;

        if (path.Count > 0 || start is not null)
        {
            string firstNet = path.Count > 0 ? path[0].In : start!.SignalNets.First();
            pathNets.Add(firstNet);
            foreach (var step in path) pathNets.Add(step.Out);
            foreach (var step in path) step.Item.Placed = true;

            foreach (var n in pathNets) hangers[n] = [];
            foreach (var it in withPins)
                if (IsHanger(it) && hangers.TryGetValue(it.SignalNets.First(), out var list)) list.Add(it);

            // The end fixtures sit at the very ends of their nets' rows, so the main line runs
            // from one port to the other.
            void MoveTo(Item? it, bool first)
            {
                if (it is null) return;
                foreach (var l in hangers.Values) l.Remove(it);
                string n = it.SignalNets.First();
                if (!hangers.TryGetValue(n, out var list)) return;
                if (first) list.Insert(0, it); else list.Add(it);
            }
            MoveTo(start, first: true);
            if (end is not null && !ReferenceEquals(end, start)) MoveTo(end, first: false);

            // With artwork hints (R-as6-3): each net's hangers in the order the artwork runs, and a two-pin
            // hanger whose copper lies on the left of the artwork's travel drawn above the line.
            var above = new HashSet<Item>(ReferenceEqualityComparer.Instance);
            if (hints is not null)
                for (int k = 0; k < pathNets.Count; k++)
                    OrderByArtwork(pathNets[k], k, hangers[pathNets[k]], path, start, end, hints, above);
            if (!artworkSides) above.Clear();

            // `cursor` is where the main line has got to: the last element's exit, or 0.
            double cursor = 0;
            for (int k = 0; k < pathNets.Count; k++)
            {
                string net = pathNets[k];
                var row = hangers[net];
                double first = k == 0 ? 0 : cursor + Lead;
                double x = first, last = first;
                for (int h = 0; h < row.Count; h++)
                {
                    var it = row[h];
                    bool up = above.Contains(it);
                    PlaceHanger(it, net, x, HangerDrop, up);
                    if (h == 0 && k > 0)
                    {
                        // Clear of the previous element's labels as well as its glyph.
                        double shift = Snap(Math.Max(0, cursor - P - (it.Comp.X + it.Full.MinX)));
                        if (shift > 0) { PlaceHanger(it, net, x + shift, HangerDrop, up); first += shift; }
                    }
                    last = it.Pin(Array.IndexOf(it.Nets, net)).X;
                    order.Add(it);
                    if (h + 1 < row.Count)
                    {
                        var nx = row[h + 1];
                        PlaceHanger(nx, net, 0, HangerDrop, above.Contains(nx));   // orient, to measure it
                        // With its pin at x = 0, nx's left edge is at nx.Comp.X + nx.Full.MinX.
                        double clear = it.Comp.X + it.Full.MaxX + 2 * P - (nx.Comp.X + nx.Full.MinX);
                        x = Math.Max(last + HangerPitch, Snap(clear));
                    }
                }

                double entry = row.Count > 0 ? last + Lead : k == 0 ? 0 : cursor + 2 * Lead;
                junction[net] = row.Count > 0 ? (first + last) / 2 : Snap((cursor + entry) / 2);

                if (k < path.Count) cursor = PlaceOnPath(path[k], entry);
            }
            // Path elements go to the router FIRST, then the two end fixtures, so each end net is
            // drawn as the line running on to its port and the other hangers T onto that.
            order.RemoveAll(i => ReferenceEquals(i, start) || ReferenceEquals(i, end));
            var head = path.Select(s => s.Item).ToList();
            if (start is not null) head.Add(start);
            if (end is not null && !ReferenceEquals(end, start)) head.Add(end);
            order.InsertRange(0, head);
        }

        // ── Elements bridging two path nets: above the line ────────────────
        var aboveRows = new List<List<(double L, double R)>>();
        foreach (var it in withPins)
        {
            if (it.Placed || it.Local.Length != 2) continue;
            var sn = it.SignalNets.ToList();
            if (sn.Count != 2 || !junction.ContainsKey(sn[0]) || !junction.ContainsKey(sn[1])) continue;

            string left  = junction[sn[0]] <= junction[sn[1]] ? sn[0] : sn[1];
            int leftPin  = Array.IndexOf(it.Nets, left);
            Orient(it, leftPin, 1 - leftPin, horizontal: true);
            double cx = Snap((junction[sn[0]] + junction[sn[1]]) / 2
                             - (it.Offset(leftPin).X + it.Offset(1 - leftPin).X) / 2);
            double l = cx + it.Full.MinX - P, rr = cx + it.Full.MaxX + P;

            int r = 0;
            while (true)
            {
                if (r == aboveRows.Count) aboveRows.Add([]);
                if (!aboveRows[r].Any(s => s.L < rr && l < s.R)) break;
                r++;
            }
            aboveRows[r].Add((l, rr));
            it.Comp.X = cx;
            it.Comp.Y = Snap(-BridgeRise - r * BridgeRowPitch - it.Offset(leftPin).Y);
            it.Placed = true;
            order.Add(it);
        }

        // ── Everything else: rows below, near what it connects to ──────────
        double rowsTop = Snap(order.Count == 0 ? 0
            : order.Max(i => i.Comp.Y + i.Full.MaxY + (i.Nets.Contains(Ground) ? 300 : 0)) + RowGap);

        var anchor = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var anchorTop = new Dictionary<string, double>(StringComparer.Ordinal);   // net → its highest terminal
        void Anchor(Item it)
        {
            for (int k = 0; k < it.Nets.Length; k++)
            {
                if (it.Nets[k] == Ground) continue;
                if (!anchor.TryGetValue(it.Nets[k], out var l)) anchor[it.Nets[k]] = l = [];
                var (px, py) = it.Pin(k);
                l.Add(px);
                anchorTop[it.Nets[k]] = anchorTop.TryGetValue(it.Nets[k], out double t) ? Math.Min(t, py) : py;
            }
        }
        foreach (var it in order) Anchor(it);

        var occupied = new List<(int Row, double L, double R)>();
        double rightmost = order.Count == 0 ? 0 : order.Max(i => i.Comp.X + i.Full.MaxX);

        var rest = withPins.Where(i => !i.Placed).ToList();
        while (rest.Count > 0)
        {
            // Take the next one touching something already placed, so a chain grows outwards from
            // the path rather than being scattered in file order.
            // A link that continues a chain goes before what merely hangs from it, so the chain takes the column under
            // its predecessor and a shunt part is drawn beside the chain rather than the chain beside it (R-dfr22-2-3).
            var next = rest.FirstOrDefault(i => i.Local.Length == 2 && i.SignalNets.Count() == 2 && i.SignalNets.Count(anchor.ContainsKey) == 1)
                    ?? rest.FirstOrDefault(i => i.SignalNets.Any(anchor.ContainsKey)) ?? rest[0];
            rest.Remove(next);

            OrientFree(next, anchor);
            var xs = next.SignalNets.Where(anchor.ContainsKey).Select(n => anchor[n].Average()).ToList();
            double want = xs.Count > 0 ? xs.Average() : rightmost + 2 * Slot;

            // A two-pin part joining a net that so far ends on ONE pin continues that pin's chain: straight below it, rows
            // lower if need be, never beside it. Beside, its upper pin sat barely below the one it joins, and the wire —
            // which reaches each pin along its lead — looped round to come at it from above (R-dfr22-2-3).
            var joined = next.SignalNets.Where(anchor.ContainsKey).ToList();
            bool chain = next.Local.Length == 2 && joined.Count == 1 && anchor[joined[0]].Count == 1;
            if (chain) want = anchor[joined[0]][0] - next.Offset(Array.IndexOf(next.Nets, joined[0])).X;
            double sideways = chain ? 10 : 1;

            double left = next.Full.MinX - 2 * P, right = next.Full.MaxX + 2 * P;
            double height = next.Full.MaxY - next.Full.MinY + (next.Nets.Contains(Ground) ? 300 : 0);
            int rowsNeeded = Math.Max(1, (int)Math.Ceiling((height + 2 * P) / RowPitchOf()));

            // A chain grows DOWNWARDS: a part hangs below the nets it connects to, so the wire to
            // each comes from above rather than looping round from the far side.
            double below = next.SignalNets.Where(anchorTop.ContainsKey)
                               .Select(n => anchorTop[n] + 2 * P).DefaultIfEmpty(rowsTop).Max();
            int firstRow = Math.Max(0, (int)Math.Ceiling((below - rowsTop) / RowPitchOf()));

            // An upright part drawn BESIDE the pins of the net above it is reached by a wire that leaves that net
            // sideways: its upper pin must sit far enough down for that wire to run clear of both glyphs and come at the
            // pin along its lead, or it loops round from above (R-dfr22-2-3).
            int upper = next.Local.Length == 2 ? Enumerable.Range(0, 2).Where(k => anchor.ContainsKey(next.Nets[k]))
                                                     .OrderBy(k => next.Offset(k).Y).DefaultIfEmpty(-1).First() : -1;

            double bestCost = double.MaxValue, bestX = want; int bestRow = firstRow;
            for (int row = firstRow; row < firstRow + 40; row++)
            {
                for (int s = 0; s < 80; s++)
                {
                    foreach (int sign in s == 0 ? new[] { 1 } : new[] { 1, -1 })
                    {
                        double x = Snap(want + sign * s * Slot);
                        bool clash = occupied.Any(o => o.Row >= row && o.Row < row + rowsNeeded
                                                       && o.L < x + right && x + left < o.R);
                        if (clash) continue;
                        if (upper >= 0)
                        {
                            string net = next.Nets[upper];
                            double pinX = x + next.Offset(upper).X;
                            double pinY = Snap(rowsTop + row * RowPitchOf() - next.Full.MinY) + next.Offset(upper).Y;
                            if (!anchor[net].Any(ax => Math.Abs(ax - pinX) < 1) && pinY < anchorTop[net] + 4 * P) continue;
                        }
                        double cost = Math.Abs(x - want) / Slot * sideways + (row - firstRow) * 3;
                        if (cost < bestCost) { bestCost = cost; bestX = x; bestRow = row; }
                    }
                }
                if (bestCost <= (row - firstRow + 1) * 3) break;   // no later row can beat it
            }

            next.Comp.X = bestX;
            next.Comp.Y = Snap(rowsTop + bestRow * RowPitchOf() - next.Full.MinY);
            for (int r = bestRow; r < bestRow + rowsNeeded; r++) occupied.Add((r, bestX + left, bestX + right));
            next.Placed = true;
            order.Add(next);
            Anchor(next);
            rightmost = Math.Max(rightmost, next.Comp.X + next.Full.MaxX);
        }

        // Components with no pins (a mutual coupling names its inductors, not nets): along the top.
        double tx = 0;
        foreach (var it in items.Where(i => i.Local.Length == 0))
        {
            BoxOf(it);
            it.Comp.X = Snap(tx - it.Box.MinX);
            it.Comp.Y = Snap(-BridgeRise - aboveRows.Count * BridgeRowPitch - BridgeRowPitch - it.Full.MaxY);
            tx = it.Comp.X + it.Box.MaxX + 2 * Slot;
            it.Placed = true;
        }

        return order;

        static double RowPitchOf() => 600;
    }

    private sealed record Step(Item Item, int InPin, int OutPin, string In, string Out);

    /// <summary>
    /// The main line: the shortest chain of elements from the first port to the second, or — with
    /// fewer than two ports — between the two drive/termination fixtures furthest apart.
    /// </summary>
    private static (List<Step> Path, Item? Start, Item? End) FindPath(List<Item> items)
    {
        var bridges = items.Where(i => i.SignalNets.Count() >= 2).ToList();

        var ports = items.Where(i => i.Comp.Symbol is SymbolKind.Term or SymbolKind.TermG && i.SignalNets.Count() == 1)
                         .OrderBy(i => NumOf(i.Comp))
                         .ToList();

        List<Item> ends;
        if (ports.Count >= 2) ends = ports;
        else
        {
            ends = [.. items.Where(i => i.SignalNets.Count() == 1 && Fixtures.Contains(i.Comp.Symbol))];
            if (ends.Count < 2) ends = [.. items.Where(i => i.SignalNets.Count() == 1)];
        }

        (Item? A, Item? B, List<Step>? Path) best = (null, null, null);
        if (ports.Count >= 2)
        {
            var p = Bfs(bridges, ports[0].SignalNets.First(), ports[1].SignalNets.First());
            best = (ports[0], ports[1], p);
        }
        else
        {
            for (int a = 0; a < ends.Count; a++)
                for (int b = a + 1; b < ends.Count; b++)
                {
                    var p = Bfs(bridges, ends[a].SignalNets.First(), ends[b].SignalNets.First());
                    if (p is not null && (best.Path is null || p.Count > best.Path.Count))
                        best = (ends[a], ends[b], p);
                }
        }

        if (best.Path is not null) return (best.Path, best.A, best.B);

        // No two ends connect: the longest chain out of whatever there is, so there is still a line.
        if (ends.Count > 0) return ([], ends[0], null);
        if (bridges.Count == 0) return ([], null, null);
        string s = bridges[0].SignalNets.First();
        var far = Farthest(bridges, s);
        return (Bfs(bridges, s, far) ?? [], null, null);
    }

    private static int NumOf(EditableComponent c)
        => int.TryParse(c.Parameters.FirstOrDefault(p => p.Name == "Num")?.Expression, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;

    private static List<Step>? Bfs(List<Item> bridges, string from, string to)
    {
        if (from == to) return [];
        var prev = new Dictionary<string, Step>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            string n = queue.Dequeue();
            foreach (var it in bridges)
            {
                int inPin = Array.IndexOf(it.Nets, n);
                if (inPin < 0) continue;
                for (int k = 0; k < it.Nets.Length; k++)
                {
                    string m = it.Nets[k];
                    if (m == Ground || m == n || !seen.Add(m)) continue;
                    prev[m] = new Step(it, inPin, k, n, m);
                    if (m == to)
                    {
                        var path = new List<Step>();
                        for (string c = to; c != from; c = prev[c].In) path.Add(prev[c]);
                        path.Reverse();
                        // An element may not appear twice on one line.
                        return path.Select(p => p.Item).Distinct().Count() == path.Count ? path : null;
                    }
                    queue.Enqueue(m);
                }
            }
        }
        return null;
    }

    private static string Farthest(List<Item> bridges, string from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        string last = from;
        while (queue.Count > 0)
        {
            last = queue.Dequeue();
            foreach (var it in bridges.Where(i => i.Nets.Contains(last)))
                foreach (var m in it.Nets)
                    if (m != Ground && seen.Add(m)) queue.Enqueue(m);
        }
        return last;
    }

    /// <summary>Places a path element with its entry pin at <paramref name="entryX"/> on the main
    /// line; returns the x the line continues from.</summary>
    private static double PlaceOnPath(Step step, double entryX)
    {
        var it = step.Item;
        Orient(it, step.InPin, step.OutPin, horizontal: true);
        var (ix, iy) = it.Offset(step.InPin);
        it.Comp.X = Snap(entryX - ix);
        it.Comp.Y = Snap(-iy);
        double rightmost = it.Comp.X + Math.Max(it.Box.MaxX, Enumerable.Range(0, it.Local.Length).Max(k => it.Offset(k).X));
        return Math.Max(it.Pin(step.OutPin).X, rightmost - P);
    }

    /// <summary>A hanging element: its signal pin uppermost at (x, <paramref name="drop"/>) — or, drawn
    /// <paramref name="up"/>, lowermost at (x, −<paramref name="drop"/>), standing above the line.</summary>
    /// <summary>
    /// R-im10-4: every item with an exact hint at its snapped position and orientation, in netlist order — unless one
    /// of its pins would land on another's pin of a different net, inside another's glyph, or its glyph over another's
    /// pin. Such an item is left for the automatic arrangement, and named.
    /// </summary>
    private static void PlaceExact(List<Item> withPins, ExactPlacement exact, List<Item> order, List<string> fallbacks)
    {
        var pins = new Dictionary<(long, long), string>();
        var glyphs = new List<(double L, double T, double R, double B)>();
        // Unsnapped (R-si1-6), a pin is where it is: keyed to the unit, so two pins 50 apart are two pins.
        (long, long) PinKey((double X, double Y) p) => exact.SnapToGrid ? Key(p) : ((long)Math.Round(p.X), (long)Math.Round(p.Y));
        (double X, double Y) PinAt((long, long) k) => exact.SnapToGrid ? (k.Item1 * P, k.Item2 * P) : (k.Item1, k.Item2);
        foreach (var it in withPins)
        {
            if (!exact.Instances.TryGetValue(it.Comp.InstanceName, out var e)) continue;
            it.Comp.Rotation = e.Rotation;
            it.Comp.MirrorX = e.MirrorX;
            it.Comp.X = exact.SnapToGrid ? Snap(e.X) : e.X;
            it.Comp.Y = exact.SnapToGrid ? Snap(e.Y) : e.Y;
            BoxOf(it);
            var own = Enumerable.Range(0, it.Local.Length).Select(k => (Key: PinKey(it.Pin(k)), Net: it.Nets[k], At: it.Pin(k))).ToList();
            var glyph = Inner(it);
            bool clash = own.Any(p => pins.TryGetValue(p.Key, out var n) && n != p.Net)
                      || !exact.KeepOverlappingGlyphs
                         && (glyphs.Any(g => g.L < glyph.R && glyph.L < g.R && g.T < glyph.B && glyph.T < g.B)
                             || own.Any(p => glyphs.Any(g => Inside(g, p.At)))
                             || pins.Keys.Any(k => Inside(glyph, PinAt(k))));
            if (clash)
            {
                it.Comp.Rotation = SymbolRotation.R0;
                it.Comp.MirrorX = false;
                it.Comp.X = it.Comp.Y = 0;
                BoxOf(it);
                fallbacks.Add(it.Comp.InstanceName);
                continue;
            }
            foreach (var p in own) pins.TryAdd(p.Key, p.Net);
            glyphs.Add(glyph);
            it.Placed = true;
            order.Add(it);
        }

        // The glyph's box a quarter cell inside its edge: two symbols drawn touching are not on top of each other.
        static (double L, double T, double R, double B) Inner(Item it) =>
            (it.Comp.X + it.Box.MinX + P / 4, it.Comp.Y + it.Box.MinY + P / 4,
             it.Comp.X + it.Box.MaxX - P / 4, it.Comp.Y + it.Box.MaxY - P / 4);
        static bool Inside((double L, double T, double R, double B) g, (double X, double Y) p) =>
            p.X > g.L && p.X < g.R && p.Y > g.T && p.Y < g.B;
    }

    private static void PlaceHanger(Item it, string net, double x, double drop, bool up = false)
    {
        int top = Array.IndexOf(it.Nets, net);
        int other = it.Local.Length == 2 ? 1 - top : -1;
        if (up && other >= 0)
        {
            Orient(it, other, top, horizontal: false);
            var (ux, uy) = it.Offset(top);
            it.Comp.X = Snap(x - ux);
            it.Comp.Y = Snap(-drop - uy);
            it.Placed = true;
            return;
        }
        Orient(it, top, other, horizontal: false);
        var (ox, oy) = it.Offset(top);
        it.Comp.X = Snap(x - ox);
        it.Comp.Y = Snap(drop - oy);
        it.Placed = true;
    }

    /// <summary>
    /// R-as6-3: the hangers of path net <paramref name="k"/> sorted by where their copper lies along the
    /// artwork's travel there, and those on its left marked to be drawn above the line. The travel is read
    /// from the hints of the path elements either side of the net (the end fixtures at the two ends); a net
    /// with no two such hints is left in its drawn order. The end fixtures keep their places.
    /// </summary>
    private static void OrderByArtwork(
        string net, int k, List<Item> row, List<Step> path, Item? start, Item? end,
        IReadOnlyDictionary<string, (long X, long Y)> hints, HashSet<Item> above)
    {
        (long X, long Y)? Hint(Item? it) =>
            it is not null && hints.TryGetValue(it.Comp.InstanceName, out var h) ? h : null;

        var before = k > 0 ? Hint(path[k - 1].Item) : Hint(start);
        var after  = k < path.Count ? Hint(path[k].Item) : Hint(end);
        if (before is not { } a || after is not { } b) return;
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return;
        dx /= len; dy /= len;

        var fixedEnds = row.Where(i => ReferenceEquals(i, start) || ReferenceEquals(i, end)).ToList();
        var free = row.Where(i => !fixedEnds.Contains(i)).ToList();
        var along = new Dictionary<Item, double>(ReferenceEqualityComparer.Instance);
        foreach (var it in free)
        {
            if (Hint(it) is not { } h) { along[it] = double.MaxValue; continue; }
            double rx = h.X - a.X, ry = h.Y - a.Y;
            along[it] = rx * dx + ry * dy;
            // Left of travel in a y-up layout is above a line drawn left to right.
            if (it.Local.Length == 2 && dx * ry - dy * rx > 0) above.Add(it);
        }
        var sorted = free.OrderBy(i => along[i]).ToList();   // stable: unhinted ones keep their order, last
        row.Clear();
        if (start is not null && fixedEnds.Contains(start)) row.Add(start);
        row.AddRange(sorted);
        if (end is not null && fixedEnds.Contains(end) && !ReferenceEquals(end, start)) row.Add(end);
    }

    /// <summary>
    /// Picks the rotation that puts pin <paramref name="a"/> furthest left of pin <paramref name="b"/>
    /// (horizontal) or furthest above it (vertical) — R0 on a tie, so a symbol is drawn as the palette
    /// draws it whenever that already reads correctly.
    /// </summary>
    private static void Orient(Item it, int a, int b, bool horizontal)
    {
        SymbolRotation best = SymbolRotation.R0;
        double bestScore = double.MaxValue;
        foreach (var rot in new[] { SymbolRotation.R0, SymbolRotation.R90, SymbolRotation.R270, SymbolRotation.R180 })
        {
            it.Comp.Rotation = rot;
            var pa = it.Offset(a);
            double score;
            if (b >= 0)
            {
                var pb = it.Offset(b);
                score = horizontal ? (pa.X - pb.X) * 10 + Math.Abs(pa.Y - pb.Y)
                                   : (pa.Y - pb.Y) * 10 + Math.Abs(pa.X - pb.X);
            }
            else score = horizontal ? pa.X : pa.Y;   // a single pin: furthest left / furthest up
            if (score < bestScore - 1e-9) { bestScore = score; best = rot; }
        }
        it.Comp.Rotation = best;
        BoxOf(it);
    }

    /// <summary>An element off the line: two-terminal parts stand upright with an already-placed net
    /// on top, so the wire to it rises; anything else is drawn as the palette draws it.</summary>
    private static void OrientFree(Item it, Dictionary<string, List<double>> anchor)
    {
        if (it.Local.Length == 2)
        {
            int top = anchor.ContainsKey(it.Nets[0]) || it.Nets[1] == Ground ? 0
                    : anchor.ContainsKey(it.Nets[1]) || it.Nets[0] == Ground ? 1 : 0;
            Orient(it, top, 1 - top, horizontal: false);
            return;
        }
        it.Comp.Rotation = SymbolRotation.R0;
        BoxOf(it);
    }

    /// <summary>The drawn glyph's box relative to the origin, with every pin inside it.</summary>
    /// <summary>
    /// Measures the item under its current rotation — glyph, pins and labels — and, for an upright
    /// part, moves its labels beside it first.
    /// </summary>
    private static (double, double, double, double) BoxOf(Item it)
    {
        var (box, bottom, full) = Measure(it);
        it.Box = box;
        it.GlyphBottom = bottom;
        it.Comp.LabelOffsets.Clear();
        if (box.Item4 - box.Item2 > box.Item3 - box.Item1 && it.Comp.Symbol != SymbolKind.Ground)
        {
            LabelsBeside(it);
            full = Measure(it).Full;
        }
        it.Full = (Math.Min(full.Item1, box.Item1), Math.Min(full.Item2, box.Item2),
                   Math.Max(full.Item3, box.Item3), Math.Max(full.Item4, box.Item4));
        return box;
    }

    private static ((double, double, double, double) Box, double GlyphBottom, (double, double, double, double) Full)
        Measure(Item it)
    {
        var probe = new SchematicEditModel();
        var c = it.Comp;
        double x = c.X, y = c.Y;
        c.X = 0; c.Y = 0;
        probe.Components.Add(c);
        var rc = probe.BuildRenderModel().Model.Components[0];
        c.X = x; c.Y = y;

        double minX = rc.GlyphBbMinX, minY = rc.GlyphBbMinY, maxX = rc.GlyphBbMaxX, maxY = rc.GlyphBbMaxY;
        for (int k = 0; k < it.Local.Length; k++)
        {
            var (px, py) = it.Offset(k);
            minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
        }
        // The label block, from the renderer's own row geometry but with a width nearer the text
        // than its culling estimate, which is generous on purpose and would space parts a third
        // further apart than the text needs.
        double lMinX = minX, lMinY = minY, lMaxX = maxX, lMaxY = maxY;
        double baseX = SchematicComponent.LabelBaseXFor(c.Symbol);
        double baseY = SchematicComponent.LabelBaseYFor(c.Symbol, c.PortCount, rc.GlyphBbMaxY);
        for (int i = 0; i < rc.Labels.Count; i++)
        {
            if (string.IsNullOrEmpty(rc.Labels[i])) continue;
            var (dx, dy) = SchematicComponent.LabelOffsetAt(c.LabelOffsets, i);
            double bx = baseX + dx, by = baseY + dy + i * SchematicComponent.LabelWorldStep;
            lMinX = Math.Min(lMinX, bx);
            lMaxX = Math.Max(lMaxX, bx + rc.Labels[i].Length * LabelCharWidth);
            lMinY = Math.Min(lMinY, by - SchematicComponent.LabelWorldHeight);
            lMaxY = Math.Max(lMaxY, by + 20);
        }
        return ((minX, minY, maxX, maxY), rc.GlyphBbMaxY, (lMinX, lMinY, lMaxX, lMaxY));
    }

    /// <summary>Average world width of one label character at the renderer's label size.</summary>
    internal const double LabelCharWidth = 40;

    /// <summary>
    /// Moves an upright part's label block beside it, vertically centred on the glyph. The default
    /// place is UNDER the glyph, which on a part standing on its own ground symbol is on top of
    /// that symbol.
    /// </summary>
    private static void LabelsBeside(Item it)
    {
        var c = it.Comp;
        int rows = 2 + c.Parameters.Count(p => p.ShowOnSchematic);
        double baseX = SchematicComponent.LabelBaseXFor(c.Symbol);
        double baseY = SchematicComponent.LabelBaseYFor(c.Symbol, c.PortCount, it.GlyphBottom);
        double centre = (it.Box.MinY + it.Box.MaxY) / 2;
        double firstBaseline = centre - rows * SchematicComponent.LabelWorldStep / 2 + SchematicComponent.LabelWorldHeight;
        c.LabelOffsets.Clear();
        c.LabelOffsets.Add((it.Box.MaxX + 60 - baseX, firstBaseline - baseY));
    }

    private static double Snap(double v) => Math.Round(v / P) * P;

    // ─────────────────────────────────────────────────────────────────────────
    //  Grounds
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One ground symbol ON every terminal sitting on net <c>0</c>, pointing the way that terminal
    /// does. Each pair gets a private net name so the router never joins two grounds across the
    /// sheet; extraction gives them all net <c>0</c> regardless, because a ground NAMES its net.
    /// </summary>
    private static List<Item> PlaceGrounds(SchematicEditModel model, List<Item> placed)
    {
        var made = new List<Item>();
        int n = 0;
        foreach (var owner in placed)
            for (int k = 0; k < owner.Nets.Length; k++)
            {
                if (owner.Nets[k] != Ground) continue;
                string net = $"{Ground}#{n++}";
                owner.Nets[k] = net;

                var (px, py) = owner.Pin(k);
                var (ux, uy) = Outward(owner, k);
                var comp = NewGround(px, py, ux, uy, n);
                model.Components.Add(comp);
                var g = new Item { Comp = comp, Nets = [net], Local = [(0f, 0f)], Placed = true };
                BoxOf(g);
                made.Add(g);
            }
        return made;
    }

    /// <summary>A ground symbol with its pin at (<paramref name="x"/>, <paramref name="y"/>), its stem pointing along
    /// (<paramref name="ux"/>, <paramref name="uy"/>) — away from the terminal or wire it sits on — named for
    /// <paramref name="n"/>.</summary>
    public static EditableComponent NewGround(double x, double y, int ux, int uy, int n)
    {
        var info = ComponentTypeRegistry.Get(SymbolKind.Ground);
        return new EditableComponent
        {
            Symbol           = SymbolKind.Ground,
            ShowTypeLabel    = info.DefaultShowTypeLabel,
            ShowInstanceName = info.DefaultShowInstanceName,
            X                = x,
            Y                = y,
            // A ground's stem is drawn along local +Y, so it is turned to point AWAY from what it sits on.
            Rotation = (ux, uy) switch
            {
                (0, > 0) => SymbolRotation.R0,
                (< 0, 0) => SymbolRotation.R90,
                (0, < 0) => SymbolRotation.R180,
                _        => SymbolRotation.R270,
            },
            InstanceName = $"{ComponentTypeRegistry.InstancePrefix(SymbolKind.Ground)}{n}",
        };
    }

    /// <summary>The way a lead points: from the glyph's centre towards the pin, on the dominant axis.</summary>
    private static (int X, int Y) Outward(Item it, int k)
    {
        var (px, py) = it.Offset(k);
        double cx = (it.Box.MinX + it.Box.MaxX) / 2, cy = (it.Box.MinY + it.Box.MaxY) / 2;
        double dx = px - cx, dy = py - cy;
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return (0, -1);
        return Math.Abs(dx) > Math.Abs(dy) ? (Math.Sign(dx), 0) : (0, Math.Sign(dy));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Wires and labels
    // ─────────────────────────────────────────────────────────────────────────

    private static void Wire(SchematicEditModel model, List<Item> placed, List<Item> grounds, List<string> notes)
    {
        var all = placed.Concat(grounds).ToList();

        var blocks = all.Select(it => new SchematicAutoRouter.Block(
            [.. Enumerable.Range(0, it.Local.Length).Select(k => { var (x, y) = it.Pin(k); return (x, y, (string?)it.Nets[k]); })],
            KeepOut(it, isGround: it.Comp.Symbol == SymbolKind.Ground))).ToList();

        var routed = SchematicAutoRouter.Route(blocks);

        // The net each terminal cell belongs to, so each wire can be attributed to its net — the
        // route's last vertex is always the terminal it was routed to.
        var netAt = new Dictionary<(long, long), string>();
        foreach (var it in all)
            for (int k = 0; k < it.Local.Length; k++)
                netAt[Key(it.Pin(k))] = it.Nets[k];

        var wiresOf = new Dictionary<string, List<EditableWire>>(StringComparer.Ordinal);
        foreach (var path in routed.Wires)
        {
            var wire = new EditableWire();
            foreach (var pt in path) wire.Points.Add(pt);
            model.Wires.Add(wire);
            if (netAt.TryGetValue(Key(path[^1]), out var net) || netAt.TryGetValue(Key(path[0]), out net))
            {
                if (!wiresOf.TryGetValue(net, out var l)) wiresOf[net] = l = [];
                l.Add(wire);
            }
        }

        // A terminal the router could not reach is connected by name instead — a real connection.
        var unroutedNets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (x, y, net) in routed.Unrouted)
        {
            if (net.StartsWith(Ground + "#", StringComparison.Ordinal)) continue;   // a ground sits ON its pin
            model.NetLabels.Add(new EditableNetLabel { X = x, Y = y, Name = net });
            unroutedNets.Add(net);
        }
        if (unroutedNets.Count > 0)
            notes.Add(
                $"{unroutedNets.Count} net(s) could not be wired completely without crossing something, and "
              + $"are joined by net label instead: {string.Join(", ", unroutedNets.Order(StringComparer.Ordinal))}. "
              + "The circuit is the one the netlist states; the drawing is what suffered.");

        // Every net keeps the name the netlist gave it: one label, on a stretch of its own wire no
        // other net's wire passes through, or on one of its terminals when it has no wire at all.
        var segs = new List<(string Net, double Ax, double Ay, double Bx, double By)>();
        foreach (var (net, ws) in wiresOf)
            foreach (var w in ws)
                for (int i = 0; i + 1 < w.Points.Count; i++)
                    segs.Add((net, w.Points[i].X, w.Points[i].Y, w.Points[i + 1].X, w.Points[i + 1].Y));

        var keepCells = new HashSet<(long, long)>();
        foreach (var b in blocks) foreach (var c in b.KeepOut) keepCells.Add(Key(c));

        var signalNets = placed.SelectMany(i => i.Nets).Where(n => !n.StartsWith(Ground + "#", StringComparison.Ordinal) && n != Ground)
                               .Distinct(StringComparer.Ordinal).ToList();
        foreach (var net in signalNets)
        {
            if (unroutedNets.Contains(net)) continue;   // already labelled at its terminals
            if (LabelPoint(net, wiresOf, segs, keepCells) is { } at)
            {
                var label = new EditableNetLabel { Name = net };
                label.AnchorToWire(at.Wire, at.X, at.Y);
                model.NetLabels.Add(label);
                continue;
            }
            // No stretch clear of everything else: still ON a wire of its own, the middle of its longest run — a label
            // left on a terminal of a net that has wires floated off them once the drawing was edited (R-dfr22-2-4).
            if (LongestRun(net, wiresOf) is { } run)
            {
                var label = new EditableNetLabel { Name = net };
                label.AnchorToWire(run.Wire, run.X, run.Y);
                model.NetLabels.Add(label);
                continue;
            }
            var term = placed.SelectMany(i => Enumerable.Range(0, i.Nets.Length).Where(k => i.Nets[k] == net).Select(k => i.Pin(k))).First();
            model.NetLabels.Add(new EditableNetLabel { X = term.X, Y = term.Y, Name = net });
        }
    }

    /// <summary>
    /// R-im10-4: each net with routes wired along them — snapped, made orthogonal, and every pin they miss re-joined
    /// with at most one dog-leg, leaving along the pin's lead. A net whose wiring would touch another net's wire or pin
    /// (snapping can bring two runs together) is joined by a net label on each pin instead, which is a real connection;
    /// so is a net with no route. Each separate group of a net's wires carries the net's name, because a picture joins
    /// two runs by giving them one name.
    /// </summary>
    private static void WireExact(SchematicEditModel model, List<Item> placed, List<Item> grounds, ExactPlacement exact,
                                  List<string> notes)
    {
        bool IsGround(string n) => n == Ground || n.StartsWith(Ground + "#", StringComparison.Ordinal);
        var pinsOf = new Dictionary<string, List<((double X, double Y) At, (int X, int Y) Out)>>(StringComparer.Ordinal);
        var allPins = new List<((double X, double Y) At, string Net)>();
        var labelled = new List<((double X, double Y) At, string Net)>();   // R-si1-5: joined by a label on the pin
        foreach (var it in placed.Concat(grounds))
            for (int k = 0; k < it.Local.Length; k++)
            {
                allPins.Add((it.Pin(k), it.Nets[k]));
                if (IsGround(it.Nets[k]) || it.Comp.Symbol == SymbolKind.Ground) continue;
                if (exact.JoinByLabel.Contains((it.Comp.InstanceName, k))) { labelled.Add((it.Pin(k), it.Nets[k])); continue; }
                if (!pinsOf.TryGetValue(it.Nets[k], out var l)) pinsOf[it.Nets[k]] = l = [];
                l.Add((it.Pin(k), Outward(it, k)));
            }

        (double X, double Y) Grid((double X, double Y) p) => exact.SnapToGrid ? (Snap(p.X), Snap(p.Y)) : p;
        var drawn = new Dictionary<string, List<List<(double X, double Y)>>>(StringComparer.Ordinal);
        var byLabel = new List<string>();
        foreach (var (net, pins) in pinsOf.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var runs = new List<List<(double X, double Y)>>();
            if (exact.Routes.TryGetValue(net, out var given))
                foreach (var run in given)
                    if (Orthogonal(run.Select(Grid)) is { Count: >= 2 } pts) runs.Add(pts);
            if (runs.Count == 0)
            {
                // Pins that touch are joined already (two parts drawn pin to pin): no wire to draw, and one label only to
                // show the net's name or to join the pins labelled elsewhere.
                var touching = pins.Select(p => p.At).Distinct().ToList();
                if (touching.Count != 1) byLabel.Add(net);
                else if (!exact.QuietNets.Contains(net) || labelled.Any(l => l.Net == net))
                    model.NetLabels.Add(new EditableNetLabel { X = touching[0].X, Y = touching[0].Y, Name = net });
                continue;
            }

            var vertices = runs.SelectMany(r => r).Distinct().ToList();
            foreach (var (at, (ox, _)) in pins)
            {
                if (runs.Any(r => OnPolyline(r, at))) continue;
                var v = vertices.MinBy(q => Math.Abs(q.X - at.X) + Math.Abs(q.Y - at.Y));
                // The re-join is a short bend to the pin's own wire. A pin whose own stub was trimmed away under a longer
                // native body is nearest some OTHER run of its net, which the source joins only by name — a supply
                // label at each end — and a dog-leg to it is a wire across the sheet the picture never drew.
                if (Math.Abs(v.X - at.X) + Math.Abs(v.Y - at.Y) > MaxRejoin) { labelled.Add((at, net)); continue; }
                var bend = ox != 0 ? (v.X, at.Y) : (at.X, v.Y);
                if (Orthogonal([at, bend, v]) is { Count: >= 2 } leg) runs.Add(leg);
            }
            drawn[net] = runs;
        }

        // A net whose wiring touches anything of another net is labelled instead.
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (net, runs) in drawn)
        {
            foreach (var (other, otherRuns) in drawn)
            {
                if (other == net) continue;
                if (runs.Any(r => r.Any(v => otherRuns.Any(o => OnPolyline(o, v))))) { conflicted.Add(net); conflicted.Add(other); }
            }
            foreach (var (at, pinNet) in allPins)
                if (pinNet != net && runs.Any(r => OnPolyline(r, at))) conflicted.Add(net);
        }
        foreach (var net in conflicted) { drawn.Remove(net); byLabel.Add(net); }

        foreach (var (net, runs) in drawn.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var wires = runs.Select(r => { var w = new EditableWire(); w.Points.AddRange(r); return w; }).ToList();
            model.Wires.AddRange(wires);

            // One label per separate group of this net's wires, on a stretch no other net's wire passes through.
            var parent = Enumerable.Range(0, wires.Count).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int i = 0; i < runs.Count; i++)
                for (int j = 0; j < runs.Count; j++)
                    if (i != j && runs[i].Any(v => OnPolyline(runs[j], v))) parent[Find(i)] = Find(j);
            var segs = drawn.Where(kv => kv.Key != net)
                            .SelectMany(kv => kv.Value.SelectMany(r => r.Zip(r.Skip(1), (a, b) => (kv.Key, a.X, a.Y, b.X, b.Y))))
                            .ToList();
            var groups = Enumerable.Range(0, wires.Count).GroupBy(Find).OrderBy(g => g.Key).ToList();
            if (groups.Count == 1 && exact.QuietNets.Contains(net) && labelled.All(l => l.Net != net)) continue;
            foreach (var group in groups)
            {
                var mine = new Dictionary<string, List<EditableWire>>(StringComparer.Ordinal) { [net] = [.. group.Select(i => wires[i])] };
                var label = new EditableNetLabel { Name = net };
                if ((LabelPoint(net, mine, segs, []) ?? LongestRun(net, mine)) is { } at) label.AnchorToWire(at.Wire, at.X, at.Y);
                else { label.X = runs[group.First()][0].X; label.Y = runs[group.First()][0].Y; }
                model.NetLabels.Add(label);
            }
        }

        foreach (var net in byLabel.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            foreach (var (at, _) in pinsOf[net])
                model.NetLabels.Add(new EditableNetLabel { X = at.X, Y = at.Y, Name = net });
        foreach (var (at, net) in labelled)
            model.NetLabels.Add(new EditableNetLabel { X = at.X, Y = at.Y, Name = net });
        var unrouted = byLabel.Where(n => pinsOf[n].Count > 1).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (unrouted.Count > 0)
            notes.Add(
                $"{unrouted.Count} net(s) could not be wired as the picture draws them and are joined by net label "
              + $"instead: {string.Join(", ", unrouted)}. The circuit is the one the picture states; the drawing is what suffered.");
    }

    /// <summary>The longest bend <see cref="WireExact"/> draws to re-join a pin to its net's wiring; farther, the pin is
    /// joined by a label.</summary>
    private const double MaxRejoin = 4 * P;

    /// <summary>A run made orthogonal: repeated points dropped, a diagonal step turned into an L.</summary>
    private static List<(double X, double Y)> Orthogonal(IEnumerable<(double X, double Y)> points)
    {
        var r = new List<(double X, double Y)>();
        foreach (var p in points)
        {
            if (r.Count > 0 && r[^1] == p) continue;
            if (r.Count > 0 && r[^1].X != p.X && r[^1].Y != p.Y) r.Add((p.X, r[^1].Y));
            r.Add(p);
        }
        // Collinear middle vertices are kept: a T from another run may land on one.
        return r;
    }

    private static bool OnPolyline(List<(double X, double Y)> run, (double X, double Y) p)
    {
        for (int i = 0; i + 1 < run.Count; i++)
            if (SchematicGeometry.PointOnSegment(p.X, p.Y, run[i].X, run[i].Y, run[i + 1].X, run[i + 1].Y, 1.0)) return true;
        return run.Count == 1 && run[0] == p;
    }

    private static (EditableWire Wire, double X, double Y)? LabelPoint(
        string net, Dictionary<string, List<EditableWire>> wiresOf,
        List<(string Net, double Ax, double Ay, double Bx, double By)> segs, HashSet<(long, long)> keep)
    {
        if (!wiresOf.TryGetValue(net, out var ws)) return null;

        var candidates = new List<(EditableWire W, double X, double Y, double Score)>();
        foreach (var w in ws)
            for (int i = 0; i + 1 < w.Points.Count; i++)
            {
                var (ax, ay) = w.Points[i];
                var (bx, by) = w.Points[i + 1];
                bool horizontal = ay == by;
                double len = Math.Abs(bx - ax) + Math.Abs(by - ay);
                int cells = (int)Math.Round(len / P);
                for (int c = 1; c < cells; c++)
                {
                    double x = ax + Math.Sign(bx - ax) * c * P, y = ay + Math.Sign(by - ay) * c * P;
                    if (keep.Contains(Key((x, y)))) continue;
                    if (segs.Any(s => s.Net != net && SchematicGeometry.PointOnSegment(x, y, s.Ax, s.Ay, s.Bx, s.By, P / 2)))
                        continue;
                    // Long horizontal runs first, and the middle of a run rather than its end.
                    double score = (horizontal ? 0 : 1000) - len + Math.Abs(c - cells / 2.0) * 10;
                    candidates.Add((w, x, y, score));
                }
            }
        if (candidates.Count == 0) return null;
        var best = candidates.MinBy(c => c.Score);
        return (best.W, best.X, best.Y);
    }

    /// <summary>The middle of <paramref name="net"/>'s longest wire segment, or null when it has no wire — where its label
    /// goes when no stretch of its wiring is clear of everything else (R-dfr22-2-4).</summary>
    private static (EditableWire Wire, double X, double Y)? LongestRun(string net, Dictionary<string, List<EditableWire>> wiresOf)
    {
        if (!wiresOf.TryGetValue(net, out var ws)) return null;
        (EditableWire Wire, double X, double Y)? best = null;
        double longest = -1;
        foreach (var w in ws)
            for (int i = 0; i + 1 < w.Points.Count; i++)
            {
                var (ax, ay) = w.Points[i];
                var (bx, by) = w.Points[i + 1];
                double len = Math.Abs(bx - ax) + Math.Abs(by - ay);
                if (len > longest) { longest = len; best = (w, (ax + bx) / 2, (ay + by) / 2); }
            }
        return best;
    }

    /// <summary>
    /// The world cells no wire may enter: the drawn glyph, one grid square proud — except each pin's
    /// own cell and the cell immediately outside it, which is what makes a terminal reachable from
    /// exactly the direction its lead points. A ground keeps out only its own glyph, because it sits
    /// ON another part's pin and a margin would wall that pin in.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> KeepOut(Item it, bool isGround)
    {
        double margin = isGround ? 0 : P;
        var exempt = new HashSet<(long, long)>();
        for (int k = 0; k < it.Local.Length; k++)
        {
            var (px, py) = it.Pin(k);
            exempt.Add(Key((px, py)));
            if (isGround) continue;
            var (ux, uy) = Outward(it, k);
            exempt.Add(Key((px + ux * P, py + uy * P)));
        }

        var cells = new HashSet<(long, long)>();
        // The glyph's box to the NEAREST grid line, then the margin: a glyph that pokes a few units
        // past its own pin (a polarity mark) must not cost a whole extra cell, or the cell outside
        // the pin is walled in and nothing reaches it.
        double x0 = Snap(it.Comp.X + it.Box.MinX) - margin, x1 = Snap(it.Comp.X + it.Box.MaxX) + margin;
        double y0 = Snap(it.Comp.Y + it.Box.MinY) - margin, y1 = Snap(it.Comp.Y + it.Box.MaxY) + margin;
        for (double x = x0; x <= x1 + 0.5; x += P)
            for (double y = y0; y <= y1 + 0.5; y += P)
                cells.Add(Key((x, y)));

        // The label rows too, with no margin: a wire through a value is unreadable, a wire
        // alongside one is not. Only the cells the text actually covers — the box around glyph
        // AND text would wall in a pin that sits between the two.
        if (!isGround)
            foreach (var (l, t, r, b) in LabelRows(it))
                for (double x = Math.Ceiling((it.Comp.X + l) / P) * P; x <= it.Comp.X + r; x += P)
                    for (double y = Math.Ceiling((it.Comp.Y + t) / P) * P; y <= it.Comp.Y + b; y += P)
                        cells.Add(Key((x, y)));

        cells.ExceptWith(exempt);
        return [.. cells.Select(c => (c.Item1 * P, c.Item2 * P))];
    }

    /// <summary>Each non-empty label row's box, relative to the component origin.</summary>
    private static IEnumerable<(double L, double T, double R, double B)> LabelRows(Item it)
    {
        var c = it.Comp;
        var labels = c.ToRenderComponent().Labels;
        double baseX = SchematicComponent.LabelBaseXFor(c.Symbol);
        double baseY = SchematicComponent.LabelBaseYFor(c.Symbol, c.PortCount, it.GlyphBottom);
        for (int i = 0; i < labels.Count; i++)
        {
            if (string.IsNullOrEmpty(labels[i])) continue;
            var (dx, dy) = SchematicComponent.LabelOffsetAt(c.LabelOffsets, i);
            double bx = baseX + dx, by = baseY + dy + i * SchematicComponent.LabelWorldStep;
            yield return (bx, by - SchematicComponent.LabelWorldHeight, bx + labels[i].Length * LabelCharWidth, by + 20);
        }
    }

    private static (long, long) Key((double X, double Y) p)
        => ((long)Math.Round(p.X / P), (long)Math.Round(p.Y / P));

    // ─────────────────────────────────────────────────────────────────────────
    //  Variables and measurements
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The netlist's globals in one VAR block and its measurements in one MEAS block, side
    /// by side ABOVE everything else, each ending just over the circuit. Above rather than below
    /// because a block's rows grow downwards from its glyph, and a block at the bottom of the sheet
    /// runs off the end of a picture framed on the glyphs.</summary>
    private static void PlaceDirectives(SchematicEditModel model, TestBench tb)
    {
        if (tb.GlobalVariables.Count == 0 && tb.Measurements.Count == 0) return;

        var bounds = model.BuildRenderModel().Model;
        double top = 0, x = 0;
        if (model.Components.Count > 0)
        {
            top = bounds.Components.Min(c => c.FullBbMinY);
            x   = bounds.Components.Min(c => c.FullBbMinX);
        }
        foreach (var w in model.Wires) foreach (var (_, wy) in w.Points) top = Math.Min(top, wy);
        x = Snap(x);

        void Block(SymbolKind kind, string name, IEnumerable<EditableParameter> rows)
        {
            var c = new EditableComponent { Symbol = kind, InstanceName = name };
            foreach (var r in rows) c.Parameters.Add(r);
            var probe = new SchematicEditModel();
            probe.Components.Add(c);
            var rc = probe.BuildRenderModel().Model.Components[0];
            c.X = Snap(x - rc.FullBbMinX);
            c.Y = Snap(top - 400 - rc.FullBbMaxY);
            model.Components.Add(c);
            x = Snap(c.X + rc.FullBbMaxX + 600);
        }

        if (tb.GlobalVariables.Count > 0)
            Block(SymbolKind.Var, "VAR1", tb.GlobalVariables.Select(v => new EditableParameter
            {
                Name = v.Name, Expression = v.Expression ?? "", Unit = v.Unit ?? "",
            }));
        if (tb.Measurements.Count > 0)
            Block(SymbolKind.Meas, "MEAS1", tb.Measurements.Select(m => new EditableParameter
            {
                Name = m.Name, Expression = m.Expression, Unit = m.Unit ?? "",
            }));
    }
}
