// L8c — WHICH terms are extracted, and what the smooth remainder is.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THERE ARE THREE SINGULAR PIECES IN THIS KERNEL, NOT ONE, AND THE SECOND IS THE ONE THAT GETS
// MISSED. Read DcimModel.Evaluate beside this file; every coefficient below names its term there.
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
//   G(ρ) = (1 + QuasiStatic)·e^{−jk₀ρ}/(4πρ)                       ← 1/ρ,  the obvious one
//        + Σ_poles  Residue·(−j/4)·H₀⁽²⁾(k_p ρ)                    ← ln ρ, NOT obvious, NOT optional
//        + Σ_images A_i·e^{−jk₀R_i}/(4πR_i),  R_i = √(ρ² + b_i²)   ← smooth PROVIDED b_i ≠ 0
//
// • The FIRST term is what every MoM text tells you to extract.
//
// • The SECOND is a real logarithmic singularity and it is easy to walk past. H₀⁽²⁾ = J₀ − jY₀ and
//   Y₀(z) → (2/π)(ln(z/2) + γ) as z → 0, so −(j/4)H₀⁽²⁾ = −(j/4)J₀ − (¼)Y₀ carries −(1/2π)·ln ρ.
//   A grounded slab ALWAYS has at least one surface wave, however thin — L8a's R-lgf-3 verified that
//   down to h = 1 µm — so this term is never absent and the coefficient is never zero. An
//   implementation that extracts only 1/ρ pushes a logarithm through Gauss-Legendre and converges
//   slowly toward something plausible and wrong.
//
// • The THIRD is smooth ONLY IF no fitted image depth is small compared with a cell. That is a
//   CONDITION, not a fact, and R-fil-8 measures it rather than assuming it —
//   PlanarKernelTerms.SmallestImageDepth exists for exactly that measurement.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D3 — HOW MANY TERMS TO EXTRACT IS A MEASUREMENT, NOT A GUESS
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// The 1/ρ and the ln ρ are mandatory (R-fil-3). Whether to also remove the next orders is a genuine
// trade — one more closed form each, against a quadrature-order saving — and L8a's own precedent
// applies exactly: higher-order exact statements are not automatically better, and it recorded a
// table rather than a preference. The three settings are:
//
//   Order 0 (Inverse)  remove C₁/ρ and C_log·ln ρ. The remainder is BOUNDED, tending to C_const.
//   Order 1 (Constant) also remove C_const. The remainder tends to 0 like ρ — continuous, with a
//                      conical kink at ρ = 0, since ρ = |r − r′| is not smooth there.
//   Order 2 (Linear)   also remove C_lin·ρ. The remainder is then O(ρ²) — genuinely smooth, which is
//                      what a Gauss rule wants. Costs the ∫∫r and ∫∫u·r closed forms.
//
// PlanarFillTests' Tier 6 measures all three and PlanarFillCostTests reports what each costs. The
// DEFAULT is stated there, with the measurement that chose it, rather than here.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE REMAINDER IS EVALUATED BY SUBTRACTION, WITH A FLOOR, AND THE ERROR THAT COSTS IS BOUNDED
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Remainder(ρ) = G(ρ) − Extracted(ρ) loses digits as ρ → 0, because both sides grow like C₁/ρ. The
// loss is bounded and small: at ρ the absolute error is ~ε·|C₁|/ρ, and the quadrature weight that
// multiplies it is ~A_b/n, so relative to the entry's own scale (~A_b/(4πd), d a cell size) the error
// is ~(d/(n·ρ))·ε. Even at ρ = 1e-8·d that is 1e-9 — far below the kernel's own 6e-3 (L8a's R-lgf-4).
// Exact coincidence is the only genuine failure, and it happens: an outer and an inner Gauss rule of
// the same order on the same cell share nodes exactly. So ρ ≤ RhoFloor returns the ANALYTIC limit,
// which is known in closed form for every order. The floor is set by PlanarFill from the mesh's own
// smallest cell and is reported (R-fil-5), not hidden.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>D3's three settings. See the file header for what each leaves behind.</summary>
public enum PlanarExtractionOrder
{
    /// <summary>1/ρ and ln ρ only — R-fil-3's mandatory pair. Remainder is bounded.</summary>
    Inverse = 0,
    /// <summary>…and the constant. Remainder → 0 like ρ, i.e. continuous with a conical kink.</summary>
    Constant = 1,
    /// <summary>…and the linear term. Remainder is O(ρ²). Costs two more closed forms.</summary>
    Linear = 2,
}

/// <summary>
/// One kernel (G_A or G_q), decomposed into the analytically-integrable singular cores plus a smooth
/// remainder. <b>Every coefficient names the term of <see cref="DcimModel.Evaluate"/> it comes from</b>
/// — R-fil-3's requirement, so that a future change to the DCIM's own decomposition cannot silently
/// leave a singularity unextracted.
/// </summary>
public sealed class PlanarKernelTerms
{
    /// <summary>Coefficient of <c>1/ρ</c>. From the direct + quasi-static-image term.</summary>
    public Complex Inverse { get; }

    /// <summary>Coefficient of <c>ln ρ</c>. From the surface-wave sum — <c>−Σ Residue/(2π)</c>.</summary>
    public Complex Log { get; }

    /// <summary>Coefficient of <c>1</c>: the kernel's value at ρ = 0 once 1/ρ and ln ρ are gone.
    /// Collects a piece from all three terms.</summary>
    public Complex Constant { get; }

    /// <summary>Coefficient of <c>ρ</c>. Only the direct term has one; the surface wave's next
    /// correction is O(ρ²ln ρ) and an image's is O(ρ²).</summary>
    public Complex Linear { get; }

    public PlanarExtractionOrder Order { get; }

    /// <summary>Below this ρ, <see cref="Remainder"/> returns its analytic limit instead of a
    /// difference of two large numbers. Reported by the fill, per R-fil-5.</summary>
    public double RhoFloor { get; }

    /// <summary>
    /// R-fil-8 — <b>the smallest |b_i| among the fitted complex images</b>, or
    /// <see cref="double.PositiveInfinity"/> when there are none. The claim "the images are smooth" is
    /// conditional on this being large against a cell dimension; the fill reports the ratio rather
    /// than assuming it.
    /// </summary>
    public double SmallestImageDepth { get; }

    private readonly Func<double, Complex> _full;
    private readonly Func<double, Complex>? _derivative;

    public PlanarKernelTerms(Func<double, Complex> full, Complex inverse, Complex log,
                             Complex constant, Complex linear,
                             PlanarExtractionOrder order = PlanarExtractionOrder.Constant,
                             double rhoFloor = 0.0,
                             double smallestImageDepth = double.PositiveInfinity,
                             Func<double, Complex>? derivative = null)
    {
        _full = full; _derivative = derivative;
        Inverse = inverse; Log = log; Constant = constant; Linear = linear;
        Order = order; RhoFloor = rhoFloor; SmallestImageDepth = smallestImageDepth;
    }

    /// <summary>
    /// <b>L9c — <c>dG/dρ</c> in closed form, present only on the MIXED component's terms.</b> The ẑx̂
    /// dyadic entry is <c>j ∂G/∂x = j G′(ρ)(x−x′)/ρ</c>, so the fill never asks the mixed kernel for a
    /// value — it asks for this. Null on every other kernel, where it would have no meaning.
    /// </summary>
    public Complex Derivative(double rhoM) =>
        _derivative is null
            ? throw new InvalidOperationException(
                "These terms carry no radial derivative. Only the MIXED component has one, because it " +
                "is the only kernel whose dyadic entry is a ∂/∂x rather than a value.")
            : _derivative(rhoM);

    /// <summary>True when <see cref="Derivative"/> is available.</summary>
    public bool HasDerivative => _derivative is not null;

    /// <summary>The same terms at a different extraction order or floor — the fill sets the floor
    /// from the mesh, and Tier 6 sweeps the order.</summary>
    public PlanarKernelTerms With(PlanarExtractionOrder order, double rhoFloor) =>
        new(_full, Inverse, Log, Constant, Linear, order, rhoFloor, SmallestImageDepth, _derivative);

    /// <summary>Does the assembled entry need the ∫∫dS core? (Always — but the constant is only
    /// EXTRACTED, i.e. moved out of the remainder, from order 1 up.)</summary>
    public bool ExtractsConstant => Order >= PlanarExtractionOrder.Constant;

    /// <summary>Does the assembled entry need the ∫∫r and ∫∫u·r cores?</summary>
    public bool ExtractsLinear => Order >= PlanarExtractionOrder.Linear;

    /// <summary>The full kernel — DCIM in production, a closed form in the reduction tests.</summary>
    public Complex Evaluate(double rhoM) => _full(rhoM);

    /// <summary>The analytic part that the closed-form inner integrals handle.</summary>
    public Complex Extracted(double rhoM)
    {
        Complex v = Inverse / rhoM + Log * Math.Log(rhoM);
        if (ExtractsConstant) v += Constant;
        if (ExtractsLinear)   v += Linear * rhoM;
        return v;
    }

    /// <summary>What is left for quadrature. Finite everywhere, including at ρ = 0.</summary>
    public Complex Remainder(double rhoM)
    {
        if (!(rhoM > RhoFloor)) return RemainderAtZero;
        return _full(rhoM) - Extracted(rhoM);
    }

    /// <summary>The ρ → 0 limit of <see cref="Remainder"/>, in closed form: the constant if it has
    /// not been extracted, and exactly zero once it has.</summary>
    public Complex RemainderAtZero => ExtractsConstant ? Complex.Zero : Constant;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Factories — one per kernel path, each stating which Green's function it decomposes
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The production path.</b> Decomposes <see cref="DcimModel.Evaluate"/> term by term.
    ///
    /// <list type="number">
    ///   <item><c>(1+QS)·e^{−jk₀ρ}/(4πρ)</c> → <c>(1+QS)/4π · [1/ρ − jk₀ − k₀²ρ/2 + …]</c>, giving
    ///     the 1/ρ, part of the constant, and the whole of the linear coefficient.</item>
    ///   <item><c>Res·(−j/4)H₀⁽²⁾(k_pρ)</c> → <c>Res·[−ln ρ/2π − j/4 − (ln(k_p/2)+γ)/2π] + O(ρ²ln ρ)</c>,
    ///     giving the ln ρ coefficient and part of the constant. <b>This is the term that gets
    ///     missed.</b></item>
    ///   <item><c>A_i·e^{−jk₀R_i}/(4πR_i)</c> → its value at ρ = 0, which is <c>A_i e^{−jk₀b_i}/(4πb_i)</c>;
    ///     it is even in ρ so it contributes nothing linear. Smooth only if b_i is not small compared
    ///     with a cell — R-fil-8, measured via <see cref="SmallestImageDepth"/>.</item>
    /// </list>
    /// </summary>
    public static PlanarKernelTerms FromDcim(DcimModel model,
                                             PlanarExtractionOrder order = PlanarExtractionOrder.Constant,
                                             double rhoFloor = 0.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        double k0 = model.K0;

        // ── term 1: the direct wave plus the zero-depth quasi-static image ────────────────────
        Complex cd = (1.0 + model.QuasiStatic) / (4.0 * Math.PI);
        Complex inverse  = cd;
        Complex constant = cd * (-Complex.ImaginaryOne * k0);
        Complex linear   = cd * (-(k0 * k0) / 2.0);

        // ── term 2: the surface waves — the LOGARITHM ─────────────────────────────────────────
        Complex log = Complex.Zero;
        foreach (var p in model.SurfaceWaves)
        {
            log      += p.Residue * (-1.0 / (2.0 * Math.PI));
            constant += p.Residue * (-0.25 * Complex.ImaginaryOne
                                     - (Complex.Log(p.KRho * 0.5) + Bessel.EulerGamma)
                                       / (2.0 * Math.PI));
        }

        // ── term 3: the fitted complex images — smooth, but check the depths (R-fil-8) ────────
        double smallestDepth = double.PositiveInfinity;
        foreach (var im in model.Images)
        {
            Complex r0 = Complex.Sqrt(im.Depth * im.Depth);
            if (r0.Real < 0) r0 = -r0;                       // the decaying branch, as Evaluate does
            constant += im.Amplitude * SommerfeldIntegral.FreeSpace(k0, r0);
            smallestDepth = Math.Min(smallestDepth, r0.Magnitude);
        }

        return new PlanarKernelTerms(model.Evaluate, inverse, log, constant, linear,
                                     order, rhoFloor, smallestDepth);
    }

    /// <summary>
    /// <b>L9c / M5 — the same decomposition for an INTERIOR fit, i.e. one of
    /// <see cref="Dcim.FitAtHeights"/>'s models at one height pairing.</b>
    ///
    /// <para><b>Which pieces are singular depends on the height pair, and that is the whole
    /// difference.</b> L8a's model has both points on one plane, so its direct term and its
    /// quasi-static image both sit at ρ = 0 and both contribute 1/ρ. An interior model carries its own
    /// Δ = |z − z′| and Σ_b, and a term whose depth is NON-ZERO is <b>bounded at ρ = 0</b> — its
    /// closed form is <c>C e^{−jk_m Δ}/(4πΔ)</c> and it belongs in the constant, not in the 1/ρ
    /// coefficient. So a CROSS-LEVEL entry has no 1/ρ at all.</para>
    ///
    /// <para><b>The logarithm does not go away, and that is the trap.</b> A surface wave is a property
    /// of the stack, not of the height pair: <c>H₀⁽²⁾(k_pρ)</c> carries its <c>ln ρ</c> whatever Δ is.
    /// An implementation that reasons "different levels, therefore smooth, therefore plain quadrature"
    /// pushes a logarithm through Gauss-Legendre — R-fil-3's original failure, one level up. <b>The
    /// mixed component adds a SECOND logarithm</b>, from the <c>1/k_ρ²</c> tail its asymptote inverts
    /// to: <c>ln[(q+√(q²+ρ²))/(p+√(p²+ρ²))]</c> is <c>−ln ρ</c> as ρ → 0 when p = Σ_b = 0, which is
    /// exactly the case a via foot produces.</para>
    /// </summary>
    public static PlanarKernelTerms FromDcimAtHeights(
        DcimModel model, PlanarExtractionOrder order = PlanarExtractionOrder.Constant,
        double rhoFloor = 0.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsAtHeights)
            throw new ArgumentException(
                "This model was fitted for the TOP HALF-SPACE, where the height pair is an exact shift " +
                "of one fit — use FromDcim. FromDcimAtHeights decomposes a Dcim.FitAtHeights model, " +
                "whose singular structure depends on its own Δ and Σ.", nameof(model));

        Complex km = model.ReferenceK;
        Complex inverse = Complex.Zero, log = Complex.Zero;
        Complex constant = Complex.Zero, linear = Complex.Zero;

        // ── the two extracted asymptotes: 1/ρ only where the depth is ZERO ────────────────────
        foreach (var (coefficient, depth) in model.AsymptotePieces)
        {
            if (coefficient == Complex.Zero) continue;
            if (depth <= 0)
            {
                // e^{−jk_m ρ}/4πρ = (1/4π)[1/ρ − jk_m − k_m²ρ/2 + …]
                Complex c = coefficient / (4.0 * Math.PI);
                inverse  += c;
                constant += c * (-Complex.ImaginaryOne * km);
                linear   += c * (-(km * km) / 2.0);
            }
            else
            {
                // Bounded at ρ = 0, and even in ρ, so it contributes nothing linear.
                constant += coefficient * SommerfeldIntegral.FreeSpace(km, depth);
            }
        }

        // ── the mixed component's LOG asymptote, when it has one ──────────────────────────────
        if (model.MixedLogCoefficient != Complex.Zero)
        {
            double p = model.MixedLogNearDepth, q = model.MixedLogFarDepth;
            Complex c = model.MixedLogCoefficient / (4.0 * Math.PI * Complex.ImaginaryOne);
            if (p <= 0)
            {
                log      += -c;                                   // ln[2q/ρ] = ln(2q) − ln ρ
                constant += c * Math.Log(2.0 * q);
            }
            else
            {
                constant += c * Math.Log((q + q) / (p + p));
            }
        }

        // ── the surface waves — the LOGARITHM, which the height pair does not touch ───────────
        foreach (var pw in model.SurfaceWaves)
        {
            log      += pw.Residue * (-1.0 / (2.0 * Math.PI));
            constant += pw.Residue * (-0.25 * Complex.ImaginaryOne
                                      - (Complex.Log(pw.KRho * 0.5) + Bessel.EulerGamma)
                                        / (2.0 * Math.PI));
        }

        // ── the fitted images — smooth, and R-fil-8's ratio measured the same way ─────────────
        double smallestDepth = double.PositiveInfinity;
        foreach (var im in model.Images)
        {
            Complex r0 = Complex.Sqrt(im.Depth * im.Depth);
            if (r0.Real < 0) r0 = -r0;
            constant += im.Amplitude * SommerfeldIntegral.FreeSpace(km, r0);
            smallestDepth = Math.Min(smallestDepth, r0.Magnitude);
        }

        return new PlanarKernelTerms(model.EvaluateAtHeights, inverse, log, constant, linear,
                                     order, rhoFloor, smallestDepth, model.DerivativeAtHeights);
    }

    /// <summary>
    /// <b>The via z-integral's half of <see cref="FromDcimAtHeights"/>: the same decomposition with
    /// the two asymptotes' STATIC parts <c>C/(4πR)</c> taken out.</b>
    ///
    /// <para>Those two pieces are the only ones whose ρ-dependence lives on the scale of the height
    /// separation itself, and they are exactly the ones whose z-integral is available in closed form
    /// (their coefficients do not depend on the heights and their depths are Δ and Σ_b). Removing them
    /// leaves a kernel that is genuinely smooth in z — the asymptotes' own wave correction
    /// <c>C(e^{−jk_mR}−1)/(4πR)</c>, which is O(k), plus the poles and the fitted images — so an
    /// ordinary Gauss rule in z reproduces it. See <c>ViaZIntegral</c>'s header for why a rule applied
    /// to the WHOLE kernel does not.</para>
    ///
    /// <para>The extraction coefficients follow term by term: a ZERO-depth asymptote loses its 1/ρ and
    /// keeps its constant and linear pieces (they come from the wave factor, which is still there); a
    /// non-zero-depth one keeps a constant of <c>C(e^{−jk_m d} − 1)/(4πd)</c> instead of
    /// <c>C e^{−jk_m d}/(4πd)</c>.</para>
    /// </summary>
    public static PlanarKernelTerms FromDcimAtHeightsMinusStaticAsymptotes(
        DcimModel model, PlanarExtractionOrder order = PlanarExtractionOrder.Constant,
        double rhoFloor = 0.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        var full = FromDcimAtHeights(model, order, rhoFloor);

        Complex inverse = full.Inverse, constant = full.Constant;
        var pieces = new List<(Complex C, double D)>();
        foreach (var (coefficient, depth) in model.AsymptotePieces)
        {
            if (coefficient == Complex.Zero) continue;
            pieces.Add((coefficient, depth));
            if (depth <= 0) inverse  -= coefficient / (4.0 * Math.PI);
            else            constant -= coefficient / (4.0 * Math.PI * depth);
        }
        if (pieces.Count == 0) return full;

        var removed = pieces.ToArray();
        Complex Reduced(double rho)
        {
            Complex v = model.EvaluateAtHeights(rho);
            foreach (var (c, d) in removed)
                v -= c / (4.0 * Math.PI * Math.Sqrt(rho * rho + d * d));
            return v;
        }

        return new PlanarKernelTerms(Reduced, inverse, full.Log, constant, full.Linear,
                                     order, rhoFloor, full.SmallestImageDepth);
    }

    /// <summary>
    /// <b>A weighted sum of decompositions — the z-quadrature, applied to the TERMS rather than to the
    /// matrix entry.</b>
    ///
    /// <para>Every extraction coefficient is linear in the kernel and so is the remainder, so a
    /// weighted sum of terms is the decomposition of the weighted sum of kernels — exactly, not
    /// approximately. That is what lets a via's z-integral cost n_z² FITS and zero extra cell-pair
    /// quadratures: the fill's O(N²) work sees one kernel, as it always has.</para>
    /// </summary>
    public static PlanarKernelTerms Combine(
        IReadOnlyList<(double Weight, PlanarKernelTerms Terms)> parts,
        PlanarExtractionOrder order, double rhoFloor)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) throw new ArgumentException("nothing to combine", nameof(parts));
        if (parts.Count == 1 && parts[0].Weight == 1.0) return parts[0].Terms.With(order, rhoFloor);

        Complex inverse = Complex.Zero, log = Complex.Zero;
        Complex constant = Complex.Zero, linear = Complex.Zero;
        double smallest = double.PositiveInfinity;
        var snapshot = parts.ToArray();

        foreach (var (w, t) in snapshot)
        {
            inverse  += w * t.Inverse;
            log      += w * t.Log;
            constant += w * t.Constant;
            linear   += w * t.Linear;
            smallest  = Math.Min(smallest, t.SmallestImageDepth);
        }

        Complex Full(double rho)
        {
            Complex v = Complex.Zero;
            foreach (var (w, t) in snapshot) v += w * t._full(rho);
            return v;
        }

        return new PlanarKernelTerms(Full, inverse, log, constant, linear,
                                     order, rhoFloor, smallest);
    }

    /// <summary>
    /// <b>R-fil-7's reduction, in closed form.</b> With εᵣ = 1 there is no slab: the Green's function
    /// is free space plus <b>one</b> image of amplitude <paramref name="imageAmplitude"/> at depth
    /// <paramref name="imageDepthM"/>, for BOTH kernels — a PEC ground plane gives −1 at depth 2h.
    /// No DCIM, no Prony, no Bessel function anywhere in this path, which is the whole point: Tier 3
    /// reproduces the entire fill from it and finds a sign, a 4π or a transposed index on the first
    /// run.
    /// </summary>
    public static PlanarKernelTerms FreeSpaceWithImage(double k0, Complex imageAmplitude, double imageDepthM,
                                                      PlanarExtractionOrder order = PlanarExtractionOrder.Constant,
                                                      double rhoFloor = 0.0)
    {
        Complex Full(double rho) =>
            SommerfeldIntegral.FreeSpace(k0, rho)
          + imageAmplitude * SommerfeldIntegral.FreeSpace(k0, Math.Sqrt(rho * rho + imageDepthM * imageDepthM));

        Complex cd = 1.0 / (4.0 * Math.PI);
        Complex constant = cd * (-Complex.ImaginaryOne * k0);
        if (imageDepthM > 0)
            constant += imageAmplitude * SommerfeldIntegral.FreeSpace(k0, imageDepthM);
        else
            throw new ArgumentOutOfRangeException(nameof(imageDepthM), imageDepthM,
                "A zero-depth image is not smooth — fold it into the 1/ρ coefficient instead.");

        return new PlanarKernelTerms(Full, cd, Complex.Zero, constant, cd * (-(k0 * k0) / 2.0),
                                     order, rhoFloor, imageDepthM);
    }

    /// <summary>
    /// Free space alone — no slab, no ground plane. The <c>h → ∞</c> limit, and Tier 5's isolated-plate
    /// fixture. Every extraction coefficient is exact here; there is nothing fitted anywhere.
    /// </summary>
    public static PlanarKernelTerms FreeSpace(double k0,
                                              PlanarExtractionOrder order = PlanarExtractionOrder.Linear,
                                              double rhoFloor = 0.0)
    {
        Complex cd = 1.0 / (4.0 * Math.PI);
        return new PlanarKernelTerms(rho => SommerfeldIntegral.FreeSpace(k0, rho),
                                     cd, Complex.Zero, cd * (-Complex.ImaginaryOne * k0),
                                     cd * (-(k0 * k0) / 2.0), order, rhoFloor);
    }

    /// <summary>
    /// <b>The ω → 0 branch (Tier 5).</b> <see cref="StaticGreens.ScalarPotential"/> is exact there and
    /// has no wave, no surface wave and no fitted image at all: the singular part is
    /// <c>(1+K)/(4πρ)</c> and everything else is the convergent image series, whose ρ = 0 value is
    /// the constant. There is no logarithm and no linear term — the series is even in ρ.
    /// </summary>
    public static PlanarKernelTerms StaticScalar(GroundedSlab slab, int maxImages = 4000,
                                                 double rhoFloor = 0.0)
    {
        Complex k = (1.0 - slab.EpsComplex) / (1.0 + slab.EpsComplex);
        Complex inverse = (1.0 + k) / (4.0 * Math.PI);

        // The ρ = 0 value of −(1+K)(1−K)Σ_{n≥1} K^{n−1}/(2nh) / 4π.
        Complex sum = Complex.Zero, kPow = Complex.One;
        for (int n = 1; n <= maxImages; n++)
        {
            Complex t = kPow / (2.0 * n * slab.HeightM);
            sum += t;
            kPow *= k;
            if (t.Magnitude < 1e-18 / slab.HeightM) break;
        }
        Complex constant = -(1.0 + k) * (1.0 - k) * sum / (4.0 * Math.PI);

        return new PlanarKernelTerms(rho => StaticGreens.ScalarPotential(slab, rho, maxImages),
                                     inverse, Complex.Zero, constant, Complex.Zero,
                                     PlanarExtractionOrder.Constant, rhoFloor, 2.0 * slab.HeightM);
    }

    /// <summary>
    /// The ω → 0 VECTOR kernel: free space plus one perfect negative image at depth 2h, for every εᵣ
    /// (L8a records why the dielectric does not enter the magnetostatic problem). Kept beside
    /// <see cref="StaticScalar"/> so the static harness can be built for both halves.
    /// </summary>
    public static PlanarKernelTerms StaticVector(GroundedSlab slab, double rhoFloor = 0.0) =>
        FreeSpaceWithImage(0.0, -Complex.One, 2.0 * slab.HeightM,
                           PlanarExtractionOrder.Constant, rhoFloor);
}

/// <summary>
/// A radial table of the smooth remainder, sampled once per frequency and interpolated on the fill
/// path.
///
/// <para><b>Why this exists, measured rather than assumed.</b> The remainder is a function of ρ
/// ALONE — that is D2's whole gift — but a direct evaluation costs a complex exponential, a Hankel
/// function per surface-wave pole and a complex square root per fitted image: ~2 µs. A single cell
/// pair's remainder integral wants tens of evaluations and a matrix has O(N²) pairs, so at the R17
/// ceiling direct evaluation alone would be tens of minutes per frequency. Interpolating a smooth
/// one-dimensional function is ~20 ns. <b>The error this trades away is measured</b> — PlanarFillTests
/// compares a tabulated fill against a directly-evaluated one entry by entry, and the setting can be
/// turned off outright.</para>
///
/// <para>Catmull-Rom through four neighbouring samples: O(h⁴), needs no derivatives, and — unlike a
/// spline — is local, so a table can be built in one pass and shared by every thread without
/// locking.</para>
/// </summary>
public sealed class RadialRemainderTable
{
    private readonly Complex[] _v;
    private readonly double   _h;
    private readonly double   _rhoMax;
    private readonly Complex  _atZero;

    public int SampleCount => _v.Length;
    public double Spacing  => _h;
    public double RhoMax   => _rhoMax;

    private RadialRemainderTable(Complex[] v, double h, double rhoMax, Complex atZero)
    { _v = v; _h = h; _rhoMax = rhoMax; _atZero = atZero; }

    /// <summary>
    /// Samples <paramref name="terms"/>'s remainder on [0, ρ_max] at spacing
    /// <paramref name="spacing"/>. The caller (<see cref="PlanarFill"/>) derives the spacing from the
    /// mesh's smallest cell and from λ_g, and reports it.
    /// </summary>
    /// <summary>
    /// <b>L9c — the same table over the mixed component's radial DERIVATIVE.</b> The ẑx̂ block
    /// evaluates <c>G′(ρ)</c> at every quadrature point of a 4-D integral, and <c>G′</c> is a loop
    /// over every fitted image plus a Hankel function per pole — the same per-point cost L8c built
    /// this table to avoid, arriving in the one block that has four nested loops rather than two.
    /// Measured: without it a 514-unknown two-level fill does not finish in two minutes; with it the
    /// whole fill is seconds. <b>Nothing else in the fill changes</b> — it is the same interpolation,
    /// on a different function, at the same spacing, and <c>UseRadialTable = false</c> still selects
    /// the direct path for the reference comparison.
    /// </summary>
    public static RadialRemainderTable BuildDerivative(PlanarKernelTerms terms, double rhoMax,
                                                       double spacing, int maxSamples = 1 << 22)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return BuildFrom(terms.Derivative, rhoMax, spacing, maxSamples);
    }

    /// <summary>The same table over any radial function — what the VIA's z-averaged mixed derivative
    /// needs, since it is a weighted sum of n_z models' derivatives plus one closed form and is
    /// therefore not any single <see cref="PlanarKernelTerms"/>'s own.</summary>
    public static RadialRemainderTable BuildFrom(Func<double, Complex> f, double rhoMax,
                                                 double spacing, int maxSamples = 1 << 22)
    {
        ArgumentNullException.ThrowIfNull(f);
        int n = (int)Math.Ceiling(rhoMax / spacing) + 4;
        if (n > maxSamples) { n = maxSamples; spacing = rhoMax / (n - 4); }
        n = Math.Max(n, 8);

        var v = new Complex[n];
        for (int i = 1; i < n; i++) v[i] = f(i * spacing);
        v[0] = Complex.Zero;                    // the ODD integrand's limit; never actually queried
        return new RadialRemainderTable(v, spacing, (n - 4) * spacing, Complex.Zero);
    }

    public static RadialRemainderTable Build(PlanarKernelTerms terms, double rhoMax, double spacing,
                                             int maxSamples = 1 << 22)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (!(rhoMax > 0))  throw new ArgumentOutOfRangeException(nameof(rhoMax));
        if (!(spacing > 0)) throw new ArgumentOutOfRangeException(nameof(spacing));

        int n = (int)Math.Ceiling(rhoMax / spacing) + 4;
        if (n > maxSamples) { n = maxSamples; spacing = rhoMax / (n - 4); }
        n = Math.Max(n, 8);

        var v = new Complex[n];
        for (int i = 0; i < n; i++) v[i] = terms.Remainder(i * spacing);
        v[0] = terms.RemainderAtZero;

        return new RadialRemainderTable(v, spacing, (n - 4) * spacing, terms.RemainderAtZero);
    }

    /// <summary>Interpolated remainder. Beyond the table it clamps to the last sample — the caller
    /// sizes the table to the mesh's own diagonal, so that only happens on a degenerate query.</summary>
    public Complex Evaluate(double rhoM)
    {
        if (!(rhoM > 0)) return _atZero;

        double t = rhoM / _h;
        int i = (int)t;
        if (i >= _v.Length - 2) return _v[^1];
        t -= i;

        // Catmull-Rom needs a sample either side; mirror at the origin, where the remainder is an
        // even function of the SEPARATION VECTOR and therefore even in ρ.
        Complex p0 = i == 0 ? _v[1] : _v[i - 1];
        Complex p1 = _v[i];
        Complex p2 = _v[i + 1];
        Complex p3 = _v[i + 2];

        Complex a = 2.0 * p1;
        Complex b = p2 - p0;
        Complex c = 2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3;
        Complex d = -p0 + 3.0 * p1 - 3.0 * p2 + p3;
        return 0.5 * (a + b * t + c * (t * t) + d * (t * t * t));
    }
}
