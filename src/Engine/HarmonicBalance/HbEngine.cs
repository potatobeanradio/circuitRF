using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using RfCore.Data;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Resolved HB analysis parameters (expression strings evaluated against ResolvedGlobals).
///
/// Two modes (harmonic-balance.md §3.2):
///   Single-tone:  ToneFreqsHz.Length == 1.  MaxMixOrder is ignored.
///   Multi-tone:   ToneFreqsHz.Length >= 2.  MaxHarmonic provides per-axis reach; MaxMixOrder
///                 bounds the diamond |k₁|+|k₂| ≤ MaxMixOrder.
/// </summary>
public sealed record HbAnalysisParams(
    /// <summary>
    /// Tone frequencies in Hz.  Length 1 = single-tone; length 2+ = multi-tone.
    /// Use ToneHz as a convenience getter for the single-tone case.
    /// </summary>
    double[] ToneFreqsHz,
    int      MaxHarmonic,
    /// <summary>Diamond mixing-order bound for two-tone. Ignored when single-tone.</summary>
    int      MaxMixOrder,
    int      FFTOverSample,
    double   Tol,
    DcBiasSteppingMode DriveStepping,
    /// <summary>Guard harmonic index H.  0 = off.  See B3 in CLAUDE.md.</summary>
    int      GuardHarmonic,
    string?  SweepVarName,
    double   SweepStart,
    double   SweepStop,
    double   SweepStep,
    int      MaxIter = 100,
    /// <summary>
    /// Newton step damping factor λ ∈ (0,1].
    /// Default 1.0 = full Newton step (no damping).
    /// Owner can supply &lt;1 via the cnl Lambda= key or by constructing with Lambda: x.
    /// B2.
    /// </summary>
    double   Lambda  = 1.0,
    /// <summary>
    /// The small-signal (probe-tickle) frequencies in Hz — the <c>ssfreq</c> axis of a probed run
    /// (WSP-5 R-wsp5-1). <b>Empty means no small-signal solve at all</b>, and an HB run with an
    /// empty grid is byte-identical to one from before WSP-5 (R-wsp5-9(a)).
    /// </summary>
    double[]? SsFreqsHz = null,
    /// <summary>The sideband order K_ss of the conversion matrix. Null ⇒ MaxHarmonic; clamped to
    /// it, since a sideband above the retained spectrum has no operating point to sit on.</summary>
    int?     SsMaxHarm = null,
    /// <summary>The WSProbe stability-margin report threshold in dB, or null for
    /// <c>MarginThreshold=none</c> (WSP-9 R-wsp9-3, applied per operating point).</summary>
    double?  MarginThresholdDb = -15.0)
{
    /// <summary>Convenience: fundamental frequency for single-tone runs.</summary>
    public double ToneHz      => ToneFreqsHz[0];

    /// <summary>The small-signal grid, never null.</summary>
    public double[] SsGrid    => SsFreqsHz ?? [];

    /// <summary>True when the directive asked for a small-signal sweep.</summary>
    public bool HasSsSweep    => SsGrid.Length > 0;

    /// <summary>K_ss as the solve uses it: the requested order clamped to the retained K.</summary>
    public int SsOrder        => Math.Clamp(SsMaxHarm ?? MaxHarmonic, 0, MaxHarmonic);
    /// <summary>True when two or more independent tones are declared.</summary>
    public bool   IsMultiTone => ToneFreqsHz.Length > 1;
    public bool   HasSweep    => SweepVarName is not null;

    /// <summary>
    /// Enumerate the sweep values.
    /// Returns the actual sweep points when HasSweep=true; empty when HasSweep=false.
    /// The engine's Run() always executes at least one Newton solve — no-sweep is handled
    /// by a separate single-iteration path, not by yielding a dummy value.
    /// </summary>
    public IEnumerable<double> SweepValues()
    {
        if (!HasSweep) yield break;
        for (double v = SweepStart; v <= SweepStop + 1e-10; v += SweepStep)
            yield return v;
    }
}

/// <summary>
/// HB engine result: the solved V and I_nl [node, harmonic] per sweep point.
/// </summary>
public sealed class HbResult
{
    /// <summary>Swept variable values (one entry per sweep point).</summary>
    public double[] SweepValues { get; }
    /// <summary>Converged interface voltages: [sweepIdx][nodeIdx, harmonicK].</summary>
    public Complex[][,] V { get; }
    /// <summary>
    /// Nonlinear device currents at the interface: [sweepIdx][nodeIdx, harmonicK].
    /// I_nl[n,0] = DC device current — changes with Pin (self-biasing; Task-1 fix).
    /// I_nl[n,k] = k-th harmonic of the device current at interface node n.
    /// I_nl current-direction convention —  current flowing FROM interface node n INTO the nonlinear device.
    /// Positive = current entering the nonlinear device (passive sign convention on the device ports).
    /// </summary>
    public Complex[][,] INl { get; }
    /// <summary>Interface node indices (circuit node numbers, 1-based).</summary>
    public int[] InterfaceNodes { get; }
    /// <summary>Node names at the interface (e.g. "n_gate", "n_drain").</summary>
    public string[] InterfaceNodeNames { get; }
    /// <summary>Per-sweep-point convergence trace.</summary>
    public HbConvergenceTrace Trace { get; }

    /// <summary>
    /// Two-tone only: the mixing lattice. When non-null, the second index of <see cref="V"/> /
    /// <see cref="INl"/> is the <c>mixIndex</c> (0..M-1) per <see cref="MixingGrid"/>, NOT the
    /// scalar harmonic k. Null for single-tone runs (index is harmonic k=0..K).
    /// </summary>
    public MixingGrid? Grid { get; }

    /// <summary>Tone frequencies (Hz). Length 1 single-tone; length 2+ multi-tone.</summary>
    public double[] ToneFreqsHz { get; }

    /// <summary>True when the result is on the 2-D mixing-lattice axis.</summary>
    public bool IsMultiTone => Grid is not null;

    internal HbResult(double[] sweepValues, Complex[][,] V, Complex[][,] iNl,
        int[] interfaceNodes, string[] names, HbConvergenceTrace trace,
        MixingGrid? grid = null, double[]? toneFreqsHz = null)
    {
        SweepValues       = sweepValues;
        this.V            = V;
        INl               = iNl;
        InterfaceNodes    = interfaceNodes;
        InterfaceNodeNames = names;
        Trace             = trace;
        Grid              = grid;
        ToneFreqsHz       = toneFreqsHz ?? [];
    }
}

/// <summary>
/// Single-tone harmonic-balance engine — Phase 4a (harmonic-balance.md §13).
///
/// Usage:
///   var engine = new HbEngine(netlist, testBench, settings);
///   var result = engine.Run(analysisParams);
///
/// See CLAUDE.md for FFT convention, sign convention, and DC-exclusion rationale.
/// </summary>
public sealed class HbEngine
{
    private readonly ElaboratedNetlist  _netlist;
    private readonly TestBench          _tb;
    private readonly AnalysisSettings   _settings;

    /// <param name="wspCache">
    /// The WSProbe small-signal sweep's operating-point-independent cache (brief-wsprobe-8 R-wsp8-4).
    /// Supplied by whoever owns the DRIVE SWEEP, because that is the lifetime the cache pays for
    /// itself over — <c>ParametricSweepEngine</c> re-elaborates the netlist and builds a fresh
    /// <see cref="HbEngine"/> at every point, so a cache created here would be discarded exactly
    /// when it was about to be reused. Null gives every point its own cache, which is correct and
    /// costs what WSP-5 cost.
    /// </param>
    /// <param name="lib">The library the netlist was elaborated from, and the base directory that
    /// elaboration used — needed ONLY to elaborate the per-worker copies of a parallel small-signal
    /// sweep (R-wsp8-9). Null pins the sweep to the serial path.</param>
    public HbEngine(ElaboratedNetlist netlist, TestBench tb, AnalysisSettings? settings = null,
                    HbSmallSignalCache? wspCache = null,
                    Library? lib = null, string? baseDirectory = null)
    {
        _netlist  = netlist;
        _tb       = tb;
        _settings = settings ?? AnalysisSettings.Default;
        _wspCache = wspCache;
        _lib      = lib;
        _baseDir  = baseDirectory;
        RefuseNonlinearBranchEquations(netlist);
    }

    private readonly HbSmallSignalCache? _wspCache;
    private readonly Library?            _lib;
    private readonly string?             _baseDir;

    /// <summary>
    /// Refuses, up front and by name, a device that CONSTRAINS a port voltage nonlinearly.
    ///
    /// <para><b>Why this cannot simply work.</b> The harmonic-balance unknowns are the voltage
    /// phasors at the nonlinear-facing nodes — that is the formulation, not an implementation
    /// choice: the linear subnetwork is characterised in the frequency domain and reduced onto those
    /// nodes, and the residual is KCL there. A device whose relation is <i>between</i> node voltages
    /// carries a branch-current unknown that is neither a node voltage nor reducible into the linear
    /// network, because its own row is nonlinear. Carrying it would mean bordering the Newton system
    /// with one extra unknown per constraint per harmonic and threading that through the extractor,
    /// the Jacobian, the back-solver and the warm start.</para>
    ///
    /// <para><b>And it must be a refusal rather than an omission.</b> Such a device evaluates to
    /// zero port current, so an HB run that ignored the constraint would converge, quickly, on a
    /// circuit in which the source is simply absent — a plausible answer with nothing wrong on the
    /// face of it. DC and S-parameter analysis both carry these devices exactly.</para>
    ///
    /// <para><b>The charge idiom should not reach here, and where it does that is the bug.</b> A
    /// behavioural source driving a linear capacitor states a nonlinear CHARGE, and
    /// <c>SpiceChargePairCollapse</c> rewrites that pair, at import, into the device's own charge
    /// equation — which HB has carried since it was written. That rewrite is exact and
    /// unconditional, but it only fires when the pattern holds exactly (nothing else on the interior
    /// node, and the expression not sensing the pair it constrains), so a charge written some other
    /// way still lands on this refusal. It is worth checking the pattern before concluding that
    /// harmonic balance is what is missing.</para>
    ///
    /// <para><b>What is genuinely absent</b> is a voltage constraint that is not a charge — a logic
    /// or behavioural block written as one. Carrying those means bordering the Newton system with
    /// one extra unknown per constraint per harmonic, through the extractor, the Jacobian, the
    /// back-solver, the warm start and both the 2-D and N-D variants; it was not built.</para>
    /// </summary>
    private static void RefuseNonlinearBranchEquations(ElaboratedNetlist netlist)
    {
        var offenders = netlist.Components
            .Where(ec => ec.Model.Kind == CircuitRF.Core.ModelKind.Nonlinear && ec.Model.BranchEquationCount > 0)
            .Select(ec => ec.InstancePath)
            .ToList();
        if (offenders.Count == 0) return;

        throw new InvalidOperationException(
            $"Harmonic balance cannot run this circuit: {string.Join(", ", offenders)} "
            + (offenders.Count == 1 ? "holds" : "hold")
            + " a nonlinear voltage constraint (an SDD 'V[p]' equation, which is what a behavioural "
            + "voltage source becomes). Harmonic balance solves for the voltage phasors at the "
            + "nonlinear-facing nodes, and such a device adds a branch-current unknown that is not "
            + "one of them. DC and S-parameter analysis both carry it exactly, so an operating point "
            + "and a small-signal linearisation about it are available; a large-signal run is not. "
            + "A source of this kind that exists to carry a nonlinear CHARGE — one driving a linear "
            + "capacitor and nothing else — has a form harmonic balance does solve: state it as the "
            + "capacitor's stored charge (an SDD 'I[p,1]' equation, which an import writes for you "
            + "when the source drives one capacitor and nothing else touches the node between "
            + "them).");
    }

    // ── The linear extractor outlives one solve (HB-P2) ───────────────────────
    //
    // The extractor holds one LU per harmonic. Rebuilding it per solve threw those away and
    // refactorized every harmonic on every call — ~55% of a warm RunSinglePoint and ~70% of its
    // allocation, although the only thing that changes between the Pin steps of one loadpull Γ
    // point is the drive, which touches the right-hand side and not the matrix. So the engine keeps
    // one, and a solve re-stamps and reuses it.
    //
    // Reuse is NOT taken on trust: the extractor compares the matrix it just stamped, bit for bit,
    // against the one each cached factorization was built from, and rebuilds on any difference. A
    // loadpull tuner's per-grid-point impedance override is therefore picked up with no cooperation
    // from the loadpull engine at all — which is the point, since a "remember to invalidate"
    // protocol fails silently the one time a caller forgets.
    //
    // The extractor is per (netlist, extractor-relevant settings). The netlist is fixed for the
    // engine's life; the settings are not — RunSinglePoint takes a settingsOverride, and the
    // loadpull engine uses it to force InductanceRegularization=Always. So the cached extractor is
    // kept only while the fields it actually reads are unchanged, compared by VALUE rather than by
    // reference: RunSinglePoint mints a fresh AnalysisSettings whenever the directive's MaxIter
    // differs from the settings default, and reference equality would never hold across those.
    private HbLinearExtractor? _extractor;
    private (double Gmin, RegularizationMode Cond, RegularizationMode Ind, double IndR, bool Diag)
        _extractorKey;

    private HbLinearExtractor GetExtractor(AnalysisSettings settings)
    {
        var key = (settings.Gmin, settings.ConductanceRegularization,
                   settings.InductanceRegularization, settings.InductanceRegR,
                   settings.HbConsoleDiagnostics);

        if (_extractor is not null && _extractorKey == key) return _extractor;

        _extractorKey = key;
        return _extractor = new HbLinearExtractor(_netlist, settings);
    }

    /// <summary>
    /// How many times this engine's linear extractor has LU-factorized a harmonic's MNA. Test-facing:
    /// the property HB-P2 asserts is one factorization per harmonic per topology, not one per solve.
    /// </summary>
    public int LinearFactorizations => _extractor?.Factorizations ?? 0;

    /// <summary>
    /// Drop the cached linear factorizations. Never required for correctness — a matrix that no
    /// longer matches is detected on the next stamp — but available to a caller that wants the
    /// memory back, or a test that wants the cold path.
    /// </summary>
    public void InvalidateLinear() => _extractor?.InvalidateLinear();

    /// <summary>Drop one harmonic's cached factorization. Same status as the parameterless overload.</summary>
    public void InvalidateLinear(double omega) => _extractor?.InvalidateLinear(omega);

    // ── Directive resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Resolve a HarmonicBalanceAnalysis directive against the elaborated globals.
    /// Pass <paramref name="globalsWithUnit"/> (from <c>ElaboratedNetlist.GlobalsWithExplicitUnit</c>)
    /// to enable the var-unit-wins rule for tone frequencies.
    /// </summary>
    public static HbAnalysisParams Resolve(
        HarmonicBalanceAnalysis hba,
        IReadOnlyDictionary<string, Value> globals,
        IReadOnlyCollection<string>? globalsWithUnit = null)
    {
        double Num(string expr, double def)
        {
            try
            {
                var scope = BuildScope(globals);
                var ev    = BuildEvaluator(globals);
                var ast   = Parser.Parse(expr);
                var val   = ev.EvalExpr(ast, scope);
                return val.Kind == ValueKind.Real ? val.AsReal() : val.AsComplex().Real;
            }
            catch { return def; }
        }

        // An unresolvable Tone is REFUSED, not defaulted. Substituting 1 GHz here produced a run
        // whose every later message described a tone grid the design never asked for — most
        // visibly the commensurability check, which then blamed the source for sitting off it.
        double ToneHz(string what, string expr, string unit)
        {
            try { return FreqUnit.ResolveHz(expr, unit, globals, globalsWithUnit); }
            catch (ExpressionException ex)
            {
                throw new InvalidOperationException(
                    "HB " + FreqUnit.UnresolvedFieldMessage(what, hba.Name, expr, ex), ex);
            }
        }

        int    maxH    = (int)Num(hba.MaxHarmonicExpr, 7);
        int    osamp   = Math.Max(1, (int)Num(hba.FFTOverSampleExpr, 1));
        double tol     = Num(hba.TolExpr, 1e-6);
        int    guard   = (int)Num(hba.GuardHarmonicExpr, 0);
        double lambda  = Math.Clamp(Num(hba.LambdaExpr, 1.0), 1e-4, 1.0);  // B2
        int    maxIter = Math.Max(1, (int)Num(hba.MaxIterExpr, 100));

        // ── Tone frequencies: single-tone (scalar Tone=) or multi-tone (Tone[1..N]) ──
        int numFreqs = Math.Max(1, (int)Num(hba.NumFreqsExpr, 1));
        double[] toneFreqsHz;
        if (numFreqs > 1 && hba.ToneExprs.Length >= numFreqs)
        {
            toneFreqsHz = new double[numFreqs];
            for (int i = 0; i < numFreqs; i++)
            {
                string unit = i < hba.ToneUnits.Length ? hba.ToneUnits[i] : "Hz";
                toneFreqsHz[i] = ToneHz($"Tone[{i + 1}]", hba.ToneExprs[i], unit);
            }
        }
        else
        {
            // Single-tone: use scalar Tone= / ToneUnit=.
            toneFreqsHz = [ToneHz("Tone", hba.ToneExpr, hba.ToneUnit)];
        }

        int maxMixOrder = Math.Max(1, (int)Num(hba.MaxMixOrderExpr, 5));

        var driveStepping = DcBiasSteppingMode.IfNecessary;
        var dsStr = hba.DriveSteppingExpr.Trim();
        if (dsStr.Equals("Always", StringComparison.OrdinalIgnoreCase))
            driveStepping = DcBiasSteppingMode.Always;
        else if (dsStr.Equals("Never", StringComparison.OrdinalIgnoreCase))
            driveStepping = DcBiasSteppingMode.Never;

        // Read deprecated sweep fields for back-compat (HbAnalysisParams carries them so
        // callers can inspect p.HasSweep; the engine ignores them in Run()).
#pragma warning disable CS0618
        double sweepStart = 0, sweepStop = 0, sweepStep = 1;
        string? sweepVar = hba.SweepVarName;
        if (sweepVar is not null)
        {
            sweepStart = Num(hba.SweepStartExpr ?? "0", 0);
            sweepStop  = Num(hba.SweepStopExpr  ?? "0", 0);
            sweepStep  = Num(hba.SweepStepExpr  ?? "1", 1);
        }
#pragma warning restore CS0618

        // ── The small-signal (probe-tickle) sweep — WSP-5 R-wsp5-1 ───────────
        // Expanded through the ordinary FrequencySpec, so SSUnit honours the var-unit-wins rule and
        // SSLog reaches the same log grid the S-parameter sweep does. An UNRESOLVABLE sweep is
        // refused rather than dropped: a run that silently carried no wsp would look exactly like a
        // run whose directive had no SS* keys, and the design's own question would go unanswered.
        double[]? ssGrid = null;
        if (hba.SsSweep() is { } ssSpec)
        {
            try { ssGrid = ssSpec.Expand(globals, globalsWithUnit); }
            catch (ExpressionException ex)
            {
                throw new InvalidOperationException(
                    $"HB '{hba.Name}': the small-signal sweep (SSStart/SSStop) could not be " +
                    $"resolved — {ex.Message}", ex);
            }
            if (ssGrid.Length == 0) ssGrid = null;
        }
        int? ssMaxHarm = hba.SsMaxHarmExpr.Trim().Length > 0
            ? Math.Max(0, (int)Num(hba.SsMaxHarmExpr, maxH))
            : null;

        return new HbAnalysisParams(toneFreqsHz, maxH, maxMixOrder, osamp, tol, driveStepping, guard,
            sweepVar, sweepStart, sweepStop, sweepStep,
            MaxIter: maxIter, Lambda: lambda,
            SsFreqsHz: ssGrid, SsMaxHarm: ssMaxHarm,
            MarginThresholdDb: Analysis.ParseMarginThresholdDb(hba.MarginThresholdExpr));
    }

    private static Scope BuildScope(IReadOnlyDictionary<string, Value> globals)
    {
        var scope = new Scope("hb-resolve");
        foreach (var kv in globals)
            scope.Bind(kv.Key, kv.Value.ToString()!);
        return scope;
    }

    private static Evaluator BuildEvaluator(IReadOnlyDictionary<string, Value> globals)
    {
        var ev = new Evaluator();
        foreach (var kv in globals)
            ev.InjectResolved("hb-resolve", kv.Key, kv.Value);
        return ev;
    }

    // ── Engine entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Run the HB analysis.  Returns an <see cref="HbRunResult"/> that wraps the DataSet
    /// and, for single-tone runs, a lazy <see cref="HbLinearBackSolver"/> for recovering
    /// linear-interior node voltages (C1).  Implicit conversion to DataSet is available.
    /// </summary>
    public HbRunResult Run(HbAnalysisParams p, Complex[,]? warmStart = null)
    {
        // TWO OR MORE tones go to the APFT lattice path. Two tones used to be routed to the
        // rectangular-FFT path instead; AnalysisSettings.HbTwoToneOnLattice (default TRUE since
        // 2026-08-30) is the switch back, and that setting carries the measurements the default
        // was chosen on. RunMultiTone is tone-count-general and MixingLattice at T = 2 reproduces
        // MixingGrid's enumeration exactly, so the DataSet has the same shape and the same
        // mixIndex labels either way — only the numbers differ, and only in their last digits.
        if (p.ToneFreqsHz.Length == 2 && !_settings.HbTwoToneOnLattice)
        {
            var tt = RunTwoTone(p, warmStart);
            return new HbRunResult(tt.Ds, converged: tt.Converged, interfaceV: tt.InterfaceV,
                trace: tt.Trace);
        }
        // >= 2, not >= 3: two tones must land HERE unless HbTwoToneOnLattice was cleared above.
        // Gating this on >= 3 instead lets a two-tone run fall through to the single-tone path
        // below, which converges cleanly and returns a plausible DataSet carrying a `harmonic`
        // axis where the caller expects `mixIndex` — a wrong answer with no error anywhere.
        if (p.ToneFreqsHz.Length >= 2)
        {
            var mt = RunMultiTone(p, warmStart);
            return new HbRunResult(mt.Ds, converged: mt.Converged, interfaceV: mt.InterfaceV,
                trace: mt.Trace);
        }

        int    K      = p.MaxHarmonic;
        double f0     = p.ToneHz;
        int    gridN  = HbFft.GridSize(K, p.FFTOverSample);
        double omega0 = 2.0 * Math.PI * f0;

        // ── Setup: linear extractor (extracts Y_{N×N} and I_src at each harmonic) ──
        var extractor  = GetExtractor(_settings);
        int N          = extractor.InterfaceCount;
        int[] ifNodes  = extractor.InterfaceNodes;

        // Node names for result labelling.
        var nodeNames = ifNodes.Select(n =>
            n < _netlist.Nodes.Count ? _netlist.Nodes.NameOf(n) : $"node{n}").ToArray();

        // ── Commensurability check (harmonic-balance.md §3.1) ────────────────
        CheckCommensurability(f0, K);

        // ── Configure P1Tone / PnTone / Tuner terminations with tone context (must precede extraction) ──
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is P1ToneModel p1)
            {
                double driveHz = ec.Parameters.TryGetValue("Freq", out var fv) && fv.Kind == ValueKind.Real
                    ? fv.AsReal() : f0;
                p1.SetToneContext(fc: f0, driveFreqHz: driveHz);
            }
            else if (ec.Model is PnToneModel pn)
            {
                pn.SetToneContext(fc: f0);   // single-tone band ruler; PnTone drives at its own Freq[i]
            }
            else if (ec.Model is TunerModel tn)
            {
                GiveTunerItsBandRuler(tn, f0);
            }
        }

        // ── Extract Y_{N×N}(0) and I_src(0) for DC (k=0): real DC admittance + Norton source.
        // ExtractDC throws SingularMatrixException if any interface node is voltage-pinned
        // (ideal inductor + ideal voltage source = zero-impedance DC path).  Fix: add R to chokes.
        var yNN  = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();

        // ── Extract Y_{N×N} and I_src for k=1..K ─────────────────────────────
        for (int k = 1; k <= K; k++)
        {
            double omegaK = k * omega0;  // exact-harmonic guarantee: k*(2π*f0)
            var (y, s)    = extractor.Extract(omegaK);
            yNN[k]        = y;
            iSrc[k]       = s;
        }

        var trace = new HbConvergenceTrace();

        // C2: Per-branch current accumulator: "instancePath:terminalName" → spectra.
        var portCurrentsByBranch = new Dictionary<string, List<Complex[]>>(StringComparer.Ordinal);

        // C1: Source RHS snapshot for the linear back-solver.
        var bSrcThisPoint = new Complex[K + 1][];
        for (int k = 0; k <= K; k++)
        {
            double omegaSnap = k == 0 ? 0.0 : k * omega0;
            bSrcThisPoint[k] = extractor.BuildSourceRhs(omegaSnap);
        }

        // Initial guess: continuation warm-start from the previous sweep point's converged spectrum
        // when supplied (skips the per-point nonlinear-DC solve — harmonic-balance.md §11); otherwise
        // seed from a fresh DC operating point.
        Complex[,] V;
        if (warmStart is not null && warmStart.GetLength(0) == N && warmStart.GetLength(1) == K + 1)
        {
            V = (Complex[,])warmStart.Clone();
        }
        else
        {
            var dcResult = NonlinearDcEngine.Run(_netlist, _settings);
            if (!dcResult.Converged)
                _netlist.AddWarningOnce("hb-dc-nonconverge",
                    "HB: DC operating point did not converge; proceeding with best available.");
            V = InitialGuess(null, dcResult, N, K, ifNodes);
        }

        // Re-extract source excitation (drive amplitude baked into I_src).
        // Y_{N×N} is topology-based (constant); only I_src changes with drive level.
        for (int k = 1; k <= K; k++)
        {
            double omegaK = k * omega0;
            var (_, s) = extractor.Extract(omegaK);
            iSrc[k] = s;
        }

        // ── Control-current context (brief #2): detect SDDs with C[n] and resolve HB branch indices.
        // The extractor has already stamped all linear devices (assigning LastBranchIndex etc.),
        // so we can read valid HB-MNA indices here before the Newton loop.
        // RunSinglePoint (loadpull) and two-tone pass null — control currents out of scope there.
        ControlCurrentContext? cc = null;
        {
            bool hasCtrl = false;
            foreach (var ec in _netlist.Components)
                if (ec.Model is SddModel sdd && sdd.ControlRefs.Length > 0)
                { hasCtrl = true; break; }
            if (hasCtrl)
            {
                ResolveControlBranchIndicesHb(extractor, _netlist);
                cc = new ControlCurrentContext(extractor, bSrcThisPoint, f0, K);
            }
        }

        // ── Newton solve ──────────────────────────────────────────────────────
        var effectiveSettings = p.MaxIter != _settings.HbMaxIter
            ? new AnalysisSettings
              {
                  HbMaxIter              = p.MaxIter,
                  Gmin                   = _settings.Gmin,
                  ConductanceRegularization = _settings.ConductanceRegularization,
                  InductanceRegularization  = _settings.InductanceRegularization,
                  InductanceRegR            = _settings.InductanceRegR,
                  Gmax                      = _settings.Gmax,
                  DcBiasStepping            = _settings.DcBiasStepping,
                  DriveStepping             = _settings.DriveStepping,
                  NonlinearAbsTol           = _settings.NonlinearAbsTol,
              }
            : _settings;
        // A cold solve first unless the directive says to ramp unconditionally (DriveStepping=Always).
        HbNewton.SolveResult? solveResult = null;
        if (p.DriveStepping != DcBiasSteppingMode.Always)
        {
            solveResult = HbNewton.Solve(V, yNN, iSrc, f0, K, N,
                _netlist, ifNodes, gridN, effectiveSettings, p.Tol,
                p.Lambda, p.GuardHarmonic, cc);

            trace.AddStep(new HbConvergenceTrace.StepRecord(
                0.0, solveResult.Iterations, solveResult.Converged,
                solveResult.IterTrace));
        }

        // ── DriveStepping (§11): the drive-ramp fallback ──────────────────────
        // Never = report the failure as it stands; IfNecessary = ramp only when the cold solve did
        // not converge; Always = ramp without trying cold. The line search catches most of what used
        // to land here (HB-P3 M1), so IfNecessary is normally free.
        if (p.DriveStepping != DcBiasSteppingMode.Never &&
            (solveResult is null || !solveResult.Converged) &&
            // A vacuous tolerance is not a hard solve — every ramp rung LOWERS the drive, so each one
            // is vacuous too. Ramping would spend the whole ladder to arrive at the same refusal.
            !(solveResult is not null && double.IsFinite(solveResult.VacuousToleranceScale)))
        {
            var sources = HbDriveRamp.Collect(_netlist);
            if (sources.Count > 0)
            {
                try
                {
                    var rung = HbDriveRamp.Walk(
                        sources,
                        (offsetDb, warm) => SolveRung(
                            offsetDb, warm, extractor, yNN, iSrc, f0, K, N, ifNodes, gridN,
                            effectiveSettings, p, cc is not null, trace),
                        r => r.V);

                    // A ramp that never reached the requested drive leaves the cold result standing
                    // (fact 5: a wrong branch must never be smuggled in as an answer).
                    if (rung is not null)
                    {
                        Array.Copy(rung.V, V, V.Length);
                        solveResult = rung.Result;
                    }
                }
                finally
                {
                    // A throw mid-ramp must not leave the netlist stamped at a rung.
                    HbDriveRamp.Restore(sources);
                    for (int k = 1; k <= K; k++) iSrc[k] = extractor.Extract(k * omega0).ISrc;
                }
            }
        }

        // DriveStepping=Always with no ramp to run (no reachable tone source, or no rung converged):
        // fall back to the ordinary cold solve so the point still reports SOMETHING measured.
        if (solveResult is null)
        {
            solveResult = HbNewton.Solve(V, yNN, iSrc, f0, K, N,
                _netlist, ifNodes, gridN, effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic, cc);
            trace.AddStep(new HbConvergenceTrace.StepRecord(
                0.0, solveResult.Iterations, solveResult.Converged, solveResult.IterTrace));
        }

        if (!solveResult.Converged && double.IsFinite(solveResult.VacuousToleranceScale))
        {
            string msg = VacuousToleranceMessage(p.Tol, solveResult.VacuousToleranceScale);
            _netlist.AddWarning(msg);
            Console.Error.WriteLine($"[HB] {msg}");
        }
        else if (!solveResult.Converged)
        {
            double res = solveResult.IterTrace.LastOrDefault()?.ResidualNorm ?? 0.0;
            _netlist.AddWarning(
                $"HB did not converge (‖F‖={res:E3} after {solveResult.Iterations} iterations); " +
                $"stored best-available result.");
            Console.Error.WriteLine(
                $"[HB] Non-convergence: ‖F‖={res:E3} after {solveResult.Iterations} iterations.");
        }

        if (_settings.HbConsoleDiagnostics)
        {
            Console.Error.Write("[HB-DC]:");
            for (int n = 0; n < N; n++)
            {
                string nm = n < nodeNames.Length ? nodeNames[n] : $"if[{n}]";
                Console.Error.Write(
                    $"  {nm} V={V[n,0].Real:F3}V I_nl={solveResult.INl[n,0].Real*1e3:F2}mA");
            }
            Console.Error.WriteLine();
        }

        // C2: Post-convergence per-device port-current extraction (not in Newton hot path).
        // Pass cc + converged INl so SDDs with C[n] refs get the correct _cn values.
        var pointPortCurrents = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, f0, _netlist, ifNodes, cc, solveResult.INl, solveResult.PortTerms);
        foreach (var (key, spec) in pointPortCurrents)
        {
            if (!portCurrentsByBranch.TryGetValue(key, out var lst))
                portCurrentsByBranch[key] = lst = [];
            lst.Add(spec);
        }

        if (_settings.HbConsoleDiagnostics) trace.Print();

        // C1: Lazy linear back-solver — recovers linear-interior node voltages on demand.
        var backSolver = new HbLinearBackSolver(
            extractor, f0, K, [solveResult.INl], [bSrcThisPoint], _netlist);

        // Expand V/INl to all user-facing non-ground nodes (brief hb-linear-nodes-in-cube).
        // Interface nodes use the converged Newton solution directly; linear-only nodes
        // are recovered via HbLinearBackSolver.  __-prefixed internal mint nodes are excluded.
        // Every user-facing non-ground node under its own name, then one extra row per VProbe
        // alias carrying the SAME node's spectrum under the probe's name. One call rather than the
        // loop this used to be, so DC and HB cannot disagree about which rows a probe adds.
        var (fullNodeIds, namesFull) = _netlist.Nodes.ResultRows(excludeInternal: true);

        var ifNodeToIdx = new Dictionary<int, int>(N);
        for (int n = 0; n < N; n++)
            ifNodeToIdx[ifNodes[n]] = n;

        int     Nfull     = fullNodeIds.Length;
        var     Vfull     = new Complex[Nfull, K + 1];
        var     INlfull   = new Complex[Nfull, K + 1];

        for (int fi = 0; fi < Nfull; fi++)
        {
            int c = fullNodeIds[fi];
            if (ifNodeToIdx.TryGetValue(c, out int ifIdx))
            {
                for (int k = 0; k <= K; k++)
                {
                    Vfull  [fi, k] = V             [ifIdx, k];
                    INlfull[fi, k] = solveResult.INl[ifIdx, k];
                }
            }
            else
            {
                // Linear-only node: back-solve voltage; INl = 0 (no nonlinear current here).
                for (int k = 0; k <= K; k++)
                    Vfull[fi, k] = backSolver.GetNodeVoltage(c, k, 0);
            }
        }

        // IProbe branch currents (full spectrum) via the linear back-solver.
        // LastBranchIndex is the absolute MNA row (AddBranch returns _nodeCount + branchLocalIdx),
        // so x[ip.LastBranchIndex] directly yields the branch current.
        var probeCurrents = new Dictionary<string, Complex[]>(StringComparer.Ordinal);
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is not SeriesProbeModelBase ip || ip.LastBranchIndex < 0) continue;
            var spec = new Complex[K + 1];
            for (int k = 0; k <= K; k++)
            {
                var x = backSolver.GetSolution(k, 0);
                spec[k] = ip.LastBranchIndex < x.Length ? x[ip.LastBranchIndex] : Complex.Zero;
            }
            probeCurrents[ec.InstancePath] = spec;
        }

        // Operating-point read-back, at the converged spectrum and nowhere else. One evaluation per
        // reporting external device over the whole time grid, so a model's own quantities come back
        // as waveforms on the same harmonic axis V and INl already use. Empty — and free — for a
        // design with no compiled model, or one whose devices have the read-back switched off.
        var opVars = HbOpVars.CollectSingleTone(V, N, K, gridN, _netlist, ifNodes);

        var ds = BuildSingleToneDataSet(
            Vfull, INlfull, namesFull, f0, K, PointOutcome.Of(solveResult.Converged, solveResult.IterTrace), portCurrentsByBranch,
            _netlist.Nodes.LabeledNames, probeCurrents, opVars);

        // ── The WSProbe small-signal sweep (brief-wsprobe-5) ──────────────────
        // The conversion-matrix solve linearises THIS converged periodic steady state, which is why
        // it takes the solve's own G/C spectra rather than re-evaluating the devices: after a drive
        // ramp, a second evaluation would linearise at whichever iterate happened to be in V.
        LastLinearizedPoint = solveResult.G is not null && solveResult.C is not null
            ? new LinearizedPoint(yNN, iSrc, solveResult.G, solveResult.C,
                                  solveResult.Buckets ?? [], V, N, K, omega0)
            : null;

        var wspProbes = WspProbeSites();
        if (wspProbes.Length > 0 && p.HasSsSweep && solveResult.G is not null && solveResult.C is not null)
        {
            var sidebands = new HbSmallSignal.ToneSidebands(
                solveResult.G, solveResult.C, solveResult.Buckets, p.SsOrder, omega0);
            NoteSidebandTruncation(p, sidebands.Count);
            AddWspSmallSignalCubes(ds, p, extractor, sidebands, wspProbes, yNN[0],
                commensurateWith: $"the fundamental {f0 / 1e9:G6} GHz");
        }
        else
        {
            NoteNoSmallSignalSweep(wspProbes.Length);
        }

        // V holds the converged interface spectrum [N, K+1]; expose it so a parametric sweep can
        // warm-start the next point (continuation — §11). Only propagate a converged seed.
        return new HbRunResult(ds, backSolver, solveResult.Converged, V, trace);
    }

    /// <summary>
    /// The <c>Converged</c>/<c>Residual</c> scalars a point publishes, taken from THE SOLVE THAT
    /// PRODUCED ITS ANSWER.
    ///
    /// <para>These used to be read off <c>trace.Steps[0]</c>, which was the same thing while a point
    /// was always exactly one Newton solve. It stopped being the same thing when <c>DriveStepping</c>
    /// gained its ramp (HB-P3 M2): step 0 is then the cold attempt that FAILED, and a point rescued by
    /// the ramp would have published <c>Converged = 0</c> beside a perfectly good spectrum — while
    /// under <c>Always</c>, step 0 is the small-signal bottom rung and would publish its residual for
    /// the requested drive's. Reading the last step instead is no better: a ramp that runs out of
    /// rungs ends on a failing one that is not the answer either.</para>
    /// </summary>
    private readonly record struct PointOutcome(bool Converged, double Residual)
    {
        public static PointOutcome Of(bool converged,
            IReadOnlyList<HbConvergenceTrace.IterRecord> iterTrace)
            => new(converged, iterTrace.Count > 0 ? iterTrace[^1].ResidualNorm : 0.0);
    }

    /// <summary>One rung of the single-tone drive ramp: its converged spectrum and its solve.</summary>
    private sealed record DriveRung(Complex[,] V, HbNewton.SolveResult Result);

    /// <summary>
    /// Solve the single-tone point at one rung of the <c>DriveStepping</c> ramp — the sources are
    /// already scaled by <see cref="HbDriveRamp.Walk{T}"/>, so this re-extracts the Norton excitation
    /// at that drive and runs the Newton from <paramref name="warm"/> (the rung below) or, for the
    /// bottom rung, from a fresh DC operating point.
    ///
    /// <para>Only <c>I_src</c> is re-extracted: <c>Y_NN</c> is topology, and a drive change moves the
    /// right-hand side only — the extractor's cached LU is reused, so a rung costs one back-solve per
    /// harmonic on top of its Newton. The control-current context is rebuilt per rung for the same
    /// reason its <c>BSrc</c> exists at all: it is a snapshot of the source RHS, and a rung's sources
    /// are not the requested drive's.</para>
    ///
    /// <para>Returns null for a rung that did not converge, which is what makes the ladder subdivide
    /// it. Every rung — converged or not — goes into the trace at its own dB offset.</para>
    /// </summary>
    private DriveRung? SolveRung(
        double offsetDb, Complex[,]? warm,
        HbLinearExtractor extractor, Complex[][,] yNN, Complex[][] iSrc,
        double f0, int K, int N, int[] ifNodes, int gridN,
        AnalysisSettings effectiveSettings, HbAnalysisParams p, bool hasControl,
        HbConvergenceTrace trace)
    {
        double omega0 = 2.0 * Math.PI * f0;
        for (int k = 1; k <= K; k++) iSrc[k] = extractor.Extract(k * omega0).ISrc;

        ControlCurrentContext? cc = null;
        if (hasControl)
        {
            var bSrc = new Complex[K + 1][];
            for (int k = 0; k <= K; k++) bSrc[k] = extractor.BuildSourceRhs(k == 0 ? 0.0 : k * omega0);
            cc = new ControlCurrentContext(extractor, bSrc, f0, K);
        }

        Complex[,] v;
        if (warm is not null && warm.GetLength(0) == N && warm.GetLength(1) == K + 1)
        {
            v = (Complex[,])warm.Clone();
        }
        else
        {
            var dc = NonlinearDcEngine.Run(_netlist, effectiveSettings);
            v = InitialGuess(null, dc, N, K, ifNodes);
        }

        var sr = HbNewton.Solve(v, yNN, iSrc, f0, K, N, _netlist, ifNodes, gridN,
            effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic, cc);

        trace.AddStep(new HbConvergenceTrace.StepRecord(
            offsetDb, sr.Iterations, sr.Converged, sr.IterTrace));

        return sr.Converged ? new DriveRung(v, sr) : null;
    }

    // ── Two-tone engine entry point (harmonic-balance.md §6) ─────────────────

    /// <summary>
    /// Multi-tone (two-tone) HB run. Generalizes <see cref="Run"/> from the scalar harmonic axis
    /// to the 2-D mixing lattice (<see cref="MixingGrid"/>): the linear interface and Norton source
    /// are extracted per retained mixing product at ω(k₁,k₂)=2π(k₁f₁+k₂f₂), the nonlinear blocks
    /// use the separable 2-D FFT, and the Newton solve is <see cref="HbNewton2D.Solve"/>.
    /// The returned <see cref="HbResult"/> carries the grid; V/INl are indexed [sweep][node, mixIdx].
    /// </summary>
    private (DataSet Ds, bool Converged, Complex[,] InterfaceV, HbConvergenceTrace Trace) RunTwoTone(
        HbAnalysisParams p, Complex[,]? warmStart)
    {
        double f1 = p.ToneFreqsHz[0];
        double f2 = p.ToneFreqsHz[1];
        double w1 = 2.0 * Math.PI * f1, w2 = 2.0 * Math.PI * f2;

        var grid     = new MixingGrid(p.MaxMixOrder);
        int M        = grid.MixCount;
        // Per-axis grid sizing reaches the diamond's single-axis extent (k=MaxMixOrder) and, via
        // the 4·order rule, the 2·order sum bins the Jacobian needs (harmonic-balance.md §5.2/§6.1).
        var (N1, N2) = HbFft2D.GridSizes(p.MaxMixOrder, p.MaxMixOrder, p.FFTOverSample);

        var extractor = GetExtractor(_settings);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        var nodeNames = ifNodes.Select(n =>
            n < _netlist.Nodes.Count ? _netlist.Nodes.NameOf(n) : $"node{n}").ToArray();

        CheckCommensurabilityMultiTone(grid, f1, f2);

        // ── Configure P1Tone / PnTone sources with tone context (must precede extraction) ──
        double fcTwoTone = (f1 + f2) / 2.0;
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is P1ToneModel p1)
            {
                double driveHz = ec.Parameters.TryGetValue("Freq", out var fv) && fv.Kind == ValueKind.Real
                    ? fv.AsReal() : f1;
                p1.SetToneContext(fc: fcTwoTone, driveFreqHz: driveHz);
            }
            else if (ec.Model is PnToneModel pn)
            {
                // A PnTone is the natural two-tone driver: it injects each of its Freq[i] tones (f1, f2)
                // with its own Pavl[i]/Phase[i]. f_c is the band ruler for the shared Z[k] terminations.
                pn.SetToneContext(fc: fcTwoTone);
            }
            else if (ec.Model is TunerModel tn)
            {
                GiveTunerItsBandRuler(tn, fcTwoTone);   // the same band ruler the two tone sources get
            }
        }

        // Extract the linear interface per mixing product. A retained rep may have NEGATIVE
        // frequency (e.g. (1,-1) = f1−f2); for a real network Y(−ω)=conj(Y(ω)). We extract at |ω|
        // and conjugate so the Z_Port's explicit complex Z(|f|) stays consistent with the L/C
        // elements (which conjugate naturally under ω→−ω). Sources sit only on positive carriers,
        // so the Norton vector at a negative-ω rep is zero (conj is a no-op there).
        (Complex[,] y, Complex[] s) ExtractMix(double omega)
        {
            if (omega >= 0) return extractor.Extract(omega);
            var (yp, sp) = extractor.Extract(-omega);
            return (ConjugateMatrix(yp), ConjugateVector(sp));
        }

        var yNN  = new Complex[M][,];
        var iSrc = new Complex[M][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int m = 1; m < M; m++)
            (yNN[m], iSrc[m]) = ExtractMix(grid.OmegaOf(m, w1, w2));

        var trace = new HbConvergenceTrace();
        var portCurrentsByBranch = new Dictionary<string, List<Complex[]>>(StringComparer.Ordinal);
        var effectiveSettings = EffectiveSettings(p);

        // Continuation warm start (§11.1) — the same rule the single-tone path has followed since it
        // was built, on the lattice instead of the harmonic axis: a seed of THIS run's shape is the
        // Newton guess and the per-point nonlinear-DC solve is skipped entirely (HB-P3 M3).
        var V = SeedLattice(warmStart, N, M, ifNodes, effectiveSettings);

        // Re-extract source excitation (drive amplitude baked into I_src).
        for (int m = 1; m < M; m++)
            (_, iSrc[m]) = ExtractMix(grid.OmegaOf(m, w1, w2));

        HbNewton2D.SolveResult? solveResult = null;
        if (p.DriveStepping != DcBiasSteppingMode.Always)
        {
            solveResult = HbNewton2D.Solve(V, yNN, iSrc, grid, f1, f2, N, N1, N2,
                _netlist, ifNodes, effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic);

            trace.AddStep(new HbConvergenceTrace.StepRecord(
                0.0, solveResult.Iterations, solveResult.Converged, solveResult.IterTrace));
        }

        // DriveStepping's drive ramp — the two-tone twin of the single-tone one; see HbDriveRamp.
        if (p.DriveStepping != DcBiasSteppingMode.Never &&
            (solveResult is null || !solveResult.Converged))
        {
            var sources = HbDriveRamp.Collect(_netlist);
            if (sources.Count > 0)
            {
                try
                {
                    var rung = HbDriveRamp.Walk(
                        sources,
                        (offsetDb, warm) =>
                        {
                            for (int m = 1; m < M; m++) (_, iSrc[m]) = ExtractMix(grid.OmegaOf(m, w1, w2));
                            var v  = SeedLattice(warm, N, M, ifNodes, effectiveSettings);
                            var sr = HbNewton2D.Solve(v, yNN, iSrc, grid, f1, f2, N, N1, N2,
                                _netlist, ifNodes, effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic);
                            trace.AddStep(new HbConvergenceTrace.StepRecord(
                                offsetDb, sr.Iterations, sr.Converged, sr.IterTrace));
                            return sr.Converged
                                ? new LatticeRung<HbNewton2D.SolveResult>(v, sr) : null;
                        },
                        r => r.V);

                    if (rung is not null)
                    {
                        Array.Copy(rung.V, V, V.Length);
                        solveResult = rung.Result;
                    }
                }
                finally
                {
                    HbDriveRamp.Restore(sources);
                    for (int m = 1; m < M; m++) (_, iSrc[m]) = ExtractMix(grid.OmegaOf(m, w1, w2));
                }
            }
        }

        if (solveResult is null)
        {
            solveResult = HbNewton2D.Solve(V, yNN, iSrc, grid, f1, f2, N, N1, N2,
                _netlist, ifNodes, effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic);
            trace.AddStep(new HbConvergenceTrace.StepRecord(
                0.0, solveResult.Iterations, solveResult.Converged, solveResult.IterTrace));
        }

        if (!solveResult.Converged)
        {
            double res2d = solveResult.IterTrace.LastOrDefault()?.ResidualNorm ?? 0.0;
            _netlist.AddWarning(
                $"HB (two-tone) did not converge (‖F‖={res2d:E3} after {solveResult.Iterations} iters); " +
                $"stored best-available result.");
            Console.Error.WriteLine(
                $"[HB2D] Non-convergence: ‖F‖={res2d:E3} after {solveResult.Iterations} iters.");
        }

        if (_settings.HbConsoleDiagnostics)
        {
            Console.Error.Write("[HB2D-DC]:");
            for (int n = 0; n < N; n++)
                Console.Error.Write(
                    $"  {nodeNames[n]} V={V[n,0].Real:F3}V I_nl={solveResult.INl[n,0].Real*1e3:F2}mA");
            Console.Error.WriteLine();
        }

        // C2: post-convergence per-device port-current extraction over the mixing lattice.
        var pointPortCurrents = HbNewton2D.ComputeDevicePortCurrents2D(
            V, grid, N, N1, N2, f1, f2, _netlist, ifNodes, solveResult.PortTerms);
        foreach (var (key, spec) in pointPortCurrents)
        {
            if (!portCurrentsByBranch.TryGetValue(key, out var lst))
                portCurrentsByBranch[key] = lst = [];
            lst.Add(spec);
        }

        if (_settings.HbConsoleDiagnostics) trace.Print();

        // ── Recover the FULL result: linear-interior node voltages + IProbe branch currents ──
        // Mirror single-tone Run by back-solving the linear network per mixing product, so V spans
        // ALL user nodes (not just the nonlinear-facing interface) and IProbe currents are available
        // — otherwise measurements like HB1.V("Vin",1) / HB1.I("Iin",1) fail (node/branch absent).
        // The per-product frequency ω_m = k1·w1 + k2·w2 can be NEGATIVE; for ω<0 we solve at |ω| with
        // the conjugated excitation and conjugate the result (Y(−ω)=conj(Y(ω))), reusing the LU that
        // ExtractMix already cached at |ω| — the same extract-at-|ω| convention the engine uses.
        Complex[] SolveMixFull(int m)
        {
            var iNlM = new Complex[N];
            for (int n = 0; n < N; n++) iNlM[n] = solveResult.INl[n, m];
            double omega = grid.OmegaOf(m, w1, w2);
            if (omega >= 0)
                return extractor.SolveFullNetwork(omega, iNlM, extractor.BuildSourceRhs(omega));

            for (int n = 0; n < N; n++) iNlM[n] = Complex.Conjugate(iNlM[n]);
            var bsrc = extractor.BuildSourceRhs(-omega);
            for (int i = 0; i < bsrc.Length; i++) bsrc[i] = Complex.Conjugate(bsrc[i]);
            var xn = extractor.SolveFullNetwork(-omega, iNlM, bsrc);
            for (int i = 0; i < xn.Length; i++) xn[i] = Complex.Conjugate(xn[i]);
            return xn;
        }

        var xMix = new Complex[M][];
        for (int m = 0; m < M; m++) xMix[m] = SolveMixFull(m);

        // Expand V/INl to all non-ground, non-internal user nodes (interface from the Newton solution;
        // linear-only nodes from the back-solve). __-prefixed mint nodes are excluded.
        // Every user-facing non-ground node under its own name, then one extra row per VProbe
        // alias carrying the SAME node's spectrum under the probe's name. One call rather than the
        // loop this used to be, so DC and HB cannot disagree about which rows a probe adds.
        var (fullNodeIds, namesFull) = _netlist.Nodes.ResultRows(excludeInternal: true);
        var ifNodeToIdx = new Dictionary<int, int>(N);
        for (int n = 0; n < N; n++) ifNodeToIdx[ifNodes[n]] = n;

        int Nfull   = fullNodeIds.Length;
        var Vfull   = new Complex[Nfull, M];
        var INlfull = new Complex[Nfull, M];
        for (int fi = 0; fi < Nfull; fi++)
        {
            int c = fullNodeIds[fi];
            if (ifNodeToIdx.TryGetValue(c, out int ifIdx))
                for (int m = 0; m < M; m++) { Vfull[fi, m] = V[ifIdx, m]; INlfull[fi, m] = solveResult.INl[ifIdx, m]; }
            else
                for (int m = 0; m < M; m++) Vfull[fi, m] = c - 1 < xMix[m].Length ? xMix[m][c - 1] : Complex.Zero;
        }

        // IProbe branch currents over the full mixing lattice (LastBranchIndex set during extraction).
        var probeCurrents = new Dictionary<string, Complex[]>(StringComparer.Ordinal);
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is not SeriesProbeModelBase ip || ip.LastBranchIndex < 0) continue;
            var spec = new Complex[M];
            for (int m = 0; m < M; m++)
                spec[m] = ip.LastBranchIndex < xMix[m].Length ? xMix[m][ip.LastBranchIndex] : Complex.Zero;
            probeCurrents[ec.InstancePath] = spec;
        }

        var opVars2 = HbOpVars.CollectTwoTone(V, grid, N, N1, N2, _netlist, ifNodes);

        var ds2 = BuildTwoToneDataSet(
            Vfull, INlfull, namesFull, grid, f1, f2, PointOutcome.Of(solveResult.Converged, solveResult.IterTrace), portCurrentsByBranch,
            _netlist.Nodes.LabeledNames, probeCurrents, opVars2);

        // The WSProbe small-signal sweep lives on the LATTICE two-tone path (R-wsp5-7), which is the
        // default; this is the rectangular-FFT path AnalysisSettings.HbTwoToneOnLattice = false
        // selects, and its Jacobian keeps its own frozen goldens. Said rather than silently skipped:
        // a probed run that produced no wsp with SS* keys present would look like a bug.
        var wspProbes2 = WspProbeSites();
        if (wspProbes2.Length > 0 && p.HasSsSweep)
            _netlist.AddWarningOnce("wsprobe.hb-ss-two-tone-grid-path",
                "WSProbe small-signal sweep: this two-tone run took the rectangular-FFT path " +
                "(HbTwoToneOnLattice = false), which the small-signal solve does not serve — it is " +
                "built on the mixing lattice. Leave HbTwoToneOnLattice at its default to compute the " +
                "probes' large-signal transfer functions.");
        else
            NoteNoSmallSignalSweep(wspProbes2.Length);

        // V holds the converged interface spectrum [N, M]; expose it so a parametric sweep can
        // warm-start the next point (§11 — the chain is shape-agnostic, so it works at any tone count).
        return (ds2, solveResult.Converged, V, trace);
    }

    /// <summary>
    /// One rung of a lattice (two-tone / T-tone) drive ramp. Generic in the solve result because
    /// <c>HbNewton2D</c> and <c>HbNewtonNd</c> declare their own — the two Newton loops are
    /// deliberately parallel implementations, not one behind an interface.
    /// </summary>
    private sealed record LatticeRung<TResult>(Complex[,] V, TResult Result);

    /// <summary>
    /// The lattice paths' initial guess: <paramref name="warmStart"/> when it is a seed of THIS run's
    /// shape <c>[N, M]</c>, otherwise a fresh nonlinear-DC operating point. Exactly the single-tone
    /// branch's five lines with <c>K+1</c> read as <c>M</c> — which is all the two-tone and T-tone
    /// paths ever needed to join the continuation the single-tone sweep has always had.
    /// </summary>
    private Complex[,] SeedLattice(Complex[,]? warmStart, int N, int M, int[] ifNodes,
        AnalysisSettings settings)
    {
        if (warmStart is not null && warmStart.GetLength(0) == N && warmStart.GetLength(1) == M)
            return (Complex[,])warmStart.Clone();

        var dcResult = NonlinearDcEngine.Run(_netlist, settings);
        if (!dcResult.Converged)
            _netlist.AddWarningOnce("hb-dc-nonconverge",
                "HB: DC operating point did not converge; proceeding with best available.");
        return InitialGuess2D(null, dcResult, N, M, ifNodes);
    }

    // ── T-tone engine entry point (harmonic-balance.md §6.5) ─────────────────

    /// <summary>
    /// Multi-tone (T ≥ 3) HB run. Structurally the same pass as <see cref="RunTwoTone"/> — extract
    /// the linear interface and Norton source per retained product at ω = 2π·Σ_t k_t f_t, solve
    /// Newton over the lattice, back-solve the linear interior, build the cubes — with three
    /// substitutions: the <see cref="MixingGrid"/> becomes the T-dimensional
    /// <see cref="MixingLattice"/>, the separable 2-D FFT becomes <see cref="HbApft"/>, and
    /// <see cref="HbNewton2D"/> becomes <see cref="HbNewtonNd"/>.
    ///
    /// <para>The returned DataSet has the SAME shape as the two-tone one — same cube names, same
    /// <c>mixIndex</c> axis carrying signed product frequencies, labels widened from
    /// <c>"(k1,k2)"</c> to <c>"(k1,…,kT)"</c> — so the data display renders a T-tone spectrum
    /// through exactly the path a two-tone one already uses.</para>
    /// </summary>
    private (DataSet Ds, bool Converged, Complex[,] InterfaceV, HbConvergenceTrace Trace) RunMultiTone(
        HbAnalysisParams p, Complex[,]? warmStart)
    {
        double[] f = p.ToneFreqsHz;
        int T = f.Length;

        CheckMultiToneCeiling(T, p.MaxMixOrder);

        var omegas  = new double[T];
        for (int t = 0; t < T; t++) omegas[t] = 2.0 * Math.PI * f[t];

        // The transform depends only on (tone count, MaxMixOrder, oversample) — nothing that
        // changes between sweep points — so it comes from the process-wide cache rather than being
        // rebuilt here. See HbApft.Get. The ceiling check above still runs first, so an over-cap
        // request is refused before anything is constructed.
        var apft    = HbApft.Get(T, p.MaxMixOrder, _settings.HbApftOversample);
        var lattice = apft.Lattice;
        int M       = lattice.MixCount;

        var extractor = GetExtractor(_settings);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        var nodeNames = ifNodes.Select(n =>
            n < _netlist.Nodes.Count ? _netlist.Nodes.NameOf(n) : $"node{n}").ToArray();

        CheckCommensurabilityLattice(lattice, f);

        // ── Configure P1Tone / PnTone / Tuner terminations with tone context ──
        // f_c is the band ruler for the shared Z[k] terminations; the two-tone path uses
        // (f₁+f₂)/2, whose general form is the mean of the declared tones.
        double fc = 0;
        for (int t = 0; t < T; t++) fc += f[t];
        fc /= T;

        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is P1ToneModel p1)
            {
                double driveHz = ec.Parameters.TryGetValue("Freq", out var fv) && fv.Kind == ValueKind.Real
                    ? fv.AsReal() : f[0];
                p1.SetToneContext(fc: fc, driveFreqHz: driveHz);
            }
            else if (ec.Model is PnToneModel pn)
            {
                pn.SetToneContext(fc: fc);
            }
            else if (ec.Model is TunerModel tn)
            {
                GiveTunerItsBandRuler(tn, fc);
            }
        }

        // Extract the linear interface per product. A retained rep may have NEGATIVE frequency
        // (e.g. (1,−1,0)); for a real network Y(−ω)=conj(Y(ω)). Extract at |ω| and conjugate, so
        // an explicit complex Z(|f|) stays consistent with the L/C elements. Same rule as two-tone.
        (Complex[,] y, Complex[] s) ExtractMix(double omega)
        {
            if (omega >= 0) return extractor.Extract(omega);
            var (yp, sp) = extractor.Extract(-omega);
            return (ConjugateMatrix(yp), ConjugateVector(sp));
        }

        var yNN  = new Complex[M][,];
        var iSrc = new Complex[M][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int m = 1; m < M; m++)
            (yNN[m], iSrc[m]) = ExtractMix(lattice.OmegaOf(m, omegas));

        var trace = new HbConvergenceTrace();
        var portCurrentsByBranch = new Dictionary<string, List<Complex[]>>(StringComparer.Ordinal);
        var effectiveSettings = EffectiveSettings(p);

        // Continuation warm start (§11.1), identical to the two-tone path — see SeedLattice.
        var V = SeedLattice(warmStart, N, M, ifNodes, effectiveSettings);

        // Re-extract source excitation (drive amplitude baked into I_src).
        for (int m = 1; m < M; m++)
            (_, iSrc[m]) = ExtractMix(lattice.OmegaOf(m, omegas));

        HbNewtonNd.SolveResult? solveResult = null;
        if (p.DriveStepping != DcBiasSteppingMode.Always)
        {
            solveResult = HbNewtonNd.Solve(V, yNN, iSrc, apft, f, N,
                _netlist, ifNodes, effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic);

            trace.AddStep(new HbConvergenceTrace.StepRecord(
                0.0, solveResult.Iterations, solveResult.Converged, solveResult.IterTrace));
        }

        // DriveStepping's drive ramp — the T-tone twin; see HbDriveRamp.
        if (p.DriveStepping != DcBiasSteppingMode.Never &&
            (solveResult is null || !solveResult.Converged))
        {
            var sources = HbDriveRamp.Collect(_netlist);
            if (sources.Count > 0)
            {
                try
                {
                    var rung = HbDriveRamp.Walk(
                        sources,
                        (offsetDb, warm) =>
                        {
                            for (int m = 1; m < M; m++) (_, iSrc[m]) = ExtractMix(lattice.OmegaOf(m, omegas));
                            var v  = SeedLattice(warm, N, M, ifNodes, effectiveSettings);
                            var sr = HbNewtonNd.Solve(v, yNN, iSrc, apft, f, N,
                                _netlist, ifNodes, effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic);
                            trace.AddStep(new HbConvergenceTrace.StepRecord(
                                offsetDb, sr.Iterations, sr.Converged, sr.IterTrace));
                            return sr.Converged
                                ? new LatticeRung<HbNewtonNd.SolveResult>(v, sr) : null;
                        },
                        r => r.V);

                    if (rung is not null)
                    {
                        Array.Copy(rung.V, V, V.Length);
                        solveResult = rung.Result;
                    }
                }
                finally
                {
                    HbDriveRamp.Restore(sources);
                    for (int m = 1; m < M; m++) (_, iSrc[m]) = ExtractMix(lattice.OmegaOf(m, omegas));
                }
            }
        }

        if (solveResult is null)
        {
            solveResult = HbNewtonNd.Solve(V, yNN, iSrc, apft, f, N,
                _netlist, ifNodes, effectiveSettings, p.Tol, p.Lambda, p.GuardHarmonic);
            trace.AddStep(new HbConvergenceTrace.StepRecord(
                0.0, solveResult.Iterations, solveResult.Converged, solveResult.IterTrace));
        }

        if (!solveResult.Converged)
        {
            double resNd = solveResult.IterTrace.LastOrDefault()?.ResidualNorm ?? 0.0;
            _netlist.AddWarning(
                $"HB ({T}-tone) did not converge (‖F‖={resNd:E3} after {solveResult.Iterations} iters); " +
                $"stored best-available result.");
            Console.Error.WriteLine(
                $"[HBnD] Non-convergence: ‖F‖={resNd:E3} after {solveResult.Iterations} iters.");
        }

        if (_settings.HbConsoleDiagnostics)
        {
            Console.Error.Write($"[HBnD-DC] ({T} tones, M={M}, APFT S={apft.SampleCount}):");
            for (int n = 0; n < N; n++)
                Console.Error.Write(
                    $"  {nodeNames[n]} V={V[n,0].Real:F3}V I_nl={solveResult.INl[n,0].Real*1e3:F2}mA");
            Console.Error.WriteLine();
        }

        // Post-convergence per-device port-current extraction over the lattice.
        var pointPortCurrents = HbNewtonNd.ComputeDevicePortCurrentsNd(
            V, apft, N, lattice, f, _netlist, ifNodes, solveResult.PortTerms);
        foreach (var (key, spec) in pointPortCurrents)
        {
            if (!portCurrentsByBranch.TryGetValue(key, out var lst))
                portCurrentsByBranch[key] = lst = [];
            lst.Add(spec);
        }

        if (_settings.HbConsoleDiagnostics) trace.Print();

        // ── Recover the FULL result: linear-interior node voltages + IProbe branch currents ──
        // Identical to the two-tone back-solve, including the ω<0 conjugate handling, so a
        // measurement naming a node behind an N-port resolves at T tones exactly as at two.
        Complex[] SolveMixFull(int m)
        {
            var iNlM = new Complex[N];
            for (int n = 0; n < N; n++) iNlM[n] = solveResult.INl[n, m];
            double omega = lattice.OmegaOf(m, omegas);
            if (omega >= 0)
                return extractor.SolveFullNetwork(omega, iNlM, extractor.BuildSourceRhs(omega));

            for (int n = 0; n < N; n++) iNlM[n] = Complex.Conjugate(iNlM[n]);
            var bsrc = extractor.BuildSourceRhs(-omega);
            for (int i = 0; i < bsrc.Length; i++) bsrc[i] = Complex.Conjugate(bsrc[i]);
            var xn = extractor.SolveFullNetwork(-omega, iNlM, bsrc);
            for (int i = 0; i < xn.Length; i++) xn[i] = Complex.Conjugate(xn[i]);
            return xn;
        }

        var xMix = new Complex[M][];
        for (int m = 0; m < M; m++) xMix[m] = SolveMixFull(m);

        // Every user-facing non-ground node under its own name, then one extra row per VProbe
        // alias carrying the SAME node's spectrum under the probe's name. One call rather than the
        // loop this used to be, so DC and HB cannot disagree about which rows a probe adds.
        var (fullNodeIds, namesFull) = _netlist.Nodes.ResultRows(excludeInternal: true);
        var ifNodeToIdx = new Dictionary<int, int>(N);
        for (int n = 0; n < N; n++) ifNodeToIdx[ifNodes[n]] = n;

        int Nfull   = fullNodeIds.Length;
        var Vfull   = new Complex[Nfull, M];
        var INlfull = new Complex[Nfull, M];
        for (int fi = 0; fi < Nfull; fi++)
        {
            int c = fullNodeIds[fi];
            if (ifNodeToIdx.TryGetValue(c, out int ifIdx))
                for (int m = 0; m < M; m++) { Vfull[fi, m] = V[ifIdx, m]; INlfull[fi, m] = solveResult.INl[ifIdx, m]; }
            else
                for (int m = 0; m < M; m++) Vfull[fi, m] = c - 1 < xMix[m].Length ? xMix[m][c - 1] : Complex.Zero;
        }

        var probeCurrents = new Dictionary<string, Complex[]>(StringComparer.Ordinal);
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is not SeriesProbeModelBase ip || ip.LastBranchIndex < 0) continue;
            var spec = new Complex[M];
            for (int m = 0; m < M; m++)
                spec[m] = ip.LastBranchIndex < xMix[m].Length ? xMix[m][ip.LastBranchIndex] : Complex.Zero;
            probeCurrents[ec.InstancePath] = spec;
        }

        var opVarsNd = HbOpVars.CollectMultiTone(V, apft, N, _netlist, ifNodes);

        var dsNd = BuildMultiToneDataSet(
            Vfull, INlfull, namesFull, lattice, f, PointOutcome.Of(solveResult.Converged, solveResult.IterTrace), portCurrentsByBranch,
            _netlist.Nodes.LabeledNames, probeCurrents, opVarsNd);

        // ── The WSProbe small-signal sweep over the mixing lattice (R-wsp5-7) ──
        var wspProbesNd = WspProbeSites();
        if (wspProbesNd.Length > 0 && p.HasSsSweep)
        {
            // v1 supports one and two tones. The APFT path's conversion blocks are a separate piece
            // of work, and the refusal states the size it would have to carry so the number is the
            // user's rather than a shrug.
            if (T >= 3)
            {
                int wouldNeed = MixingLattice.CountFor(T, 2 * p.MaxMixOrder);
                throw new InvalidOperationException(
                    $"wsprobe.hb-tones-unsupported: the WSProbe small-signal sweep supports one and " +
                    $"two tones; this analysis declares {T}. The conversion matrix would need the " +
                    $"device spectra over {wouldNeed} retained products at mixing order " +
                    $"{2 * p.MaxMixOrder}, which is a separate piece of work on the APFT path. Run " +
                    "the small-signal sweep on a one- or two-tone operating point.");
            }

            int ssOrder = Math.Clamp(p.SsMaxHarm ?? p.MaxMixOrder, 0, p.MaxMixOrder);
            var (diffLattice, gSpec, cSpec) = HbSmallSignal.LatticeSpectra(
                V, apft, _settings.HbApftOversample, N, _netlist, ifNodes);
            var sidebandsNd = new HbSmallSignal.LatticeSidebands(
                lattice, diffLattice, gSpec, cSpec, omegas, ssOrder);
            if (ssOrder < p.MaxMixOrder)
                _netlist.AddNoteOnce("wsprobe.hb-ss-maxharm",
                    $"WSProbe small-signal sweep: SSMaxHarm = {ssOrder} truncates the sideband set " +
                    $"to mixing order {ssOrder} ({sidebandsNd.Count} sidebands), below the " +
                    $"MaxMixOrder = {p.MaxMixOrder} the operating point retains. The wsp entries are " +
                    "the cheaper answer.");
            AddWspSmallSignalCubes(dsNd, p, extractor, sidebandsNd, wspProbesNd, yNN[0],
                commensurateWith: $"the tone lattice ({string.Join(", ", f.Select(x => $"{x / 1e9:G6} GHz"))})");
        }
        else
        {
            NoteNoSmallSignalSweep(wspProbesNd.Length);
        }

        return (dsNd, solveResult.Converged, V, trace);
    }

    /// <summary>
    /// The multi-tone analysis ceiling, enforced BEFORE any extraction or Newton solve.
    ///
    /// <para>The T ≥ 3 path solves a dense Jacobian whose size is 2·N·M, and M grows steeply with
    /// tone count — so an over-ambitious analysis must be refused in milliseconds rather than
    /// become a long run that then throws. <see cref="MixingLattice.CountFor"/> answers the size
    /// question in closed form, so this costs nothing.</para>
    ///
    /// <para>The message names the knob that actually BINDS and the value that would work: a
    /// refusal that only says "too big" leaves the user guessing which of tone count and mix
    /// order to move, and by how much.</para>
    /// </summary>
    private void CheckMultiToneCeiling(int tones, int maxMixOrder)
    {
        if (tones > _settings.HbMaxTones)
            throw new InvalidOperationException(
                $"HB: {tones} tones declared, but this engine supports at most {_settings.HbMaxTones} " +
                $"(AnalysisSettings.HbMaxTones). Reduce NumFreqs.");

        int products = MixingLattice.CountFor(tones, maxMixOrder);
        if (products <= _settings.HbMaxMixProducts) return;

        // Largest order that still fits, so the refusal carries a value the user can type.
        int fits = 0;
        for (int o = maxMixOrder - 1; o >= 1; o--)
            if (MixingLattice.CountFor(tones, o) <= _settings.HbMaxMixProducts) { fits = o; break; }

        string remedy = fits >= 1
            ? $"Lower MaxMixOrder to {fits} ({MixingLattice.CountFor(tones, fits):N0} products), " +
              $"or reduce the tone count."
            : $"Even MaxMixOrder=1 exceeds the cap at {tones} tones — reduce the tone count.";

        throw new InvalidOperationException(
            $"HB: {tones} tones at MaxMixOrder={maxMixOrder} retains {products:N0} mixing products " +
            $"(cap {_settings.HbMaxMixProducts:N0}, ≈{2 * products:N0} dense unknowns per interface node). " +
            remedy);
    }

    /// <summary>
    /// T-tone commensurability (harmonic-balance.md §3.1): every tone-source frequency must land on
    /// the {Σ_t k_t f_t} lattice of a retained mixing product. The general form of
    /// <see cref="CheckCommensurabilityMultiTone"/>; errors naming the off-grid source.
    /// </summary>
    private void CheckCommensurabilityLattice(MixingLattice lattice, double[] toneFreqsHz)
    {
        const double Tol = 1.0;  // 1 Hz

        bool OnGrid(double fTone)
        {
            for (int m = 0; m < lattice.MixCount; m++)
                if (Math.Abs(lattice.FrequencyOf(m, toneFreqsHz) - fTone) <= Tol) return true;
            return false;
        }

        string Grid() =>
            $"{{tones {string.Join(", ", toneFreqsHz.Select(x => $"{x:G6}"))}, " +
            $"MaxMixOrder={lattice.MaxMixOrder}}}";

        void Require(string instance, string key, double fTone)
        {
            if (Math.Abs(fTone) < Tol || OnGrid(fTone)) return;
            throw new InvalidOperationException(
                $"Commensurability check failed: source '{instance}' {key}={fTone:G6} Hz " +
                $"is not on the {toneFreqsHz.Length}-tone grid {Grid()}" +
                UnitMismatchHint(toneFreqsHz[0], fTone));
        }

        foreach (var ec in _netlist.Components)
        {
            switch (ec.Model)
            {
                case ToneSourceModelBase:
                    foreach (var (key, val) in ec.Parameters)
                    {
                        if (val.Kind != ValueKind.Real) continue;
                        if (!key.Equals("Freq", StringComparison.OrdinalIgnoreCase) &&
                            !key.StartsWith("Freq[", StringComparison.OrdinalIgnoreCase)) continue;
                        Require(ec.InstancePath, key, val.AsReal());
                    }
                    break;

                case P1ToneModel p1:
                    Require(ec.InstancePath, "Freq", p1.FreqHz);
                    break;

                case PnToneModel pn:
                    foreach (double fTone in pn.ToneFreqsHz)
                        Require(ec.InstancePath, "Freq", fTone);
                    break;
            }
        }
    }

    /// <summary>
    /// Effective settings with HbMaxIter overridden from the directive's MaxIter.
    ///
    /// <para><b>This is a hand-written copy, so every field must be listed — an omission is
    /// SILENT.</b> It used to drop <c>HbConsoleDiagnostics</c>, which meant `--diag` went quiet
    /// for any analysis whose <c>MaxIter=</c> differed from the settings default: the flag
    /// appeared to work on some netlists and not others, with nothing to indicate why. Anything
    /// added to <see cref="AnalysisSettings"/> that the HB engine reads belongs here too.</para>
    /// </summary>
    private AnalysisSettings EffectiveSettings(HbAnalysisParams p)
        => p.MaxIter == _settings.HbMaxIter ? _settings : new AnalysisSettings
        {
            HbMaxIter                 = p.MaxIter,
            Gmin                      = _settings.Gmin,
            ConductanceRegularization = _settings.ConductanceRegularization,
            InductanceRegularization  = _settings.InductanceRegularization,
            InductanceRegR            = _settings.InductanceRegR,
            Gmax                      = _settings.Gmax,
            DcBiasStepping            = _settings.DcBiasStepping,
            DriveStepping             = _settings.DriveStepping,
            NonlinearAbsTol           = _settings.NonlinearAbsTol,
            HbConsoleDiagnostics      = _settings.HbConsoleDiagnostics,
            HbSweepWarmStart          = _settings.HbSweepWarmStart,
            HbMaxTones                = _settings.HbMaxTones,
            HbMaxMixProducts          = _settings.HbMaxMixProducts,
            HbApftOversample          = _settings.HbApftOversample,
        };

    private static Complex[,] ConjugateMatrix(Complex[,] a)
    {
        int r = a.GetLength(0), c = a.GetLength(1);
        var b = new Complex[r, c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                b[i, j] = Complex.Conjugate(a[i, j]);
        return b;
    }

    private static Complex[] ConjugateVector(Complex[] a)
    {
        var b = new Complex[a.Length];
        for (int i = 0; i < a.Length; i++) b[i] = Complex.Conjugate(a[i]);
        return b;
    }

    private static Complex[,] InitialGuess2D(
        Complex[,]? prevV, NonlinearDcEngine.DcResult dcResult, int N, int M, int[] ifNodes)
    {
        var V = new Complex[N, M];
        if (prevV is not null)
        {
            for (int n = 0; n < N; n++)
            for (int m = 0; m < M; m++)
                V[n, m] = prevV[n, m];
            return V;
        }

        for (int n = 0; n < N; n++)
        {
            int circNode = ifNodes[n];
            double vdc = circNode > 0 && circNode - 1 < dcResult.NodeVoltages.Length
                ? dcResult.NodeVoltages[circNode - 1] : 0.0;
            V[n, 0] = new Complex(vdc, 0);          // (0,0) DC from the operating point
            for (int m = 1; m < M; m++)
                V[n, m] = new Complex(1e-3, 1e-3);  // small AC seed
        }
        return V;
    }

    /// <summary>
    /// Multi-tone commensurability (harmonic-balance.md §3.1): every tone-source frequency must land
    /// on the {k₁f₁+k₂f₂} lattice of a retained mixing product. Errors naming any off-grid source.
    /// </summary>
    private void CheckCommensurabilityMultiTone(MixingGrid grid, double f1, double f2)
    {
        const double Tol = 1.0;  // 1 Hz
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is ToneSourceModelBase)
            {
                foreach (var (key, val) in ec.Parameters)
                {
                    if (val.Kind != ValueKind.Real) continue;
                    bool isFreqKey = key.Equals("Freq", StringComparison.OrdinalIgnoreCase)
                                  || key.StartsWith("Freq[", StringComparison.OrdinalIgnoreCase);
                    if (!isFreqKey) continue;
                    double fTone = val.AsReal();
                    if (Math.Abs(fTone) < Tol) continue;

                    bool onGrid = false;
                    foreach (var (k1, k2) in grid.All())
                        if (Math.Abs(k1 * f1 + k2 * f2 - fTone) <= Tol) { onGrid = true; break; }
                    if (!onGrid)
                        throw new InvalidOperationException(
                            $"Commensurability check failed: source '{ec.InstancePath}' {key}={fTone:G6} Hz " +
                            $"is not on the two-tone grid {{f1={f1:G6}, f2={f2:G6}, MaxMixOrder={grid.MaxMixOrder}}}" +
                            UnitMismatchHint(f1, fTone));
                }
            }
            else if (ec.Model is P1ToneModel p1)
            {
                double fTone = p1.FreqHz;
                if (Math.Abs(fTone) < Tol) continue;

                bool onGrid = false;
                foreach (var (k1, k2) in grid.All())
                    if (Math.Abs(k1 * f1 + k2 * f2 - fTone) <= Tol) { onGrid = true; break; }
                if (!onGrid)
                    throw new InvalidOperationException(
                        $"Commensurability check failed: source '{ec.InstancePath}' Freq={fTone:G6} Hz " +
                        $"is not on the two-tone grid {{f1={f1:G6}, f2={f2:G6}, MaxMixOrder={grid.MaxMixOrder}}}" +
                        UnitMismatchHint(f1, fTone));
            }
            else if (ec.Model is PnToneModel pn)
            {
                foreach (double fTone in pn.ToneFreqsHz)
                {
                    if (Math.Abs(fTone) < Tol) continue;
                    bool onGrid = false;
                    foreach (var (k1, k2) in grid.All())
                        if (Math.Abs(k1 * f1 + k2 * f2 - fTone) <= Tol) { onGrid = true; break; }
                    if (!onGrid)
                        throw new InvalidOperationException(
                            $"Commensurability check failed: source '{ec.InstancePath}' Freq={fTone:G6} Hz " +
                            $"is not on the two-tone grid {{f1={f1:G6}, f2={f2:G6}, MaxMixOrder={grid.MaxMixOrder}}}" +
                            UnitMismatchHint(f1, fTone));
                }
            }
        }
    }

    // ── Single-point solve (used by the Loadpull engine) ─────────────────────

    /// <summary>
    /// Result of a single HB Newton solve at one operating point.
    /// </summary>
    /// <summary>
    /// INl[n,k] sign convention — stated once here, followed everywhere:
    ///
    /// INl[n,k] = current flowing FROM interface node n INTO the nonlinear device
    ///            (passive sign convention: positive = entering the device, leaving the node).
    ///
    /// Derivation: EvaluateNonlinear accumulates res.I[p] (SDD port current, passive convention)
    /// into iTime[nodeIdx] then FFTs.  The HB residual is F = Y_NN*(V−V_oc) + INl = 0.
    /// At DC, INl[drain,0] = +Idd (drain current leaving n_drain into FET) balances the
    /// Norton supply current Y_NN*(V_oc−V[drain]) = +Idd flowing into n_drain.  ✓
    ///
    /// Consequences for power and impedance formulas:
    ///   At RF (choke open ⟹ only FET + load at drain; only FET + source at gate):
    ///
    ///   KCL at n_drain: I_into_load = −INl[drain,k]   (load absorbs what FET drives out)
    ///   KCL at n_gate:  I_into_gate_from_source = INl[gate,k]  (source delivers what FET absorbs)
    ///
    ///   ⟹ Pout          = ½·Re(V[drain]·conj(−INl[drain,1])) = −½·Re(V[drain]·conj(INl[drain,1]))
    ///   ⟹ Pin_delivered = ½·Re(V[gate]·conj(INl[gate,1]))     = +½·Re(V[gate]·conj(INl[gate,1]))
    ///   ⟹ Zin (DUT input impedance seen from source)          = V[gate,1] / INl[gate,1]
    ///      (no negation — INl[gate,1] = I_from_source_into_gate)
    ///   ⟹ Zsource = conj(Zin)
    ///
    /// The sign ASYMMETRY between Pout and Pin is NOT ad-hoc; it follows directly from
    /// which side is "into load" vs "into device."
    /// </summary>
    public sealed record SinglePointResult(
        Complex[,] V,          // [N, K+1] — converged interface voltages (or best-available)
        Complex[,] INl,        // [N, K+1] — nonlinear device currents; see sign convention above
        bool       Converged,
        int        Iterations,
        string?    FailReason, // non-null only on non-convergence or errors
        IReadOnlyList<HbConvergenceTrace.IterRecord> IterTrace,
        HbLinearBackSolver? BackSolver = null);  // lazy linear back-solver (linear-interior node
                                                 // voltages + every linear branch current); null on
                                                 // a singular DC extraction. Used by the loadpull
                                                 // engine to recover source-tuner branch currents.

    /// <summary>
    /// Runs a single HB Newton solve at the current netlist operating point (no sweep loop).
    /// The caller (LoadpullEngine) manages the outer termination-grid and Pin loops,
    /// updating TunerModel state before each call.
    ///
    /// <paramref name="warmStart"/> — if provided, seeds V from this array instead of the DC point.
    /// Y_NN is re-extracted from the current netlist state (after any TunerModel overrides).
    ///
    /// Settings used: <paramref name="p"/>.MaxIter overrides HbMaxIter;
    /// InductanceRegularization is taken from the injected <paramref name="settingsOverride"/>
    /// (the loadpull engine passes Always).
    /// </summary>
    /// <summary>
    /// The sentence a refused-as-vacuous solve reports (see <see cref="HbNewton.IsToleranceVacuous"/>).
    /// It names both numbers, because the fault is their ORDER and neither one alone looks wrong.
    /// </summary>
    internal static string VacuousToleranceMessage(double tol, double rfScale) =>
        $"HB tolerance {tol:E3} A is not smaller than the RF excitation itself ({rfScale:E3} A), so " +
        "\u2016F\u2016 < tol is satisfied with no RF response at all and the solve has nothing to determine. " +
        "The drive reaching the circuit is far below the tolerance \u2014 typically a source impedance " +
        "whose real part starves a high-impedance input, or a Tol left at a value meant for a " +
        "milliamp-scale circuit.";

    public SinglePointResult RunSinglePoint(
        HbAnalysisParams  p,
        Complex[,]?       warmStart        = null,
        AnalysisSettings? settingsOverride  = null)
    {
        int    K      = p.MaxHarmonic;
        double f0     = p.ToneHz;
        int    gridN  = HbFft.GridSize(K, p.FFTOverSample);
        double omega0 = 2.0 * Math.PI * f0;

        var settings = settingsOverride ?? _settings;
        // Honour the directive's MaxIter.
        if (p.MaxIter != settings.HbMaxIter)
            settings = new AnalysisSettings
            {
                HbMaxIter                 = p.MaxIter,
                Gmin                      = settings.Gmin,
                ConductanceRegularization = settings.ConductanceRegularization,
                InductanceRegularization  = settings.InductanceRegularization,
                InductanceRegR            = settings.InductanceRegR,
                Gmax                      = settings.Gmax,
                DcBiasStepping            = settings.DcBiasStepping,
                DriveStepping             = settings.DriveStepping,
                NonlinearAbsTol           = settings.NonlinearAbsTol,
            };

        var extractor = GetExtractor(settings);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;

        // Extract Y_NN and I_src at every harmonic.
        var yNN  = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        try
        {
            (yNN[0], iSrc[0]) = extractor.ExtractDC();
        }
        catch (SingularMatrixException ex)
        {
            return new SinglePointResult(
                new Complex[N, K + 1], new Complex[N, K + 1],
                false, 0, $"DC extraction singular: {ex.Message}", []);
        }
        for (int k = 1; k <= K; k++)
        {
            double omegaK = k * omega0;
            var (y, s) = extractor.Extract(omegaK);
            yNN[k]  = y;
            iSrc[k] = s;
        }

        // Initial guess: warm-start if provided; else seed from DC operating point.
        Complex[,] V;
        if (warmStart is not null && warmStart.GetLength(0) == N && warmStart.GetLength(1) == K + 1)
        {
            V = new Complex[N, K + 1];
            for (int n = 0; n < N; n++)
            for (int k = 0; k <= K; k++)
                V[n, k] = warmStart[n, k];
        }
        else
        {
            var dcResult = NonlinearDcEngine.Run(_netlist, settings);
            V = InitialGuess(null, dcResult, N, K, ifNodes);
        }

        // Newton solve.
        var sr = HbNewton.Solve(V, yNN, iSrc, f0, K, N, _netlist, ifNodes, gridN, settings, p.Tol,
            p.Lambda, p.GuardHarmonic);

        string? failReason = null;
        if (!sr.Converged)
            failReason = double.IsFinite(sr.VacuousToleranceScale)
                ? VacuousToleranceMessage(p.Tol, sr.VacuousToleranceScale)
                : $"Newton non-convergence: ‖F‖={sr.IterTrace.LastOrDefault()?.ResidualNorm:E3} after {sr.Iterations} iters";

        // Lazy linear back-solver: snapshot the per-harmonic source RHS while component state is still
        // current (it must be captured now — BuildSourceRhs reflects only the latest stamp), then hand
        // it the converged NL currents. The actual linear solves happen only if a consumer (the loadpull
        // engine recovering source-tuner branch currents) calls GetSolution. Same construction as Run().
        var bSrcThisPoint = new Complex[K + 1][];
        for (int k = 0; k <= K; k++)
            bSrcThisPoint[k] = extractor.BuildSourceRhs(k == 0 ? 0.0 : k * omega0);
        var backSolver = new HbLinearBackSolver(extractor, f0, K, [sr.INl], [bSrcThisPoint], _netlist);

        return new SinglePointResult(V, sr.INl, sr.Converged, sr.Iterations, failReason, sr.IterTrace, backSolver);
    }

    // ── Initial guess ────────────────────────────────────────────────────────

    private static Complex[,] InitialGuess(
        Complex[,]?  prevV,
        NonlinearDcEngine.DcResult dcResult,
        int N, int K, int[] ifNodes)
    {
        var V = new Complex[N, K + 1];

        if (prevV is not null)
        {
            // Warm-start: copy previous converged spectrum.
            for (int n = 0; n < N; n++)
            for (int k = 0; k <= K; k++)
                V[n, k] = prevV[n, k];
            return V;
        }

        // Cold-start: DC operating point for k=0; small seed for k≥1.
        for (int n = 0; n < N; n++)
        {
            int circNode = ifNodes[n];
            double vdc   = circNode > 0 && circNode - 1 < dcResult.NodeVoltages.Length
                ? dcResult.NodeVoltages[circNode - 1]
                : 0.0;
            V[n, 0] = new Complex(vdc, 0);
            for (int k = 1; k <= K; k++)
                V[n, k] = new Complex(1e-3, 1e-3);   // small harmonic seed (deck slide 28)
        }

        return V;
    }

    // ── Sweep-point update ────────────────────────────────────────────────────

    private void UpdateSweepPoint(string sweepVar, double newValue)
    {
        // Update the sweep variable in the resolved globals (for tone-source re-evaluation).
        // Find tone-source instances (voltage AND current) and re-evaluate their amplitude/offset
        // expressions.
        var updatedGlobals = new Dictionary<string, Value>(_netlist.ResolvedGlobals,
            StringComparer.Ordinal)
        {
            [sweepVar] = new Value(newValue)
        };

        // Re-evaluate all globals that might depend on the sweep variable.
        // We use the TestBench GlobalVariables (expression strings) to rebuild.
        var reEvaluated = ReEvaluateGlobals(_tb, sweepVar, newValue,
            _netlist.ResolvedGlobals);

        // Update every tone source with the new globals.
        foreach (var ec in _netlist.Components)
            if (ec.Model is ToneSourceModelBase tsm)
                tsm.ReevaluateFromGlobals(reEvaluated);
    }

    private static IReadOnlyDictionary<string, Value> ReEvaluateGlobals(
        TestBench tb, string sweepVar, double newValue,
        IReadOnlyDictionary<string, Value> baseGlobals)
    {
        var result = new Dictionary<string, Value>(baseGlobals, StringComparer.Ordinal)
        {
            [sweepVar] = new Value(newValue)
        };

        // Re-evaluate all GlobalVariables from their expressions with sweepVar overridden.
        var scope = new Scope("sweep-globals");
        foreach (var kv in result)
            scope.Bind(kv.Key, kv.Value.ToString()!);
        var ev = new Evaluator();
        foreach (var kv in result)
            ev.InjectResolved("sweep-globals", kv.Key, kv.Value);

        foreach (var v in tb.GlobalVariables)
        {
            if (v.Name == sweepVar) continue;
            try
            {
                var ast = Parser.Parse(v.Expression);
                var val = ev.EvalExpr(ast, scope);
                if (val.Kind is ValueKind.Real or ValueKind.Complex)
                {
                    result[v.Name] = val;
                    // Update scope for subsequent variables that depend on this one.
                    scope.Bind(v.Name, val.ToString()!);
                    ev.InjectResolved("sweep-globals", v.Name, val);
                }
            }
            catch { /* leave previous value */ }
        }

        return result;
    }

    // ── Jacobian diagnostic (PASS A) ─────────────────────────────────────────

    /// <summary>
    /// Compare the analytic Jacobian (BuildJ) to a central-difference Jacobian of BuildF
    /// at operating point <paramref name="Vstar"/>, with the sweep variable set to
    /// <paramref name="sweepVal"/>.
    ///
    /// Use after engine.Run() to get a converged V; pass result.V[sweepIdx] as Vstar.
    /// </summary>
    public HbNewton.JacobianComparisonResult RunJacobianDiagnostic(
        HbAnalysisParams p,
        Complex[,]       Vstar,
        double           sweepVal,
        bool             useControlJacobian = true)
    {
        // Set sweep state so I_src(k) reflects the right drive level.
        if (p.SweepVarName is not null)
            UpdateSweepPoint(p.SweepVarName, sweepVal);

        int    K      = p.MaxHarmonic;
        double f0     = p.ToneHz;
        int    gridN  = HbFft.GridSize(K, p.FFTOverSample);
        double omega0 = 2.0 * Math.PI * f0;

        var extractor = GetExtractor(_settings);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;

        var yNN  = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int k = 1; k <= K; k++)
        {
            double omegaK = k * omega0;
            var (y, s) = extractor.Extract(omegaK);
            yNN[k]  = y;
            iSrc[k] = s;
        }

        // Vstar may have been computed at a different N (e.g. different netlist state).
        // Validate dimensions.
        if (Vstar.GetLength(0) != N || Vstar.GetLength(1) != K + 1)
            throw new ArgumentException(
                $"Vstar dimensions [{Vstar.GetLength(0)},{Vstar.GetLength(1)}] " +
                $"do not match extractor N={N}, K={K}.");

        // Control-current context (brief #3): without it the FD oracle can't exercise the
        // control path, so J_cc would be untested. Build it exactly as Run() does.
        ControlCurrentContext? cc = null;
        bool hasCtrl = false;
        foreach (var ec in _netlist.Components)
            if (ec.Model is SddModel sdd && sdd.ControlRefs.Length > 0) { hasCtrl = true; break; }
        if (hasCtrl)
        {
            ResolveControlBranchIndicesHb(extractor, _netlist);
            var bSrcThisPoint = new Complex[K + 1][];
            for (int k = 0; k <= K; k++)
                bSrcThisPoint[k] = extractor.BuildSourceRhs(k == 0 ? 0.0 : k * omega0);
            cc = new ControlCurrentContext(extractor, bSrcThisPoint, f0, K);
        }

        return HbNewton.CompareJacobianNumerical(Vstar, yNN, iSrc, f0, K, N, _netlist, ifNodes, gridN,
            cc, useControlJacobian);
    }

    // ── The WSProbe small-signal sweep (brief-wsprobe-5) ─────────────────────

    /// <summary>
    /// The probes as the small-signal solve addresses them, in the <c>idx</c> order the elaborator
    /// assigned (overview D-3). Empty for every unprobed netlist, and the emptiness is what keeps
    /// such a run byte-identical to one from before WSP-5 (R-wsp5-9(a)).
    /// </summary>
    private HbSmallSignal.ProbeSite[] WspProbeSites() =>
        _netlist.WspProbes
            .Select(w => new HbSmallSignal.ProbeSite(
                w.ComponentIndex, w.Label, w.Idx,
                _netlist.Components[w.ComponentIndex].Nodes[0],
                _netlist.Components[w.ComponentIndex].Nodes[1]))
            .ToArray();

    /// <summary>
    /// R-wsp5-1's "not an error" case: a WSProbe with no <c>SS*</c> keys. The probe is transparent
    /// (R-wsp1-1) and the run simply carries no <c>wsp</c> — said ONCE, because a designer who
    /// placed a probe and got no transfer functions has no other way to learn why.
    /// </summary>
    private void NoteNoSmallSignalSweep(int probeCount)
    {
        if (probeCount == 0) return;
        _netlist.AddNoteOnce("wsprobe.hb-no-ss-sweep",
            $"{probeCount} WSProbe{(probeCount == 1 ? "" : "s")} present; add SSStart/SSStop to the " +
            "HB analysis to compute their large-signal transfer functions.");
    }

    /// <summary>
    /// The whole small-signal half of a probed HB run: build the sideband family's conversion matrix
    /// at every probe frequency, solve the two injections per probe against it, and write the cubes
    /// (R-wsp5-5, R-wsp5-6). Shared by the single-tone and lattice paths — everything that differs
    /// between them is inside <paramref name="sidebands"/>.
    /// </summary>
    private void AddWspSmallSignalCubes(
        DataSet ds, HbAnalysisParams p, HbLinearExtractor extractor,
        HbSmallSignal.ISidebands sidebands, HbSmallSignal.ProbeSite[] probes,
        Complex[,] yDc, string commensurateWith)
    {
        double[] grid = p.SsGrid;
        var census = HbSmallSignalCache.SidebandCensus(grid, sidebands);

        int degree = PlanSmallSignalDegree(grid.Length, sidebands);
        var cache  = _wspCache ?? new HbSmallSignalCache(_settings, degree);

        // The chunk boundaries decide which slice holds which frequency, so the cache's slice count
        // and this run's degree are the same number by construction.
        cache.EnsureDegree(degree);
        cache.Plan(extractor.InterfaceCount, probes.Length, census.Distinct, grid.Length);

        var counters = new HbSmallSignal.Counters();
        var wsp = degree > 1
            ? SolveProbesInParallel(extractor, sidebands, probes, grid, yDc, counters,
                                    commensurateWith, cache, degree)
            : HbSmallSignal.SolveProbes(_netlist, extractor, sidebands, probes, grid, yDc,
                                        counters, commensurateWith, cache);

        counters.DistinctSidebandFrequencies = census.Distinct;
        counters.SignedSidebandFrequencies   = census.Signed;
        counters.TotalSidebandVisits         = census.Total;
        LastSmallSignalCounters = counters;

        ReportSidebandCensus(census, grid, sidebands);
        if (cache.OverBudgetNote() is { } budget)
            _netlist.AddNoteOnce("wsprobe.hb-cache-over-budget", budget);

        WspCubePacker.Add(
            ds, _netlist,
            new Axis("ssfreq", (double[])p.SsGrid.Clone(), "Hz"), p.SsGrid,
            probes.Select(x => new WspCubePacker.Probe(
                x.Label, x.Idx,
                WspCubePacker.TermZAt(_netlist, x.GNode),
                WspCubePacker.TermZAt(_netlist, x.LNode))).ToArray(),
            wsp, p.MarginThresholdDb,
            axisWhat: "a probe frequency of ");
    }

    // ── WSP-8: the tickle grid in parallel, and the census it reports ───────

    /// <summary>Minimum probe frequencies a worker must be given before splitting is worth an
    /// elaboration and a thread start — the S-parameter sweep's own threshold, for the same
    /// reason.</summary>
    public const int MinSsPointsPerWorker = 32;

    /// <summary>
    /// How many workers this small-signal sweep will actually run with. Pure and public to the
    /// tests, because "did it take the serial path?" is otherwise only answerable by timing.
    ///
    /// <para>Three things pin it to 1 regardless of <see cref="AnalysisSettings.MaxParallelism"/>:
    /// no <see cref="Library"/> was supplied (there is then nothing to elaborate a per-worker copy
    /// FROM, and a copy is the whole thread-safety story — a model writes state during
    /// <c>Stamp</c>); a netlist that may not be elaborated twice (an external device is a slot in a
    /// worker PROCESS, a control-referencing SDD resolves per netlist); and a LATTICE sideband
    /// family, whose difference-index memo and scratch buffer are written on first use and are not
    /// shared state anything may race on.</para>
    /// </summary>
    public int PlanSmallSignalDegree(int ssPointCount, HbSmallSignal.ISidebands sidebands)
    {
        int requested = _settings.MaxParallelism;
        if (requested == 1)                              return 1;
        if (_lib is null)                                return 1;
        if (sidebands is not HbSmallSignal.ToneSidebands) return 1;
        if (!SParameterEngine.CanElaborateInParallel(_netlist)) return 1;

        int cap = requested > 1 ? requested : Environment.ProcessorCount;
        return Math.Max(1, Math.Min(cap, ssPointCount / MinSsPointsPerWorker));
    }

    /// <summary>
    /// The tickle grid split into contiguous chunks, each on its own elaborated copy of the netlist,
    /// its own extractor and its own cache slice — R-wsp8-9. Each chunk writes only its own slice of
    /// <c>wsp</c> by index, so nothing is merged and the result is bit-identical to the serial run.
    /// </summary>
    private Complex[][,] SolveProbesInParallel(
        HbLinearExtractor extractor, HbSmallSignal.ISidebands sidebands,
        HbSmallSignal.ProbeSite[] probes, double[] grid, Complex[,] yDc,
        HbSmallSignal.Counters counters, string commensurateWith,
        HbSmallSignalCache cache, int degree)
    {
        var wsp     = new Complex[grid.Length][,];
        var nets    = new ElaboratedNetlist[degree];
        var exs     = new HbLinearExtractor[degree];
        var extras  = new ElaboratedNetlist?[degree - 1];
        var percore = new HbSmallSignal.Counters[degree];

        nets[0] = _netlist;
        exs[0]  = extractor;
        for (int i = 0; i < degree; i++) percore[i] = new HbSmallSignal.Counters();

        try
        {
            // Elaborated SERIALLY, before any worker starts: the Elaborator reads the TestBench's
            // global-variable list, which a parametric sweep is mutating around this very call.
            for (int i = 1; i < degree; i++)
            {
                var copy = new Elaborator(_lib!) { BaseDirectory = _baseDir }.Elaborate(_tb);
                extras[i - 1] = copy;
                nets[i] = copy;
                exs[i]  = new HbLinearExtractor(copy, _settings);
            }

            var faults  = new Exception?[degree];
            int faulted = 0;
            Func<bool> abort = () => Volatile.Read(ref faulted) != 0;

            Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, c =>
            {
                var (lo, hi) = ChunkRange(grid.Length, degree, c);
                try
                {
                    HbSmallSignal.SolveProbeRange(
                        nets[c], exs[c], sidebands, probes, grid, lo, hi, wsp, yDc,
                        percore[c], commensurateWith, cache, worker: c, abort);
                }
                catch (Exception ex)
                {
                    faults[c] = ex;
                    Volatile.Write(ref faulted, 1);
                }
            });

            // Chunks are contiguous and in order, so the lowest faulting CHUNK holds the lowest
            // faulting frequency — the point the serial loop would have died on.
            foreach (var fault in faults)
                if (fault is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();

            foreach (var copy in extras) if (copy is not null) _netlist.MergeDiagnosticsFrom(copy);
            foreach (var c in percore) Accumulate(counters, c);
            return wsp;
        }
        finally
        {
            foreach (var copy in extras) copy?.Dispose();
        }
    }

    /// <summary>Half-open range of probe-frequency indices chunk <paramref name="chunk"/> owns. The
    /// first <c>count % degree</c> chunks carry one extra point, so the split is contiguous and
    /// covers the grid exactly whatever the remainder.</summary>
    internal static (int Lo, int Hi) ChunkRange(int count, int degree, int chunk)
    {
        int size = count / degree, rem = count % degree;
        int lo   = chunk * size + Math.Min(chunk, rem);
        int hi   = lo + size + (chunk < rem ? 1 : 0);
        return (lo, Math.Min(hi, count));
    }

    /// <summary>Fold one worker's counters into the run's — R-wsp8-9(g)'s "each worker's counters
    /// sum to the serial counters".</summary>
    private static void Accumulate(HbSmallSignal.Counters into, HbSmallSignal.Counters from)
    {
        into.LinearPartitions      += from.LinearPartitions;
        into.DenseFactorizations   += from.DenseFactorizations;
        into.DenseSolves           += from.DenseSolves;
        into.SparseSolves          += from.SparseSolves;
        into.DegeneratePoints      += from.DegeneratePoints;
        into.SparseFactorizations  += from.SparseFactorizations;
        into.TransposedSolves      += from.TransposedSolves;
        into.Stamps                += from.Stamps;
        into.CacheHits             += from.CacheHits;
        into.CacheMisses           += from.CacheMisses;
    }

    /// <summary>
    /// R-wsp8-5's report: how many distinct frequencies the sidebands actually visit against how
    /// many they would if none coincided, and — when the grid is uniform — what the step is as a
    /// fraction of the fundamental. A grid whose step DIVIDES the fundamental shares sidebands
    /// between points and collapses the sparse work by that ratio.
    ///
    /// <para><b>The grid is never altered to achieve it.</b> A frequency the user wrote is a
    /// frequency the engine uses (the AUT-8 rule); this is said so a designer can choose an aligned
    /// step, not so the engine can choose one for them.</para>
    /// </summary>
    private void ReportSidebandCensus(
        (int Distinct, int Signed, int Total) census, double[] grid, HbSmallSignal.ISidebands sb)
    {
        if (census.Total == 0) return;

        string alignment = "";
        if (grid.Length > 1 && sb.Count > 1)
        {
            double step = grid[1] - grid[0];
            bool uniform = true;
            for (int i = 2; i < grid.Length && uniform; i++)
                uniform = Math.Abs((grid[i] - grid[i - 1]) - step) <= 1e-9 * Math.Abs(step);

            double f0 = (sb.Offset(sb.Zero + 1) - sb.Offset(sb.Zero)) / (2.0 * Math.PI);
            if (uniform && step > 0 && f0 > 0)
            {
                double m = f0 / step;
                alignment = Math.Abs(m - Math.Round(m)) < 1e-6 && Math.Round(m) >= 1
                    ? $" (the grid step is f0/{Math.Round(m):F0}, so the sidebands of one point are the sidebands of others)"
                    : " (the grid step does not divide f0, so no two points share a sideband; a step of f0/m would)";
            }
        }

        _netlist.AddNoteOnce("wsprobe.hb-sideband-census",
            $"WSProbe small-signal sweep: {census.Total} sideband visits landed on {census.Signed} " +
            $"distinct sideband frequencies, {census.Distinct} of them after folding ω < 0 onto " +
            $"|ω| — which is the number of linear-partition factorisations the whole drive sweep " +
            $"performs{alignment}. The grid itself is untouched.");
    }

    /// <summary>
    /// The linearisation of the most recent single-tone <see cref="Run"/>'s converged operating
    /// point: the interface admittances and Norton sources per harmonic, the one-sided
    /// full-amplitude device spectra, the <c>w ≥ 2</c> buckets, and the converged interface
    /// spectrum itself.
    ///
    /// <para>Exposed because these are exactly the pieces <see cref="HbNewton.BuildJ"/> and
    /// <see cref="HbSmallSignal.BuildJss"/> are each built from, and R-wsp5-9(b) compares the two
    /// matrices — the one comparison that pins the two-sided coefficient convention, the
    /// <c>jω_k</c> factor and the <c>Y_NN</c> placement all at once. Null after a run that produced
    /// no linearisation (a multi-tone run, or one whose solve reported none).</para>
    /// </summary>
    public sealed record LinearizedPoint(
        Complex[][,] YNN, Complex[][] ISrc, Complex[,,] G, Complex[,,] C,
        IReadOnlyList<HigherWeightBucket> Buckets, Complex[,] V,
        int InterfaceCount, int MaxHarmonic, double Omega0);

    /// <inheritdoc cref="LinearizedPoint"/>
    public LinearizedPoint? LastLinearizedPoint { get; private set; }

    /// <summary>
    /// The structural counters of the most recent small-signal solve — R-wsp5-9(i), which asserts
    /// the SHAPE of the work rather than its wall clock (overview D-14). Null when the run performed
    /// no small-signal solve. WSP-8 lowers these against the same fixture.
    /// </summary>
    public HbSmallSignal.Counters? LastSmallSignalCounters { get; private set; }

    /// <summary>
    /// R-wsp5-1's <c>SSMaxHarm</c> report: a truncated sideband order is stated whenever it is below
    /// the retained <c>K</c>, because a <c>wsp</c> computed over fewer sidebands than the operating
    /// point has harmonics is a different (cheaper, and less accurate) answer and nothing else in
    /// the result says so.
    /// </summary>
    private void NoteSidebandTruncation(HbAnalysisParams p, int sidebandCount)
    {
        if (p.SsOrder >= p.MaxHarmonic) return;
        _netlist.AddNoteOnce("wsprobe.hb-ss-maxharm",
            $"WSProbe small-signal sweep: SSMaxHarm = {p.SsOrder} truncates the conversion matrix " +
            $"to {sidebandCount} sidebands, below the {p.MaxHarmonic} harmonics the operating point " +
            "retains. The wsp entries are the cheaper answer; raise SSMaxHarm to MaxHarm to remove " +
            "the truncation.");
    }

    // ── DataSet builders (5-3) ────────────────────────────────────────────────

    /// <summary>
    /// Build the DataSet returned by single-tone Run().
    /// Always single-point: V/INl axes [node, harmonic]; Converged/Residual = scalar.
    /// I:* branch-current cubes have axis [harmonic].
    /// Node axis carries Labels = nodeNames for V("n_drain", …) lookups.
    /// Harmonic axis values = {0, f0, 2f0, …, K·f0} Hz.
    /// </summary>
    /// <summary>
    /// Adds the <c>OP</c> cube and its provenance twin to a harmonic-balance DataSet.
    ///
    /// <para><b>One cube on a labelled axis</b>, matching <c>V</c> on <c>node</c> and <c>I</c> on
    /// <c>branch</c> — not one cube per quantity. A compact model declares tens of these, so a
    /// handful of devices is hundreds of names; as separate cubes that is a DataSet nobody can
    /// navigate and a picker with no structure to group by.</para>
    ///
    /// <para><b>Complex, on the same spectral axis as everything else here</b>, because at a
    /// large-signal point an op-var is a waveform: k=0 is the cycle average — the number a designer
    /// means by "gm at this drive" — and the rest say how hard the quantity is being swung.</para>
    ///
    /// <para>Nothing is added when no device reported one, so a design with no compiled model gets
    /// exactly the DataSet it always did.</para>
    /// </summary>
    private static void AddOpVarCubes(
        DataSet ds, Dictionary<string, Complex[]> opVars, Axis spectralAxis, int spectralLength)
    {
        if (opVars.Count == 0) return;

        // Ordinal, so the axis order is stable across runs and across platforms; a picker whose rows
        // move between two runs of the same design is unusable.
        var names = opVars.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var idx   = new double[names.Length];
        var data  = new Complex[names.Length * spectralLength];

        for (int i = 0; i < names.Length; i++)
        {
            idx[i] = i;
            var spec = opVars[names[i]];
            for (int k = 0; k < spectralLength; k++)
                data[i * spectralLength + k] = k < spec.Length ? spec[k] : Complex.Zero;
        }

        var opAxis = new Axis("opvar", idx, "", names);
        ds.Add("OP", new DataCube([opAxis, spectralAxis], data));
        ds.Add("__OpVars", new DataCube(
            [new Axis("opvar", idx, "", names)], new double[names.Length]));
    }

    private static DataSet BuildSingleToneDataSet(
        Complex[,]  V,
        Complex[,]  iNl,
        string[]    nodeNames,
        double      f0,
        int         K,
        PointOutcome outcome,
        Dictionary<string, List<Complex[]>> portCurrents,
        HashSet<string>?    labeledNames = null,
        Dictionary<string, Complex[]>? probeCurrents = null,
        Dictionary<string, Complex[]>? opVars = null)
    {
        int N  = V.GetLength(0);
        int K1 = K + 1;

        var nodeVals = new double[N];
        for (int i = 0; i < N; i++) nodeVals[i] = i;
        var nodeAxis = new Axis("node", nodeVals, "", nodeNames);

        var harmVals = new double[K1];
        for (int k = 0; k < K1; k++) harmVals[k] = k;
        var harmAxis = new Axis("harmonic", harmVals, "");

        var vData   = new Complex[N * K1];
        var inlData = new Complex[N * K1];
        for (int n = 0; n < N; n++)
        for (int k = 0; k < K1; k++)
        {
            vData  [n * K1 + k] = V  [n, k];
            inlData[n * K1 + k] = iNl[n, k];
        }

        double conv = outcome.Converged ? 1.0 : 0.0;
        double res  = outcome.Residual;

        var ds = new DataSet();
        ds.Add("V",         new DataCube([nodeAxis, harmAxis], vData));
        ds.Add("INl",       new DataCube([nodeAxis, harmAxis], inlData));
        ds.Add("Converged", DataCube.Scalar(conv));
        ds.Add("Residual",  DataCube.Scalar(res));
        var toneAxis = new Axis("tone", [1.0], "");
        ds.Add("ToneFreqs", new DataCube([toneAxis], new double[] { f0 }));

        // Provenance: which node-axis entries came from a user net label (for the node-picker filter).
        if (labeledNames is not null)
        {
            var labeled = nodeNames.Where(n => labeledNames.Contains(n)).Distinct().ToArray();
            if (labeled.Length > 0)
            {
                var lIdx = Enumerable.Range(0, labeled.Length).Select(i => (double)i).ToArray();
                ds.Add("__LabeledNodes", new DataCube(
                    [new Axis("label", lIdx, "", labeled)],
                    new double[labeled.Length]));
            }
        }

        AddOpVarCubes(ds, opVars ?? [], harmAxis, K1);

        // Unified I [branch, harmonic] cube: probes first (labeled), then device ports.
        {
            var brNames = new List<string>();
            var brSpecs = new List<Complex[]>();

            string[] probeLabels = Array.Empty<string>();
            if (probeCurrents is { Count: > 0 })
            {
                probeLabels = probeCurrents.Keys.ToArray();
                foreach (var name in probeLabels)
                {
                    brNames.Add(name);
                    brSpecs.Add(probeCurrents[name]);
                }
            }
            foreach (var (key, specList) in portCurrents)
            {
                if (specList.Count == 0) continue;
                brNames.Add(key);
                brSpecs.Add(specList[0]);
            }

            if (brNames.Count > 0)
            {
                int B         = brNames.Count;
                var bVals     = Enumerable.Range(0, B).Select(i => (double)i).ToArray();
                var branchAxis = new Axis("branch", bVals, "", brNames.ToArray());
                var iData     = new Complex[B * K1];
                for (int b = 0; b < B; b++)
                {
                    var spec = brSpecs[b];
                    for (int k = 0; k < K1; k++) iData[b * K1 + k] = k < spec.Length ? spec[k] : Complex.Zero;
                }
                ds.Add("I", new DataCube([branchAxis, harmAxis], iData));

                if (probeLabels.Length > 0)
                {
                    var pIdx = Enumerable.Range(0, probeLabels.Length).Select(i => (double)i).ToArray();
                    ds.Add("__ProbeBranches", new DataCube(
                        [new Axis("probe", pIdx, "", probeLabels)], new double[probeLabels.Length]));
                }
            }
        }

        return ds;
    }

    /// <summary>
    /// Build the DataSet returned by two-tone Run().
    /// Always single-point: V/INl axes [node, mixIndex] (Complex); Converged/Residual = scalar.
    /// MixIndex axis values = {k1·f1+k2·f2} Hz per product, Labels = {"(k1,k2)"}.
    /// ToneFreqs cube has axis [tone] (Real): {f1, f2} Hz.
    /// MetaMixOrder cube has axis [1] (Real): {MaxMixOrder}.
    /// I cube has axes [branch, mixIndex] for device-port currents (no IProbe back-solver for two-tone yet).
    /// </summary>
    private static DataSet BuildTwoToneDataSet(
        Complex[,]  V,
        Complex[,]  iNl,
        string[]    nodeNames,
        MixingGrid  grid,
        double      f1,
        double      f2,
        PointOutcome outcome,
        Dictionary<string, List<Complex[]>> portCurrentsByBranch,
        HashSet<string>?    labeledNames = null,
        Dictionary<string, Complex[]>? probeCurrents = null,
        Dictionary<string, Complex[]>? opVars = null)
    {
        int N = V.GetLength(0);
        int M = grid.MixCount;

        var nodeVals = new double[N];
        for (int i = 0; i < N; i++) nodeVals[i] = i;
        var nodeAxis = new Axis("node", nodeVals, "", nodeNames);

        var mixVals   = new double[M];
        var mixLabels = new string[M];
        for (int m = 0; m < M; m++)
        {
            var (k1, k2) = grid.ToneOf(m);
            mixVals[m]   = k1 * f1 + k2 * f2;
            mixLabels[m] = $"({k1},{k2})";
        }
        var mixAxis = new Axis("mixIndex", mixVals, "Hz", mixLabels);

        var vData   = new Complex[N * M];
        var inlData = new Complex[N * M];
        for (int n = 0; n < N; n++)
        for (int m = 0; m < M; m++)
        {
            vData  [n * M + m] = V  [n, m];
            inlData[n * M + m] = iNl[n, m];
        }

        double conv = outcome.Converged ? 1.0 : 0.0;
        double res  = outcome.Residual;

        var toneAxis  = new Axis("tone", [1.0, 2.0], "");
        var orderAxis = new Axis("order", [1.0], "");

        var ds = new DataSet();
        ds.Add("V",            new DataCube([nodeAxis, mixAxis], vData));
        ds.Add("INl",          new DataCube([nodeAxis, mixAxis], inlData));
        ds.Add("Converged",    DataCube.Scalar(conv));
        ds.Add("Residual",     DataCube.Scalar(res));
        ds.Add("ToneFreqs",    new DataCube([toneAxis], new double[] { f1, f2 }));
        ds.Add("MetaMixOrder", new DataCube([orderAxis], new double[] { grid.MaxMixOrder }));

        if (labeledNames is not null)
        {
            var labeled = nodeNames.Where(n => labeledNames.Contains(n)).Distinct().ToArray();
            if (labeled.Length > 0)
            {
                var lIdx = Enumerable.Range(0, labeled.Length).Select(i => (double)i).ToArray();
                ds.Add("__LabeledNodes", new DataCube(
                    [new Axis("label", lIdx, "", labeled)],
                    new double[labeled.Length]));
            }
        }

        AddOpVarCubes(ds, opVars ?? [], mixAxis, M);

        // Unified I [branch, mixIndex] cube: IProbe currents first (labeled, back-solved over the
        // mixing lattice), then device-port currents. Mirrors BuildSingleToneDataSet.
        {
            var brNames = new List<string>();
            var brSpecs = new List<Complex[]>();

            string[] probeLabels = Array.Empty<string>();
            if (probeCurrents is { Count: > 0 })
            {
                probeLabels = probeCurrents.Keys.ToArray();
                foreach (var name in probeLabels) { brNames.Add(name); brSpecs.Add(probeCurrents[name]); }
            }
            foreach (var (key, specList) in portCurrentsByBranch)
            {
                if (specList.Count == 0) continue;
                brNames.Add(key);
                brSpecs.Add(specList[0]);
            }
            if (brNames.Count > 0)
            {
                int B         = brNames.Count;
                var bVals     = Enumerable.Range(0, B).Select(i => (double)i).ToArray();
                var branchAxis = new Axis("branch", bVals, "", brNames.ToArray());
                var iData     = new Complex[B * M];
                for (int b = 0; b < B; b++)
                {
                    var spec = brSpecs[b];
                    for (int m = 0; m < M; m++) iData[b * M + m] = m < spec.Length ? spec[m] : Complex.Zero;
                }
                ds.Add("I", new DataCube([branchAxis, mixAxis], iData));

                if (probeLabels.Length > 0)
                {
                    var pIdx = Enumerable.Range(0, probeLabels.Length).Select(i => (double)i).ToArray();
                    ds.Add("__ProbeBranches", new DataCube(
                        [new Axis("probe", pIdx, "", probeLabels)], new double[probeLabels.Length]));
                }
            }
        }

        return ds;
    }

    /// <summary>
    /// T-tone result cubes. Deliberately the SAME shape as <see cref="BuildTwoToneDataSet"/> —
    /// same cube names, same <c>node</c>/<c>mixIndex</c>/<c>branch</c> axis names, and a
    /// <c>mixIndex</c> axis whose VALUES are the signed product frequencies in Hz and whose
    /// LABELS are the product tags.
    ///
    /// <para>That sameness is the point. The data display keys its spectrum rendering off the
    /// axis NAME, positions stems from the axis VALUES, and prints the axis LABEL verbatim, so
    /// widening the tag from <c>"(k1,k2)"</c> to <c>"(k1,…,kT)"</c> is the only difference a
    /// T-tone result presents — and the two-tone spectrum path, which is frozen, is not touched
    /// at all.</para>
    /// </summary>
    private static DataSet BuildMultiToneDataSet(
        Complex[,]    V,
        Complex[,]    iNl,
        string[]      nodeNames,
        MixingLattice lattice,
        double[]      toneFreqsHz,
        PointOutcome outcome,
        Dictionary<string, List<Complex[]>> portCurrentsByBranch,
        HashSet<string>?    labeledNames = null,
        Dictionary<string, Complex[]>? probeCurrents = null,
        Dictionary<string, Complex[]>? opVars = null)
    {
        int N = V.GetLength(0);
        int M = lattice.MixCount;

        var nodeVals = new double[N];
        for (int i = 0; i < N; i++) nodeVals[i] = i;
        var nodeAxis = new Axis("node", nodeVals, "", nodeNames);

        var mixVals   = new double[M];
        var mixLabels = new string[M];
        for (int m = 0; m < M; m++)
        {
            mixVals[m]   = lattice.FrequencyOf(m, toneFreqsHz);
            mixLabels[m] = lattice.Label(m);
        }
        var mixAxis = new Axis("mixIndex", mixVals, "Hz", mixLabels);

        var vData   = new Complex[N * M];
        var inlData = new Complex[N * M];
        for (int n = 0; n < N; n++)
        for (int m = 0; m < M; m++)
        {
            vData  [n * M + m] = V  [n, m];
            inlData[n * M + m] = iNl[n, m];
        }

        double conv = outcome.Converged ? 1.0 : 0.0;
        double res  = outcome.Residual;

        int T = toneFreqsHz.Length;
        var toneVals  = new double[T];
        for (int t = 0; t < T; t++) toneVals[t] = t + 1;
        var toneAxis  = new Axis("tone", toneVals, "");
        var orderAxis = new Axis("order", [1.0], "");

        var ds = new DataSet();
        ds.Add("V",            new DataCube([nodeAxis, mixAxis], vData));
        ds.Add("INl",          new DataCube([nodeAxis, mixAxis], inlData));
        ds.Add("Converged",    DataCube.Scalar(conv));
        ds.Add("Residual",     DataCube.Scalar(res));
        ds.Add("ToneFreqs",    new DataCube([toneAxis], (double[])toneFreqsHz.Clone()));
        ds.Add("MetaMixOrder", new DataCube([orderAxis], new double[] { lattice.MaxMixOrder }));

        if (labeledNames is not null)
        {
            var labeled = nodeNames.Where(n => labeledNames.Contains(n)).Distinct().ToArray();
            if (labeled.Length > 0)
            {
                var lIdx = Enumerable.Range(0, labeled.Length).Select(i => (double)i).ToArray();
                ds.Add("__LabeledNodes", new DataCube(
                    [new Axis("label", lIdx, "", labeled)],
                    new double[labeled.Length]));
            }
        }

        AddOpVarCubes(ds, opVars ?? [], mixAxis, M);

        // Unified I [branch, mixIndex] cube: IProbe currents first (labeled, back-solved over the
        // lattice), then device-port currents. Mirrors BuildTwoToneDataSet / BuildSingleToneDataSet.
        {
            var brNames = new List<string>();
            var brSpecs = new List<Complex[]>();

            string[] probeLabels = Array.Empty<string>();
            if (probeCurrents is { Count: > 0 })
            {
                probeLabels = probeCurrents.Keys.ToArray();
                foreach (var name in probeLabels) { brNames.Add(name); brSpecs.Add(probeCurrents[name]); }
            }
            foreach (var (key, specList) in portCurrentsByBranch)
            {
                if (specList.Count == 0) continue;
                brNames.Add(key);
                brSpecs.Add(specList[0]);
            }
            if (brNames.Count > 0)
            {
                int B         = brNames.Count;
                var bVals     = Enumerable.Range(0, B).Select(i => (double)i).ToArray();
                var branchAxis = new Axis("branch", bVals, "", brNames.ToArray());
                var iData     = new Complex[B * M];
                for (int b = 0; b < B; b++)
                {
                    var spec = brSpecs[b];
                    for (int m = 0; m < M; m++) iData[b * M + m] = m < spec.Length ? spec[m] : Complex.Zero;
                }
                ds.Add("I", new DataCube([branchAxis, mixAxis], iData));

                if (probeLabels.Length > 0)
                {
                    var pIdx = Enumerable.Range(0, probeLabels.Length).Select(i => (double)i).ToArray();
                    ds.Add("__ProbeBranches", new DataCube(
                        [new Axis("probe", pIdx, "", probeLabels)], new double[probeLabels.Length]));
                }
            }
        }

        return ds;
    }

    // ── Control-current branch resolution (HB) — brief #2 ───────────────────

    /// <summary>
    /// Re-resolve SDD ControlBranchIndices against the HB extractor's MNA.
    /// The extractor has already stamped all linear devices, so LastBranchIndex /
    /// PortBranchIndices are HB-valid. Asserts each index is in [0, mnaSize).
    /// </summary>
    private static void ResolveControlBranchIndicesHb(
        HbLinearExtractor extractor, ElaboratedNetlist netlist)
    {
        int mnaSize = extractor.MnaSize;
        foreach (var ec in netlist.Components)
        {
            if (ec.Model is not SddModel sdd || sdd.ControlRefs.Length == 0) continue;
            for (int i = 0; i < sdd.ControlRefs.Length; i++)
            {
                var (n, refInst, port) = sdd.ControlRefs[i];
                ElaboratedComponent? target = null;
                foreach (var tec in netlist.Components)
                    if (string.Equals(tec.InstancePath, refInst, StringComparison.Ordinal))
                    { target = tec; break; }
                if (target is null)
                    throw new InvalidOperationException(
                        $"HB: SDD '{sdd.Name}': C[{n}]={refInst} — no component named '{refInst}'.");
                int brIdx = GetControlBranchIndexHb(sdd.Name, n, port, target);
                if (brIdx < 0 || (mnaSize > 0 && brIdx >= mnaSize))
                    throw new InvalidOperationException(
                        $"HB: SDD '{sdd.Name}': C[{n}] branch index {brIdx} " +
                        $"out of HB MNA range [0, {mnaSize}).");
                sdd.ControlBranchIndices[i] = brIdx;
            }
        }
    }

    private static int GetControlBranchIndexHb(
        string sddName, int n, int port, ElaboratedComponent target)
    {
        const string Allowed = "Vdc, V_1Tone/V_nTone, IProbe, WSProbe, L (Inductor), SRLC, PRLC, SRL, SLC, PRL, PLC, SnP, Z_Port";
        return target.Model switch
        {
            VdcModel        vdc  => ValidateSinglePortBranchHb(sddName, n, port, vdc.LastBranchIndex,  "Vdc"),
            ToneSourceModel tone => ValidateSinglePortBranchHb(sddName, n, port, tone.LastBranchIndex, "V_1Tone/V_nTone"),
            // An ideal current source's current is an INPUT, not a solved unknown — it allocates no
            // branch to point at. Named explicitly because "the other tone source works" is exactly
            // the wrong inference to leave the user to draw from a generic list of allowed kinds.
            CurrentToneSourceModel => throw new InvalidOperationException(
                $"HB: SDD '{sddName}': C[{n}]={target.InstancePath} is an ideal current source (I_1Tone/I_nTone): " +
                $"its current is an input, not a solved unknown, so it has no branch to reference. " +
                $"Put an IProbe in series with it and reference that instead."),
            SeriesProbeModelBase probe => ValidateSinglePortBranchHb(sddName, n, port, probe.LastBranchIndex, target.ComponentType),
            // L, SRLC and PRLC — every model carrying an inductor branch (IInductiveBranch).
            IInductiveBranch ind => ValidateSinglePortBranchHb(sddName, n, port, ind.LastBranchIndex, target.ComponentType),
            SnpModel    snp   => ValidateMultiPortBranchHb(sddName, n, port, snp.PortBranchIndices,  "SnP"),
            ZPortModel  zp    => ValidateMultiPortBranchHb(sddName, n, port, zp.PortBranchIndices,   "Z_Port"),
            _ => throw new InvalidOperationException(
                $"HB: SDD '{sddName}': C[{n}]={target.InstancePath} references " +
                $"'{target.ComponentType}'; allowed: {Allowed}.")
        };
    }

    private static int ValidateSinglePortBranchHb(
        string sddName, int n, int port, int branchIdx, string kind)
    {
        if (port > 1)
            throw new InvalidOperationException(
                $"HB: SDD '{sddName}': Cport[{n}]={port} — {kind} is two-terminal; " +
                $"Cport must be absent or 1.");
        if (branchIdx < 0)
            throw new InvalidOperationException(
                $"HB: SDD '{sddName}': C[{n}] — {kind} branch index not assigned; " +
                $"ensure the referenced device is stamped before resolution.");
        return branchIdx;
    }

    private static int ValidateMultiPortBranchHb(
        string sddName, int n, int port, int[] indices, string kind)
    {
        if (port == 0)
            throw new InvalidOperationException(
                $"HB: SDD '{sddName}': C[{n}] references {kind}; Cport[{n}]=<port> is required.");
        if (port < 1 || port > indices.Length)
            throw new InvalidOperationException(
                $"HB: SDD '{sddName}': Cport[{n}]={port} out of range for {kind} " +
                $"with {indices.Length} port(s). Valid: 1..{indices.Length}.");
        if (indices[port - 1] < 0)
            throw new InvalidOperationException(
                $"HB: SDD '{sddName}': C[{n}] — {kind} port {port} branch index not assigned.");
        return indices[port - 1];
    }

    // ── Commensurability check (harmonic-balance.md §3.1) ────────────────────

    /// <summary>
    /// Hands a <see cref="TunerModel"/> the band ruler its own per-harmonic <c>Z[k]</c> lookup needs.
    ///
    /// <para><b>Without this a Tuner presents Z[1] AT EVERY HARMONIC under a plain HB run, silently.</b>
    /// <c>TunerModel.GetZ</c> branches on <c>_toneFreqHz &lt;= 0</c> — its "S-param mode", where a
    /// termination has no harmonics to speak of and the declared fundamental is the only sensible flat
    /// answer. That field was only ever set by <c>LoadpullEngine</c>/<c>LoadpullPursuitEngine</c>, so a
    /// Tuner placed on an ordinary <c>type=hb</c> testbench could declare <c>Z[2]</c>, <c>Z[3]</c>… and
    /// have them quietly ignored. Found while exporting harmonicaRF's own load termination as a
    /// <c>LoadTuner</c> (owner, Round 10): the exported schematic ran and answered for a different
    /// circuit, which is the worst shape a defect can take.</para>
    ///
    /// <para><b>Role-gated to Load, and that is not merely defensive.</b> A Source-role tuner's
    /// <c>StampSource</c> stamps a <c>V_1Tone</c> drive branch as soon as <c>_toneFreqHz &gt; 0</c>, at
    /// a <c>|Vs|</c> that only <c>SetSourceDrive</c> ever computes — so setting a tone on one whose
    /// drive has not been configured would stamp a 0 V source, i.e. a SHORT where there was an open.
    /// Nothing outside the loadpull engines assigns a role, so every tuner on a plain HB testbench is
    /// already <see cref="TunerRole.Load"/> and this gate costs nothing today; it is what keeps the
    /// change from reaching into the source path if that ever stops being true.</para>
    ///
    /// <para>This is <c>Run</c>/<c>RunTwoTone</c> only. The loadpull path goes through
    /// <see cref="RunSinglePoint"/>, which has no tone-context pass of its own precisely because
    /// <c>LoadpullEngine.PrepareContext</c> has already set the roles, the tone and the drive.</para>
    /// </summary>
    private static void GiveTunerItsBandRuler(TunerModel tuner, double bandCenterHz)
    {
        if (tuner.Role == TunerRole.Load) tuner.SetTone(bandCenterHz);
    }

    private void CheckCommensurability(double f0, int K)
    {
        const double Tol = 1.0;  // 1 Hz tolerance on frequency matching

        foreach (var ec in _netlist.Components)
        {
            // Collect the tone frequencies this source contributes (PnTone has several).
            IEnumerable<double> freqs;
            if (ec.Model is ToneSourceModelBase)
                freqs = ec.Parameters.TryGetValue("Freq", out var fv) && fv.Kind == ValueKind.Real
                    ? [fv.AsReal()] : [];
            else if (ec.Model is P1ToneModel p1) freqs = [p1.FreqHz];
            else if (ec.Model is PnToneModel pn) freqs = pn.ToneFreqsHz;
            else continue;

            foreach (double freqHz in freqs)
            {
                if (freqHz == 0) continue;  // DC-only source, no tone

                // Check: freqHz = m * f0 for some integer m in 1..K.
                double ratio    = freqHz / f0;
                double nearestK = Math.Round(ratio);
                if (Math.Abs(ratio - nearestK) * f0 > Tol || nearestK < 1 || nearestK > K)
                    throw new InvalidOperationException(
                        $"Commensurability check failed: source '{ec.InstancePath}' " +
                        $"Freq={freqHz:G6} Hz is not on the HB tone grid " +
                        $"{{f0={f0:G6} Hz, MaxHarm={K}}}" +
                        UnitMismatchHint(f0, freqHz) +
                        SweptToneHint(freqHz));
            }
        }
    }

    /// <summary>
    /// The specific way this check fires under a frequency sweep: the SOURCE's Freq follows a swept
    /// global (<c>Freq=RFfreq GHz</c>) while the analysis's own Tone is a fixed number, so the tone
    /// grid stays where it started and every point past the first is off-grid. The generic message
    /// names the source — which is the half that is right — so without this the reader goes looking
    /// at the source. Names the global whose CURRENT value the source is sitting on, and the fix.
    /// </summary>
    private string SweptToneHint(double freqHz)
    {
        const double Rel = 1e-9;
        foreach (var (name, val) in _netlist.ResolvedGlobals)
        {
            if (val.Kind != ValueKind.Real) continue;
            double v = val.AsReal();
            if (v == 0) continue;
            // The global may be Hz-valued already, or unit-less with the unit applied at the use site
            // (Freq=RFfreq GHz) — accept either spelling.
            foreach (double scale in new[] { 1.0, 1e3, 1e6, 1e9 })
                if (Math.Abs(v * scale - freqHz) <= Math.Abs(freqHz) * Rel)
                    return $" — the source's frequency follows the variable '{name}' (now {freqHz:G6} Hz) "
                         + "while this analysis's Tone is fixed; set the analysis Tone to "
                         + $"'{name}' so the tone grid follows the sweep.";
        }
        return "";
    }

    private static string UnitMismatchHint(double f0, double freqHz)
    {
        if (FreqUnit.LooksLikeUnitMismatch(f0, freqHz) == 0) return "";
        return " — this looks like a frequency-unit mismatch (off by ~1000×ⁿ); check the Tone unit and your variable's units.";
    }
}
