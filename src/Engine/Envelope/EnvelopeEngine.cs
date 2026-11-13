// ================================================================
//  EnvelopeEngine.cs  —  circuit-envelope analysis (single fundamental)
//
//  Each envelope step is one harmonic-balance solve on the interface
//  spectrum V[N, K+1] with a time derivative added to every charge:
//
//    linear:    (G + (j k w0 + alpha) C) x_k(n) = b_k(n) - C (b1 x_k(n-1) + b2 x_k(n-2))
//    devices:   i(V) + (j k w0) q(V) + alpha q(V) + b1 q(n-1) + b2 q(n-2)
//
//  Gear-2 (BDF2) with backward Euler on the first step.  For lumped
//  (affine-in-jw) linear models the s-domain stamp is exact: C is
//  recovered from two stamps at k w0 +- delta.  A two-conductor line is
//  stepped as its delay (EnvelopeLineDelay): its delayed waves enter the
//  right-hand side, and the current step's share of them, when the line
//  is shorter than a step, the stepped matrix.  An S-parameter block, a
//  System Filter and any other element whose stamp is not affine (a
//  Z(freq) block) is replaced for the run by a rational fit that is
//  affine by construction (EnvelopeSnpFit).  One whose fit is refused is
//  approximated to first order around each harmonic (its value and slope
//  there); the run reports how far that is from its true stamp across the
//  signal band, matrix-wide and per element (EnvelopeTangentCheck), and
//  warns naming the element.
//
//  The sparse factorization of G + (j k w0 + alpha) C is done ONCE per
//  harmonic per run (alpha is fixed by the step), so a step costs one
//  sparse back-substitution per harmonic for the history, a small dense
//  Newton on the interface (chord: the Jacobian LU is reused while the
//  contraction stays fast) and a rank-N update of the linear state.
//
//  The device Jacobian/residual come from HbNewton's own BuildJ/BuildF:
//  passing G + alpha*C for the conductance spectra adds exactly the
//  alpha*C conversion block (same weights, same DC handling), and the
//  envelope charge term rides in the nonlinear-current argument — so
//  harmonic balance itself is untouched.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.HarmonicBalance;
using CSparse;
using CSparse.Complex;
using CSparse.Complex.Factorization;
using CSparse.Storage;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

public sealed record EnvelopeParams
{
    /// <summary>Simulated time (s) after the start point.</summary>
    public double StopS { get; init; } = 100e-6;
    /// <summary>Explicit envelope step (s); 0 = automatic from the signal rate x <see cref="Oversample"/>.</summary>
    public double StepS { get; init; }
    /// <summary>Steps per signal sample when the step is automatic (1 draft, 4 standard, 8 fine); a run takes more for a
    /// carrier off the envelope's centre (<see cref="EnvelopeEngine.Oversample"/>).</summary>
    public int Oversample { get; init; } = 4;
    public int MaxHarm { get; init; } = 5;
    public int FftOverSample { get; init; } = 1;
    public double Tol { get; init; } = 1e-6;
    /// <summary>Nodes an old file's retired <c>Probes</c> list names: they join the recorded voltages, which are every
    /// labeled net (brief ENV-MEAS-3).</summary>
    public string[] Probes { get; init; } = Array.Empty<string>();
    /// <summary>Signal-record offset of t = 0 (s) — lets a segment start mid-record.</summary>
    public double StartS { get; init; }
    /// <summary>Warm-up simulated before <see cref="StartS"/> and discarded (s).</summary>
    public double WarmupS { get; init; }
    /// <summary>Inject thermal noise (brief NZ-7, <see cref="EnvelopeNoise"/>).</summary>
    public bool Noise { get; init; }
    /// <summary>The seed of every random draw: thermal noise and the sources' phase-noise masks.</summary>
    public ulong Seed { get; init; } = 1;
    /// <summary>Apply the sources' <c>PhaseNoise</c> masks. False only for <c>EvmSplit</c>'s second, noiseless run (brief
    /// NZ-8).</summary>
    public bool PhaseNoise { get; init; } = true;
    /// <summary>Newton updates every step takes before its tolerance may accept (<see cref="EnvelopeNewton.Solve"/>): 0,
    /// except EvmSplit's noiseless run.</summary>
    public int MinNewton { get; init; }
}

public sealed class EnvelopeRunResult
{
    public required double[] Time { get; init; }
    public required string[] ProbeNames { get; init; }
    /// <summary>[step][probe][harmonic] complex (peak phasors).</summary>
    public required Complex[][][] Probe { get; init; }
    public required string[] SupplyNames { get; init; }
    /// <summary>[step][supply] DC supply branch current (A).</summary>
    public required double[][] SupplyCurrent { get; init; }
    public required double[] SupplyVoltage { get; init; }
    /// <summary>Every IProbe in the netlist (a WSProbe is not one), by instance path.</summary>
    public required string[] IProbeNames { get; init; }
    /// <summary>[step][iprobe][harmonic] the probe's branch current (A, peak phasors), flowing from its first node to its second.</summary>
    public required Complex[][][] IProbeCurrent { get; init; }
    /// <summary>[step][iprobe][harmonic] the voltage at the probe's first node (an IProbe is 0 V, so at both).</summary>
    public required Complex[][][] IProbeVoltage { get; init; }
    /// <summary>Every wBond that solves its wire temperature, by instance path; empty when none does.</summary>
    public string[] WBondPaths { get; init; } = [];
    /// <summary>[step][wBond][product · arrays + array] each array's branch current (A, peak phasors), product 0 real
    /// (<see cref="EnvelopeWireTemperature"/>).</summary>
    public Complex[][][] WBondCurrent { get; init; } = [];
    /// <summary>Instantaneous available-drive phasor (V peak) at the fundamental: one row per MOD source, or on the lattice
    /// one per (source, tone group), row s·T + t. Rows are different sources or different products, so their powers add.</summary>
    public required Complex[][] Drive { get; init; }
    /// <summary>Re Z of the source each <see cref="Drive"/> row belongs to.</summary>
    public required double[] DriveRe { get; init; }
    public required double F0Hz { get; init; }
    public required double StepS { get; init; }
    public required int MaxHarm { get; init; }
    public required double SignalRateHz { get; init; }
    public required ModSourceModel[] Sources { get; init; }
    public double WallSeconds { get; init; }
    public double MeanNewton { get; init; }
    public int JacobianBuilds { get; init; }
    public double WorstTangentError { get; init; }
    /// <summary>The SnP blocks the run replaced by a rational fit (EnvelopeSnpFit).</summary>
    public IReadOnlyList<SnpFitReport> SnpFits { get; init; } = [];
    /// <summary>Per carrier (sources in order, then their carriers): index on the harmonic/mix axis whose envelope carries it.</summary>
    public int[] CarrierMix { get; init; } = Array.Empty<int>();
    /// <summary>Per carrier: offset (Hz) of the carrier from that product's frequency.</summary>
    public double[] CarrierOffsetHz { get; init; } = Array.Empty<double>();
    public string[] CarrierLabels { get; init; } = Array.Empty<string>();
    /// <summary>Multi-fundamental runs: the tones, and one label + signed frequency per mixing product.</summary>
    public double[]? ToneFreqsHz { get; init; }
    public string[]? MixLabels { get; init; }
    public double[]? MixFreqHz { get; init; }
    public int[][]? MixTones { get; init; }
    public List<string> Notes { get; } = new();
}

public sealed class EnvelopeEngine
{
    private readonly ElaboratedNetlist _nl;
    private readonly AnalysisSettings _settings;

    public EnvelopeEngine(ElaboratedNetlist netlist, AnalysisSettings? settings = null)
    {
        _nl = netlist;
        _settings = settings ?? new AnalysisSettings();
    }

    /// <summary>What one envelope Newton evaluation hands on: the charge and total-injection spectra the step keeps,
    /// and the Jacobian ingredients.</summary>
    private sealed record StepEval(Complex[,] Q, Complex[,] Inj, Complex[,,] G, Complex[,,] Cc,
                                   IReadOnlyList<HigherWeightBucket> Buckets);

    /// <summary>One product's linear network factored at one <c>alpha</c>: <c>G + (j w + alpha) C</c>.</summary>
    internal sealed class Stage
    {
        public SparseLU Lu = null!;
        public Complex[][] Zcol = null!;                     // [port][row]
        public Complex[,] Y = null!;
        public Complex[] Xs = null!;                         // static-source solution
        public Complex[][] Xu = null!;                       // [source][row] unit-drive solution

        public static Stage Of((SparseLU lu, Complex[][] zcol, Complex[,] Y, Complex[] xs, Complex[][] xu) t)
            => new() { Lu = t.lu, Zcol = t.zcol, Y = t.Y, Xs = t.xs, Xu = t.xu };
    }

    private sealed class Product
    {
        public double Omega;
        public CompressedColumnStorage<Complex> A0 = null!, C = null!;
        public Complex[] BStatic = null!;
        public Complex[][] BUnit = null!;
        public Stage Be = null!, G2 = null!;
        /// <summary>Backward Euler at h/m, factored the first time a step is split m ways.</summary>
        public readonly Dictionary<int, Stage> Split = new();
        public Complex[] Xm1 = null!, Xm2 = null!;             // state history
    }

    /// <summary>How finely a step whose Newton failed may be split before the run gives up.</summary>
    internal const int MaxStepSplit = 16;

    /// <summary>
    /// The fewest steps a run takes per cycle of the highest frequency in any carrier's envelope, its band edge
    /// (<see cref="Oversample"/>). The signal rate is at least five bandwidths, so a carrier at the envelope's centre gets
    /// ten at <c>draft</c>; this gives a carrier off the centre at least as many. It bounds the step at the band edge,
    /// not the error: 20 would cost up to 7x at draft, and no fixed count suits every filter's delay.
    /// </summary>
    internal const double StepsPerEdgeCycle = 10;

    /// <summary>
    /// The steps per signal sample a run takes: the accuracy preset's, or more when a carrier's band edge
    /// <paramref name="edgeHz"/> (its offset from the envelope's centre plus half its bandwidth) would get fewer than
    /// <see cref="StepsPerEdgeCycle"/> steps a cycle. Gear-2 steps an envelope frequency f as
    /// jω(1 + θ²/3 − 7θ⁴/60) + θ⁴/(4h), θ = 2π f h. Its damping θ⁴/(4h), through a filter's group delay τ, scales the gain
    /// by e^(−σ τ), a tilt across a carrier off the centre; the warp's f³ phase is the smaller part. The signal rate only
    /// holds an off-centre carrier's spectrum: a 5 MHz NR carrier 4.5 MHz off the centre of a draft run, at
    /// 2π·7 MHz·16.3 ns = 0.72 rad, read 1.1 % EVM through a 60 MHz bandpass where the same carrier centred reads 0.38 %,
    /// and reads 0.39 % at this rule's step. What is left still grows with the offset and with the filter's delay (the
    /// carrier centred in that bandpass, the envelope's centre moved: 0.69 % at 3.5 MHz, where the rule does not act,
    /// and 1.5 % at 22 MHz, at draft), so EVM through a filter is read at standard or fine, or with Fc on the carrier.
    /// A carrier at the centre, every carrier at <c>fine</c> and nearly every one at <c>standard</c> keep their preset,
    /// bit for bit.
    /// </summary>
    internal static int Oversample(int preset, double rateHz, double edgeHz)
        => Math.Max(Math.Max(1, preset), (int)Math.Ceiling(StepsPerEdgeCycle * edgeHz / rateHz - 1e-9));

    /// <summary>
    /// The Newton tolerance is at most this fraction of the run's RF excitation (<see cref="RunTolerance"/>). Tol is absolute,
    /// in amperes at the nonlinear interface; at 1e-6 A it was ~1 % of a -37 dBm signal's current, which a noiseless run
    /// then read as distortion (an amplifier far below compression showed NMSE -32 dB, -36 dB once converged). 1e-4 of the
    /// excitation leaves every run whose excitation is 10 mA or more on Tol exactly — bit for bit — and tightens a small one.
    /// </summary>
    internal const double RelativeTol = 1e-4;

    /// <summary>The tolerance's floor, as a multiple of the largest Norton current in the run (<see cref="RunTolerance"/>): four
    /// machine epsilons, where a biased stage behind ideal chokes was measured to converge at under one.</summary>
    internal const double RoundOffFloor = 2.220446049250313e-16;

    /// <summary>The fraction of the excitation above which a round-off-held tolerance is reported: the signal is then solved
    /// to no better than 1 % of its own level, and EVM, ACLR and distortion read it.</summary>
    internal const double FloorWarnFraction = 1e-2;

    /// <summary>
    /// <see cref="RunTolerance(double, Complex[][], Func{int, bool})"/>, warning once on <paramref name="nl"/> when round-off
    /// holds the tolerance above <see cref="FloorWarnFraction"/> of the excitation.
    /// </summary>
    internal static double RunTolerance(double tol, Complex[][] iSrc, Func<int, bool> isRf, ElaboratedNetlist nl)
    {
        double t = RunTolerance(tol, iSrc, isRf, out double scale);
        if (scale > 0 && t > FloorWarnFraction * scale && t < tol)
            nl.AddWarningOnce(EnvelopeToneDiagnostics.RoundOffFloorKey, EnvelopeToneDiagnostics.RoundOffFloor(t, scale).Render());
        return t;
    }

    /// <summary>
    /// The tolerance a run's steps and its harmonic-balance start are solved to: <paramref name="tol"/>, or
    /// <see cref="RelativeTol"/> of the RF excitation <paramref name="iSrc"/> (the products <paramref name="isRf"/> names)
    /// when that is smaller. The excitation is the record's RMS drive, not the drive at t = 0, which may be zero.
    /// </summary>
    internal static double RunTolerance(double tol, Complex[][] iSrc, Func<int, bool> isRf) => RunTolerance(tol, iSrc, isRf, out _);

    private static double RunTolerance(double tol, Complex[][] iSrc, Func<int, bool> isRf, out double scale)
    {
        double sum = 0;
        for (int m = 0; m < iSrc.Length; m++)
        {
            if (!isRf(m) || iSrc[m] is not { } col) continue;
            foreach (var v in col) sum += v.Real * v.Real + v.Imaginary * v.Imaginary;
        }
        scale = Math.Sqrt(sum);
        if (!(scale > 0) || RelativeTol * scale >= tol) return tol;
        // Never below what the solve can reach: the residual sums currents as large as the biggest Norton current (a supply
        // through an ideal choke regularised to ~1 µΩ is ~1e6 A per volt), and round-off on those leaves a floor that a
        // small enough signal's relative tolerance would sit under — and every step would then fail to converge.
        double big = 0;
        foreach (var col in iSrc)
            if (col is not null)
                foreach (var v in col) big = Math.Max(big, v.Magnitude);
        return Math.Min(tol, Math.Max(RelativeTol * scale, RoundOffFloor * big));
    }

    /// <summary>The RMS magnitude of a drive window: the level the run's tolerance is set against.</summary>
    internal static double Rms(Complex[] x)
    {
        if (x.Length == 0) return 0;
        double sum = 0;
        foreach (var v in x) sum += v.Real * v.Real + v.Imaginary * v.Imaginary;
        return Math.Sqrt(sum / x.Length);
    }

    public EnvelopeRunResult Run(EnvelopeParams p, RunControl? control = null)
    {
        var sw = Stopwatch.StartNew();
        var sources = _nl.Components.Select(c => c.Model).OfType<ModSourceModel>().ToArray();
        var srcEcs = _nl.Components.Where(c => c.Model is ModSourceModel).ToArray();
        if (sources.Length == 0)
            throw new InvalidOperationException("Envelope analysis needs at least one MOD_Source (the modulated signal).");
        foreach (var ec in _nl.Components)
            if (ec.Model is SddModel sdd && sdd.ControlRefs.Length > 0)
                throw new InvalidOperationException($"Envelope analysis does not support SDD control currents yet ('{ec.InstancePath}').");

        // ── fundamental and envelope rate ─────────────────────────────────
        double f0 = sources[0].FcHz;
        int K = p.MaxHarm;
        double omega0 = 2 * Math.PI * f0;
        var signals = sources.Select(s => s.Signal).ToArray();
        double need = 0, edge = 0;
        for (int s = 0; s < sources.Length; s++)
            foreach (var c in signals[s].Carriers)
            {
                double off = sources[s].FcHz + c.OffsetHz - f0;
                need = Math.Max(need, 2 * (Math.Abs(off) + 2.5 * c.Spec.BandwidthHz));
                need = Math.Max(need, c.NativeRateHz);
                edge = Math.Max(edge, Math.Abs(off) + 0.5 * c.Spec.BandwidthHz);
            }
        double rate = SignalRate.AtLeast(need, signals);
        int osr = Oversample(p.Oversample, rate, edge);
        double h = p.StepS > 0 ? p.StepS : 1.0 / (rate * osr);
        double recordRate = 1.0 / h;
        double record = signals.Min(sg => sg.RecordSeconds);
        double nRec = record * recordRate;
        if (Math.Abs(nRec - Math.Round(nRec)) > 1e-6 * nRec)
            throw new InvalidOperationException($"envelope step {h:G4} s does not divide the {record * 1e3:0.###} ms signal record; leave Step automatic or pick a divisor");
        foreach (var sg in signals)
            if (Math.Abs(sg.RecordSeconds - record) > 1e-12)
                throw new InvalidOperationException("all MOD_Sources in one envelope run must use the same record length");
        // Noise (brief NZ-7), from every part's own stamp, before a line or an S-parameter block is replaced for the run.
        var thermal = p.Noise ? EnvelopeNoise.Thermal(_nl, _settings, [.. Enumerable.Range(0, K + 1).Select(k => k * f0)], h, p.Seed, f0) : null;
        var phase = p.PhaseNoise ? EnvelopePhaseNoise.Of(_nl, (int)Math.Round(nRec), recordRate, p.Seed) : null;
        // Every line is its delay for the whole run — the DC solve, the HB start and the stepping (EnvelopeLineDelay) — and
        // every other non-affine element (an SnP, a System Filter, a Z(freq) block) its rational fit (EnvelopeSnpFit).
        using var lines = EnvelopeLineDelay.Apply(_nl, [.. Enumerable.Range(0, K + 1).Select(k => k * f0)], h, 0.5 * need);
        using var snpFit = EnvelopeSnpFit.Apply(_nl, K * f0 + 0.5 * rate, [.. Enumerable.Range(1, K).Select(k => k * f0)], 0.5 * rate);
        // An element whose fit was refused (any stamp whose 1 Hz / 2 Hz tangent is not exact) is held at its DC stamp for
        // the baseband product.
        using var baseband = EnvelopeBaseband.Apply(_nl, Math.PI * need);

        var swSetup = Stopwatch.StartNew();
        bool profS = Environment.GetEnvironmentVariable("CRF_ENV_PROFILE") is not null;
        void Mark(string what) { if (profS) Console.Error.WriteLine($"[env-profile] {what} {swSetup.Elapsed.TotalSeconds:0.00}s"); }
        Mark("signals");
        // per-source drive WINDOW at the envelope rate, relative to f0: only the samples this run touches
        int nWarm = (int)Math.Round(p.WarmupS / h);
        int nSteps = (int)Math.Round(p.StopS / h);
        long i0 = (long)Math.Round(p.StartS / h) - nWarm;
        int nDrive = nWarm + nSteps + 1;
        var drive = new Complex[sources.Length][];
        for (int s = 0; s < sources.Length; s++)
        {
            var x = signals[s].CompositeWindow(recordRate, f0 - sources[s].FcHz, i0, nDrive);
            double vs = sources[s].VsPeak;
            for (int i = 0; i < x.Length; i++) x[i] *= vs;
            phase?.Apply(sources[s], x, i0);
            drive[s] = x;
        }
        Complex Drv(int s, int step) => drive[s][step];

        // ── linear partition ──────────────────────────────────────────────
        var extractor = new HbLinearExtractor(_nl, _settings);
        int N = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        int gridN = HbFft.GridSize(K, p.FftOverSample);
        foreach (var ec in _nl.Components)
        {
            if (ec.Model is P1ToneModel p1)
            {
                double driveHz = ec.Parameters.TryGetValue("Freq", out var fv) && fv.Kind == Core.Expressions.ValueKind.Real ? fv.AsReal() : f0;
                p1.SetToneContext(fc: f0, driveFreqHz: driveHz);
            }
            else if (ec.Model is PnToneModel pn) pn.SetToneContext(fc: f0);
        }
        foreach (var s in sources) { s.SetToneContext(f0); s.EnvelopeDrive = Complex.Zero; }
        // Before the products are stamped: stamping an element alone re-numbers a source's branch (LastBranchIndex).
        // Measured out to the signal's own extent (+-need/2: carriers and their ACPR2 bands), where the result is read.
        EnvelopeTangentCheck.Warn(_nl, EnvelopeTangentCheck.Measure(_nl, Enumerable.Range(1, K).Select(k => k * omega0), Math.PI * need), 0.5 * need);

        double hBe = h, aBe = 1.0 / hBe, aG2 = 1.5 / h, b1G2 = -2.0 / h, b2G2 = 0.5 / h;
        double tanErrWorst = 0;
        var products = new Product[K + 1];
        var mna = extractor.NewSmallSignalMna();
        try
        {
            for (int k = 0; k <= K; k++)
            {
                double w = k * omega0;
                double dlt = Math.Max(1e-6 * Math.Max(w, 2 * Math.PI * 1e6), 2 * Math.PI * 1.0);
                CompressedColumnStorage<Complex> A0, C;
                baseband.Holding = k == 0;            // the whole baseband product, its right-hand side included
                lines.Product = k;
                if (k == 0)
                {
                    var a1 = Stamp(extractor, mna, dlt);
                    var a2 = Stamp(extractor, mna, 2 * dlt);
                    C = Combine(a2, a1, 1.0 / new Complex(0, dlt), -1.0 / new Complex(0, dlt));
                    A0 = Combine(a1, C, 1.0, -new Complex(0, dlt));
                }
                else
                {
                    var ap = Stamp(extractor, mna, w + dlt);
                    var am = Stamp(extractor, mna, w - dlt);
                    C = Combine(ap, am, 1.0 / new Complex(0, 2 * dlt), -1.0 / new Complex(0, 2 * dlt));
                    A0 = Stamp(extractor, mna, w);
                    // tangent check at the envelope band edge (affine models: ~round-off)
                    double band = 0.5 * rate;
                    if (w - 2 * Math.PI * band > 0)
                    {
                        var aEdge = Stamp(extractor, mna, w + 2 * Math.PI * band);
                        var tan = Combine(A0, C, 1.0, new Complex(0, 2 * Math.PI * band));
                        tanErrWorst = Math.Max(tanErrWorst, RelDiff(aEdge, tan));
                    }
                }
                // RHS at the exact harmonic (sources: DC at k=0, CW tones at their harmonic)
                var bStatic = RhsAt(extractor, mna, w, sources, null);
                var bUnit = new Complex[sources.Length][];
                for (int s = 0; s < sources.Length; s++)
                {
                    if (k != 1) { bUnit[s] = new Complex[bStatic.Length]; continue; }
                    var b = RhsAt(extractor, mna, w, sources, s);
                    for (int r = 0; r < b.Length; r++) b[r] -= bStatic[r];
                    bUnit[s] = b;
                }
                bUnit = ToneUnits(bUnit, bStatic, phase, () => RhsAt(extractor, mna, w, sources, null));
                var pr = new Product { Omega = w, A0 = A0, C = C, BStatic = bStatic, BUnit = bUnit };
                baseband.Holding = false;
                lines.Product = null;
                lines.Bind();
                var delayNow = lines.Coupling(k, 1, A0.RowCount);
                pr.Be = Stage.Of(Prepare(A0, C, w, aBe, ifNodes, bStatic, bUnit, delayNow));
                pr.G2 = Stage.Of(Prepare(A0, C, w, aG2, ifNodes, bStatic, bUnit, delayNow));
                products[k] = pr;
            }
        }
        finally
        {
            baseband.Holding = false;
            lines.Product = null;
            foreach (var s in sources) s.EnvelopeDrive = null;
        }

        Mark("linear products");
        // ── start: harmonic balance at the first stimulus sample ─────────
        var driveRms = drive.Select(Rms).ToArray();
        var (V, hbX, qPrev) = SteadyStart(extractor, baseband, sources, f0, K, N, ifNodes, gridN, p, s => Drv(s, 0), s => driveRms[s],
                                          out double tolRun);
        for (int k = 0; k <= K; k++) { products[k].Xm1 = hbX[k]; products[k].Xm2 = hbX[k]; }
        lines.Start(hbX);
        var qm1 = qPrev; var qm2 = qPrev;
        Mark("HB start");

        // probes and supplies
        var probeNodes = RecordedNodes(_nl, p.Probes, srcEcs);
        static int BranchOf(ComponentModel m) => m switch { VdcModel v => v.LastBranchIndex, ToneSourceModel t => t.LastBranchIndex, _ => -1 };
        var supplies = _nl.Components.Where(c => BranchOf(c.Model) >= 0 &&
                                                 c.Parameters.TryGetValue("Vdc", out var vv) && vv.Kind == Core.Expressions.ValueKind.Real && vv.AsReal() != 0).ToArray();
        var supplyRows = supplies.Select(c => BranchOf(c.Model)).ToArray();   // AddBranch returns the absolute MNA row
        var supplyV = supplies.Select(c => c.Parameters.TryGetValue("Vdc", out var v) && v.Kind == Core.Expressions.ValueKind.Real ? v.AsReal() : 0.0).ToArray();
        var (iprobes, iprobeRows, iprobeNodes) = IProbesOf(_nl);
        var (wbonds, wbondRows) = EnvelopeWireTemperature.Rows(_nl);

        int total = nWarm + nSteps + 1;
        var tOut = new double[nSteps + 1];
        var pOut = new Complex[nSteps + 1][][];
        var sOut = new double[nSteps + 1][];
        var iOut = new Complex[nSteps + 1][][];
        var ivOut = new Complex[nSteps + 1][][];
        var wOut = new Complex[wbonds.Length > 0 ? nSteps + 1 : 0][][];
        var dOut = new Complex[sources.Length][];
        for (int s = 0; s < sources.Length; s++) dOut[s] = new Complex[nSteps + 1];
        void Record(int step)
        {
            int o = step - nWarm;
            if (o < 0) return;
            tOut[o] = o * h;
            if (wbonds.Length > 0) wOut[o] = EnvelopeWireTemperature.Sample(wbondRows, K + 1, (k, row) => products[k].Xm1[row]);
            var pr = new Complex[probeNodes.Length][];
            for (int pi = 0; pi < probeNodes.Length; pi++)
            {
                pr[pi] = new Complex[K + 1];
                int row = probeNodes[pi].node - 1;
                for (int k = 0; k <= K; k++) pr[pi][k] = row >= 0 ? products[k].Xm1[row] : Complex.Zero;
                pr[pi][0] = new Complex(pr[pi][0].Real, 0);
            }
            pOut[o] = pr;
            var su = new double[supplies.Length];
            for (int i = 0; i < supplies.Length; i++) su[i] = products[0].Xm1[supplyRows[i]].Real;
            sOut[o] = su;
            var ic = new Complex[iprobes.Length][];
            var iv = new Complex[iprobes.Length][];
            for (int i = 0; i < iprobes.Length; i++)
            {
                ic[i] = new Complex[K + 1];
                iv[i] = new Complex[K + 1];
                for (int k = 0; k <= K; k++)
                {
                    ic[i][k] = products[k].Xm1[iprobeRows[i]];
                    iv[i][k] = iprobeNodes[i] >= 0 ? products[k].Xm1[iprobeNodes[i]] : Complex.Zero;
                }
                ic[i][0] = new Complex(ic[i][0].Real, 0);
                iv[i][0] = new Complex(iv[i][0].Real, 0);
            }
            iOut[o] = ic;
            ivOut[o] = iv;
            for (int s = 0; s < sources.Length; s++) dOut[s][o] = Drv(s, step);
        }
        Record(0);

        // ── time stepping ─────────────────────────────────────────────────
        int dof = 2 * N * (K + 1);
        var judge = HbNewton.JudgeFor(_nl, ifNodes);
        var newton = new EnvelopeNewton(dof, judge);
        var vHist = new List<Complex[,]> { (Complex[,])V.Clone() };
        double tolAbs = tolRun;
        bool prof = Environment.GetEnvironmentVariable("CRF_ENV_PROFILE") is not null;
        var swL = new Stopwatch(); var swE = new Stopwatch(); var swF = new Stopwatch(); var swJ = new Stopwatch(); var swU = new Stopwatch();
        int splitSteps = 0, finestSplit = 1;

        // One step from the history (x1, x2, q1, q2) to the new state of every product, with V the starting guess and,
        // on return, the solved interface spectrum. Null when Newton did not converge.
        // delayed(k, b) adds the lines' known delayed waves for product k to its right-hand side.
        // sample is the step's absolute index in the signal record: what the noise and the phase records are read at.
        Complex[][]? Advance(EnvelopeNewton nw, double alpha, double b1, double b2, Func<int, Stage> stageOf,
                             Func<int, Complex> drv, Complex[][] x1, Complex[][] x2, Complex[,] q1, Complex[,] q2,
                             Complex[,] V, Action<int, Complex[]>? delayed, long sample, out Complex[,] qNow)
        {
            delayed = WithNoise(delayed, thermal, sample);
            if (prof) swL.Start();
            var iSrc = new Complex[K + 1][];
            var yEff = new Complex[K + 1][,];
            var xoc = new Complex[K + 1][];
            for (int k = 0; k <= K; k++)
            {
                var sg = stageOf(k);
                int n = sg.Xs.Length;
                // history right-hand side: -C (b1 x(n-1) + b2 x(n-2))
                var hv = new Complex[n];
                for (int r = 0; r < n; r++) hv[r] = b1 * x1[k][r] + b2 * x2[k][r];
                var bh = new Complex[n];
                MulCsc(products[k].C, hv, bh, -1.0);
                delayed?.Invoke(k, bh);
                var xh = new Complex[n];
                sg.Lu.Solve(bh, xh);
                var x = xoc[k] = new Complex[n];
                for (int r = 0; r < n; r++) x[r] = sg.Xs[r] + xh[r];
                if (k == 1)
                    for (int s = 0; s < sources.Length; s++)
                    {
                        Complex a = drv(s);
                        var u = sg.Xu[s];
                        for (int r = 0; r < n; r++) x[r] += a * u[r];
                    }
                AddTonePhase(phase, sg.Xu, sources.Length, k * f0, sample, x);
                var voc = new Complex[N];
                for (int j = 0; j < N; j++) voc[j] = x[ifNodes[j] - 1];
                var isrc = new Complex[N];
                for (int a = 0; a < N; a++) { Complex acc = 0; for (int b = 0; b < N; b++) acc += sg.Y[a, b] * voc[b]; isrc[a] = -acc; }
                if (k == 0) for (int a = 0; a < N; a++) isrc[a] = new Complex(isrc[a].Real, 0);
                iSrc[k] = isrc; yEff[k] = sg.Y;
            }
            if (prof) swL.Stop();
            // device charge history (Gear): b1 q(n-1) + b2 q(n-2)
            var qh = new Complex[N, K + 1];
            for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) qh[a, k] = b1 * q1[a, k] + b2 * q2[a, k];

            var accepted = nw.Solve(V, tolAbs, v =>
            {
                if (prof) swE.Start();
                var (iNl, qNl, G, Cc, buckets) = HbNewton.EvaluateNonlinear(v, N, K, gridN, _nl, ifNodes);
                if (prof) { swE.Stop(); swF.Start(); }
                var inj = new Complex[N, K + 1];
                for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) inj[a, k] = iNl[a, k] + alpha * qNl[a, k] + qh[a, k];
                // A w>=2 bucket is memoryless in the envelope (H[w] at each harmonic), so it joins the injection here:
                // the residual and the linear state's update below then both carry it. Handed to BuildF instead, it
                // balanced the interface but never reached the state, which read the block as missing.
                foreach (var b in buckets)
                    for (int k = 0; k <= K; k++)
                    {
                        var h = b.Model.Weight(b.W, k * omega0);
                        for (int a = 0; a < N; a++) inj[a, k] += h * b.WNl[a, k];
                    }
                var F = HbNewton.BuildF(v, yEff, iSrc, inj, qNl, N, K, omega0);
                if (prof) swF.Stop();
                return (F, new StepEval(qNl, inj, G, Cc, buckets));
            }, e =>
            {
                if (prof) swJ.Start();
                var Gs = new Complex[N, N, e.G.GetLength(2)];
                for (int a = 0; a < N; a++) for (int b = 0; b < N; b++) for (int m = 0; m < e.G.GetLength(2); m++)
                    Gs[a, b, m] = e.G[a, b, m] + alpha * e.Cc[a, b, m];
                var J = HbNewton.BuildJ(yEff, Gs, e.Cc, N, K, omega0, 0, e.Buckets);
                if (prof) swJ.Stop();
                return J;
            }, p.MinNewton);
            if (accepted is null) { qNow = null!; return null; }
            qNow = accepted.Q;

            if (prof) swU.Start();
            // update linear state: x = x_oc - sum_j Zcol_j * (total device injection)_j
            for (int k = 0; k <= K; k++)
            {
                var zc = stageOf(k).Zcol;
                var x = xoc[k];
                for (int j = 0; j < N; j++)
                {
                    Complex inj = accepted.Inj[j, k] + (k > 0 ? new Complex(0, k * omega0) * qNow[j, k] : Complex.Zero);
                    if (k == 0) inj = new Complex(inj.Real, 0);
                    var z = zc[j];
                    for (int r = 0; r < x.Length; r++) x[r] -= z[r] * inj;
                }
            }
            if (prof) swU.Stop();
            return xoc;
        }

        // A step whose Newton failed is retaken as m backward-Euler sub-steps of h/m (m = 2, 4, … MaxStepSplit), the
        // drive interpolated linearly between the step's two samples. The last sub-step lands on the step's own time,
        // so the Gear-2 history at h — x(n-1), x(n-2) — is exactly what the next step needs.
        Complex[][] Split(int step, Complex[,] V, out Complex[,] qNow)
        {
            for (int m = 2; m <= MaxStepSplit; m *= 2)
            {
                double aSub = m / h;
                var nw = new EnvelopeNewton(dof, judge);
                var x = products.Select(pr => pr.Xm1).ToArray();
                var q = qm1;
                var vs = (Complex[,])vHist[^1].Clone();
                bool ok = true;
                lines.BeginSplit();
                for (int i = 1; i <= m && ok; i++)
                {
                    double f = (double)i / m;
                    var xn = Advance(nw, aSub, -aSub, 0.0, k => SplitStage(k, m, aSub),
                                     s => Drv(s, step - 1) + f * (Drv(s, step) - Drv(s, step - 1)), x, x, q, q, vs,
                                     lines.Any ? (k, b) => lines.AddSplit(k, b, m, i) : null, i0 + step, out var qn);
                    if (xn is null) ok = false;
                    else { x = xn; q = qn; lines.PushSub(xn); }
                }
                if (!ok) continue;
                Array.Copy(vs, V, V.Length);
                newton.Invalidate();                        // built at the failed full step's last iterate
                splitSteps++; finestSplit = Math.Max(finestSplit, m);
                qNow = q;
                return x;
            }
            throw new InvalidOperationException(
                $"envelope step {step} (t = {p.StartS + (step - nWarm) * h:G4} s) did not converge, even split into {MaxStepSplit} sub-steps");
        }

        Stage SplitStage(int k, int m, double aSub)
        {
            var pr = products[k];
            if (!pr.Split.TryGetValue(m, out var sg))
                pr.Split[m] = sg = Stage.Of(Prepare(pr.A0, pr.C, pr.Omega, aSub, ifNodes, pr.BStatic, pr.BUnit, lines.Coupling(k, m, pr.A0.RowCount)));
            return sg;
        }

        for (int step = 1; step < total; step++)
        {
            if ((step & 1023) == 0) control?.ThrowIfCancellationRequested();
            bool be = step == 1;
            double alpha = be ? aBe : aG2;
            double b1 = be ? -aBe : b1G2, b2 = be ? 0.0 : b2G2;
            if (step <= 2) newton.Invalidate();             // alpha changes BE -> Gear-2

            // predictor: quadratic extrapolation of the interface spectrum
            if (vHist.Count >= 3)
            {
                var v1 = vHist[^1]; var v2 = vHist[^2]; var v3 = vHist[^3];
                for (int a = 0; a < N; a++) for (int k = 0; k <= K; k++) V[a, k] = 3 * v1[a, k] - 3 * v2[a, k] + v3[a, k];
            }

            var xNew = Advance(newton, alpha, b1, b2, k => be ? products[k].Be : products[k].G2, s => Drv(s, step),
                               products.Select(pr => pr.Xm1).ToArray(), products.Select(pr => pr.Xm2).ToArray(), qm1, qm2, V,
                               lines.Any ? lines.AddMain : null, i0 + step, out var qNow)
                       ?? Split(step, V, out qNow);
            for (int k = 0; k <= K; k++) { products[k].Xm2 = products[k].Xm1; products[k].Xm1 = xNew[k]; }
            lines.Push(xNew);
            qm2 = qm1; qm1 = qNow;
            vHist.Add((Complex[,])V.Clone()); if (vHist.Count > 3) vHist.RemoveAt(0);
            Record(step);
        }

        if (prof)
            Console.Error.WriteLine($"[env-profile] steps {total - 1}  linear {swL.Elapsed.TotalSeconds:0.00}s  eval {swE.Elapsed.TotalSeconds:0.00}s  F {swF.Elapsed.TotalSeconds:0.00}s  J {swJ.Elapsed.TotalSeconds:0.00}s ({newton.JacobianBuilds})  update {swU.Elapsed.TotalSeconds:0.00}s  newton {(double)newton.Iterations / Math.Max(1, total - 1):0.00}");
        var res = new EnvelopeRunResult
        {
            Time = tOut, ProbeNames = probeNodes.Select(x => x.name).ToArray(), Probe = pOut,
            SupplyNames = supplies.Select(c => c.InstancePath).ToArray(), SupplyCurrent = sOut, SupplyVoltage = supplyV,
            IProbeNames = iprobes, IProbeCurrent = iOut, IProbeVoltage = ivOut, WBondPaths = wbonds, WBondCurrent = wOut,
            Drive = dOut, DriveRe = sources.Select(s => s.Z.Real).ToArray(),
            F0Hz = f0, StepS = h, MaxHarm = K, SignalRateHz = rate, Sources = sources,
            WallSeconds = sw.Elapsed.TotalSeconds, MeanNewton = (double)newton.Iterations / Math.Max(1, total - 1),
            JacobianBuilds = newton.JacobianBuilds, WorstTangentError = tanErrWorst, SnpFits = snpFit.Reports,
            CarrierMix = sources.SelectMany((s, si) => signals[si].Carriers.Select(_ => 1)).ToArray(),
            CarrierOffsetHz = sources.SelectMany((s, si) => signals[si].Carriers.Select(c => s.FcHz + c.OffsetHz - f0)).ToArray(),
            CarrierLabels = sources.SelectMany((s, si) => signals[si].Carriers.Select(c => $"{s.InstanceName}:{c.Spec.Label} @ {(s.FcHz + c.OffsetHz) / 1e9:0.####} GHz")).ToArray(),
        };
        if (splitSteps > 0)
            res.Notes.Add($"envelope: {splitSteps} step(s) did not converge at the full step and were retaken as up to {finestSplit} sub-steps");
        return res;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// The node voltages a run records (brief ENV-MEAS-3): every labeled net (<see cref="NodeMap.LabeledNames"/>, the set
    /// harmonic balance marks), and every node an old file's retired <c>Probes</c> list names, in ascending node-index
    /// order as HB emits them. A netlist that labels nothing records its MOD_Source nodes.
    /// </summary>
    public static (string name, int node)[] RecordedNodes(ElaboratedNetlist nl, string[] listed, ElaboratedComponent[] srcEcs)
    {
        var set = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var nm in nl.Nodes.LabeledNames)
            if (nl.Nodes.TryGetIndex(nm, out int n) && n > 0) set[nm] = n;
        foreach (var nm in listed)
        {
            if (!nl.Nodes.TryGetIndex(nm, out int n) || n <= 0)
                throw new InvalidOperationException($"envelope probe '{nm}' is not a node of the circuit");
            set[nm] = n;
        }
        if (set.Count == 0)
            foreach (var e in srcEcs)
                if (e.Nodes[0] > 0) set[nl.Nodes.NameOf(e.Nodes[0])] = e.Nodes[0];
        return [.. set.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>
    /// Every IProbe of the netlist (a WSProbe shares the stamp and is not one), with its branch row and its first
    /// node's row in the state vector (−1 at ground). Read after the products are stamped: the branch row is the one
    /// the last full stamp gave it.
    /// </summary>
    internal static (string[] names, int[] rows, int[] nodes) IProbesOf(ElaboratedNetlist nl)
    {
        var ps = nl.Components.Where(c => c.Model is IProbeModel { LastBranchIndex: >= 0 } && c.Nodes.Length >= 2).ToArray();
        return ([.. ps.Select(c => c.InstancePath)], [.. ps.Select(c => ((IProbeModel)c.Model).LastBranchIndex)],
                [.. ps.Select(c => c.Nodes[0] - 1)]);
    }

    /// <summary>
    /// Each phase-noisy CW source's own share of a product's static right-hand side, appended to <paramref name="bUnit"/>
    /// after the MOD_Sources' unit drives (brief NZ-7): the static RHS less the RHS with that source's drive scaled to 0
    /// (<paramref name="rhs"/> stamps it). Its solution, times (exp(j·n·φ) − 1), turns the source's phase at each step.
    /// <paramref name="bUnit"/> unchanged when no source states a mask.
    /// </summary>
    internal static Complex[][] ToneUnits(Complex[][] bUnit, Complex[] bStatic, EnvelopePhaseNoise? phase, Func<Complex[]> rhs)
    {
        if (phase is not { Tones.Count: > 0 }) return bUnit;
        var all = new List<Complex[]>(bUnit);
        foreach (var t in phase.Tones)
        {
            double was = t.Model.DriveScale;
            t.Model.DriveScale = 0;
            try
            {
                var b0 = rhs();
                var d = new Complex[bStatic.Length];
                for (int r = 0; r < d.Length; r++) d[r] = bStatic[r] - b0[r];
                all.Add(d);
            }
            finally { t.Model.DriveScale = was; }
        }
        return [.. all];
    }

    /// <summary>Adds each phase-noisy CW source's phase turn at a product of |f| = <paramref name="productHz"/> to its
    /// open-circuit state <paramref name="x"/>: (exp(j·n·φ) − 1) times the source's own solution, which
    /// <see cref="ToneUnits"/> placed after the <paramref name="first"/> MOD_Source columns of <paramref name="xu"/>.</summary>
    internal static void AddTonePhase(EnvelopePhaseNoise? phase, Complex[][] xu, int first, double productHz, long sample, Complex[] x)
    {
        if (phase is not { Tones.Count: > 0 } || productHz <= 0) return;
        for (int j = 0; j < phase.Tones.Count; j++)
        {
            var f = phase.ToneFactor(j, productHz, sample);
            var u = xu[first + j];
            for (int r = 0; r < x.Length; r++) x[r] += f * u[r];
        }
    }

    /// <summary>The right-hand-side hook with the step's thermal noise added after it (brief NZ-7); unchanged without noise.</summary>
    internal static Action<int, Complex[]>? WithNoise(Action<int, Complex[]>? delayed, EnvelopeNoise? thermal, long sample) =>
        thermal is null ? delayed : (k, b) => { delayed?.Invoke(k, b); thermal.Add(k, sample, b); };

    internal static CompressedColumnStorage<Complex> Stamp(HbLinearExtractor ex, MnaSystem mna, double omega)
    {
        ex.StampSmallSignal(mna, omega);
        return mna.BuildCsc();
    }

    internal static Complex[] RhsAt(HbLinearExtractor ex, MnaSystem mna, double omega, ModSourceModel[] srcs, int? unit)
    {
        for (int s = 0; s < srcs.Length; s++) srcs[s].EnvelopeDrive = unit == s ? Complex.One : Complex.Zero;
        ex.StampSmallSignal(mna, omega);
        var b = mna.BuildRhs();
        for (int s = 0; s < srcs.Length; s++) srcs[s].EnvelopeDrive = Complex.Zero;
        return b;
    }

    /// <summary>a·A + b·B over the union pattern.</summary>
    internal static CompressedColumnStorage<Complex> Combine(CompressedColumnStorage<Complex> A, CompressedColumnStorage<Complex> B, Complex a, Complex b)
    {
        int n = A.RowCount;
        if (B.RowCount != n || A.ColumnCount != B.ColumnCount)
            throw new InvalidOperationException("envelope: the linear network's size changes with frequency (a model adds unknowns only at some frequencies)");
        var dict = new Dictionary<long, Complex>();
        void Add(CompressedColumnStorage<Complex> M, Complex s)
        {
            for (int j = 0; j < M.ColumnCount; j++)
                for (int p = M.ColumnPointers[j]; p < M.ColumnPointers[j + 1]; p++)
                {
                    long key = (long)M.RowIndices[p] * n + j;
                    dict[key] = (dict.TryGetValue(key, out var v) ? v : 0) + s * M.Values[p];
                }
        }
        Add(A, a); Add(B, b);
        var tri = new CoordinateStorage<Complex>(n, A.ColumnCount, dict.Count);
        foreach (var kv in dict) tri.At((int)(kv.Key / n), (int)(kv.Key % n), kv.Value);
        return SparseMatrix.OfIndexed(tri);
    }

    internal static double RelDiff(CompressedColumnStorage<Complex> A, CompressedColumnStorage<Complex> B)
    {
        var d = Combine(A, B, 1.0, -1.0);
        double num = 0, den = 0;
        // A matrix with no stored entries (a purely resistive network has C = 0) may carry no Values array.
        foreach (var v in d.Values ?? []) num = Math.Max(num, v.Magnitude);
        foreach (var v in A.Values ?? []) den = Math.Max(den, v.Magnitude);
        return den > 0 ? num / den : 0;
    }

    internal static void MulCsc(CompressedColumnStorage<Complex> M, Complex[] x, Complex[] y, double scale)
    {
        for (int j = 0; j < M.ColumnCount; j++)
        {
            Complex xj = x[j];
            if (xj == Complex.Zero) continue;
            for (int p = M.ColumnPointers[j]; p < M.ColumnPointers[j + 1]; p++)
                y[M.RowIndices[p]] += scale * M.Values[p] * xj;
        }
    }

    /// <param name="delayNow">The lines' share of the current step's delayed wave (<see cref="EnvelopeLineDelay.Run.Coupling"/>),
    /// null when there is none.</param>
    internal static (SparseLU lu, Complex[][] zcol, Complex[,] Y, Complex[] xs, Complex[][] xu) Prepare(
        CompressedColumnStorage<Complex> A0, CompressedColumnStorage<Complex> C, double w, double alpha,
        int[] ifNodes, Complex[] bStatic, Complex[][] bUnit, CompressedColumnStorage<Complex>? delayNow = null)
    {
        var A = Combine(A0, C, 1.0, alpha);
        if (delayNow is not null) A = Combine(A, delayNow, 1.0, 1.0);
        var lu = SparseLU.Create(A, CSparse.ColumnOrdering.MinimumDegreeAtPlusA, 1.0);
        int n = A.RowCount, N = ifNodes.Length;
        var zcol = new Complex[N][];
        var zP = new Complex[N, N];
        for (int j = 0; j < N; j++)
        {
            var e = new Complex[n]; e[ifNodes[j] - 1] = 1.0;
            var z = new Complex[n];
            lu.Solve(e, z);
            zcol[j] = z;
            for (int i = 0; i < N; i++) zP[i, j] = z[ifNodes[i] - 1];
        }
        var Y = Invert(zP, N);
        var xs = new Complex[n];
        lu.Solve((Complex[])bStatic.Clone(), xs);
        var xu = new Complex[bUnit.Length][];
        for (int s = 0; s < bUnit.Length; s++)
        {
            xu[s] = new Complex[n];
            lu.Solve((Complex[])bUnit[s].Clone(), xu[s]);
        }
        return (lu, zcol, Y, xs, xu);
    }

    internal static Complex[,] Invert(Complex[,] a, int n)
    {
        var m = (Complex[,])a.Clone();
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = 1;
        for (int c = 0; c < n; c++)
        {
            int p = c; double best = m[c, c].Magnitude;
            for (int r = c + 1; r < n; r++) if (m[r, c].Magnitude > best) { best = m[r, c].Magnitude; p = r; }
            if (best == 0) throw new InvalidOperationException("envelope: singular port impedance matrix");
            if (p != c)
                for (int j = 0; j < n; j++) { (m[c, j], m[p, j]) = (m[p, j], m[c, j]); (inv[c, j], inv[p, j]) = (inv[p, j], inv[c, j]); }
            Complex d = 1.0 / m[c, c];
            for (int j = 0; j < n; j++) { m[c, j] *= d; inv[c, j] *= d; }
            for (int r = 0; r < n; r++)
            {
                if (r == c || m[r, c] == Complex.Zero) continue;
                Complex f = m[r, c];
                for (int j = 0; j < n; j++) { m[r, j] -= f * m[c, j]; inv[r, j] -= f * inv[c, j]; }
            }
        }
        return inv;
    }

    /// <summary>Harmonic balance with each MOD_Source fixed at its t0 envelope value; returns V, full states, q.</summary>
    /// <remarks>The DC solve and the k = 0 extraction see the baseband product's held elements (a line shorted), so the run
    /// starts at the equilibrium it steps from.</remarks>
    /// <param name="driveRms">Each source's RMS drive over the run, which the tolerance is set against.</param>
    /// <param name="tol">The run's tolerance (<see cref="RunTolerance"/>), which the start is solved to and the steps take.</param>
    private (Complex[,] V, Complex[][] X, Complex[,] q) SteadyStart(HbLinearExtractor ex, EnvelopeBaseband.Hold baseband, ModSourceModel[] srcs, double f0, int K,
        int N, int[] ifNodes, int gridN, EnvelopeParams p, Func<int, Complex> drive0, Func<int, double> driveRms, out double tol)
    {
        double w0 = 2 * Math.PI * f0;
        var yNN = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        var bSrc = new Complex[K + 1][];
        void ExtractAll(double scale, Func<int, Complex>? at = null)
        {
            for (int k = 0; k <= K; k++)
            {
                for (int s = 0; s < srcs.Length; s++) srcs[s].EnvelopeDrive = k == 1 ? (at ?? drive0)(s) * scale : Complex.Zero;
                baseband.Holding = k == 0;
                if (k == 0) (yNN[0], iSrc[0]) = ex.ExtractDC();
                else (yNN[k], iSrc[k]) = ex.Extract(k * w0);
                bSrc[k] = ex.BuildSourceRhs(k == 0 ? 0.0 : k * w0);
                baseband.Holding = false;
            }
        }
        try
        {
            baseband.Holding = true;
            var dc = NonlinearDcEngine.Run(_nl, _settings);
            baseband.Holding = false;
            // A linearised-charge model (a composed FET with LinearC) evaluates its charge at the quiescent point:
            // freeze it there before the first HB solve reads charge, as HbEngine does.
            BiasFreeze.Apply(_nl, dc.Converged ? dc.NodeVoltages : null, dc.NodeVoltages);
            Complex[,] V = new Complex[N, K + 1];
            for (int a = 0; a < N; a++)
            {
                int node = ifNodes[a];
                V[a, 0] = dc.Converged && node - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[node - 1] : 0.0;
            }
            // the run's tolerance, from the RF excitation at the record's RMS drive (the drive at t = 0 may be zero)
            ExtractAll(1.0, s => driveRms(s));
            tol = RunTolerance(p.Tol, iSrc, k => k > 0, _nl);
            HbNewton.SolveResult? sol = null;
            foreach (double sc in new[] { 1.0 })
            {
                ExtractAll(sc);
                sol = HbNewton.Solve(V, yNN, iSrc, f0, K, N, _nl, ifNodes, gridN, _settings, tol);
            }
            // The drive at t = 0 below what the run's tolerance resolves — a DBPSK record starts on a phase reversal, where it
            // is zero — is refused by the solve as a tolerance it cannot constrain. For a drive-up ladder that refusal is the
            // point; here the start IS that drive, and its solution is the seed to within the tolerance every step is held to.
            bool belowTol = sol is { Converged: false } && double.IsFinite(sol.VacuousToleranceScale);
            if (!belowTol && (sol is null || !sol.Converged))
            {
                // drive ramp in 10 steps
                for (int r = 1; r <= 10; r++)
                {
                    ExtractAll(r / 10.0);
                    sol = HbNewton.Solve(V, yNN, iSrc, f0, K, N, _nl, ifNodes, gridN, _settings, tol);
                }
                if (sol is null || !sol.Converged)
                    throw new InvalidOperationException("envelope: the harmonic-balance start point did not converge");
            }
            if (sol is null) throw new InvalidOperationException("envelope: the harmonic-balance start point did not converge");
            var X = new Complex[K + 1][];
            for (int k = 0; k <= K; k++)
            {
                var inl = new Complex[N];
                for (int a = 0; a < N; a++) inl[a] = sol.INl[a, k];
                baseband.Holding = k == 0;
                X[k] = ex.SolveFullNetwork(k == 0 ? 0.0 : k * w0, inl, bSrc[k]);
                baseband.Holding = false;
            }
            var (_, qNl, _, _, _) = HbNewton.EvaluateNonlinear(V, N, K, gridN, _nl, ifNodes);
            return (V, X, qNl);
        }
        finally
        {
            baseband.Holding = false;
            foreach (var s in srcs) s.EnvelopeDrive = null;
        }
    }
}
