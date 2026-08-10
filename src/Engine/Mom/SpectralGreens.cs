using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// Which MPIE kernel component is wanted.
///
/// <para><b>L9c/D1 — this stayed a FLAT ENUM rather than becoming a (source direction, observer
/// direction) pair, and the reason is that all four members are scalar functions of
/// <c>(k_ρ, z, z′)</c> and nothing else.</b> The direction structure of the dyadic lives entirely in
/// how the FILL uses them — <see cref="MixedVectorPotential"/> enters through a
/// <c>∂/∂x</c> and <see cref="VectorPotential"/> through an identity — so putting a direction pair
/// in the kernel would push a `switch` on directions into `Dcim`, `SommerfeldIntegral` and
/// `SingularExtraction`, none of which has any use for one. Two of those switch on this enum in a
/// hot loop; a flat enum keeps that a jump table.</para>
///
/// <para><b>There are exactly FOUR, and which transmission line each comes from is not the obvious
/// answer.</b> See <see cref="LayeredSpectralGreens.VerticalKernel"/> for the derivation.</para>
/// </summary>
public enum GreensKernel
{
    /// <summary>G_A^xx — the transverse magnetic-vector-potential kernel, <b>TE line only</b> in
    /// formulation C: <c>V_i^h/(jωµ₀)</c>.</summary>
    VectorPotential,
    /// <summary>G_q — the scalar (electric) potential kernel; a true scalar in formulation C, and
    /// built from BOTH lines: <c>jωε₀(V_i^e − V_i^h)/k_ρ²</c>.</summary>
    ScalarPotential,
    /// <summary>
    /// <b>L9c — G_A^zz, the ẑẑ component a z-directed (via) current needs.</b> Built from the
    /// SERIES-source line responses <c>I_v</c>, not from <c>V_i</c>, and — despite a vertical
    /// current exciting no TE field at all — it carries a TE term, because the single scalar
    /// <see cref="ScalarPotential"/> it must stay consistent with is not TM-only.
    /// </summary>
    VerticalVectorPotential,
    /// <summary>
    /// <b>L9c — the ẑx̂/ẑŷ mixed component, as the scalar whose <c>∂/∂x</c> is the dyadic entry.</b>
    /// Built from the shunt-source line CURRENTS <c>I_i</c>. It is exactly zero over a bare ground
    /// plane and is what couples a via to the horizontal metal it lands on.
    /// </summary>
    MixedVectorPotential,
}

public enum SurfaceWavePolarization { Tm, Te }

/// <summary>
/// One surface-wave mode of the grounded slab. <see cref="KRho"/> is the actual (complex, if the
/// slab is lossy) pole location; <see cref="LosslessKRho"/> is the real root of the same dispersion
/// relation with tanδ dropped, which is where the complex search started and which is the
/// independently-checkable quantity of R-lgf-3.
/// </summary>
public sealed record SurfaceWaveMode(
    SurfaceWavePolarization Polarization,
    int                     Index,
    Complex                 KRho,
    double                  LosslessKRho)
{
    public string Name => $"{(Polarization == SurfaceWavePolarization.Tm ? "TM" : "TE")}{Index}";
}

/// <summary>
/// <b>M1 — the spectral-domain layered Green's function for a grounded slab, with one conductor
/// layer on the slab's top surface (D2).</b> No inverse transform lives here; that is
/// <see cref="SommerfeldIntegral"/> (the oracle) and <see cref="Dcim"/> (the product).
///
/// <para><b>R-lgf-1 — the formulation is MPIE, Michalski-Zheng FORMULATION C</b>, whose defining
/// property is that the vector-potential kernel is purely TE and the scalar-potential kernel is a
/// genuine scalar (no k_x/k_y dependence survives). That is what keeps the near-terms in L8c
/// integrable, and a reader six months from now needs to know which of the three formulations this
/// is, because they differ in how the vector and scalar parts split.</para>
///
/// <para><b>D4 — where the formulas came from, plainly.</b> Nothing here is transcribed. The kernel
/// pair below is <i>derived</i> from Maxwell's equations plus the standard transmission-line
/// analogy for a stratified medium, and the derivation is written out in this header so it can be
/// checked rather than trusted. The names attached to it in the literature — Michalski &amp; Zheng
/// for the MPIE formulations, Sommerfeld for the identity, Aksun for DCIM — are <b>attribution, not
/// provenance</b>: no paper was read to produce these lines and none is being paraphrased. What
/// makes the result trustworthy is not a citation but the exact reductions in
/// <c>LayeredGreensFunctionTests</c>, above all the εᵣ = 1 collapse to free space plus one image,
/// which no plausible-but-wrong kernel survives.</para>
///
/// <para><b>The derivation, in full.</b> Each spectral (k_x, k_y) component splits into TM (e) and
/// TE (h) partial waves obeying a 1-D transmission-line equation in z, with characteristic
/// impedances <c>Z^e = k_z/(ωε)</c> and <c>Z^h = ωµ/k_z</c>. Writing V_i^p(z, z′) for the voltage
/// at z due to a 1 A shunt current source at z′ on line p, the electric-field dyadic for a
/// horizontal electric dipole is</para>
/// <code>
///   G̃^EJ_xx = −(k_x² V_i^e + k_y² V_i^h)/k_ρ²        (and its yy, xy partners)
/// </code>
/// <para>Requiring the mixed-potential representation <c>E = −jωA − ∇φ</c>, with
/// <c>A = µ₀ G_A ⋆ J</c>, <c>φ = (1/ε₀) G_q ⋆ q</c> and <c>q = −(∇·J)/jω</c>, to reproduce that
/// dyadic gives — from the xx, yy and xy components independently, which is the consistency check
/// that the split is legitimate —</para>
/// <code>
///   G̃_A = V_i^h /(jωµ₀)                 G̃_q = jωε₀ (V_i^e − V_i^h)/k_ρ²
/// </code>
/// <para>Both reduce to <c>1/(2j k_z0)</c> in free space, i.e. to <c>e^{−jk₀R}/4πR</c> in space,
/// which fixes the normalisation used everywhere below. With the slab shorted by the ground plane
/// at z = 0, the line below the source is a shorted stub of length h, so
/// <c>Z_in^p = j Z_1^p tan(k_z1 h)</c> and</para>
/// <code>
///   G̃(k_ρ; z, z′) = [ e^{−j k_z0 |z−z′|} + Γ e^{−j k_z0 (z+z′−2h)} ] / (2j k_z0)
///   Γ^h = (j k_z0 T − k_z1)/(j k_z0 T + k_z1)          T = tan(k_z1 h)
///   Γ^e = (j k_z1 T − εᵣ k_z0)/(j k_z1 T + εᵣ k_z0)
///   Γ^q = Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h)
/// </code>
/// <para>The apparent 0/0 in Γ^q at k_ρ → 0 is removable and is removed <i>exactly</i>, not by a
/// guard: <c>Γ^e − Γ^h = 2 j T k_ρ²(εᵣ−1)/[(jk_z1T + εᵣk_z0)(jk_z0T + k_z1)]</c>, in which the k_ρ²
/// cancels algebraically. See <see cref="ReflectionScalar"/>.</para>
///
/// <para><b>R-lgf-5 — frequency dependence is total, and this is the single biggest change from
/// kernel A.</b> Kernel A's whole performance story (R-mom-11) is that [C], [C₀] and ∂L/∂n are
/// frequency-independent, enforced by a counter. <b>None of that survives here.</b> Every quantity
/// on this object is a function of ω; a <see cref="SpectralGreens"/> is constructed <i>per
/// frequency</i>, the DCIM fit is redone per frequency, and at L8c so will be the matrix fill and
/// the LU. There is deliberately no fill counter implying otherwise. §10.7's adaptive frequency
/// sampling is the eventual answer to the cost and is explicitly an L9 item.</para>
/// </summary>
public sealed class SpectralGreens
{
    private readonly Lazy<IReadOnlyList<SurfaceWaveMode>> _modes;

    public GroundedSlab Slab        { get; }
    public double       FrequencyHz { get; }

    /// <summary>k₀ — free space above the slab, real and lossless by construction.</summary>
    public double  K0 { get; }
    /// <summary>k₁ = k₀√(ε*) inside the slab; complex whenever tanδ ≠ 0.</summary>
    public Complex K1 { get; }
    /// <summary>ε* = εᵣ(1 − j tanδ) — R-mom-6, unchanged from kernel A.</summary>
    public Complex EpsR { get; }

    public SpectralGreens(GroundedSlab slab, double frequencyHz)
    {
        var ok = CanSolveAt(slab, frequencyHz);
        if (!ok.Ok) throw new ArgumentException(ok.Reason);

        Slab        = slab;
        FrequencyHz = frequencyHz;
        EpsR        = slab.EpsComplex;
        K0          = slab.FreeSpaceWavenumberAt(frequencyHz);
        K1          = K0 * Complex.Sqrt(EpsR);
        _modes      = new Lazy<IReadOnlyList<SurfaceWaveMode>>(FindSurfaceWaveModes);
    }

    /// <summary>
    /// <b>R-lgf-6 / R-mom-17 — the stated refusal.</b> Below <see cref="GroundedSlab.MinElectricalThickness"/>
    /// the wave corrections are numerically indistinguishable from the ω → 0 limit, and silently
    /// returning a fit outside its range is the failure mode this whole phase exists to prevent.
    /// The static branch is exact and is named in the refusal.
    /// </summary>
    public static EmSuitability CanSolveAt(GroundedSlab slab, double frequencyHz)
    {
        if (!(slab.HeightM > 0))
            return EmSuitability.No($"The grounded slab has height {slab.HeightM} m; it must be positive.");
        if (!(slab.Material.EpsR >= 1.0))
            return EmSuitability.No($"The slab's εᵣ is {slab.Material.EpsR}; a passive dielectric has εᵣ ≥ 1.");
        if (!(frequencyHz > 0))
            return EmSuitability.No($"Frequency {frequencyHz} Hz; the full-wave kernel needs f > 0.");

        double kh = slab.FreeSpaceWavenumberAt(frequencyHz) * slab.HeightM;
        if (kh < GroundedSlab.MinElectricalThickness)
            return EmSuitability.No(
                $"At {frequencyHz:G4} Hz the slab is k₀h = {kh:E2} thick, below the L8 full-wave " +
                $"kernel's floor of {GroundedSlab.MinElectricalThickness:E0}. This is the quasi-static " +
                $"regime, where the spectral function's structure is different and a fit tuned at " +
                $"microwave frequencies does not hold; use StaticGreens, which is exact there, or " +
                $"the quasi-static cross-section solve for a uniform cross-section.");

        return EmSuitability.Yes;
    }

    // -------------------------------------------------------------------------------------------
    // Vertical wavenumbers.
    //
    // The PROPER (top) Riemann sheet: Im k_z ≤ 0, so e^{−j k_z z} decays away from the interface
    // under the e^{jωt} convention this repository uses everywhere. Getting this branch backwards
    // produces a Green's function that GROWS with distance — which is loud, unlike most errors in
    // this kernel, but it is pinned by a test anyway.
    // -------------------------------------------------------------------------------------------

    public Complex Kz0(Complex kRho) => ProperRoot(K0 * (Complex)K0 - kRho * kRho);
    public Complex Kz1(Complex kRho) => ProperRoot(K1 * K1 - kRho * kRho);

    /// <summary>
    /// <b>R-lyr-2 — the ONE branch convention, chosen once and shared.</b> The general layered
    /// kernel (<see cref="LayeredSpectralGreens"/>) calls this same function rather than writing
    /// its own square root, so the two kernels cannot land on opposite sheets. A sign flip here is
    /// invisible in the propagating region and catastrophic in the evanescent one, which is most
    /// of the DCIM sampling path.
    /// </summary>
    internal static Complex ProperRoot(Complex squared)
    {
        var r = Complex.Sqrt(squared);
        return r.Imaginary > 0 ? -r : r;
    }

    // -------------------------------------------------------------------------------------------
    // Reflection coefficients.
    //
    // Written in the cross-multiplied form (numerator and denominator each scaled by k_z1·cos(k_z1h))
    // rather than as (j r T − 1)/(j r T + 1), because r = k_z1/(εᵣk_z0) blows up at the branch point
    // k_ρ = k₀ where k_z0 = 0. In this form Γ^e → +1 and Γ^h → −1 there, both finite and both
    // correct. Every expression is EVEN in k_z1, so the k_z1 branch choice cannot matter — which is
    // why there is no branch cut at k_ρ = k₁ and why the only branch point is k₀.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// k_z1 expressed from k_z0 rather than from k_ρ: <c>k_z1² = k₁² − k₀² + k_z0²</c>.
    ///
    /// <para>Both DCIM's sampling path and its branch-point Taylor expansion are naturally
    /// parameterised by k_z0, and routing them through k_ρ and back costs a square root that can
    /// land on the wrong branch — and, worse, cannot reach k_z0 &lt; 0 at all, which is exactly
    /// where the branch-point derivatives have to be evaluated. The reflection coefficients are
    /// even in k_z1, so this needs no branch decision of its own.</para>
    /// </summary>
    public Complex Kz1FromKz0(Complex kz0) => ProperRoot(K1 * K1 - K0 * (Complex)K0 + kz0 * kz0);

    /// <summary>Γ^h — the TE (transverse-electric) reflection coefficient of the shorted slab.</summary>
    public Complex ReflectionTe(Complex kRho) => TeFrom(Kz0(kRho), Kz1(kRho));

    /// <summary>Γ^e — the TM (transverse-magnetic) reflection coefficient of the shorted slab.</summary>
    public Complex ReflectionTm(Complex kRho) => TmFrom(Kz0(kRho), Kz1(kRho));

    /// <summary>The reflection coefficient as a function of k_z0 — see <see cref="Kz1FromKz0"/>.</summary>
    public Complex ReflectionAtKz0(GreensKernel kernel, Complex kz0)
    {
        Complex kz1 = Kz1FromKz0(kz0);
        return kernel switch
        {
            GreensKernel.VectorPotential => TeFrom(kz0, kz1),
            GreensKernel.ScalarPotential => ScalarFrom(kz0, kz1),
            _ => throw new ArgumentException(NoVerticalCurrent(kernel), nameof(kernel)),
        };
    }

    /// <summary>
    /// <b>R-mom-17 / R-via-6 — the one-layer kernel refuses the z-directed components BY NAME.</b>
    /// This is a refusal that was EARNED rather than written in advance: the two new components
    /// exist as of L9c, on <see cref="LayeredSpectralGreens"/>, and there is nothing for them to
    /// mean here. <see cref="GroundedSlab"/> is one slab with the metal on its top surface (D2), so
    /// there is no second level for a via to reach and no interior height for it to start from —
    /// <see cref="GroundedSlab.CanHost"/> refuses both, and this is the same limit said in the
    /// kernel rather than in the stackup.
    /// </summary>
    internal static string NoVerticalCurrent(GreensKernel kernel) =>
        $"{kernel} is a Z-DIRECTED-CURRENT component and the one-layer grounded-slab kernel does " +
        $"not carry one. GroundedSlab (L8's D2) places exactly one conductor layer on the slab's " +
        $"top surface, so there is no second level for a via to reach and no buried height for one " +
        $"to start from — a vertical current would have nowhere to flow. The vertical and mixed " +
        $"components arrive with the general stratified medium: build a LayerStack (LayerStack." +
        $"FromGroundedSlab reproduces this slab exactly) and use LayeredSpectralGreens, whose " +
        $"VerticalKernel and MixedKernel carry them.";

    private Complex TeFrom(Complex kz0, Complex kz1)
    {
        if (IsDoublyDegenerate(kz0, kz1)) return -Complex.One;
        Complex a = Complex.ImaginaryOne * kz0 * TanOverArgument(kz1);   // j·k_z0·tan(k_z1h)/k_z1
        return (a - 1.0) / (a + 1.0);
    }

    private Complex TmFrom(Complex kz0, Complex kz1)
    {
        if (IsDoublyDegenerate(kz0, kz1)) return -Complex.One;
        Complex a = Complex.ImaginaryOne * kz1 * kz1 * TanOverArgument(kz1);  // j·k_z1·tan(k_z1h)
        Complex b = EpsR * kz0;
        return (a - b) / (a + b);
    }

    /// <summary>
    /// Γ^q — the effective reflection coefficient of the SCALAR potential kernel. It is not Γ^e:
    /// formulation C's scalar kernel is built from the difference of the two transmission lines.
    ///
    /// <para>Evaluated through the algebraically cancelled identity
    /// <c>Γ^e − Γ^h = 2jT k_ρ²(εᵣ−1)/[(jk_z1T + εᵣk_z0)(jk_z0T + k_z1)]</c> so that the k₀²/k_ρ²
    /// prefactor meets an exact k_ρ² and nothing is ever divided by a small number. Writing it the
    /// obvious way instead works fine at k_ρ ~ k₀ and quietly loses every digit as k_ρ → 0 — which
    /// is precisely where the DCIM sampling path starts.</para>
    /// </summary>
    public Complex ReflectionScalar(Complex kRho) => ScalarFrom(Kz0(kRho), Kz1(kRho));

    private Complex ScalarFrom(Complex kz0, Complex kz1)
    {
        if (IsDoublyDegenerate(kz0, kz1)) return -Complex.One;

        Complex j = Complex.ImaginaryOne;
        Complex s = TanOverArgument(kz1);            // tan(k_z1h)/k_z1 — finite as k_z1 → 0

        Complex denTm = j * kz1 * kz1 * s + EpsR * kz0;   // vanishes on a TM surface-wave pole
        Complex denTe = j * kz0 * s + 1.0;                // vanishes on a TE surface-wave pole

        Complex gammaTm = (j * kz1 * kz1 * s - EpsR * kz0) / denTm;
        Complex diffOverKRhoSq = 2.0 * j * s * (EpsR - 1.0) / (denTm * denTe);

        return gammaTm - K0 * (Complex)K0 * diffOverKRhoSq;
    }

    public Complex Reflection(GreensKernel kernel, Complex kRho) => kernel switch
    {
        GreensKernel.VectorPotential => ReflectionTe(kRho),
        GreensKernel.ScalarPotential => ReflectionScalar(kRho),
        _ => throw new ArgumentException(NoVerticalCurrent(kernel), nameof(kernel)),
    };

    /// <summary>
    /// The k_ρ → ∞ (quasi-static) limit of the reflection coefficient. <b>DCIM's first extraction
    /// is this constant</b>, because a constant in the spectral domain is an image at zero depth
    /// and inverts in closed form; leave it in and the fitted remainder never decays.
    ///
    /// <para>G_A's is <b>0</b> — the TE line's impedance ratio tends to 1, so the slab becomes
    /// invisible. G_q's is <b>(1 − εᵣ)/(1 + εᵣ)</b>, the classic dielectric half-space image
    /// coefficient, which is the same K kernel A's dielectric-interface row carries (R-mom's
    /// <c>K = (ε₁ − ε₂)/(ε₁ + ε₂)</c>). The two kernels agreeing on that constant across two
    /// completely different formulations is not a coincidence and is worth noticing.</para>
    /// </summary>
    public Complex AsymptoticReflection(GreensKernel kernel) => kernel switch
    {
        GreensKernel.VectorPotential => Complex.Zero,
        GreensKernel.ScalarPotential => (1.0 - EpsR) / (1.0 + EpsR),
        _ => throw new ArgumentException(NoVerticalCurrent(kernel), nameof(kernel)),
    };

    // -------------------------------------------------------------------------------------------
    // The kernels themselves.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The free-space (direct) part <c>1/(2j k_z0)</c>, whose inverse transform is exactly
    /// <c>e^{−jk₀ρ}/4πρ</c>. It is always handled in closed form and never integrated or fitted.
    /// </summary>
    public Complex DirectKernel(Complex kRho) => 1.0 / (2.0 * Complex.ImaginaryOne * Kz0(kRho));

    /// <summary>The reflected part <c>Γ/(2j k_z0)</c> — everything the slab adds to free space.</summary>
    public Complex ReflectedKernel(GreensKernel kernel, Complex kRho) =>
        Reflection(kernel, kRho) * DirectKernel(kRho);

    /// <summary>The full spectral kernel at the metal plane, z = z′ = h.</summary>
    public Complex Kernel(GreensKernel kernel, Complex kRho) =>
        (1.0 + Reflection(kernel, kRho)) * DirectKernel(kRho);

    /// <summary>
    /// The general two-height form, for <b>z, z′ ≥ h</b> (both in the air above the slab).
    ///
    /// <para><b>Production never calls this</b> — D2 fixes z = z′ = h and that is the whole reason
    /// the problem is one-variable. It exists so Tier 0's reciprocity-in-heights check is asking a
    /// real question rather than a tautology, and so that the height dependence is written down
    /// once, correctly, for whoever lifts this kernel to two metal levels at L9.</para>
    ///
    /// <para>Reciprocity is <b>structural</b> here, at kernel A's own standard: the expression
    /// depends on the heights only through |z − z′| and z + z′, both symmetric, so G(z, z′) and
    /// G(z′, z) are bit-identical rather than equal to a tolerance.</para>
    /// </summary>
    public Complex KernelAtHeights(GreensKernel kernel, Complex kRho, double z, double zp)
    {
        double h = Slab.HeightM;
        if (z < h || zp < h)
            throw new ArgumentException(
                $"This is SpectralGreens — the ONE-LAYER kernel — and it is defined for z, z′ ≥ h = " +
                $"{h:G6} m (both above the slab); got z = {z:G6}, z′ = {zp:G6}. A source inside or " +
                $"below the medium is what LayeredSpectralGreens.KernelAtHeights does, at any pair " +
                $"of heights in any stratified stack; use that.");

        Complex kz0    = Kz0(kRho);
        Complex direct = Complex.Exp(-Complex.ImaginaryOne * kz0 * Math.Abs(z - zp));
        Complex image  = Complex.Exp(-Complex.ImaginaryOne * kz0 * (z + zp - 2 * h));
        return (direct + Reflection(kernel, kRho) * image) / (2.0 * Complex.ImaginaryOne * kz0);
    }

    // -------------------------------------------------------------------------------------------
    // Surface waves — R-lgf-3.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every surface-wave mode the slab supports at this frequency, TM₀ first. <b>A grounded slab
    /// always has at least one:</b> the TM₀ mode has no cutoff and propagates at every frequency
    /// however thin the slab, which is why "there is no surface wave here" is never a valid
    /// simplification.
    /// </summary>
    public IReadOnlyList<SurfaceWaveMode> SurfaceWaveModes => _modes.Value;

    /// <summary>
    /// The dispersion relation, as a residual that is zero on a pole. It is the DENOMINATOR of the
    /// corresponding reflection coefficient, multiplied through by cos(k_z1h) so that it is entire
    /// rather than carrying tan's poles — i.e. exactly the thing whose vanishing makes Γ infinite,
    /// which is what makes this an independent check rather than a restatement.
    /// </summary>
    public Complex DispersionResidual(SurfaceWavePolarization pol, Complex kRho)
    {
        Complex kz0 = Kz0(kRho), kz1 = Kz1(kRho), j = Complex.ImaginaryOne;
        Complex u   = kz1 * Slab.HeightM;
        return pol == SurfaceWavePolarization.Tm
            ? j * kz1 * Complex.Sin(u) + EpsR * kz0 * Complex.Cos(u)
            : j * kz0 * Complex.Sin(u) + kz1 * Complex.Cos(u);
    }

    /// <summary>
    /// How many modes of each polarisation the LOSSLESS slab supports, from the cutoff conditions
    /// alone — <c>U = k₀h√(εᵣ−1)</c>, TM_n exists for U &gt; nπ (so TM₀ always), TE_n for
    /// U &gt; (2n−1)π/2. Used to cross-check <see cref="SurfaceWaveModes"/>: the code must NOTICE a
    /// second mode appearing rather than silently fitting one pole to two, which is the classic way
    /// a DCIM implementation degrades as frequency rises.
    /// </summary>
    public (int Tm, int Te) ModeCountFromCutoffs()
    {
        double u = NormalisedThickness();
        int tm = 0;
        while (u > tm * Math.PI) tm++;                 // n = 0, 1, … while U > nπ
        int te = 0;
        while (u > (2 * (te + 1) - 1) * Math.PI / 2) te++;
        return (tm, te);
    }

    /// <summary>U = k₀h√(εᵣ′ − 1), the slab's normalised thickness — the only parameter the
    /// lossless dispersion relations depend on.</summary>
    public double NormalisedThickness() =>
        K0 * Slab.HeightM * Math.Sqrt(Math.Max(0, Slab.Material.EpsR - 1.0));

    private IReadOnlyList<SurfaceWaveMode> FindSurfaceWaveModes()
    {
        var (nTm, nTe) = ModeCountFromCutoffs();
        var list = new List<SurfaceWaveMode>();

        for (int n = 0; n < nTm; n++)
            Add(SurfaceWavePolarization.Tm, n);
        for (int n = 1; n <= nTe; n++)
            Add(SurfaceWavePolarization.Te, n);

        return list;

        void Add(SurfaceWavePolarization pol, int n)
        {
            double lossless = LosslessRoot(pol, n);
            if (double.IsNaN(lossless)) return;
            Complex refined = RefineComplex(pol, lossless);
            list.Add(new SurfaceWaveMode(pol, n, refined, lossless));
        }
    }

    /// <summary>
    /// The real root of the lossless dispersion relation, by bisection on u = k_z1h.
    ///
    /// <para>With (k_z1h)² + (αh)² = U² fixed by geometry, the transcendental equations are
    /// <c>u tan u = εᵣ√(U²−u²)</c> for TM_n on u ∈ (nπ, nπ+π/2) and
    /// <c>u cot u = −√(U²−u²)</c> for TE_n on u ∈ ((2n−1)π/2, nπ). Both brackets are guaranteed to
    /// change sign whenever the mode exists, so bisection cannot land on the wrong root — which
    /// matters, because a Newton iteration started carelessly here happily converges onto the
    /// NEIGHBOURING mode and the fit then carries two copies of one pole and none of the other.</para>
    /// </summary>
    private double LosslessRoot(SurfaceWavePolarization pol, int n)
    {
        double bigU = NormalisedThickness();
        if (bigU <= 0) return double.NaN;
        double epsR = Slab.Material.EpsR;

        double lo, hi;
        Func<double, double> f;
        const double pad = 1e-12;

        if (pol == SurfaceWavePolarization.Tm)
        {
            lo = n * Math.PI + pad;
            hi = Math.Min((n + 0.5) * Math.PI - pad, bigU);
            f  = u => u * Math.Tan(u) - epsR * Math.Sqrt(Math.Max(0, bigU * bigU - u * u));
        }
        else
        {
            lo = (2 * n - 1) * Math.PI / 2 + pad;
            hi = Math.Min(n * Math.PI - pad, bigU);
            f  = u => u / Math.Tan(u) + Math.Sqrt(Math.Max(0, bigU * bigU - u * u));
        }

        if (hi <= lo) return double.NaN;
        double flo = f(lo), fhi = f(hi);
        if (double.IsNaN(flo) || double.IsNaN(fhi) || flo * fhi > 0) return double.NaN;

        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi), fm = f(mid);
            if (flo * fm <= 0) { hi = mid; }
            else               { lo = mid; flo = fm; }
        }

        double uRoot  = 0.5 * (lo + hi);
        double alphaH = Math.Sqrt(Math.Max(0, bigU * bigU - uRoot * uRoot));
        double alpha  = alphaH / Slab.HeightM;
        return Math.Sqrt(K0 * K0 + alpha * alpha);      // k_ρ = √(k₀² + α²)
    }

    /// <summary>
    /// Move the lossless root to the actual complex pole by a secant iteration on the dispersion
    /// residual, in the variable w = k_ρ². Loss is a small perturbation, so this converges in a
    /// handful of steps; if it does not, the lossless root is returned unchanged rather than a
    /// wandering iterate, and the caller's own residual check will notice.
    /// </summary>
    private Complex RefineComplex(SurfaceWavePolarization pol, double losslessKRho)
    {
        Complex w0 = losslessKRho * (Complex)losslessKRho;
        Complex w1 = w0 * (1.0 + 1e-7) + 1e-30;
        Complex f0 = DispersionResidual(pol, Complex.Sqrt(w0));
        Complex f1 = DispersionResidual(pol, Complex.Sqrt(w1));

        for (int i = 0; i < 100 && (f1 - f0).Magnitude > 0; i++)
        {
            Complex w2 = w1 - f1 * (w1 - w0) / (f1 - f0);
            if (!double.IsFinite(w2.Real) || !double.IsFinite(w2.Imaginary)) break;
            w0 = w1; f0 = f1;
            w1 = w2; f1 = DispersionResidual(pol, Complex.Sqrt(w1));
            if ((w1 - w0).Magnitude <= 1e-15 * w1.Magnitude) break;
        }

        Complex root = Complex.Sqrt(w1);
        if (root.Real < 0) root = -root;
        return double.IsFinite(root.Real) && double.IsFinite(root.Imaginary) ? root : losslessKRho;
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// tan(z) saturated at ±j for large |Im z|, which is its exact limit and avoids overflowing
    /// sin/cos when the slab is many decay lengths thick at large k_ρ — the ordinary case out in
    /// the evanescent tail, not an edge case.
    /// </summary>
    internal static Complex StableTan(Complex z)
    {
        if (Math.Abs(z.Imaginary) > 30.0)
            return new Complex(0, Math.Sign(z.Imaginary));
        return Complex.Tan(z);
    }

    /// <summary>
    /// <c>tan(k_z1·h)/k_z1</c> — the combination every reflection coefficient actually contains,
    /// which is <b>even in k_z1</b> (so the k_z1 branch cannot matter, and k₀ is the only branch
    /// point) and <b>finite as k_z1 → 0</b> (so the branch point k_ρ = k₁ is not a numerical event
    /// either). Writing the coefficients in terms of tan alone divides by k_z1 somewhere and turns
    /// the perfectly ordinary point k_ρ = k₁ into a 0/0.
    /// </summary>
    private Complex TanOverArgument(Complex kz1)
    {
        double h = Slab.HeightM;
        Complex u = kz1 * h;
        if (u.Magnitude < 1e-4)
        {
            Complex u2 = u * u;                       // tan u / u = 1 + u²/3 + 2u⁴/15 + …
            return h * (1.0 + u2 / 3.0 + 2.0 * u2 * u2 / 15.0);
        }
        return StableTan(u) / kz1;
    }

    /// <summary>
    /// The one genuinely degenerate point: an index-matched slab (εᵣ = 1, i.e. no slab at all)
    /// observed exactly at the branch point k_ρ = k₀, where k_z0 and k_z1 vanish together and every
    /// algebraic form becomes 0/0. The limit is the bare ground plane, Γ = −e^{−2jk_z0h} = −1,
    /// which is exactly what εᵣ = 1 means.
    ///
    /// <para>No quadrature path reaches it — the substitutions in <see cref="SommerfeldIntegral"/>
    /// are chosen precisely to keep k_z0 out of the denominator — and it needs εᵣ = 1 exactly, so
    /// it is unreachable in production. It is handled anyway because a NaN sitting at a branch
    /// point is the kind of thing that surfaces two phases later inside something else.</para>
    /// </summary>
    private static bool IsDoublyDegenerate(Complex kz0, Complex kz1) =>
        kz0 == Complex.Zero && kz1 == Complex.Zero;
}

// ===============================================================================================
// L9a — the GENERAL layered kernel, alongside the one-layer one above.
//
// D5: nothing here supersedes SpectralGreens. The grounded slab is a special case of what this
// class computes, and the two are gated against each other at machine precision (Tier 1) rather
// than one being deleted in favour of the other. Collapsing them is a later, separate, MEASURED
// decision — the L7b-b precedent, where the general modal decomposition superseded L7b's fixed
// matrix only after the error against it was measured, and L7b's construction survived as an
// oracle.
// ===============================================================================================

/// <summary>
/// <b>M1/M2 — the spectral-domain Green's function for an ARBITRARY stratified medium, with
/// source and observer at arbitrary heights.</b> No inverse transform lives here; that is
/// <see cref="SommerfeldIntegral.EvaluateLayered"/> (the oracle).
///
/// <para><b>D1 — the medium is a transmission-line cascade, DERIVED, not transcribed.</b> Each
/// region is a section of two equivalent transmission lines — TM (superscript <c>e</c>) with
/// <c>Z^e = k_z/(ωε)</c>, TE (superscript <c>h</c>) with <c>Z^h = ωµ/k_z</c> — and the spectral
/// Green's function is the voltage <c>V_i(z|z′)</c> on those lines due to a 1 A shunt current
/// source at <c>z′</c>. Exactly the object <see cref="SpectralGreens"/> already builds for one
/// layer; the generalisation is that the terminating impedances looking up and down from the
/// source are now the input impedances of a cascade rather than closed forms. The mapping onto
/// the MPIE formulation-C kernels is unchanged and is <see cref="SpectralGreens"/>'s own
/// derivation:</para>
/// <code>
///   G̃_A = V_i^h /(jωµ₀)                 G̃_q = jωε₀ (V_i^e − V_i^h)/k_ρ²
/// </code>
/// <para><b>Nothing is transcribed from a paper.</b> L8a's rule carries over verbatim: names in
/// the literature are <i>attribution, not provenance</i>, and what makes the result trustworthy is
/// the oracle ladder in <c>GeneralLayeredMediumTests</c> — above all Tier 1's exact reduction to
/// the shipped one-layer kernel and Tier 2's bit-identical split-a-layer invariance, neither of
/// which any plausible-but-wrong cascade survives.</para>
///
/// <para><b>The cascade is written in REFLECTION COEFFICIENTS, and that is R-lyr-3 generalised.</b>
/// L8a's rule — write <c>tan(k_z h)/k_z</c>, never <c>tan</c> alone, because that combination is
/// even in k_z (so no interior <c>k_i</c> becomes a branch point) and finite as <c>k_z → 0</c> (so
/// <c>k_ρ = k_i</c> is not a numerical event) — has a direct N-layer analogue. Here every layer
/// enters as the Möbius step <c>Γ ← (r + Γ′e^{−2jk_z d})/(1 + rΓ′e^{−2jk_z d})</c>, whose value is
/// invariant under <c>k_z → −k_z</c> (it reduces algebraically to the tan form) and which is
/// finite at <c>k_z = 0</c> because <b>every interface coefficient is written cross-multiplied</b>
/// — <c>(ε_a k_zb − ε_b k_za)/(ε_a k_zb + ε_b k_za)</c>, never <c>(Z_b − Z_a)/(Z_b + Z_a)</c>,
/// which is 0/0 or ∞/∞ there. Under the proper branch every propagation factor also satisfies
/// <c>|e^{−2jk_z d}| ≤ 1</c>, so nothing overflows however thick the stack.</para>
///
/// <para><b>R-lyr-7 — everything is per frequency and nothing is cached across frequencies.</b> A
/// layered medium is <i>more</i> frequency-dependent than one slab, not less. The one cache on
/// this object (<see cref="TaylorCoefficients"/>) is keyed by height pair within a single
/// instance, which is per-frequency by construction.</para>
/// </summary>
public sealed class LayeredSpectralGreens
{
    /// <summary>k₀² fraction below which G̃_q switches from direct division to the Taylor path.
    /// It is also the radius of the extraction contour. Above it the plain division is already
    /// conditioned to ~2e-14 (measured); below it, it is not.</summary>
    internal const double SmallWFraction = 1e-2;

    /// <summary>k₀² fraction inside which <see cref="KzOfRegion"/> takes the PRINCIPAL square root
    /// rather than the proper sheet. Deliberately several times <see cref="SmallWFraction"/> so the
    /// extraction contour lies strictly inside it — a contour that grazes the boundary picks the
    /// proper-sheet sign flip back up on the samples that round outward, which is exactly the bug
    /// this margin exists to make impossible.</summary>
    private const double PrincipalDiskFraction = 4e-2;
    private  const int    ContourSamples = 48;
    private  const int    TaylorOrders   = 12;

    public LayerStack Stack       { get; }
    public double     FrequencyHz { get; }
    /// <summary>k₀ — free space, real and lossless by construction.</summary>
    public double     K0          { get; }
    public double     Omega       { get; }

    private readonly Complex[] _kSq;      // k_i² per region
    private readonly Complex[] _eps;      // ε*_r per region
    private readonly Complex[] _mu;       // µ_r per region
    private readonly double[]  _t;        // region thickness (+∞ for the two terminations)

    private readonly Dictionary<(int, double, double), Complex[]> _taylor = new();
    private readonly object _taylorGate = new();
    private readonly Lazy<SurfaceWaveSearchReport> _poles;

    /// <summary>
    /// Every surface-wave mode of this stack at this frequency, found once and reused. A grounded
    /// stack always has at least one (R-lgf-3 generalised), so "there is no surface wave here" is
    /// never a valid simplification — and a MISSED pole does not fail loudly, it produces a
    /// plausible kernel that is wrong at large ρ.
    /// </summary>
    public SurfaceWaveSearchReport SurfaceWaves => _poles.Value;

    public LayeredSpectralGreens(LayerStack stack, double frequencyHz)
    {
        var ok = CanSolveAt(stack, frequencyHz);
        if (!ok.Ok) throw new ArgumentException(ok.Reason);

        Stack       = stack;
        FrequencyHz = frequencyHz;
        Omega       = 2.0 * Math.PI * frequencyHz;
        K0          = Omega / EmConstants.C0;

        int r = stack.RegionCount;
        _kSq = new Complex[r];
        _eps = new Complex[r];
        _mu  = new Complex[r];
        _t   = new double[r];
        for (int i = 0; i < r; i++)
        {
            var m = stack.MaterialOfRegion(i);
            _eps[i] = m.EpsComplex;
            _mu[i]  = m.MuR;
            _kSq[i] = K0 * (Complex)K0 * _eps[i] * _mu[i];
            _t[i]   = stack.IsSemiInfinite(i)
                        ? double.PositiveInfinity
                        : stack.RegionTopZ(i) - stack.RegionBottomZ(i);
        }

        _poles = new Lazy<SurfaceWaveSearchReport>(() => SurfaceWavePoles.Find(Stack, FrequencyHz));
    }

    /// <summary>
    /// <b>R-lyr-8 / R-mom-17 — the stated refusal.</b> Below the electrical-thickness floor the
    /// wave corrections are numerically indistinguishable from the ω → 0 limit; the static branch
    /// is exact there and is named in the refusal.
    /// </summary>
    public static EmSuitability CanSolveAt(LayerStack stack, double frequencyHz)
    {
        if (stack is null) return EmSuitability.No("The layer stack is null.");
        if (!(frequencyHz > 0))
            return EmSuitability.No($"Frequency {frequencyHz} Hz; the full-wave kernel needs f > 0.");

        double k0 = 2.0 * Math.PI * frequencyHz / EmConstants.C0;
        if (stack.LayerCount > 0)
        {
            double kh = k0 * stack.TopZ;
            if (kh < GroundedSlab.MinElectricalThickness)
                return EmSuitability.No(
                    $"At {frequencyHz:G4} Hz the stack is k₀·H = {kh:E2} thick, below the full-wave " +
                    $"kernel's floor of {GroundedSlab.MinElectricalThickness:E0}. This is the " +
                    $"quasi-static regime, where the spectral function's structure is different; use " +
                    $"LayeredStaticGreens, which is exact there.");
        }
        return EmSuitability.Yes;
    }

    // -------------------------------------------------------------------------------------------
    // Vertical wavenumbers and line impedances.
    //
    // R-lyr-2: the branch is SpectralGreens.ProperRoot — literally the same function the one-layer
    // kernel uses, so the two cannot land on opposite sheets.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// k_zi as a function of <c>w = k_ρ²</c>, on the proper (Im ≤ 0) sheet — with ONE deliberate
    /// exception, inside the small disk <c>|w| ≤ SmallWFraction·k₀²</c>, where the PRINCIPAL square
    /// root is used instead.
    ///
    /// <para><b>This is not a fudge; the proper-sheet rule is the wrong function there and the
    /// difference is invisible on the real axis.</b> R-lyr-4's Taylor extraction integrates around a
    /// circle in the complex w plane, and that only makes sense if the integrand is ANALYTIC on it.
    /// <c>ProperRoot</c> is not: for a real k² it negates its own result exactly when
    /// <c>Im(w) &lt; 0</c>, so it flips sign on half of any circle centred at the origin — a
    /// discontinuity that has nothing to do with a branch cut (the cut of k_z0 lies on the real axis
    /// at <c>w &gt; k₀²</c>, far outside the disk) and everything to do with the proper-sheet
    /// CONDITION being a rule about physical decay rather than an analytic function.</para>
    ///
    /// <para>Inside the disk <c>Re(k_i² − w) &gt; 0</c> for every region (|w| is a few per cent of
    /// k₀², and every k_i² is at least k₀² in real part), so the principal root is exactly the
    /// analytic continuation of the physical root from w = 0 — and on the real axis inside the disk
    /// the two functions are IDENTICAL, which is the only place production ever evaluates. Getting
    /// this wrong is silent: the reflection coefficients themselves stay perfect (they were, to
    /// 2e-16, in the run that found this), and only Γ^q's small-k_ρ limit comes out ~5% wrong.</para>
    /// </summary>
    public Complex KzOfRegion(int region, Complex w) =>
        w.Magnitude <= PrincipalDiskFraction * K0 * K0
            ? Complex.Sqrt(_kSq[region] - w)
            : SpectralGreens.ProperRoot(_kSq[region] - w);

    /// <summary>k_z in the TOP termination's half-space — the variable every Sommerfeld path uses.</summary>
    public Complex Kz0(Complex kRho) => KzOfRegion(Stack.RegionCount - 1, kRho * kRho);

    /// <summary>
    /// k_i² of a region — region 0 is the bottom termination, <c>RegionCount − 1</c> the top.
    ///
    /// <para>Exposed because <b>D3's second branch point is a property of this array</b>: an
    /// open-below stack's Γ genuinely depends on <c>k_zb = √(k_b² − k_top² + k_z0²)</c>, whose branch
    /// points sit at <c>k_z0² = k_top² − k_b²</c>. A caller measuring where those are should read the
    /// wavenumbers the cascade actually uses rather than rebuilding them from the material.</para>
    /// </summary>
    public Complex RegionWavenumberSquared(int region) => _kSq[region];

    /// <summary>k² of the TOP half-space — the region every <c>k_z0</c> in this file refers to, and
    /// therefore the one that turns a k_z0 into a k_ρ: <c>k_ρ² = k_top² − k_z0²</c>.</summary>
    public Complex TopWavenumberSquared => _kSq[Stack.RegionCount - 1];

    private Complex Zc(SurfaceWavePolarization p, int region, Complex kz) =>
        p == SurfaceWavePolarization.Tm
            ? kz / (Omega * EmConstants.Eps0 * _eps[region])
            : Omega * EmConstants.Mu0 * _mu[region] / kz;

    /// <summary>Z_a / Z_b, cross-multiplied so neither a vanishing nor a diverging Z can produce a NaN.</summary>
    private Complex ZRatio(SurfaceWavePolarization p, int a, int b, Cascade c)
    {
        Complex ka = c.Kz[a], kb = c.Kz[b];
        return p == SurfaceWavePolarization.Tm
            ? _eps[b] * ka / (_eps[a] * kb)
            : _mu[a]  * kb / (_mu[b]  * ka);
    }

    /// <summary>
    /// Fresnel reflection at interface <paramref name="i"/> for a wave in the region ABOVE it
    /// incident downward on the region below. Cross-multiplied (R-lyr-3): finite at k_z = 0.
    /// </summary>
    private Complex FresnelDown(SurfaceWavePolarization p, int i, Cascade c)
    {
        if (i == 0 && Stack.IsWall(0))
            return Stack.Bottom.Kind == TerminationKind.Pec ? -Complex.One : Complex.One;

        int below = i, above = i + 1;
        Complex kb = c.Kz[below], ka = c.Kz[above];
        Complex num, den;
        if (p == SurfaceWavePolarization.Tm)
        {
            num = _eps[above] * kb - _eps[below] * ka;
            den = _eps[above] * kb + _eps[below] * ka;
        }
        else
        {
            num = _mu[below] * ka - _mu[above] * kb;
            den = _mu[below] * ka + _mu[above] * kb;
        }
        // Both vertical wavenumbers vanish only when the two media are identical — an invisible
        // interface, whose reflection is exactly zero. Returning 0/0 there would put a NaN in the
        // middle of an otherwise exact answer (the εᵣ = 1 reduction hits it at k_ρ = k₀ precisely).
        return den == Complex.Zero ? Complex.Zero : num / den;
    }

    /// <summary>Fresnel reflection at interface <paramref name="i"/> for a wave in the region BELOW
    /// it incident upward. Equal to −<see cref="FresnelDown"/> at a real interface; ±1 at a wall.</summary>
    private Complex FresnelUp(SurfaceWavePolarization p, int i, Cascade c)
    {
        int top = Stack.RegionCount - 1;
        if (i == top - 1 && Stack.IsWall(top))
            return Stack.Top.Kind == TerminationKind.Pec ? -Complex.One : Complex.One;
        return -FresnelDown(p, i, c);
    }

    // -------------------------------------------------------------------------------------------
    // D7 — the per-w cascade, built ONCE and shared by both polarisations and both ladders.
    //
    // Every k_zi and every e^{−2jk_z d} is polarisation-INDEPENDENT, and every consumer in this file
    // wants two polarisations at the same w: the scalar kernel differences them, and the reflection
    // route asks for Γ^e and Γ^h at the same w twice over. Before this the three-layer PCB stack
    // took ~24 complex square roots and 12 exponentials per sample where 5 and 3 suffice, because
    // FresnelDown, FresnelUp and RoundTrip each re-derived their own. L9a named this as "a
    // straightforward ~2×" and D7 asks for it to be MEASURED rather than assumed — the measurement
    // is in CLAUDE.md §L9b, and it is not 2×.
    //
    // The cache is ONE ENTRY DEEP on purpose: every caller walks w in a single pass and asks for
    // everything it wants at each step before moving on, so depth 1 hits every time. R-dcm-7 — it
    // lives inside one instance, which is inside one frequency, and nothing crosses frequencies.
    // -------------------------------------------------------------------------------------------

    /// <summary>The per-w geometry, plus each polarisation's ladders as they are asked for.</summary>
    private sealed class Cascade
    {
        public required Complex   W;
        public required Complex   KzTop;
        /// <summary>True when <see cref="KzTop"/> is the top region's own proper root of w, rather
        /// than a k_z0 supplied by the caller with its own sign (D1).</summary>
        public required bool      NaturalTop;
        public required Complex[] Kz;
        public required Complex[] Rt;
        public readonly Complex[]?[] Down = new Complex[2][];
        public readonly Complex[]?[] Up   = new Complex[2][];
    }

    private readonly object _cascadeGate = new();
    private Cascade? _cascade;

    private Cascade CascadeAt(Complex w, Complex kzTop, bool naturalTop)
    {
        var hit = _cascade;
        if (hit is not null && hit.W == w && hit.NaturalTop == naturalTop &&
            (naturalTop || hit.KzTop == kzTop))
            return hit;

        int n = Stack.RegionCount, top = n - 1;
        var kz = new Complex[n];
        var rt = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            // A wall has no medium and no vertical wavenumber; FresnelDown/FresnelUp short-circuit
            // there and Voltage refuses a source in it, so nothing reads these.
            if (Stack.IsWall(i)) continue;
            kz[i] = i == top && !naturalTop ? kzTop : KzOfRegion(i, w);
            rt[i] = double.IsInfinity(_t[i])
                        ? Complex.Zero
                        : Complex.Exp(-2.0 * Complex.ImaginaryOne * kz[i] * _t[i]);
        }
        return _cascade = new Cascade
        {
            W = w, KzTop = kz[top], NaturalTop = naturalTop, Kz = kz, Rt = rt,
        };
    }

    /// <summary>The DOWN ladder alone — <c>Down[i]</c> looks down from just above interface i.
    /// <paramref name="kzTop"/> is the top half-space's vertical wavenumber, supplied so D1's
    /// k_z0-parameterised entry point can hand over a NEGATIVE one.</summary>
    private Complex[] DownLadder(SurfaceWavePolarization p, Complex w, Complex kzTop, bool naturalTop)
    {
        lock (_cascadeGate)
        {
            var c = CascadeAt(w, kzTop, naturalTop);
            return c.Down[(int)p] ??= BuildDown(p, c);
        }
    }

    /// <summary>
    /// Both generalised-reflection ladders, built bottom-up and top-down.
    /// <c>Down[i]</c> looks DOWN from just above interface i; <c>Up[i]</c> looks UP from just below it.
    /// </summary>
    private (Cascade C, Complex[] Down, Complex[] Up) Ladders(SurfaceWavePolarization p, Complex w)
    {
        lock (_cascadeGate)
        {
            var c = CascadeAt(w, Complex.Zero, naturalTop: true);
            return (c, c.Down[(int)p] ??= BuildDown(p, c), c.Up[(int)p] ??= BuildUp(p, c));
        }
    }

    private Complex[] BuildDown(SurfaceWavePolarization p, Cascade c)
    {
        int nIf = Stack.InterfaceCount;
        var down = new Complex[nIf];
        down[0] = FresnelDown(p, 0, c);
        for (int i = 1; i < nIf; i++)
        {
            Complex prev = down[i - 1] * c.Rt[i];   // region i is the layer between interfaces i−1 and i
            Complex r    = FresnelDown(p, i, c);
            down[i] = (r + prev) / (1.0 + r * prev);
        }
        return down;
    }

    private Complex[] BuildUp(SurfaceWavePolarization p, Cascade c)
    {
        int nIf = Stack.InterfaceCount;
        var up = new Complex[nIf];
        up[nIf - 1] = FresnelUp(p, nIf - 1, c);
        for (int i = nIf - 2; i >= 0; i--)
        {
            Complex prev = up[i + 1] * c.Rt[i + 1];
            Complex r    = FresnelUp(p, i, c);
            up[i] = (r + prev) / (1.0 + r * prev);
        }
        return up;
    }

    // -------------------------------------------------------------------------------------------
    // The transmission-line voltage — the one object every kernel is built from.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>V_i^p(z | z′)</c> for a 1 A shunt current source at z′, as a function of <c>w = k_ρ²</c>.
    ///
    /// <para><b>R-lyr-5 — reciprocity in heights is STRUCTURAL for the same-region case.</b> When
    /// source and observer share a region the four-term form below depends on the heights only
    /// through <c>|z − z′|</c>, <c>z + z′</c> and the two interface distances — all symmetric — so
    /// <c>V(z,z′)</c> and <c>V(z′,z)</c> are <b>bit-identical</b>, not equal to a tolerance.</para>
    ///
    /// <para>When source and observer are in DIFFERENT regions the two orders take genuinely
    /// different computational paths (an upward amplitude chain versus a downward one), so
    /// reciprocity there is a real, independent check on the cascade rather than a tautology — and
    /// it is tested as one. Do not "fix" that by canonicalising the order; the agreement of two
    /// different paths is worth more than bit-identity obtained by never taking one of them.</para>
    /// </summary>
    public Complex Voltage(SurfaceWavePolarization p, Complex w, double z, double zp) =>
        LineResponse(p, w, z, zp, dual: false).Potential;

    /// <summary>
    /// <b>L9c — <c>I_i^p(z|z′)</c>, the line CURRENT for the same 1 A shunt current source.</b> The
    /// mixed component <see cref="MixedKernel"/> is built from it, and it is the quantity that
    /// carries the coupling between a horizontal current and A_z.
    /// </summary>
    public Complex Current(SurfaceWavePolarization p, Complex w, double z, double zp) =>
        LineResponse(p, w, z, zp, dual: false).Flux;

    /// <summary>
    /// <b>L9c — <c>I_v^p(z|z′)</c>, the line current for a 1 V SERIES voltage source at z′.</b> That
    /// is what a z-directed current element looks like on the TM line (see
    /// <see cref="VerticalKernel"/>'s derivation), and <c>G_A^zz</c> is built from it.
    /// </summary>
    public Complex SeriesCurrent(SurfaceWavePolarization p, Complex w, double z, double zp) =>
        LineResponse(p, w, z, zp, dual: true).Potential;

    /// <summary>
    /// <b>L9c — <c>V_v^p(z|z′)</c>, the line voltage for a 1 V series voltage source at z′.</b>
    ///
    /// <para>Computed on the DUAL line, so its agreement with the reciprocity relation
    /// <c>V_v(z|z′) = −I_i(z′|z)</c> is a genuine Tier-0 check between two different computations
    /// rather than a definition — the same standard R-lyr-5 holds cross-region reciprocity to.</para>
    /// </summary>
    public Complex SeriesVoltage(SurfaceWavePolarization p, Complex w, double z, double zp) =>
        LineResponse(p, w, z, zp, dual: true).Flux;

    /// <summary>
    /// <b>All FOUR transmission-line Green's functions, from one traversal of the cascade.</b>
    ///
    /// <para><c>dual = false</c> returns <c>(V_i, I_i)</c> — the voltage and current for a 1 A shunt
    /// current source at z′. <c>dual = true</c> returns <c>(I_v, V_v)</c> — the current and voltage
    /// for a 1 V series voltage source. <b>The second is the first on the DUAL line</b>, and the
    /// duality is exact and costs nothing: interchanging V↔I and Z↔Y turns a shunt current source
    /// into a series voltage source, negates every reflection coefficient (a short becomes an open),
    /// and leaves the Möbius ladder's composition alone — <c>Γ ← (r + Γ′ρ)/(1 + rΓ′ρ)</c> maps to
    /// <c>−Γ</c> under <c>r → −r, Γ′ → −Γ′</c>, so the dual ladder is literally the negative of the
    /// one already built, with no second cascade. The generalised transmission coefficient
    /// <c>2/((1+Γ) + (Z_a/Z_b)(1−Γ))</c> becomes the same expression with <c>Z_a/Z_b</c> replaced by
    /// its reciprocal, which is what a current transmission coefficient is.</para>
    ///
    /// <para><b>The four differ only in a sign pattern on the same four exponentials</b>, and that
    /// is worth writing down because it is what makes L9b's D6 result generalise. With
    /// <c>E1 = e^{−jk_z|Δ|}</c>, <c>E2 = Γ_t e^{−jk_z(2z_t−z−z′)}</c>,
    /// <c>E3 = Γ_b e^{−jk_z(z+z′−2z_b)}</c>, <c>E4 = Γ_tΓ_b e^{−jk_z(2d−|Δ|)}</c> and
    /// <c>s = sgn(z − z′)</c>, in the source's own region:</para>
    /// <code>
    ///   V_i = (Z_m/2D)[ E1 + E2 + E3 + E4 ]      I_i = (1/2D)[ sE1 − E2 + E3 − sE4 ]
    ///   I_v = (Y_m/2D)[ E1 − E2 − E3 + E4 ]      V_v = (1/2D)[ sE1 + E2 − E3 − sE4 ]
    /// </code>
    /// <para>so <b>L9b's four-family interior shift covers every one of them</b>: the same four
    /// exponents, four height-independent coefficients, different signs.</para>
    ///
    /// <para><b>R-via-1.</b> The <c>dual = false</c> path is byte-for-byte the arithmetic L9a
    /// shipped — the only change is that the reflection coefficients are read through a conditional
    /// negation, which is exact, and the prefactor through a conditional reciprocal.
    /// <c>GeneralLayeredMediumTests.R1_1</c> pins twelve dumped values at exact equality.</para>
    /// </summary>
    private (Complex Potential, Complex Flux) LineResponse(
        SurfaceWavePolarization p, Complex w, double z, double zp, bool dual)
    {
        int m = RegionOfOrThrow(zp, "source");
        int n = RegionOfOrThrow(z,  "observer");

        var (c, down, up) = Ladders(p, w);
        int top = Stack.RegionCount - 1;

        Complex kzm = c.Kz[m];
        Complex zm  = Zc(p, m, kzm);
        Complex pre = dual ? Complex.One / zm : zm;
        double  zbM = Stack.RegionBottomZ(m), ztM = Stack.RegionTopZ(m);

        Complex gb = m == 0   ? Complex.Zero : (dual ? -down[m - 1] : down[m - 1]);
        Complex gt = m == top ? Complex.Zero : (dual ? -up[m]       : up[m]);
        Complex denom = 1.0 - gb * gt * c.Rt[m];

        Complex ExpDecay(double distance) =>
            Complex.Exp(-Complex.ImaginaryOne * kzm * distance);

        if (n == m)
        {
            double d = Math.Abs(z - zp);
            Complex t1 = ExpDecay(d);
            Complex t2 = gt.Equals(Complex.Zero) ? Complex.Zero : gt * ExpDecay(2 * ztM - z - zp);
            Complex t3 = gb.Equals(Complex.Zero) ? Complex.Zero : gb * ExpDecay(z + zp - 2 * zbM);
            Complex t4 = gt.Equals(Complex.Zero) || gb.Equals(Complex.Zero)
                            ? Complex.Zero
                            : gt * gb * ExpDecay(2 * _t[m] - d);
            double s = Math.Sign(z - zp);
            return (pre * (t1 + t2 + t3 + t4) / (2.0 * denom),
                    (s * t1 - t2 + t3 - s * t4) / (2.0 * denom));
        }

        if (n > m)
        {
            // Up-going amplitude at the source, then a generalised-transmission chain upward.
            Complex rd = gb.Equals(Complex.Zero)
                            ? Complex.Zero
                            : gb * Complex.Exp(-2.0 * Complex.ImaginaryOne * kzm * (zp - zbM));
            Complex a = pre * (1.0 + rd) / (2.0 * denom) * ExpDecay(ztM - zp);

            for (int i = m; i <= n - 1; i++)
            {
                int above = i + 1;
                Complex kzA = c.Kz[above];
                Complex gaV = above == top ? Complex.Zero : up[above] * c.Rt[above];
                Complex ga  = dual ? -gaV : gaV;
                Complex ratio = ZRatio(p, i, above, c);
                if (dual) ratio = Complex.One / ratio;
                a = 2.0 * a / ((1.0 + ga) + ratio * (1.0 - ga));
                if (above < n) a *= Complex.Exp(-Complex.ImaginaryOne * kzA * _t[above]);
            }

            Complex kzn = c.Kz[n];
            double  zbN = Stack.RegionBottomZ(n), ztN = Stack.RegionTopZ(n);
            Complex gtNv = n == top ? Complex.Zero : up[n];
            Complex gtN  = dual ? -gtNv : gtNv;
            Complex forward = Complex.Exp(-Complex.ImaginaryOne * kzn * (z - zbN));
            Complex back = gtN.Equals(Complex.Zero)
                            ? Complex.Zero
                            : gtN * Complex.Exp(-Complex.ImaginaryOne * kzn * (2 * ztN - zbN - z));
            Complex zn = Zc(p, n, kzn);
            Complex preN = dual ? Complex.One / zn : zn;
            // `forward` is the up-going wave (current +1/Z_n), `back` the down-going one (−1/Z_n).
            return (a * (forward + back), a * (forward - back) / preN);
        }

        {
            // n < m: down-going amplitude at the source, then a chain downward.
            Complex ru = gt.Equals(Complex.Zero)
                            ? Complex.Zero
                            : gt * Complex.Exp(-2.0 * Complex.ImaginaryOne * kzm * (ztM - zp));
            Complex a = pre * (1.0 + ru) / (2.0 * denom) * ExpDecay(zp - zbM);

            for (int i = m - 1; i >= n; i--)
            {
                int below = i;
                Complex kzB = c.Kz[below];
                Complex gdV = below == 0 ? Complex.Zero : down[below - 1] * c.Rt[below];
                Complex gd  = dual ? -gdV : gdV;
                Complex ratio = ZRatio(p, i + 1, below, c);
                if (dual) ratio = Complex.One / ratio;
                a = 2.0 * a / ((1.0 + gd) + ratio * (1.0 - gd));
                if (below > n) a *= Complex.Exp(-Complex.ImaginaryOne * kzB * _t[below]);
            }

            Complex kzn = c.Kz[n];
            double  zbN = Stack.RegionBottomZ(n), ztN = Stack.RegionTopZ(n);
            Complex gbNv = n == 0 ? Complex.Zero : down[n - 1];
            Complex gbN  = dual ? -gbNv : gbNv;
            Complex forward = Complex.Exp(-Complex.ImaginaryOne * kzn * (ztN - z));
            Complex back = gbN.Equals(Complex.Zero)
                            ? Complex.Zero
                            : gbN * Complex.Exp(-Complex.ImaginaryOne * kzn * (z + ztN - 2 * zbN));
            Complex zn = Zc(p, n, kzn);
            Complex preN = dual ? Complex.One / zn : zn;
            // Here `forward` is the DOWN-going wave (current −1/Z_n) and `back` the up-going one.
            return (a * (forward + back), a * (back - forward) / preN);
        }
    }

    private int RegionOfOrThrow(double z, string what)
    {
        if (!double.IsFinite(z))
            throw new ArgumentException($"The {what} height is {z}; it must be finite.");
        int r = Stack.RegionOf(z);
        if (Stack.IsWall(r))
            throw new ArgumentException(
                $"The {what} at z = {z:G6} m lies inside the stack's {(r == 0 ? "bottom" : "top")} " +
                $"{(r == 0 ? Stack.Bottom : Stack.Top)} termination, which is a solid wall, not a " +
                $"medium. Place it inside a layer or in an open half-space.");
        return r;
    }

    // -------------------------------------------------------------------------------------------
    // The kernels.
    // -------------------------------------------------------------------------------------------

    /// <summary>Any of the four MPIE kernel components at arbitrary source and observer heights.</summary>
    public Complex KernelAtHeights(GreensKernel kernel, Complex kRho, double z, double zp) => kernel switch
    {
        GreensKernel.VectorPotential         => VectorKernel(kRho * kRho, z, zp),
        GreensKernel.ScalarPotential         => ScalarKernel(kRho * kRho, z, zp),
        GreensKernel.VerticalVectorPotential => VerticalKernel(kRho * kRho, z, zp),
        GreensKernel.MixedVectorPotential    => MixedKernel(kRho * kRho, z, zp),
        _ => throw new ArgumentOutOfRangeException(nameof(kernel)),
    };

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // L9c / D1 — THE Z-DIRECTED COMPONENTS, DERIVED HERE, EXACTLY AS L8a DERIVED ITS PAIR.
    //
    // Nothing below is transcribed. R-lgf-1's rule stands: names in the literature are attribution,
    // not provenance, and what makes this trustworthy is the εᵣ = 1 reduction at the bottom of this
    // comment — which no plausible-but-wrong dyadic survives, and which has a DIFFERENT exact answer
    // for the vertical components than for the horizontal ones.
    //
    // ── 1. HOW A CURRENT DRIVES THE TWO LINES ─────────────────────────────────────────────────
    //
    // With ∇_t → −j k_ρ û, v̂ = ẑ × û, V^e = E_u, I^e = H_v, V^h = E_v, I^h = −H_u, Maxwell's two
    // curl equations separate into two transmission lines plus one algebraic relation:
    //
    //     ∂_z V^e = −j k_z Z^e I^e + v^e,   v^e = +k_ρ Ĵ_z /(ωε)      Z^e = k_z/(ωε)
    //     ∂_z I^e = −j k_z Y^e V^e + i^e,   i^e = −Ĵ_u
    //     ∂_z V^h = −j k_z Z^h I^h,                                    Z^h = ωµ/k_z
    //     ∂_z I^h = −j k_z Y^h V^h + i^h,   i^h = −Ĵ_v
    //     E_z     = −(j k_ρ I^e + Ĵ_z δ)/(jωε)                         H_z = k_ρ V^h/(ωµ)
    //
    // Two things fall straight out and are the checks D1 asks for.
    //
    //   • A Z-DIRECTED CURRENT EXCITES TM ONLY. H_z ∝ V^h and the TE line has no source term at
    //     all for Ĵ_z, so the TE field of a vertical dipole is identically zero. But it enters the
    //     TM line as a SERIES VOLTAGE SOURCE, not a shunt current one — which is why the vertical
    //     components below are built from I_v and V_v and NOT from V_i. "The horizontal kernel with
    //     the TE line swapped for the TM one" is the wrong obvious answer twice over: wrong source
    //     type, and (see below) not TM-only anyway.
    //
    //   • RECIPROCITY, in the form this file can test: V_v(z|z′) = −I_i(z′|z), I_v(z|z′) = I_v(z′|z)
    //     and V_i(z|z′) = V_i(z′|z). LineResponse computes the left-hand sides on the dual line and
    //     the right-hand sides on the primal one, so their agreement is a real check.
    //
    // ── 2. MATCHING THE MIXED-POTENTIAL FORM ──────────────────────────────────────────────────
    //
    // Require E = −jωA − ∇φ, with A = µ₀ Ḡ_A ⋆ J, φ = (1/ε₀) G_q ⋆ q and q = −∇·J/(jω), to
    // reproduce those fields. Five components of E (E_u, E_v, E_z from a horizontal source; E_u, E_z
    // from a vertical one) fix five kernels, and the system is consistent — E_v from a vertical
    // source is zero on both sides, which is the one free consistency check.
    //
    //   E_v, HED  →  G_A^xx = V_i^h/(jωµ₀)                            (L8a's, unchanged)
    //   E_u, HED  →  G_q    = jωε₀(V_i^e − V_i^h)/k_ρ²                (L8a's, unchanged)
    //   E_z, HED  →  G_A^zu = (µ_n/µ₀)(I_i^h − I_i^e)/(j k_ρ)
    //   E_u, VED  →  G_A^uz = (µ_m/µ₀)(V_v^h − V_v^e)/(j k_ρ) = −G_A^zu with the heights swapped
    //   E_z, VED  →  G_A^zz = [(µ_n/ε_m + µ_m/ε_n) I_v^e
    //                          + (ω²µ_nµ_m/k_ρ²)(I_v^h − I_v^e)] / (jωµ₀)
    //
    // The E_z rows are the ones with real content, because ∇φ has a ∂_z and a vertical source's
    // charge is a δ′. Both were reduced with the line equations themselves (∂_z V_i^p = −jk_zZ^p I_i^p
    // and ∂_{z′}V_i^p = +jk_z′Z^{p′}V_v^p, the latter from reciprocity), which is what turns
    // ∂_z G_q and ∂_z∂_{z′}G_q into ordinary line quantities and leaves no derivative anywhere in
    // the code. **The δ(z−z′) in E_z and the δ from ∂_z∂_{z′}G_q cancel exactly** — algebraically,
    // not to a tolerance — and that cancellation is the strongest algebraic check in the derivation.
    //
    // ── 3. WHERE THE FORMULATION'S ASYMMETRY LANDS ────────────────────────────────────────────
    //
    // TWO facts here are the opposite of what the "TM only" argument suggests, and both are
    // consequences of insisting on ONE scalar kernel G_q for every source direction:
    //
    //   • G_A^zz CARRIES A TE TERM. The FIELD of a vertical dipole is TM-only; G_A^zz is not the
    //     field, it is what is left after subtracting −∇φ, and φ is built from a G_q that is a TM−TE
    //     difference. The TE part of G_A^zz is exactly the TE content of ∇φ, put back. Choosing
    //     instead to make G_A^zz TM-only is a different, equally valid formulation — it needs a
    //     SECOND scalar kernel for vertical sources, which would break L8c's D4 (one per-cell
    //     potential matrix P for every pair) and with it the exact charge bookkeeping the via basis
    //     depends on. One scalar kernel is the choice; the TE term in G_A^zz is its price.
    //
    //   • THE DYADIC HAS BOTH MIXED COMPONENTS, and they are not equal — G_A^uz(z,z′) = −G_A^zu(z′,z).
    //     A formulation with only ẑx̂ and no x̂ẑ would give a Galerkin matrix with an entry for
    //     (vertical m, horizontal n) and none for the transpose, i.e. no reciprocity. The extra
    //     minus sign is exactly compensated by the ẑx̂ component being ODD in x − x′ (it enters
    //     through ∂/∂x), so Z stays symmetric. That is a structural test, not an observed one.
    //
    // ── 4. THE k_ρ → 0 STRUCTURE, AND WHY IT IS THE SAME PROBLEM AS G_q's ─────────────────────
    //
    // G_A^zu and G_A^zz both carry a difference of the two lines against a 1/k_ρ (resp. 1/k_ρ²)
    // prefactor, and both differences vanish identically at k_ρ = 0 because the two lines are then
    // the same network. So both go through DifferenceOverW's contour extraction, unchanged — see
    // LineDifference. Neither needs a new mechanism and neither may use a plain division.
    //
    // The mixed component is exposed with the 1/k_ρ² already folded in, i.e. as the SCALAR whose
    // ∂/∂x is the dyadic entry: spectrally the ẑx̂ entry is k_x·MixedKernel, and multiplication by
    // k_x is j∂/∂x in space. That keeps every member of GreensKernel a function of (k_ρ, z, z′)
    // alone, which is what lets Dcim and SommerfeldIntegral treat all four identically.
    //
    // ── 5. THE εᵣ = 1 REDUCTION — TIER 1, AND THE IMAGE SIGN FLIPS ────────────────────────────
    //
    // Over a bare PEC ground plane with no dielectric at all (Δ = |z−z′|, Σ = z+z′, G̃₀(d) =
    // e^{−jk_z d}/(2jk_z)), every one of the four collapses to free space plus ONE image, exactly:
    //
    //     G_A^xx = G̃₀(Δ) − G̃₀(Σ)      G_q    = G̃₀(Δ) − G̃₀(Σ)      ← NEGATIVE image
    //     G_A^zz = G̃₀(Δ) + G̃₀(Σ)                                    ← POSITIVE image
    //     G_A^zu ≡ 0
    //
    // A PEC is a SHORT on both lines, so the voltage reflection is −1 and the CURRENT reflection is
    // +1 — and the vertical components are built from currents. That is the whole of the image-sign
    // flip, and it is why getting it backwards produces a smooth, plausible, completely wrong
    // structure: nothing else in the ladder notices. The mixed component vanishing is the third
    // exact statement — over a bare ground plane image theory leaves a pure free-space problem, and
    // the free-space dyadic is diagonal, so a horizontal current makes no A_z.
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>G̃_A^zz — the ẑẑ component, which is what a via's own self-coupling is built from.</b>
    ///
    /// <para><c>jωµ₀ G_A^zz = (µ_n/ε_m + µ_m/ε_n) I_v^e + (ω²µ_nµ_m/k_ρ²)(I_v^h − I_v^e)</c>, with
    /// m the source's region and n the observer's. See the block comment above for the derivation,
    /// for why it is built from the SERIES-source current <c>I_v</c>, and for why the TE term is
    /// there at all when a vertical dipole radiates no TE field.</para>
    ///
    /// <para>Symmetric in the two heights by inspection (<c>I_v</c> is, and the coefficient is), so
    /// reciprocity is structural — bit-identical in the same-region case, and a genuine two-path
    /// check across regions, exactly as R-lyr-5 has it for the horizontal components.</para>
    ///
    /// <para>In free space it reduces to <c>1/(2jk_z)</c>, i.e. to <c>e^{−jkR}/4πR</c> — the same
    /// normalisation as every other kernel in this file, which is what makes <c>A = µ₀G_A^zz ⋆ J_z</c>
    /// the ordinary vector potential when there is no stack.</para>
    /// </summary>
    public Complex VerticalKernel(Complex w, double z, double zp)
    {
        int m = RegionOfOrThrow(zp, "source");
        int n = RegionOfOrThrow(z,  "observer");

        Complex epsM = EmConstants.Eps0 * _eps[m], epsN = EmConstants.Eps0 * _eps[n];
        Complex muM  = EmConstants.Mu0  * _mu[m],  muN  = EmConstants.Mu0  * _mu[n];

        Complex ive  = SeriesCurrent(SurfaceWavePolarization.Tm, w, z, zp);
        // (I_v^h − I_v^e)/k_ρ² — the SAME removable 0/0 G_q has, taken the same stable way.
        Complex diff = -DifferenceOverW(LineDifference.SeriesCurrent, w, z, zp);

        Complex numerator = (muN / epsM + muM / epsN) * ive
                          + Omega * Omega * muN * muM * diff;
        return numerator / (Complex.ImaginaryOne * Omega * EmConstants.Mu0);
    }

    /// <summary>
    /// <b>The ẑx̂ / ẑŷ mixed component, as the SCALAR whose <c>∂/∂x</c> is the dyadic entry.</b>
    ///
    /// <para><c>MixedKernel = (µ_n/µ₀)(I_i^h − I_i^e)/(j k_ρ²)</c>. The spectral ẑx̂ entry is
    /// <c>k_x ·</c> this, and <c>k_x ↔ j ∂/∂x</c>, so in space the dyadic component is
    /// <c>j ∂G/∂x = j G′(ρ)(x − x′)/ρ</c> — <b>odd in x − x′</b>, which is what makes the Galerkin
    /// matrix symmetric despite <c>G_A^uz = −G_A^zu</c> with the heights swapped.</para>
    ///
    /// <para><b>It is identically zero over a bare ground plane</b> (both lines then have the same
    /// current reflection, so the difference vanishes) and identically zero in free space. It is
    /// non-zero only because a dielectric interface reflects the two polarisations differently —
    /// which is precisely the coupling between a horizontal current and A_z that a via foot needs.</para>
    /// </summary>
    /// <summary>
    /// <b>L9c / D3 — the k_ρ → ∞ decomposition of one kernel component at one height pair, which is
    /// what an inverse transform for an INTERIOR source has to extract before it can converge.</b>
    ///
    /// <para>L8a's partition (a direct term plus one quasi-static constant) is <b>not enough here,
    /// and that is a statement about geometry rather than about accuracy.</b> Everything a source
    /// radiates decays like <c>e^{−k_ρ x}</c> with x the distance the term travels, so a term stops
    /// decaying exactly when x = 0. A source in the open half-space has one such term — the direct
    /// one, at <c>Δ = |z−z′|</c> — because its image is a whole substrate thickness away. <b>A source
    /// sitting ON an interior interface, which is precisely where metal goes, has TWO</b>: the direct
    /// term AND the down-reflection, because <c>Σ_b = z + z′ − 2z_b</c> is zero when both points are
    /// at the bottom of their own region. Leave the second one in and the tail never converges.</para>
    ///
    /// <para><b>The two coefficients are the k_ρ → ∞ limits of the cascade</b>, in which every
    /// round-trip factor <c>e^{−2jk_z d}</c> has died, so the generalised bottom reflection collapses
    /// to the single local Fresnel coefficient at the interface below the source — <c>R_e</c> for TM,
    /// <c>R_h</c> for TE (zero for a non-magnetic dielectric interface, ∓1 at a PEC/PMC wall). Per
    /// component, with <c>E1 = e^{−jk_zmΔ}</c>, <c>E3 = e^{−jk_zmΣ_b}</c>:</para>
    /// <code>
    ///   G_A^xx  →  (µ_m/µ₀)[E1 + R_h E3] / (2j k_zm)
    ///   G_q     →  (ε₀/ε_m)[E1 + R_e E3] / (2j k_zm)
    ///   G_A^zz  →  (µ_m/µ₀)[E1 + (R_h − 2R_e) E3] / (2j k_zm)
    ///   mixed   →  (µ_m/µ₀)(R_h − R_e) E3 / (2j k_ρ²)          ← a DIFFERENT shape (see below)
    /// </code>
    /// <para>The <c>G_A^zz</c> row is worth reading twice: over a PEC, <c>R_h = R_e = −1</c> gives
    /// <c>R_h − 2R_e = +1</c>, i.e. free space PLUS one image — the sign flip Tier 1 gates on, arriving
    /// from the asymptotics as well as from the exact reduction.</para>
    ///
    /// <para><b>The mixed component's asymptote has a different SHAPE and needs its own closed form.</b>
    /// Its direct terms cancel outright (<c>I_i</c>'s direct term is <c>sgn(z−z′)/2</c> on both lines
    /// and the difference kills it), so it has no <c>1/k_z</c> piece at all — what survives is
    /// <c>1/k_ρ²</c>, which is not a Sommerfeld-identity exponential. <see cref="IsMixedForm"/> says
    /// so, and the transform extracts it with the log form instead.</para>
    ///
    /// <para><b>Cross-region needs no extraction at all</b>, and that is the one cheerful thing in
    /// this milestone: a transmission chain carries a factor <c>e^{−jk_z t}</c> for the full thickness
    /// of every region it crosses, so the whole kernel decays like <c>e^{−k_ρ t}</c> and both
    /// coefficients are zero. The cost lands in the tail's LENGTH rather than in its convergence —
    /// a 3 µm spacer means integrating out to k_ρ ~ 1/3 µm.</para>
    /// </summary>
    /// <param name="DirectCoefficient">Coefficient of <c>e^{−jk_zmΔ}/(2jk_zm)</c>, or of nothing when
    /// <see cref="IsMixedForm"/>.</param>
    /// <param name="ImageCoefficient">Coefficient of <c>e^{−jk_zmΣ}/(2jk_zm)</c>, or of
    /// <c>e^{−k_ρΣ}/(2jk_ρ²)</c> when <see cref="IsMixedForm"/>.</param>
    /// <param name="ReferenceWavenumber">The SOURCE region's own k, on the proper sheet. Complex
    /// whenever that region is lossy — which is why <see cref="SommerfeldIntegral.FreeSpace"/> had to
    /// widen from a <c>double</c> wavenumber.</param>
    /// <param name="DirectDepth">Δ = |z − z′|.</param>
    /// <param name="ImageDepth">Σ_b = z + z′ − 2z_b(m), the distance the down-reflection travels.</param>
    /// <param name="IsMixedForm">The mixed component's <c>1/k_ρ²</c> shape rather than the
    /// <c>1/k_z</c> one.</param>
    public readonly record struct InteriorAsymptote(
        Complex DirectCoefficient,
        Complex ImageCoefficient,
        Complex ReferenceWavenumber,
        double  DirectDepth,
        double  ImageDepth,
        bool    IsMixedForm);

    /// <inheritdoc cref="InteriorAsymptote"/>
    public InteriorAsymptote AsymptoticAtHeights(GreensKernel kernel, double z, double zp)
    {
        int m = RegionOfOrThrow(zp, "source");
        int n = RegionOfOrThrow(z,  "observer");
        Complex km = SpectralGreens.ProperRoot(_kSq[m]);
        bool mixed = kernel == GreensKernel.MixedVectorPotential;

        // Cross-region: every term crosses at least one full region and decays. Nothing to extract.
        if (n != m)
            return new InteriorAsymptote(Complex.Zero, Complex.Zero, km, 0, 0, mixed);

        double zb = Stack.RegionBottomZ(m);
        bool hasFloor = !double.IsNegativeInfinity(zb);
        double delta = Math.Abs(z - zp);
        double sigma = hasFloor ? z + zp - 2 * zb : 0.0;

        // The k_ρ → ∞ limit of the generalised reflection looking DOWN from the source's region: the
        // local Fresnel coefficient there, because every round trip below it has died.
        Complex re = Complex.Zero, rh = Complex.Zero;
        if (hasFloor)
        {
            if (m == 1 && Stack.IsWall(0))
            {
                re = rh = Stack.Bottom.Kind == TerminationKind.Pec ? -Complex.One : Complex.One;
            }
            else
            {
                Complex ea = _eps[m], eb = _eps[m - 1];
                Complex ma = _mu[m],  mb = _mu[m - 1];
                re = (ea - eb) / (ea + eb);
                rh = (mb - ma) / (mb + ma);
            }
        }

        Complex muRel = _mu[m];
        (Complex dir, Complex img) = kernel switch
        {
            GreensKernel.VectorPotential         => (muRel,          muRel * rh),
            GreensKernel.ScalarPotential         => (1.0 / _eps[m],  re / _eps[m]),
            GreensKernel.VerticalVectorPotential => (muRel,          muRel * (rh - 2.0 * re)),
            GreensKernel.MixedVectorPotential    => (Complex.Zero,   muRel * (rh - re)),
            _ => throw new ArgumentOutOfRangeException(nameof(kernel)),
        };

        return new InteriorAsymptote(dir, img, km, delta, hasFloor ? sigma : 0.0, mixed);
    }

    public Complex MixedKernel(Complex w, double z, double zp)
    {
        int n = RegionOfOrThrow(z, "observer");
        _ = RegionOfOrThrow(zp, "source");
        Complex muN = _mu[n];                                   // relative — the µ₀ divides out
        // (I_i^h − I_i^e)/k_ρ², through the contour extraction rather than a plain division.
        return muN * -DifferenceOverW(LineDifference.ShuntCurrent, w, z, zp) / Complex.ImaginaryOne;
    }

    private Complex VectorKernel(Complex w, double z, double zp) =>
        Voltage(SurfaceWavePolarization.Te, w, z, zp) /
        (Complex.ImaginaryOne * Omega * EmConstants.Mu0);

    /// <summary>
    /// G̃_q. When source and observer are both in the OPEN TOP half-space — which is every case
    /// production and every gate in this slice exercises — the kernel is assembled from Γ^q, whose
    /// own difference <c>Γ^e − Γ^h</c> is taken on O(1) quantities. The voltage route is
    /// mathematically identical but subtracts two ~Z-sized numbers to leave an O(w) remainder, so
    /// it amplifies rounding by |Z|/|V^e − V^h| — measured at ~3e-12 on a thin GaAs slab at 1 GHz,
    /// against ~1e-13 for the reflection route. The voltage route is kept for INTERIOR heights,
    /// where there is no half-space reflection to refer to, and its looser conditioning is a stated
    /// property of that path rather than a surprise for L9c.
    /// </summary>
    private Complex ScalarKernel(Complex w, double z, double zp)
    {
        int top = Stack.RegionCount - 1;
        // The reflection route is written in the AIR-TOP normalisation — it has no (ε₀/ε_top)
        // prefactor and its Γ^q uses k₀² where the general form needs the top region's own k². Both
        // are exactly right for every stack production or any earlier slice has ever built (air on
        // top) and both are silently wrong otherwise, by a factor of ε_top. **L9c's εᵣ-uniform
        // Tier-3 fixture — a lossy medium over a PEC with the SAME medium as the top half-space,
        // which is the reduction the brief asks for — is the first stack in this repository that is
        // not air-topped, and it read 4.4× wrong until this guard went in.** The voltage route below
        // is general by construction (it is built from Voltage, which knows every region's ε), so a
        // non-air top simply takes it and pays L9a's stated ~3e-12 conditioning instead of ~1e-13.
        // Nothing air-topped changes path, which is why every L9a and L9b number is untouched.
        bool airTop = (_eps[top] * _mu[top] - Complex.One).Magnitude <= 1e-12;
        if (airTop && Stack.Top.Kind == TerminationKind.HalfSpace &&
            Stack.RegionOf(z) == top && Stack.RegionOf(zp) == top)
        {
            Complex kz0 = KzOfRegion(top, w);
            double  bigH = Stack.TopZ;
            Complex gq   = TopFresnel(SurfaceWavePolarization.Tm, w)
                         - K0 * (Complex)K0 * ReflectionDifferenceOverW(w);
            Complex dir  = Complex.Exp(-Complex.ImaginaryOne * kz0 * Math.Abs(z - zp));
            Complex img  = Complex.Exp(-Complex.ImaginaryOne * kz0 * (z + zp - 2 * bigH));
            return (dir + gq * img) / (2.0 * Complex.ImaginaryOne * kz0);
        }

        return Complex.ImaginaryOne * Omega * EmConstants.Eps0 * DifferenceOverW(w, z, zp);
    }

    /// <summary>
    /// <b>R-lyr-4 — the k_ρ² → 0 cancellation, generalised.</b> At k_ρ = 0 every layer's TM and TE
    /// characteristic impedances coincide (both become the intrinsic impedance √(µ/ε)) and so do
    /// both terminations, so the two equivalent lines are the SAME network and
    /// <c>V^e − V^h</c> vanishes identically — against a <c>1/k_ρ²</c> prefactor. The one-layer
    /// kernel removes the resulting 0/0 in closed form (<see cref="SpectralGreens.ReflectionScalar"/>);
    /// for a cascade there is no such closed form, and the naive difference <b>has lost every digit
    /// by k_ρ ≈ 1e-8 k₀</b> — which is exactly where a DCIM sampling path starts.
    ///
    /// <para>What replaces it is the technique this area already uses for pole residues: the
    /// function <c>G(w) = V^e − V^h</c> is analytic at w = 0 with G(0) = 0, so its Taylor
    /// coefficients are extracted by a <b>trapezoidal average on a small circle in w</b> —
    /// spectrally accurate for an analytic function, needing nothing re-derived if the formulation
    /// changes, and structurally incapable of the cancellation the direct division suffers. Inside
    /// the circle the series is used; outside it the direct division is already well conditioned.
    /// The circle radius is <see cref="SmallWFraction"/>·k₀², a hundredth of the distance to the
    /// nearest singularity (the branch point at k_ρ = k₀), so twelve orders are far more than the
    /// series needs.</para>
    ///
    /// <para>The coefficients depend on the height pair and not on w, so they are extracted once
    /// per (z, z′) — which is what keeps a DCIM sampling path at a fixed height pair from paying
    /// the contour cost on every sample.</para>
    /// </summary>
    private Complex DifferenceOverW(Complex w, double z, double zp) =>
        DifferenceOverW(LineDifference.ShuntVoltage, w, z, zp);

    /// <summary>
    /// <b>L9c — which TM-minus-TE difference is being extracted.</b> All three vanish identically at
    /// <c>k_ρ = 0</c> for the same reason (there the TM and TE lines are the SAME network: every
    /// region's two characteristic impedances become its intrinsic impedance, and every cross-
    /// multiplied Fresnel coefficient collapses to <c>(k_a − k_b)/(k_a + k_b)</c> on both lines), so
    /// all three meet their <c>1/k_ρ²</c> prefactor as an exact zero and all three need the same
    /// contour extraction. Adding one is adding a member here, not a second mechanism.
    /// </summary>
    private enum LineDifference
    {
        /// <summary><c>V_i^e − V_i^h</c> — G_q's (L9a).</summary>
        ShuntVoltage,
        /// <summary><c>I_i^e − I_i^h</c> — the mixed component's (L9c).</summary>
        ShuntCurrent,
        /// <summary><c>I_v^e − I_v^h</c> — G_A^zz's (L9c).</summary>
        SeriesCurrent,
    }

    private Complex TmMinusTe(LineDifference kind, Complex w, double z, double zp) => kind switch
    {
        LineDifference.ShuntVoltage =>
            Voltage(SurfaceWavePolarization.Tm, w, z, zp) -
            Voltage(SurfaceWavePolarization.Te, w, z, zp),
        LineDifference.ShuntCurrent =>
            Current(SurfaceWavePolarization.Tm, w, z, zp) -
            Current(SurfaceWavePolarization.Te, w, z, zp),
        _ =>
            SeriesCurrent(SurfaceWavePolarization.Tm, w, z, zp) -
            SeriesCurrent(SurfaceWavePolarization.Te, w, z, zp),
    };

    private Complex DifferenceOverW(LineDifference kind, Complex w, double z, double zp)
    {
        double r = SmallWFraction * K0 * K0;
        if (w.Magnitude > r)
            return TmMinusTe(kind, w, z, zp) / w;

        var a = TaylorCoefficients(kind, z, zp);
        Complex sum = Complex.Zero, wp = Complex.One;      // wp = w^{n−1}
        for (int n = 1; n <= TaylorOrders; n++)
        {
            sum += a[n] * wp;
            wp  *= w;
        }
        return sum;
    }

    private Complex[] TaylorCoefficients(LineDifference kind, double z, double zp)
    {
        lock (_taylorGate)
        {
            if (_taylor.TryGetValue(((int)kind, z, zp), out var cached)) return cached;

            double r = SmallWFraction * K0 * K0;
            var g = new Complex[ContourSamples];
            for (int k = 0; k < ContourSamples; k++)
            {
                double th = 2.0 * Math.PI * k / ContourSamples;
                Complex w = r * Complex.Exp(new Complex(0, th));
                g[k] = TmMinusTe(kind, w, z, zp);
            }

            var a = new Complex[TaylorOrders + 1];
            for (int n = 1; n <= TaylorOrders; n++)
            {
                Complex s = Complex.Zero;
                for (int k = 0; k < ContourSamples; k++)
                {
                    double th = 2.0 * Math.PI * k / ContourSamples;
                    s += g[k] * Complex.Exp(new Complex(0, -n * th));
                }
                a[n] = s / (ContourSamples * Math.Pow(r, n));
            }

            _taylor[((int)kind, z, zp)] = a;
            return a;
        }
    }

    /// <summary>
    /// The NAIVE scalar kernel — a plain division by k_ρ² with no cancellation care. It exists so
    /// <c>T0</c> can assert that it IS ruined near k_ρ = 0, so the test cannot quietly stop
    /// demonstrating why <see cref="DifferenceOverW"/> matters. <b>Never call this in production.</b>
    /// </summary>
    public Complex ScalarKernelNaive(Complex kRho, double z, double zp)
    {
        Complex w = kRho * kRho;
        return Complex.ImaginaryOne * Omega * EmConstants.Eps0 *
               (Voltage(SurfaceWavePolarization.Tm, w, z, zp) -
                Voltage(SurfaceWavePolarization.Te, w, z, zp)) / w;
    }

    // -------------------------------------------------------------------------------------------
    // Reflection coefficients at the top interface — the quantities D5 gates directly against the
    // shipped one-layer kernel's Γ^e / Γ^h / Γ^q.
    // -------------------------------------------------------------------------------------------

    /// <summary>Γ^e (Tm) or Γ^h (Te) looking down at the stack's top interface, from the half-space above.</summary>
    public Complex TopInterfaceFresnel(SurfaceWavePolarization pol, Complex kRho)
    {
        RequireOpenTop();
        return TopFresnel(pol, kRho * kRho);
    }

    private Complex TopFresnel(SurfaceWavePolarization pol, Complex w) =>
        DownLadder(pol, w, Complex.Zero, naturalTop: true)[^1];

    /// <summary>Γ^e (Tm) or Γ^h (Te) at the top interface, as a function of k_z0 WITH ITS SIGN — D1.
    /// See <see cref="TopInterfaceReflectionAtKz0"/> for why the sign is the whole point.</summary>
    private Complex TopFresnelAtKz0(SurfaceWavePolarization pol, Complex kz0) =>
        DownLadder(pol, TopWavenumberSquared - kz0 * kz0, kz0, naturalTop: false)[^1];

    /// <summary>
    /// Γ^h (vector) or Γ^q (scalar) at the top interface — the reflection that appears in
    /// <c>G̃ = [e^{−jk_z0|z−z′|} + Γ e^{−jk_z0(z+z′−2H)}]/(2j k_z0)</c> for a source and observer in
    /// the top half-space.
    ///
    /// <para><b>Γ^q is NOT Γ^e</b>, exactly as in the one-layer kernel:
    /// <c>Γ^q = Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h)</c>. The two equivalent lines coincide at k_ρ = 0 (both
    /// impedances become the intrinsic impedance), so <c>Γ^e − Γ^h</c> vanishes as k_ρ² against the
    /// k₀²/k_ρ² prefactor and a finite, generally non-zero limit survives. That difference is taken
    /// through the SAME stable path R-lyr-4 uses for the kernel, not by a plain division — and it
    /// is taken directly on the two reflection coefficients rather than by inverting the kernel, so
    /// it stays finite at the branch point k_ρ = k₀ where the kernel itself does not.</para>
    /// </summary>
    public Complex TopInterfaceReflection(GreensKernel kernel, Complex kRho)
    {
        RequireOpenTop();
        if (kernel == GreensKernel.VectorPotential)
            return TopInterfaceFresnel(SurfaceWavePolarization.Te, kRho);

        Complex w  = kRho * kRho;
        Complex ge = TopInterfaceFresnel(SurfaceWavePolarization.Tm, kRho);
        return ge - K0 * (Complex)K0 * ReflectionDifferenceOverW(w);
    }

    /// <summary>
    /// <b>D1 — Γ^h (vector) or Γ^q (scalar) at the top interface as a function of k_z0, CARRYING ITS
    /// SIGN.</b> This is the entry point DCIM samples through, and it exists for exactly the reason
    /// <see cref="SpectralGreens.Kz1FromKz0"/> exists in the one-layer kernel.
    ///
    /// <para><b>Routing through k_ρ and back cannot express the question.</b> The cascade is
    /// parameterised by <c>w = k_ρ²</c>, and <c>w = k_top² − k_z0²</c> is <b>EVEN in k_z0</b> — so w
    /// alone discards the sign, the round trip costs a square root that can land on the wrong branch,
    /// and <c>k_z0 &lt; 0</c> is unreachable outright. DCIM's branch-point Taylor expansion is taken
    /// by central differences <i>through</i> k_z0 = 0 (<c>Dcim.BranchPointTaylor</c>), so it needs
    /// exactly the values the k_ρ entry point cannot supply.</para>
    ///
    /// <para><b>The generalisation is one rule.</b> Every INTERIOR region's k_zi is even in k_z0 and
    /// comes from <c>k_zi² = k_i² − k_top² + k_z0²</c> — the whole Möbius ladder below the top
    /// interface is invariant under <c>k_zi → −k_zi</c> (R-lyr-3), so their branch cannot matter. The
    /// TOP region's vertical wavenumber <b>IS</b> the supplied k_z0, with its literal sign, and that
    /// is the only place the sign enters. Nothing else changes.</para>
    ///
    /// <para><b>The bottom termination is the exception, and it is D3.</b> An open-below stack's
    /// <c>k_zb</c> also fails to be even — flipping it replaces the bottom Fresnel coefficient by its
    /// reciprocal — so Γ genuinely carries <c>√(k_b² − k_top² + k_z0²)</c>, a SECOND branch point at
    /// <c>k_z0² = k_top² − k_b²</c>. That is a fact about the spectrum, not about this method; see
    /// <c>CLAUDE.md</c> §L9b for where it sits and what it does to a fit in k_z0 alone.</para>
    /// </summary>
    public Complex TopInterfaceReflectionAtKz0(GreensKernel kernel, Complex kz0)
    {
        RequireOpenTop();
        if (kernel == GreensKernel.VectorPotential)
            return TopFresnelAtKz0(SurfaceWavePolarization.Te, kz0);

        Complex w  = TopWavenumberSquared - kz0 * kz0;
        Complex ge = TopFresnelAtKz0(SurfaceWavePolarization.Tm, kz0);
        return ge - K0 * (Complex)K0 * ReflectionDifferenceOverWAtKz0(w, kz0);
    }

    /// <summary>Γ/(2j k_z0) — the REFLECTED part of the top-half-space kernel at z = z′ = H, i.e.
    /// everything the stack adds to free space. The quantity DCIM takes pole residues of.</summary>
    public Complex TopReflectedKernel(GreensKernel kernel, Complex kRho) =>
        TopInterfaceReflection(kernel, kRho) / (2.0 * Complex.ImaginaryOne * Kz0(kRho));

    /// <summary>(Γ^e − Γ^h)/k_ρ², by direct division where that is conditioned and by the same
    /// contour-extracted Taylor series as <see cref="DifferenceOverW"/> where it is not.</summary>
    private Complex ReflectionDifferenceOverW(Complex w)
    {
        double r = SmallWFraction * K0 * K0;
        if (w.Magnitude > r)
        {
            return (TopFresnel(SurfaceWavePolarization.Tm, w) -
                    TopFresnel(SurfaceWavePolarization.Te, w)) / w;
        }

        var a = ReflectionTaylor(negativeBranch: false);
        Complex sum = Complex.Zero, wp = Complex.One;
        for (int n = 1; n <= TaylorOrders; n++) { sum += a[n] * wp; wp *= w; }
        return sum;
    }

    /// <summary>
    /// The same quantity along D1's k_z0 parameterisation.
    ///
    /// <para><b>Small |w| means k_z0 has come back to ±k_top, and the two signs are DIFFERENT
    /// analytic functions of w.</b> Γ^e − Γ^h vanishes as w on each of them (at k_ρ = 0 both
    /// equivalent lines are the same network, on either branch, because every interface coefficient
    /// reduces to <c>(k_a − k_b)/(k_a + k_b)</c> for both polarisations there) — but their Taylor
    /// coefficients differ, and quietly using the positive branch's series for a negative k_z0 is the
    /// kind of error that leaves the reflection coefficients perfect and only ruins Γ^q, which is
    /// exactly how L9a's proper-sheet defect presented.</para>
    /// </summary>
    private Complex ReflectionDifferenceOverWAtKz0(Complex w, Complex kz0)
    {
        double r = SmallWFraction * K0 * K0;
        if (w.Magnitude > r)
        {
            return (TopFresnelAtKz0(SurfaceWavePolarization.Tm, kz0) -
                    TopFresnelAtKz0(SurfaceWavePolarization.Te, kz0)) / w;
        }

        Complex root = Complex.Sqrt(TopWavenumberSquared - w);
        bool negative = (kz0 - root).Magnitude > (kz0 + root).Magnitude;

        var a = ReflectionTaylor(negative);
        Complex sum = Complex.Zero, wp = Complex.One;
        for (int n = 1; n <= TaylorOrders; n++) { sum += a[n] * wp; wp *= w; }
        return sum;
    }

    private readonly Complex[]?[] _reflectionTaylor = new Complex[2][];

    /// <summary>
    /// The Taylor coefficients of Γ^e − Γ^h at w = 0, extracted on a circle of radius
    /// <see cref="SmallWFraction"/>·k₀², on the requested branch of k_z0 = ±√(k_top² − w).
    ///
    /// <para>The positive branch is what <see cref="KzOfRegion"/> produces inside the principal disk
    /// (the radius is four times inside it, deliberately — R-lyr-4), so this reproduces the w-path's
    /// own series exactly rather than being a second, subtly different extraction of it.</para>
    /// </summary>
    private Complex[] ReflectionTaylor(bool negativeBranch)
    {
        lock (_taylorGate)
        {
            int slot = negativeBranch ? 1 : 0;
            if (_reflectionTaylor[slot] is { } cached) return cached;

            double r = SmallWFraction * K0 * K0;
            var g = new Complex[ContourSamples];
            for (int k = 0; k < ContourSamples; k++)
            {
                double th = 2.0 * Math.PI * k / ContourSamples;
                Complex w = r * Complex.Exp(new Complex(0, th));
                if (negativeBranch)
                {
                    Complex kz0 = -Complex.Sqrt(TopWavenumberSquared - w);
                    g[k] = TopFresnelAtKz0(SurfaceWavePolarization.Tm, kz0) -
                           TopFresnelAtKz0(SurfaceWavePolarization.Te, kz0);
                }
                else
                {
                    g[k] = TopFresnel(SurfaceWavePolarization.Tm, w) -
                           TopFresnel(SurfaceWavePolarization.Te, w);
                }
            }

            var a = new Complex[TaylorOrders + 1];
            for (int n = 1; n <= TaylorOrders; n++)
            {
                Complex s = Complex.Zero;
                for (int k = 0; k < ContourSamples; k++)
                {
                    double th = 2.0 * Math.PI * k / ContourSamples;
                    s += g[k] * Complex.Exp(new Complex(0, -n * th));
                }
                a[n] = s / (ContourSamples * Math.Pow(r, n));
            }

            return _reflectionTaylor[slot] = a;
        }
    }

    /// <summary>
    /// The k_ρ → ∞ (quasi-static) limit of the top-interface reflection: <b>0</b> for G_A (the TE
    /// impedance ratio tends to 1, so the stack becomes invisible) and the classic dielectric
    /// image coefficient <c>(ε_top − ε_1)/(ε_top + ε_1)</c> for G_q — the same K kernel A's
    /// dielectric-interface row carries.
    /// </summary>
    public Complex AsymptoticTopReflection(GreensKernel kernel)
    {
        RequireOpenTop();
        if (kernel is GreensKernel.VerticalVectorPotential or GreensKernel.MixedVectorPotential)
            throw new ArgumentException(
                $"{kernel} has no 'asymptotic top reflection': it is not built from a reflection " +
                $"coefficient at all. Its k_ρ → ∞ behaviour is a pair of coefficients on the DIRECT " +
                $"and the DOWN-REFLECTED exponential in the SOURCE region's own k_zm — see " +
                $"AsymptoticAtHeights, which is what the interior inverse transform consumes.",
                nameof(kernel));
        if (kernel == GreensKernel.VectorPotential) return Complex.Zero;
        int top = Stack.RegionCount - 1;
        if (Stack.LayerCount == 0) return Complex.Zero;
        Complex et = _eps[top], e1 = _eps[top - 1];
        return (et - e1) / (et + e1);
    }

    private void RequireOpenTop()
    {
        if (Stack.Top.Kind != TerminationKind.HalfSpace)
            throw new InvalidOperationException(
                $"The top termination is {Stack.Top}, a solid wall — there is no half-space above " +
                $"the stack for a reflection coefficient to be referenced in.");
    }
}
