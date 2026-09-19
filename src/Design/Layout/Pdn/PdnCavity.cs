// §4.1's last two terms — the shunt branch to the reference plane — and the medium they are of
// (docs/sonnet-briefs/brief-railrf-14-cavity.md R-rail14-2 … R-rail14-4, railrf.md §4.1, §2.2, §9).
//
// ── WHY ONE FILE, THE SAME REASON PdnInductance IS ONE FILE ────────────────────────────────────
//
//     Shunt capacitance to the reference plane   C = ε₀·εᵣ·Δ² / h
//     Dielectric loss                            G = ω·C·tan δ
//
// Two callers need these: the mesh (one pair per cell) and the readout §9 asks for (one number for
// the whole plane pair). Those two MUST agree — the readout's whole job is to be the number a
// designer recognises as wrong, and a readout computed by different arithmetic from the model it
// claims to describe would be the one thing worse than no readout at all. So the expressions are
// here and both callers multiply them by their own area.
//
// ── ε₀ IS DERIVED, NOT TYPED, AND THAT IS ABOUT THE PLANE PAIR RATHER THAN ABOUT PRECISION ─────
//
// The same pair of planes carries L = µ₀·h (PdnInductance) and C = ε₀εᵣA/h (here), and the wave on
// it travels at 1/√(LC) per square. Two independently-typed constants would let that velocity drift
// from c/√εᵣ in the ninth digit for no reason anybody could point at, and every cavity frequency
// this series computes is that velocity over a length. So ε₀ = 1/(µ₀c²) off PdnInductance's own µ₀:
// ONE electromagnetic basis, and the plane pair's L and C cannot come to disagree about the medium.
//
// ── THE SERIES COMBINATION IS NOT AN AVERAGE, AND A PLANE PAIR ROUTINELY NEEDS IT ──────────────
//
// An inner pair split by a core and two prepregs is three dielectric entries. The capacitance of
// the stack is the SERIES combination of the three,
//
//     1/C = Σ hᵢ / (ε₀·εᵣᵢ·A)        →        εᵣ_eff = Σhᵢ / Σ(hᵢ/εᵣᵢ)
//
// which is a harmonic mean weighted by thickness, not an arithmetic one. On a 100 µm core of εr 4.3
// beside a 100 µm film of εr 9 the arithmetic mean says 6.65 and the right answer is 5.81 — 13 %,
// straight onto the headline capacitance §9 says a designer recognises. The loss tangent combines
// on the same weights (each layer's share of the RECIPROCAL capacitance), because that is the share
// of the voltage it drops.
//
// ── NOTHING HERE READS ARTWORK OR BUILDS AN ELEMENT ────────────────────────────────────────────
//
// Doubles and stackup entries in, doubles out, base SI. PdnMeshExtractor rasterises the overlap and
// PdnAssembly builds the elements — the same split PdnInductance already keeps.

using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// What the field between a rail's conductor and its reference actually crosses, combined to ONE
/// medium (<see cref="PdnCavity.MediumBetween"/>).
/// </summary>
/// <param name="EpsilonR">The series-effective relative permittivity — see this file's header for
/// why it is not the mean of the entries.</param>
/// <param name="TanDelta">The series-effective loss tangent, on the same weights.</param>
/// <param name="ThicknessMetres">The dielectric the entries add up to. <b>Not necessarily
/// <see cref="PdnStackupGeometry.SeparationMetres"/></b>, which measures the copper faces: they
/// agree on a well-formed stackup and disagree on one whose entry thicknesses do not add up to its
/// own geometry, which is worth being able to see.</param>
/// <param name="TanDeltaIsClassDefault">True where any entry stated no tan δ and brief 11's
/// per-class figure supplied one. <b>Every peak height in the cavity band is then indicative</b>
/// (R-rail14-3) — tan δ is the difference between a 6 dB bump and a 20 dB one, so this flag travels
/// with the number rather than beside it.</param>
/// <param name="Basis">The sentence a report prints: which entries, how thick, and who said what.</param>
public sealed record PdnMedium(
    double EpsilonR,
    double TanDelta,
    double ThicknessMetres,
    bool TanDeltaIsClassDefault,
    string Basis);

/// <summary>§4.1's shunt branch, and the medium it is of.</summary>
public static class PdnCavity
{
    /// <summary>Metres per second, in vacuum.</summary>
    public const double SpeedOfLight = 299_792_458.0;

    /// <summary>
    /// The permittivity of free space, F/m — <c>1/(µ₀c²)</c>, derived off
    /// <see cref="PdnInductance.MuZero"/> rather than typed. See this file's header: the plane
    /// pair's L and C are of the same medium and may not disagree about it.
    /// </summary>
    public static readonly double Epsilon0 =
        1.0 / (PdnInductance.MuZero * SpeedOfLight * SpeedOfLight);

    // ── R-rail14-2's two terms ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// §4.1's <c>C = ε₀·εᵣ·A / h</c>, in farads — the parallel-plate capacitance of one patch of
    /// plane pair.
    ///
    /// <para><b><paramref name="areaSquareMetres"/> is the OVERLAP, never an outline</b> (R-rail14-3).
    /// A cell is present where BOTH conductors have copper, and a board with a cutout, an antipad
    /// field or a split has less overlap than either plane has area. Passing an outline here
    /// over-states the capacitance by exactly the fraction of the board that is not plane pair — in
    /// the optimistic direction, which is the direction nobody investigates.</para>
    ///
    /// <para>Zero on any non-positive argument, which is what an unstated <c>h</c> looks like. §4.1's
    /// shunt branch then vanishes and the extraction says so rather than substituting a plausible
    /// separation.</para>
    /// </summary>
    public static double CapacitanceFarads(
        double epsilonR, double areaSquareMetres, double separationMetres) =>
        epsilonR > 0 && areaSquareMetres > 0 && separationMetres > 0
            ? Epsilon0 * epsilonR * areaSquareMetres / separationMetres
            : 0.0;

    /// <summary>
    /// §4.1's <c>G = ω·C·tan δ</c>, in siemens — the dielectric loss in parallel with that
    /// capacitance.
    ///
    /// <para><b>It is the term that sets how sharp a plane resonance is</b> (§2.2): tan δ is one of
    /// the two stackup numbers most often wrong and *"it sets how sharp the cavity resonances are,
    /// which is the difference between a 6 dB bump and a 20 dB one."* A model insensitive to it has
    /// a bug, which is what <c>PdnCavityTests</c> asserts rather than assumes.</para>
    ///
    /// <para>Zero at ω = 0, which is a statement about the branch rather than a guard: a capacitor's
    /// dielectric dissipates nothing when nothing is changing, and §2.8's DC system stays real,
    /// symmetric and positive-definite because both halves of the shunt branch vanish together.</para>
    /// </summary>
    public static double ConductanceSiemens(
        double frequencyHz, double capacitanceFarads, double tanDelta) =>
        frequencyHz > 0 && capacitanceFarads > 0 && tanDelta > 0
            ? 2.0 * Math.PI * frequencyHz * capacitanceFarads * tanDelta
            : 0.0;

    // ── the medium ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one medium between two conductors of <paramref name="tech"/>'s stackup — every dielectric
    /// entry between them, combined in SERIES (this file's header) and flagged where brief 11's
    /// per-class tan δ had to stand in for one nobody stated.
    /// </summary>
    /// <returns>Null where the stackup puts no dielectric between the two, which is an honest
    /// answer and never a defaulted FR-4: §4.1's shunt branch has no medium to be of, the extraction
    /// carries none, and it says so.</returns>
    public static PdnMedium? MediumBetween(
        Technology tech, PdnConductorZ? rail, PdnConductorZ? reference)
    {
        var entries = PdnStackupGeometry.DielectricsBetween(tech, rail, reference);
        if (entries.Count == 0) return null;

        // Thickness in DBU here only to divide it out again: the stackup states thickness in DBU and
        // the WEIGHTS below are ratios, so the unit cancels and the caller's own dbuPerMicron is not
        // needed. Only ThicknessMetres needs it, and it is taken from the conductor faces instead —
        // which is the measurement §4.1's h already is.
        double totalDbu = 0, reciprocal = 0, lossy = 0;
        bool anyDefault = false;
        var said = new List<string>(entries.Count);

        foreach (var e in entries)
        {
            double h = e.ThicknessDbu;
            if (!(h > 0) || !(e.Epsr > 0)) continue;

            var loss = RailEsrDefaults.BoardLossTangent(e);
            double share = h / e.Epsr;               // this entry's share of 1/C

            totalDbu += h;
            reciprocal += share;
            lossy += share * loss.TanDelta;
            anyDefault |= loss.IsClassDefault;

            said.Add($"'{(e.Name.Length > 0 ? e.Name : "(unnamed)")}' εr {e.Epsr:0.###}, " +
                     $"tan δ {loss.TanDelta:0.####}{(loss.IsClassDefault ? $" (a {loss.ClassUsed} class default)" : "")}");
        }

        if (!(totalDbu > 0) || !(reciprocal > 0)) return null;

        double epsilonR = totalDbu / reciprocal;
        double tanDelta = lossy / reciprocal;
        double thickness = PdnStackupGeometry.SeparationMetres(rail, reference);

        string basis = entries.Count == 1
            ? said[0]
            : $"{said.Count} dielectrics in series — {string.Join("; ", said)} — which combine to " +
              $"εr {epsilonR:0.###} and tan δ {tanDelta:0.####} over {thickness * 1e6:0.#} µm";

        return new PdnMedium(epsilonR, tanDelta, thickness, anyDefault, basis);
    }
}
