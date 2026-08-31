// L9c / M5 — the Green's function of a MULTI-LEVEL problem is not one function, it is one per height
// PAIRING, and this is where that is organised.
//
// L8d's PlanarKernelPair holds exactly two PlanarKernelTerms because L8's D2 puts every source and
// every observer on one plane: the kernel is mesh-independent and height-independent, so one fit per
// component per frequency serves the DUT and every calibration standard. **Neither half of that
// survives more than one level**, and the two halves fail differently:
//
//   • MESH-independence SURVIVES. A pairing is (z, z′), not (cell, cell), so a fit is still shared
//     across the DUT and its standards. L8d's caching decision is unchanged.
//   • HEIGHT-independence does NOT. L9b's D5 shift covers a pair in the top half-space with no refit,
//     and L9c's M3 measured that the interior pairings are exact shifts too — but of FOUR families in
//     the source region's own k_zm, which is a different fit, not the same one shifted. So a pairing
//     that is not high–high needs Dcim.FitAtHeights.
//
// D7 projected "four kernel components × three height pairings ≈ 12 fits per frequency at L9b's
// measured ~0.1 s each ≈ 1.2 s". This fits LAZILY and counts, so the projection is checked rather
// than assumed: a two-level structure with one via has three heights of interest (two levels and the
// via's midpoint), six unordered pairings, and asks for far fewer than 4 × 6 because most components
// are never wanted at most pairings — G_A^zz only between two vias, the mixed one only between a via
// and a level.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The z of each conductor level, and the two quantities a via basis needs from a pair of them.
///
/// <para><b>The via's z-integral is RESOLVED, not replaced</b> — <c>ViaZIntegral</c>, and see its
/// header for the split (the two asymptotes' static parts in closed form, everything else on a Gauss
/// rule in z). L9c evaluated the kernel once at the midpoint of the two feet and multiplied by ℓ;
/// L9e measured that the via's own inductance came out high by ≈ 0.673·(ℓ/w) and shipped a geometric
/// bound. <b>That bound is retired</b>: the same sweep re-run against the fill is flat to 0.13% over
/// ℓ/w ∈ [0.01, 5] and a 16× range of w (<c>ViaPhysicsTests.T3_1</c>).</para>
///
/// <para><b><see cref="MaxElectricalLength"/> stays, and it is now about a different thing.</b> It is
/// no longer the quadrature — it is the BASIS. L9c's via basis carries one z-rooftop per inter-level
/// gap, so its current is UNIFORM along the whole via; that is an electrical assumption with no
/// quadrature anywhere in it, and no z rule removes it. Splitting the via across intermediate levels
/// is what gives it a current profile, and that remains the remedy the refusal names.</para>
/// </summary>
/// <param name="GroundZ">
/// <b>Where the ground plane is — the stack's own bottom termination interface</b>, which
/// <c>LayerStack</c>'s stated convention puts at z = 0. A GROUND-ATTACHMENT basis spans
/// <c>GroundZ … Of(LayerIndex)</c>, so this is the one number that lets a half basis have a z extent
/// at all; every other basis ignores it.
/// </param>
public sealed record PlanarLevels(IReadOnlyList<double> Z, double GroundZ = 0.0)
{
    public static PlanarLevels From(PlanarProblem problem)
    {
        var z = new double[problem.Layers.Count];
        for (int i = 0; i < z.Length; i++) z[i] = problem.LevelZ(i);
        var stack = problem.EffectiveStack;
        return new PlanarLevels(z, stack.InterfaceZ.Count > 0 ? stack.InterfaceZ[0] : 0.0);
    }

    /// <summary>The height a HORIZONTAL basis on this level sits at.</summary>
    public double Of(int layerIndex) => Z[layerIndex];

    /// <summary>The midpoint of a VERTICAL basis's span. Kept because the kernel's own asymptote is
    /// asked for at a representative height (its coefficients do not depend on the heights at all);
    /// the ENTRY is no longer evaluated there — see the type's own note.</summary>
    public double MidOf(int lower) => 0.5 * (Z[lower] + Z[lower + 1]);

    /// <summary>ℓ — the via's length, which multiplies its z-integral.</summary>
    public double LengthOf(int lower) => Z[lower + 1] - Z[lower];

    /// <summary>The z extent a GROUND-ATTACHMENT basis on <paramref name="layerIndex"/> occupies —
    /// the plane to the metal. Its length is what multiplies that basis's own z-integral.</summary>
    public double AttachmentLengthOf(int layerIndex) => Z[layerIndex] - GroundZ;

    /// <summary>
    /// R-mom-17: the electrical length above which a via's current can no longer be taken as UNIFORM
    /// along it — a property of the BASIS (one z-rooftop per span), never of any quadrature.
    ///
    /// <para><b>0.05 was inherited from L9c and is ~20× tighter than what the effect measures.</b>
    /// M1's own R-gv-1 measurement, on an ATTACHED via — the only kind that exists in a real
    /// structure, and the kind a backside via is at BOTH ends — subdivided the same via into n
    /// segments and compared the reaction vᵀZ⁻¹v:</para>
    ///
    /// <list type="table">
    ///   <item><term>k·ℓ = 0.01</term><description>n = 1 → 8 moves the answer 0.062%</description></item>
    ///   <item><term>k·ℓ = 0.23</term><description>0.077% — §0.2 item 3's own number, 4.5× over the bound</description></item>
    ///   <item><term>k·ℓ = 0.50</term><description>0.172%</description></item>
    ///   <item><term>k·ℓ = 1.00</term><description>0.141%, and the current is 2.0% non-uniform</description></item>
    /// </list>
    ///
    /// <para>A FLOATING rod does move — 10.2% at n = 8, 28.5% non-uniform — but that movement is
    /// <b>98% static</b> (identical at k·ℓ = 0.01 and 0.23), i.e. it is the floating end condition,
    /// not electrical length. A via in a circuit is terminated at both ends and has no such freedom.
    /// <b>0.30 is therefore what this bound is set to</b>: past every measured point where the
    /// uniform-current basis is worth under 0.1%, and comfortably inside where the CHAIN that would
    /// fix it is affordable (M1 measured 14.2% of a de-embedded point at n = 8, growing ~4× per
    /// doubling — so it is not).</para>
    ///
    /// <para><b>Widening it unlocks nothing on its own</b>, and the refusal says so:
    /// <see cref="Dcim.ValidatedRhoOverLambdaAtHeights"/> = 0.1 on G_A^zz already restricts every
    /// via-bearing run to electrically small structures, and 1.0 is as far as that limit let M1
    /// measure on its own fixture. It is untouched.</para>
    /// </summary>
    public const double MaxElectricalLength = 0.30;

    /// <summary>
    /// <b>MIM-3 — how large a CELL may be against the SEPARATION between two conductor levels
    /// before the cross-level block stops being the answer it looks like. A NOTE, never a
    /// refusal.</b>
    ///
    /// <para>This is a property of the QUADRATURE, not of the kernel, and the two tiers were
    /// separated before either was believed. The kernel is fine: at height pairs straddling a
    /// 0.05-3 um capacitor dielectric, <see cref="Dcim.FitAtHeights"/> against direct Sommerfeld
    /// integration is <b>flat in the separation</b> — worst 4.2e-3 of the free-space kernel at
    /// 0.05 um and 6.4e-3 at 3 um, i.e. the interconnect-scale spacing L9c already measured. There
    /// is no thin-layer kernel failure to find.</para>
    ///
    /// <para>The FILL is where it goes. On two coincident plates straddling the dielectric,
    /// entry-wise against the same matrix at forced-high quadrature, scaled by the block's own
    /// largest entry — the SAME-level block stays put and only the CROSS-level block moves:</para>
    ///
    /// <list type="table">
    ///   <item><term>cell/d = 1</term><description>2.3e-7</description></item>
    ///   <item><term>cell/d = 2</term><description>9.6e-6</description></item>
    ///   <item><term>cell/d = 5</term><description>4.1e-3</description></item>
    ///   <item><term>cell/d = 10</term><description>4.1e-2</description></item>
    ///   <item><term>cell/d = 20</term><description>1.7e-1</description></item>
    /// </list>
    ///
    /// <para>Four decades between cell/d = 1 and 20 — steepest at the bottom, flattening as it
    /// saturates — and §L8c's own failure mode: reciprocity holds to 1e-19 and passivity to 1e-5
    /// the whole way up, so nothing downstream looks wrong. The
    /// mechanism is the recorded one (§3.5): a cross-level entry "has no 1/rho", but at
    /// d &lt;&lt; cell the kernel has a peak of width d inside a cell of width h, and a rule that
    /// treats the pair as smooth integrates straight over it.</para>
    ///
    /// <para><b>What it costs in the answer</b>, on a de-embedded 10 x 10 um shunt plate pair
    /// against eps0*epsr*A/d, with the same structure minus its lower plate subtracted as the
    /// baseline: 0.89 / 0.99 / 1.10 at cell/d = 1.25 / 2.5 / 5, then <b>1.46 at 12.5 and the wrong
    /// SIGN at 25 and 50</b>. 5 is where the two ladders agree, so 5 is the number.</para>
    ///
    /// <para>Kernel A on the same cross-section reproduces the closed form to 1.007-1.16 over the
    /// whole ladder and is mesh-converged to five digits, so the closed form is not in doubt.</para>
    /// </summary>
    public const double ValidatedCellOverSeparation = 5.0;

    /// <summary>
    /// The refusal, and it is now earned on ONE quantity rather than two.
    ///
    /// <para><b>L9e's geometric bound (<c>MaxLengthOverWidth = 0.5</c>) is RETIRED.</b> It existed
    /// because the midpoint rule froze <c>1/R</c> over the via's length, making the via's inductance
    /// high by ≈ 0.673·(ℓ/w) with no frequency in the condition at all. The z-integral is now
    /// resolved and the same measurement reads flat to 0.13% over ℓ/w ∈ [0.01, 5] and a 16× range of
    /// footprint width, so there is nothing left for a geometric bound to refuse. Retiring it does
    /// NOT widen what this kernel can answer:
    /// <see cref="Dcim.ValidatedRhoOverLambdaAtHeights"/> = 0.1 on G_A^zz already restricts every
    /// via-bearing run to electrically small structures, and that limit is untouched.</para>
    ///
    /// <para>What remains is electrical and real: a via basis is ONE z-rooftop per inter-level gap, so
    /// the current it carries is uniform over the whole length. That is exact for a short via and
    /// wrong for a resonant one however well the kernel is integrated.</para>
    /// </summary>
    /// <param name="kMax">Wavenumber at the top of the sweep, in the fastest-slowing medium.</param>
    /// <param name="hasGroundAttachment">Whether any via runs to the ground plane, whose span is
    /// <see cref="GroundZ"/> → the metal rather than one inter-level gap.</param>
    public EmSuitability CanRepresentVias(double kMax, bool hasGroundAttachment = false)
    {
        for (int i = 0; i + 1 < Z.Count; i++)
        {
            var v = CheckOne(kMax, LengthOf(i), $"The via between levels {i} and {i + 1}");
            if (!v.Ok) return v;
        }

        if (hasGroundAttachment)
            for (int i = 0; i < Z.Count; i++)
            {
                var v = CheckOne(kMax, AttachmentLengthOf(i),
                                 $"The ground via from the plane up to level {i}");
                if (!v.Ok) return v;
            }

        return EmSuitability.Yes;
    }

    private static EmSuitability CheckOne(double kMax, double ell, string subject)
    {
        double kl = kMax * ell;
        if (kl <= MaxElectricalLength) return EmSuitability.Yes;

        return EmSuitability.No(
            $"{subject} is {ell:G4} m long, i.e. k·ℓ = {kl:G4} at the top of the sweep, above this " +
            $"kernel's floor of {MaxElectricalLength}. A vertical basis here is a SINGLE z-rooftop " +
            $"spanning the whole run, so the current it carries is UNIFORM along it — a limit on the " +
            $"BASIS, not on the quadrature: the z-integral of the Green's function is resolved " +
            $"(ViaZIntegral), and no amount of integrating it better gives the via a current profile " +
            $"it has no degree of freedom for. The bound is set at 0.30 from a MEASUREMENT rather " +
            $"than from O((kℓ)²): subdividing an ATTACHED via moved the answer 0.077% at k·ℓ = 0.23 " +
            $"and 0.141% at k·ℓ = 1.0, while the subdivision itself costs ~14% of a de-embedded " +
            $"point (see PlanarLevels.MaxElectricalLength). Lower the sweep's top, or — for a via " +
            $"between two meshed levels — split it across intermediate levels, which gives it an " +
            $"n-step profile at that cost.");
    }
}

/// <summary>
/// Every kernel component at every height pairing a mesh asks for, fitted ONCE PER FREQUENCY and
/// lazily. See the file header for what survives from L8d's <see cref="PlanarKernelPair"/> and what
/// does not.
/// </summary>
public sealed class PlanarKernelSet
{
    /// <summary>
    /// <b>L9d/M1 — the FIT cache is shared by every per-mesh view, and that is the load-bearing
    /// half of L8d's caching decision rather than an optimisation.</b>
    ///
    /// <para>L8d's own rule is "fit once per frequency, share across the DUT and every standard",
    /// and a de-embedded solve touches THREE meshes at every frequency. Before L9d, <c>For</c>
    /// returned a fresh set whose cache was a COPY of whatever had been fitted so far — which was
    /// harmless while only one mesh ever asked (L9c had no solve), and turns 9 fits per frequency
    /// into 9 per MESH the moment a calibrator does. So the <see cref="DcimModel"/>s live here, once
    /// per (component, height pairing), and every view derives its own
    /// <see cref="PlanarKernelTerms"/> from them — which is the cheap half
    /// (<c>FromDcimAtHeights</c> re-decomposes an already-fitted model; <c>FitAtHeights</c> is the
    /// ~0.1 s one).</para>
    ///
    /// <para>The dictionary is a pure LOOKUP built by lazy insertion and never iterated, so R-mlp-5's
    /// determinism is unaffected — there is no hash order anywhere on this path.</para>
    /// </summary>
    private sealed class FitCache
    {
        public readonly Dictionary<(GreensKernel, double, double), DcimModel> Models = new();
        // M2's direct tables live here for exactly the reason the fits do (L9d's own finding): a
        // de-embedded solve builds one PlanarKernelSet VIEW per mesh, and a per-view cache would
        // rebuild them per mesh with no answer anywhere looking wrong. It matters MORE here — a
        // miss is seconds of Sommerfeld integration rather than ~90 ms of fit.
        public readonly Dictionary<((GreensKernel, double, double), double, int), PlanarKernelTerms> Direct = new();
        /// <summary>One gate per direct-table key, so exactly ONE thread pays for a table and the
        /// rest wait for it. Building outside the shared lock (which is right — a table is seconds
        /// and must not block every other fit) otherwise lets N threads each build the SAME table.</summary>
        public readonly Dictionary<((GreensKernel, double, double), double, int), object> DirectGates = new();
        public readonly object Gate = new();
        public int Count;
    }

    private readonly LayeredSpectralGreens _greens;
    private readonly DcimSettings          _dcim;
    private readonly PlanarExtractionOrder _order;
    private readonly double                _rhoFloor;
    private readonly FitCache              _fits;
    private readonly Dictionary<(GreensKernel, double, double), PlanarKernelTerms> _terms = new();
    private readonly Dictionary<(GreensKernel, double, double), PlanarKernelTerms> _reduced = new();

    public LayerStack Stack       => _greens.Stack;
    public double     FrequencyHz => _greens.FrequencyHz;

    /// <summary>
    /// <b>D7's counter, and it is the R-mom-11 pattern.</b> "Four components × three pairings ≈ 12
    /// fits per frequency" is a projection; this is what was actually asked for. A test asserts it,
    /// so a future change that starts refitting per CELL PAIR instead of per PAIRING fails loudly
    /// rather than costing an hour a sweep.
    ///
    /// <para>It counts fits across every view produced by <see cref="For"/>, because that is the
    /// quantity L8d's decision is about — the DUT and its standards share one number, not three.</para>
    /// </summary>
    public int FitCount { get { lock (_fits.Gate) return _fits.Count; } }

    public PlanarKernelSet(LayeredSpectralGreens greens,
                           PlanarExtractionOrder order = PlanarExtractionOrder.Constant,
                           double rhoFloor = 0.0, DcimSettings? dcim = null)
        : this(greens, order, rhoFloor, dcim ?? DcimSettings.Default, new FitCache()) { }

    private PlanarKernelSet(LayeredSpectralGreens greens, PlanarExtractionOrder order,
                            double rhoFloor, DcimSettings dcim, FitCache fits)
    {
        _greens = greens;
        _order = order;
        _rhoFloor = rhoFloor;
        _dcim = dcim;
        _fits = fits;
    }

    /// <summary>The same set re-floored for one mesh's smallest cell — L8d's <c>For</c>, unchanged in
    /// spirit: the per-mesh part of the terms is only the ρ floor. The FIT cache is shared with the
    /// set this was made from, so a second mesh at the same frequency refits nothing.</summary>
    public PlanarKernelSet For(PlanarFillCores cores)
        => new(_greens, _order, cores.RhoFloorM, _dcim, _fits);

    /// <summary>
    /// The terms for one component at one height pairing. <b>Symmetric in the two heights</b>, and
    /// canonicalised so that (z, z′) and (z′, z) share one fit — which is legitimate here and is NOT
    /// the canonicalisation L9a's R-lyr-5 forbids: that one is about never computing the reverse
    /// chain, and reciprocity of the KERNEL is measured independently in
    /// <c>VerticalCurrentTests.T0_2</c>. This is about not fitting the same function twice.
    /// </summary>
    public PlanarKernelTerms Get(GreensKernel kernel, double zA, double zB)
    {
        var key = Key(kernel, zA, zB);
        lock (_terms)
            if (_terms.TryGetValue(key, out var hit)) return hit;

        var terms = PlanarKernelTerms.FromDcimAtHeights(Model(kernel, zA, zB), _order, _rhoFloor);
        lock (_terms) _terms[key] = terms;
        return terms;
    }

    /// <summary>
    /// <b>The via z-integral's view of the same fit: the decomposition with the two asymptotes' STATIC
    /// parts removed</b> (<see cref="PlanarKernelTerms.FromDcimAtHeightsMinusStaticAsymptotes"/>).
    /// Shares the fit — asking for both views of one height pair costs one
    /// <see cref="Dcim.FitAtHeights"/>, which is the whole point of L9d's shared cache.
    /// </summary>
    public PlanarKernelTerms GetMinusStaticAsymptotes(GreensKernel kernel, double zA, double zB)
    {
        var key = Key(kernel, zA, zB);
        lock (_reduced)
            if (_reduced.TryGetValue(key, out var hit)) return hit;

        var terms = PlanarKernelTerms.FromDcimAtHeightsMinusStaticAsymptotes(
            Model(kernel, zA, zB), _order, _rhoFloor);
        lock (_reduced) _reduced[key] = terms;
        return terms;
    }

    /// <summary>
    /// <b>M2 (brief-gazz-accuracy-ceiling) — the SAME decomposition with the fit replaced by DIRECT
    /// Sommerfeld integration.</b>
    ///
    /// <para>The fit is what fails: M1 measured every reachable <see cref="DcimSettings"/> knob and
    /// the best configuration is still 71× outside the envelope the other three components meet at
    /// ρ/λ = 1 — and is 23× WORSE inside ρ/λ ≤ 0.1, where the kernel is used today. So this path
    /// keeps every part of the decomposition that is exact and replaces only the part that is
    /// fitted.</para>
    ///
    /// <para><b>What is kept, and why that makes the result exact rather than merely different.</b>
    /// The extraction coefficients and the removed static-asymptote pieces come from the cached fit
    /// — one fit, already paid for — but the assembled entry is
    /// <c>Extracted·(closed-form core) + Remainder·(quadrature)</c> with
    /// <c>Remainder = full − Extracted</c>, so the split only decides how much value sits on each
    /// side. With <c>full</c> the direct integral, the SUM is the direct integral whatever the
    /// coefficients are. That is the same extraction-order invariance L9c's own T4_3 measures.</para>
    ///
    /// <para><b>The cost is real and is the point of it being a SETTING.</b> One
    /// <see cref="SommerfeldIntegral.EvaluateInterior"/> point is 40–50 ms, against a whole
    /// <see cref="Dcim.FitAtHeights"/> at ~90 ms — so this is affordable only because the ẑẑ block
    /// consumes its kernel through a radial TABLE, and only at the sample count M2 measured rather
    /// than at the DCIM table's own mesh-derived spacing.</para>
    /// </summary>
    public PlanarKernelTerms GetDirectMinusStaticAsymptotes(
        GreensKernel kernel, double zA, double zB, double rhoMaxM, int samples)
    {
        var key = Key(kernel, zA, zB);
        if (!(rhoMaxM > 0))
            throw new ArgumentOutOfRangeException(nameof(rhoMaxM), rhoMaxM,
                "The direct ẑẑ table needs a positive radial extent.");
        if (samples < 8)
            throw new ArgumentOutOfRangeException(nameof(samples), samples,
                "The direct ẑẑ table needs at least 8 samples; M2 measured the assembled block " +
                "converging at 128 and still moving 2.2e-3 at 32.");

        var cacheKey = (key, rhoMaxM, samples);
        object gate;
        lock (_fits.Gate)
        {
            if (_fits.Direct.TryGetValue(cacheKey, out var hit)) return hit;
            if (!_fits.DirectGates.TryGetValue(cacheKey, out var g))
                _fits.DirectGates[cacheKey] = g = new object();
            gate = g;
        }

        // Serialise on the KEY, not on the shared cache: one thread pays for the table, the rest
        // wait for that one rather than each building an identical copy, and no fit anywhere else
        // is blocked meanwhile.
        lock (gate)
        {
        lock (_fits.Gate)
            if (_fits.Direct.TryGetValue(cacheKey, out var hit2)) return hit2;

        // The fitted view supplies the extraction coefficients and the list of static asymptote
        // pieces the via's closed-form z-integral has already accounted for. Both are cheap and
        // cached; neither is what M1 measured as failing.
        var fitted = GetMinusStaticAsymptotes(kernel, zA, zB);
        var model  = Model(kernel, zA, zB);
        var pieces = model.AsymptotePieces
                          .Where(p => p.Coefficient != Complex.Zero)
                          .Select(p => (p.Coefficient, p.Depth))
                          .ToArray();

        // The direct kernel, minus the static asymptote pieces the via's closed-form z-integral has
        // already taken.
        Complex Full(double rho)
        {
            Complex v = SommerfeldIntegral.EvaluateInterior(_greens, kernel, rho, key.Hi, key.Lo).Value;
            foreach (var (c, d) in pieces)
                v -= c / (4.0 * Math.PI * Math.Sqrt(rho * rho + d * d));
            return v;
        }

        // TABULATE THE REMAINDER, NOT THE KERNEL. The kernel still diverges as 1/ρ once the static
        // asymptotes are removed (the poles' ln ρ and the direct term's own 1/ρ are still in it), and
        // a linear table cannot carry either — it would be worst exactly at the self and touching
        // cell pairs, which is where most of the block's value is. Subtracting `Extracted` first is
        // what makes the tabulated function bounded, and it is the same thing L8c's own
        // RadialRemainderTable.Build does; only the evaluator differs.
        var table = RadialRemainderTable.BuildFrom(
            rho => Full(rho) - fitted.Extracted(rho),
            rhoMaxM, rhoMaxM / Math.Max(samples - 4, 4), samples);

        // Handing back `table + Extracted` as the FULL kernel makes Remainder() return the tabulated
        // bounded function exactly, so the fill sees the same shape it always does.
        var terms = new PlanarKernelTerms(
            rho => table.Evaluate(rho) + fitted.Extracted(rho),
            fitted.Inverse, fitted.Log, fitted.Constant, fitted.Linear,
            _order, _rhoFloor, fitted.SmallestImageDepth);

        lock (_fits.Gate) _fits.Direct[cacheKey] = terms;
        return terms;
        }
    }

    /// <summary>The fitted model at one height pairing, from the shared cache — what a caller that
    /// needs the model's own structure (its asymptote depths, its radial derivative) asks for rather
    /// than re-fitting.</summary>
    public DcimModel Model(GreensKernel kernel, double zA, double zB)
    {
        var key = Key(kernel, zA, zB);
        lock (_fits.Gate)
        {
            if (!_fits.Models.TryGetValue(key, out var model))
            {
                model = Dcim.FitAtHeights(_greens, kernel, key.Hi, key.Lo, _dcim);
                _fits.Models[key] = model;
                _fits.Count++;
            }
            return model;
        }
    }

    /// <summary>The k_ρ → ∞ asymptote of one component at one height pair. <b>Costs no fit</b> — it is
    /// a handful of Fresnel coefficients — and its two COEFFICIENTS do not depend on the heights at
    /// all, which is what makes the via's singular z-integral a closed form.</summary>
    public LayeredSpectralGreens.InteriorAsymptote Asymptote(GreensKernel kernel, double z, double zp)
        => _greens.AsymptoticAtHeights(kernel, z, zp);

    private static (GreensKernel Kernel, double Lo, double Hi) Key(GreensKernel k, double zA, double zB)
        => (k, Math.Min(zA, zB), Math.Max(zA, zB));

    /// <summary>R-via-4's refusal, asked once for a mesh rather than per entry: is the widest
    /// separation this mesh will ask about inside the interior fit's validated range?</summary>
    public EmSuitability WithinValidatedRange(double meshExtentM)
    {
        double lambda = EmConstants.C0 / FrequencyHz;
        return Dcim.WithinValidatedRangeAtHeights(GreensKernel.VerticalVectorPotential,
                                                  _greens, meshExtentM / lambda);
    }
}
