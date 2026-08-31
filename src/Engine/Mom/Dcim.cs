using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>One fitted complex image: amplitude A, complex depth b, giving A·e^{−jk₀R}/4πR with
/// R = √(ρ² + b²).</summary>
public sealed record ComplexImage(Complex Amplitude, Complex Depth);

/// <summary>
/// An extracted surface-wave pole and its residue in the variable w = k_ρ².
///
/// <para><b>D4 — this record was WIDENED rather than duplicated, and the reason is worth stating.</b>
/// Two finders produce poles here: <see cref="SpectralGreens.SurfaceWaveModes"/> returns
/// <see cref="SurfaceWaveMode"/>, and the general stack's chain-matrix search returns
/// <see cref="LayeredSurfaceWaveMode"/>. The three ways out were to widen this record, to unify the
/// two mode records, or to carry both.</para>
///
/// <list type="bullet">
///   <item><b>Unifying the mode records was rejected</b> as premature: the two differ only in what
///     they record about the SEARCH (the slab carries its lossless root; the general finder carries
///     a normalised residual as well), and merging them would force the shipped one-layer finder to
///     change for the benefit of a consumer that never reads either field. R-dcm-1 says the
///     one-layer path stays put.</item>
///   <item><b>Carrying both was rejected</b> because it puts two nullable fields on every term and
///     makes every consumer branch on which one is populated — for a fit that uses neither.</item>
///   <item><b>Widening is what is left, and it is the honest shape</b>: DCIM consumes exactly a
///     pole LOCATION, its RESIDUE and a NAME to report it by. The search diagnostics stay on the
///     search reports, where they belong and where they are still reachable.</item>
/// </list>
/// </summary>
/// <param name="Name">The mode's own name, e.g. <c>TM0</c> — for reporting only.</param>
/// <param name="KRho">The pole location in k_ρ (complex whenever the medium is lossy).</param>
/// <param name="Residue">The residue of Γ/(2j k_z0) at w = KRho², by contour average.</param>
public sealed record SurfaceWaveTerm(string Name, Complex KRho, Complex Residue);

/// <summary>
/// The DCIM knobs. <see cref="PathExtent"/> is the single most consequential one: the sampling path
/// reaches k_ρ ≈ k₀·PathExtent, and the fit is only meaningful where the spectral content it saw
/// matters — which is why the accuracy claim in <c>CLAUDE.md</c> is a MEASURED ρ/λ range and not
/// "it works".
/// </summary>
public sealed record DcimSettings(
    double PathExtent    = 300.0,
    int    Samples       = 512,
    int    MaxOrder      = 14,
    double FitTolerance  = 1e-8,
    bool   ExtractSurfaceWaves = true,
    /// <summary>
    /// How many branch-point Taylor orders to impose EXACTLY on the amplitudes: 0 = none,
    /// 1 = the far-field sum rule alone, 3 = value, slope and curvature.
    ///
    /// <para><b>1, chosen by measurement (worst |ΔG| against the direct integrator over
    /// ρ/λ ∈ [1e-4, 10], both starter substrates at 10 GHz):</b></para>
    /// <code>
    ///   orders   FR-4 G_q   GaAs G_q   FR-4 G_A   GaAs G_A
    ///     0      2.6e-2     8.2e-2     2.6e-3     5.1e-4
    ///     1      4.9e-3     5.1e-1     6.8e-5     1.4e-6      &lt;- default
    ///     2      8.3e-3     3.4e+0     4.9e-6     1.4e-6
    ///     3      1.9e-2     6.2e+1     1.0e-6     1.4e-6
    /// </code>
    /// <para>Order 1 is the only one of these that is a <b>theorem rather than a knob</b>: 1 + Γ
    /// vanishes identically at the branch point, so Σ A_i = −(1 + Γ(∞)) is exact physics and the
    /// 1/ρ far field it removes has no business being there. Orders 2 and 3 pin genuine Taylor
    /// coefficients too, but as EXACT equalities they fight the sampled data — the spectral fit
    /// residual degrades 100× on GaAs — and the table is what says so. They are kept reachable
    /// because they are right for G_A, where they buy another 60×.</para>
    /// </summary>
    int    BranchPointOrders   = 1,
    double FarPathExtent = 4.0,
    int    FarSamples    = 192,
    int    FarOrder      = 6,
    /// <summary>
    /// Samples of a third block straddling the branch point k_z0 = 0 on the real axis, where the
    /// far-field physics lives and where neither sampling path goes. Fitting it by least squares
    /// is the softer alternative to pinning Taylor coefficients exactly.
    ///
    /// <para><b>0 — off by default, and that is also a measurement.</b> It buys FR-4's G_A another
    /// 30× (6.8e-5 → 2.4e-6) and costs FR-4's G_q 4× (4.9e-3 → 2.0e-2). G_q is the kernel that
    /// carries the charge, so it wins the trade. Left in place because the same trade goes the
    /// other way on a thin high-εᵣ slab and someone will want it.</para>
    /// </summary>
    int    BranchSamples = 0,
    /// <summary>Half-width of that block, in units of k₀.</summary>
    double BranchExtent  = 1.0)
{
    public static readonly DcimSettings Default = new();
}

/// <summary>
/// <b>M3 — the Discrete Complex Image Method: the production inverse transform.</b> Approximate the
/// spectral Green's function as a sum of complex exponentials in k_z0, each of which inverts in
/// closed form by the Sommerfeld identity, so the spatial-domain kernel becomes a handful of
/// closed-form image terms instead of an oscillatory integral.
///
/// <para><b>Three things are peeled off BEFORE any exponential is fitted, and the order matters.</b></para>
/// <list type="number">
///   <item><b>The free-space direct term</b> — exact, and never anywhere near the fit.</item>
///   <item><b>The quasi-static constant</b> Γ(∞) = (1 − εᵣ)/(1 + εᵣ). A constant in the spectral
///     domain is an image at zero depth; leave it in and the remainder never decays, so the fit
///     spends its whole order budget approximating something with a closed form.</item>
///   <item><b>The surface-wave poles</b> — <b>R-lgf-3, and this is the one that is easy to skip.</b>
///     A pole is not well approximated by a sum of exponentials. Skipping the extraction gives a
///     DCIM that looks fine in the near field and falls apart at a few wavelengths, which is the
///     classic failure and is invisible unless something independent is watching. Each pole's
///     residue in w = k_ρ² is taken by a circular contour average — no analytic differentiation of
///     the dispersion relation, so nothing has to be re-derived if the formulation changes — and
///     inverts exactly to <c>R·(−j/4)·H₀⁽²⁾(k_pρ)</c>.</item>
/// </list>
///
/// <para><b>Why Prony and not a matrix pencil.</b> GPOF's pole step is the eigenvalue problem of a
/// small general complex matrix, and this repository deliberately does not have a general complex
/// eigensolver: L7b-b weighed writing one (Hessenberg + shifted QR) and declined, on the grounds
/// that it is a real numerical-methods commitment that the measurement did not earn. The same
/// judgement applies here and the same escape is available — classic Prony reaches the same poles
/// through a linear-prediction least squares (solved by Householder QR, written here) plus
/// polynomial rooting (Durand-Kerner, thirty lines). <b>The order is chosen by measurement, not
/// assumed:</b> M is raised one at a time and the first order whose fit residual meets tolerance
/// wins, so the system is never driven into rank deficiency. And the whole step is self-validating
/// — <see cref="DcimModel.FitResidual"/> reports how well the fitted exponentials reproduce the
/// samples they were fitted to, which no choice of rooting algorithm can fake.</para>
///
/// <para><b>R-lgf-5 — everything here is per-frequency.</b> A <see cref="DcimModel"/> is built for
/// one <see cref="SpectralGreens"/>, i.e. one ω. Unlike kernel A there is no frequency-independent
/// quantity to cache and there is deliberately no fill counter suggesting otherwise.</para>
/// </summary>
public static class Dcim
{
    /// <summary>
    /// <b>R-lgf-4 / R-mom-17 — the stated validated range, as a refusal rather than a footnote.</b>
    ///
    /// <para>Measured against direct Sommerfeld integration over ρ/λ ∈ [1e-4, 10], on both starter
    /// substrates, at 2, 10 and 20 GHz. <b>Two numbers, because they answer two questions.</b></para>
    ///
    /// <list type="bullet">
    ///   <item><b>Error as a fraction of the free-space kernel at the same ρ</b> — |ΔG|·4πρ, which
    ///     is what a MoM matrix fill experiences, since an entry perturbed by ε·(1/4πρ) perturbs the
    ///     linear system by ε. <b>≤ 6e-3 across the ENTIRE span</b>, worst case, both kernels, every
    ///     substrate and frequency measured. This is the number L8c should be scheduled against.</item>
    ///   <item><b>Strict relative error |ΔG|/|G|</b> — what a user reading one Green's-function value
    ///     off a plot sees. <b>≤ 1e-2 out to ρ/λ ≈ 1</b>, which is what this refusal enforces, and
    ///     degrading beyond: 0.25–0.57 at ρ/λ = 10 on the cases where the scalar kernel's own value
    ///     has fallen two orders below the terms it is the difference of.</item>
    /// </list>
    ///
    /// <para><b>The gap between those two is real physics, not slack.</b> G_q has deep cancellation
    /// zones — a few substrate heights out, charge plus its ground image is a DIPOLE, so G_q falls
    /// like h²/ρ³ while its constituents fall like 1/ρ — and a relative error against a quantity
    /// that is nearly zero says more about the zero than about the method. On the 100 µm GaAs slab
    /// at 2 GHz that zone begins at ρ/λ ≈ 1e-4, where the strict error is 1.4e-2 and the scaled
    /// error is 1.8e-3.</para>
    ///
    /// <para>This is L8's own limit, not a bug to be fixed later by tightening a tolerance. It is
    /// stated here so L8c's matrix fill — which knows its own mesh extent — can ask, rather than
    /// discovering it from a plausible, smooth, wrong s-parameter. The full curve is in
    /// <c>src/Engine/Mom/CLAUDE.md</c> and is reproduced by <c>LayeredGreensFunctionTests.T2_1</c>.</para>
    /// </summary>
    public const double ValidatedRhoOverLambda = 1.0;

    /// <inheritdoc cref="ValidatedRhoOverLambda"/>
    public static EmSuitability WithinValidatedRange(GreensKernel kernel, double rhoOverLambda) =>
        rhoOverLambda <= ValidatedRhoOverLambda
            ? EmSuitability.Yes
            : EmSuitability.No(
                $"The L8 DCIM Green's function is validated to ρ/λ ≤ {ValidatedRhoOverLambda} in strict " +
                $"relative error; this asks for {kernel} at ρ/λ = {rhoOverLambda:G4}. Beyond that the " +
                $"far field is carried by the surface wave and the lateral wave and the fitted complex " +
                $"images stop resolving it — measured at 0.25–0.57 relative at ρ/λ = 10 on the starter " +
                $"substrates (LayeredGreensFunctionTests.T2_1). Note the error as a fraction of the " +
                $"free-space kernel stays under 6e-3 even there, so a matrix fill is far less exposed " +
                $"than this refusal implies; it is worded on the strict measure deliberately. " +
                $"A structure larger than a wavelength needs an explicit far-field treatment — a " +
                $"lateral-wave term or an asymptotic evaluation of the tail — which no fit in this " +
                $"repository provides; SommerfeldIntegral is accurate everywhere out there and far " +
                $"too slow to fill a matrix with.");

    /// <summary>
    /// <b>D3 / R-dcm-6 — the stacks whose SPECTRUM this basis cannot represent, refused by name.</b>
    /// This is not an accuracy threshold; it is a statement about what a sum of exponentials in k_z0
    /// is capable of, and the measurement that establishes it is in <c>CLAUDE.md</c> §L9b.
    ///
    /// <para>DCIM approximates Γ as <c>Σ A_i e^{−j k_z0 b_i}</c> — an ENTIRE function of k_z0. With
    /// the bottom termination closed (PEC/PMC) that is the right shape: Γ depends on k_z0 only through
    /// the single branch <c>√(k_top² − k_z0² …)</c> of the top half-space, and every interior region's
    /// k_zi is even in k_z0 so its branch cannot matter. <b>An open bottom breaks that.</b> Γ then also
    /// depends on <c>k_zb = √(k_b² − k_top² + k_z0²)</c>, which is NOT even and NOT single-valued: it
    /// carries a second branch point at <c>k_z0² = k_top² − k_b²</c>, and with a denser half-space
    /// below (k_b &gt; k_top — which εᵣ ≥ 1 makes the only possibility under an air top) that point sits
    /// on the <b>negative-imaginary k_z0 axis</b>, inside the half-plane the sampling path runs into.
    /// Physically the cut is the lateral wave launched into the substrate; it decays as 1/ρ² and no
    /// image can carry it.</para>
    ///
    /// <para><b>Measured on a 4 µm oxide over semi-infinite silicon at 10 GHz</b> (the honest shape;
    /// <c>LayerStacks.OpenBelow</c> is alumina in AIR, where k_b = k₀ exactly and the two branch
    /// points COINCIDE, so it cannot separate the question): the fit is still good in the near field
    /// and reaches an error of <b>59× the free-space kernel on G_q and 2.3e+4 on G_A</b> by ρ/λ = 10,
    /// against ≤ 2e-2 on every grounded stack. That is not a fit that needs more orders.</para>
    ///
    /// <para>An open bottom that is NOT denser than the top is accepted: the two branch points then
    /// coincide and there is only one cut, which is the case this basis was built for.</para>
    /// </summary>
    public static EmSuitability CanFit(LayerStack stack)
    {
        if (stack.Top.Kind != TerminationKind.HalfSpace)
            return EmSuitability.No(
                $"The top termination is {stack.Top}, a solid wall. DCIM's images are referenced to a " +
                $"half-space above the stack — the direct term and the quasi-static image are the " +
                $"inverse transforms of exponentials in that region's own k_z0 — so a closed guide " +
                $"needs a different decomposition entirely: a discrete WAVEGUIDE-MODE expansion of " +
                $"the enclosure rather than a continuous spectrum with images fitted to it. Nothing " +
                $"in this repository provides one.");

        if (stack.Bottom.Kind != TerminationKind.HalfSpace) return EmSuitability.Yes;

        double below = stack.Bottom.Material.EpsR * stack.Bottom.Material.MuR;
        double above = stack.Top.Material.EpsR * stack.Top.Material.MuR;
        if (below <= above * (1.0 + 1e-9)) return EmSuitability.Yes;

        return EmSuitability.No(
            $"This stack is OPEN BELOW into a half-space that is denser than the one above it " +
            $"(εᵣµᵣ = {below:G4} below against {above:G4} above), which puts a SECOND branch point of " +
            $"the spectrum at k_z0 = ±j·k₀√({below:G4} − {above:G4}) — on the negative-imaginary axis, " +
            $"inside the half-plane the DCIM sampling path runs into. DCIM fits Γ as a sum of " +
            $"exponentials in k_z0, which is an entire function and cannot carry a second cut: the " +
            $"missing piece is the lateral wave launched downward into the substrate, which falls off " +
            $"as 1/ρ² and is not an image. Measured on a 4 µm oxide over silicon at 10 GHz, the error " +
            $"reaches 59× the free-space kernel on G_q and 2.3e+4× on G_A by ρ/λ = 10, against ≤ 1.6e-2 " +
            $"inside the validated range on every stack that is not refused. This is a MISSING TERM, not a tolerance — carrying it needs a " +
            $"second exponential family in k_zb or an extracted lateral-wave term, and neither is " +
            $"built. SommerfeldIntegral.EvaluateLayered is accurate on this stack everywhere measured " +
            $"(it moves by 1.4e-10 under a 100× refinement) and is far too slow to fill a matrix with.");
    }

    /// <summary>
    /// <b>R-dcm-4 — the GENERAL medium's own validated range, measured on its own stacks and worded
    /// as its own refusal.</b> <see cref="ValidatedRhoOverLambda"/> and its wording are the ONE-LAYER
    /// kernel's and have not been touched; that this constant has the same value is a measurement,
    /// not an inheritance.
    ///
    /// <para>Measured against direct Sommerfeld integration over ρ/λ ∈ [1e-4, 10], on six stacks
    /// (both shipped starters, a three-dielectric PCB build, a two-level MMIC, an ungrounded alumina
    /// slab and a thin film on silicon), both kernels, at 2, 10 and 20 GHz —
    /// <c>LayeredDcimTests.R4_2</c>. Inside everything this refusal admits, the error as a fraction of
    /// the free-space kernel is <b>≤ 1.6e-2</b>, against L8a's ≤ 6e-3 on the two single-substrate
    /// starters. <b>The general medium is worse, by about 2.6×, and this says so rather than
    /// rounding it into the same sentence.</b> The four largest values all sit at ρ/λ = 5.6e-4, the
    /// first grid point above the near-field floor below; every case whose worst point is elsewhere
    /// is ≤ 5.1e-4. Strict relative error on G_q is ≤ 1e-2 out to
    /// ρ/λ ≈ 1 exactly as at L8a, and degrades to 0.25–0.65 at ρ/λ = 10 in the same dipole
    /// cancellation zone.</para>
    ///
    /// <para><b>Three things beyond L8a's single ceiling, each measured:</b></para>
    /// <list type="number">
    ///   <item><b>A stack whose spectrum this basis cannot represent at all</b> — see
    ///     <see cref="CanFit(LayerStack)"/>. That is a missing term, not a range.</item>
    ///   <item><b>A NEAR-FIELD floor, which L8a did not have and which is derived rather than
    ///     fitted.</b> The sampling path reaches k_ρ = <c>PathExtent</c>·k₀ and no further, so no
    ///     spatial structure finer than <c>1/(PathExtent·k₀)</c> was ever sampled — which in ρ/λ is
    ///     the stack-independent constant <c>1/(2π·PathExtent) = 5.3e-4</c> at the default. L8a's own
    ///     ρ/λ ∈ [1e-4, 10] sweep ran below that and got away with it only because both starters are
    ///     single substrates whose thinnest feature is 100 µm. Put a 3 µm spacer in the stack and it
    ///     bites: the MMIC two-level stack's G_q is 1.8e-1 at ρ/λ = 1e-4 and 1.6e-2 above the
    ///     floor.</item>
    ///   <item><b>An electrically thin UNGROUNDED stack.</b> Measured on 0.5 mm alumina in air: at
    ///     2 GHz (k₀H = 0.021) the error is 1.5e-1 on G_q and 1.7e-1 on G_A, against ≤ 2.1e-2 on the
    ///     same stack at 10 GHz (k₀H = 0.105) and 20 GHz. <b>The mechanism has not been isolated and
    ///     the refusal does not claim one</b> — the obvious candidate, a barely-guided TM₀ pole
    ///     sitting on the branch point, is ruled out by the grounded slabs putting their poles
    ///     CLOSER at the same frequency and staying accurate. It is a measured, bracketed,
    ///     conservative floor on one stack shape, and it touches no grounded stack.</item>
    /// </list>
    /// </summary>
    public const double ValidatedRhoOverLambdaLayered = 1.0;

    /// <summary>
    /// <b>L9e/D8 — the near-DC floor, and it closes a hole L8e recorded rather than fixed.</b>
    ///
    /// <para>Writing L8's own phase gate produced a run that spent 50 s and ended in
    /// <c>Array dimensions exceeded supported range</c> — a raw framework exception with no refusal
    /// attached. The cause was a <b>6 Hz</b> frequency point, at which the per-frequency radial
    /// remainder table is sized for a wavelength of 50,000 km. It was unreachable from the EM panel
    /// (the frequency spec is authored in GHz and the mesher refuses long before), so it was recorded
    /// and left. <b>Adaptive frequency sampling is what makes it reachable</b>: a scheme that chooses
    /// its own frequencies must never choose one there, so the refusal belongs in this slice.</para>
    ///
    /// <para>The number is <see cref="GroundedSlab.MinElectricalThickness"/>'s own — k₀H &lt; 1e-6
    /// puts every wave correction below 1e-12 of the static answer, so a full-wave fit there is
    /// computing nothing the static kernel would not give exactly and far faster.</para>
    /// </summary>
    public const double MinElectricalThicknessForFit = GroundedSlab.MinElectricalThickness;

    /// <summary>
    /// <b>L9b's R-dcm-4, turned into a check.</b> <see cref="DcimSettings.PathExtent"/> is a
    /// statement in units of k₀, while the stack's image structure lives at k_ρ ~ 1/H, which does
    /// not move with frequency. So what actually decides whether the fit SEES the stack is
    /// <c>PathExtent·k₀H</c>, and on the 1.4 mm PCB stack that product falls through 1 between
    /// 300 MHz and 100 MHz — below which the error GROWS as the frequency falls (measured: 3.8e-3
    /// at 300 MHz, 2.9e-2 at 100 MHz), which is not a floor and is not the oracle.
    /// </summary>
    public static EmSuitability CanFitAtFrequency(double k0, double stackHeightM,
                                                  DcimSettings? settings = null)
    {
        double kh = k0 * stackHeightM;

        if (!(kh > MinElectricalThicknessForFit))
            return EmSuitability.No(
                $"At this frequency the stack is k₀H = {kh:G3} thick, below the full-wave fit's " +
                $"floor of {MinElectricalThicknessForFit:G3}. Two separate things break there and " +
                $"neither is recoverable: every wave correction is more than twelve orders below the " +
                $"static answer, so the fit is computing nothing StaticGreens/LayeredStaticGreens " +
                $"would not give exactly; and the per-frequency radial remainder table is sized " +
                $"against the wavelength, which at this frequency is astronomical — a 6 Hz point " +
                $"spends 50 s and ends in a raw 'Array dimensions exceeded supported range', with no " +
                $"refusal attached, which is what this one exists to replace. Raise the sweep's " +
                $"lower edge, or use the static kernel for the DC limit.");

        double product = (settings ?? DcimSettings.Default).PathExtent * kh;
        return product >= 1.0
            ? EmSuitability.Yes
            : EmSuitability.No(
                $"At this frequency PathExtent·k₀H = {product:G3}, i.e. the sampling path stops " +
                $"before it reaches the k_ρ ~ 1/H scale the stack's own image structure lives at, so " +
                $"the fit does not see the stack. This is not a floor the error settles onto — it " +
                $"GROWS as the frequency falls: measured on a 1.4 mm PCB stack, 3.8e-3 at 300 MHz " +
                $"and 2.9e-2 at 100 MHz. Raise DcimSettings.PathExtent to at least " +
                $"{1.0 / kh:F0} (and Samples with it, since a wider path at a fixed sample count is " +
                $"a sparser one), or raise the sweep's lower edge.");
    }

    /// <summary>Below this the fit never sampled the scale being asked about — see
    /// <see cref="ValidatedRhoOverLambdaLayered"/>, item 2. Stack-independent by construction.</summary>
    public static double NearFieldFloorRhoOverLambda(DcimSettings? settings = null) =>
        1.0 / (2.0 * Math.PI * (settings ?? DcimSettings.Default).PathExtent);

    /// <summary>An UNGROUNDED stack thinner than this (electrically) is refused — item 3 above.</summary>
    public const double MinUngroundedElectricalThickness = 0.05;

    /// <inheritdoc cref="ValidatedRhoOverLambdaLayered"/>
    public static EmSuitability WithinValidatedRangeLayered(
        GreensKernel kernel, LayeredSpectralGreens g, double rhoOverLambda,
        DcimSettings? settings = null)
    {
        var structural = CanFit(g.Stack);
        if (!structural.Ok) return structural;

        double floor = NearFieldFloorRhoOverLambda(settings);
        if (rhoOverLambda < floor)
            return EmSuitability.No(
                $"The L9 DCIM fit samples k_ρ out to {(settings ?? DcimSettings.Default).PathExtent:G4}·k₀ " +
                $"and no further, so it has no information about spatial structure finer than " +
                $"ρ/λ = {floor:G3}; this asks for {kernel} at ρ/λ = {rhoOverLambda:G4}. That floor does " +
                $"not depend on the stack — it is 1/(2π·PathExtent) — but whether it MATTERS does: it " +
                $"is invisible on a single 1.6 mm substrate and it dominates on a stack with a 3 µm " +
                $"layer in it, where the error is 1.8e-1 at ρ/λ = 1e-4 and 1.6e-2 above the floor " +
                $"(LayeredDcimTests.R4_2, MMIC 2-level at 2 GHz). Raising DcimSettings.PathExtent " +
                $"lowers the floor proportionally and costs nothing but samples.");

        double kh = g.K0 * g.Stack.TopZ;
        if (g.Stack.Bottom.IsOpen && kh < MinUngroundedElectricalThickness)
            return EmSuitability.No(
                $"This stack is UNGROUNDED (open below) and only k₀·H = {kh:G3} thick, below the " +
                $"measured floor of {MinUngroundedElectricalThickness:G3} for an open-below stack. " +
                $"Measured on 0.5 mm alumina in air: at 2 GHz (k₀H = 0.021) the error reaches 1.5e-1 " +
                $"on G_q and 1.7e-1 on G_A as a fraction of the free-space kernel at ρ/λ = 10, against " +
                $"≤ 2.1e-2 on the SAME stack at 10 GHz (k₀H = 0.105) and 20 GHz. The floor is bracketed " +
                $"by those two measurements and is deliberately conservative. **The mechanism has not " +
                $"been isolated and this refusal does not claim one** — the obvious candidate, a TM₀ " +
                $"pole sitting on top of the branch point, is ruled out: the GROUNDED FR-4 and GaAs " +
                $"slabs put their poles CLOSER to it at the same frequency (1 + 7.8e-6 and 1 + 7.5e-6, " +
                $"against 1 + 4.4e-5 here) and are accurate to 2.0e-3 and 1.0e-2. A grounded stack of " +
                $"any thickness is unaffected by this refusal. SommerfeldIntegral.EvaluateLayered is " +
                $"accurate there and far too slow to fill a matrix with.");

        if (rhoOverLambda > ValidatedRhoOverLambdaLayered)
            return EmSuitability.No(
                $"The L9 general-medium DCIM Green's function is validated to " +
                $"ρ/λ ≤ {ValidatedRhoOverLambdaLayered} in strict relative error; this asks for " +
                $"{kernel} at ρ/λ = {rhoOverLambda:G4}. Beyond that the far field is carried by the " +
                $"surface waves and the lateral wave and the fitted complex images stop resolving it — " +
                $"measured at 0.26–0.65 relative at ρ/λ = 10 across the stacks in " +
                $"LayeredDcimTests.R4_2. As at L8a the error as a fraction of the free-space kernel " +
                $"stays far smaller than this refusal implies (≤ 1.6e-2 across everything admitted " +
                $"here, against L8a's ≤ 6e-3 on the two single-substrate starters — the general medium " +
                $"is about 2.6× worse), so a matrix fill is much less exposed than a plotted value; it " +
                $"is worded on the strict measure deliberately, exactly as the one-layer refusal is. " +
                $"SommerfeldIntegral.EvaluateLayered is accurate everywhere out there and far too slow " +
                $"to fill a matrix with.");

        return EmSuitability.Yes;
    }

    public static DcimModel Fit(SpectralGreens g, GreensKernel kernel, DcimSettings? settings = null)
    {
        var s = settings ?? DcimSettings.Default;

        Complex kInfinity = g.AsymptoticReflection(kernel);

        // ---- 2. the surface-wave poles, with their residues in w = k_ρ².
        var poles = new List<SurfaceWaveTerm>();
        if (s.ExtractSurfaceWaves)
            foreach (var m in g.SurfaceWaveModes)
                poles.Add(new SurfaceWaveTerm(m.Name, m.KRho, Residue(g, kernel, m)));

        return FitCore(kz0 => g.ReflectionAtKz0(kernel, kz0), g.K0, g.K0 * (Complex)g.K0,
                       g.Slab.HeightM, kInfinity, poles, s, g, null, kernel);
    }

    /// <summary>
    /// <b>L9b — the same fit, driven through the GENERAL layered kernel.</b> Everything below the
    /// entry point is shared with the one-layer path verbatim, which is what makes R-dcm-1's
    /// bit-identity claim checkable rather than aspirational: only the three inputs change.
    ///
    /// <list type="number">
    ///   <item>Γ(∞) is the top interface's own dielectric image coefficient rather than the slab's.</item>
    ///   <item>The poles come from the chain-matrix search and there may be MORE THAN ONE — an
    ///     ungrounded stack carries a TE mode at every frequency measured, where a grounded slab has
    ///     none until 25 GHz (L9a's own table). <see cref="PoleSum"/> already sums N of them.</item>
    ///   <item>The remainder is sampled through <see cref="LayeredSpectralGreens.TopInterfaceReflectionAtKz0"/>
    ///     — D1, and the reason a naive port fails.</item>
    /// </list>
    ///
    /// <para><b>No height argument, and that is D5's finding rather than an omission.</b> For source
    /// and observer anywhere in the TOP half-space the height pair enters the spatial answer as an
    /// EXACT SHIFT of every fitted image, so one fit serves every height pair —
    /// <see cref="DcimModel.Evaluate(double, double, double)"/>. A fit per height pair would be
    /// wasted work, and the measurement that says so is in <c>CLAUDE.md</c> §L9b.</para>
    /// </summary>
    public static DcimModel Fit(LayeredSpectralGreens g, GreensKernel kernel,
                                DcimSettings? settings = null)
    {
        var s = settings ?? DcimSettings.Default;

        Complex kInfinity = g.AsymptoticTopReflection(kernel);

        var modes = g.SurfaceWaves.Modes;
        var poles = new List<SurfaceWaveTerm>();
        if (s.ExtractSurfaceWaves)
            foreach (var m in modes)
                poles.Add(new SurfaceWaveTerm(m.Name, m.KRho, Residue(g, kernel, m, modes)));

        return FitCore(kz0 => g.TopInterfaceReflectionAtKz0(kernel, kz0), g.K0, g.TopWavenumberSquared,
                       g.Stack.TopZ, kInfinity, poles, s, null, g, kernel);
    }

    /// <summary>
    /// <b>L9c / M3 — the fit at an ARBITRARY height pair, including inside the stack and across
    /// regions.</b> This is the third of D4's three pairings; the other two already existed
    /// (<see cref="Fit(LayeredSpectralGreens, GreensKernel, DcimSettings?)"/> plus L9b's D5 exact
    /// shift covers every high–high pair with no refit at all).
    ///
    /// <para><b>The whole fit is re-referenced from the top half-space's k₀ to the SOURCE REGION's
    /// own k_m, and that is the one structural change.</b> L8a's decomposition writes the kernel as
    /// <c>[1 + Γ(k_z0)]/(2jk_z0)</c> and fits Γ as a sum of exponentials in k_z0, each of which the
    /// Sommerfeld identity inverts to <c>e^{−jk₀R}/4πR</c>. For an interior source the same three
    /// steps work with k_z0 → k_zm and k₀ → k_m throughout — the identity itself is unchanged for a
    /// complex wavenumber — but <b>k_m is complex</b> wherever the source region is lossy, which is
    /// why <see cref="SommerfeldIntegral.FreeSpace(Complex, Complex)"/> and the two complex-reference
    /// path helpers below had to exist.</para>
    ///
    /// <para><b>The two extracted closed forms are D3's, not L8a's.</b> The direct term and the
    /// quasi-static image are replaced by
    /// <see cref="LayeredSpectralGreens.AsymptoticAtHeights"/>'s pair — a coefficient on
    /// <c>e^{−jk_zmΔ}</c> and one on <c>e^{−jk_zmΣ_b}</c>, because a source sitting on an interior
    /// interface has TWO non-decaying terms rather than one. The mixed component's asymptote is not
    /// an exponential at all and is extracted with the log form instead.</para>
    ///
    /// <para><b>THE FAR-FIELD SUM RULE SURVIVES, AND IT IS STILL A THEOREM — for a different reason
    /// than L8a's, which is why it had to be measured rather than assumed.</b> L8a's rule
    /// <c>ΣA_i = −(1 + Γ(∞))</c> holds because <c>1 + Γ</c> vanishes identically at the branch point:
    /// there the kernel <c>(1+Γ)/(2jk_z0)</c> would otherwise have a POLE, and Γ^e → +1, Γ^h → −1
    /// deliver the zero that removes it. An interior source has no such identity available — but it
    /// does not need one. <b>Its kernel is simply FINITE at its own region's branch point</b> (the
    /// four-term bracket and the resonance denominator both vanish at <c>k_zm = 0</c> and their ratio
    /// is finite), so the numerator <c>M(k_zm) = 2j k_zm · K</c> vanishes there by inspection, and the
    /// spatial answer's uncancelled 1/ρ coefficient <c>C_dir + C_img + ΣA_i = M(0)</c> is zero for the
    /// same reason.</para>
    ///
    /// <para><b>This was the finding of M3, and it was found the expensive way.</b> The first version
    /// of this fit imposed no constraint, on the stated grounds that "there is no theorem here" — and
    /// the measurement said otherwise: the scaled error tracked <c>|C_dir + C_img + ΣA_i|</c> across
    /// every stack, component and pairing (MMIC's G_A^xx: total 1.9e-3, error 9.3e-5; FR-4's G_A^zz:
    /// total 77, error 4.1), which is precisely L8a's own M4 signature. <c>M(k_zm)</c> then measured
    /// as exactly O(k_zm) on every one of them — 24 cases, four decades of k_zm, dead linear.
    /// <b>Asserting the ABSENCE of a theorem needs the same evidence as asserting one.</b>
    /// <c>VerticalCurrentTests.T4_1</c> is that measurement, kept.</para>
    /// </summary>
    /// <summary>
    /// <b>R-via-4 / R-mom-17 — the interior and cross-region fit's own measured range, as a refusal.</b>
    /// <see cref="ValidatedRhoOverLambdaLayered"/> and its wording belong to the TOP-HALF-SPACE
    /// pairing and are untouched; that this constant is smaller is a measurement, not an inheritance.
    ///
    /// <para>Measured against <see cref="SommerfeldIntegral.EvaluateInterior"/> at 10 GHz on the four
    /// GROUNDED stacks, both interior pairings, all four components, ρ/λ ∈ [1e-3, 1] — the table is in
    /// <c>CLAUDE.md</c> §L9c. As a fraction of the free-space kernel:</para>
    /// <list type="bullet">
    ///   <item><b>G_A^xx, G_q and the mixed component: ≤ 1.9e-2</b>, i.e. L9b's own envelope for the
    ///     top-half-space pairing (≤ 1.6e-2) to within the same factor the general medium already
    ///     cost over L8a. <b>The cross-region pairing is NOT worse than the same-region one</b>, which
    ///     is the answer to the question §10.2's warning was about and is the opposite of what the
    ///     located branch point suggested.</item>
    ///   <item><b>G_A^zz is the outlier and is refused above <see cref="ValidatedRhoOverLambdaAtHeights"/>.</b>
    ///     It is ≤ 4.6e-3 on the MMIC two-level stack and reaches <b>14</b> on the 100 µm GaAs slab at
    ///     ρ/λ = 1 — and the mechanism is diagnosed rather than guessed: the winning depth set carries
    ///     Σ|A_i| = 1.1e9 with two images of 5.6e8 at depths comparable to ρ itself, whose
    ///     cancellation is exact on the sampled path and degrades as ρ walks into them (the error
    ///     grows like ρ⁴, from 1.3e-3 at ρ/λ = 0.1). Rejecting such candidates on their conditioning
    ///     was tried and measured WORSE; what it needs is a depth search that is not
    ///     Prony-on-two-fixed-paths, and the standard one (GPOF with an SVD truncation) is a general
    ///     complex eigensolver, which D8 declines.</item>
    /// </list>
    /// </summary>
    public const double ValidatedRhoOverLambdaAtHeights = 0.1;

    /// <summary>
    /// <b>How far L9c's Tier 5 measured the OTHER three interior components — the ones
    /// <see cref="ValidatedRhoOverLambdaAtHeights"/> is not about.</b>
    ///
    /// <para>G_A^xx, G_q and the mixed component are ≤ 1.9e-2 of the free-space kernel out to ρ/λ = 1
    /// on every grounded stack, both interior pairings — L9b's own envelope for the top-half-space
    /// pairing. <b>This is a MEASURED RANGE, not a refusal</b>, and it exists because scoping
    /// <see cref="ValidatedRhoOverLambdaAtHeights"/> to the via footprints (R-zz-1) would otherwise
    /// have left these three checked by nothing at all: the mixed block couples a via to EVERY
    /// horizontal basis, so its ρ genuinely spans the mesh.</para>
    ///
    /// <para>Past it there is simply no measurement. <c>PlanarSolve</c> says so in a note rather than
    /// refusing, for R-prt-13's own reason: reporting "unmeasured" is honest, and refusing on it
    /// would be inventing a limit instead of reporting one.</para>
    /// </summary>
    public const double ValidatedRhoOverLambdaInteriorHorizontal = 1.0;

    /// <inheritdoc cref="ValidatedRhoOverLambdaAtHeights"/>
    public static EmSuitability WithinValidatedRangeAtHeights(
        GreensKernel kernel, LayeredSpectralGreens g, double rhoOverLambda)
    {
        var structural = CanFit(g.Stack);
        if (!structural.Ok) return structural;

        if (g.Stack.Bottom.IsOpen)
            return EmSuitability.No(
                $"This stack is UNGROUNDED (open below) and the INTERIOR fit is not validated on one. " +
                $"Measured at 10 GHz on 0.5 mm alumina in air, the interior and cross-region pairings " +
                $"reach 9.2 and 6.7 times the free-space kernel by ρ/λ = 1 on G_A^xx and G_q, against " +
                $"≤ 2.1e-2 for the TOP-HALF-SPACE pairing on the same stack at the same frequency " +
                $"(L9b's R4_2). The top-half-space pairing is unaffected and is what " +
                $"WithinValidatedRangeLayered governs. A grounded stack of any thickness is unaffected " +
                $"by this refusal. SommerfeldIntegral.EvaluateInterior is accurate there and far too " +
                $"slow to fill a matrix with.");

        if (rhoOverLambda > ValidatedRhoOverLambdaAtHeights)
            return EmSuitability.No(
                $"The L9c interior/cross-region fit is validated to ρ/λ ≤ {ValidatedRhoOverLambdaAtHeights} " +
                $"— an ORDER OF MAGNITUDE tighter than the top-half-space pairing's " +
                $"{ValidatedRhoOverLambdaLayered}, and measured rather than inherited; this asks for " +
                $"{kernel} at ρ/λ = {rhoOverLambda:G4}. Inside it, G_A^xx, G_q and the mixed component " +
                $"are ≤ 1.9e-2 of the free-space kernel on every grounded stack — L9b's own envelope — " +
                $"and the CROSS-REGION pairing is no worse than the same-region one. Above it " +
                $"{nameof(GreensKernel.VerticalVectorPotential)} degrades sharply (14× on the 100 µm " +
                $"GaAs slab at ρ/λ = 1, from 1.3e-3 at ρ/λ = 0.1) because its fitted depth set becomes " +
                $"a near-cancelling pair at depths comparable to ρ. That is a property of " +
                $"Prony-on-two-fixed-paths, not of the kernel — SommerfeldIntegral.EvaluateInterior is " +
                $"accurate everywhere out there and far too slow to fill a matrix with, and carrying " +
                $"it properly needs a depth search this repository has declined to write (D8).");

        return EmSuitability.Yes;
    }

    public static DcimModel FitAtHeights(LayeredSpectralGreens g, GreensKernel kernel,
                                         double z, double zp, DcimSettings? settings = null)
    {
        var s = settings ?? DcimSettings.Default;
        var ok = SommerfeldIntegral.CanIntegrateInterior(g, z, zp);
        if (!ok.Ok) throw new ArgumentException(ok.Reason);

        var a  = g.AsymptoticAtHeights(kernel, z, zp);
        Complex km = a.ReferenceWavenumber;
        Complex kmSq = km * km;
        double regulator = a.IsMixedForm ? 1.0 / Complex.Abs(km) : 0.0;

        // ---- the poles, as residues of THIS height pair's kernel in w = k_ρ².
        var modes = g.SurfaceWaves.Modes;
        var poles = new List<SurfaceWaveTerm>();
        if (s.ExtractSurfaceWaves)
            foreach (var m in modes)
                poles.Add(new SurfaceWaveTerm(m.Name, m.KRho, ResidueAtHeights(g, kernel, m, modes, z, zp)));

        // ---- the numerator, in the source region's own k_zm.
        //
        // K = [C_dir e^{−jk_zmΔ} + C_img e^{−jk_zmΣ} + N(k_zm)]/(2j k_zm), so N is what is fitted —
        // the exact analogue of Γ − Γ(∞) in L8a's decomposition. PoleSum already returns a pole's
        // contribution in NUMERATOR units (it multiplies by 2j k_z0), so it needs no change beyond
        // being handed k_m² in place of k_top².
        Complex Numerator(Complex kzm)
        {
            Complex w = kmSq - kzm * kzm;
            Complex k = g.KernelAtHeights(kernel, Complex.Sqrt(w), z, zp);
            if (a.IsMixedForm)
            {
                if (a.ImageCoefficient != Complex.Zero)
                    k -= a.ImageCoefficient
                       * (Complex.Exp(-Complex.Sqrt(w) * a.ImageDepth)
                        - Complex.Exp(-Complex.Sqrt(w) * (a.ImageDepth + regulator)))
                       / (2.0 * Complex.ImaginaryOne * w);
                return 2.0 * Complex.ImaginaryOne * kzm * k - PoleSum(poles, kmSq, kzm);
            }
            return 2.0 * Complex.ImaginaryOne * kzm * k
                 - a.DirectCoefficient * Complex.Exp(-Complex.ImaginaryOne * kzm * a.DirectDepth)
                 - a.ImageCoefficient  * Complex.Exp(-Complex.ImaginaryOne * kzm * a.ImageDepth)
                 - PoleSum(poles, kmSq, kzm);
        }

        // ---- the same two-level path L8a's M3 measured into existence, in k_m rather than k₀.
        var (farKz,  farY)  = SamplePathAt(s.FarPathExtent, s.FarSamples, km, Numerator);
        var (farZ, farA, _) = Prony.Fit(farY, s.FarOrder, s.FitTolerance);
        var farDepths       = DepthsAt(farZ, s.FarPathExtent, s.FarSamples, km);

        var (nearKz, nearY) = SamplePathAt(s.PathExtent, s.Samples, km, Numerator);

        var farAmp = new Complex[farDepths.Count];
        for (int j = 0; j < farDepths.Count; j++)
            farAmp[j] = farA[j] * Complex.Exp(Complex.ImaginaryOne * farDepths[j] * km);

        var nearResidual = new Complex[nearY.Length];
        for (int i = 0; i < nearY.Length; i++)
        {
            Complex level1 = Complex.Zero;
            for (int j = 0; j < farDepths.Count; j++)
                level1 += farAmp[j] * Complex.Exp(-Complex.ImaginaryOne * nearKz[i] * farDepths[j]);
            nearResidual[i] = nearY[i] - level1;
        }

        var (nearZ,  _, _) = Prony.Fit(nearResidual, s.MaxOrder, s.FitTolerance);
        var (plainZ, _, _) = Prony.Fit(nearY,        s.MaxOrder, s.FitTolerance);

        var twoLevel = new List<Complex>(farDepths);
        twoLevel.AddRange(DepthsAt(nearZ, s.PathExtent, s.Samples, km));

        double scale = 1.0 / Complex.Abs(km);
        var candidates = new List<List<Complex>>
        {
            DeduplicateAt(twoLevel, scale),
            DeduplicateAt(DepthsAt(plainZ, s.PathExtent, s.Samples, km), scale),
            DeduplicateAt(farDepths, scale),
        };

        List<ComplexImage> images = [];
        double bestResidual = double.PositiveInfinity;
        Complex sumRule = Complex.Zero;
        Complex[][] kzBlocks = [farKz, nearKz];
        Complex[][] yBlocks  = [farY,  nearY];

        // The sum rule, imposed EXACTLY (order 1 — L8a's measured default, and the only order here
        // that is a theorem rather than a knob). N(0) = M(0) − C_dir − C_img − PoleSum(0), and both
        // M(0) and PoleSum(0) are zero, so the constraint is ΣA_i = −(C_dir + C_img) — or ΣA_i = 0
        // for the mixed component, whose two extracted pieces are the same closed form.
        Complex nAtZero = a.IsMixedForm ? Complex.Zero : -(a.DirectCoefficient + a.ImageCoefficient);
        Complex[] taylor = [nAtZero, Complex.Zero, Complex.Zero];

        // AN AMPLITUDE-CONDITIONING CAP WAS BUILT HERE AND THEN REMOVED, and the negative result is
        // worth keeping. The failing candidate on GaAs's G_A^zz carries Σ|A_i| = 1.1e9 with two
        // images of 5.6e8 at depths of 8.9 cm and 16.9 cm — depths COMPARABLE TO ρ, whose
        // cancellation is exact on the sampled path and degrades as ρ walks into them, so the spatial
        // error grows like ρ⁴. Rejecting such candidates on Σ|A_i| against the data they fit is the
        // obvious remedy and **it measured WORSE on balance**: at a cap of 1e4 the GaAs low–low
        // G_A^zz error went 14 → 39 (the rejected candidate was the better one spatially despite its
        // conditioning), and at 1e2 every candidate on every stack was rejected. The conditioning is a
        // real diagnosis and a bad selector. What the outlier needs is a depth-set search that is not
        // Prony-on-two-fixed-paths — GPOF with an SVD rank truncation is the standard answer and it
        // is a general complex eigensolver, which D8 declines. **Left as the measured limit below.**
        foreach (var cand in candidates)
        {
            var (im, res, rule) = FitAmplitudes(cand, kzBlocks, yBlocks, taylor, 1);
            if (!(res < bestResidual)) continue;
            bestResidual = res; images = im; sumRule = rule;
        }

        return new DcimModel(g, km, kernel, a, regulator, z, zp, poles, images, bestResidual, sumRule, s);
    }

    /// <summary><see cref="SamplePath"/> with a COMPLEX reference wavenumber. Kept separate rather
    /// than widening the shipped one: promoting a real k₀ to a <c>Complex</c> multiply re-associates
    /// the arithmetic, and R-dcm-1/R-via-1 pin the one-layer fit at exact equality.</summary>
    private static (Complex[] Kz, Complex[] Y) SamplePathAt(double t0, int n, Complex km,
                                                            Func<Complex, Complex> numerator)
    {
        var kz = new Complex[n];
        var y  = new Complex[n];
        double dt = t0 / (n - 1);
        for (int i = 0; i < n; i++)
        {
            double t = i * dt;
            kz[i] = km * new Complex(1.0 - t / t0, -t);
            y[i]  = numerator(kz[i]);
        }
        return (kz, y);
    }

    /// <inheritdoc cref="Depths"/>
    private static List<Complex> DepthsAt(Complex[] z, double t0, int n, Complex km)
    {
        double dt = t0 / (n - 1);
        Complex slope = km * (Complex.ImaginaryOne / t0 - 1.0);
        var depths = new List<Complex>(z.Length);
        foreach (var zi in z)
        {
            Complex b = Complex.Log(zi) / dt / slope;
            if (double.IsFinite(b.Real) && double.IsFinite(b.Imaginary)) depths.Add(b);
        }
        return depths;
    }

    /// <inheritdoc cref="Deduplicate"/>
    private static List<Complex> DeduplicateAt(List<Complex> depths, double scale)
    {
        var kept = new List<Complex>(depths.Count);
        foreach (var b in depths)
        {
            bool dup = false;
            foreach (var q in kept)
                if ((b - q).Magnitude <= 1e-4 * Math.Max(scale, Math.Max(b.Magnitude, q.Magnitude)))
                { dup = true; break; }
            if (!dup) kept.Add(b);
        }
        return kept;
    }

    /// <summary>D4's residue, for a kernel component at a fixed height pair rather than for the top
    /// interface's reflected kernel. Same contour rule, same reason it is a contour rather than a
    /// derivative.</summary>
    private static Complex ResidueAtHeights(LayeredSpectralGreens g, GreensKernel kernel,
                                            LayeredSurfaceWaveMode m,
                                            IReadOnlyList<LayeredSurfaceWaveMode> allModes,
                                            double z, double zp)
    {
        Complex wp = m.KRho * m.KRho;

        double gap = double.PositiveInfinity;
        for (int r = 0; r < g.Stack.RegionCount; r++)
            gap = Math.Min(gap, (g.RegionWavenumberSquared(r) - wp).Magnitude);
        foreach (var q in allModes)
        {
            if (ReferenceEquals(q, m)) continue;
            gap = Math.Min(gap, (wp - q.KRho * q.KRho).Magnitude);
        }

        double radius = Math.Min(0.05 * gap, 0.05 * wp.Magnitude);
        if (!(radius > 0)) radius = 1e-8 * wp.Magnitude;

        return ContourAverage(kRho => g.KernelAtHeights(kernel, kRho, z, zp), wp, radius);
    }

    private static DcimModel FitCore(Func<Complex, Complex> reflectionAtKz0, double k0, Complex topKSq,
                                     double referenceHeightM, Complex kInfinity,
                                     List<SurfaceWaveTerm> poles, DcimSettings s,
                                     SpectralGreens? slabGreens, LayeredSpectralGreens? layeredGreens,
                                     GreensKernel kernel)
    {
        // ---- 3. what is left, sampled along the DCIM path and fitted.
        //
        // The path is linear in t, which is what makes e^{−j k_z0 b} a GEOMETRIC sequence in the
        // sample index and therefore fittable by Prony at all:
        //     k_z0(t) = k₀[(1 − t/T₀) − j t],  t ∈ [0, T₀]
        // t = 0 is k_ρ = 0; t = T₀ is k_ρ ≈ k₀T₀. Im k_z0 ≤ 0 all the way along, so it is on the
        // proper sheet by construction and matches what SpectralGreens would compute from k_ρ.
        Complex Remainder(Complex kz0) =>
            reflectionAtKz0(kz0) - kInfinity - PoleSum(poles, topKSq, kz0);

        // ---- TWO LEVELS, and M3's measurement is why (see FitAmplitudes for the other half of
        // that story). One path cannot resolve both regimes: with T₀ = 300 and 512 samples the
        // step is Δt = 0.59, while the whole small-k_ρ region that governs the FAR field lives at
        // t ≲ 0.5 — so the single-level fit had literally one sample interval covering it, and
        // interpolated across the entire far-field physics. Level 1 walks a short path (k_ρ out to
        // a few k₀) and picks up the large-depth images; level 2 subtracts those and walks the long
        // path for the small-depth, near-field ones.
        var (farKz,  farY)  = SamplePath(s.FarPathExtent, s.FarSamples, k0, Remainder);
        var (farZ, farA, _) = Prony.Fit(farY, s.FarOrder, s.FitTolerance);
        var farDepths       = Depths(farZ, s.FarPathExtent, s.FarSamples, k0);

        var (nearKz, nearY) = SamplePath(s.PathExtent, s.Samples, k0, Remainder);

        // Level 1's contribution at the near-path samples, expressed directly in the (A, b) basis
        // the model actually uses, so the two levels compose without a change of variables.
        var farAmp = new Complex[farDepths.Count];
        for (int j = 0; j < farDepths.Count; j++)
            farAmp[j] = farA[j] * Complex.Exp(Complex.ImaginaryOne * farDepths[j] * k0);

        var nearResidual = new Complex[nearY.Length];
        for (int i = 0; i < nearY.Length; i++)
        {
            Complex level1 = Complex.Zero;
            for (int j = 0; j < farDepths.Count; j++)
                level1 += farAmp[j] * Complex.Exp(-Complex.ImaginaryOne * nearKz[i] * farDepths[j]);
            nearResidual[i] = nearY[i] - level1;
        }

        var (nearZ,  _, _) = Prony.Fit(nearResidual, s.MaxOrder, s.FitTolerance);
        var (plainZ, _, _) = Prony.Fit(nearY,        s.MaxOrder, s.FitTolerance);

        var twoLevel = new List<Complex>(farDepths);
        twoLevel.AddRange(Depths(nearZ, s.PathExtent, s.Samples, k0));

        // Three candidate depth SETS, scored by the same measured residual and the best one kept.
        //
        // Two levels are not unconditionally better and the measurement says so: on FR-4 the
        // composed set cut the worst far-field error from 3.8e-2 to 4.9e-3, while on the
        // electrically-thin GaAs slab the two levels produce near-duplicate depths, the combined
        // design matrix goes rank deficient, and the amplitudes come back as enormous cancelling
        // numbers. Choosing by residual is what stops a "better" method from being applied where it
        // is worse — and the alternative, picking one scheme and asserting it, is exactly the kind
        // of thing this area has been burned by before.
        var candidates = new List<List<Complex>>
        {
            Deduplicate(twoLevel, k0),
            Deduplicate(Depths(plainZ, s.PathExtent, s.Samples, k0), k0),
            Deduplicate(farDepths, k0),
        };

        List<ComplexImage> images = [];
        double bestResidual = double.PositiveInfinity;
        Complex sumRule = Complex.Zero;

        var taylor = BranchPointTaylor(Remainder, k0);

        // A third sample block straddling k_z0 = 0 on the real axis — the branch point, which is
        // where the far field comes from and which NEITHER path visits. Fitting it by least squares
        // rather than pinning Taylor coefficients exactly is the difference between informing the
        // fit and fighting it: see the measured comparison in CLAUDE.md.
        var branchKz = new Complex[Math.Max(0, s.BranchSamples)];
        var branchY  = new Complex[branchKz.Length];
        for (int i = 0; i < branchKz.Length; i++)
        {
            double u = branchKz.Length == 1 ? 0 : -1.0 + 2.0 * i / (branchKz.Length - 1);
            branchKz[i] = u * s.BranchExtent * k0;
            branchY[i]  = Remainder(branchKz[i]);
        }

        Complex[][] kzBlocks = branchKz.Length > 0 ? [farKz, nearKz, branchKz] : [farKz, nearKz];
        Complex[][] yBlocks  = branchKz.Length > 0 ? [farY,  nearY,  branchY]  : [farY,  nearY];

        foreach (var cand in candidates)
        {
            var (im, res, rule) = FitAmplitudes(cand, kzBlocks, yBlocks,
                                                taylor, s.BranchPointOrders);
            if (!(res < bestResidual)) continue;
            bestResidual = res; images = im; sumRule = rule;
        }

        return new DcimModel(slabGreens, layeredGreens, k0, topKSq, referenceHeightM, kernel,
                             kInfinity, poles, images, bestResidual, sumRule, s);
    }

    /// <summary>
    /// Drop depths that duplicate one already kept. Two levels fitted independently will sometimes
    /// find the same exponential twice, and a design matrix with two near-identical columns is
    /// rank deficient — which does not fail loudly, it returns two enormous amplitudes that cancel
    /// to the right answer on the samples and to noise everywhere else.
    /// </summary>
    private static List<Complex> Deduplicate(List<Complex> depths, double k0)
    {
        var kept = new List<Complex>(depths.Count);
        double scale = 1.0 / k0;                       // one free-space radian, as a length
        foreach (var b in depths)
        {
            bool dup = false;
            foreach (var q in kept)
                if ((b - q).Magnitude <= 1e-4 * Math.Max(scale, Math.Max(b.Magnitude, q.Magnitude)))
                { dup = true; break; }
            if (!dup) kept.Add(b);
        }
        return kept;
    }

    /// <summary>
    /// Sample the remainder along <c>k_z0(t) = k₀[(1 − t/T) − j t]</c>, t ∈ [0, T]. Linear in t is
    /// what makes <c>e^{−j k_z0 b}</c> a GEOMETRIC sequence in the sample index and therefore
    /// fittable by Prony at all. Im k_z0 ≤ 0 the whole way, so the path is on the proper sheet by
    /// construction and agrees with what <see cref="SpectralGreens"/> computes from k_ρ.
    /// </summary>
    private static (Complex[] Kz, Complex[] Y) SamplePath(double t0, int n, double k0,
                                                          Func<Complex, Complex> remainder)
    {
        var kz = new Complex[n];
        var y  = new Complex[n];
        double dt = t0 / (n - 1);
        for (int i = 0; i < n; i++)
        {
            double t = i * dt;
            kz[i] = k0 * new Complex(1.0 - t / t0, -t);
            y[i]  = remainder(kz[i]);
        }
        return (kz, y);
    }

    /// <summary>
    /// Prony returns z_i = e^{s_iΔt}; the path's linearity turns those into complex image depths:
    /// <c>−j k_z0 b = −j b k₀ + b k₀ t (j/T₀ − 1)</c>, so <c>s = b k₀ (j/T₀ − 1)</c>.
    /// </summary>
    private static List<Complex> Depths(Complex[] z, double t0, int n, double k0)
    {
        double dt = t0 / (n - 1);
        Complex slope = k0 * (Complex.ImaginaryOne / t0 - 1.0);
        var depths = new List<Complex>(z.Length);
        foreach (var zi in z)
        {
            Complex b = Complex.Log(zi) / dt / slope;
            if (double.IsFinite(b.Real) && double.IsFinite(b.Imaginary)) depths.Add(b);
        }
        return depths;
    }

    /// <summary>
    /// Solve for the image amplitudes subject to <b>the branch-point constraints</b>
    /// <c>Σ A_i (−j b_i)^k = F^{(k)}(0)</c>, k = 0, 1, 2 — i.e. the model's own Taylor expansion at
    /// k_z0 = 0 is forced to agree with the true remainder's to second order.
    ///
    /// <para><b>This is M4, and M3's measurement is what called for it.</b> One-level DCIM with a
    /// free amplitude solve is excellent in the near and intermediate field (~1e-6 relative out to
    /// ρ ≈ λ/100) and then falls apart: on GaAs it reached <b>187% error at ρ/λ = 10</b>. The cause
    /// is exact, and worth stating because it looks like a fitting problem and is not.</para>
    ///
    /// <para><b>k = 0 is the far-field sum rule.</b> The total spectral numerator <c>1 + Γ(k_ρ)</c>
    /// vanishes identically at the branch point k_ρ = k₀ — Γ^e → +1 and Γ^h → −1 there, so both
    /// 1 + Γ^q and 1 + Γ^h are zero. That zero is not decoration:
    /// <c>e^{−jk₀R_i}/4πR_i → e^{−jk₀ρ}/4πρ</c> for every image as ρ → ∞, so the coefficient of the
    /// 1/ρ far field is exactly <c>(1 + Γ(∞)) + Σ A_i</c>, while the physical far field (a surface
    /// wave in 1/√ρ, a lateral wave in 1/ρ²) has <b>no 1/ρ term at all</b>. The sampling path never
    /// passes through k_z0 = 0, so an unconstrained fit only <i>extrapolates</i> that cancellation,
    /// and whatever it gets wrong survives as an uncancelled 1/ρ that eventually dwarfs an answer
    /// which has by then decayed to 1/ρ². On GaAs at 10 GHz the true G_q at ρ = 10λ is 167× smaller
    /// than the leading term it has to cancel against, which is why that substrate showed the
    /// failure first and worst.</para>
    ///
    /// <para><b>k = 1 and 2 continue the same idea one and two orders further</b>, which is what
    /// reaches the 1/ρ² lateral wave: the image sum's own large-ρ expansion is
    /// <c>(e^{−jk₀ρ}/4πρ)[ΣA − (jk₀/2ρ)ΣAb² + …]</c>, so the second moment is the 1/ρ² amplitude and
    /// leaving it free leaves the far field free. Note k = 1's right-hand side is NOT zero once the
    /// surface-wave poles have been subtracted: the subtracted term carries a factor k_z0, which
    /// contributes <c>−2j Σ R_p/(k₀² − k_p²)</c> to F′(0). Taking the coefficients numerically
    /// rather than analytically is what keeps that automatically consistent.</para>
    ///
    /// <para>They are imposed <b>exactly</b>, by eliminating three amplitudes rather than by adding
    /// weighted rows — a weighted constraint still leaves a residual 1/ρ, which is the entire thing
    /// being removed. The three eliminated columns are chosen by maximising the determinant of the
    /// 3×3 constraint block over all triples, because that block is a Vandermonde in (−j b) and a
    /// careless choice of three similar depths makes it singular.</para>
    /// </summary>
    private static (List<ComplexImage> Images, double Residual, Complex SumRule) FitAmplitudes(
        List<Complex> depths, Complex[][] kzBlocks, Complex[][] yBlocks, Complex[] taylor, int orders)
    {
        int m = depths.Count;
        var images = new List<ComplexImage>(m);
        if (m == 0) return (images, double.PositiveInfinity, Complex.Zero);

        // The two sampling paths differ in magnitude by orders of magnitude, so each block is
        // normalised by its own RMS before they are stacked. Without it the long path — which
        // carries the big near-field values — simply outvotes the short one, and the far field goes
        // back to being fitted by accident.
        int n = 0;
        foreach (var blk in yBlocks) n += blk.Length;
        var kz = new Complex[n];
        var y  = new Complex[n];
        var w  = new double[n];
        int at = 0;
        for (int b = 0; b < yBlocks.Length; b++)
        {
            double rms = 0;
            foreach (var v in yBlocks[b]) rms += v.Magnitude * v.Magnitude;
            rms = Math.Sqrt(rms / Math.Max(1, yBlocks[b].Length));
            double weight = rms > 0 ? 1.0 / rms : 1.0;
            for (int i = 0; i < yBlocks[b].Length; i++, at++)
            {
                kz[at] = kzBlocks[b][i];
                y[at]  = yBlocks[b][i];
                w[at]  = weight;
            }
        }

        // φ_i(t_n) = e^{−j k_z0(t_n) b_i}
        var phi = new Complex[n, m];
        for (int i = 0; i < m; i++)
        for (int r = 0; r < n; r++)
            phi[r, i] = Complex.Exp(-Complex.ImaginaryOne * kz[r] * depths[i]);

        // ---- the branch-point constraints:  Σ A_i (−j b_i)^k = F^{(k)}(0),  k = 0 … orders−1.
        int nc = Math.Clamp(orders, 0, Math.Min(taylor.Length, m));
        var cRow = new Complex[nc, m];
        var cRhs = new Complex[nc];
        for (int k = 0; k < nc; k++)
        {
            cRhs[k] = taylor[k];
            for (int i = 0; i < m; i++)
                cRow[k, i] = Complex.Pow(-Complex.ImaginaryOne * depths[i], k);
        }

        // Row-scale the constraint block. Its rows are 1, b, b² over depths that routinely span six
        // orders of magnitude — the far-field images are enormous next to the near-field ones — so
        // an unscaled |det| pivot search is really a search for "whichever three have the biggest
        // b", and the elimination that follows is built on an ill-conditioned 3×3.
        for (int k = 0; k < nc; k++)
        {
            double rowMax = 0;
            for (int i = 0; i < m; i++) rowMax = Math.Max(rowMax, cRow[k, i].Magnitude);
            if (!(rowMax > 0)) continue;
            for (int i = 0; i < m; i++) cRow[k, i] /= rowMax;
            cRhs[k] /= rowMax;
        }

        Complex[]? amp = nc == 0 ? null : ConstrainedSolve(phi, y, w, cRow, cRhs, n, m, nc);

        if (amp is null)
        {
            // Unconstrained fallback. Reached only when the constrained system is rank deficient;
            // reported through the sum-rule residual rather than hidden.
            var wphi = new Complex[n, m];
            var wy   = new Complex[n];
            for (int r = 0; r < n; r++)
            {
                for (int i = 0; i < m; i++) wphi[r, i] = w[r] * phi[r, i];
                wy[r] = w[r] * y[r];
            }
            if (!LinearAlgebra.LeastSquares(wphi, wy, out amp))
                return (images, double.PositiveInfinity, Complex.Zero);
        }

        double err = 0, scale = 0;
        Complex total = Complex.Zero;
        for (int r = 0; r < n; r++)
        {
            Complex model = Complex.Zero;
            for (int i = 0; i < m; i++) model += amp[i] * phi[r, i];
            double d2 = (model - y[r]).Magnitude * w[r];
            err += d2 * d2;
            scale += (y[r] * w[r]).Magnitude * (y[r] * w[r]).Magnitude;
        }

        for (int i = 0; i < m; i++)
        {
            total += amp[i];
            if (double.IsFinite(amp[i].Real) && double.IsFinite(amp[i].Imaginary))
                images.Add(new ComplexImage(amp[i], depths[i]));
        }

        return (images, Math.Sqrt(err / Math.Max(scale, 1e-300)), total - taylor[0]);
    }

    /// <summary>
    /// Weighted least squares subject to the exact equalities <c>C·A = d</c>, by eliminating
    /// <paramref name="nc"/> amplitudes:
    /// <c>A_P = C_P⁻¹(d − C_F A_F)</c> ⇒ <c>[Φ_F − Φ_P C_P⁻¹C_F] A_F = y − Φ_P C_P⁻¹ d</c>.
    ///
    /// <para>The pivot columns are chosen by maximising |det C_P| over <b>all</b> triples. C is a
    /// Vandermonde in (−j b), so three depths of similar magnitude make C_P singular and the
    /// elimination then manufactures the enormous cancelling amplitudes it exists to avoid. With
    /// m ≤ 20 the exhaustive search is ~1000 3×3 determinants, which is free.</para>
    /// </summary>
    private static Complex[]? ConstrainedSolve(Complex[,] phi, Complex[] y, double[] w,
                                               Complex[,] c, Complex[] d, int n, int m, int nc)
    {
        if (nc > m) return null;
        var pivots = BestPivots(c, m, nc);
        if (pivots is null) return null;

        var free = new List<int>(m - nc);
        for (int i = 0; i < m; i++) if (Array.IndexOf(pivots, i) < 0) free.Add(i);

        // cInv = C_P⁻¹, by Gauss-Jordan on a small nc×nc system.
        var cp = new Complex[nc, nc];
        for (int r = 0; r < nc; r++)
        for (int k = 0; k < nc; k++) cp[r, k] = c[r, pivots[k]];
        var cInv = Invert(cp, nc);
        if (cInv is null) return null;

        // g = C_P⁻¹ d,  and  H = C_P⁻¹ C_F
        var gVec = new Complex[nc];
        for (int r = 0; r < nc; r++)
        {
            Complex sum = Complex.Zero;
            for (int k = 0; k < nc; k++) sum += cInv[r, k] * d[k];
            gVec[r] = sum;
        }
        var h = new Complex[nc, free.Count];
        for (int r = 0; r < nc; r++)
        for (int j = 0; j < free.Count; j++)
        {
            Complex sum = Complex.Zero;
            for (int k = 0; k < nc; k++) sum += cInv[r, k] * c[k, free[j]];
            h[r, j] = sum;
        }

        Complex[] amp = new Complex[m];
        if (free.Count == 0)
        {
            for (int r = 0; r < nc; r++) amp[pivots[r]] = gVec[r];
            return amp;
        }

        var design = new Complex[n, free.Count];
        var rhs    = new Complex[n];
        for (int r = 0; r < n; r++)
        {
            Complex offset = Complex.Zero;
            for (int k = 0; k < nc; k++) offset += phi[r, pivots[k]] * gVec[k];
            rhs[r] = w[r] * (y[r] - offset);
            for (int j = 0; j < free.Count; j++)
            {
                Complex col = phi[r, free[j]];
                for (int k = 0; k < nc; k++) col -= phi[r, pivots[k]] * h[k, j];
                design[r, j] = w[r] * col;
            }
        }

        if (!LinearAlgebra.LeastSquares(design, rhs, out var aFree)) return null;

        for (int j = 0; j < free.Count; j++) amp[free[j]] = aFree[j];
        for (int r = 0; r < nc; r++)
        {
            Complex sum = gVec[r];
            for (int j = 0; j < free.Count; j++) sum -= h[r, j] * aFree[j];
            amp[pivots[r]] = sum;
        }
        foreach (var v in amp) if (!double.IsFinite(v.Real) || !double.IsFinite(v.Imaginary)) return null;
        return amp;
    }

    private static int[]? BestPivots(Complex[,] c, int m, int nc)
    {
        var idx = new int[nc];
        int[]? best = null;
        double bestDet = 0;

        void Recurse(int depth, int start)
        {
            if (depth == nc)
            {
                var sub = new Complex[nc, nc];
                for (int r = 0; r < nc; r++)
                for (int k = 0; k < nc; k++) sub[r, k] = c[r, idx[k]];
                double det = Determinant(sub, nc).Magnitude;
                if (det > bestDet) { bestDet = det; best = (int[])idx.Clone(); }
                return;
            }
            for (int i = start; i < m; i++) { idx[depth] = i; Recurse(depth + 1, i + 1); }
        }

        Recurse(0, 0);
        return bestDet > 0 ? best : null;
    }

    private static Complex Determinant(Complex[,] a, int n)
    {
        var t = (Complex[,])a.Clone();
        Complex det = Complex.One;
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++) if (t[i, k].Magnitude > t[p, k].Magnitude) p = i;
            if (t[p, k] == Complex.Zero) return Complex.Zero;
            if (p != k)
            {
                for (int j = 0; j < n; j++) (t[k, j], t[p, j]) = (t[p, j], t[k, j]);
                det = -det;
            }
            det *= t[k, k];
            for (int i = k + 1; i < n; i++)
            {
                Complex f = t[i, k] / t[k, k];
                for (int j = k; j < n; j++) t[i, j] -= f * t[k, j];
            }
        }
        return det;
    }

    private static Complex[,]? Invert(Complex[,] a, int n)
    {
        var t = new Complex[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) t[i, j] = a[i, j];
            t[i, n + i] = Complex.One;
        }
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++) if (t[i, k].Magnitude > t[p, k].Magnitude) p = i;
            if (t[p, k].Magnitude == 0) return null;
            if (p != k) for (int j = 0; j < 2 * n; j++) (t[k, j], t[p, j]) = (t[p, j], t[k, j]);
            Complex piv = t[k, k];
            for (int j = 0; j < 2 * n; j++) t[k, j] /= piv;
            for (int i = 0; i < n; i++)
            {
                if (i == k) continue;
                Complex f = t[i, k];
                if (f == Complex.Zero) continue;
                for (int j = 0; j < 2 * n; j++) t[i, j] -= f * t[k, j];
            }
        }
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            inv[i, j] = t[i, n + j];
            if (!double.IsFinite(inv[i, j].Real) || !double.IsFinite(inv[i, j].Imaginary)) return null;
        }
        return inv;
    }

    /// <summary>Σ_p R_p/(k_ρ² − k_p²), scaled by 2j·k_z0 so it lives in the same units as Γ. N poles
    /// need no new algebra, which is why D4 is a typing question rather than an arithmetic one.</summary>
    private static Complex PoleSum(List<SurfaceWaveTerm> poles, Complex topKSq, Complex kz0)
    {
        if (poles.Count == 0) return Complex.Zero;
        Complex kRhoSq = topKSq - kz0 * kz0;
        Complex sum = Complex.Zero;
        foreach (var p in poles)
            sum += p.Residue / (kRhoSq - p.KRho * p.KRho);
        return 2.0 * Complex.ImaginaryOne * kz0 * sum;
    }

    /// <summary>
    /// The first three Taylor coefficients of the fitted remainder at the branch point k_z0 = 0,
    /// i.e. <c>F(0)</c>, <c>F′(0)</c>, <c>F″(0)</c>, by fourth-order central differences.
    ///
    /// <para><b>The branch point IS the far field, which is why these are worth pinning exactly.</b>
    /// The large-ρ behaviour of a Sommerfeld integral is governed by its integrand near k_ρ = k₀,
    /// and the DCIM sampling path never goes there — it runs from k_z0 = k₀ down into the lower
    /// half plane and never crosses zero. Matching the model's own expansion
    /// <c>Σ A_i e^{−jk_z0 b_i} = ΣA − jk_z0 ΣAb − ½k_z0² ΣAb² + …</c> to these three numbers turns
    /// the far field from an extrapolation into three exact linear constraints on the amplitudes.
    /// </para>
    ///
    /// <para>Evaluating at k_z0 &lt; 0 is a legitimate analytic continuation — the reflection
    /// coefficients are analytic in k_z0 near 0 (only k_ρ has a branch point there) — and it is the
    /// reason <see cref="SpectralGreens.ReflectionAtKz0"/> exists rather than the k_ρ-parameterised
    /// entry point, which cannot express a negative k_z0 at all.</para>
    /// </summary>
    private static Complex[] BranchPointTaylor(Func<Complex, Complex> remainder, double k0)
    {
        double d = 1e-3 * k0;                   // balances O(δ²) truncation against ε/δ² round-off
        Complex f0 = remainder(Complex.Zero);
        Complex fp1 = remainder(d),  fm1 = remainder(-d);
        Complex fp2 = remainder(2 * d), fm2 = remainder(-2 * d);

        Complex d1 = (8.0 * (fp1 - fm1) - (fp2 - fm2)) / (12.0 * d);
        Complex d2 = (16.0 * (fp1 + fm1) - (fp2 + fm2) - 30.0 * f0) / (12.0 * d * d);
        return [f0, d1, d2];
    }

    /// <summary>
    /// The residue of the reflected spectral kernel Γ/(2jk_z0) at w = k_p², taken as a circular
    /// contour average.
    ///
    /// <para>Not by differentiating the dispersion relation: the trapezoidal rule on a circle is
    /// spectrally accurate for an analytic function, needs nothing re-derived if the formulation
    /// ever changes, and — unlike a closed-form derivative — cannot silently disagree with the
    /// reflection coefficient it is supposed to be the residue OF. The radius is a fraction of the
    /// distance to the nearest other singularity (the branch point at k₀², and the slab wavenumber),
    /// so the circle encloses one pole and nothing else.</para>
    /// </summary>
    private static Complex Residue(SpectralGreens g, GreensKernel kernel, SurfaceWaveMode m)
    {
        Complex wp = m.KRho * m.KRho;
        double k0Sq = g.K0 * g.K0;
        double radius = 0.05 * Math.Min((wp - k0Sq).Magnitude, (g.K1 * g.K1 - wp).Magnitude);
        radius = Math.Min(radius, 0.05 * wp.Magnitude);
        if (!(radius > 0)) radius = 1e-8 * wp.Magnitude;

        return ContourAverage(kRho => g.ReflectedKernel(kernel, kRho), wp, radius);
    }

    /// <summary>
    /// <b>D4 — the same residue for a general stack, with the contour radius generalised.</b>
    ///
    /// <para>The one-layer radius is written against the slab — <c>0.05·min(|w_p − k₀²|, |K1² − w_p|)</c>
    /// — and neither term survives. A general stack has no single <c>K1</c>, so the distance to the
    /// nearest OTHER singularity is the minimum over <i>every</i> region's <c>|k_i² − w_p|</c> (which
    /// covers both branch points, the top half-space's <c>k₀²</c> and, on an open-below stack,
    /// <c>k_b²</c>) <b>and over every other pole</b> — because with more than one mode the nearest
    /// singularity is routinely the neighbouring pole rather than any wavenumber. L9a measured
    /// two poles on the ungrounded stack at every frequency in the band.</para>
    ///
    /// <para><b>Getting this wrong does not fail loudly.</b> A circle that encloses a second
    /// singularity returns a residue contaminated by it — a smooth, plausible, wrong far field, which
    /// is precisely the failure mode L8a's M4 burned a milestone on.</para>
    /// </summary>
    private static Complex Residue(LayeredSpectralGreens g, GreensKernel kernel,
                                   LayeredSurfaceWaveMode m,
                                   IReadOnlyList<LayeredSurfaceWaveMode> allModes)
    {
        Complex wp = m.KRho * m.KRho;

        double gap = double.PositiveInfinity;
        for (int r = 0; r < g.Stack.RegionCount; r++)
            gap = Math.Min(gap, (g.RegionWavenumberSquared(r) - wp).Magnitude);
        foreach (var q in allModes)
        {
            if (ReferenceEquals(q, m)) continue;
            gap = Math.Min(gap, (wp - q.KRho * q.KRho).Magnitude);
        }

        double radius = Math.Min(0.05 * gap, 0.05 * wp.Magnitude);
        if (!(radius > 0)) radius = 1e-8 * wp.Magnitude;

        return ContourAverage(kRho => g.TopReflectedKernel(kernel, kRho), wp, radius);
    }

    /// <summary>The trapezoidal contour average that both residue paths share: spectrally accurate
    /// for an analytic function, and it cannot silently disagree with the coefficient it is the
    /// residue OF, which a closed-form derivative can.</summary>
    private static Complex ContourAverage(Func<Complex, Complex> kernelAtKRho, Complex wp, double radius)
    {
        const int m0 = 64;
        Complex sum = Complex.Zero;
        for (int i = 0; i < m0; i++)
        {
            double theta = 2.0 * Math.PI * i / m0;
            Complex dw   = radius * Complex.Exp(Complex.ImaginaryOne * theta);
            Complex w    = wp + dw;
            Complex kRho = Complex.Sqrt(w);
            sum += kernelAtKRho(kRho) * dw;
        }
        return sum / m0;
    }
}

/// <summary>The fitted model, and everything needed to say how far to trust it.</summary>
public sealed class DcimModel
{
    /// <summary>The one-layer kernel this was fitted from, or <c>null</c> for a general stack.</summary>
    public SpectralGreens?        Greens        { get; }
    /// <summary>The general layered kernel this was fitted from, or <c>null</c> for a grounded slab.</summary>
    public LayeredSpectralGreens? LayeredGreens { get; }
    /// <summary>k₀ — free space, whichever kernel produced the fit.</summary>
    public double         K0          { get; }
    /// <summary>k² of the half-space the fit is referenced in: <c>k_ρ² = TopKSquared − k_z0²</c>.
    /// Needed at evaluation time only to put a surface-wave pole's own k_z0 on the proper sheet when
    /// the height pair shifts it (D5).</summary>
    public Complex        TopKSquared { get; }
    /// <summary>z of the interface the images are referenced to — the slab's top surface, or the
    /// stack's <c>TopZ</c>. A height pair is measured from here (D5).</summary>
    public double         ReferenceHeightM { get; }
    public GreensKernel   Kernel      { get; }
    /// <summary>Γ(∞) — the quasi-static image at zero depth, extracted in closed form.</summary>
    public Complex        QuasiStatic { get; }
    public IReadOnlyList<SurfaceWaveTerm> SurfaceWaves { get; }
    public IReadOnlyList<ComplexImage>    Images       { get; }
    /// <summary>How well the fitted exponentials reproduce the samples they were fitted to,
    /// relative to the sampled function's own RMS. This is a SPECTRAL-domain residual: it says the
    /// fit is faithful, not that the spatial answer is accurate outside the sampled k_ρ range.
    /// The two are different questions and only the oracle answers the second.</summary>
    public double         FitResidual { get; }
    /// <summary>
    /// The far-field sum-rule residual <c>(1 + Γ(∞)) + Σ A_i</c>, which must be zero for the
    /// spatial answer to have no 1/ρ far field. Zero by construction when
    /// <see cref="DcimSettings.EnforceSumRule"/> is on; surfaced anyway, because the one case where
    /// the constrained solve falls back to an unconstrained one is exactly the case worth noticing.
    /// </summary>
    public Complex        SumRuleResidual { get; }
    public DcimSettings   Settings    { get; }

    internal DcimModel(SpectralGreens? g, LayeredSpectralGreens? layered, double k0, Complex topKSq,
                       double referenceHeightM, GreensKernel kernel, Complex quasiStatic,
                       IReadOnlyList<SurfaceWaveTerm> surfaceWaves, IReadOnlyList<ComplexImage> images,
                       double fitResidual, Complex sumRuleResidual, DcimSettings settings)
    {
        Greens = g; LayeredGreens = layered; K0 = k0; TopKSquared = topKSq;
        ReferenceHeightM = referenceHeightM;
        Kernel = kernel; QuasiStatic = quasiStatic;
        SurfaceWaves = surfaceWaves; Images = images; FitResidual = fitResidual;
        SumRuleResidual = sumRuleResidual; Settings = settings;
        ReferenceK = k0;
    }

    // -------------------------------------------------------------------------------------------
    // L9c / M3 — the INTERIOR mode. One type rather than two, so PlanarKernelTerms, SingularExtraction
    // and the whole of PlanarFill keep consuming a DcimModel and do not have to learn about a second
    // one. The shipped path sets none of these and its Evaluate branch is untouched.
    // -------------------------------------------------------------------------------------------

    /// <summary>The wavenumber every image in this model is referenced to: k₀ for a top-half-space
    /// fit, the SOURCE REGION's own (complex, if lossy) k_m for an interior one.</summary>
    public Complex ReferenceK { get; }

    /// <summary>True when this model was produced by <see cref="Dcim.FitAtHeights"/>.</summary>
    public bool IsAtHeights { get; }

    /// <summary>The height pair this model was fitted at — meaningful only when
    /// <see cref="IsAtHeights"/>. An interior fit is for ONE pair; unlike L9b's D5 shift there is no
    /// re-use across heights, which is D4's whole point.</summary>
    public double ObserverZ { get; }
    /// <inheritdoc cref="ObserverZ"/>
    public double SourceZ { get; }

    private readonly LayeredSpectralGreens.InteriorAsymptote _asym;
    private readonly double _regulator;

    internal DcimModel(LayeredSpectralGreens layered, Complex km, GreensKernel kernel,
                       LayeredSpectralGreens.InteriorAsymptote asym, double regulator,
                       double z, double zp,
                       IReadOnlyList<SurfaceWaveTerm> surfaceWaves, IReadOnlyList<ComplexImage> images,
                       double fitResidual, Complex sumRuleResidual, DcimSettings settings)
    {
        LayeredGreens = layered; K0 = layered.K0; ReferenceK = km;
        TopKSquared = layered.TopWavenumberSquared;
        ReferenceHeightM = layered.Stack.TopZ;
        Kernel = kernel; QuasiStatic = asym.ImageCoefficient;
        SurfaceWaves = surfaceWaves; Images = images; FitResidual = fitResidual;
        SumRuleResidual = sumRuleResidual; Settings = settings;
        _asym = asym; _regulator = regulator;
        IsAtHeights = true; ObserverZ = z; SourceZ = zp;
    }

    /// <summary>
    /// The two extracted asymptotes as (coefficient, depth) pairs, for
    /// <see cref="PlanarKernelTerms.FromDcimAtHeights"/> to decide which of them is singular at
    /// ρ = 0. Empty for the mixed component, whose asymptote is a logarithm instead — see
    /// <see cref="MixedLogCoefficient"/>.
    /// </summary>
    public IReadOnlyList<(Complex Coefficient, double Depth)> AsymptotePieces =>
        !IsAtHeights || _asym.IsMixedForm
            ? []
            : [(_asym.DirectCoefficient, _asym.DirectDepth),
               (_asym.ImageCoefficient,  _asym.ImageDepth)];

    /// <summary>The mixed component's log-form asymptote, or zero. See
    /// <see cref="EvaluateAtHeights"/>.</summary>
    public Complex MixedLogCoefficient =>
        IsAtHeights && _asym.IsMixedForm ? _asym.ImageCoefficient : Complex.Zero;
    /// <summary>Σ_b — the near depth of the log form. Zero for a via foot, which is what makes it a
    /// genuine <c>−ln ρ</c>.</summary>
    public double MixedLogNearDepth => IsAtHeights ? _asym.ImageDepth : 0.0;
    /// <summary>Σ_b + the regulator — the far depth of the log form.</summary>
    public double MixedLogFarDepth  => IsAtHeights ? _asym.ImageDepth + _regulator : 0.0;

    /// <summary>
    /// <b>d/dρ of <see cref="EvaluateAtHeights"/>, in closed form — what the MIXED block needs.</b>
    ///
    /// <para>The ẑx̂ dyadic entry is <c>j ∂G/∂x = j G′(ρ)·(x − x′)/ρ</c>, so the fill never wants the
    /// mixed kernel itself; it wants its radial derivative. Differentiating the model term by term is
    /// exact and costs nothing — <c>d/dρ [e^{−jkR}/4πR] = −(1 + jkR) e^{−jkR}/(4πR²) · ρ/R</c>,
    /// <c>d/dρ H₀⁽²⁾(k_pρ) = −k_p H₁⁽²⁾(k_pρ)</c>, and the log form differentiates to
    /// <c>ρ/(√(p²+ρ²)(p+√(p²+ρ²)))</c> minus the same at q. <b>A finite difference here would be a
    /// second cancellation on top of a fit</b>, which is the shape this slice has already been caught
    /// by once.</para>
    /// </summary>
    public Complex DerivativeAtHeights(double rhoM)
    {
        if (!IsAtHeights)
            throw new InvalidOperationException("DerivativeAtHeights belongs to a Dcim.FitAtHeights model.");

        Complex value = Complex.Zero;

        if (_asym.IsMixedForm)
        {
            double p = _asym.ImageDepth, q = _asym.ImageDepth + _regulator;
            double rp = Math.Sqrt(p * p + rhoM * rhoM), rq = Math.Sqrt(q * q + rhoM * rhoM);
            value = _asym.ImageCoefficient / (4.0 * Math.PI * Complex.ImaginaryOne)
                  * (rhoM / (rq * (q + rq)) - rhoM / (rp * (p + rp)));
        }
        else
        {
            foreach (var (c, d) in AsymptotePieces)
            {
                if (c == Complex.Zero) continue;
                value += c * RadialDerivative(ReferenceK, Math.Sqrt(rhoM * rhoM + d * d), rhoM);
            }
        }

        foreach (var pw in SurfaceWaves)
            value += pw.Residue * (-0.25 * Complex.ImaginaryOne)
                   * (-pw.KRho) * Bessel.H12(pw.KRho * rhoM);

        foreach (var im in Images)
        {
            Complex r = Complex.Sqrt(rhoM * rhoM + im.Depth * im.Depth);
            if (r.Real < 0) r = -r;
            value += im.Amplitude * RadialDerivative(ReferenceK, r, rhoM);
        }
        return value;
    }

    /// <summary>d/dρ of <c>e^{−jkR}/4πR</c> with <c>R = √(ρ² + b²)</c>, i.e. <c>dR/dρ = ρ/R</c>.</summary>
    private static Complex RadialDerivative(Complex k, Complex r, double rho) =>
        -(Complex.One + Complex.ImaginaryOne * k * r) * Complex.Exp(-Complex.ImaginaryOne * k * r)
        / (4.0 * Math.PI * r * r) * (rho / r);

    /// <summary>
    /// <b>M3 — the spatial answer of an interior fit, at the height pair it was fitted at.</b> Every
    /// term is closed form and every one is referenced to <see cref="ReferenceK"/> rather than k₀:
    /// <list type="bullet">
    ///   <item>the two extracted asymptotes → <c>C e^{−jk_m R}/4πR</c> at <c>R = √(ρ² + Δ²)</c> and
    ///     <c>√(ρ² + Σ²)</c> — or, for the mixed component, the single LOGARITHM its <c>1/k_ρ²</c>
    ///     tail inverts to;</item>
    ///   <item>each pole → the same <c>H₀⁽²⁾(k_pρ)</c> as always, because a pole's inverse transform
    ///     is in k_ρ and does not care which region the fit is referenced in;</item>
    ///   <item>each fitted image → <c>A_i e^{−jk_m R_i}/4πR_i</c>.</item>
    /// </list>
    /// </summary>
    public Complex EvaluateAtHeights(double rhoM)
    {
        if (!IsAtHeights)
            throw new InvalidOperationException(
                "This DcimModel was fitted for a source and observer in the TOP HALF-SPACE, where " +
                "the height pair is an exact shift of one fit (L9b's D5) — use Evaluate(ρ, z, z′). " +
                "EvaluateAtHeights belongs to a model from Dcim.FitAtHeights, which is fitted for one " +
                "interior or cross-region height pair and is not shiftable to another.");

        Complex value;
        if (_asym.IsMixedForm)
        {
            double p = _asym.ImageDepth, q = _asym.ImageDepth + _regulator;
            value = _asym.ImageCoefficient / (4.0 * Math.PI * Complex.ImaginaryOne)
                  * Complex.Log((q + Math.Sqrt(q * q + rhoM * rhoM)) /
                                (p + Math.Sqrt(p * p + rhoM * rhoM)));
        }
        else
        {
            value = _asym.DirectCoefficient
                  * SommerfeldIntegral.FreeSpace(ReferenceK,
                        Math.Sqrt(rhoM * rhoM + _asym.DirectDepth * _asym.DirectDepth))
                  + _asym.ImageCoefficient
                  * SommerfeldIntegral.FreeSpace(ReferenceK,
                        Math.Sqrt(rhoM * rhoM + _asym.ImageDepth * _asym.ImageDepth));
        }

        foreach (var p in SurfaceWaves)
            value += p.Residue * (-0.25 * Complex.ImaginaryOne) * Bessel.H02(p.KRho * rhoM);

        foreach (var im in Images)
        {
            Complex r = Complex.Sqrt(rhoM * rhoM + im.Depth * im.Depth);
            if (r.Real < 0) r = -r;
            value += im.Amplitude * SommerfeldIntegral.FreeSpace(ReferenceK, r);
        }
        return value;
    }

    /// <summary>The spatial-domain Green's function at lateral separation ρ — all closed form.</summary>
    public Complex Evaluate(double rhoM)
    {
        double k0 = K0;

        // Free space plus the quasi-static image at zero depth.
        Complex value = (1.0 + QuasiStatic) * SommerfeldIntegral.FreeSpace(k0, rhoM);

        // Each surface-wave pole: (1/2π)∫₀^∞ k J₀(kρ)/(k² − k_p²) dk = −(j/4)H₀⁽²⁾(k_pρ), i.e. the
        // 2-D outgoing cylindrical wave the mode physically is. This is the term that carries the
        // answer at several wavelengths, where the images have long since died.
        foreach (var p in SurfaceWaves)
            value += p.Residue * (-0.25 * Complex.ImaginaryOne) * Bessel.H02(p.KRho * rhoM);

        // The fitted images.
        foreach (var im in Images)
        {
            Complex r = Complex.Sqrt(rhoM * rhoM + im.Depth * im.Depth);
            if (r.Real < 0) r = -r;                     // the decaying branch: e^{−jk₀R} must shrink
            value += im.Amplitude * SommerfeldIntegral.FreeSpace(k0, r);
        }

        return value;
    }

    /// <summary>
    /// <b>D5 — the same model at an arbitrary source and observer height in the top half-space, and
    /// it needs NO REFIT.</b> This is the finding of L9b's M4 rather than a convenience overload.
    ///
    /// <para>In the top half-space the spectral kernel is exactly</para>
    /// <code>
    ///   G̃(k_ρ; z, z′) = [ e^{−j k_z0 Δ} + Γ(k_ρ) e^{−j k_z0 Σ} ] / (2j k_z0),
    ///                    Δ = |z − z′|,   Σ = z + z′ − 2H
    /// </code>
    /// <para>so substituting DCIM's own decomposition of Γ gives, term by term, four things that are
    /// every one of them closed form:</para>
    /// <list type="bullet">
    ///   <item>the direct term → <c>e^{−jk₀R}/4πR</c> with <c>R = √(ρ² + Δ²)</c> — exact, no fit;</item>
    ///   <item>the quasi-static constant → the same image, at depth <b>Σ</b>;</item>
    ///   <item>each fitted image <c>A_i e^{−jk_z0 b_i}</c> → an image at depth <b>b_i + Σ</b>, with
    ///     the SAME amplitude;</item>
    ///   <item>each pole term → the same <c>H₀⁽²⁾(k_pρ)</c> with its residue scaled by the constant
    ///     <c>e^{−j k_z0(k_p) Σ}</c>, because a residue only ever sees the integrand at its own pole.
    ///     k_z0(k_p) is <c>−jα</c> on the proper sheet, so that factor is the real decay
    ///     <c>e^{−αΣ}</c> — the surface wave dying away from the interface, which is what it should
    ///     be doing.</item>
    /// </list>
    ///
    /// <para><b>The far-field theorem survives the shift untouched.</b> Every image's amplitude is
    /// unchanged, so <c>Σ A_i = −(1 + Γ(∞))</c> still holds and the 1/ρ term still cancels — which
    /// matters, because that cancellation is the whole of L8a's M4.</para>
    ///
    /// <para><b>Consequence, stated plainly: "a fit per source/observer height pair" is wrong</b> for
    /// the case that covers every L8 geometry and the top level of a two-level stack. The two-variable
    /// fit §10.2 warns about is not needed here. Interior heights are a different question and are
    /// answered separately — see <c>CLAUDE.md</c> §L9b.</para>
    /// </summary>
    public Complex Evaluate(double rhoM, double z, double zp)
    {
        double h = ReferenceHeightM;
        if (z < h || zp < h)
            throw new ArgumentException(
                $"A DcimModel's images are referenced to the top half-space and need z, z′ ≥ H = " +
                $"{h:G6} m; got z = {z:G6}, z′ = {zp:G6}. A source INSIDE the stack is not an " +
                $"exact shift of this fit — its exponentials are in the source region's own k_zm, " +
                $"not in k_z0 — so it needs its own fit, which is what Dcim.FitAtHeights builds and " +
                $"what PlanarKernelSet.Get asks for: use that path, not this one.");

        double k0 = K0;
        double delta = Math.Abs(z - zp);
        double sigma = z + zp - 2 * h;

        Complex value = SommerfeldIntegral.FreeSpace(k0, Math.Sqrt(rhoM * rhoM + delta * delta))
                      + QuasiStatic *
                        SommerfeldIntegral.FreeSpace(k0, Math.Sqrt(rhoM * rhoM + sigma * sigma));

        foreach (var p in SurfaceWaves)
        {
            Complex kz = SpectralGreens.ProperRoot(TopKSquared - p.KRho * p.KRho);
            value += p.Residue * Complex.Exp(-Complex.ImaginaryOne * kz * sigma)
                   * (-0.25 * Complex.ImaginaryOne) * Bessel.H02(p.KRho * rhoM);
        }

        foreach (var im in Images)
        {
            Complex b = im.Depth + sigma;
            Complex r = Complex.Sqrt(rhoM * rhoM + b * b);
            if (r.Real < 0) r = -r;                     // the decaying branch, as above
            value += im.Amplitude * SommerfeldIntegral.FreeSpace(k0, r);
        }

        return value;
    }
}

/// <summary>
/// Prony's method: fit uniformly-sampled data as a sum of complex exponentials.
///
/// <para>Two steps, each with an independent way of being checked. <b>Linear prediction</b> — the
/// samples satisfy a constant-coefficient recurrence whose characteristic roots are the
/// exponentials — solved as an overdetermined least-squares by Householder QR. <b>Rooting</b> — the
/// characteristic polynomial, by Durand-Kerner, whose own residual |p(z_i)| is reported. The
/// amplitudes then come from a second least squares on the Vandermonde system.</para>
///
/// <para>The ORDER is raised one at a time until the residual meets tolerance rather than being
/// chosen up front. That is not just tidiness: an over-ordered Prony system is rank-deficient, and
/// a rank-deficient least squares does not fail loudly, it returns a plausible answer built out of
/// round-off.</para>
/// </summary>
internal static class Prony
{
    public static (Complex[] Z, Complex[] A, double Residual) Fit(Complex[] y, int maxOrder, double tol)
    {
        double scale = Rms(y);
        if (scale <= 0) return ([], [], 0);

        Complex[] bestZ = [], bestA = [];
        double bestResidual = double.PositiveInfinity;

        for (int m = 1; m <= Math.Min(maxOrder, (y.Length - 1) / 2); m++)
        {
            if (!TryOrder(y, m, out var z, out var a, out double residual)) continue;
            if (residual < bestResidual) { bestResidual = residual; bestZ = z; bestA = a; }
            if (bestResidual / scale <= tol) break;
        }

        return (bestZ, bestA, bestResidual / scale);
    }

    private static bool TryOrder(Complex[] y, int m, out Complex[] z, out Complex[] a, out double residual)
    {
        z = []; a = []; residual = double.PositiveInfinity;
        int rows = y.Length - m;
        if (rows < m) return false;

        // Linear prediction:  y[n+m] = −Σ_{k<m} c_k y[n+k]
        var pred = new Complex[rows, m];
        var rhs  = new Complex[rows];
        for (int r = 0; r < rows; r++)
        {
            for (int k = 0; k < m; k++) pred[r, k] = y[r + k];
            rhs[r] = -y[r + m];
        }
        if (!LinearAlgebra.LeastSquares(pred, rhs, out var c)) return false;

        // p(w) = w^m + Σ c_k w^k
        var poly = new Complex[m + 1];
        for (int k = 0; k < m; k++) poly[k] = c[k];
        poly[m] = Complex.One;
        z = DurandKerner(poly);

        // A root at the origin or outside the unit disc is not a decaying exponential along a path
        // on which the sampled function decays; it is a rooting artefact, and carrying it into a
        // Vandermonde of 500 rows overflows before the least squares can reject it.
        foreach (var zi in z)
            if (!double.IsFinite(zi.Real) || !double.IsFinite(zi.Imaginary) ||
                zi.Magnitude > 1.05 || zi.Magnitude < 1e-300) return false;

        // Amplitudes: y[n] = Σ a_i z_i^n, with the powers accumulated rather than re-exponentiated.
        var vand = new Complex[y.Length, m];
        var pow  = new Complex[m];
        for (int i = 0; i < m; i++) pow[i] = Complex.One;
        for (int r = 0; r < y.Length; r++)
            for (int i = 0; i < m; i++)
            {
                vand[r, i] = pow[i];
                pow[i] *= z[i];
            }
        if (!LinearAlgebra.LeastSquares(vand, y, out a)) return false;

        double sum = 0;
        for (int r = 0; r < y.Length; r++)
        {
            Complex model = Complex.Zero;
            for (int i = 0; i < m; i++) model += a[i] * vand[r, i];
            sum += (model - y[r]).Magnitude * (model - y[r]).Magnitude;
        }
        residual = Math.Sqrt(sum / y.Length);
        return double.IsFinite(residual);
    }

    /// <summary>
    /// Durand-Kerner (Weierstrass) simultaneous root finding for a monic complex polynomial. The
    /// starting points are the classic spiral (0.4 + 0.9j)^i, which is chosen precisely so that no
    /// two iterates coincide and no iterate sits on a real axis of symmetry.
    /// </summary>
    public static Complex[] DurandKerner(Complex[] monic)
    {
        int m = monic.Length - 1;
        var z = new Complex[m];
        var seed = new Complex(0.4, 0.9);
        z[0] = Complex.One;
        for (int i = 1; i < m; i++) z[i] = z[i - 1] * seed;

        for (int it = 0; it < 2000; it++)
        {
            double move = 0;
            for (int i = 0; i < m; i++)
            {
                Complex denom = Complex.One;
                for (int j = 0; j < m; j++) if (j != i) denom *= z[i] - z[j];
                if (denom == Complex.Zero) continue;
                Complex step = Evaluate(monic, z[i]) / denom;
                z[i] -= step;
                move = Math.Max(move, step.Magnitude);
            }
            if (move < 1e-15) break;
        }
        return z;
    }

    private static Complex Evaluate(Complex[] coeffs, Complex x)
    {
        Complex v = Complex.Zero;
        for (int i = coeffs.Length - 1; i >= 0; i--) v = v * x + coeffs[i];
        return v;
    }

    private static double Rms(Complex[] y)
    {
        double s = 0;
        foreach (var v in y) s += v.Magnitude * v.Magnitude;
        return Math.Sqrt(s / Math.Max(1, y.Length));
    }
}

/// <summary>
/// The two pieces of dense complex linear algebra this file needs, written here rather than reached
/// for: a Householder QR least-squares solve. NumFlat covers Hermitian and SVD cases; an
/// overdetermined complex least squares with a rank check is three dozen lines and keeps the
/// numerical behaviour of the DCIM fit under this file's own control.
/// </summary>
internal static class LinearAlgebra
{
    /// <summary>
    /// Minimise ‖Ax − b‖₂ by Householder QR. Returns false — rather than a plausible answer made of
    /// round-off — when A is numerically rank deficient, which is exactly what an over-ordered
    /// Prony system looks like and is the signal <see cref="Prony.Fit"/> uses to stop raising M.
    /// </summary>
    public static bool LeastSquares(Complex[,] a, Complex[] b, out Complex[] x)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        x = new Complex[n];
        if (m < n) return false;

        var r = (Complex[,])a.Clone();
        var y = (Complex[])b.Clone();

        double firstPivot = 0;
        for (int k = 0; k < n; k++)
        {
            // Householder vector for column k below the diagonal.
            double norm = 0;
            for (int i = k; i < m; i++) norm += r[i, k].Real * r[i, k].Real + r[i, k].Imaginary * r[i, k].Imaginary;
            norm = Math.Sqrt(norm);
            if (norm == 0) return false;

            Complex alpha = r[k, k];
            double amag = alpha.Magnitude;
            Complex phase = amag > 0 ? alpha / amag : Complex.One;
            Complex beta = -phase * norm;

            var v = new Complex[m];
            for (int i = k; i < m; i++) v[i] = r[i, k];
            v[k] -= beta;

            double vnorm2 = 0;
            for (int i = k; i < m; i++) vnorm2 += v[i].Real * v[i].Real + v[i].Imaginary * v[i].Imaginary;
            if (vnorm2 > 0)
            {
                for (int j = k; j < n; j++)
                {
                    Complex dot = Complex.Zero;
                    for (int i = k; i < m; i++) dot += Complex.Conjugate(v[i]) * r[i, j];
                    Complex f = 2.0 * dot / vnorm2;
                    for (int i = k; i < m; i++) r[i, j] -= f * v[i];
                }
                Complex dotb = Complex.Zero;
                for (int i = k; i < m; i++) dotb += Complex.Conjugate(v[i]) * y[i];
                Complex fb = 2.0 * dotb / vnorm2;
                for (int i = k; i < m; i++) y[i] -= fb * v[i];
            }

            double pivot = r[k, k].Magnitude;
            if (k == 0) firstPivot = pivot;
            if (pivot <= 1e-13 * firstPivot) return false;      // rank deficient: say so
        }

        for (int i = n - 1; i >= 0; i--)
        {
            Complex sum = y[i];
            for (int j = i + 1; j < n; j++) sum -= r[i, j] * x[j];
            x[i] = sum / r[i, i];
        }

        foreach (var xi in x) if (!double.IsFinite(xi.Real) || !double.IsFinite(xi.Imaginary)) return false;
        return true;
    }
}
