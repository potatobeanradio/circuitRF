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

    private SNP? _snp;

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
        OutOfRangePolicy     extrapPolicy = OutOfRangePolicy.WarnClamp)
    {
        _portCount    = portCount;
        _filePath     = absoluteFilePath;
        _interpMethod = interpMethod;
        _extrapPolicy = extrapPolicy;
        PortBranchIndices = new int[portCount];
        for (int k = 0; k < portCount; k++) PortBranchIndices[k] = -1;
    }

    private SNP LoadSnp()
    {
        if (_snp is not null) return _snp;
        if (!File.Exists(_filePath))
            throw new FileNotFoundException(
                $"SnP: Touchstone file not found: '{_filePath}'", _filePath);
        return _snp = TouchstoneIO.ReadFile(_filePath);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        var snp   = LoadSnp();
        double hz = omega / (2.0 * Math.PI);

        // Interpolate the stored S-parameters to this frequency, then convert to Z.
        var interpolated = RFNetwork.Interpolate(
            snp,
            [hz],
            _interpMethod,
            InterpolationFormat.RealImag,
            MatrixType.S,
            _extrapPolicy);
        var zMat = RFNetwork.SToZ(interpolated.Matrices[0], snp.Z0);

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
