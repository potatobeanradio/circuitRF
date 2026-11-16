// brief-paa-8 R-paa8-2 / R-paa8-3 — the identical-channel fast path of a Feed-mode Phased Antenna Array (docs/design/phased-array.md §6.3).
//
// THE BEAMFORMER ISOLATES. Its S is [[0, tᵀ], [t, 0]] (PhasedArrayFeedModel): path k carries t_k = w_k/√N from F to element k and back, and
// no path reaches another. So a wave reflected up channel k leaves at F and nowhere else, and the elements' side closes on its own:
//
//   α = (I − Γ_ch·S)⁻¹ · S21·t · a_F          the waves incident on the elements, a_F the wave the circuit sends into F
//   β = S·α                                   the waves they send back
//   Γ_F = Σ_k t_k·(S11·t_k + S12·β_k/a_F)     what the closed array presents at F
//
// with S the array network's (PhasedArrayModel.SparseAt), [S11 S12; S21 S22] the channel's two-port in the array's Z0 (in from the
// beamformer, out to the element) and Γ_ch = S22 on every element whose channel is placed. With no channel the channel is a through
// (S11 = S22 = 0) and α = t·a_F with no solve at all; with Coupling=off S is diagonal and so is the closure.
//
// WHAT IS KEPT. I − S22·S does not depend on the weights, so its sparse LU is factorised once per frequency and kept while nothing but the
// weights moves (R-paa8-3): the key is the resolved values of the array's parameters other than Amp/Phase/Delay (never in them: the
// elaborator evaluates those per element), PhaseBits, AttenStep and Off, and of one channel's components — no expression text. An element
// switched off has no channel (an identical one is omitted) and its row of the closure is the identity's; that is a rank-|Off| change of the
// kept factor, applied by Sherman-Morrison-Woodbury against it rather than by factorising again, so Off moving keeps the factor too.
//
// THE CIRCUIT OUTSIDE. Everything that is not the array's own parts is solved by the ordinary engine with the closed array standing at F as
// a one-port of reflection Γ_F (ArrayFeedLoadModel): the drive's reflection, the circuit's S and the wave into F come out exactly as the full
// solve reports them. That circuit is the bench's port and whatever feeds the array — a few nodes — so it is solved at every evaluation.
//
// WHEN NOT. A channel that differs per element, a nonlinear part anywhere (the S-parameter run linearises it about an operating point the
// whole circuit sets), a complex Z0 (power waves do not join across a complex reference), a channel reaching a net outside itself, or a
// circuit wired to the array's internal nodes: the whole circuit is solved as elaborated. Decide says which, in a sentence explain prints.

using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CSparse;
using CSparse.Complex;
using CSparse.Complex.Factorization;
using RfCore.Data;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>R-paa8-2 — how a Feed-mode array's circuit is solved, and why, in one sentence.</summary>
public sealed record PhasedArraySolvePath(bool Fast, string Sentence);

/// <summary>R-paa8-1 — the last fast evaluation's costs, for the measurement harness.</summary>
public sealed record PhasedArrayFastTimings(double ChannelMs, double FactorMs, double BackSubstitutionMs, double OuterMs,
                                            long FactorNonZeros, long MatrixNonZeros, string Closure);

/// <summary>R-paa8-2 / R-paa8-3 — the identical-channel fast path of a Feed-mode array.</summary>
public static class PhasedArrayFastPath
{
    /// <summary>The fill-reducing ordering the closure is factorised with: the closure's pattern is the coupling's, symmetric, so minimum
    /// degree on A + Aᵀ (R-paa8-1 measured it against the natural row-major order at 4096 elements).</summary>
    public const ColumnOrdering ClosureOrdering = ColumnOrdering.MinimumDegreeAtPlusA;

    /// <summary>Above this many elements switched off, the closure is factorised for that set rather than updated against the kept factor.</summary>
    public const int WoodburyLimit = 256;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private static long _channelSolves, _factorisations, _backSubstitutions, _fastEvaluations;
    private static PhasedArrayFastTimings? _last;

    /// <summary>Channel two-port solves, one per frequency, in this process.</summary>
    public static long ChannelSolves => Interlocked.Read(ref _channelSolves);

    /// <summary>Closure factorisations, one per frequency, in this process.</summary>
    public static long ClosureFactorisations => Interlocked.Read(ref _factorisations);

    /// <summary>Back-substitutions through a kept closure factor, in this process.</summary>
    public static long BackSubstitutions => Interlocked.Read(ref _backSubstitutions);

    /// <summary>Evaluations that took the fast path, in this process.</summary>
    public static long FastEvaluations => Interlocked.Read(ref _fastEvaluations);

    /// <summary>The last fast evaluation's costs; null before the first.</summary>
    public static PhasedArrayFastTimings? LastTimings { get { lock (_gate) return _last; } }

    /// <summary>Off: every evaluation solves the whole circuit — what the gate comparing the two paths and the measurement harness set.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Drops every kept channel solve and factor.</summary>
    public static void Clear() { lock (_gate) { _states.Clear(); _receive.Clear(); } }

    /// <summary>How the array at <paramref name="block"/> in <paramref name="nl"/> is solved, and why. Null when it is not a Feed-mode array.</summary>
    public static PhasedArraySolvePath? Decide(ElaboratedNetlist nl, string block)
        => Split(nl, block) is { } parts ? parts.Path : null;

    /// <summary>What a fast evaluation gives: the waves at the array's pins for the drives, per frequency, as SParameterEngine.BlockWaves reports
    /// them, and the circuit's S and Z0 cubes as SParameterEngine.Run gives them.</summary>
    public sealed record Solved(SParameterEngine.BlockPortDrive[] Waves, DataSet CircuitS, PhasedArraySolvePath Path);

    /// <summary>
    /// The fast evaluation of the Feed-mode array at <paramref name="block"/> at <paramref name="freqs"/> for <paramref name="drives"/>, or null
    /// when the fast path does not apply (<see cref="Decide"/> says why).
    /// </summary>
    public static Solved? TryWaves(ElaboratedNetlist nl, string block, double[] freqs, IReadOnlyList<(int Port, Complex Amplitude)> drives)
    {
        if (!Enabled || Split(nl, block) is not { Path.Fast: true } parts) return null;
        // brief-pch-9 — a channel's beam is selected before the weights below are read
        if (freqs.Length > 0) BeamSelection.Apply(nl, freqs[freqs.Length / 2]);
        // brief-pch-9 — a Propagation Channel that RECEIVES on this array puts its rays on the elements, which the closed array at F does
        // not carry: solved whole instead
        if (nl.Components.Any(c => c.Model is PropagationChannelModel ch && ReferenceEquals(ch.RxEnd, parts.Block))) return null;
        var pa = parts.Array;
        var feed = pa.Feed!;
        int n = pa.PortCount;
        double z0 = pa.Z0.Real;
        double channelMs = 0, factorMs = 0, backMs = 0;
        long factorNnz = 0, matrixNnz = 0;
        string closure = "";

        var alphaHat = new Complex[freqs.Length][];
        var betaHat = new Complex[freqs.Length][];
        var gammaF = new Dictionary<double, Complex>();
        lock (_gate)
        {
            if (!_states.TryGetValue(block, out var state)) _states[block] = state = new State();
            // ── the channel two-port, once per frequency while its resolved values stand ──
            Complex[][] ch;
            if (parts.Channel is { } channel)
            {
                if (state.ChannelKey != channel.Key) { state.ChannelKey = channel.Key; state.ChannelS.Clear(); state.Closures.Clear(); }
                var missing = freqs.Where(f => !state.ChannelS.ContainsKey(f)).ToArray();
                if (missing.Length > 0)
                {
                    var t0 = Stopwatch.StartNew();
                    var solved = SolveChannel(nl, channel, missing, z0);
                    for (int i = 0; i < missing.Length; i++) state.ChannelS[missing[i]] = solved[i];
                    Interlocked.Add(ref _channelSolves, missing.Length);
                    channelMs = t0.Elapsed.TotalMilliseconds;
                }
                ch = [.. freqs.Select(f => state.ChannelS[f])];
            }
            else
            {
                if (state.ChannelKey is not null) { state.ChannelKey = null; state.ChannelS.Clear(); state.Closures.Clear(); }
                ch = [.. freqs.Select(_ => Through)];
            }
            if (state.ArrayKey != parts.ArrayKey) { state.ArrayKey = parts.ArrayKey; state.Closures.Clear(); }

            for (int fi = 0; fi < freqs.Length; fi++)
            {
                double hz = freqs[fi];
                var s = ch[fi];
                Complex s11 = s[0], s12 = s[1], s21 = s[2], s22 = s[3];
                var t = new Complex[n];
                double norm = 1 / Math.Sqrt(n);
                for (int k = 0; k < n; k++) t[k] = feed.Quantised(k, hz) * norm;
                var a = new Complex[n];
                var sa = state.Closures.TryGetValue(hz, out var kept) ? kept : null;
                var arrayS = sa?.S ?? pa.SparseAt(hz);
                bool diagonal = sa?.Diagonal ?? IsDiagonal(arrayS);
                var r = new Complex[n];
                for (int k = 0; k < n; k++) r[k] = pa.Lattice.IsOn(k) ? s21 * t[k] : Complex.Zero;

                if (s22 == Complex.Zero)
                {
                    // no reflection up the channel: each element's wave is its path's, with no closure at all
                    Array.Copy(r, a, n);
                    closure = "none: no wave returns down a channel, so each element's incident wave is its path's";
                    sa ??= state.Closures[hz] = new Closure(arrayS, diagonal, null, 0, 0);
                }
                else if (diagonal)
                {
                    for (int k = 0; k < n; k++)
                        a[k] = pa.Lattice.IsOn(k) ? r[k] / (1 - s22 * arrayS[k, k]) : Complex.Zero;
                    closure = "diagonal: the elements do not couple, so each closes on its own reflection";
                    sa ??= state.Closures[hz] = new Closure(arrayS, true, null, 0, 0);
                }
                else
                {
                    if (sa?.Lu is null)
                    {
                        var t0 = Stopwatch.StartNew();
                        var (lu, nnz) = Factorise(arrayS, s22, null);
                        factorMs += t0.Elapsed.TotalMilliseconds;
                        Interlocked.Increment(ref _factorisations);
                        sa = state.Closures[hz] = new Closure(arrayS, false, lu, nnz, arrayS.NonZeros + n);
                    }
                    factorNnz = sa.FactorNonZeros;
                    matrixNnz = sa.MatrixNonZeros;
                    var t1 = Stopwatch.StartNew();
                    SolveClosure(sa, pa.Lattice, s22, r, a);
                    backMs += t1.Elapsed.TotalMilliseconds;
                    closure = $"sparse LU of I − Γ_ch·S ({ClosureOrdering}), {sa.FactorNonZeros:N0} non-zeros in L and U for {sa.MatrixNonZeros:N0} in the matrix";
                }
                var b = new Complex[n];
                arrayS.Multiply(a, b);
                Complex g = 0;
                for (int k = 0; k < n; k++)
                    if (pa.Lattice.IsOn(k) && t[k] != Complex.Zero) g += t[k] * (s11 * t[k] + s12 * b[k]);
                alphaHat[fi] = a;
                betaHat[fi] = b;
                gammaF[hz] = g;
            }
        }

        // ── the circuit outside, with the closed array at F ──
        var t2 = Stopwatch.StartNew();
        var (outer, loadPath) = Outer(nl, parts, gammaF, z0);
        var waves = new SParameterEngine.BlockPortDrive[freqs.Length];
        double root = Math.Sqrt(z0);
        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var atF = SParameterEngine.BlockWaves(outer, freqs[fi], loadPath, drives);
            Complex aF = (atF.Voltage[0] + z0 * atF.Current[0]) / (2 * root);
            var v = new Complex[n];
            var i = new Complex[n];
            for (int k = 0; k < n; k++)
            {
                Complex alpha = aF * alphaHat[fi][k], beta = aF * betaHat[fi][k];
                v[k] = root * (alpha + beta);
                i[k] = (alpha - beta) / root;
            }
            waves[fi] = atF with { Voltage = v, Current = i };
        }
        var run = SParameterEngine.Run(outer, freqs);
        var circuitS = new DataSet();
        foreach (string name in (string[])["S", "Z0"])
            if (run.Cubes.TryGetValue(name, out var cube)) circuitS.Add(name, cube);
        foreach (string warning in outer.Warnings) nl.AddWarningOnce($"paa-fast:{warning}", warning);
        double outerMs = t2.Elapsed.TotalMilliseconds;
        Interlocked.Increment(ref _fastEvaluations);
        lock (_gate) _last = new PhasedArrayFastTimings(channelMs, factorMs, backMs, outerMs, factorNnz, matrixNnz, closure);
        return new Solved(waves, circuitS, parts.Path);
    }

    private static readonly Complex[] Through = [Complex.Zero, Complex.One, Complex.One, Complex.Zero];

    // ── brief-pch-8b R-pch8b-1 — the receive collapse ─────────────────────────────────────────────────────────

    private static readonly Dictionary<string, PropagationChannelModel.ReceiveCollapse> _receive = new(StringComparer.Ordinal);

    /// <summary>Receive collapses formed in this process (one per array, frequency and resolved values).</summary>
    public static long ReceiveCollapses => Interlocked.Read(ref _receiveCollapses);
    private static long _receiveCollapses;

    [System.Runtime.CompilerServices.ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "src/Core cannot reference src/Engine; the receive collapse is installed into its seam when the engine loads, as src/Design installs the pattern readers.")]
    internal static void InstallReceive() => PropagationChannelModel.ReceiveCollapseSource = Receive;

    /// <summary>
    /// What a Feed-mode receive array that meets the fast path's conditions does with the waves its elements emit (b = S·a + c), with its feed
    /// matched: the beamformer is matched and isolating, so the wave from channel k's beamformer side leaves at F with t_k and nothing comes
    /// back; the element side closes as M·b = c with M = I − Γ_ch·D·S (D the elements whose channel is placed, Γ_ch the channel's S22) — the
    /// fast path's own closure matrix. So the feed sends b_F = S12_ch·tᵀ·M⁻¹·c into a matched load: G = S12_ch·M⁻ᵀ·t, solved with Mᵀ factorised
    /// explicitly. Γ_F is what the closed array presents at F, as <see cref="TryWaves"/> forms it. Null, with the fast path's sentence, where
    /// it does not apply.
    /// </summary>
    public static (PropagationChannelModel.ReceiveCollapse? Collapse, string Sentence) Receive(ElaboratedNetlist nl, ElaboratedComponent array, double hz)
    {
        if (!Enabled) return (null, "Element by element: the array fast path is switched off.");
        if (Split(nl, array.InstancePath) is not { } parts) return (null, "Element by element: the receive end is not a Feed-mode array.");
        if (!parts.Path.Fast) return (null, "Element by element: " + parts.Path.Sentence);
        var pa = parts.Array;
        int n = pa.PortCount;
        double z0 = pa.Z0.Real;
        // brief-pch-9 — and per WEIGHTS: G = S12·M⁻ᵀ·t and Γ_F carry the beamformer's t, and ArrayKey leaves out what only moves the weights,
        // so a key without them handed a re-weighed array (a selected beam, a tuned steer VAR) the previous beam's collapse
        string key = $"{array.InstancePath}|{parts.ArrayKey}|{parts.Channel?.Key}|{pa.Feed!.WeightsKey()}|{hz.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
        // G and Γ_F are kept per resolved array, channel, weights and frequency; the feed's node is this netlist's own, never kept
        lock (_gate)
            if (_receive.TryGetValue(key, out var kept)) return (kept with { FeedNode = parts.Feed.Nodes[0] }, Taken(parts));
        var ch = parts.Channel is { } channel ? SolveChannel(nl, channel, [hz], z0)[0] : Through;
        Complex s11 = ch[0], s12 = ch[1], s21 = ch[2], s22 = ch[3];
        var feed = pa.Feed!;
        var t = new Complex[n];
        double norm = 1 / Math.Sqrt(n);
        for (int k = 0; k < n; k++) t[k] = pa.Lattice.IsOn(k) ? feed.Quantised(k, hz) * norm : Complex.Zero;
        var s = pa.SparseAt(hz);
        var off = new HashSet<int>(Enumerable.Range(0, n).Where(k => !pa.Lattice.IsOn(k)));
        // M = I − s22·D·S (off rows the identity's); z = M⁻ᵀ·t, a = M⁻¹·(s21·t)
        var z = new Complex[n];
        var a = new Complex[n];
        if (s22 == Complex.Zero) { Array.Copy(t, z, n); for (int k = 0; k < n; k++) a[k] = s21 * t[k]; }
        else
        {
            var (lu, _) = Factorise(s, s22, off);
            var r = t.Select(x => s21 * x).ToArray();
            lu.Solve(r, a);
            var luT = FactoriseTranspose(s, s22, off);
            luT.Solve(t, z);
        }
        var g = new Complex[n];
        for (int k = 0; k < n; k++) g[k] = s12 * z[k];
        var b = new Complex[n];
        s.Multiply(a, b);
        Complex gammaF = 0;
        for (int k = 0; k < n; k++) if (pa.Lattice.IsOn(k) && t[k] != Complex.Zero) gammaF += t[k] * (s11 * t[k] + s12 * b[k]);
        var result = new PropagationChannelModel.ReceiveCollapse(parts.Feed.Nodes[0], g, gammaF, z0);
        lock (_gate) _receive[key] = result;
        Interlocked.Increment(ref _receiveCollapses);
        return (result, Taken(parts));
    }

    /// <summary>
    /// brief-pch-10b R-pch10b-1 — Γ_F alone, the reflection the closed array presents at its feed at <paramref name="hz"/>: what a receive
    /// array replaced by its feed presents. One factorization (M, not also Mᵀ: the transfer G is not needed), kept per resolved array,
    /// channel, weights and frequency, and taken from a full collapse when one was made at that frequency. Null with the reason off the path.
    /// </summary>
    public static (Complex? GammaF, string Sentence) FeedReflection(ElaboratedNetlist nl, ElaboratedComponent array, double hz)
    {
        if (!Enabled) return (null, "Element by element: the array fast path is switched off.");
        if (Split(nl, array.InstancePath) is not { } parts) return (null, "Element by element: the receive end is not a Feed-mode array.");
        if (!parts.Path.Fast) return (null, "Element by element: " + parts.Path.Sentence);
        var pa = parts.Array;
        int n = pa.PortCount;
        string key = $"{array.InstancePath}|{parts.ArrayKey}|{parts.Channel?.Key}|{pa.Feed!.WeightsKey()}|{hz.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
        lock (_gate)
        {
            if (_receive.TryGetValue(key, out var kept)) return (kept.GammaF, Taken(parts));
            if (_feedReflection.TryGetValue(key, out var g0)) return (g0, Taken(parts));
        }
        var ch = parts.Channel is { } channel ? SolveChannel(nl, channel, [hz], pa.Z0.Real)[0] : Through;
        Complex s11 = ch[0], s12 = ch[1], s21 = ch[2], s22 = ch[3];
        var t = new Complex[n];
        double norm = 1 / Math.Sqrt(n);
        for (int k = 0; k < n; k++) t[k] = pa.Lattice.IsOn(k) ? pa.Feed!.Quantised(k, hz) * norm : Complex.Zero;
        var s = pa.SparseAt(hz);
        var off = new HashSet<int>(Enumerable.Range(0, n).Where(k => !pa.Lattice.IsOn(k)));
        var a = new Complex[n];
        if (s22 == Complex.Zero) for (int k = 0; k < n; k++) a[k] = s21 * t[k];
        else Factorise(s, s22, off).Lu.Solve(t.Select(x => s21 * x).ToArray(), a);
        var b = new Complex[n];
        s.Multiply(a, b);
        Complex gammaF = 0;
        for (int k = 0; k < n; k++) if (pa.Lattice.IsOn(k) && t[k] != Complex.Zero) gammaF += t[k] * (s11 * t[k] + s12 * b[k]);
        lock (_gate) _feedReflection[key] = gammaF;
        return (gammaF, Taken(parts));
    }

    private static readonly Dictionary<string, Complex> _feedReflection = [];

    private static string Taken(Parts parts)
        => $"Collapsed: {parts.Array.PortCount} receive elements reach the feed through one closure ({parts.Path.Sentence.Replace("Fast path: ", "")}), so each " +
           "channel's rays are stamped as one source at the feed rather than on every element.";

    /// <summary>LU of Mᵀ, M = I − s22·D·S with the rows of <paramref name="identityRows"/> the identity's (so Mᵀ has those columns the identity's).</summary>
    private static SparseLU FactoriseTranspose(ArraySparseMatrix s, Complex s22, HashSet<int> identityRows)
    {
        int n = s.Size;
        var cols = new List<(int Row, Complex Value)>[n];      // column i of Mᵀ is row i of M
        for (int i = 0; i < n; i++)
        {
            cols[i] = [];
            if (identityRows.Contains(i)) { cols[i].Add((i, Complex.One)); continue; }
            bool diag = false;
            foreach (var (c, v) in s.Row(i))
            {
                Complex m = -s22 * v;
                if (c == i) { m += 1; diag = true; }
                cols[i].Add((c, m));
            }
            if (!diag) cols[i].Add((i, Complex.One));
        }
        var colPtr = new int[n + 1];
        for (int c = 0; c < n; c++) colPtr[c + 1] = colPtr[c] + cols[c].Count;
        var rowIdx = new int[colPtr[n]];
        var values = new Complex[colPtr[n]];
        for (int c = 0; c < n; c++)
        {
            cols[c].Sort((x, y) => x.Row.CompareTo(y.Row));
            for (int k = 0; k < cols[c].Count; k++) { rowIdx[colPtr[c] + k] = cols[c][k].Row; values[colPtr[c] + k] = cols[c][k].Value; }
        }
        return SparseLU.Create(new SparseMatrix(n, n, values, rowIdx, colPtr), ClosureOrdering, 1.0);
    }

    // ── R-paa8-4 — the estimate a slider shows where the fast path does not apply ──────────────────────────────

    private sealed record Held(string Key, double[] Freqs, (int, Complex)[] Drives, SParameterEngine.BlockPortDrive[] Waves, Complex[][] Weights, DataSet CircuitS);
    private static readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);

    /// <summary>The sentence an estimated result carries.</summary>
    public const string EstimatedSentence = "Estimated while the slider moves: the array's circuit is held at its last solve and the new weights applied at the " +
                                            "beamformer; it is solved when the slider is released.";

    /// <summary>R-paa8-4 — keeps a full solve of a Feed-mode array, so a later evaluation that moves only its weights can be estimated from it.</summary>
    public static void Remember(ElaboratedNetlist nl, string block, double[] freqs, IReadOnlyList<(int Port, Complex Amplitude)> drives,
                                SParameterEngine.BlockPortDrive[] waves, DataSet circuitS)
    {
        if (HeldKey(nl, block) is not { } key || nl.Components.First(c => c.InstancePath == block).Model is not PhasedArrayModel { Feed: { } feed } pa) return;
        var weights = freqs.Select(f => Enumerable.Range(0, pa.PortCount).Select(k => feed.Quantised(k, f)).ToArray()).ToArray();
        lock (_gate) _held[block] = new Held(key, (double[])freqs.Clone(), [.. drives], waves, weights, circuitS);
    }

    /// <summary>
    /// R-paa8-4 — the waves of the last full solve with the new weights applied at the beamformer: element k's waves times w_k,new/w_k,old, the
    /// circuit everywhere else held (its active reflections, the drive's reflection, the circuit's S). Null when nothing but the weights would not
    /// account for the change — another value moved, other frequencies or drives — or an element that carried nothing now carries a weight.
    /// </summary>
    public static Solved? Estimate(ElaboratedNetlist nl, string block, double[] freqs, IReadOnlyList<(int Port, Complex Amplitude)> drives)
    {
        Held? held;
        lock (_gate) held = _held.GetValueOrDefault(block);
        if (held is null || !held.Freqs.SequenceEqual(freqs) || !held.Drives.SequenceEqual(drives)) return null;
        if (nl.Components.First(c => c.InstancePath == block).Model is not PhasedArrayModel { Feed: { } feed } pa) return null;
        if (HeldKey(nl, block) != held.Key) return null;
        int n = pa.PortCount;
        var waves = new SParameterEngine.BlockPortDrive[freqs.Length];
        for (int f = 0; f < freqs.Length; f++)
        {
            var old = held.Waves[f];
            var v = new Complex[n];
            var i = new Complex[n];
            for (int k = 0; k < n; k++)
            {
                Complex was = held.Weights[f][k], now = feed.Quantised(k, freqs[f]);
                if (was == Complex.Zero && now != Complex.Zero) return null;
                Complex ratio = was == Complex.Zero ? Complex.Zero : now / was;
                v[k] = old.Voltage[k] * ratio;
                i[k] = old.Current[k] * ratio;
            }
            waves[f] = old with { Voltage = v, Current = i };
        }
        return new Solved(waves, held.CircuitS, new PhasedArraySolvePath(false, EstimatedSentence));
    }

    /// <summary>Everything in the circuit but the beamformer's weights: every component's type, path, nodes and resolved values, the array's
    /// PhaseBits, AttenStep and Off aside. Null when <paramref name="block"/> is not a Feed-mode array.</summary>
    private static string? HeldKey(ElaboratedNetlist nl, string block)
    {
        string feedPath = block + ".feed";
        var sb = new StringBuilder();
        bool found = false;
        foreach (var c in nl.Components)
        {
            if (c.InstancePath == feedPath) { found = true; continue; }
            bool isArray = c.InstancePath == block;
            sb.Append(c.ComponentType).Append(' ').Append(c.InstancePath).Append(" m=").Append(c.Multiplicity.ToString("R")).Append(" [");
            foreach (int k in c.Nodes) sb.Append(k).Append(',');
            sb.Append("] ");
            foreach (var (k, v) in c.Parameters.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (isArray && k is "PhaseBits" or "AttenStep" or "Off") continue;
                sb.Append(k).Append('=').Append(Text(v)).Append(';');
            }
            sb.Append('\n');
        }
        return found ? Hash(sb) : null;
    }

    private sealed class State
    {
        public string? ChannelKey, ArrayKey;
        public readonly Dictionary<double, Complex[]> ChannelS = [];
        public readonly Dictionary<double, Closure> Closures = [];
    }

    /// <summary>One frequency's closure: the array's S, and the factor of I − S22·S with every element's channel in place, plus the
    /// Woodbury pieces for the last set of elements switched off.</summary>
    private sealed class Closure(ArraySparseMatrix s, bool diagonal, SparseLU? lu, long factorNonZeros, long matrixNonZeros)
    {
        public ArraySparseMatrix S { get; } = s;
        public bool Diagonal { get; } = diagonal;
        public SparseLU? Lu { get; } = lu;
        public long FactorNonZeros { get; } = factorNonZeros;
        public long MatrixNonZeros { get; } = matrixNonZeros;
        public string? OffKey;
        public int[] Off = [];
        public Complex[][] Z = [];            // M0⁻¹·(S22·e_k) for each element switched off
        public Complex[,] C = new Complex[0, 0];   // I + Vᵀ·Z, factorised in place (LU with partial pivoting)
        public int[] Pivot = [];
        public SparseLU? OffLu;               // the closure factorised for the Off set itself, past WoodburyLimit
    }

    private static bool IsDiagonal(ArraySparseMatrix s)
    {
        for (int i = 0; i < s.Size; i++)
            foreach (var (c, v) in s.Row(i))
                if (c != i && v != Complex.Zero) return false;
        return true;
    }

    /// <summary>LU of I − s22·S, the rows of <paramref name="identityRows"/> replaced by the identity's.</summary>
    private static (SparseLU Lu, long NonZeros) Factorise(ArraySparseMatrix s, Complex s22, HashSet<int>? identityRows)
    {
        int n = s.Size;
        var cols = new List<(int Row, Complex Value)>[n];
        for (int c = 0; c < n; c++) cols[c] = [];
        for (int i = 0; i < n; i++)
        {
            if (identityRows is not null && identityRows.Contains(i)) { cols[i].Add((i, Complex.One)); continue; }
            bool diag = false;
            foreach (var (c, v) in s.Row(i))
            {
                Complex m = -s22 * v;
                if (c == i) { m += 1; diag = true; }
                cols[c].Add((i, m));
            }
            if (!diag) cols[i].Add((i, Complex.One));
        }
        var colPtr = new int[n + 1];
        for (int c = 0; c < n; c++) colPtr[c + 1] = colPtr[c] + cols[c].Count;
        var rowIdx = new int[colPtr[n]];
        var values = new Complex[colPtr[n]];
        for (int c = 0; c < n; c++)
        {
            cols[c].Sort((x, y) => x.Row.CompareTo(y.Row));
            for (int k = 0; k < cols[c].Count; k++) { rowIdx[colPtr[c] + k] = cols[c][k].Row; values[colPtr[c] + k] = cols[c][k].Value; }
        }
        var m0 = new SparseMatrix(n, n, values, rowIdx, colPtr);
        var lu = SparseLU.Create(m0, ClosureOrdering, 1.0);
        return (lu, lu.NonZerosCount);
    }

    /// <summary>
    /// α = M⁻¹·r, M the closure with the elements switched off closed by the identity: M = M0 + U·Vᵀ, U = S22·E and Vᵀ = Eᵀ·S over the
    /// elements switched off E, so α = y − Z·C⁻¹·(Vᵀ·y) with y = M0⁻¹·r, Z = M0⁻¹·U and C = I + Vᵀ·Z — Z and C kept per Off set.
    /// </summary>
    private static void SolveClosure(Closure c, ArrayLattice lattice, Complex s22, Complex[] r, Complex[] a)
    {
        int n = r.Length;
        var off = lattice.Off.Select(k => k - 1).OrderBy(k => k).ToArray();
        if (off.Length == 0)
        {
            c.Lu!.Solve(r, a);
            Interlocked.Increment(ref _backSubstitutions);
            return;
        }
        string key = string.Join(",", off);
        if (off.Length > WoodburyLimit)
        {
            if (c.OffKey != key || c.OffLu is null)
            {
                c.OffLu = Factorise(c.S, s22, [.. off]).Lu;
                Interlocked.Increment(ref _factorisations);
                c.OffKey = key;
            }
            c.OffLu.Solve(r, a);
            Interlocked.Increment(ref _backSubstitutions);
            return;
        }
        int m = off.Length;
        if (c.OffKey != key)
        {
            var z = new Complex[m][];
            for (int j = 0; j < m; j++)
            {
                var u = new Complex[n];
                u[off[j]] = s22;
                z[j] = new Complex[n];
                c.Lu!.Solve(u, z[j]);
                Interlocked.Increment(ref _backSubstitutions);
            }
            var cm = new Complex[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                {
                    Complex sum = i == j ? Complex.One : Complex.Zero;
                    foreach (var (col, v) in c.S.Row(off[i])) sum += v * z[j][col];
                    cm[i, j] = sum;
                }
            c.Pivot = DenseLu(cm);
            c.Z = z;
            c.C = cm;
            c.Off = off;
            c.OffKey = key;
        }
        var y = new Complex[n];
        c.Lu!.Solve(r, y);
        Interlocked.Increment(ref _backSubstitutions);
        var q = new Complex[m];
        for (int i = 0; i < m; i++)
            foreach (var (col, v) in c.S.Row(c.Off[i])) q[i] += v * y[col];
        DenseSolve(c.C, c.Pivot, q);
        Array.Copy(y, a, n);
        for (int j = 0; j < m; j++)
            if (q[j] != Complex.Zero)
                for (int k = 0; k < n; k++) a[k] -= c.Z[j][k] * q[j];
    }

    private static int[] DenseLu(Complex[,] a)
    {
        int m = a.GetLength(0);
        var piv = new int[m];
        for (int k = 0; k < m; k++)
        {
            int p = k;
            for (int i = k + 1; i < m; i++) if (a[i, k].Magnitude > a[p, k].Magnitude) p = i;
            piv[k] = p;
            if (p != k) for (int j = 0; j < m; j++) (a[k, j], a[p, j]) = (a[p, j], a[k, j]);
            if (a[k, k] == Complex.Zero) throw new InvalidOperationException("The array's closure with its elements switched off is singular.");
            for (int i = k + 1; i < m; i++)
            {
                a[i, k] /= a[k, k];
                for (int j = k + 1; j < m; j++) a[i, j] -= a[i, k] * a[k, j];
            }
        }
        return piv;
    }

    private static void DenseSolve(Complex[,] lu, int[] piv, Complex[] b)
    {
        int m = b.Length;
        for (int k = 0; k < m; k++)
        {
            if (piv[k] != k) (b[k], b[piv[k]]) = (b[piv[k]], b[k]);
            for (int i = k + 1; i < m; i++) b[i] -= lu[i, k] * b[k];
        }
        for (int k = m - 1; k >= 0; k--)
        {
            for (int j = k + 1; j < m; j++) b[k] -= lu[k, j] * b[j];
            b[k] /= lu[k, k];
        }
    }

    // ── the split ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record ChannelPart(string Prefix, List<ElaboratedComponent> Components, int In, int Out, string Key);

    private sealed record Parts(PhasedArrayModel Array, ElaboratedComponent Feed, ElaboratedComponent Block, ChannelPart? Channel,
                                List<ElaboratedComponent> Outer, string ArrayKey, PhasedArraySolvePath Path);

    /// <summary>The array's parts, the circuit outside them, and the decision; null when <paramref name="block"/> is not a Feed-mode array.</summary>
    private static Parts? Split(ElaboratedNetlist nl, string block)
    {
        var blockC = nl.Components.FirstOrDefault(c => string.Equals(c.InstancePath, block, StringComparison.Ordinal));
        if (blockC?.Model is not PhasedArrayModel pa) return null;
        int n = pa.PortCount;
        string Full(string why) => $"Full solve: {why}; the whole circuit ({n} element{(n == 1 ? "" : "s")}, {nl.Nodes.Count - 1} nodes) is solved as elaborated at every change.";
        if (pa.Feed is null)
            return new Parts(pa, blockC, blockC, null, [], "", new(false, Full("the array is in Elements mode, so the circuit wired to its elements is what is solved")));

        string feedPath = block + ".feed", chPrefix = block + ".ch[", rxPrefix = block + ".rx[";
        var feedC = nl.Components.First(c => c.InstancePath == feedPath);
        var outer = new List<ElaboratedComponent>();
        var channelComps = new List<ElaboratedComponent>();
        foreach (var c in nl.Components)
        {
            if (ReferenceEquals(c, feedC) || ReferenceEquals(c, blockC) || c.InstancePath.StartsWith(rxPrefix, StringComparison.Ordinal)) continue;
            if (c.InstancePath.StartsWith(chPrefix, StringComparison.Ordinal)) channelComps.Add(c);
            else outer.Add(c);
        }
        string arrayKey = ArrayKey(blockC, pa);
        Parts Refuse(string why) => new(pa, feedC, blockC, null, outer, arrayKey, new(false, Full(why)));

        if (pa.Z0.Imaginary != 0 || !(pa.Z0.Real > 0))
            return Refuse($"the array's Z0 is {pa.Z0.Real:G6}{(pa.Z0.Imaginary >= 0 ? "+" : "−")}j{Math.Abs(pa.Z0.Imaginary):G6} Ω, and waves join across a reference only when it is real");
        if (nl.Components.FirstOrDefault(c => c.IsNonlinear) is { } nonlinear)
            return Refuse($"'{nonlinear.InstancePath}' is nonlinear, and the S-parameter run linearises it about an operating point the whole circuit sets");
        var channels = pa.Channels;
        if (channels is { Identical: false })
            return Refuse($"channel cell '{channels.Cell}' differs per element: it reads {string.Join(", ", channels.Reads)}" +
                          (channels.Sites.Count > 0 ? $", in {string.Join("; ", channels.Sites)}" : ""));

        // the nodes the array's own parts hold, F aside
        int fNode = feedC.Nodes[0];
        var inside = new HashSet<int>();
        foreach (var c in channelComps) foreach (int k in c.Nodes) inside.Add(k);
        foreach (int k in feedC.Nodes) inside.Add(k);
        foreach (int k in blockC.Nodes) inside.Add(k);
        inside.Remove(fNode);
        inside.Remove(0);
        foreach (var c in outer)
            foreach (int k in c.Nodes.Append(c.ReferenceNode))
                if (inside.Contains(k))
                    return Refuse($"'{c.InstancePath}' is wired to '{nl.Nodes.NameOf(k)}', inside the array");

        ChannelPart? channel = null;
        if (channels is not null && channelComps.Count > 0)
        {
            int first = Enumerable.Range(1, n).First(k => !channels.Omitted.Contains(k));
            string prefix = $"{block}.ch[{first}].";
            var mine = channelComps.Where(c => c.InstancePath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (!nl.Nodes.TryGetIndex(prefix + channels.In, out int inNode) || !nl.Nodes.TryGetIndex(prefix + channels.Out, out int outNode))
                return Refuse($"channel cell '{channels.Cell}' at element {first} has no node for its ports {channels.In} and {channels.Out}");
            // every node of this channel but its two ports is its own: nothing outside it touches one
            var own = new HashSet<int>();
            foreach (var c in mine) foreach (int k in c.Nodes.Append(c.ReferenceNode)) if (k != 0) own.Add(k);
            foreach (var c in nl.Components)
            {
                if (mine.Contains(c)) continue;
                foreach (int k in c.Nodes.Append(c.ReferenceNode))
                {
                    if (k == 0 || !own.Contains(k)) continue;
                    if (k == inNode && ReferenceEquals(c, feedC)) continue;
                    if (k == outNode && ReferenceEquals(c, blockC)) continue;
                    return Refuse($"channel cell '{channels.Cell}' reaches net '{nl.Nodes.NameOf(k)}', which '{c.InstancePath}' outside it shares");
                }
            }
            channel = new ChannelPart(prefix, mine, inNode, outNode, ChannelKey(nl, prefix, mine));
        }

        string how = channel is null
            ? "the beamformer is ideal and isolating, so each element's incident wave is its weight times the wave into F and no wave reaches another path; " +
              "the array closes with no solve, and a change of weights costs the pattern sum"
            : $"channel cell '{channels!.Cell}' is identical across elements, so it is solved once per frequency as a two-port and the array closes as " +
              "a = (I − Γ_ch·S)⁻¹·t·w" + (pa.Coupled
                  ? $", one sparse factorisation of the coupling per frequency kept while only the weights (Amp, Phase, Delay, PhaseBits, AttenStep, Off) change, and a back-substitution per change"
                  : ", which is diagonal with Coupling=off: a change of weights costs the pattern sum");
        return new Parts(pa, feedC, blockC, channel, outer, arrayKey,
                         new(true, $"Fast path: {how}. The circuit outside the array is solved with the closed array at F."));
    }

    /// <summary>R-paa8-3 — the array's resolved parameters but the ones that only move the weights, and the record's file.</summary>
    private static string ArrayKey(ElaboratedComponent c, PhasedArrayModel pa)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in c.Parameters.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (k is "PhaseBits" or "AttenStep" or "Off") continue;
            sb.Append(k).Append('=').Append(Text(v)).Append(';');
        }
        var info = new FileInfo(pa.RecordPath);
        sb.Append(info.FullName).Append('@').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        return Hash(sb);
    }

    /// <summary>R-paa8-3 — one channel's components: types, paths, nodes and resolved values, the channel's own prefix taken off.</summary>
    private static string ChannelKey(ElaboratedNetlist nl, string prefix, List<ElaboratedComponent> comps)
    {
        string Strip(string s) => s.Replace(prefix, "", StringComparison.Ordinal);
        string Node(int k) => k == 0 ? "0" : Strip(nl.Nodes.NameOf(k));
        var sb = new StringBuilder();
        foreach (var c in comps)
        {
            sb.Append(c.ComponentType).Append(' ').Append(Strip(c.InstancePath)).Append(" m=").Append(c.Multiplicity.ToString("R"));
            sb.Append(" ref=").Append(Node(c.ReferenceNode)).Append(" [");
            foreach (int k in c.Nodes) sb.Append(Node(k)).Append(',');
            sb.Append("] ");
            foreach (var (k, v) in c.Parameters.OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.Append(k).Append('=').Append(Strip(Text(v))).Append(';');
            sb.Append('\n');
        }
        return Hash(sb);
    }

    private static string Text(Value v) => v.Kind switch
    {
        ValueKind.Real => "r" + v.AsReal().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        ValueKind.Complex => "c" + v.AsComplex().Real.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
                             v.AsComplex().Imaginary.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => "s" + v,
    };

    private static string Hash(StringBuilder sb) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));

    // ── the two small circuits ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A netlist of <paramref name="comps"/> alone, its nodes renamed by <paramref name="rename"/> and numbered afresh.</summary>
    private static (ElaboratedNetlist Nl, Func<int, int> Map) Sub(ElaboratedNetlist nl, IEnumerable<ElaboratedComponent> comps, Func<string, string> rename)
    {
        var sub = new ElaboratedNetlist();
        var map = new Dictionary<int, int> { [0] = 0 };
        int Map(int k)
        {
            if (!map.TryGetValue(k, out int m)) map[k] = m = sub.Nodes.GetOrAssign(rename(nl.Nodes.NameOf(k)));
            return m;
        }
        foreach (var c in comps)
            sub.AddComponent(new ElaboratedComponent(c.ComponentType, c.InstancePath, [.. c.Nodes.Select(Map)], c.Parameters, c.Model)
            {
                ReferenceNode = Map(c.ReferenceNode), Multiplicity = c.Multiplicity, NoiseOff = c.NoiseOff, NoiseOn = c.NoiseOn,
            });
        return (sub, Map);
    }

    private static ElaboratedComponent PortOn(int node, int number, double z0)
        => new("Port", $"__paa_port{number}", [node, 0],
               new Dictionary<string, Value>(StringComparer.Ordinal) { ["Num"] = new Value(number), ["Z"] = new Value(z0) }, new PortModel());

    /// <summary>The channel's two-port at each frequency, [S11, S12, S21, S22], in the array's Z0: port 1 its input from the beamformer.</summary>
    private static Complex[][] SolveChannel(ElaboratedNetlist nl, ChannelPart ch, double[] freqs, double z0)
    {
        var (sub, map) = Sub(nl, ch.Components, name => name.StartsWith(ch.Prefix, StringComparison.Ordinal) ? name[ch.Prefix.Length..] : name);
        sub.AddComponent(PortOn(map(ch.In), 1, z0));
        sub.AddComponent(PortOn(map(ch.Out), 2, z0));
        var s = SParameterEngine.Run(sub, freqs).Cubes["S"].ComplexValues;
        foreach (string warning in sub.Warnings) nl.AddWarningOnce($"paa-channel:{warning}", warning);
        var result = new Complex[freqs.Length][];
        for (int f = 0; f < freqs.Length; f++)
            result[f] = [s[f * 4], s[f * 4 + 1], s[f * 4 + 2], s[f * 4 + 3]];
        return result;
    }

    /// <summary>The circuit outside the array with the closed array standing at F.</summary>
    private static (ElaboratedNetlist Nl, string LoadPath) Outer(ElaboratedNetlist nl, Parts parts, Dictionary<double, Complex> gammaF, double z0)
    {
        // brief-pch-9 — a Propagation Channel that TRANSMITS from this array reads its element waves (one way: it loads nothing back on
        // them), and those waves are not unknowns of the circuit outside the array: it is left out of that solve, which it cannot change.
        // Before, it stamped its rows against the array's element branches there, which that system never allocated.
        var (sub, map) = Sub(nl, [.. parts.Outer.Where(c => !(c.Model is PropagationChannelModel ch && ReferenceEquals(ch.TxEnd, parts.Block)))], name => name);
        const string path = "__paa_feed_load";
        sub.AddComponent(new ElaboratedComponent("PhasedArrayFeedLoad", path, [map(parts.Feed.Nodes[0]), 0],
                                                 new Dictionary<string, Value>(StringComparer.Ordinal), new ArrayFeedLoadModel(gammaF, z0)));
        return (sub, path);
    }
}
