// L8e — D3: PORTS COME FROM THE LAYOUT'S OWN `IsPort` LABELS. THERE IS NO NEW SHAPE TYPE.
//
// The obvious answer is a new shape type, and the code already says why it is wrong. `LabelShape`
// has carried an `IsPort` flag since L0a; it persists, survives copy/paste and flatten, is excluded
// from every boolean operation and from Flatten-to-Polygon, and the Label tool sets it to `false`
// with the comment "port placement belongs with the EM work, not here". `LayoutModel.cs` carries a
// whole paragraph explaining why a port is a label with a flag rather than its own type — a port
// label is TEXT the user sees, whereas a `LayoutPin` is CONNECTIVITY (an edge, with a width and an
// outward direction). THIS FILE IS WHAT THAT PROVISION WAS FOR. Do not add a PortShape.
//
// R-em-1 — framework-free. No Avalonia, no SkiaSharp, no document, no canvas.
//
// R-res-5 — THE SIDE IS INFERRED FROM GEOMETRY, THE INFERENCE IS REPORTED, AND AN AMBIGUOUS PORT IS
// REFUSED BY NAME. A PlanarPort needs to know which END of the conductor it is (PlanarPortSide);
// a label carries only a position. Inferring silently and being wrong reverses the direction of
// current into the structure — a hard π in S₂₁, smooth and plausible and invisible in a magnitude
// plot, which is exactly the failure mode L8d's own D1 warns about. So: infer from the nearest
// conductor boundary, SAY what was inferred, and refuse when two boundaries are equally close.
//
// R-res-4 — nothing here writes to the layout. `.clay`'s schema is untouched; a port label is an
// ordinary LabelShape that already round-trips.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>
/// One numbered port label and what became of it: the resolved <see cref="PlanarPort"/>, or the
/// reason it could not be resolved.
///
/// <para><b>This exists so a panel can LIST a port it cannot solve.</b> Owner request, 2026-08-25:
/// "if any ports aren't touching metal, the .cem editor will not list the ports. I'd like to still
/// see them listed, even if they are not on a conductor (and the .cem gives a warning)." The old
/// behaviour is a direct consequence of <see cref="EmPortExtractionResult.Ports"/> being
/// all-or-nothing — which it must stay, because a port that is not on metal has no location the
/// mesher could honestly place it at, and half a port set is a complete, plausible answer for a
/// structure nobody drew. So the SOLVER's view is unchanged and a second, diagnostic view is added
/// beside it.</para>
/// </summary>
public sealed record EmPortRow(int Number, LabelShape Label, PlanarPort? Port, string? Problem)
{
    public bool Ok => Port is not null;
}

/// <summary>Ports resolved from a layout's port labels, or a refusal that names the offending
/// label — the same R-mom-17 shape every other refusal in this area uses.</summary>
/// <param name="SourceLabels">
/// The label each port came from, <b>index-aligned with <paramref name="Ports"/></b>. Carried because
/// a port's identity in the LAYOUT is its label's own anchor, and nothing else here can say which
/// label became port 3 — a label whose text names no number is auto-numbered in document order, so
/// re-deriving the mapping anywhere else would be a second copy of that ordering, free to drift.
/// The renderer uses it to know which ports to draw as internal delta gaps.
/// </param>
/// <param name="Rows">
/// <b>Every numbered port label, in port order, resolved or not</b> — see <see cref="EmPortRow"/>.
/// On success this is exactly <paramref name="Ports"/> with its labels attached; on a per-port
/// refusal it is the only thing that still knows how many ports the user drew. Empty for the
/// whole-set refusals (no labels at all, a duplicate number, an unusable resolution), which have no
/// port order to report.
/// </param>
public sealed record EmPortExtractionResult(
    IReadOnlyList<PlanarPort> Ports,
    string?                   Refusal,
    IReadOnlyList<string>     Notes,
    IReadOnlyList<LabelShape> SourceLabels,
    IReadOnlyList<EmPortRow>  Rows)
{
    public bool Ok => Refusal is null && Ports.Count > 0;

    public static EmPortExtractionResult No(string refusal, IEnumerable<string>? notes = null)
        => new([], refusal, notes is null ? [] : [.. notes], [], []);
}

public static class EmPortExtraction
{
    /// <summary>
    /// How close a second conductor boundary has to be, as a fraction of <b>the width of the end the
    /// port sits on</b>, before "which end is this?" stops having one answer.
    ///
    /// <para>A label at the exact corner of a rectangle is equidistant from two edges and is genuinely
    /// ambiguous; a label at the middle of one end is not. Five percent is deliberately tight — the
    /// cost of refusing a placeable port is one nudge, and the cost of guessing wrong is a wrong
    /// answer that looks right.</para>
    ///
    /// <para><b>The fraction was originally taken of the conductor's smaller BOUNDING dimension, and
    /// L8's own phase gate is what caught it.</b> For an L-shaped bend the bounding box is
    /// arm × arm, so on the MMIC starter the threshold came out at 5% of 0.995 mm = 49.8 µm — and a
    /// port at the exact centre of the 72 µm line end is 36 µm from the flanking edge, so a correctly
    /// placed port was refused as ambiguous. The scale has to be LOCAL to the end, not global to the
    /// shape; see <see cref="TryInferSide"/> for how it is estimated.</para>
    /// </summary>
    private const double AmbiguityFraction = 0.05;

    /// <summary>
    /// Port labels → <see cref="PlanarPort"/>s, in port-number order.
    /// </summary>
    /// <param name="shapes">The layout's own shapes. Only <see cref="LabelShape"/>s with
    /// <see cref="LabelShape.IsPort"/> are read; everything else is artwork and is ignored here.</param>
    /// <param name="problem">The already-extracted planar problem — its conductor polygons are what
    /// the side inference measures against, in the SAME metre coordinates
    /// <c>PlanarExtractor</c> produced (no translation, no centring: see that file's header).</param>
    /// <param name="dbuPerMicron">The LAYOUT's own resolution, for the label coordinates.</param>
    /// <param name="z0For">Reference impedance for the port at a given 0-based index in the final
    /// ordered list. <b>The impedance lives in the <c>.cem</c> (<c>EmSetup.PortZ0s</c>), never on the
    /// shape</b> — R-cpl-6's list is already per-port and already additive.</param>
    /// <param name="displayUnit">The LAYOUT's own display unit, used for every coordinate this file
    /// prints. Defaults to microns so a headless caller that has no layout to ask keeps the previous
    /// wording exactly.</param>
    /// <param name="kindFor">The port TYPE at a given 0-based index in the final ordered list —
    /// edge, or an internal delta gap. <b>Lives in the <c>.cem</c> (<c>EmSetup.PortKinds</c>) beside
    /// the impedance, for the same reason: a layout is geometry.</b> Null means every port is an edge
    /// port, which is what every caller predating internal ports gets.</param>
    /// <param name="groundPathWidthM">The size of the path an <see cref="PlanarPortKind.Internal"/>
    /// port may grow down to the ground plane where the artwork has no via — the TECHNOLOGY's own
    /// default via size, in metres, which is why it comes from the caller rather than from here.
    /// Null means grow nothing and refuse such a port instead. See <c>PlanarGroundPath</c>.</param>
    public static EmPortExtractionResult Extract(
        IReadOnlyList<LayoutShape> shapes,
        PlanarProblem              problem,
        int                        dbuPerMicron,
        Func<int, Complex>?        z0For = null,
        LayoutUnit                 displayUnit = LayoutUnit.Um,
        Func<int, PlanarPortKind>? kindFor = null,
        double?                    groundPathWidthM = null)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(problem);

        if (dbuPerMicron <= 0)
            return EmPortExtractionResult.No(
                $"The layout's resolution is {dbuPerMicron} DBU per micron, which is not a usable scale.");

        var labels = new List<LabelShape>();
        foreach (var s in shapes)
            if (s is LabelShape { IsPort: true } l) labels.Add(l);

        if (labels.Count == 0)
            return EmPortExtractionResult.No(
                "This layout has no port labels, so the full-wave planar kernel has nothing to " +
                "drive. Use the Port tool to click each conductor end you want a port on — a port " +
                "label is an ordinary label with its port flag set, so it saves, copies and " +
                "flattens like any other.");

        double perDbu = 1.0 / (dbuPerMicron * 1e6);

        // Numbering (D3): a label whose text names a number keeps it; everything else takes the
        // lowest free one, in document order. Auto-numbering from the EXISTING labels is what makes
        // "click an edge, get P1" true without the tool having to know what is already there.
        var explicitNumbers = new Dictionary<int, LabelShape>();
        var pending         = new List<LabelShape>();
        foreach (var l in labels)
        {
            if (TryParseNumber(l.Text, out int n))
            {
                if (explicitNumbers.TryGetValue(n, out var first))
                    return EmPortExtractionResult.No(
                        $"Two port labels both name port {n}: '{first.Text}' at " +
                        $"{Coord(first.X, first.Y, dbuPerMicron, displayUnit)} and '{l.Text}' at " +
                        $"{Coord(l.X, l.Y, dbuPerMicron, displayUnit)}. Port numbers index the s-parameter matrix, " +
                        "so they have to be distinct — renumber one of them.");
                explicitNumbers[n] = l;
            }
            else pending.Add(l);
        }

        int next = 1;
        var numbered = new List<(int Number, LabelShape Label)>(labels.Count);
        foreach (var kv in explicitNumbers) numbered.Add((kv.Key, kv.Value));
        foreach (var l in pending)
        {
            while (explicitNumbers.ContainsKey(next)) next++;
            explicitNumbers[next] = l;
            numbered.Add((next, l));
            next++;
        }
        numbered.Sort((a, b) => a.Number.CompareTo(b.Number));

        // ── Side inference, per label ─────────────────────────────────────────────────────────
        var notes  = new List<string>();
        var ports  = new List<PlanarPort>(numbered.Count);
        var owners = new List<LabelShape>(numbered.Count);

        // ── A PORT THAT CANNOT BE RESOLVED IS RECORDED, NOT RETURNED ON ──────────────────────────
        //
        // Owner request, 2026-08-25: "if any ports aren't touching metal, the .cem editor will not
        // list the ports. I'd like to still see them listed, even if they are not on a conductor
        // (and the .cem gives a warning)."
        //
        // This loop used to `return No(...)` at the first port it could not resolve, so a single bad
        // label emptied the panel's whole port list — the user lost sight of the ports they HAD
        // placed at exactly the moment they were trying to fix one of them. It now records the
        // problem against that port's row and carries on, so every drawn port is reported.
        //
        // **`Ports` is still all-or-nothing and the run is still refused.** Below, any problem at all
        // empties `Ports` before returning: a port that is not on metal has no location the mesher
        // could honestly place it at, and a solve over the ports that happened to resolve would be a
        // complete, plausible answer for a structure nobody drew. The refusal TEXT is the first
        // problem found, unchanged, so every existing refusal-wording gate still reads the same
        // sentence.
        var rows   = new List<EmPortRow>(numbered.Count);
        string? firstProblem = null;

        for (int i = 0; i < numbered.Count; i++)
        {
            var (number, label) = numbered[i];
            double x = label.X * perDbu, y = label.Y * perDbu;

            var kind = kindFor?.Invoke(i) ?? PlanarPortKind.Edge;

            // ── A SHUNT PORT STANDS ON A VIA, AND THE VIA IS WHAT IT DRIVES ───────────────────
            //
            // Asked before anything about the metal, for two reasons. The via names its own level,
            // so the multi-level ambiguity below cannot arise for one; and "there is no via here" is
            // a refusal the user can act on immediately, where letting it fall through to the
            // mesh-time refusal would report it only after a solve was attempted.
            int? viaLevel = null;
            bool grownPath = false;
            double? assumedWidthM = null;
            if (kind == PlanarPortKind.Internal)
            {
                viaLevel = GroundViaLevelAt(problem, x, y);

                // ── NO VIA THERE IS THE ORDINARY CASE, NOT AN ERROR ──────────────────────────
                //
                // An internal port is placed on the METAL: "here, referenced to ground". The path
                // down to the plane is the solver's problem, not the user's — with a width to build
                // it at (the technology's own default via size) the run grows one and says so
                // (PlanarGroundPath); the port's answer then includes that path, because a port to
                // ground has to get there somehow.
                //
                // Without a width there is nothing to build and the refusal stands, which is what a
                // headless caller with no technology to ask gets.
                grownPath = viaLevel is null;

                // ── A TECHNOLOGY THAT DECLARES NO VIA SIZE STILL WORKS, AND SAYS WHAT IT USED ──
                //
                // The stackup's own Via ENTRIES are not consulted at all here — they carry a fill,
                // a wall and a span, never a diameter — so "the technology declares no via" is not
                // a reason to refuse a port. What the path needs is a WIDTH, and the honest order to
                // ask for one is: the technology's default drill, its default pad, and failing both,
                // a quarter of the substrate height. That last one is a rule of thumb and is
                // reported as one; it lands at the right ORDER on both a PCB and a MMIC (0.4 mm on
                // 1.6 mm FR-4 against a real 0.3 mm drill; 25 µm on a 100 µm GaAs substrate against
                // a real 60 µm one), which is what a default has to do — the exact number is the
                // user's to fix by drawing a via, and the note says so.
                if (grownPath && groundPathWidthM is null)
                {
                    double h = problem.Slab.HeightM;
                    if (h > 0)
                    {
                        assumedWidthM = 0.25 * h;
                        notes.Add(
                            $"Port {number}'s technology declares no default via size, so its path " +
                            $"down to the ground plane was built {assumedWidthM.Value * 1e6:0.##} µm " +
                            "square — a quarter of the substrate height, which is the right order " +
                            "for a real via but is a rule of thumb rather than this process's own " +
                            "number. That path is real metal and its inductance is part of what this " +
                            "port sees: draw a via where you want a size you chose, or give the " +
                            "technology a default via size.");
                    }
                    else
                    {
                        string portProblem =
                            $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron, displayUnit)} " +
                            "is an internal port, and there is nothing to build its path to the ground " +
                            "plane from: the artwork has no via under it, the technology declares no " +
                            "default via size, and the substrate has no height to fall back on. An " +
                            "internal port's second terminal is the ground plane, so its current has " +
                            "to reach the plane somehow — draw a via there, or make this an edge or " +
                            "internal delta-gap port, which drive current along the metal instead.";
                        firstProblem ??= portProblem;
                        rows.Add(new EmPortRow(number, label, null, portProblem));
                        continue;
                    }
                }
            }

            var (poly, level, containing) = NearestPolygon(problem, x, y);
            if (poly is not null && containing > 1 && viaLevel is null)
            {
                string portProblem =
                    $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron, displayUnit)} " +
                    $"sits on metal on {containing} of this EM setup's {problem.Layers.Count} conductor " +
                    $"levels (" + string.Join(", ", problem.Layers.Select(l => $"'{l.Name}'")) + "). A " +
                    "port's LEVEL is part of its identity: driving the wrong one drives a different " +
                    "conductor with the same footprint, which produces a complete and plausible " +
                    "answer for a structure that was not drawn. Move the label to a point where only " +
                    "the level you mean carries metal, or narrow this setup's analysis levels.";
                firstProblem ??= portProblem;
                rows.Add(new EmPortRow(number, label, null, portProblem));
                continue;
            }

            if (poly is null)
            {
                string portProblem =
                    $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron, displayUnit)} " +
                    "is not on any conductor the EM setup's signal layer. Move it onto the metal, or " +
                    "check that the artwork it names is on a layer bound to a signal conductor in the " +
                    "technology's stackup.";
                firstProblem ??= portProblem;
                rows.Add(new EmPortRow(number, label, null, portProblem));
                continue;
            }

            // The label's OWN direction wins when it has one, and it says so in the note. A port
            // placed by the Port tool has carried one since 2026-08-09 (seeded from the artwork,
            // then the user's to rotate); a null one is every .clay written before that field
            // existed, and still means "infer it from the geometry" — the R-res-5 path below,
            // unchanged, ambiguity refusal included.

            // ── WHICH END vs WHICH WAY ────────────────────────────────────────────────────────
            //
            // For an EDGE port, PlanarPortSide names the end of the conductor the port is on, and
            // inferring it from the nearest boundary is right because that IS what the label is
            // near. For an INTERNAL delta gap the label sits in the middle of the metal, so "nearest
            // boundary" measures nothing about the port — the quantity that matters is only which
            // way positive current crosses the cut, and the label's own direction is the sole
            // honest source of it. A port at the centre of a conductor is equidistant from all four
            // edges, so the corner-ambiguity refusal below would fire on a correctly placed internal
            // port every time; the refusal it gets instead names what is actually missing.
            PlanarPortSide side;
            bool stated = label.PortDirection is not null;

            // AN INTERNAL VIA PORT's polarity is not a direction in the plane at all: its + terminal is the
            // metal and its − terminal is the ground plane, which is the only reading of a port
            // whose second terminal is the ground reference. There is nothing to state and nothing
            // to infer, so a label that happens to carry a direction is simply not read — asking
            // for one would be asking a question with one answer.
            if (kind == PlanarPortKind.Internal)
            {
                side = PlanarPortSide.MinX;
            }
            else if (kind == PlanarPortKind.InternalDeltaGap && !stated)
            {
                string portProblem =
                    $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron, displayUnit)} " +
                    "is an internal delta-gap port with no direction on it. An internal port cuts a " +
                    "gap in the middle of a conductor, so — unlike an edge port — there is no nearby " +
                    "conductor end to infer a direction from, and the direction is what decides which " +
                    "way positive port current crosses the gap. Getting it wrong reverses the sign of " +
                    "everything through this port. Rotate the port to point the way current should " +
                    "flow across the cut.";
                firstProblem ??= portProblem;
                rows.Add(new EmPortRow(number, label, null, portProblem));
                continue;
            }
            else if (stated)
            {
                side = SideFromDirection(label.PortDirection!.Value);
            }
            else if (!TryInferSide(poly, x, y, out side, out string? ambiguity))
            {
                string portProblem =
                    $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron, displayUnit)} " +
                    $"{ambiguity} Which end of the conductor a port names decides which way current " +
                    "flows into the structure, so it is never guessed. Rotate the port to point the " +
                    "way current should flow into the structure, or move the label to the middle of " +
                    "the conductor end you mean, clear of the corner.";
                firstProblem ??= portProblem;
                rows.Add(new EmPortRow(number, label, null, portProblem));
                continue;
            }

            var z0 = z0For?.Invoke(i) ?? new Complex(50, 0);
            ports.Add(new PlanarPort(number, new EmPoint(x, y), side, z0,
                                     problem.Layers.Count > 1 ? viaLevel ?? level : null,
                                     Kind: kind,
                                     GroundPathWidthM: kind == PlanarPortKind.Internal
                                                           ? groundPathWidthM ?? assumedWidthM : null));
            owners.Add(label);
            rows.Add(new EmPortRow(number, label, ports[^1], null));

            notes.Add(kind == PlanarPortKind.Internal
                ? $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron, displayUnit)} is " +
                  "an INTERNAL port — between the metal there and the ground plane" +
                  (problem.Layers.Count > 1 && (viaLevel ?? level) is var lv
                      ? $", on level {lv} ('{problem.Layers[lv].Name}')" : "") +
                  $", at {FormatOhms(z0)}. Its + terminal is the metal and its − terminal is the " +
                  "plane, so the current it drives leaves the conductor vertically rather than along " +
                  "it, and the direction the label points is not read. " +
                  (grownPath
                      ? "There is no via under it, so the run builds the path down to the plane and " +
                        "reports the size it used; that path is real metal and its inductance is part " +
                        "of what this port sees. "
                      : "It stands on a via you drew, and drives that via. ") +
                  "It is not de-embedded."
                : kind == PlanarPortKind.InternalDeltaGap
                ? $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron, displayUnit)} is " +
                  "an INTERNAL delta gap — a cut across the conductor at that point, with metal on " +
                  "both sides" +
                  (problem.Layers.Count > 1 ? $", on level {level} ('{problem.Layers[level].Name}')" : "") +
                  $" — driving current {CurrentDirection(side)} (the port's own direction), at " +
                  $"{FormatOhms(z0)}. It is not de-embedded; the run's own notes say where the gap landed."
                : $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron, displayUnit)} was " +
                  $"taken to be on the conductor's {SideName(side)} end " +
                  (stated ? "(the port's own direction)" : "(inferred from the nearest conductor boundary)") +
                  (problem.Layers.Count > 1 ? $" of level {level} ('{problem.Layers[level].Name}')" : "") +
                  $", driving current {CurrentDirection(side)}, at {FormatOhms(z0)}.");
            // The de-embedding reference plane's position used to be restated on EVERY port note.
            // Dropped (owner request, 2026-08-11): it is a property of the method rather than
            // anything about this port, it never varies, and it belongs in the documentation.
        }

        // Any problem at all refuses the SET — see the accumulator's own note above for why a
        // partial port list must never reach the solver. `Rows` survives either way, which is the
        // whole point: it is the only thing that still knows how many ports the user drew.
        return firstProblem is null
            ? new EmPortExtractionResult(ports, null, notes, owners, rows)
            : new EmPortExtractionResult([], firstProblem, notes, [], rows);
    }

    /// <summary>
    /// <b>The size an internal port's own path to ground is built at: the technology's own default
    /// via.</b> Metres, or null when the technology declares no via size — in which case such a port
    /// is refused rather than given a path of some plausible-looking default, because the path is
    /// real metal whose inductance the answer carries.
    ///
    /// <para>The DEFAULT VIA is the honest choice among the ones available: it is a process
    /// dimension the board would really have, unlike a mesh cell (which would make the port's
    /// inductance a function of the mesh settings) or the substrate height (which is not a via size
    /// at all). A user who wants a different one draws the via.</para>
    /// </summary>
    public static double? DefaultGroundPathWidthM(Technology? tech)
    {
        long dbu = tech?.DefaultViaDrillDbu ?? 0;
        if (dbu <= 0) dbu = tech?.DefaultViaPadDbu ?? 0;
        return dbu > 0 ? dbu / (double)(LayoutUnits.DefaultDbuPerMicron * 1e6) : null;
    }

    /// <summary>
    /// The conductor level of the ground via whose footprint contains this point, or null if no via
    /// to the plane is under it. A via to ANOTHER level is not one of these: an interior via is a
    /// path between two meshed conductors, and a port across it is refused by name in the engine
    /// (<c>PlanarPorts.ViaPortRefusal</c>) for reasons that have nothing to do with this.
    /// </summary>
    private static int? GroundViaLevelAt(PlanarProblem problem, double x, double y)
    {
        foreach (var via in problem.ViaList)
        {
            if (!via.ToGround) continue;
            foreach (var poly in via.Polygons)
                if (poly.Contains(x, y)) return via.UpperLayerIndex;
        }
        return null;
    }

    // ── Numbering ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts <c>1</c>, <c>P1</c>, <c>p1</c>, <c>#1</c>, <c>Port 1</c>. Anything else is a name
    /// rather than a number and is auto-numbered instead — a user who labels a port "gate" gets a
    /// port, not a refusal.
    /// </summary>
    internal static bool TryParseNumber(string? text, out int number)
    {
        number = 0;
        if (text is null) return false;

        string t = text.Trim();
        if (t.StartsWith("port", StringComparison.OrdinalIgnoreCase)) t = t[4..].TrimStart();
        else if (t.Length > 0 && (t[0] is 'P' or 'p' or '#'))         t = t[1..].TrimStart();

        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
               && number > 0;
    }

    // ── Side inference ────────────────────────────────────────────────────────────────────────

    /// <summary>The conductor polygon the label sits in, or — when it sits in none — the nearest one
    /// by bounding box. Null when the geometry is empty.</summary>
    /// <summary>
    /// The conductor a port label names, and — L9d/D2 — WHICH LEVEL it is on.
    ///
    /// <para>A port's level is part of its identity, and with more than one level a label can sit on
    /// metal on several of them. The level is inferred from the polygon the label actually lands on
    /// rather than from the label's own drawing layer, because a port label is annotation and is
    /// routinely drawn on a marker layer that names no conductor at all. Landing on more than one
    /// level is genuinely ambiguous and is reported here rather than picked.</para>
    /// </summary>
    private static (PlanarPolygon? Poly, int Level, int Containing) NearestPolygon(
        PlanarProblem problem, double x, double y)
    {
        PlanarPolygon? best = null, contained = null;
        int bestLevel = 0, containedLevel = 0, containing = 0;
        double bestD = double.PositiveInfinity;

        for (int li = 0; li < problem.Layers.Count; li++)
            foreach (var p in problem.Layers[li].Polygons)
            {
                if (p.Contains(x, y))
                {
                    containing++;
                    if (contained is null) { contained = p; containedLevel = li; }
                    continue;
                }
                double d = BoundaryDistance(p, x, y);
                if (d < bestD) { bestD = d; best = p; bestLevel = li; }
            }

        if (contained is not null) return (contained, containedLevel, containing);
        if (best is null) return (null, 0, 0);

        // A label a long way off the metal is not "nearly on" it. The mesher would place the port on
        // whatever cell the transverse coordinate happens to fall in, which is a silent wrong answer.
        //
        // ── BOTH HALVES OF THIS TEST WERE MEASURED WRONG, AND ONE OF THEM UNBOUNDEDLY ────────
        //
        // Owner report, 2026-08-25: "if I move Port 1 or Port 3 off the metal, I get no live update
        // for bad port (but I do get a warning for port 2)."
        //
        // The distance was to the polygon's BOUNDING BOX and the reach was half that box's smaller
        // side. For a convex shape that is merely loose; **for a concave one it is unbounded** — an
        // L or a tee's bounding box spans its own empty notch, so a port dragged into the notch
        // measures a distance of exactly ZERO and is accepted however far it is from any copper.
        // Measured on the reporter's tee: a label at the far corner of the notch, 1.2 mm from the
        // nearest metal, resolved silently. That is the asymmetry in the report — port 2 sat at the
        // far end, where moving it leaves the bounding box at once, while ports 1 and 3 flank the
        // notch and could never leave it.
        //
        // This is the SAME class of mistake AmbiguityFraction's own note records having made once
        // already: a scale taken from the whole shape's bounding box rather than from the metal.
        // Both are now local to the conductor — the distance is to its actual BOUNDARY, and the reach
        // is Area/Perimeter, which for a strip of width w and length L >> w is w/2. "Within half a
        // trace width of the metal" is a sentence about the conductor; "within half the smaller side
        // of everything this polygon spans" was a sentence about the drawing.
        double reach = CharacteristicHalfWidth(best);
        return bestD <= Math.Max(reach, 0) ? (best, bestLevel, 0) : (null, 0, 0);
    }

    /// <summary>Distance from a point OUTSIDE <paramref name="poly"/> to its nearest boundary
    /// point — the outer ring's segments and every hole's, since a label in a hole is outside the
    /// metal and the hole wall is the nearest copper to it.</summary>
    private static double BoundaryDistance(PlanarPolygon poly, double x, double y)
    {
        double best = RingDistance(poly.Outer, x, y);
        foreach (var h in poly.HoleRings) best = Math.Min(best, RingDistance(h, x, y));
        return best;
    }

    private static double RingDistance(IReadOnlyList<EmPoint> ring, double x, double y)
    {
        double best = double.PositiveInfinity;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = ring[j];
            var b = ring[i];
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double len2 = vx * vx + vy * vy;
            double t = len2 > 0 ? ((x - a.X) * vx + (y - a.Y) * vy) / len2 : 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double dx = x - (a.X + t * vx), dy = y - (a.Y + t * vy);
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// Half the conductor's own characteristic width: <c>Area / Perimeter</c>, which for a strip of
    /// width <c>w</c> and length <c>L >> w</c> is exactly <c>w/2</c> and for a square pad of side
    /// <c>s</c> is <c>s/4</c>. Unlike a bounding-box dimension it does not grow when the SAME trace
    /// is drawn longer, or bent, which is the property the reach needs.
    /// </summary>
    private static double CharacteristicHalfWidth(PlanarPolygon poly)
    {
        double perimeter = RingLength(poly.Outer);
        foreach (var h in poly.HoleRings) perimeter += RingLength(h);
        return perimeter > 0 ? poly.Area() / perimeter : 0;
    }

    private static double RingLength(IReadOnlyList<EmPoint> ring)
    {
        double s = 0;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
            s += Math.Sqrt((ring[i].X - ring[j].X) * (ring[i].X - ring[j].X) +
                           (ring[i].Y - ring[j].Y) * (ring[i].Y - ring[j].Y));
        return s;
    }

    /// <summary>
    /// Which end of the conductor the label names, from its distance to the four sides of that
    /// conductor's own bounding box. Returns false — with the ambiguity worded — when the two
    /// nearest sides are within <see cref="AmbiguityFraction"/> of each other.
    /// </summary>
    internal static bool TryInferSide(PlanarPolygon poly, double x, double y,
                                      out PlanarPortSide side, out string? ambiguity)
    {
        var (x0, y0, x1, y1) = poly.Bounds();

        Span<double> d =
        [
            Math.Abs(x - x0),   // MinX
            Math.Abs(x1 - x),   // MaxX
            Math.Abs(y - y0),   // MinY
            Math.Abs(y1 - y),   // MaxY
        ];
        PlanarPortSide[] sides =
            [PlanarPortSide.MinX, PlanarPortSide.MaxX, PlanarPortSide.MinY, PlanarPortSide.MaxY];

        int best = 0, runner = -1;
        for (int i = 1; i < 4; i++) if (d[i] < d[best]) best = i;
        for (int i = 0; i < 4; i++) if (i != best && (runner < 0 || d[i] < d[runner])) runner = i;

        // The scale is the width of the END the port sits on, not the size of the whole conductor:
        // twice the label's distance to the nearer of the two edges FLANKING the chosen side. For a
        // port at the centre of a line end that is exactly the line width, which is the dimension the
        // question "is this at a corner?" is actually asked in. A label sitting exactly on a flanking
        // edge gives a scale of zero — it IS at a corner — and falls through to the tie below.
        double flankA = best <= 1 ? d[2] : d[0];        // best is MinX/MaxX → flanks are MinY/MaxY
        double flankB = best <= 1 ? d[3] : d[1];
        double scale  = 2.0 * Math.Min(flankA, flankB);
        double tie    = AmbiguityFraction * scale;

        if (d[runner] - d[best] <= tie)
        {
            side = sides[best];
            ambiguity =
                $"sits about equally close to the conductor's {SideName(sides[best])} and " +
                $"{SideName(sides[runner])} edges, so which end it names is ambiguous.";
            return false;
        }

        side = sides[best];
        ambiguity = null;
        return true;
    }

    /// <summary>
    /// A port's stated DIRECTION — the way current flows INTO the structure — is the same quantity
    /// <see cref="PlanarPortSide"/> carries, expressed the way a user points at it. A port whose
    /// current flows +x̂ sits on the conductor's LOW-x end, so the two are inverses of each other and
    /// this is the one place that inversion is written down.
    /// </summary>
    internal static PlanarPortSide SideFromDirection(LayoutRotation direction) => direction switch
    {
        LayoutRotation.R0   => PlanarPortSide.MinX,   // current +x̂ -> low-x end
        LayoutRotation.R90  => PlanarPortSide.MinY,   // current +ŷ -> low-y end
        LayoutRotation.R180 => PlanarPortSide.MaxX,   // current −x̂ -> high-x end
        _                   => PlanarPortSide.MaxY,   // current −ŷ -> high-y end
    };

    private static string SideName(PlanarPortSide s) => s switch
    {
        PlanarPortSide.MinX => "low-x (left)",
        PlanarPortSide.MaxX => "high-x (right)",
        PlanarPortSide.MinY => "low-y (bottom)",
        _                   => "high-y (top)",
    };

    private static string CurrentDirection(PlanarPortSide s) => s switch
    {
        PlanarPortSide.MinX => "in the +x direction",
        PlanarPortSide.MaxX => "in the −x direction",
        PlanarPortSide.MinY => "in the +y direction",
        _                   => "in the −y direction",
    };

    private static string Describe(LabelShape l) => l.Text is { Length: > 0 } t ? t : "unnamed";

    /// <summary>
    /// A port's position in the LAYOUT's own display unit (owner request, 2026-08-11), not a fixed
    /// micron. A user reading "(4496.85, 0 µm)" for a board drawn in mil has to convert before the
    /// number means anything — and the coordinate is only there so they can go and find the label.
    /// </summary>
    private static string Coord(long x, long y, int dbuPerMicron, LayoutUnit unit)
        => $"({LayoutUnits.Format(x, unit, dbuPerMicron)}, " +
           $"{LayoutUnits.Format(y, unit, dbuPerMicron)} {LayoutUnits.Suffix(unit)})";

    private static string FormatOhms(Complex z)
        => z.Imaginary == 0
            ? $"{z.Real.ToString("G6", CultureInfo.InvariantCulture)} Ω"
            : $"{z.Real.ToString("G6", CultureInfo.InvariantCulture)}" +
              $"{(z.Imaginary < 0 ? "-" : "+")}" +
              $"{Math.Abs(z.Imaginary).ToString("G6", CultureInfo.InvariantCulture)}j Ω";
}
