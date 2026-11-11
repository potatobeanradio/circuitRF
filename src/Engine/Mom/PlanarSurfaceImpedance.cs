// CL1 — THE SURFACE IMPEDANCE OF A CONDUCTOR, AND THE TWO LIMITS THAT MAKE IT LOAD-BEARING.
//
// Until this file existed, kernel B's metal was a PERFECT CONDUCTOR: PlanarConductorLayer.SigmaSm
// and ThicknessM reached PlanarProblem, entered the provenance hash, and were never read by the
// fill. On the MMIC technology this repository ships that omits 92-99% of the line's loss
// (brief-conductor-loss-0-overview.md §0). This file is the scalar; PlanarFill is where it is used.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE FORMULATION — A TERM IN THE EFIE, NOT A POST-PROCESSING CORRECTION
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// A surface-impedance boundary condition replaces "E_tan = 0 on the metal" with "E_tan = Z_s·J", so
// the electric-field integral equation becomes
//
//     Z_s(ω)·J(r) + jωA(r) + ∇Φ(r) = E_inc(r)
//
// and testing with f_m (Galerkin, the same D1 the rest of the fill uses) adds exactly one term:
//
//     Z[m,n] += Z_s(ω, layer of m) · ⟨f_m , f_n⟩          when m and n share a layer.
//
// The ALTERNATIVE — solve PEC, then integrate R_s|J|² over the answer — is cheaper and is wrong in
// the way that matters: the PEC current has an unregularised 1/√d edge singularity, so ∫R_s|J|² over
// it is logarithmically divergent in the edge mesh and the number depends on EdgeCells. Loading the
// operator penalises that singular edge current self-consistently. (It does not remove the
// convergence question — PlanarSurfaceImpedanceTests' edge sweep is where that is MEASURED — but it
// is the difference between a quantity that converges slowly and one that does not converge at all.)
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE SCALAR, DERIVED
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Inside a good conductor the field obeys ∂²E/∂n² = γ_c²E with
//
//     γ_c = √(jωµ₀σ) = (1+j)/δ ,     δ = √(2/(ωµ₀σ))          (the skin depth)
//     η_c = γ_c/σ    = (1+j)/(σδ)                              (the intrinsic impedance)
//
// For a slab of thickness t excited SYMMETRICALLY on both faces — which is what a single-unknown
// sheet model means — the tangential field is even about the mid-plane, E ∝ cosh(γ_c z), the current
// density is σE, and the ratio of the surface field to the TOTAL sheet current
// K = ∫σE dz = 2σE(t/2)·sinh(γ_c t/2)/γ_c is
//
//     Z_s(ω) = E(t/2)/K = (η_c/2)·coth(γ_c t/2).                                             (CL1.1)
//
// TWO LIMITS, AND BOTH ARE LOAD-BEARING RATHER THAN DECORATIVE:
//
//   t ≫ δ →  coth → 1, so Z_s → η_c/2 = (1+j)/(2σδ).  Two surfaces in parallel — the classic
//            R_s = 1/(σδ) per face, and the factor of two is the second face, not a slip.
//
//   t ≪ δ →  coth(x) → 1/x + x/3, so Z_s → (η_c/2)·(2/(γ_c t)) = 1/(σt), which is EXACTLY
//            PlanarDcSolve.SheetResistance. So the AC term walks continuously into the DC point the
//            kernel already computes, and LF2's conduction-substitution band below the DCIM fit
//            floor stops being a discontinuity in α.
//
// σ ≤ 0 OR t ≤ 0 IS A PEC AND RETURNS EXACTLY Complex.Zero. That is what every other part of this
// kernel already means by those values (PlanarDcSolve.cs's SheetResistance says the same), and it is
// what makes the PEC reproduction gate BIT-IDENTICAL rather than approximate: adding Complex.Zero to
// a matrix entry changes no bits.
//
// THE coth's LARGE-ARGUMENT BRANCH IS A PRECISION QUESTION, NOT A CRASH. The naive
// (e^{2z}+1)/(e^{2z}-1) does not overflow until t/δ > 710 and no shipped stackup reaches that —
// 105 µm copper at 40 GHz is t/δ = 318 — but past |z| ≈ 30 the ratio is 1 to every bit that matters
// and the subtraction in the denominator is pure cancellation noise. The large-argument branch is
// returned there rather than computed.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT THIS FORM CANNOT CARRY, SAID HERE RATHER THAN DISCOVERED LATER
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// (CL1.1) is EXACT for symmetric excitation and is the standard single-unknown reduction otherwise.
// A microstrip's substrate-side face carries far more current than its air-side face; ONE unknown
// per location cannot hold two independent face currents, and that asymmetry is precisely the
// information a single sheet loses. The related thickness-blindness is measured in the series
// overview §3: over the thickness range real stackups use, Re(Z_s) is flat to 0.1% (FR-4) / 5%
// (GaAs) while kernel A's true loss moves by 1.41× and 1.43×. Thick metal is deliberately NOT a
// brief in this series and §3 gives the four reasons.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// CL4 — THE GROUND PLANE IS NOT A STRIP, AND THE DIFFERENCE IS A FACTOR OF TWO
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// The laterally infinite plane the Green's function terminates on has AIR BELOW IT and carries its
// return current on its upper face alone, so neither of (CL1.1)'s two halvings applies:
//
//     Z_plane(ω) = η_c · coth(γ_c t) .                                                     (CL4.1)
//
// Same two limits, one-sided: η_c = (1+j)/(σδ) when t ≫ δ, and 1/(σt) when t ≪ δ. Reaching for
// Sheet() here would report half the ground's loss — smoothly, plausibly, and with every gate in
// this file still green, which is why the brief names it as the first place a factor of two can
// enter unseen.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE VIA BARREL IS NOT A SHEET
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// A z-directed basis is a barrel, not a sheet: its current crosses the footprint AREA and returns
// through the barrel WALL, so the quantity is a SERIES impedance in ohms rather than an ohms-per-
// square multiplying a Gram entry. For a post of length ℓ, footprint area A and footprint perimeter
// P the current at high frequency rides the wall over the perimeter, and at DC it fills the area:
//
//     Z_barrel = (ℓ/P) · η_c · coth(γ_c·t_eff) ,     t_eff = A/P.                           (CL1.2)
//
// This is the ONE-SIDED form (no ½, coth's argument not halved) because a solid post conducts on one
// face only. Its limits are the two that have to hold:
//
//   t_eff ≫ δ →  Z_barrel → ℓ·(1+j)/(σδP) — the brief's own spelling, current on the wall alone.
//   t_eff ≪ δ →  Z_barrel → (ℓ/P)·1/(σ·A/P) = ℓ/(σA), which is EXACTLY
//                PlanarDcSolve's ViaResistance for the same basis. That equality is what gates the
//                via arm (R-cl1-5) and it is an identity rather than a tolerance.
//
// Nothing here knows about a mesh, a Green's function or a frequency sweep: it is σ, t and ω.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>CL1 — the conductor's surface impedance.</b> See the file header for the derivation, for both
/// limits, and for why a via barrel takes a different spelling from a sheet.
/// </summary>
public static class PlanarSurfaceImpedance
{
    /// <summary>
    /// Past this |γ_c·t| the coth is 1 to every bit that matters and its denominator is pure
    /// cancellation noise, so the large-argument branch is RETURNED rather than computed. e^{-2·30}
    /// is 9e-27, i.e. below the double's own relative resolution against 1.
    /// </summary>
    private const double CothLargeArgument = 30.0;

    /// <summary>
    /// Below this |z| the exponential form is CANCELLATION rather than arithmetic — <c>e^{2z} − 1</c>
    /// for z ~ 1e-6 loses six digits before the division — and the Laurent series
    /// <c>coth z = 1/z + z/3 − z³/45 + 2z⁵/945 − z⁷/4725</c> is used instead. <b>Measured, not
    /// assumed:</b> at t/δ = 1e-6 the exponential form returned an imaginary part 4.1e-11 of the real
    /// one where the physics says (t/δ)²/6 = 1.7e-13, i.e. pure noise, and the thin-sheet asymptote
    /// is exactly the limit the DC-continuity gate lives in. The first omitted term at |z| = 0.1 is
    /// 2z⁹/93555 ≈ 2e-14 against a value of 10 — two decades below the 1e-12 the asymptote gates at.
    /// </summary>
    private const double CothSmallArgument = 0.1;

    /// <summary>
    /// δ = √(2/(ωµ₀σ)) — metres. <b>Positive infinity for σ ≤ 0 or ω ≤ 0</b>, which is the PEC and
    /// the DC point respectively, and is the value that makes t/δ → 0 (the DC limit) rather than a
    /// NaN that would propagate silently.
    /// </summary>
    public static double SkinDepthM(double sigmaSm, double omegaRadS)
        => sigmaSm > 0 && omegaRadS > 0
            ? Math.Sqrt(2.0 / (omegaRadS * EmConstants.Mu0 * sigmaSm))
            : double.PositiveInfinity;

    /// <summary>
    /// <b>The three spellings of "this metal is a perfect conductor"</b>, asked in one place so the
    /// sheet arm and the barrel arm cannot answer differently. σ ≤ 0 and t ≤ 0 are what the rest of
    /// this kernel already means by a PEC (<see cref="PlanarDcSolve"/>'s own sheet resistance says
    /// the same); <b>σ = +∞ is the third</b>, and it is not merely a tidy-up — kernel A spells a
    /// perfect ground exactly that way (<c>EmGroundPlane(0, ∞)</c>), so an A-vs-B comparison hands
    /// this an infinite conductivity as a matter of course. Arithmetically it is the one that bites:
    /// δ → 0, so η_c = (1+j)/(σδ) is ∞·0 = <b>NaN</b>, and a NaN in one matrix entry is not a small
    /// loss but a solve that produces nothing at all.
    /// </summary>
    private static bool IsPerfect(double sigmaSm) => !(sigmaSm > 0) || double.IsPositiveInfinity(sigmaSm);

    /// <summary>
    /// <b>CL5 — the same three spellings plus the zero-thickness one, as the single test for "this
    /// metal has Z_s = 0 exactly".</b> <see cref="Sheet"/> and <see cref="Plane"/> both open with
    /// it, and <c>Termination.Dissipates</c> reads it rather than re-spelling the rule — so
    /// "a perfect floor does not dissipate" and "a perfect floor returns exactly
    /// <see cref="Complex.Zero"/>" cannot drift apart. It is frequency-free on purpose: none of the
    /// four spellings depends on ω, which is what lets a predicate with no ω in hand ask it.
    /// </summary>
    public static bool IsPerfect(double sigmaSm, double thicknessM) =>
        IsPerfect(sigmaSm) || !(thicknessM > 0);

    /// <summary>
    /// <b>(CL1.1) — Z_s of a sheet of thickness <paramref name="thicknessM"/> carrying current on
    /// BOTH faces</b>, Ω per square. <c>σ ≤ 0</c> or <c>t ≤ 0</c> is a PEC and returns exactly
    /// <see cref="Complex.Zero"/>.
    /// </summary>
    public static Complex Sheet(double sigmaSm, double thicknessM, double omegaRadS)
    {
        if (IsPerfect(sigmaSm, thicknessM)) return Complex.Zero;

        // ω = 0 is the DC point: the skin depth is infinite, coth's argument is zero, and the limit
        // is 1/(σt) exactly. Written as the limit rather than reached through a coth of zero, which
        // would be an infinity times a zero.
        if (!(omegaRadS > 0)) return new Complex(1.0 / (sigmaSm * thicknessM), 0.0);

        double delta = SkinDepthM(sigmaSm, omegaRadS);
        var etaC  = new Complex(1.0, 1.0) / (sigmaSm * delta);
        var gammaC = new Complex(1.0, 1.0) / delta;
        return 0.5 * etaC * Coth(0.5 * gammaC * thicknessM);
    }

    /// <summary>
    /// <b>CL4 — Z_s of the laterally infinite GROUND PLANE, Ω/square: the ONE-SIDED
    /// <c>η_c·coth(γ_c t)</c>.</b>
    ///
    /// <para><b>The factor of two is the whole difference from <see cref="Sheet"/> and it is the
    /// first place one can enter unseen</b> (CL4's own words). A strip is excited on BOTH faces, so
    /// (CL1.1) halves η_c and halves the coth's argument; a ground plane has air below it and
    /// carries its return current on its UPPER face alone, so neither halving applies. Using the
    /// two-sided form here would report half the ground's loss, smoothly and plausibly.</para>
    ///
    /// <para>The two limits are the same two that make (CL1.1) load-bearing, one-sided:</para>
    /// <list type="bullet">
    /// <item>t ≫ δ → coth → 1, so Z_s → η_c = (1+j)/(σδ) — the classic R_s = 1/(σδ) for ONE face,
    /// with no factor of two anywhere.</item>
    /// <item>t ≪ δ → coth(x) → 1/x, so Z_s → η_c/(γ_c t) = 1/(σt), the plane's own DC sheet
    /// resistance. So a thin ground walks into the same DC point a thin strip does.</item>
    /// </list>
    ///
    /// <para><c>σ ≤ 0</c>, <c>σ = +∞</c> or <c>t ≤ 0</c> is a PEC and returns exactly
    /// <see cref="Complex.Zero"/> — <see cref="IsPerfect"/>'s three spellings, unchanged, and the
    /// zero every CL4 PEC-reduction gate rests on.</para>
    /// </summary>
    public static Complex Plane(double sigmaSm, double thicknessM, double omegaRadS)
    {
        if (IsPerfect(sigmaSm, thicknessM)) return Complex.Zero;
        if (!(omegaRadS > 0)) return new Complex(1.0 / (sigmaSm * thicknessM), 0.0);

        double delta = SkinDepthM(sigmaSm, omegaRadS);
        var etaC   = new Complex(1.0, 1.0) / (sigmaSm * delta);
        var gammaC = new Complex(1.0, 1.0) / delta;
        return etaC * Coth(gammaC * thicknessM);
    }

    /// <summary>
    /// <b>(CL1.2) — the series impedance of a via barrel</b>, ohms: a post of length
    /// <paramref name="lengthM"/> over a footprint of area <paramref name="areaM2"/> and perimeter
    /// <paramref name="perimeterM"/>. One-sided, because a solid post conducts on one face; its DC
    /// limit is <c>ℓ/(σA)</c>, which is <see cref="PlanarDcSolve"/>'s own via resistance exactly.
    /// <c>σ ≤ 0</c> is a PEC and returns exactly <see cref="Complex.Zero"/>.
    /// </summary>
    public static Complex Barrel(double sigmaSm, double lengthM, double areaM2, double perimeterM,
                                 double omegaRadS)
    {
        if (IsPerfect(sigmaSm) || !(lengthM > 0) || !(areaM2 > 0) || !(perimeterM > 0))
            return Complex.Zero;

        double tEff = areaM2 / perimeterM;
        if (!(omegaRadS > 0)) return new Complex(lengthM / (sigmaSm * areaM2), 0.0);

        double delta = SkinDepthM(sigmaSm, omegaRadS);
        var etaC   = new Complex(1.0, 1.0) / (sigmaSm * delta);
        var gammaC = new Complex(1.0, 1.0) / delta;
        return lengthM / perimeterM * etaC * Coth(gammaC * tEff);
    }

    /// <summary>
    /// <c>coth z</c> for the half-plane <c>Re z ≥ 0</c> this file ever asks about, with the
    /// large-argument branch returned rather than computed — see the header for why that is a
    /// precision question and not an overflow one.
    /// </summary>
    public static Complex Coth(Complex z)
    {
        if (Math.Abs(z.Real) >= CothLargeArgument) return z.Real >= 0 ? Complex.One : -Complex.One;

        if (z.Magnitude <= CothSmallArgument)
        {
            var z2 = z * z;
            return 1.0 / z + z * (1.0 / 3.0 + z2 * (-1.0 / 45.0 + z2 * (2.0 / 945.0 - z2 / 4725.0)));
        }

        var e = Complex.Exp(2.0 * z);
        return (e + 1.0) / (e - 1.0);
    }
}

/// <summary>
/// <b>CL1 — what the fill needs to know about the metal, and nothing else.</b> σ and t per conductor
/// LEVEL, plus the per-basis via conductivity lookup, resolved off the <see cref="PlanarProblem"/>
/// the mesh was built from.
///
/// <para><b>Null on <see cref="PlanarFillSettings.ConductorLoss"/> is the PEC oracle and is the
/// default</b>, on exactly the terms <see cref="PlanarFillSettings.Aim"/>'s null is the dense path:
/// nothing on this object is read and the filled matrix is bit-identical to the pre-CL1 one. It is
/// a nullable DATA object rather than a bare bool because the switch and the two numbers it switches
/// on have to arrive together — a bool alone would either need a second property beside it or would
/// silently do nothing when the numbers were not supplied, which is the failure this area keeps
/// finding.</para>
///
/// <para>It holds the problem rather than a copy of its numbers because the via lookup is a question
/// about via ARTWORK (which polygon covers this cell), and a second copy of that resolution is
/// exactly what <see cref="PlanarDcSolve.ViaSigmaFor"/> exists to prevent.</para>
/// </summary>
public sealed class PlanarConductorLoss
{
    private readonly PlanarProblem _problem;

    private PlanarConductorLoss(PlanarProblem problem) => _problem = problem;

    /// <summary>
    /// The loss model of <paramref name="problem"/>'s own metal. Never null — a problem whose every
    /// level declares σ ≤ 0 or t ≤ 0 is a legitimate all-PEC one, and it reproduces the pre-CL1
    /// matrix bit for bit through <see cref="PlanarSurfaceImpedance.Sheet"/>'s own zero rather than
    /// by being absent.
    /// </summary>
    public static PlanarConductorLoss For(PlanarProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new PlanarConductorLoss(problem);
    }

    /// <summary>How many conductor levels the problem declares.</summary>
    public int LayerCount => _problem.Layers.Count;

    /// <summary>
    /// Z_s of one conductor LEVEL at one angular frequency, Ω/square.
    ///
    /// <para><b>An out-of-range level is a REFUSAL, not a clamp.</b> It used to clamp into range,
    /// which meant a mesh naming more levels than the problem declares read the LAST level's metal
    /// — a plausible loss figure for a pairing that is simply wrong, and the same class of silent
    /// substitution the name-first resolution in <see cref="SheetTable"/> exists to prevent. There
    /// is no case where the clamp is the right answer: the two callers are that method, which
    /// resolves a level it has, and a caller asking about a level this problem does not have.</para>
    /// </summary>
    public Complex SheetAt(int layerIndex, double omegaRadS)
    {
        if ((uint)layerIndex >= (uint)_problem.Layers.Count)
            throw new ArgumentOutOfRangeException(
                nameof(layerIndex),
                $"Conductor level {layerIndex} was asked for and this problem declares " +
                $"{_problem.Layers.Count} ({string.Join(", ", _problem.Layers.Select(l => l.Name))}). " +
                "A mesh and the problem it was built from must agree about how many levels there are " +
                "— a level index that does not resolve is a mesh/problem mismatch, and answering it " +
                "with the nearest level would report a plausible loss figure for the wrong metal.");

        var layer = _problem.Layers[layerIndex];
        var zs = PlanarSurfaceImpedance.Sheet(layer.SigmaSm, layer.ThicknessM, omegaRadS);
        // Roughness scales the whole complex Z_s (ConductorRoughness' header says why). A smooth level
        // skips the multiply, so its Z_s is the pre-roughness value bit for bit.
        return layer.RoughnessRmsM > 0
            ? zs * ConductorRoughness.Factor(layer.RoughnessRmsM, layer.SigmaSm, omegaRadS)
            : zs;
    }

    /// <summary>
    /// <b>Z_s of every level of ONE MESH at one frequency, indexed by that mesh's own
    /// <c>LayerIndex</c></b> — what a fill reads once and indexes per cell.
    ///
    /// <para><b>Resolved by NAME first, by index second, and the order matters.</b> A DUT's mesh
    /// numbers its levels as the problem does, so either route agrees. A CALIBRATION STANDARD's does
    /// not: <c>PlanarCalibration.BuildLine</c> emits every cell with <c>LayerIndex = 0</c> and one
    /// layer name, so on a multi-level problem an index lookup would hand a Metal-2 standard Metal-1's
    /// metal — silently, as a plausible loss figure. Matching the name first makes the two agree
    /// wherever the names do.</para>
    ///
    /// <para><b>…and the name has to be SUPPLIED, which it was not until the review that found
    /// this.</b> <c>BuildLine</c>'s layer name defaults to <c>PlanarCalibration.DefaultLayerName</c>
    /// and nothing passed the port's own, so on a real technology the match failed and the INDEX
    /// fallback answered — level 0's metal, for every port on every level. That is the right answer
    /// on a single-level problem by either route, which is why it was invisible.
    /// <c>PlanarSolve</c> now hands <c>BuildSet</c> the name of the level the port sits on, and
    /// <c>PlanarSurfaceImpedanceTests.R_cl1_2d</c> is the gate.</para>
    /// </summary>
    public Complex[] SheetTable(PlanarMesh mesh, double omegaRadS)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        // EM-TFR — one entry per sheet CLASS: every level's metal at its own index, as ever, and then
        // every film's real constant R_s. A mesh with no film has exactly the table it always had.
        int levelCount = Math.Max(mesh.LayerNames.Count, 1);
        var t = new Complex[mesh.SheetClassCount];
        for (int f = 0; f < mesh.Films.Count; f++)
            t[levelCount + f] = new Complex(mesh.Films[f].SheetResistanceOhmPerSq, 0);
        for (int i = 0; i < levelCount; i++)
        {
            int resolved = i < mesh.LayerNames.Count ? IndexOfName(mesh.LayerNames[i]) : -1;
            if (resolved < 0) resolved = i;
            if (resolved >= _problem.Layers.Count)
                throw new InvalidOperationException(
                    $"This mesh names conductor level {i} " +
                    (i < mesh.LayerNames.Count ? $"('{mesh.LayerNames[i]}') " : "") +
                    $"and the problem declares {_problem.Layers.Count} " +
                    $"({string.Join(", ", _problem.Layers.Select(l => l.Name))}), none of them by " +
                    "that name. A mesh and the problem it was built from have to agree about the " +
                    "metal: a level that resolves by neither name nor index has no σ or thickness " +
                    "to read, and the nearest level's would be a plausible loss figure for the " +
                    "wrong conductor.");
            t[i] = SheetAt(resolved, omegaRadS);
        }
        // PMS-3 — a stacked region's interior is one meshed sheet standing for every via-joined level of the stack,
        // shorted together over the whole interior, so it carries their sheets in PARALLEL. A perfect level in the
        // stack makes the stack perfect.
        for (int k = 0; k < mesh.Stacks.Count; k++)
        {
            var st = mesh.Stacks[k];
            Complex admittance = Complex.Zero;
            bool perfect = false;
            for (int l = st.LowerLayerIndex; l <= st.UpperLayerIndex && !perfect; l++)
            {
                if (t[l] == Complex.Zero) perfect = true;
                else admittance += 1.0 / t[l];
            }
            t[mesh.StackClassOf(k)] = perfect ? Complex.Zero : 1.0 / admittance;
        }
        return t;
    }

    /// <summary>
    /// <b>EM-TFR — the one spelling of "what does this fill multiply its Gram by"</b>: the loss
    /// model's table, or, with no loss model, a table that is zero on every level's metal and R_s on
    /// every film. Null means no term at all, which is a null loss model on a mesh with no film — the
    /// matrix is then bit-identical to the pre-CL1 one, because nothing is read.
    ///
    /// <para><b>A film is a circuit element, not a loss correction,</b> so the PEC oracle makes the
    /// metal perfect and leaves the film alone. Without this the oracle would silently short every
    /// resistor in the layout, which is precisely the plausible wrong answer it exists to rule out.</para>
    /// </summary>
    public static Complex[]? TableFor(PlanarConductorLoss? loss, PlanarMesh mesh, double omegaRadS)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (loss is not null) return loss.SheetTable(mesh, omegaRadS);
        if (mesh.Films.Count == 0) return null;
        int levelCount = Math.Max(mesh.LayerNames.Count, 1);
        var t = new Complex[mesh.SheetClassCount];
        for (int f = 0; f < mesh.Films.Count; f++)
            t[levelCount + f] = new Complex(mesh.Films[f].SheetResistanceOhmPerSq, 0);
        return t;
    }

    /// <summary>
    /// <b>CL3 — is every conductor level this MESH names a perfect one?</b> Frequency-independent,
    /// because <see cref="PlanarSurfaceImpedance.Sheet"/>'s own PEC test is: σ ≤ 0, σ = +∞ or
    /// t ≤ 0. Asked through <see cref="SheetTable"/>'s exact levels so the name-first resolution is
    /// the same one the fill uses.
    ///
    /// <para>It exists so a PERFECT metal produces the identical answer however it was spelled —
    /// declared on the stackup, or asked for with
    /// <see cref="PlanarFillSettings.PerfectConductor"/>. Those two are the same physical claim, and
    /// <see cref="PlanarQuasiStaticLine"/>'s γ has two spellings that agree to the last digit of the
    /// physics and not to the last bit, so without this the oracle would be off by an ulp and would
    /// not be an oracle.</para>
    /// </summary>
    public bool IsPerfectOn(PlanarMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var table = SheetTable(mesh, 1.0);
        for (int i = 0; i < Math.Max(mesh.LayerNames.Count, 1); i++)
            if (table[i] != Complex.Zero) return false;
        return true;
    }

    /// <summary>The problem's own index of a level named <paramref name="name"/>, or −1.</summary>
    private int IndexOfName(string name)
    {
        for (int i = 0; i < _problem.Layers.Count; i++)
            if (string.Equals(_problem.Layers[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// The series impedance of one VERTICAL basis's barrel at one angular frequency, ohms.
    /// <b>The σ lookup is <see cref="PlanarDcSolve.ViaSigmaFor"/>'s</b> — the same resolution the DC
    /// point makes, including <c>PlanarGroundPath</c>'s synthesised cells and the ground-attachment
    /// case — and the length is <see cref="PlanarLevels"/>' own, so the two paths cannot disagree
    /// about which metal a via is made of.
    /// </summary>
    public Complex BarrelAt(PlanarMesh mesh, PlanarLevels levels, PlanarBasis basis, double omegaRadS)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(basis);
        if (basis.Direction != PlanarBasisDirection.Z) return Complex.Zero;

        var cell = mesh.Cells[basis.CellA];
        double length = basis.AttachesToGround
            ? levels.AttachmentLengthOf(basis.LayerIndex)
            : levels.LengthOf(basis.LayerIndex);
        double sigma = PlanarDcSolve.ViaSigmaFor(_problem, basis, cell);
        return PlanarSurfaceImpedance.Barrel(sigma, length, cell.Area, FootprintPerimeter(cell),
                                             omegaRadS);
    }

    /// <summary>
    /// The footprint's own perimeter — the METAL's when the cell is cut, the rectangle's otherwise,
    /// on exactly the terms <see cref="PlanarCell.Area"/> is asked of the region rather than of the
    /// grid rectangle.
    /// </summary>
    public static double FootprintPerimeter(PlanarCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.Region is not { } region) return 2.0 * (cell.Width + cell.Height);

        double p = 0;
        foreach (var piece in region.Pieces)
            for (int i = 0, n = piece.Count, j = n - 1; i < n; j = i++)
            {
                double dx = piece[i].X - piece[j].X, dy = piece[i].Y - piece[j].Y;
                p += Math.Sqrt(dx * dx + dy * dy);
            }
        return p;
    }
}
