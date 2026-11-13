using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// The <b>large-signal small-signal</b> (conversion-matrix) solve — brief-wsprobe-5 §3, and the
/// engine capability the WSProbe's <c>wsp</c> under harmonic balance is built on.
///
/// <para>At a converged HB operating point the nonlinear devices are periodically time-varying
/// conductances and capacitances. A vanishingly small stimulus at a frequency <c>ω_ss</c> that is in
/// general <b>not</b> on the HB grid therefore produces a response at every sideband
/// <c>ω_k = ω_ss + k·ω0</c>, and the linear system relating them is the conversion matrix
/// (S. A. Maas, <i>Nonlinear Microwave and RF Circuits</i>, 2nd ed., ch. 3; A. Suarez,
/// <i>Analysis and Design of Autonomous Microwave Circuits</i>, 2009, ch. 1–2). The WSProbe reads
/// the responses at <c>ω_ss</c> itself — the <c>k = 0</c> sideband — which is the large-signal
/// counterpart of the S-parameter <c>wsp</c>, and every derived metric of brief-wsprobe-2/3 applies
/// to it unchanged (T. A. Winslow, <i>General Circuit Analysis Using The WSProbe</i> (2023), §4.2:
/// "Linear analysis, harmonic balance, or AC analysis can all be accommodated").</para>
///
/// <para><b>The HB Jacobian is this analysis at <c>ω_ss = 0</c>.</b> At that one frequency the
/// <c>±k</c> unknowns are conjugates of each other, which is why <see cref="HbNewton.BuildJ"/> folds
/// them into a one-sided real-split form; <see cref="FoldToRealSplit"/> performs that folding and is
/// the gate (R-wsp5-9(b)). Nothing here is a second copy of the Jacobian — the two-sided
/// coefficients of R-wsp5-2 are the <i>same</i> spectra the Newton solve produced, reused.</para>
///
/// <para><b>Two-sided coefficients (R-wsp5-2 — a frozen convention).</b> <see cref="HbFft"/> and
/// <see cref="HbApft"/> report FULL-AMPLITUDE one-sided phasors: the DC bin is divided by N and an
/// AC bin by N/2, so an AC bin is <i>twice</i> the two-sided Fourier coefficient of the real
/// waveform. The conversion matrix is written in two-sided coefficients:</para>
/// <code>
///   G⁽²⁾[0]  = G[0]
///   G⁽²⁾[k]  = G[k] / 2          k &gt; 0
///   G⁽²⁾[−k] = conj(G[k]) / 2    k &gt; 0
/// </code>
/// <para>and likewise for <c>C</c> and each <c>w ≥ 2</c> bucket. Getting the factor of two wrong
/// here is invisible at low drive and doubles the mixing terms at high drive.</para>
/// </summary>
public static class HbSmallSignal
{
    // ── The sideband family ──────────────────────────────────────────────────

    /// <summary>
    /// One sideband family and the two-sided device spectra indexed by the DIFFERENCE of two
    /// sidebands — the only thing the conversion matrix needs to know about the tone regime. The
    /// single-tone implementation indexes a scalar harmonic; the lattice one indexes a mixing
    /// vector. Everything downstream is shared.
    /// </summary>
    public interface ISidebands
    {
        /// <summary>Number of sidebands: <c>2·K_ss + 1</c> single-tone, <c>2M − 1</c> on a lattice.</summary>
        int Count { get; }

        /// <summary>The sideband whose mixing offset is zero — where the probe injects and where
        /// every reading is taken.</summary>
        int Zero { get; }

        /// <summary>The mixing offset <c>Ω_s</c> in rad/s, so <c>ω_s = ω_ss + Ω_s</c>. Signed.</summary>
        double Offset(int s);

        /// <summary>Two-sided <c>G⁽²⁾</c> at the difference of sidebands <paramref name="s"/> and
        /// <paramref name="t"/>. Zero when that difference is outside the retained spectrum.</summary>
        Complex G(int n, int m, int s, int t);

        /// <summary>Two-sided <c>C⁽²⁾</c> at the same difference.</summary>
        Complex C(int n, int m, int s, int t);

        /// <summary>Number of <c>w ≥ 2</c> weighted buckets.</summary>
        int Buckets { get; }

        /// <summary>Two-sided <c>Dw⁽²⁾</c> of bucket <paramref name="b"/> at the same difference.</summary>
        Complex Dw(int b, int n, int m, int s, int t);

        /// <summary><c>H[w](ω)</c> of bucket <paramref name="b"/>. Conjugated for <c>ω &lt; 0</c>,
        /// because no model in circuitRF is ever evaluated at a negative frequency
        /// (<c>src/Engine/HarmonicBalance/CLAUDE.md</c>, "Models are evaluated only at ω ≥ 0").</summary>
        Complex BucketWeight(int b, double omega);

        /// <summary>
        /// True when <paramref name="omegaSs"/> makes the sideband family closed under negation —
        /// <c>2·ω_ss</c> is itself a mixing frequency, so <c>ω_ss</c> and <c>−ω_ss</c> land on the
        /// SAME family and the responses at the two are conjugates rather than independent. The
        /// formulation solves one family with the stimulus at its zero sideband, which is only the
        /// whole stimulus when the two families are disjoint; see <see cref="DegeneracyNote"/>.
        /// </summary>
        bool IsDegenerate(double omegaSs);

        /// <summary>
        /// Where the difference of sidebands <paramref name="s"/> and <paramref name="t"/> sits in a one-sided spectrum of
        /// the kind <see cref="G"/> reads — the harmonic <c>|s − t|</c> on one tone, the difference lattice's index on several
        /// — and whether it was reached through a negation, so the stored phasor is conjugated. <c>-1</c> when it is outside
        /// the retained spectrum. What brief NZ-5's cyclostationary noise reads its sideband correlations through, so the
        /// bookkeeping is the conversion matrix's own.
        /// </summary>
        (int Index, bool Conj) DifferenceIndex(int s, int t);
    }

    /// <summary>The relative tolerance on the commensurability test of
    /// <see cref="ISidebands.IsDegenerate"/>.</summary>
    private const double CommensurateTol = 1e-9;

    /// <summary>
    /// The sentence a run prints once when a probe frequency landed on a degenerate point. Kept
    /// beside the test so the explanation and the rule cannot drift apart.
    /// </summary>
    public static string DegeneracyNote(double ssFreqHz, string what) =>
        $"WSProbe small-signal sweep: the probe frequency {ssFreqHz / 1e9:G6} GHz is commensurate " +
        $"with {what} at order 2, so the sidebands ω_ss + kω0 and their negatives are the SAME set " +
        "of frequencies — the response at +ω_ss and at −ω_ss are then conjugates of each other " +
        "rather than independent, and the conversion system this analysis solves (one sideband " +
        "family, stimulus at its own sideband) is not the whole stimulus there. The wsp entries at " +
        "that frequency are NaN rather than a plausible wrong number. Offset the grid so no point " +
        "lands on a half-multiple of a tone (an odd SSNpts over the same span usually does it); a " +
        "parametric instability at ω0/2 is found by the points either side of it, which is what " +
        "the sweep is for.";

    // ── Single-tone sidebands ────────────────────────────────────────────────

    /// <summary>
    /// <c>ω_k = ω_ss + k·ω0</c> for <c>k = −K_ss … K_ss</c>, over the one-sided full-amplitude
    /// spectra <see cref="HbNewton.Solve"/> reports. Sideband index <c>s</c> carries
    /// <c>k = s − K_ss</c>, so <see cref="Zero"/> is <c>K_ss</c> and the difference of two sidebands
    /// is simply <c>s − t</c>.
    /// </summary>
    public sealed class ToneSidebands : ISidebands
    {
        private readonly Complex[,,] _g;
        private readonly Complex[,,] _c;
        private readonly IReadOnlyList<HigherWeightBucket> _buckets;
        private readonly double _omega0;
        private readonly int    _kss;

        public ToneSidebands(Complex[,,] g, Complex[,,] c,
            IReadOnlyList<HigherWeightBucket>? buckets, int kss, double omega0)
        {
            _g = g; _c = c; _buckets = buckets ?? []; _kss = kss; _omega0 = omega0;
        }

        public int    Count      => 2 * _kss + 1;
        public int    Zero       => _kss;
        public double Offset(int s) => (s - _kss) * _omega0;

        public Complex G(int n, int m, int s, int t) => TwoSided(_g, n, m, s - t);
        public Complex C(int n, int m, int s, int t) => TwoSided(_c, n, m, s - t);

        public int Buckets => _buckets.Count;
        public Complex Dw(int b, int n, int m, int s, int t) => TwoSided(_buckets[b].Dw, n, m, s - t);

        public Complex BucketWeight(int b, double omega) => HbNewton.WeightAt(_buckets[b].Model, _buckets[b].W, omega);

        /// <summary>2·ω_ss an exact multiple of ω0 — the whole condition, and independent of
        /// K_ss: it is a statement about the frequencies, not about the truncation.</summary>
        public bool IsDegenerate(double omegaSs)
        {
            if (_omega0 <= 0) return false;
            double r = 2.0 * omegaSs / _omega0;
            return Math.Abs(r - Math.Round(r)) <= CommensurateTol * Math.Max(1.0, Math.Abs(r));
        }

        public (int Index, bool Conj) DifferenceIndex(int s, int t) => (Math.Abs(s - t), s - t < 0);

        /// <summary>R-wsp5-2 on a scalar harmonic index: <c>G⁽²⁾[d]</c> from the one-sided
        /// full-amplitude <c>G[|d|]</c>, conjugated for <c>d &lt; 0</c> and halved for
        /// <c>d ≠ 0</c>.</summary>
        private static Complex TwoSided(Complex[,,] a, int n, int m, int d)
        {
            int k = Math.Abs(d);
            if (k > a.GetLength(2) - 1) return Complex.Zero;
            var v = a[n, m, k];
            if (d < 0) v = Complex.Conjugate(v);
            return d == 0 ? v : v * 0.5;
        }
    }

    // ── Lattice sidebands (two-tone; R-wsp5-7) ───────────────────────────────

    /// <summary>
    /// The same family over a <see cref="MixingLattice"/>: the sidebands are <c>ω_ss + Ω</c> for
    /// every <c>Ω</c> in the FULL retained diamond (each half-space representative and its
    /// negation, DC once — <c>2M − 1</c> of them), and the device spectra are looked up at the
    /// difference of two mixing vectors in a lattice of TWICE the order, because that difference
    /// can reach order <c>2·MaxMixOrder</c> exactly as <c>k − i</c> reaches <c>2K</c> single-tone.
    /// </summary>
    public sealed class LatticeSidebands : ISidebands
    {
        private readonly MixingLattice _l;         // the run's own lattice, order O
        private readonly MixingLattice _l2;        // the difference lattice, order 2·O
        private readonly Complex[,,]   _g;         // [N, N, M2] one-sided full-amplitude
        private readonly Complex[,,]   _c;
        private readonly IReadOnlyList<(ComponentModel Model, int W, Complex[,,] Dw)> _buckets;
        private readonly double[]      _omegas;    // per-tone ω_t
        private readonly int[]         _mixOf;     // sideband → half-space rep index
        private readonly int[]         _signOf;    // sideband → ±1
        private readonly double[]      _offset;    // sideband → Ω_s
        private readonly int[]         _work;
        private readonly Dictionary<long, (int Idx, bool Conj)> _diff = [];

        /// <param name="ssOrder">The sideband diamond order — <c>SSMaxHarm</c>'s lattice form. Every
        /// retained representative of order at or below it contributes a <c>±</c> pair of sidebands;
        /// DC contributes one. Pass the lattice's own <c>MaxMixOrder</c> for no truncation.</param>
        /// <param name="buckets">The <c>w ≥ 2</c> derivative spectra over <paramref name="diffLattice"/>,
        /// as <see cref="LatticeSpectra"/> returns them; null or empty when no device has any.</param>
        public LatticeSidebands(MixingLattice lattice, MixingLattice diffLattice,
            Complex[,,] g, Complex[,,] c, double[] omegas, int ssOrder,
            IReadOnlyList<(ComponentModel Model, int W, Complex[,,] Dw)>? buckets = null)
        {
            _l = lattice; _l2 = diffLattice; _g = g; _c = c; _omegas = omegas; _buckets = buckets ?? [];
            _work = new int[lattice.ToneCount];

            int M = lattice.MixCount;
            var mix = new List<int>(2 * M) { 0 };
            var sgn = new List<int>(2 * M) { +1 };
            for (int m = 1; m < M; m++)
            {
                if (lattice.OrderOf(m) > ssOrder) continue;
                mix.Add(m); sgn.Add(+1); mix.Add(m); sgn.Add(-1);
            }
            _mixOf  = [.. mix];
            _signOf = [.. sgn];
            _offset = new double[_mixOf.Length];
            for (int s = 0; s < _mixOf.Length; s++)
                _offset[s] = _signOf[s] * _l.OmegaOf(_mixOf[s], _omegas);

            // Zero is sideband 0 by construction: rep 0 of every lattice is DC.
            Zero = 0;
        }

        public int    Count => _mixOf.Length;
        public int    Zero  { get; }
        public double Offset(int s) => _offset[s];

        public Complex G(int n, int m, int s, int t) => TwoSided(_g, n, m, s, t);
        public Complex C(int n, int m, int s, int t) => TwoSided(_c, n, m, s, t);

        public int Buckets => _buckets.Count;
        public Complex Dw(int b, int n, int m, int s, int t) => TwoSided(_buckets[b].Dw, n, m, s, t);
        public Complex BucketWeight(int b, double omega) => HbNewton.WeightAt(_buckets[b].Model, _buckets[b].W, omega);

        /// <summary>2·ω_ss equal to a retained mixing frequency — the lattice form of
        /// <see cref="ToneSidebands.IsDegenerate"/>. Tested over the DIFFERENCE lattice, which is
        /// the widest set of mixing frequencies this solve can see.</summary>
        public bool IsDegenerate(double omegaSs)
        {
            double two = 2.0 * Math.Abs(omegaSs);
            for (int m = 0; m < _l2.MixCount; m++)
            {
                double w = Math.Abs(_l2.OmegaOf(m, _omegas));
                if (Math.Abs(two - w) <= CommensurateTol * Math.Max(1.0, Math.Max(two, w)))
                    return true;
            }
            return false;
        }

        public (int Index, bool Conj) DifferenceIndex(int s, int t) => Difference(s, t);

        private Complex TwoSided(Complex[,,] a, int n, int m, int s, int t)
        {
            var (idx, conj) = Difference(s, t);
            if (idx < 0) return Complex.Zero;
            var v = a[n, m, idx];
            if (conj) v = Complex.Conjugate(v);
            return idx == 0 ? v : v * 0.5;
        }

        /// <summary>
        /// The difference-lattice index of <c>k_s − k_t</c>, and whether it was reached through the
        /// NEGATION of a half-space representative (in which case the spectrum is conjugated). A
        /// half-space lattice stores one of each <c>±</c> pair, so a difference is found either
        /// directly or as its own negative — never neither, since the order of the difference is at
        /// most twice the order of the lattice the sidebands come from.
        /// </summary>
        private (int Idx, bool Conj) Difference(int s, int t)
        {
            long key = ((long)s << 20) | (uint)t;
            if (_diff.TryGetValue(key, out var hit)) return hit;

            var ks = _l.ToneOf(_mixOf[s]); int ss = _signOf[s];
            var kt = _l.ToneOf(_mixOf[t]); int st = _signOf[t];
            for (int i = 0; i < _work.Length; i++) _work[i] = ss * ks[i] - st * kt[i];

            int idx = _l2.IndexOf(_work);
            bool conj = false;
            if (idx < 0)
            {
                for (int i = 0; i < _work.Length; i++) _work[i] = -_work[i];
                idx  = _l2.IndexOf(_work);
                conj = idx >= 0;
            }
            return _diff[key] = (idx, conj);
        }
    }

    // ── The conversion matrix ────────────────────────────────────────────────

    /// <summary>
    /// <c>J_ss</c> at one probe frequency — R-wsp5-4, dense and complex, row-major over
    /// <c>(n, s)</c> with <c>n</c> the interface node and <c>s</c> the sideband:
    /// <code>
    ///   J_ss[(n,s), (m,t)] = δ_st·Y_NN(ω_s)[n,m]
    ///                      + G⁽²⁾[n,m, s−t]
    ///                      + j·ω_s·C⁽²⁾[n,m, s−t]
    ///                      + Σ_w H[w](ω_s)·Dw⁽²⁾[n,m, s−t]
    /// </code>
    /// No half-amplitude weights, no real-split, no DC special cases: at <c>ω_ss ≠ 0</c> every
    /// sideband is an ordinary complex unknown. <paramref name="yAt"/> supplies
    /// <c>Y_NN</c> at a signed frequency — see <see cref="YNNAt"/> for the conjugate and DC rules
    /// it must obey (R-wsp5-3).
    /// </summary>
    public static Complex[] BuildJss(
        ISidebands sb, int nInt, double omegaSs, Func<double, Complex[,]> yAt)
    {
        int sc = sb.Count, nc = nInt * sc;
        var j  = new Complex[nc * nc];

        // Y_NN and the bucket weights depend only on the ROW's frequency, so both are gathered
        // once per sideband rather than once per block.
        var yS = new Complex[sc][,];
        var hS = sb.Buckets > 0 ? new Complex[sc, sb.Buckets] : null;
        for (int s = 0; s < sc; s++)
        {
            double omegaS = omegaSs + sb.Offset(s);
            yS[s] = yAt(omegaS);
            for (int b = 0; b < sb.Buckets; b++) hS![s, b] = sb.BucketWeight(b, omegaS);
        }

        for (int n = 0; n < nInt; n++)
        for (int s = 0; s < sc; s++)
        {
            int    row    = n * sc + s;
            double omegaS = omegaSs + sb.Offset(s);
            var    y      = yS[s];

            for (int m = 0; m < nInt; m++)
            for (int t = 0; t < sc; t++)
            {
                var v = sb.G(n, m, s, t) + new Complex(0, omegaS) * sb.C(n, m, s, t);
                for (int b = 0; b < sb.Buckets; b++) v += hS![s, b] * sb.Dw(b, n, m, s, t);
                if (s == t && n < y.GetLength(0) && m < y.GetLength(1)) v += y[n, m];
                j[row * nc + m * sc + t] = v;
            }
        }
        return j;
    }

    /// <summary>
    /// <c>Y_NN</c> at a SIGNED frequency, R-wsp5-3: the DC formulation at zero, the extractor's own
    /// stamp above it, and the complex conjugate of <c>|ω|</c>'s below it.
    ///
    /// <para><b>Never a model call at a negative frequency</b> — the linear network is real in the
    /// time domain, so <c>Y(−ω) = conj(Y(ω))</c> is a theorem rather than an approximation, and the
    /// Touchstone interpolators would not survive a negative argument
    /// (<c>src/Engine/HarmonicBalance/CLAUDE.md</c>, "Models are evaluated only at ω ≥ 0"). This is
    /// the same rule <c>ExtractMix</c> applies on the two-tone path.</para>
    /// </summary>
    public static Complex[,] YNNAt(HbLinearExtractor extractor, double omega, Complex[,] yDc)
    {
        if (Math.Abs(omega) < 1e-12) return yDc;
        if (omega > 0) return extractor.LinearPartitionAt(omega).YNN;
        var y = extractor.LinearPartitionAt(-omega).YNN;
        int n = y.GetLength(0), m = y.GetLength(1);
        var c = new Complex[n, m];
        for (int i = 0; i < n; i++)
        for (int k = 0; k < m; k++) c[i, k] = Complex.Conjugate(y[i, k]);
        return c;
    }

    // ── The probe solves ─────────────────────────────────────────────────────

    /// <summary>One WSProbe as this solve addresses it. The branch index is read off the model at
    /// solve time, exactly as the S-parameter engine does, because the HB assembly numbers branches
    /// its own way.</summary>
    public readonly record struct ProbeSite(int ComponentIndex, string Label, int Idx,
                                             int GNode, int LNode);

    /// <summary>Structural counters — the shape of the work, never its wall clock (overview D-14,
    /// R-wsp5-9(i)). Asserted by the gate; WSP-8 lowers them against the same fixture.</summary>
    public sealed class Counters
    {
        /// <summary>Linear-partition extractions requested — factorisations OR cache hits.</summary>
        public int LinearPartitions;
        /// <summary>Dense factorisations of <c>J_ss</c>.</summary>
        public int DenseFactorizations;
        /// <summary>Dense back-substitutions against a factored <c>J_ss</c>.</summary>
        public int DenseSolves;
        /// <summary>Sparse back-substitutions against the linear partition.</summary>
        public int SparseSolves;
        /// <summary>Probe frequencies skipped as degenerate.</summary>
        public int DegeneratePoints;

        // ── WSP-8's own counters (R-wsp8-9) ─────────────────────────────────
        /// <summary>Sparse LU factorisations of the linear partition performed BY the small-signal
        /// sweep. Zero after the first operating point of a drive sweep is the whole claim of
        /// brief-wsprobe-8, and this is the counter that states it.</summary>
        public int SparseFactorizations;
        /// <summary>Transposed (Hermitian) back-substitutions — the rows of <c>M⁻¹</c> of
        /// R-wsp8-1.</summary>
        public int TransposedSolves;
        /// <summary>Stamps of the linear partition. One per frequency per operating point is what
        /// the cache's bit-for-bit certificate costs, and it is not free: it is the price of an
        /// invalidation rule no caller has to remember (R-wsp8-4).</summary>
        public int Stamps;
        /// <summary>Cache entries reused, and entries computed.</summary>
        public int CacheHits, CacheMisses;
        /// <summary>The sideband census of R-wsp8-5: distinct <c>|ω|</c> actually factored, distinct
        /// SIGNED sideband frequencies, and the number the sweep would visit if none coincided.</summary>
        public int DistinctSidebandFrequencies, SignedSidebandFrequencies, TotalSidebandVisits;
    }

    /// <summary>
    /// The <c>wsp</c> matrix at every probe frequency — R-wsp5-5, one entry per element of
    /// <paramref name="ssFreqsHz"/>, each <c>2N × 2N</c> with the document's 1-based layout
    /// (rows = stimulus probe, columns = response probe; Eq. 31–36).
    ///
    /// <para>Per probe <c>p</c> and per injection — a unit series voltage <c>vS</c> in its branch
    /// (<c>−</c> at G, <c>+</c> at L) and a unit shunt current <c>iP</c> into its G node, with every
    /// independent source OFF because this is a perturbation of an already-solved operating
    /// point:</para>
    /// <list type="number">
    ///   <item>the linear partition at <c>ω_ss</c> with the injection alone gives the open-circuit
    ///         interface voltages, and <c>I_src = −Y_NN(ω_ss)·V_oc</c> is the Norton excitation the
    ///         conversion system takes at its <c>k = 0</c> block (the extractor's own
    ///         <c>ISrcFromVoc</c> pattern);</item>
    ///   <item><c>J_ss·V = −RHS</c> for the interface voltages at every sideband;</item>
    ///   <item><c>I_nl,0 = −(Y_NN(ω_ss)·V[·,0] + I_src)</c>, the balance
    ///         <c>Y·V + I_src + I_nl = 0</c> read at <c>k = 0</c>;</item>
    ///   <item>the linear partition again with the injection AND <c>I_nl,0</c> at the interface,
    ///         read at every probe as <c>iS_j = x[br_j]</c> and <c>vP_j = x[nG_j − 1]</c>.</item>
    /// </list>
    /// <para>The factorisation at <c>ω_ss</c> and the dense factorisation of <c>J_ss</c> are shared
    /// by all <c>2N</c> right-hand sides of that <c>ω_ss</c>. The series-source sign is
    /// <c>−1</c> — the same one place a wrong guess survives every symmetric test that
    /// brief-wsprobe-1 R-wsp1-14(a) holds shut for the linear engine.</para>
    /// </summary>
    /// <summary>
    /// <b>WSP-5's straightforward path, kept as the oracle brief-wsprobe-8's fast path is measured
    /// against</b> (R-wsp8-9(b)). Every sparse factorisation and every sparse solve of the
    /// small-signal sweep happens here, per operating point — which is exactly the cost
    /// <see cref="SolveProbes"/> removes. Nothing calls this in a shipped run; it exists so the
    /// comparison is against code rather than against a remembered answer, and so the arithmetic
    /// the fast path rearranges stays written down in the form it was derived in.
    /// </summary>
    public static Complex[][,] SolveProbesStraightforward(
        ElaboratedNetlist netlist, HbLinearExtractor extractor, ISidebands sb,
        ProbeSite[] probes, double[] ssFreqsHz, Complex[,] yDc, Counters counters,
        string commensurateWith, Func<bool>? abort = null)
    {
        int np = probes.Length, size = 2 * np;
        int nInt = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        var  wsp = new Complex[ssFreqsHz.Length][,];
        var  nan = new Complex(double.NaN, double.NaN);

        if (nInt * sb.Count > ConversionMatrixCeiling)
            throw new InvalidOperationException(TooLargeMessage(nInt * sb.Count, nInt, sb.Count));

        Complex[,] YAt(double omega)
        {
            counters.LinearPartitions++;
            return YNNAt(extractor, omega, yDc);
        }

        for (int fi = 0; fi < ssFreqsHz.Length; fi++)
        {
            if (abort?.Invoke() == true) break;

            double omegaSs = 2.0 * Math.PI * ssFreqsHz[fi];
            var w = new Complex[size, size];
            wsp[fi] = w;

            if (sb.IsDegenerate(omegaSs))
            {
                counters.DegeneratePoints++;
                netlist.AddWarningOnce("wsprobe.hb-ss-degenerate-frequency",
                    DegeneracyNote(ssFreqsHz[fi], commensurateWith));
                for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++) w[r, c] = nan;
                continue;
            }

            // ── The conversion matrix, factored once for all 2N injections ────
            int sc = sb.Count, nc = nInt * sc;
            var jss = BuildJss(sb, nInt, omegaSs, YAt);
            var lu  = new DenseLu(jss, nc);
            counters.DenseFactorizations++;
            if (lu.Singular)
            {
                netlist.AddWarningOnce("wsprobe.hb-ss-singular-conversion-matrix",
                    $"WSProbe small-signal sweep: the conversion matrix is singular at " +
                    $"{ssFreqsHz[fi] / 1e9:G6} GHz, so the wsp entries there are NaN. A singular " +
                    "conversion matrix at a real probe frequency means the linearised periodic " +
                    "system has a pole exactly there — which is what the sweep is looking for, so " +
                    "read the neighbouring points rather than this one.");
                for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++) w[r, c] = nan;
                continue;
            }

            // The linear partition at ω_ss, one stamp and one factorisation-or-cache-hit for all
            // 4N back-substitutions of this probe frequency.
            var part = extractor.LinearPartitionAt(omegaSs);
            counters.LinearPartitions++;
            var yss  = Math.Abs(omegaSs) < 1e-12 ? yDc : part.YNN;

            // The branch index of THIS assembly, read off the model after the extractor's stamp.
            var br = new int[np];
            for (int i = 0; i < np; i++)
            {
                br[i] = ((SeriesProbeModelBase)netlist.Components[probes[i].ComponentIndex].Model)
                        .LastBranchIndex;
                if (br[i] < 0 || br[i] >= part.Size)
                    throw new InvalidOperationException(
                        $"WSProbe '{probes[i].Label}' allocated no branch in the harmonic-balance " +
                        "assembly, so its small-signal injections have nowhere to go.");
            }

            var bInj = new Complex[part.Size];
            var x0   = new Complex[part.Size];
            var x1   = new Complex[part.Size];
            var bFul = new Complex[part.Size];
            var rhs  = new Complex[nc];
            var vSb  = new Complex[nc];
            var iSrc = new Complex[nInt];
            var iNl0 = new Complex[nInt];

            for (int i = 0; i < np; i++)
            for (int inj = 0; inj < 2; inj++)
            {
                Array.Clear(bInj);
                if (inj == 0)
                {
                    // Series: unit vS with − at G, + at L. The constraint row is
                    // V(nG) − V(nL) = value, so −1 gives v_L − v_G = 1.
                    bInj[br[i]] = -Complex.One;
                }
                else if (probes[i].GNode > 0)
                {
                    bInj[probes[i].GNode - 1] = Complex.One;   // unit iP INTO the G node
                }

                // 1. V_oc at the interface, then the Norton excitation.
                part.Solve(bInj, x0);
                counters.SparseSolves++;
                for (int n = 0; n < nInt; n++)
                {
                    Complex acc = Complex.Zero;
                    for (int m = 0; m < nInt; m++)
                    {
                        int nodeM = ifNodes[m];
                        Complex voc = nodeM > 0 ? x0[nodeM - 1] : Complex.Zero;
                        acc += yss[n, m] * voc;
                    }
                    iSrc[n] = -acc;
                }

                // 2. J_ss·V = −RHS, the excitation at the zero sideband only.
                Array.Clear(rhs);
                for (int n = 0; n < nInt; n++) rhs[n * sc + sb.Zero] = -iSrc[n];
                lu.Solve(rhs, vSb);
                counters.DenseSolves++;

                // 3. The device current at the zero sideband.
                for (int n = 0; n < nInt; n++)
                {
                    Complex acc = Complex.Zero;
                    for (int m = 0; m < nInt; m++) acc += yss[n, m] * vSb[m * sc + sb.Zero];
                    iNl0[n] = -(acc + iSrc[n]);
                }

                // 4. Back-solve the linear partition with the injection AND I_nl,0, exactly as
                //    SolveFullNetwork injects it: the device DRAWS iNl from the node.
                Array.Copy(bInj, bFul, bInj.Length);
                for (int n = 0; n < nInt; n++)
                {
                    int node = ifNodes[n];
                    if (node > 0 && node - 1 < bFul.Length) bFul[node - 1] -= iNl0[n];
                }
                part.Solve(bFul, x1);
                counters.SparseSolves++;

                int row = 2 * i + inj;      // 0-based row of the document's 2i−1 / 2i
                for (int jp = 0; jp < np; jp++)
                {
                    w[row, 2 * jp]     = x1[br[jp]];
                    w[row, 2 * jp + 1] = probes[jp].GNode > 0 ? x1[probes[jp].GNode - 1] : Complex.Zero;
                }
            }

            // Drop this point's sideband factorisations before moving to the next one. The
            // extractor's cache is keyed by ω and would otherwise grow by 2·K_ss + 2 entries PER
            // probe frequency — a 991-point sweep at K_ss = 7 would hold ~15,000 sparse LUs of the
            // whole network at once, which is a few megabytes on a probe fixture and gigabytes on a
            // real design. Nothing is lost: a sideband frequency ω_ss + kω0 recurs at the next probe
            // frequency only if the grid step happens to be a sub-multiple of ω0, and the HB solve's
            // own harmonics kω0 are a DISJOINT set (they coincide only at ω_ss = 0, which is
            // degenerate and never reaches here) so those entries are untouched.
            for (int s = 0; s < sc; s++)
            {
                double omegaS = omegaSs + sb.Offset(s);
                if (Math.Abs(omegaS) >= 1e-12) extractor.InvalidateLinear(Math.Abs(omegaS));
            }
            extractor.InvalidateLinear(omegaSs);
        }
        return wsp;
    }

    // ── WSP-8: the same answer with no sparse work per operating point ───────

    /// <summary>
    /// The branch row each probe's series injection and current reading address, read off the
    /// models AFTER the assembly that allocated them — the HB assembly numbers branches its own
    /// way, so the index is the stamp's, never the model's own idea of one.
    /// </summary>
    private static int[] BranchRows(ElaboratedNetlist netlist, ProbeSite[] probes, int size)
    {
        var br = new int[probes.Length];
        for (int i = 0; i < probes.Length; i++)
        {
            br[i] = ((SeriesProbeModelBase)netlist.Components[probes[i].ComponentIndex].Model)
                    .LastBranchIndex;
            if (br[i] < 0 || br[i] >= size)
                throw new InvalidOperationException(
                    $"WSProbe '{probes[i].Label}' allocated no branch in the harmonic-balance " +
                    "assembly, so its small-signal injections have nowhere to go.");
        }
        return br;
    }

    /// <summary>
    /// Fill the cache's probe blocks for a whole range before any sideband-only entry is made, so a
    /// probe frequency that is another point's sideband is factored once rather than twice. Runs
    /// only while the cache is keeping probe blocks at all — under either budget fallback
    /// (R-wsp8-8) nothing would be kept and the pass would be pure cost.
    /// </summary>
    private static bool Precompute(
        HbLinearExtractor extractor, double[] ssFreqsHz, int lo, int hi, ISidebands sb,
        HbSmallSignalCache cache, Func<int, HbSmallSignalCache.ProbeGeometry> geometry,
        Counters counters, int worker, Func<bool>? abort)
    {
        bool any = false;
        for (int fi = lo; fi < hi; fi++)
        {
            if (abort?.Invoke() == true) return any;
            double omegaSs = 2.0 * Math.PI * ssFreqsHz[fi];
            if (sb.IsDegenerate(omegaSs)) continue;

            cache.ProbeBlockAt(extractor, omegaSs, geometry, counters, worker);
            any = true;

            // The tier is decided on the first entry; below Full there is nothing to prefill.
            if (cache.ChosenTier != HbSmallSignalCache.Tier.Full) return any;
        }
        return any;
    }

    /// <summary>
    /// The <c>wsp</c> matrix at every probe frequency — the same quantity
    /// <see cref="SolveProbesStraightforward"/> computes, with every operating-point-INDEPENDENT
    /// piece taken from <paramref name="cache"/> instead of being recomputed (brief-wsprobe-8 §2–§3).
    ///
    /// <para><b>What is left per operating point is dense only.</b> The cache supplies
    /// <c>Y_NN</c> at every sideband, and at <c>ω_ss</c> also <c>W</c> (the reading of each
    /// injection with the interface open), <c>T</c> (the reading per unit interface current) and
    /// <c>I_src</c> (each injection's Norton excitation). What remains is one dense factorisation of
    /// <c>J_ss</c> and <c>2N</c> dense back-substitutions — the honest floor for this formulation,
    /// and genuinely per operating point because the device spectra are.</para>
    ///
    /// <para>The arithmetic is R-wsp5-5's, rearranged and not replaced: <c>J_ss·V = −RHS</c> with the
    /// Norton excitation at the zero sideband, the balance <c>I_nl,0 = −(Y_NN·V[·,0] + I_src)</c>
    /// read there, and the reading <c>r = W[p, ·] + T·(−I_nl,0)</c> — which is the back-solve of
    /// step 4 with the <c>2N</c> rows of <c>M⁻¹</c> already taken.</para>
    /// </summary>
    public static Complex[][,] SolveProbes(
        ElaboratedNetlist netlist, HbLinearExtractor extractor, ISidebands sb,
        ProbeSite[] probes, double[] ssFreqsHz, Complex[,] yDc, Counters counters,
        string commensurateWith, HbSmallSignalCache cache, Func<bool>? abort = null)
    {
        var wsp = new Complex[ssFreqsHz.Length][,];
        SolveProbeRange(netlist, extractor, sb, probes, ssFreqsHz, 0, ssFreqsHz.Length, wsp,
                        yDc, counters, commensurateWith, cache, worker: 0, abort);
        return wsp;
    }

    /// <summary>
    /// One contiguous chunk of the tickle grid, written into <paramref name="wsp"/> BY INDEX —
    /// R-wsp8-9's parallel form. The grid is embarrassingly parallel: each point's arithmetic is
    /// unchanged and each worker writes only its own slice, so nothing is merged, nothing is
    /// reordered, and the result is bit-identical to the serial run at every degree. Each worker
    /// brings its own netlist copy, its own extractor and its own cache slice; the operating point's
    /// two-sided spectra are read-only and shared.
    /// </summary>
    public static void SolveProbeRange(
        ElaboratedNetlist netlist, HbLinearExtractor extractor, ISidebands sb,
        ProbeSite[] probes, double[] ssFreqsHz, int lo, int hi, Complex[][,] wsp,
        Complex[,] yDc, Counters counters, string commensurateWith,
        HbSmallSignalCache cache, int worker, Func<bool>? abort = null)
    {
        int np = probes.Length, size = 2 * np;
        int nInt = extractor.InterfaceCount;
        var nan = new Complex(double.NaN, double.NaN);

        // A range is exactly one operating point on one worker, so this is where the cache's
        // validation epoch turns over — nothing else has to remember to say so.
        cache.BeginOperatingPoint(worker);

        if (nInt * sb.Count > ConversionMatrixCeiling)
            throw new InvalidOperationException(TooLargeMessage(nInt * sb.Count, nInt, sb.Count));

        Complex[,] YAt(double omega) => cache.YNNAt(extractor, omega, yDc, counters, worker);

        int sc = sb.Count, nc = nInt * sc;
        var rhs  = new Complex[nc];
        var vSb  = new Complex[nc];
        var iNl0 = new Complex[nInt];

        Func<int, HbSmallSignalCache.ProbeGeometry> geometry =
            mnaSize => HbSmallSignalCache.GeometryOf(probes, BranchRows(netlist, probes, mnaSize));

        // ── The probe frequencies FIRST, before any sideband-only entry ──────
        //
        // A probe frequency is also a SIDEBAND of other probe frequencies whenever the grid step
        // divides the fundamental — which is precisely the alignment R-wsp8-5 wants to reward. A
        // sideband-only entry holds Y_NN and nothing else, so reaching one of those frequencies as a
        // probe frequency later means factoring it a SECOND time to obtain W and T; the LU itself is
        // deliberately not kept (that is the memory the whole design refuses to spend). Visiting the
        // probe frequencies first makes every such collision a cache HIT instead, at the cost of one
        // extra stamp per point — which is the trade this pass exists to make.
        if (Precompute(extractor, ssFreqsHz, lo, hi, sb, cache, geometry, counters, worker, abort))
            { /* the cache holds every probe block of this range */ }

        for (int fi = lo; fi < hi; fi++)
        {
            if (abort?.Invoke() == true) break;

            double omegaSs = 2.0 * Math.PI * ssFreqsHz[fi];
            var w = new Complex[size, size];
            wsp[fi] = w;

            if (sb.IsDegenerate(omegaSs))
            {
                counters.DegeneratePoints++;
                netlist.AddWarningOnce("wsprobe.hb-ss-degenerate-frequency",
                    DegeneracyNote(ssFreqsHz[fi], commensurateWith));
                for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++) w[r, c] = nan;
                continue;
            }

            // Everything operating-point-independent, for this probe frequency. The geometry is a
            // factory because the branch rows only exist once the cache has stamped the assembly.
            var block = cache.ProbeBlockAt(extractor, omegaSs, geometry, counters, worker);

            var yss = Math.Abs(omegaSs) < 1e-12 ? yDc : block.YNN;

            // ── The conversion matrix, factored once for all 2N injections ────
            var jss = BuildJss(sb, nInt, omegaSs, YAt);
            var lu  = new DenseLu(jss, nc);
            counters.DenseFactorizations++;
            if (lu.Singular)
            {
                netlist.AddWarningOnce("wsprobe.hb-ss-singular-conversion-matrix",
                    $"WSProbe small-signal sweep: the conversion matrix is singular at " +
                    $"{ssFreqsHz[fi] / 1e9:G6} GHz, so the wsp entries there are NaN. A singular " +
                    "conversion matrix at a real probe frequency means the linearised periodic " +
                    "system has a pole exactly there — which is what the sweep is looking for, so " +
                    "read the neighbouring points rather than this one.");
                for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++) w[r, c] = nan;
                continue;
            }

            for (int p = 0; p < size; p++)
            {
                // 1–2. J_ss·V = −RHS, the Norton excitation at the zero sideband only.
                Array.Clear(rhs);
                for (int n = 0; n < nInt; n++) rhs[n * sc + sb.Zero] = -block.ISrc[n, p];
                lu.Solve(rhs, vSb);
                counters.DenseSolves++;

                // 3. The device current at the zero sideband.
                for (int n = 0; n < nInt; n++)
                {
                    Complex acc = Complex.Zero;
                    for (int m = 0; m < nInt; m++) acc += yss[n, m] * vSb[m * sc + sb.Zero];
                    iNl0[n] = -(acc + block.ISrc[n, p]);
                }

                // 4. The readings, with the 2N rows of M⁻¹ already taken:
                //    r = W[p, ·] + T·(−I_nl,0), which is step 4's back-solve contracted.
                for (int c = 0; c < size; c++)
                {
                    Complex acc = block.W[p, c];
                    for (int n = 0; n < nInt; n++) acc += block.T[c, n] * -iNl0[n];
                    w[p, c] = acc;
                }
            }
        }
    }

    // ── R-wsp5-9(b): the fold onto the HB Jacobian ───────────────────────────

    /// <summary>
    /// The inverse of R-wsp5-2's mapping: <c>J_ss</c> built at <c>ω_ss = 0</c> with
    /// <c>K_ss = K</c>, folded onto the one-sided real-split form
    /// <see cref="HbNewton.BuildJ"/> assembles. This is the gate that pins the two-sided
    /// coefficient convention, the <c>jω_k</c> factor and the <c>Y_NN</c> placement in one
    /// comparison — and it is the reason nothing here is a second copy of the Jacobian.
    ///
    /// <para>At <c>ω_ss = 0</c> the perturbation is a real periodic waveform, so the two-sided
    /// unknown at <c>−k</c> is the conjugate of the one at <c>+k</c> and the one-sided
    /// full-amplitude unknown <c>ΔV_k = a + jb</c> maps to
    /// <c>δV[k] = ΔV_k/2</c>, <c>δV[−k] = conj(ΔV_k)/2</c> for <c>k &gt; 0</c> and
    /// <c>δV[0] = ΔV_0</c>; the residual maps the other way, <c>F_k = 2·R_k</c> for
    /// <c>k &gt; 0</c> and <c>F_0 = R_0</c>. Writing
    /// <c>A = J_ss[(n,k),(m,i)]</c> and <c>B = J_ss[(n,k),(m,−i)]</c>, the real 2×2 block is the
    /// split of <c>z ↦ A·z + B·z̄</c> scaled by those two mappings — and the Maas §7.3 DC rules
    /// (the fictitious imaginary DC degree of freedom) fall out of it rather than being applied on
    /// top.</para>
    /// </summary>
    public static double[] FoldToRealSplit(Complex[] jss, int nInt, int k)
    {
        int sc  = 2 * k + 1, zero = k;
        int dof = 2 * nInt * (k + 1);
        var j   = new double[dof * dof];

        Complex At(int n, int kr, int m, int ic)
            => jss[(n * sc + zero + kr) * (nInt * sc) + m * sc + zero + ic];

        for (int n = 0; n < nInt; n++)
        {
            for (int kr = 0; kr <= k; kr++)
            for (int m = 0; m < nInt; m++)
            for (int ic = 0; ic <= k; ic++)
            {
                // The residual scale (row) and the unknown scale (column).
                double rowScale = kr == 0 ? 1.0 : 2.0;
                double colScale = ic == 0 ? 1.0 : 0.5;

                Complex a = At(n, kr, m, ic) * rowScale * colScale;
                Complex b = ic == 0 ? Complex.Zero : At(n, kr, m, -ic) * rowScale * colScale;

                double a00 =  a.Real      + b.Real;
                double a01 = -a.Imaginary + b.Imaginary;
                double a10 =  a.Imaginary + b.Imaginary;
                double a11 =  a.Real      - b.Real;

                // Maas §7.3: the imaginary DC degree of freedom is fictitious in both directions,
                // and its diagonal mirrors the real one so the block stays invertible.
                if (ic == 0) { a01 = 0; a11 = kr == 0 ? a00 : 0; }
                if (kr == 0) { a10 = 0; a11 = ic == 0 ? a00 : 0; }

                int r0 = 2 * (n * (k + 1) + kr),     r1 = r0 + 1;
                int c0 = 2 * (m * (k + 1) + ic),     c1 = c0 + 1;
                j[r0 * dof + c0] += a00;
                j[r0 * dof + c1] += a01;
                j[r1 * dof + c0] += a10;
                j[r1 * dof + c1] += a11;
            }
        }
        return j;
    }

    // ── The lattice difference spectra (R-wsp5-7) ────────────────────────────

    /// <summary>
    /// The largest conversion matrix this solve will build. <c>J_ss</c> is dense and complex, so it
    /// costs <c>16·n_c²</c> bytes — 64 MB at the cap — and it is factored once per probe frequency.
    /// A refusal naming the knobs is the honest answer above it; the alternative is a run that
    /// allocates gigabytes and is killed with no diagnostic.
    /// </summary>
    public const int ConversionMatrixCeiling = 2000;

    /// <summary>The refusal above <see cref="ConversionMatrixCeiling"/>, naming what to turn
    /// down.</summary>
    public static string TooLargeMessage(int nc, int nInt, int sidebands) =>
        $"wsprobe.hb-ss-conversion-matrix-too-large: the small-signal conversion matrix would be " +
        $"{nc} × {nc} ({nInt} interface nodes × {sidebands} sidebands), above the {ConversionMatrixCeiling} " +
        "this analysis builds densely. Lower SSMaxHarm (fewer sidebands), or MaxHarm / MaxMixOrder " +
        "(a smaller retained spectrum), or place the probe in a design with fewer nonlinear-facing nodes.";

    /// <summary>
    /// The device derivative spectra over a lattice of TWICE the run's mixing order, which is what
    /// the conversion matrix needs: a sideband difference <c>k_s − k_t</c> reaches order
    /// <c>2·MaxMixOrder</c> exactly as <c>k − i</c> reaches <c>2K</c> single-tone.
    ///
    /// <para><b>Why this re-evaluates the devices.</b> The T-tone Newton path never forms a
    /// derivative SPECTRUM at all — <see cref="HbApft.AccumulateTripleProducts"/> consumes the
    /// derivative waveforms as raw time samples, which is exactly what makes it tone-count-general.
    /// Those samples live on the order-<c>O</c> torus and cannot resolve an order-<c>2O</c>
    /// coefficient, so the operating point is re-synthesised onto the wider torus and the devices are
    /// evaluated there once. It is one extra device pass per operating point, not per probe
    /// frequency.</para>
    ///
    /// <para>The converged spectrum embeds EXACTLY: <see cref="MixingLattice"/> enumerates by
    /// ascending total order, so every representative of the narrow lattice is a representative of
    /// the wide one, and <see cref="HbApft.Synthesize"/> then reproduces the same trigonometric
    /// polynomial on the wider sample set. The embedding is looked up rather than assumed to be the
    /// identity, because the index order is a locked contract about the narrow lattice and not a
    /// promise about the pair.</para>
    /// </summary>
    public static (MixingLattice Diff, Complex[,,] G, Complex[,,] C,
                   List<(ComponentModel Model, int W, Complex[,,] Dw)> Buckets) LatticeSpectra(
        Complex[,] v, HbApft apft, double oversample, int nInt,
        ElaboratedNetlist netlist, int[] interfaceNodes)
    {
        var lattice = apft.Lattice;
        int T = lattice.ToneCount, o = lattice.MaxMixOrder, m0 = lattice.MixCount;

        HbApft apft2;
        try { apft2 = HbApft.Get(T, 2 * o, oversample); }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidOperationException(
                $"wsprobe.hb-ss-mixorder-too-high: the small-signal conversion matrix needs the " +
                $"device spectra at mixing order {2 * o} — twice MaxMixOrder = {o} — which is beyond " +
                "the lattice's representable limit. Lower MaxMixOrder.", ex);
        }

        var l2 = apft2.Lattice;
        int m2 = l2.MixCount;

        var vWide = new Complex[nInt, m2];
        for (int m = 0; m < m0; m++)
        {
            int j = l2.IndexOf(lattice.ToneOf(m));
            if (j < 0)
                throw new InvalidOperationException(
                    $"the mixing product {MixingLattice.Label(lattice.ToneOf(m))} of the run's own " +
                    $"order-{o} lattice is not retained by the order-{2 * o} difference lattice, " +
                    "which cannot happen and means the two enumerations have diverged.");
            for (int n = 0; n < nInt; n++) vWide[n, j] = v[n, m];
        }

        var (_, _, dg, dc, bk) = HbNewtonNd.EvaluateNonlinearNd(vWide, apft2, nInt, netlist, interfaceNodes);

        var buf = new Complex[m2];
        Complex[,,] Spectrum(double[][] samples)
        {
            var a = new Complex[nInt, nInt, m2];
            for (int n = 0; n < nInt; n++)
            for (int m = 0; m < nInt; m++)
            {
                apft2.Analyze(samples[n * nInt + m], buf);
                for (int k = 0; k < m2; k++) a[n, m, k] = buf[k];
            }
            return a;
        }
        return (l2, Spectrum(dg), Spectrum(dc), [.. bk.Select(b => (b.Model, b.W, Spectrum(b.Dw)))]);
    }

    // ── Dense complex LU ─────────────────────────────────────────────────────

    /// <summary>
    /// Dense complex LU with partial pivoting, factored once and back-solved many times — the
    /// <c>2N</c> right-hand sides of one probe frequency share one factorisation, which is what
    /// R-wsp5-9(i) counts. Small and local on purpose: <c>J_ss</c> is
    /// <c>N_int·(2K_ss + 1)</c> square, which is tens for a single-FET amplifier, and the sparse
    /// path (<c>CSparse</c>) is the wrong tool for a matrix whose conversion blocks are full.
    /// </summary>
    public sealed class DenseLu
    {
        private readonly Complex[] _a;
        private readonly int[]     _piv;
        private readonly int       _n;

        /// <summary>True when a pivot vanished — the caller reports NaN rather than a fudged
        /// denominator (overview D-7).</summary>
        public bool Singular { get; }

        public DenseLu(Complex[] a, int n)
        {
            _a = a; _n = n;
            _piv = new int[n];
            for (int i = 0; i < n; i++) _piv[i] = i;

            bool singular = false;
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                double best = Complex.Abs(_a[col * n + col]);
                for (int r = col + 1; r < n; r++)
                {
                    double mag = Complex.Abs(_a[r * n + col]);
                    if (mag > best) { best = mag; pivot = r; }
                }
                if (best == 0.0 || !double.IsFinite(best)) { singular = true; break; }
                if (pivot != col)
                {
                    (_piv[col], _piv[pivot]) = (_piv[pivot], _piv[col]);
                    for (int j = 0; j < n; j++)
                        (_a[col * n + j], _a[pivot * n + j]) = (_a[pivot * n + j], _a[col * n + j]);
                }
                Complex d = _a[col * n + col];
                for (int r = col + 1; r < n; r++)
                {
                    Complex f = _a[r * n + col] / d;
                    _a[r * n + col] = f;
                    if (f == Complex.Zero) continue;
                    for (int j = col + 1; j < n; j++) _a[r * n + j] -= f * _a[col * n + j];
                }
            }
            Singular = singular;
        }

        /// <summary>One back-substitution. <paramref name="x"/> is overwritten.</summary>
        public void Solve(Complex[] b, Complex[] x)
        {
            int n = _n;
            for (int i = 0; i < n; i++) x[i] = b[_piv[i]];
            for (int i = 1; i < n; i++)
            {
                Complex acc = x[i];
                for (int j = 0; j < i; j++) acc -= _a[i * n + j] * x[j];
                x[i] = acc;
            }
            for (int i = n - 1; i >= 0; i--)
            {
                Complex acc = x[i];
                for (int j = i + 1; j < n; j++) acc -= _a[i * n + j] * x[j];
                x[i] = acc / _a[i * n + i];
            }
        }
    }
}
