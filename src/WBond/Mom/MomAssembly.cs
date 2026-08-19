using System.Diagnostics;
using System.Threading;

namespace CircuitRF.WBond.Mom;

/// <summary>
/// The frequency-independent reduction of kernel W1: <b>G</b>, <b>K̃</b>, <b>W</b> and <b>H</b>.
///
/// <h3>The formulation</h3>
/// <para>With <c>V = R u</c>, <c>Q = P⁻¹ R u</c>, <c>Ã = A R</c> and <c>E</c> the terminal slice, the
/// two PEEC relations reduce to a single system in the segment currents:</para>
/// <code>
/// G  = Rᵀ P⁻¹ R          (N_r × N_r, SPD)
/// K~ = Ã G⁻¹ Ãᵀ          (N_s × N_s, real symmetric, positive SEMI-definite)
/// W  = Ã G⁻¹ E           (N_s × T)
/// H  = Eᵀ G⁻¹ E          (T × T, SPD)
///
/// [ (jω)² L + jω D(ω) + K~ ] I  =  W i_p            ← WM-2 solves this
/// Z_port(ω) = ( H − Wᵀ M~(ω)⁻¹ W ) / (jω)
/// </code>
///
/// <h3>Why this arrangement and not the obvious one</h3>
/// <para>The obvious PEEC arrangement forms <c>Y_node(ω) = Aᵀ Z(ω)⁻¹ A + jω P⁻¹</c> and
/// Schur-complements onto the ports. That needs <b>two</b> dense factorisations per frequency and
/// needs <c>P⁻¹</c> formed explicitly. This one needs <b>one</b> factorisation per frequency, of one
/// complex <i>symmetric</i> N_s × N_s matrix, with everything else precomputed here.</para>
///
/// <h3>The eigendecomposition shortcut does NOT transfer</h3>
/// <para><see cref="ImpedanceReduction"/>'s own remarks name a shortcut: when every wire shares a
/// radius and a metal, <c>D(ω)</c> is a scalar multiple of a diagonal, so <c>Z(ω)</c> can be
/// diagonalised once and inverted per frequency for free. <b>That does not survive here.</b>
/// <c>M̃(ω) = (jω)²L + jω D(ω) + K̃</c> is a <i>quadratic</i> pencil in <c>jω</c> whose three matrices
/// <c>L</c>, <c>Λ</c> and <c>K̃</c> are not simultaneously diagonalisable, so no single similarity
/// transform frees the sweep. Recorded here so the next reader of that comment does not chase it.</para>
///
/// <h3>Nothing here has an omega</h3>
/// <para>Every matrix on this type is filled once per design and reused across a whole sweep. That is
/// the entire speed argument for kernel W1, and a member here that took a frequency would destroy
/// it.</para>
/// </summary>
public sealed class MomAssembly
{
    private MomAssembly(int segmentCount, int reducedCount, int terminalCount,
                        double[] g, double[] kTilde, double[] w, double[] h)
    {
        SegmentCount = segmentCount;
        ReducedCount = reducedCount;
        TerminalCount = terminalCount;
        G = g;
        KTilde = kTilde;
        W = w;
        H = h;
    }

    public int SegmentCount { get; }

    public int ReducedCount { get; }

    public int TerminalCount { get; }

    /// <summary>
    /// <c>G = Rᵀ P⁻¹ R</c>, N_r × N_r row-major, SPD. <b>In farads</b> — <c>P</c> is the coefficient of
    /// potential (inverse farads) and <c>P⁻¹</c> is the Maxwell capacitance, so <c>G⁻¹</c> and therefore
    /// <c>H</c> and <c>K̃</c> are in inverse farads, which is what makes <c>H/(jω)</c> an impedance.
    /// (This comment said "inverse farads" until WM-2 traced the units through <c>Z_port</c>.)
    /// </summary>
    public double[] G { get; }

    /// <summary>
    /// <c>K̃ = Ã G⁻¹ Ãᵀ</c>, N_s × N_s row-major, symmetric and positive <b>semi</b>-definite.
    ///
    /// <para><b>Its nullity is the loop count, W − M.</b> <c>null(K̃) = null(Ãᵀ)</c>, which is
    /// non-trivial exactly when terminal shorting creates a loop — two wires in one array, shorted at
    /// both ends, are a loop. <c>W</c>'s columns lie in <c>range(Ã) ⊥ null(Ãᵀ)</c>, so the DC blow-up
    /// is projected out of <c>Z_port</c>, but the conditioning of <c>M̃</c> still degrades like 1/ω.
    /// That is a named risk for WM-2's lowest usable frequency, not a defect here.</para>
    /// </summary>
    public double[] KTilde { get; }

    /// <summary><c>W = Ã G⁻¹ E</c>, N_s × T row-major.</summary>
    public double[] W { get; }

    /// <summary><c>H = Eᵀ G⁻¹ E</c>, T × T row-major, SPD — the leading block of <c>G⁻¹</c>.</summary>
    public double[] H { get; }

    /// <summary>
    /// Fills <b>P</b>, reduces it, and <b>drops P before K̃ is allocated</b>. At the 200-wire size that
    /// is 192 MB nothing downstream reads.
    /// </summary>
    public static MomAssembly Build(WireMomMesh mesh, MomStageTimes? times = null,
                                    CancellationToken cancel = default)
        => Build(mesh, times, cancel, null);

    /// <summary>The same, reporting its five stage boundaries through <paramref name="run"/>.</summary>
    public static MomAssembly Build(WireMomMesh mesh, MomStageTimes? times, CancellationToken cancel,
                                    WBondRunControl? run)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        double[]? p = null;
        MomStageTimes.Time(times, static (t, ms) => t.PotentialFillMs += ms,
                           () => p = NodePotential.Fill(mesh, null, null, run));
        return Build(mesh, p!, times, cancel, run);
    }

    /// <summary>
    /// The same, over an already-filled <b>P</b> — the route the identity gates take, because they
    /// want the same P they assert against.
    /// </summary>
    /// <param name="p">
    /// N_n × N_n row-major. <b>Not retained.</b> Step 1 is the only consumer, so a caller that has no
    /// other use for it may let it go as soon as this returns.
    /// </param>
    public static MomAssembly Build(WireMomMesh mesh, double[] p, MomStageTimes? times = null,
                                    CancellationToken cancel = default)
        => Build(mesh, p, times, cancel, null);

    /// <summary>The same, reporting its stage boundaries through <paramref name="run"/>.</summary>
    public static MomAssembly Build(WireMomMesh mesh, double[] p, MomStageTimes? times,
                                    CancellationToken cancel, WBondRunControl? run)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(p);

        int nn = mesh.NodeCount;
        int nr = mesh.ReducedCount;
        int ns = mesh.SegmentCount;
        int t = mesh.TerminalCount;

        var sw = times is null ? null : Stopwatch.StartNew();
        bool parallel = mesh.Settings.Parallel;

        // ---- 1. G = Rᵀ P⁻¹ R, as a scatter-add over merged nodes.
        //
        // R's columns are 0/1 membership indicators, so this product is not a product: every entry of
        // P⁻¹ lands on exactly one entry of G, addressed by the two nodes' reduced indices. The cost is
        // the INVERSE (N_n³/3) plus one O(N_n²) pass — against N_r triangular solves (N_n³) before.
        cancel.ThrowIfCancellationRequested();
        var pInverse = CholeskyFactor.Factor(p, nn, run, "factorising the potential matrix")
                                     .InvertInPlace(parallel, run, "inverting the potential matrix");

        run?.BeginStage("reducing to the node-merged basis");

        var g = new double[nr * nr];
        var reduced = mesh.ReducedOfNode;
        for (int m = 0; m < nn; m++)
        {
            int gRow = reduced[m] * nr, pRow = m * nn;
            for (int n = 0; n < nn; n++) g[gRow + reduced[n]] += pInverse[pRow + n];
        }

        Symmetrise(g, nr);
        pInverse = null!;   // 8·N_n² bytes nothing downstream reads, released before G's own inverse.

        if (times is not null) { times.ReduceToGMs += sw!.Elapsed.TotalMilliseconds; sw.Restart(); }

        // ---- 2. G⁻¹. G ITSELF IS KEPT — it is a public member and §9.6's definiteness gate factors it —
        // so this is the one place in the assembly where two N_r × N_r matrices are alive together.
        cancel.ThrowIfCancellationRequested();
        var gInverse = CholeskyFactor.Factor(g, nr, run, "factorising the reduced matrix")
                                     .InvertInPlace(parallel, run, "inverting the reduced matrix");

        // ---- 3. K̃, W and H, each a fixed number of gathers per entry.
        //
        // Ã = A R has at most two non-zeros per row, at segment p's start and end REDUCED node, so
        // Ã G⁻¹ Ãᵀ is the four-term expression below rather than two dense products. A segment whose
        // two ends merged into one terminal (rs == re) falls out as an exact zero row, which is the
        // right answer and needs no special case.
        var kTilde = new double[(long)ns * ns <= int.MaxValue ? ns * ns : throw new InvalidOperationException(
            $"K~ is {ns} x {ns} and does not fit in one array.")];

        var startRow = new int[ns];
        var endRow = new int[ns];
        for (int k = 0; k < ns; k++)
        {
            startRow[k] = mesh.ReducedStart(k);
            endRow[k] = mesh.ReducedEnd(k);
        }

        void KRow(int pRow)
        {
            int a = startRow[pRow] * nr, b = endRow[pRow] * nr, outRow = pRow * ns;
            for (int q = 0; q < ns; q++)
            {
                int sq = startRow[q], eq = endRow[q];
                kTilde[outRow + q] = gInverse[a + sq] - gInverse[a + eq] - gInverse[b + sq] + gInverse[b + eq];
            }

            run?.TickStage();
        }

        run?.BeginStage("assembling the segment system", ns);

        if (parallel) System.Threading.Tasks.Parallel.For(0, ns, KRow);
        else for (int pRow = 0; pRow < ns; pRow++) KRow(pRow);

        cancel.ThrowIfCancellationRequested();
        Symmetrise(kTilde, ns);

        // ---- 4. W = Ã G⁻¹ E — E is the terminal slice, so its columns are the FIRST T columns of G⁻¹.
        var w = new double[ns * t];
        for (int k = 0; k < ns; k++)
        {
            int a = startRow[k] * nr, b = endRow[k] * nr, outRow = k * t;
            for (int port = 0; port < t; port++) w[outRow + port] = gInverse[a + port] - gInverse[b + port];
        }

        // ---- 5. H = Eᵀ G⁻¹ E — the leading T × T block, copied.
        var h = new double[t * t];
        for (int i = 0; i < t; i++)
            for (int j = 0; j < t; j++)
                h[i * t + j] = gInverse[i * nr + j];

        Symmetrise(h, t);

        if (times is not null) times.AssembleKwhMs += sw!.Elapsed.TotalMilliseconds;

        return new MomAssembly(ns, nr, t, g, kTilde, w, h);
    }

    /// <summary>
    /// Averages a matrix with its own transpose in place.
    ///
    /// <para>Every one of these matrices is symmetric <i>by construction</i>; none is symmetric
    /// <i>bit-for-bit</i>, because each is assembled from triangular solves that visit its two halves
    /// in different orders. <c>Z_port</c>'s reciprocity is structural rather than a rounding accident
    /// (§2.6 item 1) only if that is made true here rather than assumed downstream.</para>
    /// </summary>
    private static void Symmetrise(double[] m, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double v = 0.5 * (m[i * n + j] + m[j * n + i]);
                m[i * n + j] = v;
                m[j * n + i] = v;
            }
    }
}
