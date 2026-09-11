// ANT-11 §3/§4 — THE FINITE-GROUND CORRECTION: THE ONE PREDICATE, AND THE MEASUREMENTS THAT REFUSED IT.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// THIS FILE IS A REFUSAL, AND THE REFUSAL IS THE RESULT. IT IS NOT A PLACEHOLDER.
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// `brief-antenna-11` proposed a UTD edge-diffraction estimate: the infinite-ground pattern
// illuminates the ground rim, the rim re-radiates, and the sum has back radiation and ripple. It is
// standard, it is a pure post-process, and on the face of it it is cheap. **It was measured and it is
// INERT on every structure this kernel can produce a pattern for.** Both measurements are in
// PlanarFiniteGroundTests, and the second one is a correction to the brief rather than a confirmation
// of it. RESOLVED.md §ANT-11 carries the tables; what follows is why they decide the question.
//
// ── R-fg-4. THE RIM ILLUMINATION IS IDENTICALLY ZERO, AND FOR TWO INDEPENDENT REASONS ─────────
//
// The estimate's input is the primary pattern AT GRAZING — F(θ = 90°, φ_rim) — because that is the
// direction the rim lies in from the source. That quantity is not small. It is ZERO, exactly, and
// the two polarizations get there by different routes:
//
//   TM:  f_TM = cosθ·(1 + Γ^e)·e^{+jk_z0 h}       and cos 90° = 0, with (1 + Γ^e) → 2, finite.
//   TE:  f_TE = (1 + Γ^h)·e^{+jk_z0 h}            and Γ^h → −1 as k_ρ → k₀, so (1 + Γ^h) → 0.
//
// The TE leg is L8a's own theorem, not a numerical accident: `1 + Γ` vanishing at k_ρ = k₀ is exactly
// the identity that made DCIM's far-field failure structural (§L8a, "ΣA_i is a theorem, not a fit").
// Stated without reference to either spelling: **at grazing the top half-space's TM characteristic
// IMPEDANCE vanishes and its TE characteristic ADMITTANCE vanishes, so a horizontal current at any
// height launches nothing along the surface.** That form is why the zero also holds in the GENERAL
// stratified kernel, whose f_TE/f_TM come from a cascade traversal and share no algebra with the
// one-slab expressions above — measured on both, on four stacks, and end to end on a real pattern at
// **−281.66 dB below the peak** while θ = 85° is only −2.24 dB down.
//
// The one current direction whose grazing field does NOT vanish is the VERTICAL one — a probe, a
// monopole, an IFA — and `PlanarFarField.VerticalBasisRefusal` refuses a vertical basis by name. So
// the set of structures whose pattern this kernel will compute and the set whose rim illumination is
// non-zero **do not intersect**, and that is a structural statement about the two refusals rather
// than a property of any board.
//
// Why this matters more than "the estimate would be inaccurate": an estimate that is identically zero
// PASSES the brief's own strongest self-test — "as the ground outline grows the corrected pattern must
// converge to the primary one and the F/B must grow without bound" — VACUOUSLY, at every ground size,
// because corrected ≡ primary and F/B ≡ ∞ always. It would ship as a capability that answers nothing
// and whose gate cannot tell. That is the failure mode this directory keeps refusing: a smooth,
// plausible, wrong answer rather than a visible one.
//
// ── R-fg-5. UTD IS NOT THE ASYMPTOTIC METHOD THE BRIEF BUDGETED FOR — IT IS EXACT HERE ────────
//
// The brief's §3.2 expected the validity range to be set by UTD's high-frequency asymptotics: "on a
// ground plane of a fraction of a wavelength it degrades… find where it breaks and refuse past it."
// **That premise is false, and the measurement is unambiguous.** For a straight perfectly-conducting
// edge — exterior wedge angle 2π, which is what the rim of a thin plane is — the Kouyoumjian-Pathak
// coefficient WITH its transition function reproduces Sommerfeld's exact half-plane solution to
// **1.6e-14 absolute against unit incidence, at every k·ρ from 0.2 to 120**, both polarizations, over every incidence and
// observation angle sampled. It is not an asymptotic approximation that degrades; it is the exact
// solution rewritten. Recorded because it redirects the budget: a future taker should NOT spend the
// validity argument on the coefficient, and a refusal written against k·ρ would have been a refusal
// against nothing.
//
// What DOES bound a finite plane is elsewhere and is NOT measured here: the locality assumption (a
// rim treated as locally straight, on a plane whose corners are a fraction of a wavelength apart) and
// MULTIPLE diffraction across the plane, rim to rim. The neglected second-order term scales as
// |D|/√w ~ 1/√(2π k w), which at the measured board's k·w = 2.56 is of order 0.25 — the same order as
// the term that would be kept. **That is an analytic estimate and it is labelled one:** the direct
// measurement is degenerate exactly where it matters, because the rim-to-rim direction lies ON both of
// the far edge's shadow boundaries, where the coefficient needs KP's transition limits — machinery
// only the estimate itself would need. RESOLVED.md §ANT-11 says what a taker must measure.
//
// ── R-fg-6. THE REFUSAL IS NARROWED, NOT DELETED, AND THIS FILE IS WHERE IT NARROWED ──────────
//
// `LayeredMedium.CanHost`'s rule: deleting a refusal instead of narrowing it is how a kernel starts
// silently answering questions it cannot answer. ANT-5 shipped `FrontToBackDb` present-and-refused
// with a sentence that named "the finite-ground phase" as the thing that would supply it. That
// sentence was a PROMISE, and it can no longer be kept as written. So it narrows twice over:
//
//   • it is now a function of the PROBLEM rather than a constant — an outline present and an outline
//     absent get different sentences, and the present case quotes the outline's own size; and
//   • it names a MEASURED reason and what would lift it, instead of naming a phase.
//
// `CanCorrect` is the one predicate `PlanarMetrics`' registry entry asks, and the one place a future
// phase flips. Everything downstream of it — the picker, the exporter, the CLI, the cube list — is
// already plumbed and needs no edit, which is what ANT-5's staging bought and it did hold.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>ANT-11 — whether the finite-ground correction can be computed, and why not.</b> See the file
/// header for the two measurements (R-fg-4, R-fg-5) and for why a refusal is the result rather than a
/// placeholder.
/// </summary>
public static class PlanarFiniteGround
{
    /// <summary>
    /// The grazing row's measured level below the pattern peak on the reference structure — the number
    /// R-fg-4 rests on, quoted in the refusal so the claim is checkable rather than asserted. A
    /// STRUCTURAL zero: 2.4e-35 W/sr against a 3.5e-7 W/sr peak, with θ = 85° only 2.24 dB down.
    /// </summary>
    public const double MeasuredGrazingLevelDb = -281.66;

    /// <summary>
    /// R-fg-5's measurement: the worst relative disagreement between the Kouyoumjian-Pathak half-plane
    /// coefficient and Sommerfeld's exact solution, over k·ρ ∈ [0.2, 120] and both polarizations. Kept
    /// as a constant because it is the number that says the coefficient is NOT where the validity
    /// budget goes, and a later phase must not re-derive it from memory.
    /// </summary>
    public const double MeasuredUtdHalfPlaneAgreement = 2e-14;

    /// <summary>
    /// <b>The one predicate.</b> Whether a finite-ground correction — a lower hemisphere, and with it
    /// a front-to-back ratio — can be computed for this problem at this frequency.
    ///
    /// <para><b>It refuses for every input today</b>, and the sentence it refuses with depends on the
    /// problem: an absent outline and a present one are different situations and a user can act on
    /// only one of them. When a future phase supplies a correction this is the single place that
    /// changes; nothing downstream is re-plumbed (R-fg-6).</para>
    /// </summary>
    /// <param name="problem">The problem as extracted — <see cref="PlanarProblem.GroundOutline"/> is
    /// what decides which branch of the refusal applies.</param>
    /// <param name="frequencyHz">The pattern's own frequency, so the outline can be quoted in λ₀.</param>
    /// <param name="metalBounds">The analysed metal's bounds, for the margin measure. Optional.</param>
    public static EmSuitability CanCorrect(
        PlanarProblem problem, double frequencyHz,
        (double MinX, double MinY, double MaxX, double MaxY)? metalBounds = null)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var extent = PlanarGroundExtent.Of(problem.GroundOutline, metalBounds, frequencyHz);
        return extent is null ? EmSuitability.No(NoOutlineRefusal)
                              : EmSuitability.No(OutlineRefusal(extent));
    }

    /// <summary>
    /// <b>No ground outline was drawn</b> — ANT-11 §5's assert-this-did-not-become-a-deletion case.
    /// Distinct from <see cref="OutlineRefusal"/> because the user action is different: here there is
    /// something to draw, there there is not.
    /// </summary>
    public const string NoOutlineRefusal =
        "There is no finite ground outline in this analysis, so there is nothing to correct the " +
        "infinite-plane pattern against. The ground plane is the laterally infinite PEC the Green's " +
        "function terminates on and the field below it is identically zero by construction, so the " +
        "θ axis stops at 90° and the front-to-back ratio is infinite rather than large. An outline " +
        "is read from artwork drawn on the RETURN PLANE's own conductor layer — the highest " +
        "ground-designated conductor below the signal — so a run whose ground comes from the " +
        "stackup's bottom boundary, or whose plane was simply never drawn, has none to read. Drawing " +
        "the pour makes the plane's SIZE reportable; it does not by itself make the correction " +
        "available, and the reason is the sentence the with-outline case gives.";

    /// <summary>
    /// <b>An outline IS present and the correction is still refused</b>, quoting the outline's own
    /// size. This is the sentence ANT-5's staged promise narrowed INTO: a measured reason and what
    /// would lift it, in place of the name of a phase.
    /// </summary>
    public static string OutlineRefusal(PlanarGroundExtent extent)
    {
        ArgumentNullException.ThrowIfNull(extent);
        return
            $"A finite ground outline IS present — {extent.BoxWidthLambda:G3} × " +
            $"{extent.BoxHeightLambda:G3} λ₀, with {extent.MarginLambda:G3} λ₀ of it beyond the " +
            $"analysed metal — and the lower hemisphere is STILL not computed, for a reason that was " +
            $"measured rather than assumed. The standard edge-diffraction estimate takes the rim's " +
            $"illumination from the pattern AT GRAZING, and that quantity is not small in this " +
            $"kernel: it is zero exactly. The TM element factor carries a cos θ and the TE element " +
            $"factor carries (1 + Γ^h), which vanishes at k_ρ = k₀ — equivalently, at grazing the " +
            $"upper half-space's TM characteristic impedance and its TE characteristic admittance " +
            $"both vanish, so a HORIZONTAL current at any height launches nothing along the surface. " +
            $"Measured at {MeasuredGrazingLevelDb:F0} dB below the pattern peak where θ = 85° is 2.2 dB " +
            $"down. An estimate built on it would be identically zero at every ground size, and would " +
            $"pass the converge-to-the-infinite-limit self-test vacuously. The current direction whose " +
            $"grazing field does NOT vanish is the vertical one, and a vertical basis is separately " +
            $"refused by name, so the two sets do not intersect. What it would take is an illumination " +
            $"taken from the SURFACE FIELD at the rim — a spatial-domain quantity, not a far-field one " +
            $"— together with a diffraction coefficient for the rim of a GROUNDED DIELECTRIC SLAB " +
            $"rather than of a bare conductor; and the dominant back-radiation mechanism on a thin " +
            $"substrate is the surface wave reaching a BOARD edge, which the 2.5-D premise cannot " +
            $"express at all because every dielectric layer here is laterally infinite. Those are " +
            $"named as not built rather than approximated.";
    }
}
