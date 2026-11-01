// L8d follow-up (2026-08-12) — the uniform feed a calibrated port needs, built by the SOLVER
// instead of demanded of the user's artwork.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHY THIS EXISTS. IT IS NOT A CONVENIENCE — WITHOUT IT A TAPER READS AS AN OPEN CIRCUIT
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// D6's peel forms the de-embedded wave matrix as
//
//     y_ij = (S_meas,ij − δ_ij·a₁₁) / (a₂₁(i)·a₂₁(j))
//
// and a₁₁ — the delta gap's EXTERNAL reflection — is measured on a calibration standard, which is an
// ISOLATED UNIFORM LINE of the port's own cross-section (D4). The diagonal is therefore a difference
// of two numbers that both sit within a few 1e-4 of unity, divided by a₂₁², and a₂₁ ∝ ω (the port is
// necessarily a series delta gap). On the owner's 50 → 12 Ω Klopfenstein taper at 1 GHz that divisor
// is 9.8e-5: the peel multiplies any error in a₁₁ by ~10⁴.
//
// So a₁₁ has to be RIGHT to about 1e-5, and it is only right if the DUT's metal actually looks like
// the standard for the distance the standard replaces. When the artwork starts changing width at the
// reference plane — every taper, every Klopfenstein, every part whose port sits on a flare — it does
// not, and the failure is not a mild inaccuracy:
//
//     MKlopf, 2000 mil, 50 → 12 Ω, drawn with the port on the taper's own end face
//       de-embedded  |S₁₁| = 1.0000 … 1.0235,  |S₂₁| = 0.0008 … 0.11,  Σ|S|² up to 1.06
//       the same part with 2 mm of uniform lead
//       de-embedded  |S₁₁| = 0.44,   |S₂₁| = 0.89,  Σ|S|² = 0.992   (and it tracks the analytic model)
//
// A non-passive answer that reads as a perfect open is what a user got, with no refusal.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT IT DOES
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Before meshing, each port's own polygon is EXTENDED outward from its drawn end face by however
// much uniform line the calibration is short of — never more, and nothing at all when the drawn feed
// is already uniform for that distance. The lead is part of the artwork the fill sees, so it is real
// metal with real coupling; afterwards it is removed EXACTLY, because it is a uniform section of the
// port's own cross-section and γ is a quantity the calibration already measured (<see cref="Peel"/>).
//
// **The user's reference plane therefore lands on the DRAWN metal edge**, which is where §10.6 says
// it lands and where a user reading a plot assumes it lands. Nothing about the artwork changes, no
// setting is introduced, and a `.clay` that produced a good answer before produces a BIT-IDENTICAL
// one now (no shortfall ⇒ no lead ⇒ the same problem object).
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT IT DELIBERATELY DOES NOT DO
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Every case it is not certain about is DECLINED rather than guessed, and declining costs nothing:
// it is exactly the behaviour that shipped before this file existed, warning included
// (`PlanarPorts.CheckFeedClearance`). The declines are: a port whose level cannot be determined from
// geometry alone; an end face that is not a single straight segment (so there is no unambiguous
// cross-section to extrude); and a lead that would run into other metal on the same level. Guessing
// any of those would move metal the user drew, which is a worse failure than the one being fixed.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// One port's automatically-added uniform feed: how long it is, and where the user's own metal
/// edge sits, which is the plane the answer must be reported at.
/// </summary>
/// <param name="PortNumber">The port this lead was grown for.</param>
/// <param name="LengthM">How much line was added, outward from <paramref name="DrawnEdgeM"/>.</param>
/// <param name="DrawnEdgeM">The longitudinal coordinate of the user's own metal edge — the plane
/// <see cref="PlanarFeedExtension.Peel"/> brings the answer back to.</param>
/// <param name="ExistingUniformM">How much uniform feed the artwork already had. The lead makes up
/// the difference to the calibration's required run and no more.</param>
public sealed record PlanarFeedLead(
    int    PortNumber,
    double LengthM,
    double DrawnEdgeM,
    double ExistingUniformM)
{
    /// <summary>
    /// <b>R-pcal7-2 — how much of this lead the port's NEIGHBOURHOOD asked for</b>, as against its
    /// cross-section. Zero on every lead grown for the reason this file was written for.
    ///
    /// <para>An init-only member rather than a positional parameter, so every existing construction
    /// site keeps compiling and keeps meaning what it meant. It exists because the two shortfalls
    /// are two different things to have gone wrong and a user chasing a lead needs to know which:
    /// "60 µm on top of 239 µm it already had" means the metal changes width at the plane, while a
    /// lead grown because a coil turn runs 8 µm away is a different sentence and a different
    /// remedy.</para>
    /// </summary>
    public double NeighbourShortfallM { get; init; }

    /// <summary>How much of this lead the port's own CROSS-SECTION asked for — R-fed-1's original
    /// quantity, kept beside <see cref="NeighbourShortfallM"/> so the note can say which of the two
    /// actually set <see cref="LengthM"/> instead of guessing from the arithmetic.</summary>
    public double SectionShortfallM { get; init; }

    /// <summary>How far inward from <see cref="DrawnEdgeM"/> the offending metal
    /// <see cref="NeighbourShortfallM"/> was measured against first appears, and how far it sits
    /// ACROSS the feed. Both NaN when the neighbourhood asked for nothing.</summary>
    public double NeighbourAtM { get; init; } = double.NaN;

    /// <summary>See <see cref="NeighbourAtM"/>.</summary>
    public double NeighbourAcrossM { get; init; } = double.NaN;

    /// <summary>
    /// <b>R-pcal7-3 — the longest lead among the ports this one could share a reference plane
    /// with</b>, when that is what set <see cref="LengthM"/>. Zero otherwise.
    ///
    /// <para>A calibration group is peeled at ONE plane and its error box is modal, so a mode has
    /// one travelled length for the whole group or none — <c>CommonPeelLength</c> refuses a group
    /// whose members' leads differ. R-pcal7-2 produces leads differing by microns routinely, so
    /// without this that refusal would fire on almost every grouped part. Growing the shorter
    /// members to the longest one costs accuracy nothing: a longer lead is still a uniform section
    /// of the same cross-section and is peeled exactly.</para>
    /// </summary>
    public double PeerFloorM { get; init; }

    /// <summary>Whether the neighbourhood term, not the cross-section, is what set
    /// <see cref="LengthM"/>.</summary>
    public bool GrownForNeighbour =>
        NeighbourShortfallM > SectionShortfallM && NeighbourShortfallM >= PeerFloorM;

    /// <summary>Whether a peer's longer lead, not this port's own shortfall, set
    /// <see cref="LengthM"/>.</summary>
    public bool GrownForPeer =>
        PeerFloorM > SectionShortfallM && PeerFloorM > NeighbourShortfallM;
}

/// <summary>R-pw1-2 — <see cref="PlanarFeedExtension.ExtendDetailed"/>'s answer: the problem the mesher sees, the leads
/// grown, the notes, and the ports whose span-width feed could not be grown and which are therefore not de-embedded.</summary>
public sealed record PlanarFeedExtensionResult(
    PlanarProblem                 Problem,
    IReadOnlyList<PlanarFeedLead> Leads,
    IReadOnlyList<string>         Notes,
    IReadOnlySet<int>             NotDeembedded);

public static class PlanarFeedExtension
{
    /// <summary>
    /// How closely the cross-section must hold for the feed to count as uniform, as a fraction of
    /// the port's own width. <b>Tight on purpose.</b> The quantity it is protecting is amplified by
    /// 1/a₂₁² (≈ 10⁴ at 1 GHz), and a drawn uniform line matches its own end face EXACTLY — its
    /// vertices are the same numbers — so there is no fragility in asking for 0.1%. On the owner's
    /// MKlopf the width drifts 2.8% over the calibration's own run, which a loose tolerance would
    /// have waved through.
    /// </summary>
    public const double UniformFractionOfWidth = 1e-3;

    /// <summary>
    /// How finely the run inward from the end face is sampled for uniformity. This sets the
    /// RESOLUTION of the answer, never whether a departure is found — the scan reports the last
    /// station that still matched, so it can credit at most one step of gently-flaring metal as
    /// uniform and the lead comes out at most one step short of the full run (1.6% of it). At the
    /// WIDE end of a taper that is exactly what happens, because a given absolute flare is a smaller
    /// fraction of a wider face; it is measured in <c>ATaperGrowsALeadAtBothPorts…</c> rather than
    /// assumed away.
    /// </summary>
    private const int UniformitySamples = 64;

    /// <summary>
    /// Grow each port's feed to the length the calibration replaces, and return the problem the
    /// mesher should actually see. <b>Returns the input problem unchanged, by reference, when no
    /// port needs anything</b> — which is what makes every previously-recorded number reproducible.
    /// </summary>
    /// <param name="problem">The artwork as drawn.</param>
    /// <param name="ports">The ports as placed. Only <see cref="PlanarPort.Side"/>,
    /// <see cref="PlanarPort.Location"/> and <see cref="PlanarPort.LayerIndex"/> are read.</param>
    /// <param name="calibration">Supplies <see cref="PlanarCalibrationSettings.EndRunHeights"/> —
    /// the same setting that decides how much feed the standard reproduces, so the two cannot drift.</param>
    /// <param name="lengthFormat">Owner request, 2026-08-15 — every distance this method's own notes
    /// quote goes through this. See <see cref="SurfaceMesher.Mesh"/>'s own parameter of the same
    /// name.</param>
    public static (PlanarProblem Problem, IReadOnlyList<PlanarFeedLead> Leads, IReadOnlyList<string> Notes)
        Extend(PlanarProblem problem,
               IReadOnlyList<PlanarPort> ports,
               PlanarCalibrationSettings? calibration = null,
               SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        var r = ExtendDetailed(problem, ports, calibration, lengthFormat);
        return (r.Problem, r.Leads, r.Notes);
    }

    /// <summary>
    /// <see cref="Extend"/>, and the ports that must NOT be de-embedded: brief-port-width-planar-and-c3d R-pw1-2 — a
    /// port narrower than its edge (<see cref="PlanarPort.Span"/>) whose span-width feed could not be grown. Its
    /// standard would be a line of the span's width while the metal beyond its cut is the whole conductor, so it is
    /// reported at its cut instead, and the notes say so by port number.
    /// </summary>
    public static PlanarFeedExtensionResult ExtendDetailed(PlanarProblem problem,
                                                           IReadOnlyList<PlanarPort> ports,
                                                           PlanarCalibrationSettings? calibration = null,
                                                           SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(ports);

        var cal       = calibration ?? PlanarCalibrationSettings.Default;
        double wanted = cal.EndRunHeights * problem.Slab.HeightM;
        if (!(wanted > 0)) return new(problem, [], [], new HashSet<int>());

        // ── R-pcal7-2 — ONE FIXED-POINT STEP, AND DELIBERATELY NOT A LOOP ────────────────────────
        //
        // The neighbourhood shortfall is measured on the artwork as DRAWN, which is the only state
        // that exists before any lead has been grown. But every other port's lead is metal too, and
        // a port whose obstruction is another port's PARALLEL feed can never be cleared by growing:
        // the obstruction grows with it, forever. So the leads are computed once, applied, and the
        // question asked again from each port's NEW outer edge — and a port whose neighbourhood is
        // still not clear has its neighbourhood term DROPPED rather than lengthened. It keeps
        // whatever its cross-section asked for, the post-mesh clearance check breaches as it did
        // before, and the port goes to PCAL4's calibration group (R-pcal7-3) or to the refusal.
        //
        // That is what makes non-convergence DETECTABLE instead of infinite, and it is why this is
        // one step rather than an iteration to a limit: a second step could only re-discover the
        // same parallel obstruction, and a lead that keeps growing is a mesh that keeps growing.
        var pass = Core(problem, ports, cal, wanted, null, null, lengthFormat);

        IReadOnlyDictionary<int, bool>? keep = null;
        if (pass.Neighbour is { } asked)
        {
            var k = new Dictionary<int, bool>();
            bool changed = false;
            foreach (var (num, shortfall) in asked)
            {
                bool clear = shortfall <= 0 || pass.StillClear.Contains(num);
                k[num]   = clear;
                changed |= !clear;
            }
            if (changed) { keep = k; pass = Core(problem, ports, cal, wanted, keep, null, lengthFormat); }
        }

        // ── R-pcal7-3 — EVERY PORT THAT COULD SHARE A PLANE PEELS THE SAME LENGTH ────────────────
        //
        // Asked once, of the leads the passes above settled on, and acted on by running the same
        // measurement again with a floor — rather than by editing the polygons a second time, which
        // would be a second spelling of the extrusion and a second chance for it to differ.
        var floors = CoplanarFloors(ports, pass.Leads);
        if (floors is not null) pass = Core(problem, ports, cal, wanted, keep, floors, lengthFormat);

        return new(pass.Problem, pass.Leads, pass.Notes, pass.NotDeembedded);
    }

    /// <summary>
    /// <b>The longest lead among the ports that could share ONE reference plane</b>, per port, or
    /// null when nothing has to grow.
    ///
    /// <para>"Could share a plane" is <c>TryFormCalibrationGroup</c>'s own test asked of the
    /// artwork: the same conductor level, the same side, the same direction, and an end face at the
    /// same station. Two ports whose DRAWN edges differ are declined by the group's plane test
    /// whatever their leads are (PCAL5), so equalising them would grow metal for a group that
    /// cannot form — this is deliberately not attempted there.</para>
    /// </summary>
    private static IReadOnlyDictionary<int, double>? CoplanarFloors(
        IReadOnlyList<PlanarPort> ports, IReadOnlyList<PlanarFeedLead> leads)
    {
        if (leads.Count == 0) return null;

        Dictionary<int, double>? floors = null;
        foreach (var lead in leads)
        {
            var port = ports.FirstOrDefault(q => q.Number == lead.PortNumber);
            if (port is null) continue;

            foreach (var peer in ports)
            {
                if (peer.Number == lead.PortNumber) continue;
                if (peer.Kind != PlanarPortKind.Edge || port.Kind != PlanarPortKind.Edge) continue;
                if (peer.Side != port.Side || peer.Direction != port.Direction) continue;
                if (peer.LayerIndex != port.LayerIndex) continue;

                bool alongX = port.Direction == PlanarBasisDirection.X;
                double peerS = alongX ? peer.Location.X : peer.Location.Y;
                if (Math.Abs(peerS - lead.DrawnEdgeM) > 1e-9 * Math.Max(1.0, Math.Abs(lead.DrawnEdgeM)))
                    continue;

                double have = 0;
                foreach (var l in leads) if (l.PortNumber == peer.Number) { have = l.LengthM; break; }
                if (have >= lead.LengthM) continue;

                floors ??= [];
                floors[peer.Number] = Math.Max(floors.GetValueOrDefault(peer.Number), lead.LengthM);
            }
        }
        return floors;
    }

    private readonly record struct CorePass(
        PlanarProblem Problem,
        IReadOnlyList<PlanarFeedLead> Leads,
        IReadOnlyList<string> Notes,
        IReadOnlyDictionary<int, double>? Neighbour,
        IReadOnlySet<int> StillClear,
        IReadOnlySet<int> NotDeembedded);

    /// <summary>
    /// One pass of the extension: measure every port's two shortfalls, take the larger, and extrude.
    /// <paramref name="allowNeighbour"/> is null on the first pass and, on the second, carries the
    /// verdict of the fixed-point step for each port that grew a neighbourhood lead.
    /// </summary>
    private static CorePass Core(PlanarProblem problem,
                                 IReadOnlyList<PlanarPort> ports,
                                 PlanarCalibrationSettings cal,
                                 double wanted,
                                 IReadOnlyDictionary<int, bool>? allowNeighbour,
                                 IReadOnlyDictionary<int, double>? peerFloor,
                                 SurfaceMesher.PlanarLengthFormat? lengthFormat)
    {
        var fmt = lengthFormat ?? SurfaceMesher.DefaultLengthFormat;
        double drivenM  = cal.DrivenNeighbourClearanceHeights  * problem.Slab.HeightM;
        double passiveM = cal.PassiveNeighbourClearanceHeights * problem.Slab.HeightM;

        var neighbourAsked = new Dictionary<int, double>();
        var stillClear     = new HashSet<int>();
        var notDeembedded  = new HashSet<int>();

        var leads = new List<PlanarFeedLead>();
        var notes = new List<string>();

        // R-pw1-2 — never silently de-embedded at the edge's width: the port is reported at its cut, by number.
        void DeclineSpan(int number, string why)
        {
            if (!notDeembedded.Add(number)) return;
            notes.Add($"Port {number} is narrower than the edge it stands on, and the feed its calibration needs could " +
                      $"not be grown at its width: {why}. It is NOT de-embedded: its s-parameters are reported at its " +
                      "cut, in the reference impedance declared for it. Check Use edge width to drive the whole edge, " +
                      "or clear the metal behind the port.");
        }

        // Layers are records holding a polygon list; a layer is only rebuilt if a port grew on it.
        List<PlanarPolygon>?[] edited = new List<PlanarPolygon>?[problem.Layers.Count];

        foreach (var port in ports)
        {
            IReadOnlyList<PlanarPolygon> PolysOn(int level) =>
                edited[level] is { } e ? e : problem.Layers[level].Polygons;

            // NEITHER internal port is fed from outside the metal, so there is nothing to extend
            // and nothing that would be calibrated if there were. Growing a lead at the conductor's
            // end because an internal gap happens to name a DIRECTION along that axis would move
            // metal the user drew, for a port whose answer does not pass through a calibration at
            // all; an internal port does not even name an in-plane direction, and the end face this
            // would extrude is not on its current path.
            if (port.Kind != PlanarPortKind.Edge) continue;

            bool alongX  = port.Direction == PlanarBasisDirection.X;
            bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;

            // ── RP-2c — A CONDUCTOR-REFERENCED PORT HAS TWO END FACES, AND THEY GROW TOGETHER ────
            //
            // The two cuts must stay at ONE station (R-rp2a-2), so growing the signal conductor's
            // lead alone would skew the pair and the port would be refused at resolution — with a
            // message about the user's own artwork, which is now not what moved it. Both terminals
            // are measured, the SHORTFALL is the larger of the two, and both are extruded by it: the
            // lead is a uniform section of the port's own cross-section, and a coplanar port's
            // cross-section is the pair.
            var terminals = new List<PlanarPort> { port };
            if (port.IsConductorReferenced && port.NegativeLocation is { } negAt)
                terminals.Add(port with { Location = negAt, LayerIndex = port.NegativeLayerIndex });

            // Measured first, edited afterwards: two terminals on one level must both see the
            // pre-edit polygons, and both extrusions replace their own index in place.
            var measured = new List<(int Layer, int PolyIndex, int VertexA,
                                     double EdgeS, double TLo, double THi, double Have, bool Tab)>();
            double add = 0;
            bool usable = true;

            // R-pw1-2 — a port narrower than its edge has its feed grown at the span's width, centred on the span: a tab
            // on the edge rather than the whole face pushed out. Not for a two-cut port, whose pair is one cross-section.
            var span = terminals.Count == 1 ? port.EdgeSpan : null;
            bool spanNarrower = false;

            foreach (var terminal in terminals)
            {
                if (!TryLevelOf(problem, terminal, out int layer)) { usable = false; break; }

                var tPolys = PolysOn(layer);
                if (!TryEndFace(tPolys, terminal, out int polyIndex, out int vertexA, out double edgeS,
                                out double tLo, out double tHi))
                { usable = false; break; }

                if (span is { } sp)
                {
                    double a = Math.Max(tLo, sp.LoM), b = Math.Min(tHi, sp.HiM);
                    if (!(b > a)) { usable = false; break; }
                    spanNarrower = (b - a) < (tHi - tLo) * (1 - UniformFractionOfWidth);
                    (tLo, tHi) = (a, b);
                }

                if (!(tHi - tLo > 0)) { usable = false; break; }

                double have = UniformRun(tPolys[polyIndex], alongX, fromLow, edgeS, tLo, tHi, wanted);
                measured.Add((layer, polyIndex, vertexA, edgeS, tLo, tHi, have, spanNarrower));
                add = Math.Max(add, wanted - have);
            }

            if (!usable)
            {
                if (span is not null) DeclineSpan(port.Number,
                    "its edge is not one straight segment containing the port's width, so there is no face to grow it from");
                continue;
            }

            // ── R-pcal7-2 — THE SECOND SHORTFALL: THE FEED'S NEIGHBOURHOOD, NOT ITS CROSS-SECTION ──
            //
            // R-fed-1 asked one question — "is this feed the same width for the run the standard
            // replaces?" — and that is only half of what makes a calibration standard describe the
            // feed. The standard is an ISOLATED uniform line, so the other half is "is anything
            // beside it for that run?", and the two failures amplify identically: the error box is
            // measured on the wrong structure and D6's peel divides the mismatch by a₂₁².
            //
            // A spiral inductor is the case that reached a user. Its port sits on the outer turn,
            // and 8 µm away — for hundreds of microns — is the next turn, which is the port's OWN
            // NET. PCAL2's clearance check used to skip own-net metal entirely and reported both
            // feeds clear while the published `.s2p` was an open circuit with a negative resistance
            // at every AC point. R-pcal7-1 makes that metal visible; this is what does something
            // about it.
            //
            // <b>The rule is exact rather than a heuristic: L ≥ endRun − u*</b>, where u* is how far
            // inward from the drawn edge the offending metal first appears. Growing the feed by that
            // much puts the whole of the standard's own run on line that has nothing beside it, and
            // the lead comes off again as a matched section of the very line the calibration
            // measured — so the user's reference plane stays on the user's own drawn metal edge.
            double sectionAdd = add;
            double neighbourAdd = 0, atM = double.NaN, acrossM = double.NaN;
            if (allowNeighbour is null || (allowNeighbour.TryGetValue(port.Number, out bool ok) && ok))
            {
                foreach (var m in measured)
                {
                    var reach = NeighbourhoodRun(problem, PolysOn, ports, m.Layer, alongX, fromLow,
                                                 m.EdgeS, m.TLo, m.THi, wanted, drivenM, passiveM);
                    if (!(wanted - reach.ClearForM > neighbourAdd)) continue;
                    neighbourAdd = wanted - reach.ClearForM;
                    atM          = reach.AtM;
                    acrossM      = reach.AcrossM;
                }
            }

            if (neighbourAdd > 0) neighbourAsked[port.Number] = neighbourAdd;
            add = Math.Max(add, neighbourAdd);

            double floor = peerFloor is not null && peerFloor.TryGetValue(port.Number, out double fl) ? fl : 0;
            add = Math.Max(add, floor);

            // Nothing shorter than the scan's own step, which is the resolution `have` was measured
            // at — below it the "shortfall" is quantisation, and growing a sliver of lead would put
            // the reference plane inside a single mesh cell for no gain.
            if (add <= wanted / UniformitySamples) continue;

            double outward = fromLow ? -add : add;

            bool blocked = false;
            foreach (var m in measured)
                if (Obstructed(PolysOn(m.Layer), alongX, m.EdgeS, m.EdgeS + outward, m.TLo, m.THi))
                { blocked = true; break; }

            if (blocked && spanNarrower)
            {
                DeclineSpan(port.Number, "there is other metal on this level directly behind the port");
                continue;
            }

            if (blocked)
            {
                notes.Add(
                    $"Port {port.Number}'s feed is not uniform for the {fmt(wanted)} the " +
                    "calibration replaces, and the uniform lead that would fix it cannot be grown — " +
                    "there is other metal on this level directly behind the port. The de-embedding " +
                    "therefore removes an error box measured on a straight line from a feed that is " +
                    "not one; read the result knowing that, or move the neighbouring metal." +
                    (terminals.Count > 1
                        ? " This port returns through drawn metal, so both of its conductors have to " +
                          "grow by the same amount to keep the two cuts at one station; neither grew."
                        : ""));
                continue;
            }

            foreach (var m in measured)
            {
                var mPolys = PolysOn(m.Layer);
                var grown  = m.Tab
                    ? ExtrudeTab(mPolys[m.PolyIndex], m.VertexA, alongX, outward, m.TLo, m.THi)
                    : ExtrudeFace(mPolys[m.PolyIndex], m.VertexA, alongX, outward);
                var list   = edited[m.Layer] ??= [.. mPolys];
                list[m.PolyIndex] = grown;
            }

            leads.Add(new PlanarFeedLead(port.Number, add, measured[0].EdgeS, measured[0].Have)
            {
                SectionShortfallM   = sectionAdd,
                PeerFloorM          = floor,
                NeighbourShortfallM = neighbourAdd,
                NeighbourAtM        = atM,
                NeighbourAcrossM    = acrossM,
            });
        }

        if (leads.Count == 0)
            return new CorePass(problem, [], notes,
                                neighbourAsked.Count == 0 ? null : neighbourAsked, stillClear, notDeembedded);

        var layers = new PlanarConductorLayer[problem.Layers.Count];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = edited[i] is { } e
                ? problem.Layers[i] with { Polygons = e }
                : problem.Layers[i];

        var grownProblem = problem with { Layers = layers };

        // ── The fixed-point question, asked ONCE, from each port's NEW outer edge ────────────────
        //
        // Only of the ports that grew a neighbourhood lead, and only on the first pass — the second
        // pass is acting on the answer and must not re-derive it.
        if (allowNeighbour is null && neighbourAsked.Count > 0)
            foreach (var lead in leads)
            {
                if (!neighbourAsked.ContainsKey(lead.PortNumber)) continue;

                var port = ports.FirstOrDefault(q => q.Number == lead.PortNumber);
                if (port is null) continue;

                bool alongX  = port.Direction == PlanarBasisDirection.X;
                bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;
                double newEdge = fromLow ? lead.DrawnEdgeM - lead.LengthM
                                         : lead.DrawnEdgeM + lead.LengthM;

                if (!TryLevelOf(grownProblem, port, out int layer)) continue;
                var gPolys = grownProblem.Layers[layer].Polygons;
                if (!TryEndFace(gPolys, port, out _, out _, out _, out double gLo, out double gHi))
                    continue;

                var again = NeighbourhoodRun(grownProblem, l => grownProblem.Layers[l].Polygons,
                                             ports, layer, alongX, fromLow, newEdge, gLo, gHi,
                                             wanted, drivenM, passiveM);
                if (again.ClearForM >= wanted) stillClear.Add(lead.PortNumber);
            }

        notes.Add(FeedNote(leads, wanted, fmt));
        return new CorePass(grownProblem, leads, notes,
                            neighbourAsked.Count == 0 ? null : neighbourAsked, stillClear, notDeembedded);
    }

    /// <summary>
    /// <b>R-pcal7-2 — how far inward from the port's own end face the feed has nothing beside it</b>,
    /// capped at <paramref name="wanted"/>, together with where the first offending metal is.
    ///
    /// <para>This is <see cref="PlanarPorts.MeasureFeedClearance"/>'s question asked of the ARTWORK
    /// rather than of the mesh, because the lead has to be grown before there is a mesh to ask. The
    /// post-mesh check then CONFIRMS — and where it still breaches, that is the refusal, with no
    /// loop. The two are deliberately the same predicate with the same two thresholds: a scan that
    /// measured something the clearance check does not would grow leads nothing needed, and one that
    /// measured less would grow leads that do not clear.</para>
    ///
    /// <para><b>Metal that OVERLAPS the feed's own transverse span is not a neighbour here</b>, for
    /// the same reason it is not one there: it is in line with the feed, which is
    /// <see cref="UniformRun"/>'s question and R-fed-1's answer.</para>
    ///
    /// <para><b>The class is read off the artwork, and it is read conservatively.</b> A polygon that
    /// a port's own label sits on takes the DRIVEN threshold; everything else takes the PASSIVE one.
    /// On the mesh the same question is asked of the CONDUCTOR — every polygon its component
    /// reaches, through touching metal and through vias — and there is no connectivity on a bare
    /// polygon list to ask it of. The two can therefore disagree in exactly one direction: a
    /// port-carrying net drawn as several polygons, whose offending piece carries no port itself,
    /// is measured against the passive threshold here and the driven one there. <b>That is a lead
    /// not grown, never a lead grown wrongly</b> — the run then breaches the post-mesh check and is
    /// refused by name, which is the behaviour that shipped before this brief. A silently published
    /// wrong answer is not reachable from the disagreement.</para>
    /// </summary>
    private static (double ClearForM, double AtM, double AcrossM) NeighbourhoodRun(
        PlanarProblem problem, Func<int, IReadOnlyList<PlanarPolygon>> polysOn,
        IReadOnlyList<PlanarPort> ports, int level, bool alongX, bool fromLow,
        double edgeS, double tLo, double tHi, double wanted, double drivenM, double passiveM)
    {
        double dir      = fromLow ? 1.0 : -1.0;
        double width    = tHi - tLo;
        double sliceTol = 1e-9 * Math.Max(width, 1e-12);
        double tMid     = 0.5 * (tLo + tHi);

        bool   onFeed = true;
        double last   = wanted;

        for (int k = 1; k <= UniformitySamples; k++)
        {
            double d = wanted * k / UniformitySamples;
            double s = edgeS + dir * d;

            // ── THE FEED'S OWN CROSS-SECTION AT THIS STATION, AND NOTHING ELSE ───────────────────
            //
            // The maximal run of metal containing the port's own transverse midpoint, ON THE PORT'S
            // OWN LEVEL. A taper's flare, a pad, a bend's corner are all this run getting wider —
            // metal IN LINE WITH the feed, which is R-fed-1's question and which UniformRun already
            // measures and grows a lead for. Counting it as a neighbour instead would re-fire the
            // clearance refusal on every taper in the repository, which is what the skip this brief
            // removes was protecting.
            var band = onFeed ? FeedSpan(polysOn(level), alongX, s, tMid, sliceTol) : null;

            // ── WHERE THE FEED ENDS, EVERYTHING AFTER IT IS SOMETHING ELSE ──────────────────────
            //
            // A pad that stops and a coil that starts 10 µm later is not a wider feed; it is a
            // different structure in the feed's own path, reached through a via on another level.
            // UniformRun reads it as a SHORT feed and says so by design (a line shorter than the
            // standard's run is not a non-uniform one), so without this the shipped spiral's second
            // port measured "uniform for the whole 300 µm" while its lead ran into the coil body
            // 40 µm in. Once the run containing the midpoint is gone it does not come back: metal
            // at the midpoint after a gap is a neighbour at zero clearance, not a resumption.
            if (band is null) onFeed = false;

            for (int lv = 0; lv < problem.Layers.Count; lv++)
            {
                var polys = polysOn(lv);
                for (int pi = 0; pi < polys.Count; pi++)
                {
                    double need = CarriesAPort(problem, polys[pi], lv, ports) ? drivenM : passiveM;
                    if (!(need > 0)) continue;

                    foreach (var (a, b) in SpansAcross(polys[pi], alongX, s, sliceTol))
                    {
                        // The feed itself, and the feed's own run when it is still in one piece.
                        if (a >= tLo - sliceTol && b <= tHi + sliceTol) continue;
                        if (lv == level && band is { } bd &&
                            a >= bd.A - sliceTol && b <= bd.B + sliceTol) continue;

                        double across = b <= tLo ? tLo - b : a >= tHi ? a - tHi : 0;
                        if (across < need)
                            return (k == 1 ? 0 : wanted * (k - 1) / UniformitySamples, d, across);
                    }
                }
            }
            last = d;
        }
        return (last, double.NaN, double.NaN);
    }

    /// <summary>The transverse extent of the metal covering the port's own midpoint at this
    /// station, or null where none does — which is the feed having ended.</summary>
    private static (double A, double B)? FeedSpan(IReadOnlyList<PlanarPolygon> polys, bool alongX,
                                                  double s, double tMid, double tol)
    {
        foreach (var poly in polys)
            foreach (var (a, b) in SpansAcross(poly, alongX, s, tol))
                if (tMid >= a - tol && tMid <= b + tol) return (a, b);
        return null;
    }

    /// <summary>Whether any port stands on this polygon — see <see cref="NeighbourhoodRun"/> on why
    /// the question is asked of the POLYGON here and of the conductor after meshing.</summary>
    private static bool CarriesAPort(PlanarProblem problem, PlanarPolygon poly, int level,
                                     IReadOnlyList<PlanarPort> ports)
    {
        var (bx0, by0, bx1, by1) = problem.Bounds();
        double tol = 1e-6 * Math.Max(Math.Max(bx1 - bx0, by1 - by0), 1e-12);

        foreach (var port in ports)
        {
            if (port.LayerIndex is { } given && given != level) continue;
            if (!Polygon2D.ContainsOrOn(poly.Outer, port.Location, tol)) continue;
            bool inHole = false;
            foreach (var h in poly.HoleRings)
                if (Polygon2D.ContainsStrict(h, port.Location)) { inHole = true; break; }
            if (!inHole) return true;
        }
        return false;
    }

    /// <summary>
    /// <b>R-fed-2 — how much of one port's grown lead sits between its reference plane and the
    /// user's own drawn metal edge</b>, which is what <see cref="Peel"/> takes back off.
    ///
    /// <para>The plane is one CELL in from the metal (D2), and after <see cref="Extend"/> the metal
    /// is the lead's outer end, so what comes off is the lead MINUS that outermost cell — the half
    /// the error box already owns. It is measured from the resolution rather than assumed, because
    /// the outermost cell's size is the mesher's decision and edge grading makes it small.</para>
    ///
    /// <para><b>A NEGATIVE result is meaningful and is returned as one</b>: the outermost cell is
    /// longer than the whole lead, so the plane landed inside the user's drawn metal. Peeling a
    /// negative length would add line that is not uniform there, so every caller declines instead —
    /// which is a different sentence in each of them, and is why this returns the number rather than
    /// clamping it.</para>
    ///
    /// <para>It exists as a function because two callers need the same quantity at different times:
    /// <c>PlanarSolve</c>'s peel, and PCAL4's calibration-group formation, which has to know whether
    /// every member of a group peels the SAME length before it may form one. Two spellings of this
    /// subtraction would be two chances for the group's gate and the peel it gates to disagree.</para>
    /// </summary>
    public static double PeelLengthM(PlanarPortResolution port, PlanarFeedLead? lead)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (lead is null || !(lead.LengthM > 0)) return 0;

        return port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY
            ? lead.DrawnEdgeM - port.ReferencePlaneM
            : port.ReferencePlaneM - lead.DrawnEdgeM;
    }

    /// <summary>
    /// <b>What <see cref="CommonPeelLength"/> found</b>: the one length a calibration group may be
    /// peeled by, or which member made that impossible.
    /// </summary>
    /// <param name="LengthM">The common peel length, when <see cref="Ok"/>.</param>
    /// <param name="UnequalPort">The first member whose peel differs from the rest, or 0.</param>
    /// <param name="UnequalLengthM">That member's own peel length.</param>
    /// <param name="NegativePort">The first member whose plane landed inside its drawn metal, or 0.
    /// There is no positive length to peel there at all.</param>
    public readonly record struct PlanarGroupPeel(
        double LengthM, int UnequalPort = 0, double UnequalLengthM = 0, int NegativePort = 0)
    {
        /// <summary>One length describes the whole group, so it may be peeled.</summary>
        public bool Ok => UnequalPort == 0 && NegativePort == 0;
    }

    /// <summary>
    /// <b>PCAL5 — the single length a calibration group's automatic feed leads may be peeled by.</b>
    ///
    /// <para>A group's error box is MODAL: after <c>PlanarDeembed.ApplyBlocks</c> each of its rows is
    /// a MODE, and a mode runs on every conductor of the group at once. So "how far along has this
    /// travelled" has one answer for the group or none, and <see cref="Peel"/> — which is index-wise
    /// — can only express the one. Where the members' leads differ, the longer one is also beside no
    /// second conductor for part of its run, so the grown region is not one cross-section either;
    /// the arithmetic and the geometry fail together, which is why one test covers both.</para>
    ///
    /// <para><b>A group NONE of whose members grew a lead reads 0 and is Ok</b>, which is the
    /// overwhelmingly common answer and is PCAL4 unchanged.</para>
    ///
    /// <para><b>On reachability, so nobody removes this as dead code.</b> Ordinary artwork does not
    /// get here: a group already requires its members to share a reference plane, the plane is one
    /// cell in from the lead's outer end, and <see cref="Extend"/> quantises how much lead it grows —
    /// so two members with different DRAWN edges come out at outer ends a few µm apart and are
    /// declined by the plane test first. Measured while trying to build a fixture for this: pads
    /// 300 µm apart in length gave leads of 2151.563 µm and 1856.25 µm, i.e. outer ends 4.69 µm
    /// apart. This is a guard on <see cref="Peel"/>'s own precondition, not a case anyone reaches by
    /// drawing — and it is what would stop a silent wrong peel if the plane tolerance were ever
    /// loosened or the lead's quantisation changed.</para>
    /// </summary>
    /// <param name="members">The group's port resolutions, in any order.</param>
    /// <param name="leads">Every lead the run grew, or null when it grew none.</param>
    /// <param name="toleranceM">How far apart two members' peel lengths may be and still count as
    /// one. A round-off tolerance only — see <c>PlanarSolve.GroupPeelToleranceFraction</c>.</param>
    public static PlanarGroupPeel CommonPeelLength(
        IReadOnlyList<PlanarPortResolution> members,
        IReadOnlyList<PlanarFeedLead>? leads,
        double toleranceM)
    {
        ArgumentNullException.ThrowIfNull(members);

        double common = double.NaN;
        foreach (var member in members)
        {
            PlanarFeedLead? lead = null;
            if (leads is not null)
                foreach (var l in leads) if (l.PortNumber == member.Number) { lead = l; break; }

            double peel = PeelLengthM(member, lead);
            if (peel < 0) return new PlanarGroupPeel(0, NegativePort: member.Number);

            if (double.IsNaN(common)) { common = peel; continue; }
            if (Math.Abs(peel - common) > toleranceM)
                return new PlanarGroupPeel(common, member.Number, peel);
        }

        return new PlanarGroupPeel(double.IsNaN(common) ? 0 : common);
    }

    /// <summary>
    /// R-fed-2 — remove the leads <see cref="Extend"/> grew, bringing every reference plane back to
    /// the user's own drawn metal edge.
    ///
    /// <para>This is exact rather than approximate, and that is the whole reason the lead is allowed
    /// to exist: it is a UNIFORM section of the port's own cross-section, so it is the matched line
    /// D6's algebra already assumes, and γ is not estimated — it is the value the two-line
    /// calibration measured for this very cross-section. Cascading a matched line of length ℓ onto
    /// port i multiplies <c>S_ij</c> by <c>e^{−(γ_iℓ_i + γ_jℓ_j)}</c>, so removing it multiplies by
    /// the reciprocal.</para>
    ///
    /// <para><b>Called on S referenced to each port's own Z_c, BEFORE renormalisation.</b> "Matched"
    /// means matched in Z_c; doing this after <c>Renormalise</c> would be peeling a line in the wrong
    /// reference and would put a reflection back that was never there.</para>
    /// </summary>
    /// <param name="sAtZc">De-embedded S at the leads' outer reference planes, referenced to Z_c.</param>
    /// <param name="lengthsM">Per matrix index, how much lead sits between the reference plane and
    /// the drawn edge. Zero where none was grown.</param>
    /// <param name="gamma">Per matrix index, the propagation constant its calibration measured.
    ///
    /// <para><b>On a CALIBRATION GROUP's rows the index is a MODE, not a port</b> (PCAL4 —
    /// <c>PlanarDeembed.ApplyBlocks</c> hands back the group's rows in its modal basis, and this is
    /// called before <c>ModalToTerminal</c>). So the entry is that mode's γ_m, and <b>every member of
    /// one group must carry the same length</b>: a mode is a combination of the group's conductors,
    /// so "how far along has this mode travelled" has one answer for the group or none. That is
    /// gated where the group forms, not here.</para></param>
    public static Mat<Complex> Peel(Mat<Complex> sAtZc,
                                    IReadOnlyList<double> lengthsM,
                                    IReadOnlyList<Complex> gamma)
    {
        ArgumentNullException.ThrowIfNull(lengthsM);
        ArgumentNullException.ThrowIfNull(gamma);

        int p = sAtZc.RowCount;
        if (lengthsM.Count != p || gamma.Count != p)
            throw new ArgumentException($"{p} ports need {p} lengths and {p} propagation constants.");

        var theta = new Complex[p];
        bool any = false;
        for (int i = 0; i < p; i++)
        {
            theta[i] = gamma[i] * lengthsM[i];
            any |= lengthsM[i] != 0;
        }
        if (!any) return sAtZc;

        var s = new Mat<Complex>(p, p);
        for (int i = 0; i < p; i++)
            for (int j = 0; j < p; j++)
                s[i, j] = sAtZc[i, j] * Complex.Exp(theta[i] + theta[j]);
        return s;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Geometry
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Which conductor level the port drives, WITHOUT a mesh. The explicit answer wins; otherwise
    /// exactly one level may carry metal at the port's own point. Two candidates is the same
    /// ambiguity <c>PlanarPorts</c> refuses by name — declined here rather than pre-empted, so the
    /// user still gets that refusal's wording rather than a silently extended structure.
    /// </summary>
    private static bool TryLevelOf(PlanarProblem problem, PlanarPort port, out int layer)
    {
        layer = -1;
        if (port.LayerIndex is { } given)
        {
            if (given < 0 || given >= problem.Layers.Count) return false;
            layer = given;
            return true;
        }

        // ── ContainsOrOn, not Contains, and it is not a nicety ──────────────────────────────────
        //
        // A port label sits ON the conductor's end face — that is what a port IS — so the test point
        // is exactly on the boundary, where an even-odd ray cast is decided by which way the ray
        // happens to leave. It answered "inside" at a MinX end and "outside" at a MaxX one, so a
        // symmetric taper grew a lead at one port only, which is the worst of both behaviours.
        var (bx0, by0, bx1, by1) = problem.Bounds();
        double tol = 1e-6 * Math.Max(Math.Max(bx1 - bx0, by1 - by0), 1e-12);

        for (int i = 0; i < problem.Layers.Count; i++)
        {
            bool on = false;
            foreach (var poly in problem.Layers[i].Polygons)
            {
                if (!Polygon2D.ContainsOrOn(poly.Outer, port.Location, tol)) continue;
                bool inHole = false;
                foreach (var h in poly.HoleRings)
                    if (Polygon2D.ContainsStrict(h, port.Location)) { inHole = true; break; }
                if (!inHole) { on = true; break; }
            }
            if (!on) continue;
            if (layer >= 0) return false;                 // ambiguous
            layer = i;
        }
        return layer >= 0;
    }

    /// <summary>
    /// The polygon the port drives and its END FACE: the first metal met marching in from the named
    /// side along the port's own transverse line, required to be a SINGLE straight segment of the
    /// outline lying on that face.
    ///
    /// <para>The single-segment requirement is what makes the extrusion unambiguous — a face made of
    /// several collinear edges, or one the ring visits twice, has no one cross-section to extrude,
    /// and inventing one would move metal the user drew. Every drawn feed end (MLIN, MTaper, MKlopf,
    /// a plain rectangle) is a single segment, so this declines only the genuinely odd cases.</para>
    /// </summary>
    private static bool TryEndFace(IReadOnlyList<PlanarPolygon> polys, PlanarPort port,
                                   out int polyIndex, out int vertexA, out double edgeS,
                                   out double tLo, out double tHi)
    {
        polyIndex = -1; vertexA = -1; edgeS = 0; tLo = 0; tHi = 0;

        bool alongX  = port.Direction == PlanarBasisDirection.X;
        bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;
        double tCoord = alongX ? port.Location.Y : port.Location.X;

        // ── The outermost metal along the port's own transverse line ────────────────────────────
        double best = fromLow ? double.PositiveInfinity : double.NegativeInfinity;
        for (int p = 0; p < polys.Count; p++)
        {
            var (x0, y0, x1, y1) = polys[p].Bounds();
            double tol = 1e-9 * Math.Max(Math.Max(x1 - x0, y1 - y0), 1e-12);

            foreach (var (a, b) in RunsAlong(polys[p], alongX, tCoord, tol))
            {
                double outer = fromLow ? a : b;
                if (fromLow ? outer < best : outer > best) { best = outer; polyIndex = p; }
            }
        }
        if (polyIndex < 0 || double.IsInfinity(best)) return false;

        // ── The outline vertices sitting on that face ───────────────────────────────────────────
        var ring = polys[polyIndex].Outer;
        var (bx0, by0, bx1, by1) = polys[polyIndex].Bounds();
        double scale = Math.Max(bx1 - bx0, by1 - by0);
        double onFace = 1e-9 * Math.Max(scale, 1e-12);

        double S(int i) => alongX ? ring[i].X : ring[i].Y;
        double T(int i) => alongX ? ring[i].Y : ring[i].X;

        int n = ring.Count, first = -1, count = 0;
        for (int i = 0; i < n; i++)
            if (Math.Abs(S(i) - best) <= onFace) { count++; if (first < 0) first = i; }

        if (count != 2) return false;

        // The two must be ADJACENT in the ring, i.e. the face is one edge. The pair can straddle the
        // wrap (a taper's own end cap is exactly the last→first edge), so both orderings are tried.
        int a1 = -1;
        for (int i = 0; i < n; i++)
            if (Math.Abs(S(i) - best) <= onFace && Math.Abs(S((i + 1) % n) - best) <= onFace) { a1 = i; break; }
        if (a1 < 0) return false;

        int a2 = (a1 + 1) % n;
        tLo = Math.Min(T(a1), T(a2));
        tHi = Math.Max(T(a1), T(a2));
        if (tCoord < tLo - onFace || tCoord > tHi + onFace) return false;

        vertexA = a1;
        edgeS   = best;
        return true;
    }

    /// <summary>
    /// How far inward from the end face the cross-section holds, capped at <paramref name="wanted"/>.
    ///
    /// <para><b>Running out of metal counts as uniform, and that is deliberate.</b> A feed shorter
    /// than the calibration's own run is a SHORT structure, not a non-uniform one — the existing code
    /// already clamps the standard to the cells that exist (<c>EndRunCellsFor</c>). Treating it as a
    /// shortfall would grow a lead on every short line in the repository and move numbers that are
    /// not wrong.</para>
    /// </summary>
    private static double UniformRun(PlanarPolygon poly, bool alongX, bool fromLow,
                                     double edgeS, double tLo, double tHi, double wanted)
    {
        double width = tHi - tLo;
        double tol   = UniformFractionOfWidth * width;
        double tMid  = 0.5 * (tLo + tHi);
        double dir   = fromLow ? 1.0 : -1.0;

        double sliceTol = 1e-9 * Math.Max(width, 1e-12);

        double last = 0;
        for (int k = 1; k <= UniformitySamples; k++)
        {
            double d = wanted * k / UniformitySamples;
            double s = edgeS + dir * d;

            var spans = SpansAcross(poly, alongX, s, sliceTol);

            bool found = false;
            foreach (var (a, b) in spans)
            {
                if (tMid < a - sliceTol || tMid > b + sliceTol) continue;
                found = true;
                if (Math.Abs(a - tLo) > tol || Math.Abs(b - tHi) > tol) return last;
                break;
            }
            if (!found) return wanted;      // the metal ended: a SHORT feed, not a flared one
            last = d;
        }
        return wanted;
    }

    /// <summary>Whether the lead would run into other metal on the same level. Sampled rather than
    /// solved: the question is only ever asked about a rectangle that sits OUTSIDE the drawn metal
    /// along the port's own line, so a coarse interior sample separates "empty" from "occupied"
    /// without a polygon boolean and without a new dependency.</summary>
    private static bool Obstructed(IReadOnlyList<PlanarPolygon> polys, bool alongX,
                                   double s0, double s1, double tLo, double tHi)
    {
        const int NS = 5, NT = 5;
        double sa = Math.Min(s0, s1), sb = Math.Max(s0, s1);
        for (int i = 1; i <= NS; i++)
        {
            double s = sa + (sb - sa) * i / (NS + 1.0);
            for (int j = 1; j <= NT; j++)
            {
                double t = tLo + (tHi - tLo) * j / (NT + 1.0);
                double x = alongX ? s : t, y = alongX ? t : s;
                foreach (var poly in polys)
                    if (poly.Contains(x, y)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Push the end face outward by <paramref name="outward"/>, by splicing two vertices into the
    /// ring after <paramref name="vertexA"/>. Winding and vertex order are preserved, so the result
    /// is the same simple polygon with a rectangle on its end — not a second shape abutting it,
    /// which would put two rings through the boundary cells at the seam and cost them their
    /// conformal cut for nothing.
    /// </summary>
    private static PlanarPolygon ExtrudeFace(PlanarPolygon poly, int vertexA, bool alongX, double outward)
    {
        var ring = poly.Outer;
        int n = ring.Count, b = (vertexA + 1) % n;

        EmPoint Push(EmPoint p) => alongX ? new EmPoint(p.X + outward, p.Y) : new EmPoint(p.X, p.Y + outward);

        var next = new List<EmPoint>(n + 2);
        for (int i = 0; i < n; i++)
        {
            next.Add(ring[i]);
            if (i != vertexA) continue;
            next.Add(Push(ring[vertexA]));
            next.Add(Push(ring[b]));
        }
        return poly with { Outer = next };
    }

    /// <summary>
    /// R-pw1-2 — a lead [<paramref name="tLo"/>, <paramref name="tHi"/>] wide on the end face that runs from
    /// <paramref name="vertexA"/> to the next vertex: four vertices spliced into that edge, so the result is the same
    /// simple polygon with a rectangular tab on its side, in the ring's own winding.
    /// </summary>
    private static PlanarPolygon ExtrudeTab(PlanarPolygon poly, int vertexA, bool alongX, double outward,
                                            double tLo, double tHi)
    {
        var ring = poly.Outer;
        int n = ring.Count, b = (vertexA + 1) % n;
        double S(EmPoint p) => alongX ? p.X : p.Y;
        double T(EmPoint p) => alongX ? p.Y : p.X;
        EmPoint At(double sv, double tv) => alongX ? new EmPoint(sv, tv) : new EmPoint(tv, sv);

        double s0 = S(ring[vertexA]);
        bool rising = T(ring[b]) > T(ring[vertexA]);
        var (t1, t2) = rising ? (tLo, tHi) : (tHi, tLo);
        var tab = new List<EmPoint>(4);
        if (t1 != T(ring[vertexA])) tab.Add(At(s0, t1));
        tab.Add(At(s0 + outward, t1));
        tab.Add(At(s0 + outward, t2));
        if (t2 != T(ring[b])) tab.Add(At(s0, t2));

        var next = new List<EmPoint>(n + 4);
        for (int i = 0; i < n; i++)
        {
            next.Add(ring[i]);
            if (i == vertexA) next.AddRange(tab);
        }
        return poly with { Outer = next };
    }

    // ── The two slices, named once, because getting them the wrong way round is silent ──────────
    //
    // Polygon2D.HorizontalFootprint(ring, y) answers "the X-intervals at this Y". Both questions
    // below are that question in one of the two frames, so each is one call plus, for one of them,
    // a transposed ring. Writing them inline is how the first draft of this file got one backwards.

    /// <summary>The metal's LONGITUDINAL intervals at transverse station <paramref name="t"/> —
    /// "marching in from the named side, where does metal start?".</summary>
    private static List<(double A, double B)> RunsAlong(
        PlanarPolygon poly, bool alongX, double t, double tol)
        => alongX
            ? Polygon2D.HorizontalFootprint(poly.Outer, t, tol)               // x-intervals at y = t
            : Polygon2D.HorizontalFootprint(Transpose(poly.Outer), t, tol);   // y-intervals at x = t

    /// <summary>The metal's TRANSVERSE intervals at longitudinal station <paramref name="s"/> —
    /// "how wide is the feed here?".</summary>
    private static List<(double A, double B)> SpansAcross(
        PlanarPolygon poly, bool alongX, double s, double tol)
        => alongX
            ? Polygon2D.HorizontalFootprint(Transpose(poly.Outer), s, tol)    // y-intervals at x = s
            : Polygon2D.HorizontalFootprint(poly.Outer, s, tol);              // x-intervals at y = s

    private static IReadOnlyList<EmPoint> Transpose(IReadOnlyList<EmPoint> ring)
    {
        var q = new EmPoint[ring.Count];
        for (int i = 0; i < ring.Count; i++) q[i] = new EmPoint(ring[i].Y, ring[i].X);
        return q;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Notes
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static string FeedNote(IReadOnlyList<PlanarFeedLead> leads, double wanted,
                                   SurfaceMesher.PlanarLengthFormat fmt)
    {
        // ── R-pcal7-2 — WHICH SHORTFALL GREW THE LEAD IS PART OF THE SENTENCE ───────────────────
        //
        // "60 µm on top of 239 µm it already had" means the metal changes WIDTH at the plane, and
        // the remedy is to draw a uniform feed. A lead grown because a coil turn runs 8 µm away is a
        // different fact with a different remedy — move the neighbour, or accept the lead — and a
        // user chasing one sentence must not be handed the other.
        var parts = leads.Select(l =>
            $"port {l.PortNumber} {fmt(l.LengthM)}" +
            (l.GrownForPeer
                ? " (matched to the longest lead at this reference plane, so the ports that share " +
                  "one calibration standard are peeled by one length)"
             : l.GrownForNeighbour
                ? $" (to clear other metal {fmt(l.NeighbourAcrossM)} away, which starts " +
                  $"{fmt(l.NeighbourAtM)} in from the port)"
                : l.ExistingUniformM > 0 ? $" (on top of {fmt(l.ExistingUniformM)} it already had)"
                                         : ""));

        // A lead grown only to match a PEER's is neither of the two things this sentence names, and
        // its own clause already says so — so it must not be counted into either half, or a run
        // where one port's lead was equalised reads as a report about that port's cross-section.
        bool anyNeighbour = leads.Any(l => l.GrownForNeighbour);
        bool anySection   = !anyNeighbour
                         || leads.Any(l => !l.GrownForNeighbour && !l.GrownForPeer);

        string why =
            anyNeighbour && anySection
                ? "sit on metal that changes cross-section, or have other metal beside them, inside the"
            : anyNeighbour
                ? "have other metal beside them inside the"
                : "sit on metal that changes cross-section inside the";

        return $"{leads.Count} port(s) {why} " +
               $"{fmt(wanted)} of feed the calibration standard replaces, so a UNIFORM " +
               $"LEAD of the port's own width was added for the solve and removed again afterwards: " +
               string.Join(", ", parts) + ". Your reference planes are still your own drawn metal " +
               "edges — the lead is meshed, solved and then peeled as a matched section of the line " +
               "the calibration itself measured, so it changes where the error box is taken, not " +
               "where the answer is reported. The standard is an ISOLATED line of the port's own " +
               "width, so a feed that either changes width or has a neighbour inside that run is " +
               "not the structure the error box was measured on — and the peel divides that " +
               "mismatch by a₂₁² — which is a non-passive answer that reads as an open circuit.";
    }
}
