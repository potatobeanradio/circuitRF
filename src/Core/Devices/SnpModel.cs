using System.Numerics;
using CircuitRF.Core.Elaboration;
using RfCore;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Frequency-domain N-port backed by a Touchstone (.sNp) file.
/// Stamps via Z(ω) branch-current expansion (linear-engine §4.1):
///   - One branch-current unknown per port.
///   - Constraint row k: V_{port_k} − V_{ref} − Σ_j Z[k,j]·I_j = 0
///   - KCL: branch k current flows from port node → reference node.
///
/// The reference node is ground (node 0) for ground-referenced blocks (N nets)
/// or the last net for floating-reference blocks (N+1 nets), per the N-or-N+1 rule.
/// The model receives the reference node index via ElaboratedComponent.ReferenceNode
/// and stamps it the same way regardless — the engine does not special-case it.
/// </summary>
public sealed class SnpModel : ComponentModel
{
    public override int       PortCount => _portCount;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly int                 _portCount;
    private readonly string              _filePath;
    private readonly InterpolationMethod _interpMethod;
    private readonly OutOfRangePolicy    _extrapPolicy;
    private readonly InterpolationFormat _interpFormat;

    private SNP?             _snp;
    private SnpInterpolator? _interp;

    /// <summary>
    /// Branch indices per port, set during each Stamp call.
    /// PortBranchIndices[k] = branch index for port k (0-based).
    /// -1 before first stamp.
    /// </summary>
    public int[] PortBranchIndices { get; private set; }

    /// <summary>The Touchstone file this block reads, as the factory resolved it (absolute; blank when none was set).
    /// brief-em3d-79 reads it to find the instance whose model is a 3D view's EM result.</summary>
    public string FilePath => _filePath;

    /// <summary>The S-parameter entry offsets this instance states (brief-sens-2), expanded over the tie; null when it
    /// states none or every one is 0 — then nothing about the block differs from a block with no such parameters.
    /// A reader of the block's S that does not go through <see cref="Stamp"/> must apply these through
    /// <see cref="SnpPerturbation.Apply"/> or refuse a nonzero one by name (<see cref="OffsetsNotAppliedBy"/>).</summary>
    public SnpOffsets? Offsets { get; }

    /// <summary>
    /// The refusal of a path that reads this block's FILE rather than its stamp, when the instance states offsets:
    /// "SnP 'SNP3''s S-parameter offsets are not applied by the envelope's rational fit". Null when there are none —
    /// using the unperturbed file silently is the defect this exists to prevent (brief-sens-2 R-sens2-2).
    /// </summary>
    public string? OffsetsNotAppliedBy(string instancePath, string path)
        => Offsets is null ? null : $"{Describe(instancePath)}'s S-parameter offsets are not applied by {path}; " +
                                    "set them to 0 to run it, or use an analysis that stamps the block";

    public SnpModel(
        int                  portCount,
        string               absoluteFilePath,
        InterpolationMethod  interpMethod = InterpolationMethod.CubicSpline,
        OutOfRangePolicy     extrapPolicy = OutOfRangePolicy.WarnClamp,
        InterpolationFormat  interpFormat = InterpolationFormat.RealImag,
        SnpOffsets?          offsets      = null)
    {
        Offsets       = offsets is { Any: true } ? offsets : null;
        _portCount    = portCount;
        _filePath     = absoluteFilePath;
        _interpMethod = interpMethod;
        _extrapPolicy = extrapPolicy;
        _interpFormat = interpFormat;
        PortBranchIndices = new int[portCount];
        for (int k = 0; k < portCount; k++) PortBranchIndices[k] = -1;
    }

    /// <summary>
    /// Loads the backing Touchstone, or refuses in a way that NAMES THE COMPONENT.
    ///
    /// <para>Owner-reported, 2026-08-26: an SnP with no file set reported only
    /// <c>"SnP: Touchstone file not found: '&lt;the workspace folder&gt;'"</c>. Two things were missing.
    /// The component was not identified at all — the only name in the line was the ANALYSIS's
    /// (<c>SchematicRunService</c> prefixes every analysis failure with its result name), so on a
    /// design with several SnPs, one of them nested, there was nothing to search for. And a blank
    /// <c>File</c> read as a wrong PATH rather than as a missing one, because an empty relative path
    /// combines to its own base directory.</para>
    ///
    /// <para>The instance path is the elaborated one — dotted, top-down, e.g. <c>X1.X2.SP1</c> — so it
    /// already IS the route through the hierarchy. That is why the refusal is raised HERE rather than
    /// in the factory: <see cref="ElaboratedComponent.InstancePath"/> exists only once the design is
    /// flattened, and the factory sees resolved parameters with no idea who they belong to. A blank
    /// path therefore reaches the model instead of being refused at construction (see
    /// <c>ComponentModelFactory.CreateSnpModel</c>).</para>
    /// </summary>
    private SNP LoadSnp(ElaboratedComponent c)
    {
        if (_snp is not null) return _snp;

        string who = Describe(c.InstancePath);

        if (string.IsNullOrWhiteSpace(_filePath))
            throw new FileNotFoundException(
                $"{who}: no Touchstone file is specified. Open the component's parameters and set " +
                "'File' to a Touchstone (.sNp) file.");

        // A folder is what a blank path used to turn into, and it can still be typed or pasted. It
        // is not "not found" — the path is right there — so saying so separately stops the user
        // hunting for a file that was never named.
        if (Directory.Exists(_filePath))
            throw new FileNotFoundException(
                $"{who}: its 'File' parameter names a folder, not a Touchstone file: '{_filePath}'.",
                _filePath);

        if (!File.Exists(_filePath))
            throw new FileNotFoundException(
                $"{who}: Touchstone file not found: '{_filePath}'.", _filePath);

        return _snp = TouchstoneCache.Get(_filePath);
    }

    /// <summary>
    /// The model's spline fit over its Touchstone file, built once and evaluated per frequency.
    ///
    /// <para>SP-P1, 2026-08-30: <see cref="Stamp"/> used to call <c>RFNetwork.Interpolate</c> with a
    /// one-element target array at every frequency point, and that call re-fits all 2·N² splines
    /// from scratch — the same coefficients, every point. On a ladder with twenty 2001-point SnPs
    /// that was 98 % of the S-parameter run. The fit depends only on (file, method, format, domain),
    /// none of which vary over a sweep, so it is built once per model and shared process-wide
    /// through <see cref="TouchstoneCache"/> — <see cref="Elaboration.Elaborator"/> news a fresh
    /// model at every point of a parametric sweep, so a per-instance field alone would not survive
    /// the sweep.</para>
    ///
    /// <para>The interpolator WRAPPER is per model, not shared: its out-of-range warning fires
    /// once per model per run rather than once per process, which is what keeps a re-run telling
    /// the user the same thing the first run did.</para>
    /// </summary>
    private SnpInterpolator LoadInterpolator(ElaboratedComponent c)
    {
        if (_interp is not null) return _interp;
        LoadSnp(c); // refuse here, naming the component, before the cache is asked for a fit
        return _interp = TouchstoneCache.GetInterpolator(
            _filePath, _interpMethod, _interpFormat, MatrixType.S, _extrapPolicy);
    }

    /// <summary>
    /// "SnP 'X1.X2.SP1'" — the elaborated instance path, verbatim. Nested or not, the dotted path IS
    /// the route through the hierarchy, read left to right, so it needs no gloss (owner, 2026-08-26 —
    /// an earlier "(inside 'X1' then 'X2')" suffix said the same thing twice). An unnamed instance
    /// (nothing but a type) gets just "SnP", so the caller never has to test for one.
    /// </summary>
    internal static string Describe(string instancePath)
        => string.IsNullOrWhiteSpace(instancePath) ? "SnP" : $"SnP '{instancePath}'";

    // ── The NDF passivation contract (brief-wsprobe-6 §3) ────────────────────

    /// <summary>
    /// <see cref="Core.Activity.BlackBox"/> at the TYPE level, because "an S-parameter block with
    /// gain hides its dependent sources": there is no transconductance to zero, only data. The
    /// answer is refined by <see cref="ActivityFor"/>, which measures the FILE.
    /// </summary>
    public override Activity Activity => Activity.BlackBox;

    /// <inheritdoc/>
    public override string? PassivationNote => _passivationNote;
    private string? _passivationNote;

    /// <summary>
    /// σ_max(S) at every sampled frequency of the file: at or below <c>1 + 1e-6</c> the block is
    /// passive and contributes nothing to <c>Δ − Δ0</c>; above it, the block has gain somewhere and
    /// the run is refused by name.
    ///
    /// <para><b>The FILE's own grid, not the run's</b> (which is why <paramref name="freqsHz"/> is
    /// unread here). Interpolating between two passive points cannot manufacture gain, and a file
    /// with gain outside the swept band still has it — a block that is an amplifier at 12 GHz is not
    /// made passive by sweeping to 6.</para>
    /// </summary>
    public override Activity ActivityFor(ElaboratedComponent c, IReadOnlyList<double> freqsHz)
    {
        const double Tol = 1e-6;
        try
        {
            // The PERTURBED matrix on the file's grid (brief-sens-2 R-sens2-2): a step that makes a passive file active
            // is refused by the analyses that refuse an active block, not hidden by measuring the file as written.
            var sig = RFNetwork.Passivity(SnpPerturbation.ApplyToFile(LoadSnp(c), Offsets));
            double worst = 0.0; int at = -1;
            for (int k = 0; k < sig.Length; k++)
                if (sig[k] > worst) { worst = sig[k]; at = k; }

            if (worst <= 1.0 + Tol)
            {
                _passivationNote = $"σ_max = {worst:G4} ≤ 1 over its {sig.Length} sampled point(s)";
                return Activity.Passive;
            }

            double hz = at >= 0 ? LoadSnp(c).Frequencies[at] : double.NaN;
            _passivationNote =
                $"σ_max = {worst:G4} > 1 at {hz / 1e9:G6} GHz — the file has gain there, and an " +
                "S-parameter block with gain hides its dependent sources";
            return Activity.BlackBox;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            _passivationNote = $"its Touchstone file could not be read ({ex.Message})";
            return Activity.BlackBox;
        }
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        var snp    = LoadSnp(c);
        var interp = LoadInterpolator(c);
        double hz  = omega / (2.0 * Math.PI);

        // Interpolate the stored S-parameters to this frequency, then convert to Z.
        // The splines were fitted once (see LoadInterpolator); this is the evaluation only.
        // brief-sens-2: the entry offsets, after interpolation — the same instance back when there are none.
        var sMat = SnpPerturbation.Apply(interp.Evaluate(hz), Offsets, hz);
        var zMat = RFNetwork.SToZ(sMat, snp.Z0);

        int n       = _portCount;
        int refNode = c.ReferenceNode;

        // brief-emdc-2 — a block with no Z (an ideal open, S = I: a 3D EM result's 0 Hz row through a DC block) is stamped as
        // its S, the wave definition IdealSBlockModel stamps, which has no singular case. Only then: every block that HAS a Z
        // stamps exactly as before.
        //
        // Designer round 32 — and so is a block whose Z is merely enormous. A part measured in SERIES (the usual form of a
        // supplier's capacitor or inductor file: S11 + S21 = 1) has no impedance matrix, since I − S is singular for every
        // series element. Its converted Z was rounding noise of order 1e17 Ω: usually solvable by luck, and wherever a sweep
        // point fell exactly on one of the file's own samples, garbage — a 3.9 pF part read |S11|² + |S21|² = 3.86, so a
        // filter built from it showed +1 dB of gain at single frequencies. Above 1e6·Z0 the S stamp is used; it is exact for
        // any S, so the threshold only decides which of two correct forms a well-conditioned block keeps.
        if (!AllFinite(zMat) || MaxMagnitude(zMat) > IllConditionedZOverZ0 * snp.Z0.Magnitude)
        {
            var nodes = new int[2 * n];
            var sArr  = new Complex[n, n];
            for (int k = 0; k < n; k++)
            {
                nodes[2 * k] = c.Nodes[k];
                nodes[2 * k + 1] = refNode;
                for (int j = 0; j < n; j++) sArr[k, j] = sMat[k, j];
            }
            IdealSBlockModel.StampWaveConstraints(mna, nodes, sArr, Enumerable.Repeat(snp.Z0, n).ToArray(), PortBranchIndices);
            return;
        }

        // Allocate one branch-current unknown per port.
        var branches = new int[n];
        for (int k = 0; k < n; k++)
        {
            branches[k]           = mna.AddBranch();
            PortBranchIndices[k]  = branches[k];
        }

        for (int k = 0; k < n; k++)
        {
            // KCL: branch k current flows from port k node → reference node.
            mna.AddBranchCurrent(branches[k], c.Nodes[k], refNode);

            // Constraint row k: V_{port_k}(+1) + V_{ref}(-1) + Σ_j I_j(-Z[k,j]) = 0
            // When refNode = 0 (ground), AddConstraint with node 0 is silently dropped.
            mna.AddConstraint(branches[k], c.Nodes[k], Complex.One);
            mna.AddConstraint(branches[k], refNode,    -Complex.One);

            for (int j = 0; j < n; j++)
                mna.AddBranchConstraint(branches[k], branches[j], -zMat[k, j]);
        }
    }

    /// <summary>Designer round 32 — the |Z|/|Z0| above which a block is stamped as its S rather than its Z: an (I − S) that
    /// close to singular (a series-measured part, an open) makes the Z stamp rounding noise.</summary>
    internal const double IllConditionedZOverZ0 = 1e6;

    private static double MaxMagnitude(NumFlat.Mat<Complex> m)
    {
        double max = 0;
        for (int i = 0; i < m.RowCount; i++)
            for (int j = 0; j < m.ColCount; j++)
                max = Math.Max(max, m[i, j].Magnitude);
        return max;
    }

    private static bool AllFinite(NumFlat.Mat<Complex> m)
    {
        for (int i = 0; i < m.RowCount; i++)
            for (int j = 0; j < m.ColCount; j++)
                if (!double.IsFinite(m[i, j].Real) || !double.IsFinite(m[i, j].Imaginary)) return false;
        return true;
    }
}
