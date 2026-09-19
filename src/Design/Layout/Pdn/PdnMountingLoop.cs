// §4.3's mounting loop, computed from the ACTUAL via geometry
// (docs/sonnet-briefs/brief-railrf-13-distributed.md R-rail13-5, railrf.md §2.2, §2.5, §2.6, §4.3).
//
// ── THE QUANTITY THE WHOLE FORM-FACTOR QUESTION TURNS ON ───────────────────────────────────────
//
// §4.3: "the partial self-inductance of the power via and the return via, MINUS TWICE THEIR PARTIAL
// MUTUAL INDUCTANCE, plus the pad-to-via trace. The dominant term is the via pair's separation and
// the plane separation h — precisely the quantity that changes when a part moves."
//
//     L_loop = L_p + L_r − 2·M_pr + L_pad
//
// THE MINUS-TWO-M TERM IS THE WHOLE PHYSICS. A power via and its return via close to each other
// have a small loop; the same pair 4 mm apart does not. Every other term is a self-inductance that
// a part's placement barely moves, so a sign error on M would produce a tool that REWARDS moving a
// capacitor away from its load — plausible numbers, wrong advice, and nothing to say so. That is
// what PdnDistributedTests' separation sweep is for.
//
// §2.5 is what brief 16 makes of this file: "A part that was 0.4 nH on the reference and is 1.1 nH
// on yours BECAUSE ITS RETURN VIA MOVED 4 MM is a finding you can act on in an afternoon."
//
// ── A COMPUTED VALUE IS A DEFAULT, NOT A FACT (§2.2) ───────────────────────────────────────────
//
// "You can override it." So this file never writes onto a document and never wins against a typed
// number: it produces a value plus its provenance, RailPartResolver takes the typed one first, and
// RailMountingBasis is what tells a reader on the parts table which they are looking at. A computed
// number that silently replaced a measured one would be the same defect as a defaulted plating
// thickness reported as a stated one, which §4.2 already refuses to have.
//
// ── WHAT IT REFUSES RATHER THAN GUESSES ───────────────────────────────────────────────────────
//
// A part with no pad on the rail, no pad on the reference, or no via within reach of either is
// UNRESOLVED and says which — never a defaulted 1 nH. §2.2 gives 0.3–1.5 nH as the sanity band a
// user has, and a defaulted value inside that band is indistinguishable from a computed one on
// every report railRF draws.

using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// The four terms of <c>L_loop = L_p + L_r − 2·M + L_pad</c>, and the geometry they were read from.
///
/// <para><b>Structured rather than only summed</b>, for the reason §2.5 gives: the A/B report's
/// per-part table has to say WHY one part is 1.1 nH and its counterpart 0.4 nH, and "its return via
/// moved 4 mm" is a statement about <see cref="SeparationMetres"/> and not about the total.</para>
/// </summary>
/// <param name="SelfPowerHenries">The power via's partial self-inductance.</param>
/// <param name="SelfReturnHenries">The return via's.</param>
/// <param name="MutualHenries">Their partial mutual inductance — <b>entering the loop twice, with a
/// minus sign</b>.</param>
/// <param name="PadHenries">Both pad-to-via traces together.</param>
/// <param name="SeparationMetres">The via pair's axis separation. <b>§2.5's own column.</b></param>
/// <param name="PowerSpanMetres">The z distance the power via carries current over.</param>
/// <param name="ReturnSpanMetres">The return via's.</param>
/// <param name="PlaneSeparationMetres">§4.1's <c>h</c> between the rail's plane and its reference —
/// the other half of §4.3's "the dominant term".</param>
public sealed record PdnMountingTerms(
    double SelfPowerHenries,
    double SelfReturnHenries,
    double MutualHenries,
    double PadHenries,
    double SeparationMetres,
    double PowerSpanMetres,
    double ReturnSpanMetres,
    double PlaneSeparationMetres)
{
    /// <summary>The loop itself, in henries — <see cref="PdnInductance.LoopHenries"/> over these
    /// four terms and nowhere else.</summary>
    public double LoopHenries => PdnInductance.LoopHenries(
        SelfPowerHenries, SelfReturnHenries, MutualHenries, PadHenries);
}

/// <summary>
/// One part's computed mounting loop, or the reason there is none.
/// </summary>
/// <param name="Refdes">The instance this is of.</param>
/// <param name="Henries">The loop, or null when <paramref name="Unresolved"/> says why not.</param>
/// <param name="Terms">Its four terms and their geometry, or null.</param>
/// <param name="PowerVia">The hole the power current leaves through, DBU, or null.</param>
/// <param name="ReturnVia">The hole the return comes back through, DBU, or null.</param>
/// <param name="Unresolved">Why nothing was computed. <b>Never a defaulted number</b>.</param>
/// <param name="Notes">What railRF established that the artwork did not state.</param>
public sealed record PdnMountingLoop(
    string Refdes,
    double? Henries,
    PdnMountingTerms? Terms,
    (long X, long Y)? PowerVia,
    (long X, long Y)? ReturnVia,
    string? Unresolved,
    IReadOnlyList<string> Notes)
{
    /// <summary>The sentence §2.5's per-part table prints.</summary>
    public string Describe()
    {
        if (Unresolved is { } why) return $"{Refdes}: no computed mounting loop — {why}";
        if (Terms is not { } t || Henries is not { } l) return $"{Refdes}: no computed mounting loop.";

        return $"{Refdes}: {l * 1e12:0.#} pH — {t.SelfPowerHenries * 1e12:0.#} pH power via + " +
               $"{t.SelfReturnHenries * 1e12:0.#} pH return via − 2 × {t.MutualHenries * 1e12:0.#} pH " +
               $"mutual + {t.PadHenries * 1e12:0.#} pH pad trace, with the pair " +
               $"{t.SeparationMetres * 1e3:0.###} mm apart over {t.PlaneSeparationMetres * 1e6:0.#} µm " +
               "of dielectric.";
    }

    internal static PdnMountingLoop No(string refdes, string why) =>
        new(refdes, null, null, null, null, why, []);
}

/// <summary>Everything the mounting loop is read from.</summary>
public sealed class PdnMountingLoopRequest
{
    /// <summary>The rail — its net name and its reference layer.</summary>
    public required RailSpec Rail { get; init; }

    /// <summary>The artwork, flattened to shapes in DBU. Only <c>ViaShape</c>s are read.</summary>
    public required IReadOnlyList<LayoutShape> Shapes { get; init; }

    /// <summary>The stackup.</summary>
    public required Technology Technology { get; init; }

    /// <summary>The artwork's DBU resolution.</summary>
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>The board's pads. A part's power pad and its return pad are found here by net.</summary>
    public required IReadOnlyList<PdnPad> Pads { get; init; }

    /// <summary>The reference return's net, where one is named. Without it a part's return pad
    /// cannot be told from its power pad and every part is unresolved, which is what happens.</summary>
    public string? ReferenceNet { get; init; }

    /// <summary>
    /// How far from a pad a via may sit and still be that pad's via, in METRES. 2 mm, which is
    /// wider than any fan-out a decoupling capacitor is laid out with and narrow enough that the
    /// next part's via is not picked up.
    ///
    /// <para>A pad with no via inside it is UNRESOLVED and names this distance, rather than
    /// reaching further and quietly attributing a neighbour's return via to it.</para>
    /// </summary>
    public double SearchRadiusMetres { get; init; } = 2e-3;

    /// <summary>
    /// The stackup conductor the parts are mounted on. Null takes the OUTERMOST conductor, which is
    /// where a surface-mount decoupling capacitor sits; state it for a board whose parts are on the
    /// other side.
    /// </summary>
    public string? MountingConductorName { get; init; }

    /// <summary>
    /// The stackup conductor carrying the rail's plane. Null takes the conductor NEAREST IN Z to the
    /// reference that is neither the reference nor the mounting surface — the other half of the
    /// plane pair — and falls back to the mounting surface on a two-layer board, where the part's
    /// pad IS the rail's copper and its power via has no length at all.
    /// </summary>
    public string? PowerConductorName { get; init; }
}

/// <summary>§4.3's mounting loop, read off the artwork.</summary>
public static class PdnMountingLoopExtractor
{
    /// <summary>
    /// Every named part's mounting loop, keyed by refdes and in the order asked for.
    /// </summary>
    /// <param name="request">The board.</param>
    /// <param name="refdeses">The parts to compute. A refdes with no pads at all is present in the
    /// result as unresolved rather than absent from it — a part missing from a table is read as a
    /// part nobody looked at.</param>
    public static IReadOnlyList<PdnMountingLoop> ComputeAll(
        PdnMountingLoopRequest request, IEnumerable<string> refdeses)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(refdeses);

        var conductors = PdnStackupGeometry.Conductors(request.Technology, request.DbuPerMicron);
        var reference = PdnStackupGeometry.ConductorOf(conductors, request.Rail.ReferenceLayer ?? default);
        var mounting = PdnStackupGeometry.ConductorNamed(conductors, request.MountingConductorName)
                       ?? conductors.FirstOrDefault();
        var power = PdnStackupGeometry.ConductorNamed(conductors, request.PowerConductorName)
                    ?? NearestToReference(conductors, reference, mounting)
                    ?? mounting;

        var vias = request.Shapes.OfType<ViaShape>()
                          .OrderBy(v => v.X).ThenBy(v => v.Y)
                          .ToList();

        double dbuPerMetre = request.DbuPerMicron * 1e6;
        double h = PdnStackupGeometry.SeparationMetres(power, reference);
        double padThickness = mounting?.ThicknessMetres ?? 0.0;

        var results = new List<PdnMountingLoop>();

        foreach (string refdes in refdeses)
        {
            results.Add(One(
                request, refdes, vias, reference, mounting, power,
                dbuPerMetre, h, padThickness));
        }

        return results;
    }

    /// <summary>One part, by refdes.</summary>
    public static PdnMountingLoop Compute(PdnMountingLoopRequest request, string refdes) =>
        ComputeAll(request, [refdes])[0];

    // ── the conductors ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The other half of the plane pair: the conductor nearest in z to the reference that is neither
    /// the reference nor the surface the parts sit on.
    ///
    /// <para><b>Nearest, because that is the pair whose <c>h</c> the current actually returns
    /// across.</b> A six-layer board has several candidates and the far one would give an <c>h</c>
    /// that no field crosses.</para>
    /// </summary>
    private static PdnConductorZ? NearestToReference(
        IReadOnlyList<PdnConductorZ> conductors, PdnConductorZ? reference, PdnConductorZ? mounting)
    {
        if (reference is null) return null;

        PdnConductorZ? best = null;
        foreach (var c in conductors)
        {
            if (c.Index == reference.Index) continue;
            if (mounting is not null && c.Index == mounting.Index) continue;
            if (best is null ||
                Math.Abs(c.MidMetres - reference.MidMetres) <
                Math.Abs(best.MidMetres - reference.MidMetres))
                best = c;
        }
        return best;
    }

    // ── one part ──────────────────────────────────────────────────────────────────────────────

    private static PdnMountingLoop One(
        PdnMountingLoopRequest request, string refdes, List<ViaShape> vias,
        PdnConductorZ? reference, PdnConductorZ? mounting, PdnConductorZ? power,
        double dbuPerMetre, double planeSeparationMetres, double padThicknessMetres)
    {
        if (reference is null)
            return PdnMountingLoop.No(refdes,
                $"rail '{request.Rail.Name}' names no reference layer that the stackup claims, so " +
                "there is no plane for a return via to reach. Name the reference return's drawing " +
                "layer, and map it onto a conductor in the stackup.");

        if (request.ReferenceNet is not { Length: > 0 } referenceNet)
            return PdnMountingLoop.No(refdes,
                "no reference net is named, so this part's return pad cannot be told from its power " +
                "pad and the loop has no second side. Name the reference return's net.");

        var pads = request.Pads
            .Where(p => string.Equals(p.Refdes, refdes, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pads.Count == 0)
            return PdnMountingLoop.No(refdes,
                "the board netlist has no pad for it, so railRF does not know where it sits. " +
                "Mounting inductance is a property of WHERE a part was placed, so there is nothing " +
                "to compute from.");

        var powerPad = pads.FirstOrDefault(p =>
            request.Rail.NetName is { Length: > 0 } net &&
            string.Equals(p.Net, net, StringComparison.OrdinalIgnoreCase));

        var returnPad = pads.FirstOrDefault(p =>
            string.Equals(p.Net, referenceNet, StringComparison.OrdinalIgnoreCase));

        if (powerPad.Refdes is null)
            return PdnMountingLoop.No(refdes,
                $"none of its {pads.Count} pad(s) is on net '{request.Rail.NetName}', so it does not " +
                "bridge this rail to its reference and has no mounting loop on it.");

        if (returnPad.Refdes is null)
            return PdnMountingLoop.No(refdes,
                $"none of its {pads.Count} pad(s) is on the reference net '{referenceNet}', so the " +
                "return half of its loop is not on this board's artwork.");

        long radius = (long)Math.Round(request.SearchRadiusMetres * dbuPerMetre);

        var powerVia = Nearest(vias, powerPad.X, powerPad.Y, radius, exclude: null);
        var returnVia = Nearest(vias, returnPad.X, returnPad.Y, radius, exclude: powerVia);

        var notes = new List<string>();

        // A part mounted directly on the rail's own outer copper has no power via, which is an
        // ordinary two-layer board rather than a failure. Its return via still has to reach the
        // reference plane, and that is the whole loop.
        double powerSpan = PdnStackupGeometry.SpanMetres(mounting, power);
        double returnSpan = PdnStackupGeometry.SpanMetres(mounting, reference);

        if (returnVia is null)
            return PdnMountingLoop.No(refdes,
                $"no via sits within {request.SearchRadiusMetres * 1e3:0.##} mm of its reference pad, " +
                "so the return current has no route to the reference plane that this artwork draws. " +
                "A stitching via inside the pad is what this looks for.");

        if (powerVia is null && powerSpan > 0)
            return PdnMountingLoop.No(refdes,
                $"no via sits within {request.SearchRadiusMetres * 1e3:0.##} mm of its pad on " +
                $"'{request.Rail.NetName}', and the rail's plane is on '{power?.Name}' rather than " +
                $"on '{mounting?.Name}' where the part is mounted — so the power half of its loop " +
                "is not drawn.");

        if (powerVia is null)
            notes.Add(
                $"{refdes} sits on the rail's own copper, so it has no power via and its loop is " +
                "the return via and the pad traces. That is a two-layer board rather than an " +
                "omission.");

        double rPower = (powerVia?.DrillSize ?? 0) / dbuPerMetre / 2.0;
        double rReturn = returnVia.DrillSize / dbuPerMetre / 2.0;

        double selfPower = PdnInductance.RoundSelfPartialHenries(powerSpan, rPower);
        double selfReturn = PdnInductance.RoundSelfPartialHenries(returnSpan, rReturn);

        // The mutual is over the length the two barrels actually run BESIDE each other. Two vias of
        // unequal span overlap over the shorter of them; the rest of the longer one has no partner
        // to couple to, so counting it would understate the loop.
        double separation = powerVia is null
            ? 0.0
            : Distance(powerVia.X, powerVia.Y, returnVia.X, returnVia.Y) / dbuPerMetre;

        double mutual = powerVia is null
            ? 0.0
            : PdnInductance.ParallelMutualPartialHenries(Math.Min(powerSpan, returnSpan), separation);

        if (powerVia is not null && Math.Abs(powerSpan - returnSpan) > 1e-9)
            notes.Add(
                $"{refdes}'s two vias span different depths ({powerSpan * 1e6:0.#} µm and " +
                $"{returnSpan * 1e6:0.#} µm), so their mutual inductance is taken over the shorter " +
                "of the two — the length they actually run beside each other.");

        double padPower = powerVia is null
            ? 0.0
            : PdnInductance.StripSelfPartialHenries(
                Distance(powerPad.X, powerPad.Y, powerVia.X, powerVia.Y) / dbuPerMetre,
                powerVia.PadSize / dbuPerMetre, padThicknessMetres);

        double padReturn = PdnInductance.StripSelfPartialHenries(
            Distance(returnPad.X, returnPad.Y, returnVia.X, returnVia.Y) / dbuPerMetre,
            returnVia.PadSize / dbuPerMetre, padThicknessMetres);

        var terms = new PdnMountingTerms(
            selfPower, selfReturn, mutual, padPower + padReturn,
            separation, powerSpan, returnSpan, planeSeparationMetres);

        double loop = terms.LoopHenries;

        // The partial forms are each valid for a conductor far longer than it is wide. A pair
        // closer together than a barrel radius drives L_p + L_r − 2M below zero, which is not a
        // small error — it is a branch that would deliver energy. PdnInductance clamps it; this is
        // where a reader is told the clamp fired, because a silently clamped zero is a part with a
        // perfect mounting loop.
        if (selfPower + selfReturn - 2.0 * mutual + padPower + padReturn < 0)
            notes.Add(
                $"{refdes}'s via pair is {separation * 1e3:0.###} mm apart over spans of " +
                $"{Math.Max(powerSpan, returnSpan) * 1e6:0.#} µm, which is closer together than the " +
                "closed-form partial inductances are valid for. The loop was clamped at zero rather " +
                "than reported negative; treat it as 'too small to resolve here' and type one if it " +
                "matters.");

        if (loop > 5e-9)
            notes.Add(
                $"{refdes}'s computed mounting loop is {loop * 1e9:0.##} nH, well above the " +
                "0.3–1.5 nH a mounted part usually has. Check that the via this found is the part's " +
                "own rather than a neighbour's.");

        return new PdnMountingLoop(
            refdes, loop, terms,
            powerVia is null ? null : (powerVia.X, powerVia.Y),
            (returnVia.X, returnVia.Y),
            null, notes);
    }

    // ── geometry ──────────────────────────────────────────────────────────────────────────────

    private static double Distance(long ax, long ay, long bx, long by)
    {
        double dx = ax - bx, dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The via nearest a pad within <paramref name="radiusDbu"/>, or null.
    ///
    /// <para><b>Ties broken by the sorted order, never by a hash.</b> Two vias equidistant from a
    /// pad happen on a symmetric fan-out, and an A/B comparison whose per-part table moved between
    /// runs would be worse than no table at all.</para>
    /// </summary>
    private static ViaShape? Nearest(
        List<ViaShape> vias, long x, long y, long radiusDbu, ViaShape? exclude)
    {
        ViaShape? best = null;
        double bestD = double.MaxValue;

        foreach (var v in vias)
        {
            if (ReferenceEquals(v, exclude)) continue;
            double d = Distance(v.X, v.Y, x, y);
            if (d > radiusDbu || d >= bestD) continue;
            best = v;
            bestD = d;
        }

        return best;
    }
}
