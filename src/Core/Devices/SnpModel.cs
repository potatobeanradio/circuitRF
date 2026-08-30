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

    public SnpModel(
        int                  portCount,
        string               absoluteFilePath,
        InterpolationMethod  interpMethod = InterpolationMethod.CubicSpline,
        OutOfRangePolicy     extrapPolicy = OutOfRangePolicy.WarnClamp,
        InterpolationFormat  interpFormat = InterpolationFormat.RealImag)
    {
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

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        var snp    = LoadSnp(c);
        var interp = LoadInterpolator(c);
        double hz  = omega / (2.0 * Math.PI);

        // Interpolate the stored S-parameters to this frequency, then convert to Z.
        // The splines were fitted once (see LoadInterpolator); this is the evaluation only.
        var zMat = RFNetwork.SToZ(interp.Evaluate(hz), snp.Z0);

        int n       = _portCount;
        int refNode = c.ReferenceNode;

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
}
