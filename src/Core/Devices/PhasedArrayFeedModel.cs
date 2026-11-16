// brief-paa-4 R-paa4-1 — the beamformer inside a Feed-mode Phased Antenna Array: the ideal N-way split of the Combiner with each path
// carrying its element's weight (docs/design/phased-array.md §4.4).
//
//   S[0,k] = S[k,0] = w_k(f) / √N,   w_k(f) = A_k·e^{jφ_k}·e^{−j2πf·τ_k},   every other entry zero
//
// which is CombinerModel's S with IL_k and Phase_k replaced by the element's quantised amplitude and phase and a delay: matched and
// lossless at the common port for N equal in-phase paths, so a uniform array's split is the Chain Budget's and its noise conventions hold
// (a taper's lost power is dissipated in the split, thermal by its S, as the Combiner's PathIL is). Port 0 is F, ports 1…N the elements in
// row-major order (D6); an element switched off has weight zero, so its port is a matched Z0 termination — what Off asks for.
//
// SPARSE, NOT AN IdealSBlockModel: that family keeps a dense (N+1)² S buffer and settles passivity by an SVD, which at 4096 elements is
// 268 MB and an O(N³) decomposition for a matrix whose only singular value is known in closed form. S here is rank two in structure,
// [[0, tᵀ], [t, 0]], so σ_max = ‖t‖; the stamp writes a few entries per element and no dense row (Stamp).

using System.Numerics;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>R-paa4-1 — the weighted N-way split of a Feed-mode Phased Antenna Array.</summary>
public sealed class PhasedArrayFeedModel : ComponentModel
{
    private readonly Complex[] _requested, _quantised;
    private readonly double[] _delay;
    private readonly Complex _z0;
    private double _sigma;

    /// <param name="requested">Each element's requested weight A·e^{jφ} before quantisation (zero for an element switched off).</param>
    /// <param name="quantised">The same after PhaseBits and AttenStep: what the split carries.</param>
    /// <param name="delay">Each element's true time delay, seconds.</param>
    /// <param name="z0">The reference impedance of every port.</param>
    public PhasedArrayFeedModel(Complex[] requested, Complex[] quantised, double[] delay, Complex z0)
    {
        _requested = requested;
        _quantised = quantised;
        _delay = delay;
        _z0 = z0.Real > 0 ? z0 : new Complex(50, 0);
        _sigma = Math.Sqrt(quantised.Sum(w => w.Magnitude * w.Magnitude) / quantised.Length);
        PortBranchIndices = [.. Enumerable.Repeat(-1, quantised.Length + 1)];
    }

    /// <summary>
    /// brief-pch-9 R-pch9-5 — takes <paramref name="from"/>'s weights (a beam a channel selected, weighed by the array's own
    /// <see cref="Arrays.ArraySteering.Weigh"/>) into this beamformer for the run: the selection is in memory, the netlist is not elaborated
    /// again, and the schematic is not written. Same element count and reference impedance.
    /// </summary>
    public void TakeWeights(PhasedArrayFeedModel from)
    {
        if (from.Elements != Elements) throw new ArgumentException($"{from.Elements} weights for {Elements} elements.");
        Array.Copy(from._requested, _requested, _requested.Length);
        Array.Copy(from._quantised, _quantised, _quantised.Length);
        Array.Copy(from._delay, _delay, _delay.Length);
        _sigma = from._sigma;
        _taken++;
    }

    /// <summary>brief-pch-9 — weights taken in place by <see cref="TakeWeights"/> on this thread: the selection's re-weighs.</summary>
    public static long WeightsTaken => _taken;
    [ThreadStatic] private static long _taken;

    /// <summary>A key for the weights as carried (quantised and delayed), for a cache that depends on them.</summary>
    public string WeightsKey()
    {
        ulong h = 14695981039346656037UL;
        void Mix(double x) { h ^= (ulong)BitConverter.DoubleToInt64Bits(x); h *= 1099511628211UL; h ^= h >> 29; }
        for (int i = 0; i < _quantised.Length; i++) { Mix(_quantised[i].Real); Mix(_quantised[i].Imaginary); Mix(_delay[i]); }
        return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + ":" + _quantised.Length;
    }

    /// <summary>brief-pch-9 — element <paramref name="e"/>'s requested weight and true delay as stored (no delay applied), for the closed-form
    /// weighing that starts from one exact one.</summary>
    internal Complex RequestedStored(int e) => _requested[e];
    internal double DelayStored(int e) => _delay[e];

    /// <summary>N, the number of elements.</summary>
    public int Elements => _quantised.Length;

    public override int PortCount => _quantised.Length + 1;
    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>Branch indices per port, set by each <see cref="Stamp"/>.</summary>
    public int[] PortBranchIndices { get; }

    /// <summary>The reference impedance of every port.</summary>
    public Complex Z0 => _z0;

    /// <summary>Element <paramref name="e"/>'s (0-based) weight at <paramref name="hz"/>, before quantisation.</summary>
    public Complex Requested(int e, double hz) => _requested[e] * Delayed(e, hz);

    /// <summary>Element <paramref name="e"/>'s (0-based) weight at <paramref name="hz"/>, as the split carries it.</summary>
    public Complex Quantised(int e, double hz) => _quantised[e] * Delayed(e, hz);

    private Complex Delayed(int e, double hz) => _delay[e] == 0 ? Complex.One : Complex.FromPolarCoordinates(1, -2 * Math.PI * hz * _delay[e]);

    /// <summary>The largest singular value of S: ‖w‖/√N. Above 1 the weights have gain, and the split is not passive.</summary>
    public double SigmaMax => _sigma;

    public override Activity Activity => _sigma <= 1 + 1e-9 ? Activity.Passive : Activity.BlackBox;

    public override Activity ActivityFor(ElaboratedComponent c, IReadOnlyList<double> freqsHz) => Activity;

    public override string? PassivationNote => _sigma <= 1 + 1e-9 ? null
        : $"σ_max = {_sigma:G4} > 1: the weights' RMS amplitude is above 1, so the beamformer has gain";

    /// <summary>brief-paa-7 — port <paramref name="p"/>'s (0 = F) noise-source node in <paramref name="c"/>, after the port pairs; 0 when none was
    /// placed.</summary>
    public int SourceNode(ElaboratedComponent c, int p) => c.Nodes.Length >= 3 * (Elements + 1) ? c.Nodes[2 * (Elements + 1) + p] : 0;

    /// <summary>
    /// brief-paa-7 — the noise-wave correlation of the split's ports at <paramref name="hz"/> and <paramref name="kelvin"/>, its Bosma noise in
    /// closed form: S = [[0, tᵀ], [t, 0]] makes I − S·Sᴴ = diag(1 − ‖t‖², I − t·tᴴ), a diagonal and one rank-one term, so a 4096-way split forms no
    /// dense matrix. Null while the weights have gain (not passive: no thermal noise).
    /// </summary>
    public ArrayCorrelation? NoiseCorrelation(double hz, double kelvin)
    {
        if (Activity != Activity.Passive) return null;
        int n = _quantised.Length;
        double kt = ArrayNoise.Boltzmann * kelvin, norm = 1 / Math.Sqrt(n);
        var t = new Complex[n + 1];
        double sum = 0;
        for (int k = 0; k < n; k++)
        {
            t[k + 1] = _quantised[k] * Delayed(k, hz) * norm;
            sum += t[k + 1].Real * t[k + 1].Real + t[k + 1].Imaginary * t[k + 1].Imaginary;
        }
        var rows = new List<(int, Complex)>[n + 1];
        rows[0] = [(0, kt * Math.Max(0, 1 - sum))];
        for (int k = 1; k <= n; k++) rows[k] = [(k, kt)];
        return new ArrayCorrelation(ArraySparseMatrix.FromRows(n + 1, rows), [(t, -kt)]);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n = _quantised.Length;
        double hz = Math.Abs(omega) / (2 * Math.PI);
        double root = Math.Sqrt(_z0.Real);
        bool real = _z0.Imaginary == 0;
        Complex z0 = omega < 0 ? Complex.Conjugate(_z0) : _z0;
        Complex back = real ? new Complex(root, 0) : Complex.Conjugate(z0) / root;
        Complex fwd = real ? new Complex(root, 0) : z0 / root;
        double norm = 1 / Math.Sqrt(n);

        var br = PortBranchIndices;
        for (int p = 0; p <= n; p++) br[p] = mna.AddBranch();
        for (int p = 0; p <= n; p++) mna.AddBranchCurrent(br[p], c.Nodes[2 * p], c.Nodes[2 * p + 1]);

        // Each port's own half of its row: (v_p − conj(Z0)·i_p)/√Re Z0. brief-paa-7 — and its noise source: the row is 2b − 2S·a, so −2·V_src
        // makes it b − S·a = V_src, V_src the voltage a noise current puts on a 1 S conductance (SourceNode).
        for (int p = 0; p <= n; p++)
        {
            int np = c.Nodes[2 * p], nm = c.Nodes[2 * p + 1];
            mna.AddConstraint(br[p], np, new Complex(1 / root, 0));
            if (nm > 0) mna.AddConstraint(br[p], nm, new Complex(-1 / root, 0));
            mna.AddBranchConstraint(br[p], br[p], -back);
            int src = SourceNode(c, p);
            if (src > 0)
            {
                mna.AddConstraint(br[p], src, new Complex(-2, 0));
                mna.AddAdmittance(src, 0, Complex.One);
            }
        }
        // − S_pq·(v_q + Z0·i_q)/√Re Z0. Element k's row reads the common port's wave: two entries each, so v_0 and i_0 are dense
        // COLUMNS, which a minimum-degree ordering on AᵀA takes as a star and eliminates last. The common port's own row would be the
        // dense ROW Σ_k t_k·a_k, and a dense row makes AᵀA a full clique: the LU of a 48 × 48 feed went dense (minutes, gigabytes). So
        // that sum is a running chain of auxiliary unknowns, σ_k = σ_(k−1) + t_k·a_k, each row holding four entries; the common port's
        // row reads the last. The same equations exactly, summed in element order.
        void Couple(int p, int q, Complex s)
        {
            int qp = c.Nodes[2 * q], qm = c.Nodes[2 * q + 1];
            mna.AddConstraint(br[p], qp, -s / root);
            if (qm > 0) mna.AddConstraint(br[p], qm, s / root);
            mna.AddBranchConstraint(br[p], br[q], -s * fwd);
        }
        int previous = -1;
        for (int k = 0; k < n; k++)
        {
            Complex t = _quantised[k] * Delayed(k, hz) * norm;
            if (t == Complex.Zero) continue;
            if (omega < 0) t = Complex.Conjugate(t);
            Couple(k + 1, 0, t);
            // σ_k − σ_(k−1) − t_k·(v_k + Z0·i_k)/√Re Z0 = 0
            int sigma = mna.AddBranch();
            mna.AddBranchConstraint(sigma, sigma, Complex.One);
            if (previous >= 0) mna.AddBranchConstraint(sigma, previous, -Complex.One);
            int kp = c.Nodes[2 * (k + 1)], km = c.Nodes[2 * (k + 1) + 1];
            mna.AddConstraint(sigma, kp, -t / root);
            if (km > 0) mna.AddConstraint(sigma, km, t / root);
            mna.AddBranchConstraint(sigma, br[k + 1], -t * fwd);
            previous = sigma;
        }
        if (previous >= 0) mna.AddBranchConstraint(br[0], previous, -Complex.One);
    }
}
