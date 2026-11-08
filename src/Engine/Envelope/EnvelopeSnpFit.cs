// ================================================================
//  EnvelopeSnpFit.cs  —  S-parameter blocks in the circuit envelope, exactly
//
//  The envelope recovers C from two stamps a hair either side of each
//  harmonic, which is exact only for a stamp that is affine in jw.  A
//  Touchstone block is not: through its interpolator it is a first-order
//  tangent at k w0, and a filter's skirt, ripple and group-delay variation
//  across the signal band are lost.  So for an envelope run every SnP is
//  replaced by a rational (pole-residue) fit of its S matrix, realized
//  with extra linear unknowns:
//
//    u_j = V_j + Z0 I_j                         (= 2 sqrt(Z0) a_j)
//    V_i - Z0 I_i = sum_j D_ij u_j + sum C x     (= 2 sqrt(Z0) b_i)
//    (s - a_p) x_pj = u_j                        the only rows with s
//
//  A conjugate pole pair is a real 2x2 block, so the baseband envelope
//  stays real, and every state row is scaled by 1/|a_p| (its state by
//  |a_p|) so the stamp's entries are O(1) at RF.  The stamp is G + jw C
//  with the same unknowns at every w, w = 0 included — the two-stamp C
//  recovery is exact for it, and the DC solve, the harmonic-balance start
//  and the stepping all see one model, so the run starts at its own
//  equilibrium.  The substitution is scoped to the run: every other
//  analysis keeps stamping the real SnpModel.
//
//  A System Filter or Duplexer has the same problem (its S(w) is the
//  rational response of a prototype, stamped as b = S(w) a), so it gets
//  the same realization, fitted to samples of its own stamp.  So does any
//  other linear element whose stamp MEASURES as far from affine in jw
//  (its tangent error above the fit's tolerance): a Z(freq) block, a
//  Klopfenstein taper.  Coupled lines are not fitted (see NotAffine).  For
//  those, a fit that misses its tolerance or is not passive is refused,
//  and the element keeps its value and slope at each harmonic.  A two-
//  conductor line is not fitted at all: it is stepped as its delay
//  (EnvelopeLineDelay), substituted before this runs.
//
//  One fit per file (absolute path, write time, length and the fit
//  settings) and per block definition: parallel-in-time segments, sweep
//  points and several instances share it.
// ================================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Modeling;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Envelope;

/// <summary>What the envelope did with one SnP: the fit's order and error, and σ_max(S_fit) from DC to the top of the run.</summary>
public sealed record SnpFitReport(string Instance, int Order, double RelativeError, double SigmaMax, bool MetTolerance);

/// <summary>
/// A rational S matrix <c>S(s) = D + Σ_p R_p/(s − a_p)</c> (poles shared by every entry) realized as an MNA stamp that
/// is exactly <c>G + jω C</c>. Ports follow <see cref="SnpModel"/>: one branch current per port, flowing from the port
/// node into the block, all referenced to <see cref="ElaboratedComponent.ReferenceNode"/>.
/// </summary>
public sealed class RationalSnpModel : ComponentModel
{
    private readonly int _n;
    private readonly double _z0;
    private readonly List<(int Index, bool Pair)> _groups = [];

    public RationalSnpModel(int ports, double z0, RationalModel fit)
    {
        _n = ports;
        _z0 = z0;
        Fit = fit;
        for (int k = 0; k < fit.Poles.Length; k++)
        {
            bool pair = fit.Poles[k].Imaginary != 0;
            _groups.Add((k, pair));
            if (pair) k++;
        }
    }

    public RationalModel Fit { get; }
    public override int PortCount => _n;
    public override ModelKind Kind => ModelKind.Linear;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n = _n, rf = c.ReferenceNode;
        var s = new Complex(0, omega);
        var I = new int[n];
        for (int k = 0; k < n; k++)
        {
            I[k] = mna.AddBranch();
            mna.AddBranchCurrent(I[k], c.Nodes[k], rf);
        }
        // -g u_j on a row: u_j = V(node_j) - V(ref) + Z0 I_j
        void U(int row, int j, double g)
        {
            mna.AddConstraint(row, c.Nodes[j], -g);
            mna.AddConstraint(row, rf, g);
            mna.AddBranchConstraint(row, I[j], -g * _z0);
        }
        for (int i = 0; i < n; i++)
        {
            mna.AddConstraint(I[i], c.Nodes[i], Complex.One);
            mna.AddConstraint(I[i], rf, -Complex.One);
            mna.AddBranchConstraint(I[i], I[i], -_z0);
            for (int j = 0; j < n; j++)
                if (Fit.D[i * n + j] != 0) U(I[i], j, Fit.D[i * n + j]);
        }
        foreach (var (p, pair) in _groups)
        {
            var a = Fit.Poles[p];
            double m = a.Magnitude;
            for (int j = 0; j < n; j++)
            {
                if (!pair)
                {
                    int x = mna.AddBranch();
                    mna.AddBranchConstraint(x, x, (s - a.Real) / m);
                    U(x, j, 1);
                    for (int i = 0; i < n; i++) mna.AddBranchConstraint(I[i], x, -Fit.Residues[i * n + j][p].Real / m);
                    continue;
                }
                // x1 = z + z~, x2 = j(z - z~) with z = u/(s - a), z~ = u/(s - a*):
                //   (s - sigma) x1 - w x2 = 2u,   w x1 + (s - sigma) x2 = 0,   r z + r* z~ = Re(r) x1 + Im(r) x2
                int x1 = mna.AddBranch(), x2 = mna.AddBranch();
                mna.AddBranchConstraint(x1, x1, (s - a.Real) / m);
                mna.AddBranchConstraint(x1, x2, -a.Imaginary / m);
                U(x1, j, 2);
                mna.AddBranchConstraint(x2, x1, a.Imaginary / m);
                mna.AddBranchConstraint(x2, x2, (s - a.Real) / m);
                for (int i = 0; i < n; i++)
                {
                    var r = Fit.Residues[i * n + j][p];
                    mna.AddBranchConstraint(I[i], x1, -r.Real / m);
                    mna.AddBranchConstraint(I[i], x2, -r.Imaginary / m);
                }
            }
        }
    }

    /// <summary>The fitted S matrix at <paramref name="hz"/>.</summary>
    public Mat<Complex> S(double hz)
    {
        var m = new Mat<Complex>(_n, _n);
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
                m[i, j] = Fit.EvaluateAtHz(i * _n + j, hz);
        return m;
    }
}

public static class EnvelopeSnpFit
{
    /// <summary>Relative RMS error the fit must reach over the file's own points (see src/Engine/RESOLVED.md for why).</summary>
    public const double Tolerance = 1e-3;
    /// <summary>The highest order tried before the best fit is taken and a warning raised.</summary>
    public const int MaxOrder = 60;
    /// <summary>The tolerance a System Filter/Duplexer is fitted to. Its sampled S is exactly rational, so anything above
    /// round-off means the true order has not been reached: a duplexer stopped at 1e-3 took order 14 of its 20, and
    /// that fit was not passive (σmax 1.0012) where the order-20 one is.</summary>
    public const double BlockTolerance = 1e-9;
    /// <summary>σ_max(S_fit) above this is reported as a warning: the fitted block can make energy.</summary>
    public const double PassivityWarnAbove = 1 + 1e-3;

    // S is bounded: no proportional term (it would grow without limit above the file).
    private static readonly VectorFitSettings Settings = new() { Proportional = false };

    private sealed class Entry
    {
        public RationalModel? Model;
        public string? Refusal;
        public int Ports, Order, Points;
        public double Z0, RelativeError, FMin, FMax;
        public Mat<Complex> First, Last;
        public readonly ConcurrentDictionary<double, double> Sigma = new();
    }

    private static readonly ConcurrentDictionary<(string Path, long Ticks, long Length, double Tol, int MaxOrder), Lazy<Entry>> Cache = new();
    private static int _fitCount;

    /// <summary>Fits computed (cache misses) in this process.</summary>
    public static int FitCount => Volatile.Read(ref _fitCount);

    /// <summary>Puts every SnP of a netlist back the way the elaborator made it.</summary>
    public sealed class Substitution : IDisposable
    {
        private readonly ElaboratedNetlist _nl;
        private readonly List<(int Index, ElaboratedComponent Original)> _swapped = [];
        internal Substitution(ElaboratedNetlist nl) => _nl = nl;
        internal void Swap(int index, ElaboratedComponent replacement)
        {
            _swapped.Add((index, _nl.Components[index]));
            _nl.Components[index] = replacement;
        }
        public List<SnpFitReport> Reports { get; } = [];
        public void Dispose()
        {
            foreach (var (i, ec) in _swapped) _nl.Components[i] = ec;
            _swapped.Clear();
        }
    }

    /// <summary>
    /// Replaces each SnP of <paramref name="nl"/> by its realized fit for the length of one envelope run.
    /// <paramref name="topHz"/> is the top of the run (highest product plus the envelope band) — σ_max is measured
    /// from DC to there; <paramref name="stampHz"/> are the products the run stamps at, and the fit is compared with
    /// the file's own interpolator at DC and at each one outside the file. A file that cannot be fitted keeps its
    /// SnpModel (its tangent) and says so.
    /// </summary>
    public static Substitution Apply(ElaboratedNetlist nl, double topHz, IReadOnlyList<double> stampHz, double bandHz)
    {
        var sub = new Substitution(nl);
        var ws = stampHz.Select(f => 2 * Math.PI * f).Where(w => w > 2 * Math.PI * bandHz).Distinct().ToArray();
        for (int i = 0; i < nl.Components.Count; i++)
        {
            var ec = nl.Components[i];
            if (ec.Model is FilterModel or DuplexerModel && !ec.IsNonlinear) { FitSystemBlock(nl, sub, i, ec, topHz, stampHz, bandHz, generic: false); continue; }
            if (ec.Model is not SnpModel snp)
            {
                if (NotAffine(ec, ws, bandHz)) FitSystemBlock(nl, sub, i, ec, topHz, stampHz, bandHz, generic: true);
                continue;
            }
            // brief-de-2 — a Deembed undoes a network, so it advances whatever that network delays, and no causal rational
            // model can do that: a fit would be wrong or unstable. It keeps its stamp, approximated around each harmonic, and
            // the run says so.
            if (snp.Inverse)
            {
                nl.AddWarningOnce("env-snp-nofit:" + ec.InstancePath,
                    $"Deembed '{ec.InstancePath}': not fitted for the envelope (it undoes a network, so it would have to advance " +
                    $"what that network delays, which no causal model does); {FirstOrder}");
                continue;
            }
            // brief-sens-2: the fit is of the FILE; a block with entry offsets is refused by name rather than fitted
            // unperturbed, which would silently drop them.
            if (snp.OffsetsNotAppliedBy(ec.InstancePath, "the envelope's rational fit") is { } offsets)
                throw new InvalidOperationException(offsets + ".");
            // A missing file is the SnpModel's own refusal, which names the instance: leave it to stamp.
            if (string.IsNullOrWhiteSpace(snp.FilePath) || !File.Exists(snp.FilePath)) continue;
            string who = $"SnP '{ec.InstancePath}'";
            var e = Get(snp.FilePath);
            if (e.Model is null || e.Ports != snp.PortCount)
            {
                string why = e.Refusal ?? $"its file has {e.Ports} port(s), the block {snp.PortCount}";
                nl.AddWarningOnce("env-snp-nofit:" + ec.InstancePath, $"{who}: not fitted for the envelope ({why}); {FirstOrder}");
                continue;
            }
            var model = new RationalSnpModel(e.Ports, e.Z0, e.Model);
            sub.Swap(i, new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, ec.Parameters, model)
            {
                ReferenceNode = ec.ReferenceNode,
                Multiplicity = ec.Multiplicity,
            });

            double sigma = e.Sigma.GetOrAdd(topHz, t => SigmaMax(model, e, t));
            sub.Reports.Add(new SnpFitReport(ec.InstancePath, e.Order, e.RelativeError, sigma, e.RelativeError <= Tolerance));
            var parts = new List<string>();
            foreach (double f in new[] { 0.0 }.Concat(stampHz).Distinct())
            {
                if (f >= e.FMin && f <= e.FMax) continue;
                double d = MaxDiff(model.S(f), FileValue(ec, snp.FilePath, e, f));
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:G3} at {1}", d, f == 0 ? "DC" : $"{f / 1e9:0.####} GHz"));
            }
            Report(nl, ec.InstancePath, who, e, sigma, topHz, $"over its {e.Points} points",
                   parts.Count == 0 ? "" : string.Format(CultureInfo.InvariantCulture, "; outside {0:0.####}-{1:0.####} GHz, where the file has no data, it differs from the interpolator by {2}",
                                                           e.FMin / 1e9, e.FMax / 1e9, string.Join(", ", parts)));
        }
        return sub;
    }

    /// <summary>What a block whose fit was refused is, in each refusal's words.</summary>
    private const string FirstOrder = "it is approximated to first order around each harmonic (its value and slope there)";

    /// <summary>
    /// Whether <paramref name="ec"/>'s stamp is far enough from affine in jω, across the products at <paramref name="ws"/>,
    /// for a fit to improve on its value and slope there: its tangent error (<see cref="EnvelopeTangentCheck.ElementError"/>)
    /// above the fit's own <see cref="Tolerance"/>. Above round-off alone is not enough: a microstrip bend, tee, cross, via
    /// or spiral reads 1e-5 to 6e-5 (an affine stamp ~1e-12), which first order already holds to better than any fit, and a
    /// spiral's fit cost 21 s (src/Engine/RESOLVED.md, ENV-FIT). Sources, terminations and inductive branches are not measured (their stamps are
    /// constant or affine by construction, and stamping one alone re-numbers its branch). A coupled line is not fitted
    /// either: it keeps its value and slope at each harmonic, because a long one cannot be fitted and finding that out
    /// walked every order (132 s for a 300 mm MCLIN, src/Engine/RESOLVED.md, ENV-FIT). Nor is a wBond: its stamp reads 1e-8 to
    /// 2e-7, and its own branch currents are what its wire temperature is solved from (EnvelopeWireTemperature).
    /// </summary>
    private static bool NotAffine(ElaboratedComponent ec, double[] ws, double bandHz)
    {
        if (ws.Length == 0 || ec.IsNonlinear || ec.Model is IDcShortedTerminals
                || ec.Model is ModSourceModel or RationalSnpModel or DelayLineModel or IInductiveBranch or WBondModel
                or MutualInductanceModel or PortModel or TermModel or VdcModel or ToneSourceModelBase or TunerModel or P1ToneModel or PnToneModel)
            return false;
        try { return EnvelopeTangentCheck.ElementError(ec, ws, 2 * Math.PI * bandHz) > Tolerance; }
        catch (Exception) { return false; }    // an element that cannot be stamped alone is the run's to refuse
    }

    /// <summary>
    /// A System Filter or Duplexer: its S is rational (a prototype placed by a frequency transformation) but stamped as
    /// <c>b = S(ω)·a</c>, which the envelope would take as a tangent like any non-affine stamp. Its own stamp is sampled
    /// instead — terminated at its nodes, as a node-referenced S in 50 ohm (<see cref="EnvelopeTangentCheck.NodeS"/>) —
    /// from DC to the top of the run and densely across every product's band, and that is fitted and realized exactly
    /// as an SnP is. Nothing of the block's own model is read but its stamp. A <paramref name="generic"/> element (one the
    /// tangent measure found not affine) is fitted to <see cref="Tolerance"/>, not to the exactly-rational block's
    /// <see cref="BlockTolerance"/>, and a fit that misses it or is not passive is refused.
    /// </summary>
    private static void FitSystemBlock(ElaboratedNetlist nl, Substitution sub, int index, ElaboratedComponent ec,
                                       double topHz, IReadOnlyList<double> stampHz, double bandHz, bool generic)
    {
        double tol = generic ? Tolerance : BlockTolerance;
        string who = $"{ec.ComponentType} '{ec.InstancePath}'";
        var nodes = ec.Nodes.Where(n => n > 0).Distinct().ToArray();
        // Its S is exactly rational, so a few hundred samples determine it: a coarse anchor from DC to the top, and 41
        // across each product's band, where the run reads it. (A 1,903-point grid cost a 3-port duplexer 20 s.)
        var grid = Enumerable.Range(0, 101).Select(k => topHz * k / 100.0)
            .Concat(new[] { 0.0 }.Concat(stampHz).SelectMany(f => Enumerable.Range(-20, 41).Select(k => f + bandHz * k / 20.0)))
            .Where(f => f >= 0 && f <= topHz).Distinct().OrderBy(f => f).ToArray();
        // One fit per block DEFINITION and grid: the parameters, the way its nets meet ground, and the run's span.
        string shape = string.Join(",", ec.Nodes.Select(n => n == 0 ? "g" : Array.IndexOf(nodes, n).ToString(CultureInfo.InvariantCulture)));
        string key = string.Join(";", ec.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)) +
                     FormattableString.Invariant($"|{ec.ComponentType}|{shape}|{topHz:R}|{bandHz:R}|{string.Join(",", stampHz)}|{tol:R}|{MaxOrder}");
        var e = BlockCache.GetOrAdd(key, _ => new Lazy<Entry>(() => BuildBlock(ec, nodes, grid, tol), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (e.Model is null)
        {
            nl.AddWarningOnce("env-snp-nofit:" + ec.InstancePath, $"{who}: not fitted for the envelope ({e.Refusal}); {FirstOrder}");
            return;
        }
        var model = new RationalSnpModel(e.Ports, e.Z0, e.Model);
        double sigma = e.Sigma.GetOrAdd(topHz, t => SigmaMax(model, e, t));
        // Written as "not within" so a NaN error or σmax is a refusal, never a fit that steps
        if (generic && !(e.RelativeError <= Tolerance && sigma <= PassivityWarnAbove))
        {
            var inv = CultureInfo.InvariantCulture;
            string why = !(e.RelativeError <= Tolerance)
                ? string.Format(inv, "its rational fit reached a relative error of {0:G2} at order {1}, against a tolerance of {2:G2}", e.RelativeError, e.Order, Tolerance)
                : string.Format(inv, "its rational fit is not passive: σmax(S) = {0:0.####} between DC and {1:0.###} GHz", sigma, topHz / 1e9);
            nl.AddWarningOnce("env-snp-nofit:" + ec.InstancePath, $"{who}: not fitted for the envelope ({why}); {FirstOrder}");
            return;
        }
        // The samples are of the element's own stamp, its multiplier applied: the fit is all m copies, so it carries none.
        sub.Swap(index, new ElaboratedComponent(ec.ComponentType, ec.InstancePath, nodes, ec.Parameters, model));
        bool met = e.RelativeError <= Tolerance;
        sub.Reports.Add(new SnpFitReport(ec.InstancePath, e.Order, e.RelativeError, sigma, met));
        Report(nl, ec.InstancePath, who, e, sigma, topHz, $"over {e.Points} samples from DC to {topHz / 1e9:0.###} GHz", "");
    }

    private static readonly ConcurrentDictionary<string, Lazy<Entry>> BlockCache = new();

    private static Entry BuildBlock(ElaboratedComponent ec, int[] nodes, double[] grid, double tol)
    {
        var e = new Entry { Ports = nodes.Length, Z0 = EnvelopeTangentCheck.NodeSReference };
        int n = nodes.Length;
        var f = new List<double>();
        var resp = Enumerable.Range(0, n * n).Select(_ => new List<Complex>()).ToArray();
        foreach (double hz in grid)
        {
            Complex[,] sm;
            try { sm = EnvelopeTangentCheck.NodeS(ec, 2 * Math.PI * hz, nodes); }
            catch (InvalidOperationException) { continue; }          // a singular sample (an exact transmission zero at DC)
            bool finite = true;
            foreach (var v in sm) finite &= double.IsFinite(v.Real) && double.IsFinite(v.Imaginary);
            if (!finite) continue;
            f.Add(hz);
            for (int m = 0; m < n * n; m++) resp[m].Add(sm[m / n, m % n]);
        }
        if (f.Count < 2) { e.Refusal = "its response could not be sampled"; return e; }
        e.Points = f.Count; e.FMin = f[0]; e.FMax = f[^1];
        Interlocked.Increment(ref _fitCount);
        try
        {
            var fit = VectorFit.FitByError([.. f], [.. resp.Select(r => r.ToArray())], tol, MaxOrder, Settings);
            e.Model = fit.Model; e.Order = fit.Order; e.RelativeError = fit.RelativeError;
        }
        catch (ArithmeticException ex) { e.Refusal = "the rational fit failed: " + ex.Message; }
        return e;
    }

    /// <summary>The fit's note, and its warnings when it missed the tolerance or is not passive.</summary>
    private static void Report(ElaboratedNetlist nl, string inst, string who, Entry e, double sigma, double topHz, string over, string tail)
    {
        var inv = CultureInfo.InvariantCulture;
        nl.AddNoteOnce("env-snp-fit:" + inst, string.Format(inv,
            "{0}: rational fit of order {1} {2}, relative error {3:G2}; σmax(S) {4:0.####} from DC to {5:0.###} GHz{6}",
            who, e.Order, over, e.RelativeError, sigma, topHz / 1e9, tail));
        if (e.RelativeError > Tolerance)
            nl.AddWarningOnce("env-snp-poorfit:" + inst, string.Format(inv,
                "{0}: the rational fit reached a relative error of {1:G2} at order {2} (tolerance {3:G2}); the envelope uses that best fit",
                who, e.RelativeError, e.Order, Tolerance));
        if (sigma > PassivityWarnAbove)
            nl.AddWarningOnce("env-snp-active:" + inst, string.Format(inv,
                "{0}: the rational fit is not passive (σmax(S) = {1:0.####} between DC and {2:0.###} GHz); the envelope can gain energy there",
                who, sigma, topHz / 1e9));
    }

    private static Entry Get(string path)
    {
        string full = Path.GetFullPath(path);
        var fi = new FileInfo(full);
        var key = (full, fi.LastWriteTimeUtc.Ticks, fi.Length, Tolerance, MaxOrder);
        return Cache.GetOrAdd(key, k => new Lazy<Entry>(() => Build(k.Path), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static Entry Build(string path)
    {
        var e = new Entry();
        SNP snp;
        try { snp = TouchstoneCache.Get(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException) { e.Refusal = ex.Message; return e; }
        if (snp.FrequencyCount < 2) { e.Refusal = "it has fewer than two frequency points"; return e; }
        var s = snp.Type switch { MatrixType.Z => RFNetwork.ZToS(snp), MatrixType.Y => RFNetwork.YToS(snp), _ => snp };
        // SnpModel references every port to Z0 (port 1's); the realization uses the same one.
        if (s.Z0.Imaginary != 0 || !(s.Z0.Real > 0)) { e.Refusal = $"its reference impedance {s.Z0} is not a positive resistance"; return e; }
        int n = s.Matrices[0].RowCount, nf = s.FrequencyCount;
        var f = s.Frequencies;
        var resp = new Complex[n * n][];
        for (int m = 0; m < n * n; m++)
        {
            resp[m] = new Complex[nf];
            for (int k = 0; k < nf; k++) resp[m][k] = s.Matrices[k][m / n, m % n];
        }
        e.Ports = n; e.Z0 = s.Z0.Real; e.Points = nf;
        e.FMin = f.Min(); e.FMax = f.Max();
        e.First = s.Matrices[Array.IndexOf(f, e.FMin)]; e.Last = s.Matrices[Array.IndexOf(f, e.FMax)];
        Interlocked.Increment(ref _fitCount);

        // A real, frequency-independent file (a matched load, an attenuator) is its own constant term: no poles.
        bool constant = resp.All(r => r.All(v => Math.Abs(v.Imaginary) <= 1e-12 && (v - r[0]).Magnitude <= 1e-12));
        if (constant)
        {
            e.Model = new RationalModel { Poles = [], Residues = [.. resp.Select(_ => Array.Empty<Complex>())], D = [.. resp.Select(r => r[0].Real)], E = new double[n * n] };
            return e;
        }
        try
        {
            var fit = VectorFit.FitByError(f, resp, Tolerance, MaxOrder, Settings);
            e.Model = fit.Model; e.Order = fit.Order; e.RelativeError = fit.RelativeError;
        }
        catch (ArithmeticException ex) { e.Refusal = "the rational fit failed: " + ex.Message; }
        return e;
    }

    /// <summary>σ_max(S_fit) on a dense grid from DC to <paramref name="topHz"/>, plus the file's points and each pole's own frequency.</summary>
    private static double SigmaMax(RationalSnpModel model, Entry e, double topHz)
    {
        var grid = Enumerable.Range(0, 2001).Select(i => topHz * i / 2000.0)
            .Concat(e.Model!.Poles.Select(p => Math.Abs(p.Imaginary) / (2 * Math.PI)).Where(x => x <= topHz));
        double worst = 0;
        foreach (double hz in grid) worst = Math.Max(worst, RFNetwork.Passivity(model.S(hz)));
        return worst;
    }

    /// <summary>What the instance's own interpolator gives outside the file: the end value (the default clamp), or its
    /// linear extrapolation when the instance asks for one.</summary>
    private static Mat<Complex> FileValue(ElaboratedComponent ec, string path, Entry e, double hz)
    {
        string Str(string k) => ec.Parameters.TryGetValue(k, out var v) && v.Kind == ValueKind.String ? v.AsString() : "";
        if (!Str("ExtrapMode").Equals("extrapolate", StringComparison.OrdinalIgnoreCase))
            return hz < e.FMin ? e.First : e.Last;
        var method = Str("InterpMode").ToLowerInvariant() switch
        {
            "linear" => InterpolationMethod.Linear,
            "makima" => InterpolationMethod.Makima,
            _ => InterpolationMethod.CubicSpline,
        };
        var format = Str("InterpDomain").Equals("RI", StringComparison.OrdinalIgnoreCase) ? InterpolationFormat.RealImag : InterpolationFormat.MagPhase;
        return TouchstoneCache.GetInterpolator(path, method, format, MatrixType.S, OutOfRangePolicy.WarnExtrapolate).Evaluate(hz);
    }

    private static double MaxDiff(Mat<Complex> a, Mat<Complex> b)
    {
        double d = 0;
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++) d = Math.Max(d, (a[i, j] - b[i, j]).Magnitude);
        return d;
    }
}
