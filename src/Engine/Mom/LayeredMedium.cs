using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The stackup as kernel B's Green's function sees it: <b>one grounded dielectric slab, with the
/// metal on its top surface</b>.
///
/// <para><b>This is D2, and it is a real simplification rather than an arbitrary limit.</b> L8's
/// own phase-table content is "full-wave, single dielectric"; multiple metal levels, z-directed
/// current and vias are L9's. With one metal layer, source and observer are always at the
/// <i>same height</i> — z = z′ = h — so the Green's function is a function of lateral separation ρ
/// alone at each frequency, which collapses what would otherwise be a two-variable problem into a
/// one-variable one. It is enough for all three of L8's own gates: a quarter-wave open stub, a
/// bend, and a uniform line each need exactly one metal layer.</para>
///
/// <para>Per <b>R-mom-17</b>'s standing rule the limit is said out loud and anything else is
/// refused <i>by name</i>, with the phase the capability arrives in — see
/// <see cref="CanHost(int, double)"/> and <see cref="SpectralGreens.CanSolveAt"/>. Deleting a
/// refusal instead of narrowing it is how a kernel starts silently answering questions it cannot
/// answer.</para>
///
/// <para><b>Coordinates.</b> z = 0 is the ground plane, z = h the slab's top surface and the metal
/// plane, z &gt; h free space. R-mom-2 applies: metres, siemens/metre, radians, hertz, doubles.</para>
/// </summary>
public sealed record GroundedSlab(double HeightM, EmMaterial Material)
{
    /// <summary>FR-4 on the 1.6 mm starter stackup — the first of the two starter technologies.</summary>
    public static GroundedSlab Fr4Starter => new(1.6e-3, new EmMaterial(4.4, 0.02));

    /// <summary>100 µm GaAs — the second starter technology, and the harder case for DCIM.</summary>
    public static GroundedSlab GaAsStarter => new(100e-6, new EmMaterial(12.9, 0.002));

    /// <summary>ε* = εᵣ(1 − j·tanδ), the R-mom-6 convention, unchanged from kernel A.</summary>
    public Complex EpsComplex => Material.EpsComplex;

    /// <summary>
    /// The whole point of D2, stated as an assertion the caller can read: the metal plane is the
    /// slab's own top surface, so every source and every observer sits at this height.
    /// </summary>
    public double MetalHeightM => HeightM;

    /// <summary>
    /// The R-mom-17 refusal for anything this Green's function is not: more than one metal layer,
    /// or metal that is not on the slab's top surface.
    /// </summary>
    public static EmSuitability CanHost(int metalLayerCount, double metalHeightM, double slabHeightM)
    {
        if (metalLayerCount != 1)
            return EmSuitability.No(
                $"This is GroundedSlab — the ONE-SLAB, ONE-CONDUCTOR-LEVEL medium (D2) — and this " +
                $"stackup has {metalLayerCount} conductor layers. Multiple metal levels, z-directed " +
                $"current and vias are the GENERAL layered path: describe the medium as a LayerStack " +
                $"and solve it through LayeredSpectralGreens, which PlanarProblem.MediumStack selects " +
                $"automatically (PlanarProblem.RequiresGeneralKernel is the single place that choice " +
                $"is made).");

        if (Math.Abs(metalHeightM - slabHeightM) > 1e-12 * Math.Max(1, slabHeightM))
            return EmSuitability.No(
                $"This is GroundedSlab, which by construction places its one conductor layer on the " +
                $"TOP SURFACE of the slab (z = h = {slabHeightM:G6} m); this stackup puts it at " +
                $"z = {metalHeightM:G6} m. Buried and multi-level metal are the GENERAL layered " +
                $"path: give the medium as a LayerStack and set each PlanarConductorLayer's own ZM, " +
                $"and LayeredSpectralGreens takes any source and observer height.");

        return EmSuitability.Yes;
    }

    /// <summary>
    /// The validity floor. <b>R-lgf-6: the static limit is a genuinely different regime and is
    /// handled explicitly rather than extrapolated into.</b> Below this the wave terms are
    /// numerically indistinguishable from their ω → 0 forms and the DCIM path is refused by name,
    /// pointing at <see cref="StaticGreens"/>, which is exact there.
    ///
    /// <para>Expressed as a slab electrical thickness rather than a frequency, because "low
    /// frequency" only means anything relative to h and εᵣ. k₀h &lt; 1e-6 puts every wave
    /// correction below 1e-12 of the static answer.</para>
    /// </summary>
    public const double MinElectricalThickness = 1e-6;

    public double FreeSpaceWavenumberAt(double frequencyHz) =>
        2.0 * Math.PI * frequencyHz / EmConstants.C0;
}

// ===============================================================================================
// L9a — the GENERAL stratified medium.  N horizontal dielectric layers between two terminations.
//
// R-lyr-1.  The stack is a first-class type with an explicit termination at each end, and the
// ordering is stated ONCE, here, and never re-derived at a call site:
//
//   *** LAYERS ARE ORDERED BOTTOM-TO-TOP, AND z INCREASES UPWARD. ***
//
// z = 0 is the BOTTOM termination interface; Layers[0] sits directly on it; z = TopZ is the TOP
// termination interface.  That matches GroundedSlab exactly (z = 0 ground plane, z = h top
// surface, z > h free space), so the one-layer reduction that D5 gates on is a re-expression
// rather than a translation.
//
// REGION indexing, also stated once: region 0 is the bottom termination's half-space (or wall),
// regions 1..N are the finite layers (region i = Layers[i-1]), and region N+1 is the top
// termination's half-space (or wall).  Interfaces are indexed 0..N; interface i separates region
// i below from region i+1 above and sits at InterfaceZ[i].  A point exactly ON an interface
// belongs to the region ABOVE it — which is what puts a source at z = h in the air above a
// grounded slab, matching L8's D2.
// ===============================================================================================

/// <summary>What closes off one end of a <see cref="LayerStack"/>.</summary>
public enum TerminationKind
{
    /// <summary>Perfect electric conductor — a short on both equivalent lines (Γ = −1).</summary>
    Pec,
    /// <summary>Perfect magnetic conductor — an open on both lines (Γ = +1).</summary>
    Pmc,
    /// <summary>A semi-infinite half-space of stated material — the line is matched into it.</summary>
    HalfSpace,
}

/// <summary>
/// <b>D2 — arbitrary termination at each end, because the cascade makes it a one-line change.</b>
/// A grounded stack terminates below in a short; an open stack terminates in a half-space
/// impedance. Both are <i>a terminating reflection coefficient</i> in the same recursion, so
/// covering both costs nothing here.
///
/// <para><b>The consequence is NOT free and is stated rather than discovered later:</b> an
/// open-below stack introduces the bottom half-space's own wavenumber <c>k_b</c> as a SECOND
/// branch point of the spectrum. L8a's kernel has exactly one, at <c>k_ρ = k₀</c>, and that fact
/// is load-bearing for DCIM's branch-point constraint. Whether DCIM can fit a two-branch-cut
/// spectrum is <b>L9b's</b> question and must be measured, not assumed;
/// <c>PlanarExtractor</c>'s ungrounded-stack refusal stays in place until it answers.</para>
/// </summary>
public sealed record Termination
{
    public TerminationKind Kind     { get; }
    /// <summary>Only meaningful for <see cref="TerminationKind.HalfSpace"/>.</summary>
    public EmMaterial      Material { get; }

    private Termination(TerminationKind kind, EmMaterial material)
    {
        Kind     = kind;
        Material = material;
    }

    public static Termination Pec => new(TerminationKind.Pec, EmMaterial.Air);
    public static Termination Pmc => new(TerminationKind.Pmc, EmMaterial.Air);
    public static Termination OpenTo(EmMaterial material) => new(TerminationKind.HalfSpace, material);
    /// <summary>The ordinary open top: free space above the stack.</summary>
    public static Termination Air => OpenTo(EmMaterial.Air);

    public bool IsOpen => Kind == TerminationKind.HalfSpace;

    public override string ToString() => Kind switch
    {
        TerminationKind.Pec       => "PEC",
        TerminationKind.Pmc       => "PMC",
        _                         => $"half-space εᵣ={Material.EpsR:G4}",
    };
}

/// <summary>One finite-thickness horizontal layer. Metres, R-mom-2.</summary>
public sealed record MediumLayer(double ThicknessM, EmMaterial Material);

/// <summary>
/// <b>R-lyr-1 — the N-layer stack, bottom to top, with an explicit termination at each end.</b>
/// See the block comment above this type for the ordering and region-indexing conventions; they
/// are stated there once and are not repeated at any call site.
///
/// <para><b>"The general layered stack" means N HORIZONTAL layers.</b> A vertical or sloped
/// dielectric boundary is outside the 2.5D premise entirely, and no amount of layering reaches it —
/// it needs a genuinely 3-D formulation. <c>QuasiStaticKernel</c>'s sloped-boundary refusal says so
/// directly now (L9e's audit): it used to end <i>"A general dielectric stack arrives at L9"</i>,
/// which was true as written and read as a promise it never made.</para>
/// </summary>
public sealed class LayerStack
{
    public Termination                 Bottom { get; }
    public IReadOnlyList<MediumLayer>  Layers { get; }
    public Termination                 Top    { get; }

    /// <summary>z of interface i, i = 0..LayerCount. <c>InterfaceZ[0] == 0</c> by construction.</summary>
    public IReadOnlyList<double> InterfaceZ { get; }

    public int    LayerCount     => Layers.Count;
    public int    InterfaceCount => Layers.Count + 1;
    /// <summary>Regions 0 (bottom termination) .. LayerCount+1 (top termination).</summary>
    public int    RegionCount    => Layers.Count + 2;
    /// <summary>z of the TOP termination interface — the total stack thickness.</summary>
    public double TopZ           => InterfaceZ[^1];

    public LayerStack(Termination bottom, IReadOnlyList<MediumLayer> layers, Termination top)
    {
        var ok = CanRepresent(bottom, layers, top);
        if (!ok.Ok) throw new ArgumentException(ok.Reason);

        Bottom = bottom;
        Layers = layers.ToArray();
        Top    = top;

        var z = new double[Layers.Count + 1];
        for (int i = 0; i < Layers.Count; i++) z[i + 1] = z[i] + Layers[i].ThicknessM;
        InterfaceZ = z;
    }

    /// <summary>
    /// <b>R-lyr-8 — every refusal names the specific feature.</b> A stack this kernel cannot
    /// represent is refused by name, never returned as a NaN two phases later.
    /// </summary>
    public static EmSuitability CanRepresent(Termination bottom, IReadOnlyList<MediumLayer> layers,
                                             Termination top)
    {
        if (layers is null) return EmSuitability.No("The layer list is null; a stack needs a list (possibly empty).");

        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            if (!(l.ThicknessM > 0))
                return EmSuitability.No(
                    $"Layer {i} has thickness {l.ThicknessM} m. A ZERO-THICKNESS LAYER is not a layer — " +
                    $"it is an interface, and the cascade has no term for it. Remove the layer, or give " +
                    $"it a real thickness.");
            if (!(l.Material.EpsR >= 1.0))
                return EmSuitability.No(
                    $"Layer {i} has εᵣ = {l.Material.EpsR}; a passive dielectric has εᵣ ≥ 1.");
            if (!(l.Material.MuR > 0))
                return EmSuitability.No($"Layer {i} has µᵣ = {l.Material.MuR}; it must be positive.");
        }

        foreach (var (t, which) in new[] { (bottom, "bottom"), (top, "top") })
        {
            if (t is null) return EmSuitability.No($"The {which} termination is null.");
            if (t.Kind != TerminationKind.HalfSpace) continue;
            if (!(t.Material.EpsR >= 1.0))
                return EmSuitability.No(
                    $"The {which} termination is an open half-space with εᵣ = {t.Material.EpsR}; " +
                    $"a passive half-space has εᵣ ≥ 1.");
            if (!(t.Material.MuR > 0))
                return EmSuitability.No(
                    $"The {which} termination is an open half-space with µᵣ = {t.Material.MuR}; " +
                    $"it must be positive.");
        }

        return EmSuitability.Yes;
    }

    /// <summary>
    /// <b>D5's bridge.</b> The grounded slab expressed as a one-layer stack: PEC below, one layer
    /// of the slab's own material and height, free space above. This is the object the general
    /// medium must reproduce the SHIPPED kernel from, to machine precision.
    /// </summary>
    public static LayerStack FromGroundedSlab(GroundedSlab slab) =>
        new(Termination.Pec, [new MediumLayer(slab.HeightM, slab.Material)], Termination.Air);

    /// <summary>
    /// Split one layer into <paramref name="fractions"/> sub-layers of the SAME material, summing
    /// to the original thickness. The physics is unchanged by construction, which is exactly what
    /// makes it Tier 2's oracle: the kernel must come out <b>bit-identical</b>.
    /// </summary>
    public LayerStack WithLayerSplit(int layerIndex, params double[] fractions)
    {
        if (layerIndex < 0 || layerIndex >= Layers.Count)
            throw new ArgumentOutOfRangeException(nameof(layerIndex));
        if (fractions.Length == 0 || fractions.Any(f => !(f > 0)))
            throw new ArgumentException("Every split fraction must be positive.", nameof(fractions));

        double sum = fractions.Sum();
        var src   = Layers[layerIndex];
        var built = new List<MediumLayer>(Layers.Count + fractions.Length - 1);
        for (int i = 0; i < Layers.Count; i++)
        {
            if (i != layerIndex) { built.Add(Layers[i]); continue; }
            foreach (double f in fractions)
                built.Add(new MediumLayer(src.ThicknessM * f / sum, src.Material));
        }
        return new LayerStack(Bottom, built, Top);
    }

    public EmMaterial MaterialOfRegion(int region)
    {
        if (region == 0)                return Bottom.Kind == TerminationKind.HalfSpace ? Bottom.Material : EmMaterial.Air;
        if (region == RegionCount - 1)  return Top.Kind    == TerminationKind.HalfSpace ? Top.Material    : EmMaterial.Air;
        return Layers[region - 1].Material;
    }

    /// <summary>Which region contains z. A point exactly on an interface belongs to the region ABOVE.</summary>
    public int RegionOf(double z)
    {
        if (z < InterfaceZ[0]) return 0;                      // below the bottom interface
        for (int i = 1; i <= LayerCount; i++)                 // interfaces 1..N bound layers 1..N
            if (z < InterfaceZ[i]) return i;
        return RegionCount - 1;                               // at or above the top interface
    }

    public double RegionBottomZ(int region) => region == 0 ? double.NegativeInfinity : InterfaceZ[region - 1];
    public double RegionTopZ(int region)    => region == RegionCount - 1 ? double.PositiveInfinity : InterfaceZ[region];
    public bool   IsSemiInfinite(int region) => region == 0 || region == RegionCount - 1;

    /// <summary>True when the given end is a solid wall (PEC/PMC) rather than a half-space.</summary>
    public bool IsWall(int region) =>
        (region == 0              && Bottom.Kind != TerminationKind.HalfSpace) ||
        (region == RegionCount - 1 && Top.Kind    != TerminationKind.HalfSpace);

    public override string ToString()
    {
        var parts = Layers.Select(l => $"{l.ThicknessM * 1e3:G4} mm εᵣ={l.Material.EpsR:G4}");
        return $"[{Bottom}] {string.Join(" | ", parts)} [{Top}]";
    }
}

/// <summary>
/// The ω → 0 branch of the grounded-slab Green's function, in closed form.
///
/// <para><b>Derived here, not taken from anywhere</b> — the derivation is short enough to write
/// out, and the brief's D4 makes an unverifiable transcription the one unacceptable outcome. In the
/// static limit the slab's TM reflection coefficient collapses to a Möbius function of
/// x = e^{−2k_ρh}:</para>
/// <code>
///   Γ_e(k_ρ) → (K − x)/(1 − Kx),      K = (1 − εᵣ)/(1 + εᵣ),   x = e^{−2k_ρh}
///   1 + Γ_e  = (1 + K)(1 − x)/(1 − Kx)
///            = (1 + K)[1 − (1 − K)·Σ_{n≥1} K^{n−1} x^n]        (geometric series in Kx)
/// </code>
/// <para>Each x^n Hankel-transforms by <c>∫₀^∞ e^{−2nk_ρh}J₀(k_ρρ)dk_ρ = 1/√(ρ² + (2nh)²)</c>,
/// giving the classic image series below. Two independent facts fall out and are used as oracles:
/// as h → ∞ it tends to <c>1/(2π(1 + εᵣ)ρ)</c>, the textbook potential of a charge on the surface
/// of a dielectric half-space; and at εᵣ = 1 only the n = 1 term survives with K = 0, leaving
/// free space plus <i>one</i> negative image, exactly.</para>
///
/// <para><b>Its slow convergence at high εᵣ is exactly why it is an oracle and not the production
/// method:</b> the ratio is |K| — 0.63 on FR-4, but 0.856 on GaAs, so ~130 images are needed for
/// 1e-9 there.</para>
/// </summary>
public static class StaticGreens
{
    /// <summary>
    /// The static scalar-potential kernel G_q(ρ), normalised so φ = (1/ε₀)∫G_q q dS′ and so the
    /// free-space value is 1/(4πρ).
    ///
    /// <para><b>K is COMPLEX, and that is not decoration.</b> Nothing in the derivation above used
    /// the realness of εᵣ, so the series is exact for ε* = εᵣ(1 − j tanδ) exactly as written — and
    /// it has to be, because the thing it is an oracle for carries loss. Written with a real εᵣ
    /// instead it sits a frequency-INDEPENDENT 1.1e-6 away from the full-wave kernel's ω → 0 limit
    /// on FR-4 at tanδ = 0.001, which reads exactly like a convergence floor in the kernel and is
    /// nothing of the sort. That was measured, not guessed: tightening the integrator 100× moved
    /// the answer by 7e-11 while the discrepancy stayed put.</para>
    /// </summary>
    public static Complex ScalarPotential(GroundedSlab slab, double rhoM, int maxImages = 4000,
                                          double tol = 1e-16)
    {
        Complex k = (1.0 - slab.EpsComplex) / (1.0 + slab.EpsComplex);
        double h = slab.HeightM;

        Complex sum = Complex.Zero, kPow = Complex.One;      // K^{n−1}
        for (int n = 1; n <= maxImages; n++)
        {
            double d = 2.0 * n * h;
            Complex term = kPow / Math.Sqrt(rhoM * rhoM + d * d);
            sum += term;
            kPow *= k;
            if (term.Magnitude < tol / Math.Max(rhoM, 1e-30)) break;
        }
        return (1.0 + k) * (1.0 / rhoM - (1.0 - k) * sum) / (4.0 * Math.PI);
    }

    /// <summary>
    /// The static vector-potential kernel G_A(ρ), same normalisation.
    ///
    /// <para><b>It does not depend on εᵣ at all, and that is a check rather than an oversight.</b>
    /// In the TE (h) transmission line the slab's impedance ratio is k_z0/k_z1, which tends to 1 as
    /// ω → 0 whatever εᵣ is — so Γ_h → −e^{−2k_ρh} and the magnetostatics of a horizontal current
    /// over a ground plane with µᵣ = 1 everywhere simply does not see the dielectric. Free space
    /// plus one perfect negative image at depth 2h, for every εᵣ.</para>
    /// </summary>
    public static double VectorPotential(GroundedSlab slab, double rhoM)
    {
        double d = 2.0 * slab.HeightM;
        return (1.0 / rhoM - 1.0 / Math.Sqrt(rhoM * rhoM + d * d)) / (4.0 * Math.PI);
    }
}

/// <summary>
/// <b>Tier 3's oracle — the ω → 0 branch for an ARBITRARY stack.</b>
///
/// <para><b>It is a genuinely different formulation, not the full-wave cascade with ω set small.</b>
/// There is no k_z here, no TE/TM split of the medium, no wave: the potential in each region is
/// <c>e^{±k_ρ z}</c> (Laplace, not Helmholtz), the interface coefficient is the electrostatic
/// <c>K = (ε_a − ε_b)/(ε_a + ε_b)</c>, and the propagation factor is the real <c>e^{−2k_ρ d}</c>.
/// The magnetostatic (vector-potential) problem is the same recursion with µ in place of ε — which
/// is why it does not see the dielectric at all for a non-magnetic stack, exactly as L8a's own
/// one-layer <see cref="StaticGreens"/> records.</para>
///
/// <para><b>K IS COMPLEX, and L8a's warning about that applies here verbatim.</b> Written with a
/// real εᵣ, the one-layer static series sat a frequency-INDEPENDENT 1.1e-6 from the full-wave
/// kernel's ω → 0 limit — which reads exactly like a convergence floor and is nothing of the sort.
/// Nothing in the derivation uses the realness of ε, so ε* = εᵣ(1 − j tanδ) is carried throughout.</para>
///
/// <para><b>The oracle is itself checked before it is believed</b> (this area has now had four
/// occasions where the ORACLE, not the method, was at fault): for a one-layer grounded stack this
/// class must reproduce <see cref="StaticGreens"/>'s closed-form image series, which L8a validated
/// independently. Only then is it used as the multilayer reference.</para>
///
/// <para>Source and observer must both lie in the top half-space; the inverse transform below is
/// referenced to that region and is refused by name otherwise.</para>
/// </summary>
public static class LayeredStaticGreens
{
    /// <summary>G_q(ρ; z, z′) in the ω → 0 limit, normalised so free space is 1/(4πρ).</summary>
    public static Complex ScalarPotential(LayerStack stack, double rhoM, double z, double zp,
                                          double relTol = 1e-12)
        => Evaluate(stack, scalar: true, rhoM, z, zp, relTol);

    /// <summary>G_A(ρ; z, z′) in the ω → 0 limit, same normalisation.</summary>
    public static Complex VectorPotential(LayerStack stack, double rhoM, double z, double zp,
                                          double relTol = 1e-12)
        => Evaluate(stack, scalar: false, rhoM, z, zp, relTol);

    /// <summary>
    /// The static reflection coefficient looking down at the stack's top interface. Exposed so the
    /// asymptote and the recursion itself can be checked directly rather than only through the
    /// inverse transform.
    /// </summary>
    public static Complex Reflection(LayerStack stack, bool scalar, double kRho)
    {
        int nIf = stack.InterfaceCount;
        Complex g = TerminationCoefficient(stack.Bottom, stack, scalar, isBottom: true);
        for (int i = 1; i < nIf; i++)
        {
            double d = stack.InterfaceZ[i] - stack.InterfaceZ[i - 1];
            Complex prev = g * Math.Exp(-2.0 * kRho * d);
            Complex r = InterfaceCoefficient(stack, scalar, i);
            g = (r + prev) / (1.0 + r * prev);
        }
        return g;
    }

    /// <summary>The k_ρ → ∞ limit of <see cref="Reflection"/> — the top interface's own coefficient.</summary>
    public static Complex AsymptoticReflection(LayerStack stack, bool scalar) =>
        stack.LayerCount == 0 ? Complex.Zero : InterfaceCoefficient(stack, scalar, stack.InterfaceCount - 1);

    private static Complex TerminationCoefficient(Termination t, LayerStack stack, bool scalar, bool isBottom)
    {
        if (t.Kind == TerminationKind.Pec) return -Complex.One;
        if (t.Kind == TerminationKind.Pmc) return  Complex.One;
        // Open half-space: an ordinary interface between it and the adjacent layer.
        return InterfaceCoefficient(stack, scalar, isBottom ? 0 : stack.InterfaceCount - 1);
    }

    /// <summary>
    /// The coefficient at interface <paramref name="i"/> for a "wave" arriving from the region
    /// ABOVE. Electrostatics: (ε_above − ε_below)/(ε_above + ε_below). Magnetostatics: the same in µ.
    /// </summary>
    private static Complex InterfaceCoefficient(LayerStack stack, bool scalar, int i)
    {
        var below = stack.MaterialOfRegion(i);
        var above = stack.MaterialOfRegion(i + 1);
        if (scalar)
        {
            Complex eb = below.EpsComplex, ea = above.EpsComplex;
            return (ea - eb) / (ea + eb);
        }
        double mb = below.MuR, ma = above.MuR;
        return (mb - ma) / (mb + ma);
    }

    private static Complex Evaluate(LayerStack stack, bool scalar, double rhoM, double z, double zp,
                                    double relTol)
    {
        if (!(rhoM > 0)) throw new ArgumentOutOfRangeException(nameof(rhoM), rhoM, "ρ must be positive.");
        double h = stack.TopZ;
        if (z < h || zp < h)
            throw new ArgumentException(
                $"LayeredStaticGreens is referenced to the top half-space and needs z, z′ ≥ H = " +
                $"{h:G6} m; got z = {z:G6}, z′ = {zp:G6}. A source inside the stack needs the " +
                $"full-wave path (LayeredSpectralGreens), which has no such restriction.");
        if (stack.Top.Kind != TerminationKind.HalfSpace)
            throw new ArgumentException(
                $"The top termination is {stack.Top}, a solid wall — there is no half-space above " +
                $"the stack for the static kernel to be referenced in.");

        double delta = Math.Abs(z - zp);
        double sigma = z + zp - 2 * h;
        Complex gInf = AsymptoticReflection(stack, scalar);

        Complex closed = 1.0 / Math.Sqrt(rhoM * rhoM + delta * delta)
                       + gInf / Math.Sqrt(rhoM * rhoM + sigma * sigma);

        // The remainder decays like e^{−k_ρ(2 d_top + Σ)}; 45 decay lengths puts it at 3e-20.
        double dTop = stack.LayerCount == 0 ? 0 : stack.Layers[^1].ThicknessM;
        double decay = 2 * dTop + sigma;
        double kMax = decay > 0 ? 45.0 / decay : 0.0;
        if (kMax <= 0) return closed / (4.0 * Math.PI);

        Complex Integrand(double k) =>
            (Reflection(stack, scalar, k) - gInf) * Math.Exp(-k * sigma) * Bessel.J0(k * rhoM);

        // Partition at the zeros of J₀(k_ρρ) so no panel straddles an oscillation, and cap the
        // panel width at a fraction of the decay length so the exponential is resolved too.
        var cuts = new List<double> { 0.0 };
        for (int m = 1; ; m++)
        {
            double zeroAt = SommerfeldIntegral.BesselZero(m) / rhoM;
            if (zeroAt >= kMax || m > 20000) break;
            cuts.Add(zeroAt);
        }
        cuts.Add(kMax);
        // The exponential varies on the k-scale 1/decay — NOT on the length scale `decay`, which
        // is what a units slip here would use, and which produced a 10^10-panel partition (capped
        // to 64 per oscillation) and a 25-second "quadrature" that was really 800 000 panels of
        // maximally-recursed adaptive bisection.
        double maxPanel = 1.0 / (8.0 * decay);
        var refined = new List<double> { cuts[0] };
        for (int i = 1; i < cuts.Count; i++)
        {
            double a = refined[^1], b = cuts[i];
            int n = Math.Max(1, (int)Math.Ceiling((b - a) / maxPanel));
            n = Math.Min(n, 64);
            for (int k = 1; k <= n; k++) refined.Add(a + (b - a) * k / n);
        }

        // Per-panel absolute tolerance, NOT the global one divided by the panel count: on a
        // partition that already isolates every oscillation and resolves the exponential, a
        // 12-point Gauss rule is far inside tolerance on its first try, and asking each panel for
        // 1/N of the global budget only drives every one of them to maximum recursion depth.
        Complex sum = Complex.Zero;
        double tol = relTol * Math.Abs(closed.Real);
        for (int i = 0; i + 1 < refined.Count; i++)
            sum += Adaptive(Integrand, refined[i], refined[i + 1], tol, 8);

        return (closed + sum) / (4.0 * Math.PI);
    }

    // -------------------------------------------------------------------------------------------
    // A small adaptive Gauss-Legendre. Nodes are COMPUTED by Newton on the Legendre recurrence,
    // never tabulated — the same rule L8a followed, for the same reason. SommerfeldIntegral has its
    // own private copy; the duplication is a dozen lines and the two have different lifetimes.
    // -------------------------------------------------------------------------------------------

    private static Complex Adaptive(Func<double, Complex> f, double a, double b, double tol, int depth)
    {
        Complex whole = Gauss(f, a, b, 12);
        double mid = 0.5 * (a + b);
        Complex halves = Gauss(f, a, mid, 12) + Gauss(f, mid, b, 12);
        if (depth <= 0 || (whole - halves).Magnitude <= tol) return halves;
        return Adaptive(f, a, mid, 0.5 * tol, depth - 1) + Adaptive(f, mid, b, 0.5 * tol, depth - 1);
    }

    private static Complex Gauss(Func<double, Complex> f, double a, double b, int n)
    {
        var (x, w) = Nodes(n);
        double half = 0.5 * (b - a), mid = 0.5 * (a + b);
        Complex s = Complex.Zero;
        for (int i = 0; i < n; i++) s += w[i] * f(mid + half * x[i]);
        return s * half;
    }

    private static readonly Dictionary<int, (double[] X, double[] W)> NodeCache = new();

    private static (double[] X, double[] W) Nodes(int n)
    {
        lock (NodeCache)
        {
            if (NodeCache.TryGetValue(n, out var hit)) return hit;
            var x = new double[n];
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double zz = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5)), pp = 0;
                for (int it = 0; it < 200; it++)
                {
                    double p0 = 1, p1 = 0;
                    for (int j = 0; j < n; j++)
                    {
                        double p2 = p1;
                        p1 = p0;
                        p0 = ((2 * j + 1) * zz * p1 - j * p2) / (j + 1);
                    }
                    pp = n * (zz * p0 - p1) / (zz * zz - 1);
                    double dz = p0 / pp;
                    zz -= dz;
                    if (Math.Abs(dz) < 1e-16) break;
                }
                x[i] = zz;
                w[i] = 2.0 / ((1 - zz * zz) * pp * pp);
            }
            NodeCache[n] = (x, w);
            return (x, w);
        }
    }
}
