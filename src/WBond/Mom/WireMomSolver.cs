using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.WBond.Mom;

/// <summary>
/// The per-frequency solve of kernel W: one dense complex factorisation per frequency point, and
/// nothing else (<c>docs/design/mom-wirebond-kernel.md</c> §4.1, §8; brief-wbond-mom-w2 §2).
///
/// <code>
/// M~(w)     = (jw)^2 L  +  jw D(w)  +  K~            N_s x N_s, complex SYMMETRIC
/// X         = M~(w)^-1 W                             N_s x T,  T = 2M
/// Z_port(w) = ( H - W^T X ) / (jw)                   T x T
/// Y_port(w) = Z_port^-1
/// </code>
///
/// <h3>Nothing but D(w) is refilled inside the frequency loop</h3>
/// <para><b>L</b>, <b>P</b>, <b>G</b>, <b>K~</b>, <b>W</b> and <b>H</b> are the design's, not the
/// frequency point's, and they are filled exactly once in <see cref="Create"/>. That is the entire
/// speed argument for this formulation: <c>src/WBond/Mom/RESOLVED.md</c> records a 1.65 s assembly at
/// N_s = 1,040 against a per-point solve two orders of magnitude below it, so refilling anything here
/// would turn a seconds-long sweep into a twenty-minute one.</para>
///
/// <h3>Why a general LU and not a complex-symmetric LDL^T</h3>
/// <para><c>M~</c> is symmetric but not Hermitian, so <see cref="CholeskyFactor"/> does not apply, and
/// an unpivoted complex LDL^T can break down on a perfectly well-conditioned matrix (see
/// <see cref="ComplexLu"/>'s own remarks). The symmetry is worth a 2x flop saving and it is deliberately
/// not taken here: WM-3 is where that is measured, and a measurement is only meaningful against a
/// reference that is already known to be right.</para>
///
/// <h3>The low-frequency floor is real and is enforced</h3>
/// <para><c>M~(w) -> K~</c> as w -> 0, and <c>K~</c> is singular whenever terminal shorting created a
/// loop — which is every array with two or more wires. The blow-up is projected out of
/// <c>Z_port</c> analytically (W's columns lie in range(A~)), but <c>M~</c>'s condition number still
/// grows like 1/w, so below some frequency the answer is rounding noise.
/// <see cref="WireMomSettings.MinimumFrequencyHz"/> refuses there, and its shipped value is measured
/// rather than guessed — see <c>RESOLVED.md</c>.</para>
/// </summary>
public sealed class WireMomSolver
{
    private readonly double[] _l;          // N_s x N_s, real, henries
    private readonly MomAssembly _assembly;
    private readonly SegmentInternalZ _internal;
    private Workspace? _point;             // lazily built, serves the point-at-a-time API only

    private WireMomSolver(WireMomMesh mesh, double[] l, MomAssembly assembly, SegmentInternalZ internalZ)
    {
        Mesh = mesh;
        _l = l;
        _assembly = assembly;
        _internal = internalZ;
    }

    /// <summary>
    /// One frequency point's scratch: <c>M̃</c>, the <c>D(ω)</c> diagonal and the T-column right-hand
    /// side block.
    ///
    /// <para><b>It is per-thread, and that is the whole reason it is a type.</b> These buffers used to
    /// be fields on the solver, which made a frequency-parallel sweep silently produce whichever
    /// point's matrix won the race. <c>M̃</c> is <c>16·N_s²</c> bytes — 369 MB at N_s = 4,800 — so the
    /// number of them that may exist at once is a memory decision, taken by
    /// <see cref="WireMomCost.SolveThreadCount"/> and reported in the result's notes.</para>
    /// </summary>
    private sealed class Workspace(int segments, int terminals)
    {
        public readonly Complex[] MTilde = new Complex[(long)segments * segments];
        public readonly Complex[] Diagonal = new Complex[segments];
        public readonly Complex[] Rhs = new Complex[(long)segments * Math.Max(1, terminals)];
    }

    public WireMomMesh Mesh { get; }

    public WBondDesign Design => Mesh.Design;

    public WireMomSettings Settings => Mesh.Settings;

    /// <summary>The mesh report — N_s, N_n, N_r, T, the memory arithmetic and the warnings.</summary>
    public WireMomMeshReport Report => Mesh.Report;

    /// <summary>T = 2M.</summary>
    public int TerminalCount => Mesh.TerminalCount;

    /// <summary>M.</summary>
    public int ArrayCount => Mesh.ArrayCount;

    public int SegmentCount => Mesh.SegmentCount;

    /// <summary><c>G1.i, G1.o, G2.i, …</c> — the exported file's own port order.</summary>
    public string[] TerminalNames => Mesh.TerminalNames;

    /// <summary>
    /// Meshes, fills and reduces a design. <b>Everything frequency-independent happens here</b>, so
    /// this is the expensive call and every <see cref="PortImpedance(double,bool)"/> after it is one
    /// factorisation.
    /// </summary>
    public static WireMomSolver Create(WBondDesign design, WireMomSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        return Create(WireMomMesh.Build(design, settings));
    }

    /// <summary>The same, accumulating the stage times of the setup into <paramref name="times"/>.</summary>
    public static WireMomSolver Create(WBondDesign design, WireMomSettings? settings, MomStageTimes times)
    {
        ArgumentNullException.ThrowIfNull(design);
        return Create(WireMomMesh.Build(design, settings), times);
    }

    /// <summary>
    /// The same, cancellable. <b>Setup is the long half at large N</b> — 34.5 s at N_s = 4,800 — so a
    /// caller that can cancel a sweep but not its setup can still leave a user waiting half a minute
    /// after they pressed Cancel.
    /// </summary>
    public static WireMomSolver Create(WBondDesign design, WireMomSettings? settings, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(design);
        return Create(WireMomMesh.Build(design, settings), null, cancel);
    }

    /// <summary>
    /// The same, reporting the setup's own stage boundaries — meshing, the two fills, the two Choleskys
    /// and the K̃/W/H assembly — through <paramref name="run"/>, and cancelling on its token.
    ///
    /// <para><b>The setup is the half a progress bar most needs</b>: it is 34.5 s at N_s = 4,800 against
    /// 14 s a point, and it happens before the frequency counter can honestly move at all. Without a
    /// stage row a large design shows a bar sitting at 0 of N points for half a minute, which is
    /// indistinguishable from a hang.</para>
    /// </summary>
    public static WireMomSolver Create(WBondDesign design, WireMomSettings? settings, WBondRunControl? run)
    {
        ArgumentNullException.ThrowIfNull(design);

        run?.BeginStage("meshing the wires");
        var mesh = WireMomMesh.Build(design, settings);
        run?.ThrowIfCancellationRequested();

        return Create(mesh, null, run?.Token ?? default, run);
    }

    /// <summary>The same over an already-built mesh — the route a caller that showed the report first takes.</summary>
    public static WireMomSolver Create(WireMomMesh mesh) => Create(mesh, null);

    /// <summary>The same, accumulating the stage times of the setup into <paramref name="times"/>.</summary>
    public static WireMomSolver Create(WireMomMesh mesh, MomStageTimes? times, CancellationToken cancel = default)
        => Create(mesh, times, cancel, null);

    /// <summary>The same, reporting the setup's stage boundaries through <paramref name="run"/>.</summary>
    public static WireMomSolver Create(WireMomMesh mesh, MomStageTimes? times, CancellationToken cancel,
                                       WBondRunControl? run)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        double[]? l = null;
        MomStageTimes.Time(times, static (t, ms) => t.InductanceFillMs += ms,
                           () => l = SegmentInductance.Fill(mesh, null, run));

        cancel.ThrowIfCancellationRequested();

        var assembly = MomAssembly.Build(mesh, times, cancel, run);
        return new WireMomSolver(mesh, l!, assembly, SegmentInternalZ.Create(mesh));
    }

    /// <summary>
    /// Whether this solver is still the right one for <paramref name="design"/> at
    /// <paramref name="settings"/> — the same design object, and settings that compare equal.
    ///
    /// <h3>Reference equality on the design, deliberately</h3>
    /// <para><b>This is not a staleness check and it must not be used as one.</b>
    /// <see cref="WBondDesign"/> is mutable: moving a wire leaves this returning true while every
    /// matrix here describes the old geometry. <see cref="WireMesh"/>'s own doc comment records the
    /// lesson — a snapshot that silently goes stale is worse than a rebuild — which is why there is no
    /// mutation-aware cache here and no static holding one. What this answers is the narrow question a
    /// caller re-exporting the SAME unedited design on a DIFFERENT frequency grid has, and that caller
    /// is the one who knows nothing has been edited.</para>
    /// </summary>
    public bool Matches(WBondDesign design, WireMomSettings? settings = null) =>
        ReferenceEquals(Design, design) && Settings == (settings ?? WireMomSettings.Default);

    // ------------------------------------------------------------------ the solve

    /// <summary>
    /// <c>Z_port(f)</c>, T × T row-major, in ohms.
    /// </summary>
    /// <param name="symmetrise">
    /// Force <c>Z[i,j] = Z[j,i]</c>, as <see cref="ImpedanceReduction.ArrayImpedance"/> does for its own
    /// output — reciprocity should be structural in what is handed out rather than true only to
    /// rounding. <b>Pass false only from the gate that asserts the raw matrix is already symmetric</b>:
    /// a gate that runs after the symmetrisation would be testing the symmetriser, not the solve.
    /// </param>
    public Complex[] PortImpedance(double frequencyHz, bool symmetrise = true) =>
        PortImpedance(frequencyHz, symmetrise, null);

    /// <summary>
    /// The same, accumulating this point's stage times into <paramref name="times"/>.
    ///
    /// <para><b>This overload is not re-entrant</b> — it reuses one lazily built workspace so that
    /// point-at-a-time callers do not re-allocate <c>16·N_s²</c> bytes per call. The sweep in
    /// <see cref="Solve"/> gives every thread its own.</para>
    /// </summary>
    public Complex[] PortImpedance(double frequencyHz, bool symmetrise, MomStageTimes? times)
    {
        _point ??= new Workspace(SegmentCount, TerminalCount);
        return SolvePoint(frequencyHz, _point, symmetrise, times, null);
    }

    /// <summary>
    /// One frequency point, over a caller-owned workspace. Everything the sweep parallelises is here
    /// and nothing outside it is written to.
    /// </summary>
    private Complex[] SolvePoint(double frequencyHz, Workspace ws, bool symmetrise,
                                 MomStageTimes? times, FallbackLog? fallbacks)
    {
        RefuseIfBelowFloor(frequencyHz);

        int ns = SegmentCount;
        int t = TerminalCount;
        double omega = 2.0 * Math.PI * frequencyHz;
        var jOmega = new Complex(0.0, omega);

        var sw = times is null ? null : Stopwatch.StartNew();

        AssembleMTilde(frequencyHz, ws);

        if (times is not null) { times.MTildeAssembleMs += sw!.Elapsed.TotalMilliseconds; sw.Restart(); }

        // THE FACTORISATION IS IN PLACE, so a fallback has to REBUILD M~ rather than re-read it. That
        // is 1 ms against the 200 ms the factorisation costs at N_s = 960, and it is what lets one
        // N_s x N_s complex buffer serve a whole thread.
        ComplexLdlt? ldlt = null;
        ComplexLu? lu = null;

        if (Settings.SymmetricFactorisation)
        {
            try
            {
                var candidate = ComplexLdlt.FactorInPlace(ws.MTilde, ns);
                if (candidate.PivotRatio >= Settings.MinimumPivotRatio) ldlt = candidate;
            }
            catch (InvalidOperationException)
            {
                // A zero pivot. Not a singular matrix — an unpivoted LDLt has no diagonal-dominance
                // guarantee — so this is a fallback, not a refusal.
            }
        }

        if (ldlt is null)
        {
            if (Settings.SymmetricFactorisation) fallbacks?.Note(frequencyHz);
            AssembleMTilde(frequencyHz, ws);
            lu = ComplexLu.FactorInPlace(ws.MTilde, ns);
        }

        if (times is not null) { times.FactorMs += sw!.Elapsed.TotalMilliseconds; sw.Restart(); }

        // X = M~^-1 W, all T right-hand sides through ONE triangular sweep.
        var w = _assembly.W;
        var x = ws.Rhs;
        for (int k = 0; k < ns; k++)
        {
            int row = k * t;
            for (int port = 0; port < t; port++) x[row + port] = new Complex(w[row + port], 0.0);
        }

        if (ldlt is not null)
        {
            ldlt.SolveInPlace(x, t);
        }
        else
        {
            var rhs = new Complex[ns];
            for (int port = 0; port < t; port++)
            {
                for (int k = 0; k < ns; k++) rhs[k] = new Complex(w[k * t + port], 0.0);
                var column = lu!.Solve(rhs);
                for (int k = 0; k < ns; k++) x[k * t + port] = column[k];
            }
        }

        // Z_port = ( H - W^T X ) / (jw).
        var h = _assembly.H;
        var z = new Complex[t * t];
        for (int i = 0; i < t; i++)
        {
            for (int j = 0; j < t; j++)
            {
                Complex acc = new(h[i * t + j], 0.0);
                for (int k = 0; k < ns; k++) acc -= w[k * t + i] * x[k * t + j];
                z[i * t + j] = acc / jOmega;
            }
        }

        if (times is not null) times.PortSolveMs += sw!.Elapsed.TotalMilliseconds;

        if (symmetrise) Symmetrise(z, t);
        return z;
    }

    /// <summary>
    /// <c>M̃ = −ω²L + K̃ + jωD(ω)</c> into the workspace. <c>(jω)² = −ω²</c>, so the <b>L</b> term is real
    /// and negative and only the diagonal picks up an imaginary part; <b>L</b> and <b>K̃</b> are never
    /// modified.
    /// </summary>
    private void AssembleMTilde(double frequencyHz, Workspace ws)
    {
        int ns = SegmentCount;
        double omega = 2.0 * Math.PI * frequencyHz;
        var jOmega = new Complex(0.0, omega);

        var m = ws.MTilde;
        var kTilde = _assembly.KTilde;
        for (int i = 0; i < ns; i++)
        {
            int row = i * ns;
            for (int j = 0; j < ns; j++)
                m[row + j] = new Complex(kTilde[row + j] - omega * omega * _l[row + j], 0.0);
        }

        _internal.FillDiagonal(frequencyHz, ws.Diagonal);
        for (int k = 0; k < ns; k++) m[k * ns + k] += jOmega * ws.Diagonal[k];
    }

    /// <summary>
    /// Which frequency points fell back from <see cref="ComplexLdlt"/> to <see cref="ComplexLu"/>.
    ///
    /// <para>A silent fallback would make the two factorisations indistinguishable in a measurement and
    /// would hide the one failure mode the symmetric path has, so a sweep that took any is a sweep that
    /// says so in its notes.</para>
    /// </summary>
    private sealed class FallbackLog
    {
        private readonly List<double> _points = [];

        public void Note(double frequencyHz)
        {
            lock (_points) _points.Add(frequencyHz);
        }

        public int Count { get { lock (_points) return _points.Count; } }

        public double Lowest { get { lock (_points) return _points.Count == 0 ? 0.0 : _points.Min(); } }
    }

    /// <summary><c>Y_port(f) = Z_port(f)^-1</c>, T × T row-major, in siemens.</summary>
    public Complex[] PortAdmittance(double frequencyHz) => Invert(PortImpedance(frequencyHz), TerminalCount);

    /// <summary>
    /// Solves a whole grid, and attaches the notes (§4) that say what the answer does and does not
    /// claim. One factorisation per point; nothing frequency-independent is touched.
    /// </summary>
    public WireMomResult Solve(IReadOnlyList<double> frequenciesHz, CancellationToken cancel = default)
        => Solve(frequenciesHz, cancel, null);

    /// <summary>
    /// The same, counting frequency points through <paramref name="run"/>.
    ///
    /// <para><b>The tick is the LEAF unit and there is nothing under it</b> — a single point is one
    /// factorisation, not a countable sequence — so the sweep bar advances once per point and the stage
    /// row simply names the sweep. That is the same split the EM sweep uses, and for the same reason:
    /// a bar per Newton step would be inventing a denominator the work does not have.</para>
    ///
    /// <para>Ticking happens from every worker thread of the frequency-parallel loop at once;
    /// <see cref="WBondRunControl"/> is written for that.</para>
    /// </summary>
    public WireMomResult Solve(IReadOnlyList<double> frequenciesHz, CancellationToken cancel,
                               WBondRunControl? run)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);

        var f = new double[frequenciesHz.Count];
        for (int i = 0; i < f.Length; i++) f[i] = frequenciesHz[i];

        var z = new Complex[f.Length][];
        var y = new Complex[f.Length][];
        var fallbacks = new FallbackLog();

        // FREQUENCY POINTS ARE COMPLETELY INDEPENDENT: same L, same K~, same W, same H, and only D(w),
        // M~, its factorisation and the T solves are the point's own. The constraint is MEMORY, not
        // cores — see WireMomCost.SolveThreadCount and the note this run attaches.
        int threads = Math.Min(WireMomCost.SolveThreadCount(SegmentCount, TerminalCount, Settings), f.Length);

        run?.BeginStage(threads <= 1
            ? "solving the frequency sweep"
            : $"solving the frequency sweep, {threads} points at a time");

        if (threads <= 1)
        {
            var ws = _point ??= new Workspace(SegmentCount, TerminalCount);
            for (int i = 0; i < f.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                z[i] = SolvePoint(f[i], ws, symmetrise: true, times: null, fallbacks);
                y[i] = Invert(z[i], TerminalCount);
                run?.Tick();
            }
        }
        else
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancel };

            // ONE WORKSPACE PER WORKER, not per point: Parallel.For's thread-local initialiser is what
            // makes "threads x 16 N_s^2 bytes" the real peak rather than "points x".
            Parallel.For(0, f.Length, options,
                () => new Workspace(SegmentCount, TerminalCount),
                (i, _, ws) =>
                {
                    z[i] = SolvePoint(f[i], ws, symmetrise: true, times: null, fallbacks);
                    y[i] = Invert(z[i], TerminalCount);
                    run?.Tick();
                    return ws;
                },
                static _ => { });
        }

        return new WireMomResult(f, z, y, TerminalCount, TerminalNames, Report,
                                 BuildNotes(f, threads, fallbacks));
    }

    /// <summary>
    /// How many frequency points this design would be solved concurrently at, and what that costs in
    /// memory — askable <b>before</b> a sweep, because it is one of the numbers the prediction is made
    /// of.
    /// </summary>
    public int SolveThreadCount => WireMomCost.SolveThreadCount(SegmentCount, TerminalCount, Settings);

    /// <summary>
    /// <c>Z_port(f)</c> transformed onto the <b>array (differential) basis</b>, M × M row-major — the
    /// form whose diagonal is each array's own series impedance and whose <b>off-diagonal is the mutual
    /// between two arrays</b>.
    ///
    /// <code>
    /// Z_arr = T Z_port Tᵀ,   T[k, 2k] = +1,  T[k, 2k+1] = −1
    /// M_ij  = Im(Z_arr[i,j]) / ω
    /// </code>
    ///
    /// <h3>Why this transform and not something else</h3>
    /// <para>Injecting <c>+i</c> at terminal <c>2k</c> and <c>−i</c> at <c>2k+1</c> is
    /// <c>i = Tᵀ i_arr</c>, and the voltage across the pair is <c>v_arr = T v</c>, so
    /// <c>v_arr = T Z_port Tᵀ i_arr</c>. <b>T's rows sum to zero, which is the point</b>: at low
    /// frequency <c>Z_port</c> is dominated by a common-mode <c>1/(jωC)</c> open circuit — millions of
    /// ohms against a fraction of one — and a zero-row-sum congruence annihilates it exactly rather
    /// than subtracting it approximately.</para>
    ///
    /// <h3>This is NOT <see cref="SeriesArmImpedance"/>, and the difference is the whole point</h3>
    /// <para><see cref="SeriesArmImpedance"/> removes the shunt <i>by construction</i> and is therefore
    /// provably equal to <see cref="ImpedanceReduction.ArrayImpedance"/> — it can never tell you
    /// anything the lumped model does not already say. <b>This one comes out of the full solve</b>, so
    /// its mutual is the distributed model's own answer and is the one worth comparing.</para>
    ///
    /// <h3>It stops meaning "an inductance" near self-resonance, and that is not a defect</h3>
    /// <para>Above roughly a third of the array's self-resonance the shunt is a real part of the
    /// network and no series mutual inductance exists to extract. Measured on two 100 mil bonds
    /// resonating at 27.6 GHz, the same transform applied to the <i>lumped</i> model — where the answer
    /// is independently known — reproduces it to 1e-7 at 10 MHz, 1e-4 at 1 GHz and 1e-2 at 10 GHz, then
    /// fails outright past resonance. <b>Run it on the lumped model as a control if you need to know
    /// where to stop trusting it.</b></para>
    /// </summary>
    public Complex[] PortImpedanceInArrayBasis(double frequencyHz)
    {
        var z = PortImpedance(frequencyHz);
        int t = TerminalCount, m = ArrayCount;

        var arr = new Complex[m * m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                arr[i * m + j] = z[(2 * i) * t + 2 * j]     - z[(2 * i) * t + 2 * j + 1]
                               - z[(2 * i + 1) * t + 2 * j] + z[(2 * i + 1) * t + 2 * j + 1];

        Symmetrise(arr, m);
        return arr;
    }

    // ------------------------------------------------------------------ the series arm (§3)

    /// <summary>
    /// What this mesh says the <b>series arm</b> is, with the shunt path removed by construction rather
    /// than by taking a frequency limit:
    ///
    /// <code>
    /// Z_wire[i,j] = SUM_{p in wire i} SUM_{q in wire j} ( jw L[p,q] + delta_pq D[p](w) )
    /// Z_arr       = ( A^T Z_wire^-1 A )^-1                              M x M
    /// </code>
    ///
    /// <para><b>On a subdivided mesh this must reproduce
    /// <see cref="ImpedanceReduction.ArrayImpedance"/> exactly</b>, and that is the point of it. With no
    /// shunt path KCL forces one current per wire, partial inductance is additive under subdivision
    /// (WM-1 §9.2 proves it), and <c>D</c> scales with length — so the segment basis and the wire basis
    /// describe the same circuit. It is a bridge to an already-validated number, not a second model:
    /// if this disagrees, the mesh, the L fill or the internal impedance is wrong and nothing
    /// downstream is worth debugging.</para>
    ///
    /// <para>It also serves the caller who genuinely wants the no-capacitance comparison, which
    /// <see cref="WBondDesign.IncludeCapacitance"/> cannot express for this kernel — see
    /// <see cref="BuildNotes"/>.</para>
    /// </summary>
    public Complex[] SeriesArmImpedance(double frequencyHz)
    {
        int nw = Mesh.WireCount;
        int m = ArrayCount;
        double omega = 2.0 * Math.PI * frequencyHz;

        // Z_wire = jw * (L summed to the wire basis) + diag( sum of D over each wire's segments ).
        var lWire = SegmentInductance.SumToWireBasis(Mesh, _l);
        var d = _internal.Diagonal(frequencyHz);

        var zWire = new Complex[nw * nw];
        for (int i = 0; i < nw; i++)
            for (int j = 0; j < nw; j++)
                zWire[i * nw + j] = new Complex(0.0, omega * lWire[i * nw + j]);

        for (int k = 0; k < SegmentCount; k++)
        {
            int wire = Mesh.WireOfSegment[k];
            zWire[wire * nw + wire] += d[k];
        }

        // Y_arr = A^T Z_wire^-1 A — A is 0/1 with one 1 per row, so both products are scatter-adds.
        var lu = ComplexLu.Factor(zWire, nw);
        var map = Mesh.ArrayOfWire;

        var x = new Complex[nw * m];
        var rhs = new Complex[nw];
        for (int a = 0; a < m; a++)
        {
            Array.Clear(rhs);
            for (int i = 0; i < nw; i++) if (map[i] == a) rhs[i] = Complex.One;

            var column = lu.Solve(rhs);
            for (int i = 0; i < nw; i++) x[i * m + a] = column[i];
        }

        var yArr = new Complex[m * m];
        for (int i = 0; i < nw; i++)
        {
            int row = map[i] * m, xr = i * m;
            for (int a = 0; a < m; a++) yArr[row + a] += x[xr + a];
        }

        Symmetrise(yArr, m);
        return Invert(yArr, m);
    }

    // ------------------------------------------------------------------ notes (§4)

    /// <summary>
    /// The result's notes: the capacitance note, every mesh warning, and the validity note — which is
    /// always present and always carries its two numbers.
    /// </summary>
    private IReadOnlyList<string> BuildNotes(IReadOnlyList<double> frequenciesHz) =>
        BuildNotes(frequenciesHz, 1, null);

    private IReadOnlyList<string> BuildNotes(IReadOnlyList<double> frequenciesHz, int threads, FallbackLog? fallbacks)
    {
        var notes = new List<string>();

        // WHY THE SWEEP RAN AT THE WIDTH IT DID, in one line. A user whose 200-wire sweep took twenty
        // minutes on a ten-core machine should not have to guess that it was memory that held it to
        // three threads.
        if (frequenciesHz.Count > 1)
        {
            long perThread = WireMomCost.BytesPerSolveThread(SegmentCount, TerminalCount);
            notes.Add(
                $"Solved {(threads == 1 ? "one frequency point at a time" : $"{threads} frequency points at a time")} " +
                $"({perThread / 1048576.0:0.#} MB of workspace each, " +
                $"{Environment.ProcessorCount} core(s) available).");
        }

        if (fallbacks is { Count: > 0 })
            notes.Add(
                $"{fallbacks.Count} of {frequenciesHz.Count} frequency point(s) fell back from the " +
                $"complex-symmetric factorisation to a pivoted LU (lowest at " +
                $"{fallbacks.Lowest * 1e-9:0.###} GHz). The answers are the LU's and are unaffected; " +
                $"the fallback exists because an unpivoted LDLt can break down on a well-conditioned " +
                $"matrix.");

        // IncludeCapacitance = false has NO MEANING for this kernel. The MoM network IS the coupled
        // L-C ladder: with G^-1 -> 0 the whole reduction degenerates (K~, W and H all vanish). So the
        // setting is neither refused nor silently obeyed — the capacitance is included and the result
        // says so. SeriesArmImpedance is what serves a caller who wants the series arm alone.
        if (!Design.IncludeCapacitance)
            notes.Add(
                "Capacitance is intrinsic to the distributed model and is included. The design's " +
                "'Include capacitance' setting applies to the lumped model only.");

        foreach (string warning in Report.Warnings) notes.Add(warning);

        notes.Add(ValidityNote(frequenciesHz));
        return notes;
    }

    /// <summary>
    /// The quasi-static caveat, with its two numbers substituted.
    ///
    /// <para>The design note (§4.1) is explicit that the neglected retardation term is largest where the
    /// coupling is <b>smallest</b> — a distant pair contributes little to begin with — so this is a
    /// caveat rather than an alarm, and it is worded as one.</para>
    /// </summary>
    private string ValidityNote(IReadOnlyList<double> frequenciesHz)
    {
        double top = 0.0;
        foreach (double f in frequenciesHz) top = Math.Max(top, f);

        double tenthLambdaMm = top > 0.0 ? 0.1 * (299_792_458.0 / top) * 1e3 : double.PositiveInfinity;
        double widestMm = WidestWirePairSeparationMetres() * 1e3;

        string reach = double.IsInfinity(tenthLambdaMm)
            ? "no frequency was requested"
            : $"{tenthLambdaMm:0.###} mm at {top * 1e-9:0.###} GHz";

        return
            $"Quasi-static: this model has no radiation and its mutual coupling is instantaneous. A wire " +
            $"pair separated by more than lambda/10 ({reach}) is increasingly optimistic about their " +
            $"coupling; the widest wire-pair separation in this design is {widestMm:0.###} mm.";
    }

    /// <summary>
    /// The largest centre-to-centre distance between two wires. Centroids rather than closest approach:
    /// the quantity the caveat is about is how far apart two <i>coupled</i> wires are, and the closest
    /// approach of two distant wires understates that by their own length.
    /// </summary>
    private double WidestWirePairSeparationMetres()
    {
        int nw = Mesh.WireCount;
        if (nw < 2) return 0.0;

        var cx = new double[nw];
        var cy = new double[nw];
        var cz = new double[nw];

        for (int k = 0; k < SegmentCount; k++)
        {
            int w = Mesh.WireOfSegment[k];
            ref readonly var f = ref Mesh.Segments[k];
            double half = 0.5 * f.Length;
            cx[w] += f.Ax + half * f.Ux;
            cy[w] += f.Ay + half * f.Uy;
            cz[w] += f.Az + half * f.Uz;
        }

        for (int w = 0; w < nw; w++)
        {
            double n = Mesh.WireSegCount[w];
            if (n <= 0) continue;
            cx[w] /= n; cy[w] /= n; cz[w] /= n;
        }

        double worst = 0.0;
        for (int i = 0; i < nw; i++)
            for (int j = i + 1; j < nw; j++)
            {
                double dx = cx[i] - cx[j], dy = cy[i] - cy[j], dz = cz[i] - cz[j];
                worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }

        return worst;
    }

    // ------------------------------------------------------------------ refusals and small algebra

    /// <summary>
    /// §5's floor. Below it <c>M~</c>'s conditioning has eaten the answer, and the analytic model —
    /// which has no such limit, because its reduction never forms <c>K~</c> — is the right thing to
    /// use instead. The message names it.
    /// </summary>
    private void RefuseIfBelowFloor(double frequencyHz)
    {
        double floor = Settings.MinimumFrequencyHz;
        if (frequencyHz >= floor) return;

        throw new InvalidOperationException(
            $"The distributed (MoM) model was asked for {frequencyHz:0.###E+0} Hz, below its measured " +
            $"floor of {floor:0.###E+0} Hz. M~(w) tends to K~ as w tends to zero, and K~ is singular " +
            $"whenever an array has two or more wires, so the condition number grows like 1/f and below " +
            $"this frequency the answer is rounding noise rather than physics. Use the lumped (analytic) " +
            $"model there — its array reduction consumes L and A only and has no low-frequency limit at " +
            $"all.");
    }

    private static void Symmetrise(Complex[] m, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                Complex v = 0.5 * (m[i * n + j] + m[j * n + i]);
                m[i * n + j] = v;
                m[j * n + i] = v;
            }
    }

    private static Complex[] Invert(Complex[] a, int n)
    {
        var lu = ComplexLu.Factor(a, n);
        var inverse = new Complex[n * n];
        var rhs = new Complex[n];

        for (int j = 0; j < n; j++)
        {
            Array.Clear(rhs);
            rhs[j] = Complex.One;
            var column = lu.Solve(rhs);
            for (int i = 0; i < n; i++) inverse[i * n + j] = column[i];
        }

        Symmetrise(inverse, n);
        return inverse;
    }
}
