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
public sealed record PlanarLevels(IReadOnlyList<double> Z)
{
    public static PlanarLevels From(PlanarProblem problem)
    {
        var z = new double[problem.Layers.Count];
        for (int i = 0; i < z.Length; i++) z[i] = problem.LevelZ(i);
        return new PlanarLevels(z);
    }

    /// <summary>The height a HORIZONTAL basis on this level sits at.</summary>
    public double Of(int layerIndex) => Z[layerIndex];

    /// <summary>The midpoint of a VERTICAL basis's span. Kept because the kernel's own asymptote is
    /// asked for at a representative height (its coefficients do not depend on the heights at all);
    /// the ENTRY is no longer evaluated there — see the type's own note.</summary>
    public double MidOf(int lower) => 0.5 * (Z[lower] + Z[lower + 1]);

    /// <summary>ℓ — the via's length, which multiplies its z-integral.</summary>
    public double LengthOf(int lower) => Z[lower + 1] - Z[lower];

    /// <summary>R-mom-17: the electrical length above which a via's current can no longer be taken as
    /// UNIFORM along it, which is a property of L9c's basis and not of any quadrature.</summary>
    public const double MaxElectricalLength = 0.05;

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
    public EmSuitability CanRepresentVias(double kMax)
    {
        for (int i = 0; i + 1 < Z.Count; i++)
        {
            double ell = LengthOf(i);
            double kl = kMax * ell;
            if (kl > MaxElectricalLength)
                return EmSuitability.No(
                    $"The via between levels {i} and {i + 1} is {ell:G4} m long, i.e. k·ℓ = " +
                    $"{kl:G4} at the top of the sweep, above this kernel's floor of " +
                    $"{MaxElectricalLength}. A vertical basis here is a SINGLE z-rooftop spanning the " +
                    $"gap, so the current it carries is UNIFORM along the whole via — exact while the " +
                    $"via is electrically short, and wrong by O((kℓ)²) = {kl * kl:G3} once it is not. " +
                    $"This is a limit on the BASIS, not on the quadrature: the z-integral of the " +
                    $"Green's function is resolved (ViaZIntegral), and no amount of integrating it " +
                    $"better gives the via a current profile it has no degree of freedom for. SPLIT " +
                    $"THE VIA ACROSS INTERMEDIATE LEVELS — n stacked sub-vias give it an n-step " +
                    $"profile — or lower the sweep's top.");
        }
        return EmSuitability.Yes;
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
