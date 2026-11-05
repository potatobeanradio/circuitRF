// QSC — γ AND Z_c FOR A CALIBRATION STANDARD, FROM ITS OWN ELECTROSTATICS
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHY THIS EXISTS, AND WHAT IT IS NOT
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// D5 MEASURES γ from two full-wave line standards, and that is the right instrument at the top of a
// band: it captures dispersion, the port's own discontinuity and higher-order effects with no
// quasi-TEM assumption. What it costs is 1/f_lo, because the two lines' length difference has to sit
// inside TRL's usable interval βΔℓ ∈ [20°, 160°]. On a 3.8 mm microstrip swept from 100 MHz that
// bought a 161.5 mm standard — 42× the DUT, 79,055 unknowns against a 12,000 ceiling — and the run
// refused. `RESOLVED.md` §RAW1 §6.
//
// **Supplying γ does NOT by itself remove the long standards, and that is the trap this file's brief
// was written to close.** γ is ALREADY an input to `PlanarDeembed.SolveErrorBox`; the error box still
// needs TWO lines, because one line gives two complex equations (M₁₁, M₂₁) for three complex unknowns
// (a₁₁, a₂₁², a₂₂) and no reciprocity or symmetry argument closes that gap.
//
// **What a known γ buys is that Δℓ no longer has to be ELECTRICALLY long.** The [20°, 160°] interval
// protects the γ EXTRACTION, where `Acosh`'s branch structure makes βΔℓ = nπ a genuine singularity
// and where (per `PlanarCalibration.Gamma`'s own header) α is two orders below β and its extracted
// sign is noise. The error box's own conditioning degrades as 1/Δℓ — ordinary conditioning, not a
// denominator zero — so Δℓ can be sized from the SUBSTRATE and the mesh instead of from the
// wavelength, and both standards become small and frequency-independent.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// R-qsc-2 — THE STANDARD'S OWN ELECTROSTATICS, NOT `RlgcExtractor`, AND IT IS NOT A PREFERENCE
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// The two candidate suppliers are kernel A's per-unit-length cross-section solve
// (`RlgcExtractor.Extract`) and the standard's own electrostatics. They are not the same quantity:
// the first describes the DUT's DRAWN cross-section, the second describes the STANDARD — the object
// the error box is actually solved from. Four reasons the second wins, and the first is structural:
//
//   • **`PlanarDeembed`'s own D7 rule already says so**: "Kernel A is the ORACLE for Z_c, never an
//     input. Reading Z_c or C_pul off QuasiStaticKernel and feeding it into B would make the phase
//     table's own 'A and B agree on a uniform line' gate a tautology and would import A's
//     discretisation error into B's answer." A γ taken from A is the same sentence one quantity over.
//   • **De-embedding is ALREADY part quasi-static**: `PlanarDeembed.CapacitancePerMetre` runs an
//     electrostatic solve on the two standards and supplies C_pul for Z_c = γ/(jωC_pul). This adds
//     ONE further solve of the same kind — the same geometry with the dielectric removed — and reads
//     L = μ₀ε₀/C₀ off it. Nothing new is introduced; an existing route is extended by its other half.
//   • **Kernel A cannot represent what a standard can.** A coplanar standard's C_pul is a MODAL
//     capacitance (RP-2c), a widened one carries a FLOATING conductor (PCAL3), and a group's is a
//     MATRIX (PCAL4/D7'). All three ride on `PlanarStandard`'s own `ModePotential`/`ModeWeight`/
//     `FloatingPotential` and are honoured here for free, because this is the same solve. Kernel A
//     would need a cross-section it does not have, and a WIDTH that is not defined for a feed that
//     is not a plain rectangle.
//   • **It is self-consistent in the discretisation.** C and C₀ come off the SAME mesh, so
//     ε_eff = C/C₀ and Z_c = √(L/C) share their discretisation error and it cancels to first order in
//     the ratio — which is exactly the argument D7 already makes for DIFFERENCING two standards
//     rather than solving one.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE ARITHMETIC
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
//     C   = C′ − jC″   from the standards' differencing with the real dielectric (complex ε*)
//     C₀  =            the same differencing with every dielectric replaced by vacuum
//     L   = μ₀ε₀/C₀    the quasi-TEM identity — `RlgcExtractor`'s own [L] = μ₀ε₀[C₀]⁻¹, N = 1
//     γ   = jω√(LC)    so α = ω√(LC′)·tanδ_eff/2 and β = ω√(LC′) fall out together
//     Z_c = γ/(jωC′)   D7's OWN spelling, unchanged — only γ's source changed
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// CL3 — THE METAL IS NO LONGER A PERFECT CONDUCTOR HERE, AND R COMES OFF THIS SOLVE'S OWN CHARGE
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// This file shipped saying "the metal is a PERFECT CONDUCTOR here, exactly as it is in the full-wave
// fill by default", which was true while CL1's `PlanarFillSettings.ConductorLoss` was off.
// `brief-conductor-loss-3-default-and-gate.md` turns it on, and leaving γ lossless below the
// crossover would have been loud in exactly one place and silent everywhere else: the standards are
// still solved FULL-WAVE, so their S carries α_c while a lossless γ does not. **On the shipped MMIC
// technology the crossover is 26.07 GHz, so that is not a corner of the band — it is all of it**, and
// α_c is 92-99 % of a GaAs line's loss. A published α four times too small, smooth and plausible, is
// the failure mode this whole directory keeps writing headers about.
//
//     R = Σ_levels Re(Z_s(ω, σ, t)) · [ ΔS_level · Δℓ / |ΔT|² ]
//     S = Σ_cells |q|²/A   (C²/m²)        T = Σ_cells q   (C)        Δ = long − short
//     γ = √( (R + jωL)(jωC) )
//
// **S and T come off the SAME differencing, the SAME two solves and the SAME mesh as C**, which is
// what makes this the third candidate rather than a fourth loss model. The derivation is the TEM
// identity K = v_p·q_s: P′ = ½Re(Z_s)∫|K|²dx and P′ = ½R|I|², so R = Re(Z_s)·∫|q_s|²dx/(∫q_s dx)²
// and v_p cancels. Differencing the two standards is what turns the surface integrals into
// per-metre ones and cancels both end effects exactly, one quantity over from D7's own C_pul.
//
// **R-cl3-0 — THE DECISION WAS MEASURED, AND THE TWO REJECTED CANDIDATES ARE REJECTED BY NUMBER AS
// WELL AS BY DOCTRINE.** α_c out of kernel B's own two lossy standards (the lossy extraction minus
// the PEC one, CL1's own instrument) against the two suppliers, at four frequencies and four meshes:
//
//     supplier                       FR-4 overlap        GaAs coarse      GaAs refined
//     this one (static charge)       0.963-0.990×        0.999-1.010×     1.059-1.147×
//     kernel A's Wheeler term        1.32-1.75×          1.52-1.72×       1.20-1.37×
//
// The static charge tracks the fill's own term to a few per cent AND MOVES WITH THE MESH THE WAY IT
// DOES — turning the edge mesh on raises both by ~32 % on FR-4, where kernel A does not move at all
// because it is a different discretisation of a different formulation. That is the whole argument:
// this is not a better model of the metal, it is the SAME model the fill loaded, read off the same
// mesh, so the two agree across the crossover by construction rather than by calibration.
//
// **Kernel A stays the ORACLE and is still not an input** — R-qsc-2's first bullet and D7's rule,
// unchanged. Its 20-75 % over-read against the fill is not an error in kernel A; it is §CL1's own
// measured 0.63 (FR-4) / 0.73 (GaAs) single-sheet deficit seen from the other side, and importing it
// here would have made both `docs/design/mom-engine.md` §10.9's A-vs-B gate and the crossover's own
// continuity tautologies at once.
//
// **What this does NOT claim.** The static distribution is the UNLOADED one, so its edge crowding is
// not regularised the way the loaded EFIE's is (series overview §1) — which is where the refined-mesh
// GaAs over-read of 6-15 % comes from, and it is reported rather than tuned. It is bounded, not
// divergent, because the mesh that resolves the singularity is the same mesh the fill's own term is
// integrated on.
//
// **Null `Conductor` is the PEC reading and is bit-identical to what shipped**, on exactly the terms
// `PlanarFillSettings.ConductorLoss` null is: `GammaAt` keeps the old `jω√(LC)` expression rather
// than the equivalent `√(jωL·jωC)`, so a run with `PlanarFillSettings.PerfectConductor` set
// reproduces every number QSC recorded.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// CL7 — AND THE GROUND PLANE IS A REAL CONDUCTOR NOW, SO γ NEEDS ITS TERM TOO
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// The block above is CL3's, and its sum is over MESHED LEVELS. `brief-conductor-loss-7` makes the
// extractor write a conducting floor, and the plane is not a meshed level: it is laterally infinite,
// unmeshed, and has no ΔS of its own. Left alone, a GaAs line's published S would carry a ground
// term at every frequency and the γ its error box is solved against would carry one at none of them
// — and on the shipped MMIC technology the quasi-static path is the WHOLE band.
//
//     R_ground = Re(Z_plane(ω, σ, t)) · [ ΔS_g · Δℓ / |ΔT|² ] ,   S_g = Σ_ij q_i q̄_j K(d_ij)
//     K(d)     = h / (π (4h² + d²)^{3/2})                          (PlanarGroundReturn)
//
// **Off the SAME two solves and the SAME mesh**, from the same charge vector, so CL3 §1's whole
// argument transfers. Three things about it are worth having here rather than one file over:
//
//   • **It is a SEPARATE object from `Conductor` and that is load-bearing.**
//     `PlanarFillSettings.PerfectConductor` makes the STRIP perfect and nothing else, because the
//     plane is a termination of the Green's function and not a member of the fill (§CL4 §8). Folding
//     the ground in would have made that flag turn the plane perfect here and not in the full-wave
//     kernel — the exact asymmetry that ate CL4's own first measurement.
//   • **Re(Z_s) is the ONE-SIDED `PlanarSurfaceImpedance.Plane`**, read through the floor itself, so
//     this path and CL6's kernel cannot come to mean different metal. A level's two-sided `Sheet`
//     would halve it.
//   • **It is the RETURN CURRENT's distribution, not the induced CHARGE's.** Those are two different
//     things in an inhomogeneous line and the charge one over-reads by 1.56× on FR-4 and 1.87× on
//     GaAs — the measurement and the reason are in `PlanarGroundReturn`'s own header.
//
// Measured against the fill's own ground term (the lossy-floor two-line extraction minus the PEC one,
// strip perfect on both sides) at four frequencies and four meshes on both starters: **0.887-0.983×**,
// drifting down the band exactly as §CL4 §7's k₀H finding predicts. `RESOLVED.md` §CL7.
//
// **A PERFECT floor produces no `Ground` at all rather than one whose R is zero**, on the same rule
// the paragraph above states for `Conductor`: a `+ 0.0` would put a PEC-ground run on the other
// spelling of γ and move it by an ulp, which is exactly enough to stop `WithPerfectGround` being an
// oracle. And **MIM-4's interior route supplies NO ground term** — `GroundTermSupplied` says so, and
// `PlanarSolve`'s own calibration note reports the residue rather than leaving it to be assumed.
//
// **C stays COMPLEX all the way to γ and that is not decoration.** `PlanarKernelTerms.StaticScalar`
// is built on `GroundedSlab.EpsComplex`, so the charge already carries tanδ; D7 drops it with a
// `.Real` because Z_c takes a double. Dropping it here would publish α = 0 on a lossy board — a
// plausible, wrong number, and one that would read as a regression against the measured path beside
// it in the same `.npy`.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>One calibration standard pair's quasi-TEM description</b> — the per-unit-length C (complex,
/// with the dielectric) and L (from the air-filled solve), and the γ and Z_c that follow. Built
/// ONCE per calibrator and reused at every frequency below the crossover, exactly as D7's own C_pul
/// is: the matrices are frequency-independent (R-mom-11's rule, one kernel over).
/// </summary>
/// <param name="CComplexPerMetre">C′ − jC″ per metre. <see cref="CPerMetre"/> is its real part and is
/// what D7's Z_c divides by.</param>
/// <param name="C0PerMetre">The same differencing with every dielectric replaced by vacuum — the ONLY
/// route to [L] that does not import a second kernel's answer.</param>
public sealed record PlanarQuasiStaticLine(Complex CComplexPerMetre, double C0PerMetre)
{
    /// <summary>
    /// <b>CL3 — the conductor's series resistance per metre, or null for a perfect one.</b> Null is
    /// what shipped before this brief and is bit-identical to it; see the file header for why the
    /// supplier is this solve's own charge and not kernel A.
    /// </summary>
    public PlanarQuasiStaticConductor? Conductor { get; init; }

    /// <summary>
    /// <b>CL7 — the ground plane's own series resistance per metre, or null when the floor is a
    /// perfect conductor or when this route cannot supply one.</b> Orthogonal to
    /// <see cref="Conductor"/>: the strip and the plane are two different surfaces and
    /// <c>PlanarFillSettings.PerfectConductor</c> speaks for only one of them.
    /// </summary>
    public PlanarQuasiStaticGround? Ground { get; init; }

    /// <summary>
    /// <b>Whether the medium this line was extracted in has a conducting floor AND this route can
    /// read it.</b> False on MIM-4's interior route even when the floor conducts — a general stack's
    /// return current has no closed form (see <c>PlanarGroundReturn</c>'s header) — so a
    /// caller can report the residue rather than assume it away.
    /// </summary>
    public bool GroundTermSupplied => Ground is not null;

    /// <summary>C′ — F/m, the real part, which is what <see cref="PlanarDeembed.CharacteristicImpedance"/>
    /// takes and what the <c>Cpul</c> diagnostic cube publishes.</summary>
    public double CPerMetre => CComplexPerMetre.Real;

    /// <summary>L = μ₀ε₀/C₀ — H/m. The quasi-TEM identity, N = 1.</summary>
    public double LPerMetre => EmConstants.Mu0 * EmConstants.Eps0 / C0PerMetre;

    /// <summary>ε_eff = C′/C₀, the number M1's tables are read in.</summary>
    public double EffectivePermittivity => CComplexPerMetre.Real / C0PerMetre;

    /// <summary>
    /// γ = jω√(LC) at one frequency. β &gt; 0 and α ≥ 0 come out of the principal square root without
    /// a branch decision — which is the whole point of not extracting it: there is no acosh here, no
    /// 2π ambiguity, and nothing to continue from the previous frequency.
    /// </summary>
    public Complex GammaAt(double fHz)
    {
        double omega = 2.0 * Math.PI * fHz;

        // The PEC reading keeps its own expression rather than the algebraically identical
        // √(jωL·jωC), so a run with PlanarFillSettings.PerfectConductor set reproduces every number
        // QSC recorded BIT FOR BIT. The two agree to the last digit of the physics and not to the
        // last bit of the arithmetic, and the oracle has to be the second kind.
        if (Conductor is null && Ground is null)
            return Complex.ImaginaryOne * omega * Complex.Sqrt(LPerMetre * CComplexPerMetre);

        double r = (Conductor?.ResistancePerMetreAt(omega) ?? 0.0)
                 + (Ground?.ResistancePerMetreAt(omega)    ?? 0.0);
        Complex z = r + Complex.ImaginaryOne * omega * LPerMetre;
        Complex y = Complex.ImaginaryOne * omega * CComplexPerMetre;
        return Complex.Sqrt(z * y);
    }

    /// <summary>R per metre at one frequency — 0 on a perfect conductor. Reported rather than
    /// inferred from γ, because α_c and α_d are not separable once they are inside a square root.</summary>
    public double ResistancePerMetreAt(double fHz) =>
        (Conductor?.ResistancePerMetreAt(2.0 * Math.PI * fHz) ?? 0.0) +
        (Ground?.ResistancePerMetreAt(2.0 * Math.PI * fHz)    ?? 0.0);

    /// <summary><b>The GROUND PLANE's half of R on its own</b>, Ω/m — 0 on a perfect floor, and on
    /// the interior route where it is not supplied. Reported because it is not recoverable from the
    /// total, and because the two zeros are different facts.</summary>
    public double GroundResistancePerMetreAt(double fHz) =>
        Ground?.ResistancePerMetreAt(2.0 * Math.PI * fHz) ?? 0.0;

    /// <summary><b>The STRIP's half of R on its own</b>, Ω/m — CL3's own reading, unchanged.</summary>
    public double ConductorResistancePerMetreAt(double fHz) =>
        Conductor?.ResistancePerMetreAt(2.0 * Math.PI * fHz) ?? 0.0;

    /// <summary>
    /// <b>The same reading as <see cref="PlanarCalibration.Gamma"/> returns</b>, so the two paths
    /// hand the sweep one type and every diagnostic downstream reads one field.
    ///
    /// <para><b><see cref="PlanarCalibration.GammaResult.Usable"/> is TRUE here whatever βΔℓ comes
    /// out at, and that is the finding rather than a relaxation.</b> The [20°, 160°] interval is
    /// drawn around D5/D6's shared denominator zero at βΔℓ = nπ — it exists to protect the
    /// EXTRACTION of γ from `½·tr(M)`, which is not performed on this path at all. The error box's
    /// own conditioning is a different and far gentler thing, and it is gated separately on the a₂₂
    /// sign margin (<see cref="PlanarErrorBox.RejectedResidual"/>) rather than on an angle.
    /// Reporting the realised angle anyway is deliberate: it is the number that says how short the
    /// standards actually are.</para>
    ///
    /// <para><see cref="PlanarCalibration.GammaResult.Unwrapped"/> is 0 by construction — there is
    /// no principal branch to add 2π to.</para>
    /// </summary>
    public PlanarCalibration.GammaResult ResultAt(double fHz, double deltaLM)
    {
        Complex g = GammaAt(fHz);
        return new PlanarCalibration.GammaResult(
            g, g.Imaginary * deltaLM * 180.0 / Math.PI, 0, true);
    }

    /// <summary>
    /// <b>Extract the pair from the two EXTREME standards, by the same differencing D7 already
    /// does.</b> Two electrostatic solves with the dielectric and two without; the ones WITH it are
    /// the identical call <see cref="PlanarDeembed.CapacitancePerMetre"/> makes, so a run whose band
    /// never reaches the quasi-static path pays for none of this and a run that does pays for the
    /// air-filled half only.
    ///
    /// <para>Both standards' <see cref="PlanarStandard.ModePotential"/>,
    /// <see cref="PlanarStandard.ModeWeight"/> and <see cref="PlanarStandard.FloatingPotential"/> ride
    /// along untouched, so a coplanar or widened standard measures the capacitance its own PORT's mode
    /// sees rather than the sheet's total — RP-2c and PCAL3's rules, inherited rather than restated.</para>
    /// </summary>
    public static PlanarQuasiStaticLine Extract(
        PlanarStandard shortStd, PlanarStandard longStd, GroundedSlab slab,
        PlanarFillSettings? settings = null,
        PlanarFillCores? shortCores = null, PlanarFillCores? longCores = null,
        double knownC0PerMetre = double.NaN)
    {
        var terms = PlanarKernelTerms.StaticScalar(slab);
        var q1 = PlanarDeembed.StaticChargeComplex(shortStd.Mesh, terms, settings, shortCores,
                                                   slab.HeightM, shortStd.ModePotential,
                                                   shortStd.FloatingPotential);
        var q2 = PlanarDeembed.StaticChargeComplex(longStd.Mesh, terms, settings, longCores,
                                                   slab.HeightM, longStd.ModePotential,
                                                   longStd.FloatingPotential);

        // SI-SIG — a conducting substrate re-extracts this line at every frequency, and the air-filled
        // half has no material in it, so it is solved once and handed back.
        Complex c0 = !double.IsNaN(knownC0PerMetre) ? knownC0PerMetre : PlanarDeembed.CapacitancePerMetreComplex(
            shortStd, longStd, slab, settings, shortCores, longCores, airFilled: true);

        // CL7 — the PLANE's own RETURN CURRENT, off this same solve and this same mesh. The slab
        // puts the metal one image-height above one plane, which is the geometry PlanarGroundReturn's
        // closed form describes — which is why the ground term is supplied on THIS overload and not
        // on the interior one below.
        return FromCharge(shortStd, longStd, q1, q2, c0, settings, slab.Floor, slab.HeightM);
    }

    /// <summary><b>MIM-4's medium, the same way.</b> Only the electrostatic kernel changes; the
    /// differencing, the end-effect cancellation and every guard are the ones above.</summary>
    public static PlanarQuasiStaticLine Extract(
        PlanarStandard shortStd, PlanarStandard longStd,
        LayerStack stack, double levelZ, double referenceHeightM,
        PlanarFillSettings? settings = null,
        PlanarFillCores? shortCores = null, PlanarFillCores? longCores = null,
        InteriorStaticModel? model = null, double knownC0PerMetre = double.NaN)
    {
        var terms = PlanarKernelTerms.StaticScalarAt(
            model ?? InteriorStaticImages.FitScalar(stack, levelZ, levelZ));
        var q1 = PlanarDeembed.StaticChargeComplex(shortStd.Mesh, terms, settings, shortCores,
                                                   referenceHeightM, shortStd.ModePotential,
                                                   shortStd.FloatingPotential);
        var q2 = PlanarDeembed.StaticChargeComplex(longStd.Mesh, terms, settings, longCores,
                                                   referenceHeightM, longStd.ModePotential,
                                                   longStd.FloatingPotential);

        Complex c0 = !double.IsNaN(knownC0PerMetre) ? knownC0PerMetre : PlanarDeembed.CapacitancePerMetreComplex(
            shortStd, longStd, stack, levelZ, referenceHeightM, settings, shortCores, longCores,
            model, airFilled: true);

        // CL7 — NO GROUND TERM ON THIS ROUTE, and that is stated rather than defaulted. A standard
        // on a buried level of a stratified stack is not one horizontal current one image-height
        // above one plane, so PlanarGroundReturn's closed form does not describe it.
        // `GroundTermSupplied` is false here and a caller can report the residue; see the header of
        // PlanarGroundReturn.cs and `RESOLVED.md` §CL7 for its measured size.
        return FromCharge(shortStd, longStd, q1, q2, c0, settings, null, 0.0);
    }

    /// <summary>
    /// <b>The two standards' charge vectors, differenced into C and — when the fill carried a
    /// conductor term — into the sheet-geometry ratio R is read through.</b>
    ///
    /// <para>One pass over the two solves the run already owed. <c>C</c> comes out of the SAME
    /// weighted sums <see cref="PlanarDeembed.StaticCapacitanceComplex"/> performs, in the same
    /// order, so a PEC run's number is bit-identical to what it was.</para>
    /// </summary>
    private static PlanarQuasiStaticLine FromCharge(
        PlanarStandard shortStd, PlanarStandard longStd,
        Complex[] q1, Complex[] q2, Complex c0, PlanarFillSettings? settings,
        Termination? floor, double heightM)
    {
        double dl = longStd.LengthM - shortStd.LengthM;
        if (!(dl > 0))
            throw new InvalidOperationException("The two calibration standards have the same length.");

        Complex t1 = Total(shortStd, q1), t2 = Total(longStd, q2);
        Complex c  = (t2 - t1) / dl;

        var line = Checked(c, c0);

        Complex dT  = t2 - t1;
        double  den = dT.Magnitude * dT.Magnitude;

        // ── CL7 — the GROUND's own ΔS, before and independently of the strip's ────────────────
        //
        // Independently, because PlanarFillSettings.PerfectConductor makes the STRIP perfect and
        // nothing else: the plane is a termination of the Green's function, not a member of the
        // fill. Reading the ground term out of the same `if` as the levels' would have made that
        // flag silently turn the plane perfect here and not in the full-wave kernel — the exact
        // asymmetry CL4 §8 records eating its own first measurement.
        //
        // A PEC floor, any floor that is not a conducting plane, and any PERFECT spelling of one
        // contribute NOTHING and are not asked for, so a perfect-ground run's γ keeps CL3's
        // arithmetic to the bit rather than gaining a `+ 0.0`.
        if (den > 0 && heightM > 0 &&
            floor is { Kind: TerminationKind.SurfaceImpedance } f &&
            !PlanarSurfaceImpedance.IsPerfect(f.ConductivitySm, f.ThicknessM))
        {
            double g1 = PlanarGroundReturn.SecondMoment(shortStd.Mesh, q1, heightM);
            double g2 = PlanarGroundReturn.SecondMoment(longStd.Mesh,  q2, heightM);
            line = line with { Ground = new PlanarQuasiStaticGround((g2 - g1) * dl / den, f) };
        }

        if ((settings ?? PlanarFillSettings.Default).ConductorLoss is not { } loss) return line;

        // A metal DECLARED perfect on the stackup and a metal asked to be perfect with
        // PlanarFillSettings.PerfectConductor are the same physical claim, so they must produce the
        // same γ — and γ has two spellings here that agree to the last digit of the physics and not
        // to the last bit. Attaching a conductor whose R is identically zero would put a PEC run on
        // the OTHER spelling and move it by an ulp, which is exactly enough to stop the flag being
        // an oracle.
        if (loss.IsPerfectOn(longStd.Mesh)) return line;

        // ΔS per LEVEL, because Re(Z_s) is per level and a multi-level standard's cells are not all
        // made of the same metal. The ratio carries Δℓ/|ΔT|² already, so what is left at each
        // frequency is one multiply per level.
        var mesh = longStd.Mesh;
        int levels = Math.Max(mesh.LayerNames.Count, 1);
        var ratio  = new double[levels];
        var s1 = SecondMoment(shortStd, q1, levels);
        var s2 = SecondMoment(longStd,  q2, levels);

        if (!(den > 0)) return line;

        for (int i = 0; i < levels; i++) ratio[i] = (s2[i] - s1[i]) * dl / den;

        return line with { Conductor = new PlanarQuasiStaticConductor(ratio, loss, mesh) };
    }

    /// <summary>Σ w·q, exactly as the capacitance reading totals it.</summary>
    private static Complex Total(PlanarStandard std, Complex[] q)
    {
        var w = std.ModeWeight;
        Complex total = Complex.Zero;
        if (w is null) for (int i = 0; i < q.Length; i++) total += q[i];
        else           for (int i = 0; i < q.Length; i++) total += w[i] * q[i];
        return total;
    }

    /// <summary>
    /// <c>Σ |q|²/A</c> per conductor LEVEL — the surface integral of the squared charge density,
    /// which is the squared CURRENT density up to the TEM factor v_p that cancels in the ratio.
    ///
    /// <para><b>The mode weight is deliberately NOT applied here.</b> It exists to pick out which
    /// mode's capacitance is being read, i.e. to combine two conductors' charges with a SIGN; the
    /// loss integral is a sum of squares over metal and every conductor the mode drives dissipates.
    /// A signed weight under a square would cancel the return conductor's own loss.</para>
    /// </summary>
    private static double[] SecondMoment(PlanarStandard std, Complex[] q, int levels)
    {
        var s = new double[levels];
        var cells = std.Mesh.Cells;
        for (int i = 0; i < q.Length; i++)
        {
            double a = cells[i].Area;
            if (!(a > 0)) continue;
            int lvl = Math.Clamp(cells[i].LayerIndex, 0, levels - 1);
            s[lvl] += q[i].Magnitude * q[i].Magnitude / a;
        }
        return s;
    }

    /// <summary>
    /// The one place both routes are sanity-checked, because a non-positive C₀ is not a small error:
    /// L = μ₀ε₀/C₀ would come back negative or infinite and γ = jω√(LC) would then be a REAL number,
    /// i.e. a line that attenuates without propagating — complete, smooth, and wrong.
    /// </summary>
    private static PlanarQuasiStaticLine Checked(Complex c, Complex c0)
    {
        if (!(c0.Real > 0) || !(c.Real > 0))
            throw new InvalidOperationException(
                $"The quasi-static port calibration differenced the two standards' static " +
                $"capacitances and got C = {c.Real:G4} F/m with the dielectric and " +
                $"{c0.Real:G4} F/m without it; both have to be positive for L = μ₀ε₀/C₀ and " +
                "γ = jω√(LC) to describe a propagating line. The two standards differ only in the " +
                "bulk cells between their reference planes, so a non-positive difference means the " +
                "electrostatic solve did not converge on one of them rather than that the geometry " +
                "is unusual.");

        return new PlanarQuasiStaticLine(c, c0.Real);
    }
}

/// <summary>
/// <b>CL3 — what a quasi-static line needs to put R into γ: one geometry ratio per conductor level,
/// and the SAME <see cref="PlanarConductorLoss"/> object the fill loaded Z_s from.</b>
///
/// <para>The split is deliberate. <see cref="RatioByLayer"/> is geometry and comes off the
/// electrostatic solve ONCE; Re(Z_s) is the frequency-dependent half and is asked per point, from
/// the object the fill used — so the two cannot come to mean different metal. The lookup is
/// <see cref="PlanarConductorLoss.SheetTable"/>'s, which resolves by layer NAME first: CL1 records
/// that <c>PlanarCalibration.BuildLine</c> emits every cell with <c>LayerIndex = 0</c> and one layer
/// name, so an index-only lookup would hand a Metal-2 standard Metal-1's metal, silently.</para>
/// </summary>
/// <param name="RatioByLayer">ΔS_level·Δℓ/|ΔT|², in 1/m per Ω/square. Frequency-independent.</param>
/// <param name="Loss">The fill's own model of the metal.</param>
/// <param name="Mesh">The standard's mesh, for the name-first level resolution.</param>
public sealed record PlanarQuasiStaticConductor(
    IReadOnlyList<double> RatioByLayer,
    PlanarConductorLoss   Loss,
    PlanarMesh            Mesh)
{
    /// <summary>R per metre at one angular frequency, Ω/m. Exactly 0 on a perfect conductor,
    /// through <see cref="PlanarSurfaceImpedance.Sheet"/>'s own zero rather than by being absent.</summary>
    public double ResistancePerMetreAt(double omegaRadS)
    {
        var zs = Loss.SheetTable(Mesh, omegaRadS);
        double r = 0;
        for (int i = 0; i < RatioByLayer.Count; i++)
            r += (i < zs.Length ? zs[i].Real : 0.0) * RatioByLayer[i];
        return r;
    }
}

/// <summary>
/// <b>CL7 — what a quasi-static line needs to put the GROUND PLANE's R into γ.</b> The plane's
/// counterpart of <see cref="PlanarQuasiStaticConductor"/>, and deliberately a SEPARATE object.
///
/// <para><b>It is separate because <see cref="PlanarFillSettings.PerfectConductor"/> makes the STRIP
/// perfect and nothing else</b> (CL4 §8) — the plane is a termination of the Green's function, not a
/// member of the fill. Folding the ground into the conductor record would have made that flag turn
/// the plane perfect too on this path and not on the full-wave one, which is the exact asymmetry
/// CL4's own first measurement died of, and it would have made the four-alpha instrument
/// unmeasurable.</para>
///
/// <para><b>Re(Z_s) here is the ONE-SIDED <see cref="PlanarSurfaceImpedance.Plane"/></b>, read
/// through the floor itself so this path and CL6's kernel cannot come to mean different metal — the
/// plane has air below it and carries its return current on one face, where a strip is excited on
/// both. A meshed level's two-sided <see cref="PlanarSurfaceImpedance.Sheet"/> would halve it.</para>
/// </summary>
/// <param name="Ratio">ΔS_g·Δℓ/|ΔT|², in 1/m per Ω/square, from
/// <see cref="PlanarGroundReturn.SecondMoment"/>. Frequency-independent.</param>
/// <param name="Floor">The medium's own floor — <see cref="GroundedSlab.Floor"/>.</param>
public sealed record PlanarQuasiStaticGround(double Ratio, Termination Floor)
{
    /// <summary>R_ground per metre at one angular frequency, Ω/m.</summary>
    public double ResistancePerMetreAt(double omegaRadS) =>
        Floor.SurfaceImpedanceAt(omegaRadS).Real * Ratio;
}
