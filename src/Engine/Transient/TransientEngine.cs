// ================================================================
//  TransientEngine.cs  —  the native transient analysis (docs/design/transient.md §4)
//
//  The elaborated circuit is
//
//    G x + C dx/dt + i(x) + dq(x)/dt = b(t)
//
//  G and C from the linear partition's own stamps (AffineStamp: exact for every
//  element TransientElements accepts), i and q from each nonlinear device's
//  Evaluate, b(t) from every source's phasors at its own frequencies. Everything
//  is real.
//
//  A step from t to t + h replaces the derivative of the total charge
//  Phi(x) = C x + q(x) by alpha Phi(t + h) + hist: backward Euler (alpha = 1/h),
//  trapezoidal (2/h, hist carrying the previous derivative) or the second-order
//  backward difference over the actual unequal steps. Two backward-Euler steps are
//  taken at the start and after every breakpoint (a source corner), where the
//  trapezoidal rule would ring and the backward difference has no history.
//
//  Step sizes are MaxStep / 2^k — a ladder — so G + alpha C is factored once per
//  rung visited and kept; only a step shortened to land on a breakpoint pays a
//  factorization it throws away. Each step's error is the difference between the
//  corrector's charges and a polynomial predictor's, scaled by the method's error
//  constant (both charges and fluxes: an inductor's branch row is its flux), and
//  the next step is chosen from its ratio to the tolerance, never more than twice
//  the last.
//
//  Newton per step has two forms. With few nonlinear ports the linear part is
//  condensed onto the interface through the rung's factorization, so an iteration
//  is a small dense solve and the rest is recovered by one back-substitution
//  (the envelope's structure). With many, the whole sparse Jacobian is factored
//  each iteration. Both converge to the same equations; the choice is cost.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.HarmonicBalance;
using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;
using CSparse.Storage;

namespace CircuitRF.Engine.Transient;

public sealed class TransientEngine
{
    /// <summary>Interface ports up to which <see cref="TransientNewtonPath.Auto"/> takes the interface path (src/Engine/RESOLVED.md,
    /// brief-trn-1: measured on the three circuits R-trn1-6 names and on inverter rings of 15 to 201 stages — even at 16 ports,
    /// the full path 1.4x faster at 52 and 2x at 102, the interface path 6x faster on one FET in a 1,200-unknown network).</summary>
    public const int InterfaceThreshold = 32;

    /// <summary>The first step after a breakpoint is this many rungs below MaxStep.</summary>
    internal const int FirstStepRungs = 6;

    /// <summary>The smallest step is MaxStep / 2^this; below it the run stops.</summary>
    internal const int MinStepRungs = 40;

    /// <summary>Newton iterations a step may take before it is rejected.</summary>
    internal const int MaxNewton = 50;

    /// <summary>The conductance (S) an IC net is held by during the operating-point solve: a 1 µΩ source.</summary>
    internal const double HoldConductance = 1e6;

    /// <summary>The resistance (Ω) an inductor's IC= current is held through during the operating-point solve: its branch row
    /// reads V(a) − V(b) + K i = K I, so the current departs from I by the branch voltage over K.</summary>
    internal const double HoldResistance = 1e9;

    /// <summary>A tone source with the transient-only keys (note §6.2): Delay, Ramp (a raised-cosine rise) and Damping. Its
    /// tones are left out of the aggregate phasor columns and added at each step through <see cref="Envelope"/>, referred to
    /// t − Delay.</summary>
    private sealed class Shaped
    {
        public required ElaboratedComponent Ec;
        public required double Delay, Ramp, Damping;
        public required double[] Hz;
        public required double[][] Re, Im;

        /// <summary>0 up to Delay; the raised cosine over Ramp; times e^(−Damping (t − Delay)).</summary>
        public double Envelope(double t)
        {
            double tau = t - Delay;
            if (tau <= 0) return 0;
            double env = Ramp > 0 && tau < Ramp ? 0.5 * (1 - Math.Cos(Math.PI * tau / Ramp)) : 1;
            return Damping == 0 ? env : env * Math.Exp(-Damping * tau);
        }
    }

    private readonly ElaboratedNetlist _nl;
    private readonly AnalysisSettings _settings;

    public TransientEngine(ElaboratedNetlist netlist, AnalysisSettings? settings = null)
    {
        _nl = netlist;
        _settings = settings ?? new AnalysisSettings();
    }

    // ── the network ───────────────────────────────────────────────────────

    /// <summary>One nonlinear device: its ports' node rows (−1 at ground) and interface indices (−1 at ground).</summary>
    private sealed class Dev
    {
        public required ElaboratedComponent Ec;
        public required int P;
        public required int[] Rp, Rm, Ip, Im;
        /// <summary>The last evaluation and the port voltages it was at: Newton's final one is the converged state's, which the
        /// step's charge and error estimate then read without another evaluation (one round trip each, to a worker device).</summary>
        public double[]? LastV;
        public NonlinearResult Last;
    }

    /// <summary>G + alpha C factored, with the interface's columns of its inverse.</summary>
    private sealed class Stage
    {
        public required SparseLU Lu;
        public required CompressedColumnStorage<double> A;
        public double[][] Zcol = [];       // [interface port][row]
        public double[,] Zp = new double[0, 0];
    }

    private int _n, _nNode, _nIf;
    private CompressedColumnStorage<double> _g = null!, _c = null!;
    private Dev[] _devs = [];
    private int[] _ifRows = [];
    private double[] _srcHz = [];
    private double[][] _srcRe = [], _srcIm = [];
    private double[] _absCol = [];          // per unknown: VTol (node) or ITol (branch)
    // brief-trn-2 — each waveform source's own right-hand-side column (its value 1 at DC), each shaped tone source's own
    // phasor columns, the parts' IC= and the SDDs whose equations read `time`.
    private (ITransientWaveform Src, int[] Rows, double[] Vals)[] _waves = [];
    private Shaped[] _shaped = [];
    private (int A, int B, double V)[] _capIc = [];
    private (int Row, double I)[] _indIc = [];
    private SddModel[] _clocked = [];
    // brief-trn-3 — devices with a soft window or state between steps (a VSwitch)
    private (Dev Dev, ITransientDevice Td)[] _tdevs = [];
    private double[] _phiPeak = [];         // per row: the largest |Phi| any accepted step has had
    private TransientParams _p = null!;
    private readonly Dictionary<(double Alpha, double LineKey), Stage> _stages = new();
    private long _factorizations, _newton, _evaluations;
    private bool _useInterface;

    /// <summary>Runs <paramref name="p"/> on the netlist. Throws <see cref="TransientRefusedException"/> when an element cannot be
    /// stepped or the run cannot finish, naming why.</summary>
    public TransientRunResult Run(TransientParams p, RunControl? control = null)
    {
        try { return RunCore(p, control); }
        finally
        {
            SetClock(null);    // an SDD that reads `time` is back in steady state for whatever runs next
            foreach (var (_, td) in _tdevs) td.EndTransient();
            _real?.Dispose();  // every realized element back as the elaborator made it (brief TRN-4)
            _real = null;
        }
    }

    private TransientRunResult RunCore(TransientParams p, RunControl? control)
    {
        _p = p;
        string who = p.Name.Length > 0 ? $"transient '{p.Name}'" : "transient";
        if (!(p.StopS > 0)) throw new TransientRefusedException($"{who}: Stop must be a positive time.");

        var problems = Setup(p);
        if (problems.Count > 0)
            throw new TransientRefusedException($"{who} cannot run: " + string.Join(" ", problems));
        var ex = _ex!;
        var (probeNames, probeRows, probeNodeRows) = Envelope.EnvelopeEngine.IProbesOf(_nl);
        // ── devices and the interface ────────────────────────────────────
        _ifRows = [.. ex.InterfaceNodes.Select(nd => nd - 1)];
        _nIf = _ifRows.Length;
        var ifOf = new Dictionary<int, int>();
        for (int j = 0; j < _nIf; j++) ifOf[_ifRows[j]] = j;
        _devs = [.. _nl.Components.Where(c => c.IsNonlinear).Select(ec =>
        {
            int P = ec.Model.PortCount;
            var rp = new int[P]; var rm = new int[P]; var ip = new int[P]; var im = new int[P];
            for (int q = 0; q < P; q++)
            {
                rp[q] = (ec.Nodes.Length > 2 * q ? ec.Nodes[2 * q] : 0) - 1;
                rm[q] = (ec.Nodes.Length > 2 * q + 1 ? ec.Nodes[2 * q + 1] : 0) - 1;
                ip[q] = rp[q] >= 0 && ifOf.TryGetValue(rp[q], out int a) ? a : -1;
                im[q] = rm[q] >= 0 && ifOf.TryGetValue(rm[q], out int b2) ? b2 : -1;
            }
            return new Dev { Ec = ec, P = P, Rp = rp, Rm = rm, Ip = ip, Im = im };
        })];
        _tdevs = [.. _devs.Where(d => d.Ec.Model is ITransientDevice).Select(d => (d, (ITransientDevice)d.Ec.Model))];
        foreach (var (_, td) in _tdevs) td.EnterTransient();
        _useInterface = p.NewtonPath switch
        {
            TransientNewtonPath.Interface => true,
            TransientNewtonPath.Full => false,
            // brief TRN-8: the interface path condenses the linear partition, which is singular at a node only a device touches
            // (a BJT's internal collector, base and emitter, joined to its terminals by its own ohmic ports): every BJT run failed
            // at t = 0 until the full path was taken there.
            _ => _nIf <= InterfaceThreshold && _ifRows.All(LinearlyTouched),
        };

        // ── the step bound ───────────────────────────────────────────────
        _absCol = new double[_n];
        for (int i = 0; i < _n; i++) _absCol[i] = i < _nNode ? p.VTol : p.ITol;
        _phiPeak = new double[_n];
        double step = p.StepS > 0 ? p.StepS : p.StopS / 1000;
        var (maxStep, reason) = StepBound(p);
        double estimate = p.StopS / maxStep;
        if (estimate > p.MaxSteps)
            throw new TransientRefusedException(string.Format(CultureInfo.InvariantCulture,
                "{0}: Stop / MaxStep is {1:G3} steps, more than MaxSteps = {2}. Shorten Stop, raise MaxStep, or raise MaxSteps.",
                who, estimate, p.MaxSteps));

        // ── the starting state ───────────────────────────────────────────
        var holds = HeldRows(p.IcNets, who);
        SetClock(0);
        double[] x0 = p.Start == TransientStart.Op ? OperatingPoint(holds, who) : ZeroState(holds);
        foreach (var (d, td) in _tdevs) td.BeginTransient(PortV(d, x0));
        _lines?.Start(x0);

        // ── the recorded quantities ──────────────────────────────────────
        var rec = p.RecordRows is { } rows
            ? [.. rows([.. ex.InterfaceNodes]).Select(r => ($"row{r}", r))]
            : RecordedNodes();
        int nRec = rec.Length, nPr = probeNames.Length;
        double Rec(double[] x, int r) => r >= 0 ? x[r] : 0.0;
        double[] Snap(double[] x)
        {
            var s = new double[nRec + nPr];
            for (int i = 0; i < nRec; i++) s[i] = Rec(x, rec[i].row);
            for (int i = 0; i < nPr; i++) s[nRec + i] = x[probeRows[i]];
            return s;
        }

        var tOut = OutputGrid(p, who);
        int nOut = tOut.Length;
        var outVals = new double[nOut][];
        int nextOut = 0;
        while (nextOut < nOut && tOut[nextOut] <= 0)
        {
            outVals[nextOut++] = Snap(x0);
            p.StopWhen?.Invoke(tOut[nextOut - 1], outVals[nextOut - 1]);   // the start is never where a seed settles
        }

        var stepTimes = p.SaveSteps ? new List<double> { 0.0 } : null;
        var stepVals = p.SaveSteps ? new List<double[]> { Snap(x0)[..nRec] } : null;
        var stepKinds = p.SaveSteps ? new List<char> { 'S' } : null;

        // ── breakpoints ──────────────────────────────────────────────────
        var bps = new SortedSet<double> { p.StopS };
        foreach (var d in p.Drives) foreach (var tb in d.Breakpoints(0, p.StopS)) if (tb > 0 && tb < p.StopS) bps.Add(tb);
        foreach (var (src, _, _) in _waves) foreach (var tb in src.Breakpoints(0, p.StopS)) if (tb > 0 && tb < p.StopS) bps.Add(tb);
        foreach (var sh in _shaped)
            foreach (var tb in new[] { sh.Delay, sh.Delay + sh.Ramp }) if (tb > 0 && tb < p.StopS) bps.Add(tb);
        // brief-trn-4 R-trn4-1: τ and 2τ after every source corner (the start included), so an edge that crosses a line and its
        // reflection land on a step
        if (_lines is { } lines)
        {
            var sourceBps = bps.Where(tb => tb < p.StopS).Prepend(0.0).ToList();
            foreach (double tau in lines.Delays)
                foreach (double tb in sourceBps)
                    for (int k = 1; k <= 2; k++)
                    {
                        double td = tb + k * tau;
                        if (td > 0 && td < p.StopS && bps.Count < MaxLineBreakpoints) bps.Add(td);
                    }
        }

        // ── stepping ─────────────────────────────────────────────────────
        // History within the current segment (since the last breakpoint): time, state, total charge.
        var hT = new List<double> { 0.0 };
        var hX = new List<double[]> { x0 };
        var hPhi = new List<double[]> { Charge(x0) };
        Peak(hPhi[0]);
        double[] dPhi = new double[_n];         // the derivative of Phi at the newest point (trapezoidal history)
        int segSteps = 0;
        double t = 0;
        int rungFirst = Math.Min(FirstStepRungs, MinStepRungs);
        int k0 = rungFirst;                      // current rung: h = maxStep / 2^k0
        long accepted = 0, rejected = 0;
        int landed = 1;                          // the start
        var methodRungs = new HashSet<(int, int)>();
        // The smallest step: MaxStep / 2^40, and never so small that t + h rounds back to t (a step that cannot advance
        // time would be accepted forever).
        double hMin = Math.Max(maxStep / Math.Pow(2, MinStepRungs), p.StopS * 1e-13);
        var prevSnap = Snap(x0);
        double prevT = 0;
        double[]? prev2Snap = null;
        double prev2T = double.NaN;
        string lastFailure = "";
        control?.BeginStage("Transient", 1000, "");
        long lastTick = 0;
        bool stopped = false;

        while (t < p.StopS * (1 - 1e-15))
        {
            if ((accepted & 255) == 0) control?.ThrowIfCancellationRequested();
            if (accepted > p.MaxSteps)
                throw new TransientRefusedException(string.Format(CultureInfo.InvariantCulture,
                    "{0}: the run took more than MaxSteps = {1} steps by t = {2:G6} s.", who, p.MaxSteps, t));

            double nextBp = bps.GetViewBetween(t * (1 + 1e-15) + 1e-300, double.MaxValue).Min;
            k0 = Math.Max(k0, SoftRung(hT, hX, maxStep, k0));
            double h = maxStep / Math.Pow(2, k0);
            bool landing = false;
            if (t + h >= nextBp - 1e-3 * h) { h = nextBp - t; landing = true; }

            // The method of this step: two backward-Euler steps open every segment.
            bool be = segSteps < 2;
            int order = be ? 1 : 2;
            int nh = hT.Count;
            double alpha;
            double[] hist = new double[_n];
            var phiN = hPhi[nh - 1];
            if (be)
            {
                alpha = 1 / h;
                for (int i = 0; i < _n; i++) hist[i] = -phiN[i] / h;
            }
            else if (p.Method == TransientMethod.Trap)
            {
                alpha = 2 / h;
                for (int i = 0; i < _n; i++) hist[i] = -alpha * phiN[i] - dPhi[i];
            }
            else
            {
                double h2 = hT[nh - 1] - hT[nh - 2];
                double rho = h / h2;
                double a0 = (1 + 2 * rho) / ((1 + rho) * h), a1 = -(1 + rho) / h, a2 = rho * rho / ((1 + rho) * h);
                alpha = a0;
                var phiM = hPhi[nh - 2];
                for (int i = 0; i < _n; i++) hist[i] = a1 * phiN[i] + a2 * phiM[i];
            }

            var rhs = Rhs(t + h);
            _lines?.AddDelayed(t, h, rhs);
            for (int i = 0; i < _n; i++) rhs[i] -= hist[i];
            var stage = StageFor(alpha, h, keep: !landing);
            var guess = Predict(hT, hX, t + h);
            SetClock(t + h);
            var x = Solve(stage, alpha, rhs, guess, out string failure);

            double ratio = 0;
            double[]? phi = null;
            if (x is not null)
            {
                phi = Charge(x);
                ratio = segSteps == 0
                    ? FirstStepRatio(t, h, hX[nh - 1], phiN, phi, x, landing, methodRungs, k0)
                    : ErrorRatio(hT, hPhi, t + h, phi, x, order, be ? 0.5 : p.Method == TransientMethod.Trap ? 1.0 / 12 : 2.0 / 9);
                SetClock(t + h);
            }
            if (x is null || ratio > 1)
            {
                rejected++;
                lastFailure = failure;
                int down = x is null ? 1 : Math.Max(1, RungsFor(ratio, order));
                k0 = Math.Min(MinStepRungs + 1, (landing ? RungOf(h, maxStep) : k0) + down);
                if (maxStep / Math.Pow(2, k0) < hMin * (1 - 1e-12))
                    throw new TransientRefusedException(string.Format(CultureInfo.InvariantCulture,
                        "{0}: the step fell below {1:G3} s at t = {2:G9} s and the run stopped. {3}",
                        who, hMin, t, lastFailure.Length > 0 ? lastFailure : "The local error stayed above the tolerance."));
                continue;
            }

            // ── accepted ─────────────────────────────────────────────────
            accepted++;
            _lines?.Push(landing ? nextBp : t + h, x!, landing);
            Peak(phi!);
            foreach (var (d, td) in _tdevs) if (td.AcceptStep(PortV(d, x!))) d.LastV = null;
            if (!landing) methodRungs.Add((be ? 0 : 1, k0));
            for (int i = 0; i < _n; i++) dPhi[i] = alpha * phi![i] + hist[i];
            double tNew = landing ? nextBp : t + h;
            var snap = Snap(x);
            while (nextOut < nOut && tOut[nextOut] <= tNew * (1 + 1e-12))
            {
                outVals[nextOut] = Interpolate(tOut[nextOut], prev2T, prev2Snap, prevT, prevSnap, tNew, snap);
                nextOut++;
                // brief TRN-8 — a seed that has settled ends the run at this output point
                if (p.StopWhen is { } stop && stop(tOut[nextOut - 1], outVals[nextOut - 1])) { nOut = nextOut; stopped = true; break; }
            }
            if (stopped) break;
            stepTimes?.Add(tNew);
            stepVals?.Add(snap[..nRec]);
            stepKinds?.Add(be ? 'E' : p.Method == TransientMethod.Trap ? 'T' : 'G');
            prev2T = prevT; prev2Snap = prevSnap; prevT = tNew; prevSnap = snap;
            t = tNew;
            segSteps++;

            if (landing)
            {
                landed++;
                // a new segment: its history starts at the breakpoint, its first step low on the ladder
                hT.Clear(); hX.Clear(); hPhi.Clear();
                hT.Add(t); hX.Add(x); hPhi.Add(phi!);
                segSteps = 0;
                prev2Snap = null; prev2T = double.NaN;
                k0 = rungFirst;
            }
            else
            {
                hT.Add(t); hX.Add(x); hPhi.Add(phi!);
                if (hT.Count > 4) { hT.RemoveAt(0); hX.RemoveAt(0); hPhi.RemoveAt(0); }
                // The next step: one rung up (twice the step) when the error allows it, never above MaxStep; an accepted step is
                // never followed by a smaller one. Held through the opening backward-Euler steps.
                int delta = ratio > 0 && 0.9 * Math.Pow(ratio, -1.0 / (order + 1)) < 2 ? 0 : -1;
                if (segSteps < 2) delta = 0;
                k0 = Math.Clamp(k0 + delta, 0, MinStepRungs);
            }

            long tick = (long)(1000 * t / p.StopS);
            if (tick > lastTick) { control?.TickStage(tick - lastTick); lastTick = tick; }
        }

        // ── the result ───────────────────────────────────────────────────
        var V = new double[nOut][];
        var I = new double[nOut][];
        var Pw = new double[nOut][];
        var Vp = new double[nOut][];
        for (int k = 0; k < nOut; k++)
        {
            var s = outVals[k] ?? prevSnap;
            V[k] = s[..nRec];
            I[k] = s[nRec..];
            Pw[k] = new double[nPr];
            Vp[k] = new double[nPr];
            for (int i = 0; i < nPr; i++)
            {
                int nr = probeNodeRows[i];
                double v = nr >= 0 ? VoltageOfRow(s, rec, nr, x0) : 0;
                Vp[k][i] = v;
                Pw[k][i] = v * I[k][i];
            }
        }
        var r = new TransientRunResult
        {
            Time = tOut.Length == nOut ? tOut : tOut[..nOut], NodeNames = [.. rec.Select(z => z.name)], V = V, ProbeNames = probeNames, I = I, P = Pw, VProbe = Vp,
            OpV = [.. rec.Select(z => Rec(x0, z.row))],
            StepTime = stepTimes?.ToArray(), StepV = stepVals?.ToArray(), StepMethod = stepKinds?.ToArray(),
            AcceptedSteps = accepted, RejectedSteps = rejected, NewtonIterations = _newton, Factorizations = _factorizations,
            DeviceEvaluations = _evaluations,
            BreakpointsLanded = landed, MethodRungsVisited = methodRungs.Count,
            MaxStepS = maxStep, MaxStepReason = reason,
            NewtonPathUsed = _useInterface ? TransientNewtonPath.Interface : TransientNewtonPath.Full, InterfaceCount = _nIf,
            Fits = [.. _real?.Fits ?? []], Lines = [.. _real?.Lines ?? []], FitBandHz = _real?.FMaxHz ?? 0,
        };
        return r;

        // A probe's first node, read from the recorded nodes when it is one of them; otherwise it is the probe's own
        // (an IProbe is 0 V across, so its first node is recorded through its neighbour) and read as 0.
        static double VoltageOfRow(double[] s, (string name, int row)[] rec, int row, double[] _)
        {
            for (int i = 0; i < rec.Length; i++) if (rec[i].row == row) return s[i];
            return double.NaN;
        }
    }

    private HbLinearExtractor? _ex;
    // brief-trn-4 — the elements realized for this run (lines, fits, terminations) and the lines' delayed waves
    private TransientRealization? _real;
    private TransientLines? _lines;

    /// <summary>The most breakpoints the lines' delayed corners may bring a run to.</summary>
    internal const int MaxLineBreakpoints = 200_000;

    /// <summary>Tone context, the elements' realization and check, the real linear partition and the sources' phasors. The
    /// refusals, or none.</summary>
    private List<string> Setup(TransientParams p)
    {
        // Tone context before anything is stamped: a P1Tone drives at its own frequency, its impedance band ruler with it.
        foreach (var ec in _nl.Components)
        {
            if (ec.Model is P1ToneModel p1) p1.SetToneContext(fc: p1.FreqHz, driveFreqHz: p1.FreqHz);
            else if (ec.Model is PnToneModel pn && pn.ToneFreqsHz.Count > 0) pn.SetToneContext(fc: pn.ToneFreqsHz.Where(f => f > 0).DefaultIfEmpty(1).Min());
        }
        // brief-trn-4: lines, frequency-defined elements and terminations realized over the band the step resolves, DC to
        // f_max = 1/(2 MaxStep) (note §5.4)
        ScanSources();
        _real?.Dispose();
        _real = TransientRealization.Apply(_nl, 1 / (2 * StepBound(p).MaxStep));
        if (_real.Problems.Count > 0) return [.. _real.Problems];
        var problems = new List<string>();

        // ── linear partition, real ───────────────────────────────────────
        var ex = _ex = new HbLinearExtractor(_nl, _settings);
        var mna = ex.NewSmallSignalMna();
        _real.Stepping = true;
        CompressedColumnStorage<Complex> gC, cC;
        try { (gC, cC) = AffineStamp.Baseband(ex, mna, AffineStamp.Delta(0)); }
        finally { _real.Stepping = false; }
        _lines = _real.LineModels.Count > 0 ? new TransientLines(_real.LineModels) : null;   // their rows as this stamp numbered them
        _n = gC.RowCount;
        _nNode = ex.NonGroundCount;
        _g = Real(gC);
        _c = Real(cC);

        // ── sources: each frequency's phasor right-hand side ─────────────
        // A waveform source and a shaped tone source each get columns of their own (brief TRN-2), so the aggregate is built with
        // every one of them silent: a waveform source stamping 0 at DC, a shaped tone at DriveScale 0. Each one's column is then
        // the aggregate with it alone switched on, less the aggregate — exact, the right-hand side being linear in each source.
        var waves = _nl.Components.Where(c => c.Model is ITransientWaveform).ToList();
        var shaped = new List<(ElaboratedComponent Ec, IDriveScalable Src, double Delay, double Ramp, double Damping, double[] Hz)>();
        var freqs = new SortedSet<double> { 0.0 };
        foreach (var ec in _nl.Components)
        {
            double[] hz = ec.Model switch
            {
                ToneSourceModelBase ts => [.. ts.ToneFreqsHz.Where(f => f > 0)],
                P1ToneModel p1 when p1.FreqHz > 0 => [p1.FreqHz],
                PnToneModel pn => [.. pn.ToneFreqsHz.Where(f => f > 0)],
                _ => [],
            };
            foreach (var f in hz) freqs.Add(f);
            if (ec.Model is not IDriveScalable ds || hz.Length == 0) continue;
            double delay = Key(ec, "Delay"), ramp = Key(ec, "Ramp"), damping = Key(ec, "Damping");
            if (delay < 0 || ramp < 0)
            {
                problems.Add($"{ec.InstancePath}: {(delay < 0 ? "Delay" : "Ramp")} must not be negative.");
                continue;
            }
            if (delay != 0 || ramp != 0 || damping != 0) shaped.Add((ec, ds, delay, ramp, damping, hz));
        }
        if (problems.Count > 0) return problems;

        var scales = shaped.Select(sh => sh.Src.DriveScale).ToArray();
        try
        {
            foreach (var w in waves) ((ITransientWaveform)w.Model).StampOverride = 0;
            foreach (var sh in shaped) sh.Src.DriveScale = 0;
            _srcHz = [.. freqs];
            _srcRe = new double[_srcHz.Length][];
            _srcIm = new double[_srcHz.Length][];
            for (int k = 0; k < _srcHz.Length; k++)
            {
                var b = ex.BuildSourceRhs(2 * Math.PI * _srcHz[k]);
                _srcRe[k] = [.. b.Select(z => z.Real)];
                _srcIm[k] = [.. b.Select(z => z.Imaginary)];
            }
            _waves = new (ITransientWaveform, int[], double[])[waves.Count];
            for (int i = 0; i < waves.Count; i++)
            {
                var src = (ITransientWaveform)waves[i].Model;
                src.StampOverride = 1;
                var b = ex.BuildSourceRhs(0);
                src.StampOverride = 0;
                var rows = new List<int>(); var vals = new List<double>();
                for (int r = 0; r < _n; r++)
                {
                    double d = b[r].Real - _srcRe[0][r];
                    if (d != 0) { rows.Add(r); vals.Add(d); }
                }
                _waves[i] = (src, [.. rows], [.. vals]);
            }
            _shaped = new Shaped[shaped.Count];
            for (int i = 0; i < shaped.Count; i++)
            {
                var sh = shaped[i];
                sh.Src.DriveScale = scales[i];
                var re = new double[sh.Hz.Length][];
                var im = new double[sh.Hz.Length][];
                for (int k = 0; k < sh.Hz.Length; k++)
                {
                    int agg = Array.IndexOf(_srcHz, sh.Hz[k]);
                    var b = ex.BuildSourceRhs(2 * Math.PI * sh.Hz[k]);
                    re[k] = new double[_n]; im[k] = new double[_n];
                    for (int r = 0; r < _n; r++) { re[k][r] = b[r].Real - _srcRe[agg][r]; im[k][r] = b[r].Imaginary - _srcIm[agg][r]; }
                }
                sh.Src.DriveScale = 0;
                _shaped[i] = new Shaped { Ec = sh.Ec, Delay = sh.Delay, Ramp = sh.Ramp, Damping = sh.Damping, Hz = sh.Hz, Re = re, Im = im };
            }
        }
        finally
        {
            foreach (var w in waves) ((ITransientWaveform)w.Model).StampOverride = null;
            for (int i = 0; i < shaped.Count; i++) shaped[i].Src.DriveScale = scales[i];
        }

        // ── IC= on capacitors (volts) and inductors (amperes), note §7.2 ─
        var capIc = new List<(int, int, double)>();
        var indIc = new List<(int, double)>();
        foreach (var ec in _nl.Components)
        {
            if (!ec.Parameters.TryGetValue("IC", out var icv) || icv.Kind is not (ValueKind.Real or ValueKind.Complex)) continue;
            double ic = icv.Kind == ValueKind.Real ? icv.AsReal() : icv.AsComplex().Real;
            if (ec.Model is CapacitorModel && ec.Nodes.Length >= 2) capIc.Add((ec.Nodes[0] - 1, ec.Nodes[1] - 1, ic));
            else if (ec.Model is InductorModel ind && ind.LastBranchIndex >= 0) indIc.Add((ind.LastBranchIndex, ic));
        }
        _capIc = [.. capIc];
        _indIc = [.. indIc];
        _clocked = [.. _nl.Components.Select(c => c.Model).OfType<SddModel>().Where(m => m.UsesTime)];

        return problems;
    }

    private double _waveHz, _toneHzMax;

    /// <summary>The highest tone frequency and the highest waveform frequency any source carries: what the step bound reads,
    /// known before anything is stamped.</summary>
    private void ScanSources()
    {
        double hi = 0;
        foreach (var ec in _nl.Components)
        {
            IEnumerable<double> hz = ec.Model switch
            {
                ToneSourceModelBase ts => ts.ToneFreqsHz,
                P1ToneModel p1 => [p1.FreqHz],
                PnToneModel pn => pn.ToneFreqsHz,
                _ => [],
            };
            foreach (var f in hz) if (f > hi) hi = f;
        }
        _toneHzMax = hi;
        _waveHz = _nl.Components.Select(c => c.Model).OfType<ITransientWaveform>().Select(w => w.HighestFrequencyHz).DefaultIfEmpty(0).Max();
    }

    /// <summary>A device's port voltages in state <paramref name="x"/>.</summary>
    private static double[] PortV(Dev d, double[] x)
    {
        var v = new double[d.P];
        for (int q = 0; q < d.P; q++) v[q] = (d.Rp[q] >= 0 ? x[d.Rp[q]] : 0) - (d.Rm[q] >= 0 ? x[d.Rm[q]] : 0);
        return v;
    }

    /// <summary>
    /// The soft breakpoints (note §7.1, brief TRN-3 R-trn3-3): the smallest ladder rung that keeps every soft window's crossing
    /// to at least its Points accepted steps. A device's control voltage is extrapolated along its slope over the segment's last
    /// two steps; when the step from <paramref name="k0"/>'s rung would reach into the window, the step is limited to
    /// window / (Points · |slope|). Nothing is landed on. With fewer than two points in the segment there is no slope and no
    /// limit — a segment's first steps are low on the ladder anyway.
    /// </summary>
    private int SoftRung(List<double> hT, List<double[]> hX, double maxStep, int k0)
    {
        int m = hT.Count, k = k0;
        if (m < 2 || _tdevs.Length == 0) return k;
        double dt = hT[m - 1] - hT[m - 2];
        if (!(dt > 0)) return k;
        foreach (var (d, td) in _tdevs)
        {
            if (td.SoftWindow is not { } w || w.Port >= d.P) continue;
            double v1 = PortV(d, hX[m - 1])[w.Port], v0 = PortV(d, hX[m - 2])[w.Port];
            double rate = (v1 - v0) / dt;
            if (rate == 0 || !double.IsFinite(rate)) continue;
            double hNow = maxStep / Math.Pow(2, k);
            double vEnd = v1 + rate * hNow;
            if (Math.Max(v1, vEnd) < w.Lo || Math.Min(v1, vEnd) > w.Hi) continue;   // the step stays clear of the window
            double hLim = (w.Hi - w.Lo) / (w.Points * Math.Abs(rate));
            if (hLim < hNow) k = Math.Max(k, Math.Min(MinStepRungs, RungOf(hLim, maxStep)));
        }
        return k;
    }

    private static double Key(ElaboratedComponent ec, string key) =>
        ec.Parameters.TryGetValue(key, out var v) && v.Kind is ValueKind.Real or ValueKind.Complex
            ? (v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real) : 0;

    /// <summary>Sets <c>time</c> for every SDD that reads it, and forgets their last evaluation (it was at another time).</summary>
    private void SetClock(double? t)
    {
        if (_clocked.Length == 0) return;
        foreach (var m in _clocked) m.Time = t;
        foreach (var d in _devs) if (d.Ec.Model is SddModel { UsesTime: true }) d.LastV = null;
    }

    /// <summary>The largest step and why (note §3): MaxStep as stated, else the smaller of the output step and 1/20 of the
    /// highest source frequency's period.</summary>
    private (double MaxStep, string Reason) StepBound(TransientParams p)
    {
        double step = p.StepS > 0 ? p.StepS : p.StopS / 1000;
        double fHi = Math.Max(_toneHzMax, _waveHz);
        if (p.MaxStepS > 0) return (p.MaxStepS, "MaxStep as stated");
        if (fHi > 0 && 1 / (20 * fHi) < step)
            return (1 / (20 * fHi), string.Format(CultureInfo.InvariantCulture, "1/20 of the period of the highest source frequency, {0:G4} Hz", fHi));
        return (step, "the output Step");
    }

    /// <summary>What a run of <paramref name="p"/> would be, solving nothing: its step bound and why, the steps that bound
    /// implies, the circuit's slowest time constant and what holds it, and the refusals (explain, R-trn1-11).</summary>
    public TransientPlan Plan(TransientParams p)
    {
        try
        {
            var problems = Setup(p);
            if (problems.Count > 0) return new TransientPlan(0, "", 0, null, null, problems);
            var (maxStep, reason) = StepBound(p);
            var slow = Envelope.EnvelopeMemory.Declared(_nl);
            var pole = SlowestPole(p.StopS);
            if (pole is { } pl && (slow is null || pl.Seconds > slow.Seconds)) slow = pl;
            return new TransientPlan(maxStep, reason, p.StopS / maxStep, slow?.Seconds, slow?.What, problems);
        }
        finally
        {
            _real?.Dispose();
            _real = null;
        }
    }

    /// <summary>The linear network's slowest pole: power iteration on (G + σC)⁻¹C, σ = 1e-3/Stop, as the envelope's memory
    /// estimate does on its baseband product. Null when nothing stores energy or the network does not factor.</summary>
    private Envelope.EnvelopeMemory.Slowest? SlowestPole(double stopS)
    {
        if (_c.NonZerosCount == 0) return null;
        double sigma = 1e-3 / stopS;
        SparseLU lu;
        try { lu = SparseLU.Create(Combine(_g, _c, sigma), ColumnOrdering.MinimumDegreeAtPlusA, 1.0); }
        catch (Exception) { return null; }
        var x = new double[_n];
        for (int i = 0; i < _n; i++) x[i] = 1.0 + 0.5 * Math.Sin(1.7 * i);
        var cx = new double[_n];
        var y = new double[_n];
        double gainPrev = double.NaN, mu = double.NaN, muPrev = double.NaN;
        for (int it = 0; it < 60; it++)
        {
            Array.Clear(cx);
            Mul(_c, x, cx);
            lu.Solve(cx, y);
            double norm = Math.Sqrt(y.Sum(v => v * v));
            if (!(norm > 0) || !double.IsFinite(norm)) return null;
            for (int i = 0; i < _n; i++) y[i] /= norm;
            (x, y) = (y, x);
            if (!double.IsNaN(gainPrev)) mu = Math.Sqrt(norm * gainPrev);
            gainPrev = norm;
            if (it > 4 && Math.Abs(mu - muPrev) <= 1e-4 * mu) break;
            muPrev = mu;
        }
        if (!(mu > 0)) return null;
        double inv = 1 / mu - sigma;
        double tau = inv > 1e-3 * sigma ? 1 / inv : 1e3 / sigma;
        int node = 0; double big = -1;
        for (int i = 0; i < _nNode; i++) if (Math.Abs(x[i]) > big) { big = Math.Abs(x[i]); node = i + 1; }
        return new Envelope.EnvelopeMemory.Slowest(tau, node > 0 ? $"the network at node '{_nl.Nodes.NameOf(node)}'" : "the linear network");
    }

    // ── pieces ────────────────────────────────────────────────────────────

    /// <summary>The nets a run records: every labelled net, or every user-facing net when none is labelled.</summary>
    private (string name, int row)[] RecordedNodes() => RecordedNodes(_nl);

    /// <summary>The nets a run on <paramref name="nl"/> records, with their rows (node − 1), in node order. brief TRN-7 — the
    /// ngspice solver records the same nets.</summary>
    internal static (string name, int row)[] RecordedNodes(ElaboratedNetlist nl)
    {
        var set = new SortedDictionary<int, string>();
        foreach (var nm in nl.Nodes.LabeledNames)
            if (nl.Nodes.TryGetIndex(nm, out int nd) && nd > 0 && !set.ContainsKey(nd)) set[nd] = nm;
        if (set.Count == 0)
        {
            var (rows, names) = nl.Nodes.ResultRows(excludeInternal: true);
            for (int i = 0; i < rows.Length; i++) if (rows[i] > 0 && !set.ContainsKey(rows[i])) set[rows[i]] = names[i];
        }
        // every IProbe's first node too, so its power is v·i at a recorded node
        foreach (var ec in nl.Components)
            if (ec.Model is IProbeModel && ec.Nodes.Length >= 2 && ec.Nodes[0] > 0 && !set.ContainsKey(ec.Nodes[0]))
                set[ec.Nodes[0]] = nl.Nodes.NameOf(ec.Nodes[0]);
        return [.. set.Select(kv => (kv.Value, kv.Key - 1))];
    }

    /// <summary>The rows of the IC nets and their values.</summary>
    private List<(int row, double v)> HeldRows(IReadOnlyDictionary<string, double> ic, string who)
    {
        var list = new List<(int, double)>();
        foreach (var (name, v) in ic)
        {
            if (!_nl.Nodes.TryGetIndex(name, out int nd) || nd <= 0)
                throw new TransientRefusedException($"{who}: IC names '{name}', which is not a net of the circuit.");
            list.Add((nd - 1, v));
        }
        return list;
    }

    private double[] ZeroState(List<(int row, double v)> holds)
    {
        BiasFreeze.Ensure(_nl, _settings);
        var x = new double[_n];
        foreach (var (row, v) in holds) x[row] = v;
        // the parts' IC=: a capacitor's voltage on its first node over its second's, an inductor's current on its branch
        foreach (var (a, b, v) in _capIc)
        {
            if (a >= 0) x[a] = v + (b >= 0 ? x[b] : 0);
            else if (b >= 0) x[b] = -v;
        }
        foreach (var (row, i) in _indIc) x[row] = i;
        return x;
    }

    /// <summary>
    /// The DC operating point in this engine's own equations (alpha = 0), the IC nets held: seeded from
    /// <see cref="NonlinearDcEngine"/>'s solution when there is a nonlinear device, so the run starts at the
    /// equilibrium its own discretization has.
    /// </summary>
    private double[] OperatingPoint(List<(int row, double v)> holds, string who)
    {
        var x = new double[_n];
        if (_devs.Length > 0)
        {
            NonlinearDcEngine.DcResult? dc = null;
            try { dc = NonlinearDcEngine.Run(_nl, _settings); }
            catch (NonlinearDcNotConvergedException) { }
            BiasFreeze.Apply(_nl, dc is { Converged: true } ? dc.NodeVoltages : null, dc?.NodeVoltages);
            if (dc is not null)
                for (int i = 0; i < Math.Min(_nNode, dc.NodeVoltages.Length); i++) x[i] = dc.NodeVoltages[i];
            else if (holds.Count == 0)
                throw new TransientRefusedException($"{who}: the DC operating point did not converge. InitialState=zero starts the run without one.");
        }
        foreach (var (row, v) in holds) x[row] = v;

        // alpha = 0: G x + i(x) = b(0) (+ the holds), solved by the full Newton. A capacitor's IC= is held across it by the same
        // stiff conductance as a net's (a Norton source of G·V); an inductor's by K on its branch row, which then reads
        // V(a) − V(b) + K i = K I (note §4.5, §7.2). All are released at t = 0⁺.
        var a = _g;
        var extra = holds.Select(hd => (hd.row, hd.row, HoldConductance)).ToList();
        var rhs = Rhs(0);
        foreach (var (row, v) in holds) rhs[row] += HoldConductance * v;
        foreach (var (ra, rb, v) in _capIc)
        {
            if (ra >= 0) { extra.Add((ra, ra, HoldConductance)); rhs[ra] += HoldConductance * v; }
            if (rb >= 0) { extra.Add((rb, rb, HoldConductance)); rhs[rb] -= HoldConductance * v; }
            if (ra >= 0 && rb >= 0) { extra.Add((ra, rb, -HoldConductance)); extra.Add((rb, ra, -HoldConductance)); }
        }
        foreach (var (row, i) in _indIc) { extra.Add((row, row, HoldResistance)); rhs[row] += HoldResistance * i; }
        if (_lines is not null) extra.AddRange(_lines.Coupling(double.PositiveInfinity));   // a line's steady state: the wave now
        if (extra.Count > 0) a = Add(_g, extra);
        var stage = new Stage { Lu = Factor(a), A = a };
        var sol = SolveFull(stage, 0, rhs, x, out string failure);
        return sol ?? throw new TransientRefusedException($"{who}: the starting state did not converge. {failure} InitialState=zero starts the run without one.");
    }

    /// <summary>b(t): every source's phasors at its own frequencies, plus the drives.</summary>
    private double[] Rhs(double t)
    {
        var b = new double[_n];
        for (int k = 0; k < _srcHz.Length; k++)
        {
            double w = 2 * Math.PI * _srcHz[k] * t, cw = Math.Cos(w), sw = Math.Sin(w);
            var re = _srcRe[k]; var im = _srcIm[k];
            if (_srcHz[k] == 0) for (int i = 0; i < _n; i++) b[i] += re[i];
            else for (int i = 0; i < _n; i++) b[i] += re[i] * cw - im[i] * sw;
        }
        foreach (var (src, rows, vals) in _waves)
        {
            double v = src.ValueAt(t);
            if (v == 0) continue;
            for (int i = 0; i < rows.Length; i++) b[rows[i]] += v * vals[i];
        }
        foreach (var sh in _shaped)
        {
            double env = sh.Envelope(t);
            if (env == 0) continue;
            for (int k = 0; k < sh.Hz.Length; k++)
            {
                double w = 2 * Math.PI * sh.Hz[k] * (t - sh.Delay), cw = env * Math.Cos(w), sw = env * Math.Sin(w);
                var re = sh.Re[k]; var im = sh.Im[k];
                for (int i = 0; i < _n; i++) b[i] += re[i] * cw - im[i] * sw;
            }
        }
        foreach (var d in _p.Drives) d.AddTo(t, b);
        return b;
    }

    /// <summary>The total charge Phi = C x + q(x): node rows in coulombs, inductor branch rows in webers.</summary>
    private double[] Charge(double[] x)
    {
        var phi = new double[_n];
        Mul(_c, x, phi);
        foreach (var d in _devs)
        {
            var r = Eval(d, x);
            for (int q = 0; q < d.P; q++)
            {
                if (d.Rp[q] >= 0) phi[d.Rp[q]] += r.Q[q];
                if (d.Rm[q] >= 0) phi[d.Rm[q]] -= r.Q[q];
            }
        }
        return phi;
    }

    /// <summary>
    /// The step's local error over its tolerance, largest over every row that stores charge or flux: the corrector's charge
    /// against the predictor's, scaled to the corrector's error by the two error constants. 0 when the segment has too little
    /// history to predict from (its first step).
    /// </summary>
    private double ErrorRatio(List<double> hT, List<double[]> hPhi, double tNew, double[] phi, double[] x, int order, double cm)
    {
        int need = order + 1;
        if (hT.Count < need) return 0;
        int m = hT.Count;
        var ts = new double[need];
        var ps = new double[need][];
        for (int j = 0; j < need; j++) { ts[j] = hT[m - 1 - j]; ps[j] = hPhi[m - 1 - j]; }
        // the predictor's error constant: prod (tNew - t_j) / (order+1)!; the corrector's: cm h^(order+1)
        double h = tNew - ts[0];
        double ep = 1;
        for (int j = 0; j < need; j++) ep *= tNew - ts[j];
        ep /= order == 1 ? 2 : 6;
        double ec = cm * Math.Pow(h, order + 1);
        double scale = ec / (ec + Math.Abs(ep));
        // Lagrange weights at tNew
        var wts = new double[need];
        for (int j = 0; j < need; j++)
        {
            double wj = 1;
            for (int l = 0; l < need; l++) if (l != j) wj *= (tNew - ts[l]) / (ts[j] - ts[l]);
            wts[j] = wj;
        }
        var absTol = AbsTol(x);
        double worst = 0;
        for (int i = 0; i < _n; i++)
        {
            if (absTol[i] == 0) continue;            // stores nothing
            double pred = 0;
            for (int j = 0; j < need; j++) pred += wts[j] * ps[j][i];
            double err = scale * Math.Abs(phi[i] - pred);
            double tol = _p.RelTol * Math.Max(Math.Max(Math.Abs(phi[i]), Math.Abs(ps[0][i])), _phiPeak[i]) + absTol[i];
            worst = Math.Max(worst, err / tol);
        }
        return worst;
    }

    /// <summary>The absolute tolerance of each row's charge at <paramref name="x"/>: the element's own scale (Σ |C_ij| tol_j),
    /// the devices' dq/dv included. 0 for a row that stores nothing.</summary>
    private double[] AbsTol(double[] x)
    {
        var absTol = new double[_n];
        for (int j = 0; j < _c.ColumnCount; j++)
            for (int pp = _c.ColumnPointers[j]; pp < _c.ColumnPointers[j + 1]; pp++)
                absTol[_c.RowIndices[pp]] += Math.Abs(_c.Values[pp]) * _absCol[j];
        foreach (var d in _devs)
        {
            var r = Eval(d, x);
            for (int q = 0; q < d.P; q++)
            {
                double s = 0;
                for (int u = 0; u < d.P; u++) s += Math.Abs(r.Dc[q, u]);
                if (d.Rp[q] >= 0) absTol[d.Rp[q]] += s * _p.VTol;
                if (d.Rm[q] >= 0) absTol[d.Rm[q]] += s * _p.VTol;
            }
        }
        return absTol;
    }

    /// <summary>
    /// The local error over its tolerance of a segment's FIRST step — a backward-Euler step from a breakpoint, where there is
    /// no history to predict from — by step doubling: the same interval in two backward-Euler half steps, whose charge differs
    /// from the full step's by half the full step's own error (both errors are C·h², the halves' summing to C·h²/2). Before
    /// this the first step was never checked, and its damping, ∝ (ω·MaxStep/64)², was outside the tolerance whatever RelTol
    /// said (src/Engine/RESOLVED.md, brief-trn-4b). The half-step stage is the trapezoidal stage of the same rung, so it costs
    /// no factorization a run that reaches that rung does not already make. A half step that fails to converge counts as an
    /// error above the tolerance. A line's delayed wave in the second half step reads the history as it stood at t — an
    /// approximation within the estimate only.
    /// </summary>
    private double FirstStepRatio(double t, double h, double[] x0, double[] phi0, double[] phiFull, double[] xFull, bool landing,
                                  HashSet<(int, int)> methodRungs, int k0)
    {
        double hh = 0.5 * h, a = 1 / hh;
        var st = StageFor(a, hh, keep: !landing);
        if (!landing) methodRungs.Add((0, k0 + 1));
        var rhs = Rhs(t + hh);
        _lines?.AddDelayed(t, hh, rhs);
        for (int i = 0; i < _n; i++) rhs[i] += phi0[i] / hh;
        SetClock(t + hh);
        var guess = new double[_n];
        for (int i = 0; i < _n; i++) guess[i] = 0.5 * (x0[i] + xFull[i]);
        var xm = Solve(st, a, rhs, guess, out _);
        if (xm is null) return 1e6;
        var phim = Charge(xm);
        rhs = Rhs(t + h);
        _lines?.AddDelayed(t + hh, hh, rhs);
        for (int i = 0; i < _n; i++) rhs[i] += phim[i] / hh;
        SetClock(t + h);
        var x2 = Solve(st, a, rhs, xFull, out _);
        if (x2 is null) return 1e6;
        var phi2 = Charge(x2);
        var absTol = AbsTol(xFull);
        double worst = 0;
        for (int i = 0; i < _n; i++)
        {
            if (absTol[i] == 0) continue;
            double err = 2 * Math.Abs(phiFull[i] - phi2[i]);
            double tol = _p.RelTol * Math.Max(Math.Max(Math.Abs(phiFull[i]), Math.Abs(phi0[i])), _phiPeak[i]) + absTol[i];
            worst = Math.Max(worst, err / tol);
        }
        return worst;
    }

    /// <summary>Raises each row's running peak of |Phi|: the scale its error is relative to. A row whose charge passes through zero,
    /// or deep in a ladder where it is small beside the drive, is judged against the size its own signal reaches, not against an
    /// instantaneous value round-off can exceed (an inductor's absolute flux tolerance is L·ITol, ~1e-22 Wb at 0.1 nH).</summary>
    private void Peak(double[] phi)
    {
        for (int i = 0; i < _n; i++) { double a = Math.Abs(phi[i]); if (a > _phiPeak[i]) _phiPeak[i] = a; }
    }

    /// <summary>How many rungs to move for an error ratio: positive is down (smaller steps).</summary>
    private static int RungsFor(double ratio, int order)
    {
        // ideal step factor 0.9 ratio^(-1/(order+1)); a rung is a factor of two
        double f = 0.9 * Math.Pow(Math.Max(ratio, 1e-12), -1.0 / (order + 1));
        return (int)Math.Ceiling(-Math.Log2(f) - 1e-12);
    }

    private static int RungOf(double h, double maxStep) => Math.Max(0, (int)Math.Ceiling(Math.Log2(maxStep / h) - 1e-9));

    /// <summary>The new state's guess: the segment's history extrapolated (linearly), or the last state.</summary>
    private static double[] Predict(List<double> hT, List<double[]> hX, double tNew)
    {
        int m = hT.Count;
        if (m < 2) return (double[])hX[m - 1].Clone();
        double t1 = hT[m - 1], t0 = hT[m - 2];
        double f = (tNew - t1) / (t1 - t0);
        var x1 = hX[m - 1]; var x0 = hX[m - 2];
        var g = new double[x1.Length];
        for (int i = 0; i < g.Length; i++) g[i] = x1[i] + f * (x1[i] - x0[i]);
        return g;
    }

    /// <summary>The uniform output grid (D11): Start to Stop at Step, Stop always the last point. brief TRN-7 — the ngspice solver
    /// reports on the same grid.</summary>
    internal static double[] OutputGrid(TransientParams p, string who)
    {
        double step = p.StepS > 0 ? p.StepS : p.StopS / 1000;
        double start = Math.Max(0, p.StartS);
        if (start >= p.StopS) throw new TransientRefusedException($"{who}: Start must be before Stop.");
        int nOut = (int)Math.Floor((p.StopS - start) / step + 1e-9) + 1;
        var tOut = new double[nOut];
        for (int k = 0; k < nOut; k++) tOut[k] = start + k * step;
        if (p.StopS - tOut[^1] > 1e-9 * step) { Array.Resize(ref tOut, nOut + 1); tOut[^1] = p.StopS; }
        return tOut;
    }

    /// <summary>The value at <paramref name="t"/> between accepted steps: quadratic through the last three in the segment, else
    /// linear between the last two. brief TRN-7 R-trn7-5 — the ngspice solver's points are resampled through this too.</summary>
    internal static double[] Interpolate(double t, double t0, double[]? s0, double t1, double[] s1, double t2, double[] s2)
    {
        var o = new double[s2.Length];
        if (t2 == t1) { Array.Copy(s2, o, o.Length); return o; }
        if (s0 is not null && t0 < t1)
        {
            double l0 = (t - t1) * (t - t2) / ((t0 - t1) * (t0 - t2));
            double l1 = (t - t0) * (t - t2) / ((t1 - t0) * (t1 - t2));
            double l2 = (t - t0) * (t - t1) / ((t2 - t0) * (t2 - t1));
            for (int i = 0; i < o.Length; i++) o[i] = l0 * s0[i] + l1 * s1[i] + l2 * s2[i];
        }
        else
        {
            double f = (t - t1) / (t2 - t1);
            for (int i = 0; i < o.Length; i++) o[i] = s1[i] + f * (s2[i] - s1[i]);
        }
        return o;
    }

    // ── Newton ────────────────────────────────────────────────────────────

    private double[]? Solve(Stage stage, double alpha, double[] rhs, double[] guess, out string failure)
    {
        if (_devs.Length == 0)
        {
            failure = "";
            _newton++;
            var x = new double[_n];
            stage.Lu.Solve((double[])rhs.Clone(), x);
            return Finite(x) ? x : null;
        }
        return _useInterface ? SolveInterface(stage, alpha, rhs, guess, out failure) : SolveFull(stage, alpha, rhs, guess, out failure);
    }

    /// <summary>
    /// The interface path: x = A⁻¹(rhs − E ι(v)), so v = v_oc − Zp ι(v) on the interface, with ι = i + alpha q injected into
    /// the device nodes. Newton on g(v) = v − v_oc + Zp ι(v), a dense N×N system.
    /// </summary>
    private double[]? SolveInterface(Stage st, double alpha, double[] rhs, double[] guess, out string failure)
    {
        EnsureInterface(st);
        int N = _nIf;
        var xoc = new double[_n];
        st.Lu.Solve((double[])rhs.Clone(), xoc);
        var v = new double[N];
        for (int j = 0; j < N; j++) v[j] = guess[_ifRows[j]];
        var iota = new double[N];
        var D = new double[N, N];
        var g = new double[N];
        double[]? dv = null;
        bool updateOk = false;
        for (int it = 0; it < MaxNewton; it++)
        {
            _newton++;
            if (!InterfaceEval(v, alpha, iota, D, out failure)) return null;
            bool resOk = true;
            for (int j = 0; j < N; j++)
            {
                double s = v[j] - xoc[_ifRows[j]];
                for (int l = 0; l < N; l++) s += st.Zp[j, l] * iota[l];
                g[j] = s;
                if (!(Math.Abs(s) <= _p.RelTol * Math.Abs(v[j]) + _p.VTol)) resOk = false;
            }
            if (it > 0 && updateOk && resOk)
            {
                var x = (double[])xoc.Clone();
                for (int l = 0; l < N; l++)
                {
                    if (iota[l] == 0) continue;
                    var z = st.Zcol[l];
                    for (int i = 0; i < _n; i++) x[i] -= z[i] * iota[l];
                }
                failure = "";
                return Finite(x) ? x : null;
            }
            // J = I + Zp D. D holds entries only between a device's own ports, so each column of Zp D is a few columns of Zp:
            // O(N · nnz(D)) rather than N³.
            var J = new double[N, N];
            for (int j = 0; j < N; j++) J[j, j] = 1;
            for (int m = 0; m < N; m++)
                for (int l = 0; l < N; l++)
                {
                    double dml = D[m, l];
                    if (dml == 0) continue;
                    for (int j = 0; j < N; j++) J[j, l] += st.Zp[j, m] * dml;
                }
            dv = DenseSolve(J, g);
            if (dv is null) { failure = "The interface Jacobian was singular."; return null; }
            updateOk = true;
            for (int j = 0; j < N; j++)
            {
                v[j] -= dv[j];
                if (!(Math.Abs(dv[j]) <= _p.RelTol * Math.Abs(v[j]) + _p.VTol)) updateOk = false;
            }
            if (!Finite(v)) { failure = "Newton diverged."; return null; }
        }
        failure = NotConverged(dv, j => _ifRows[j]);
        return null;
    }

    /// <summary>ι(v) and ∂ι/∂v on the interface: each device's i + alpha q injected at its nodes.</summary>
    private bool InterfaceEval(double[] v, double alpha, double[] iota, double[,] D, out string failure)
    {
        Array.Clear(iota);
        Array.Clear(D);
        foreach (var d in _devs)
        {
            var pv = new double[d.P];
            for (int q = 0; q < d.P; q++) pv[q] = (d.Ip[q] >= 0 ? v[d.Ip[q]] : 0) - (d.Im[q] >= 0 ? v[d.Im[q]] : 0);
            NonlinearResult r;
            try { r = Evaluate(d, pv); }
            catch (ExternalDeviceException ex) when (ex is not DeviceWorkerExitedException) { failure = $"{d.Ec.InstancePath} refused a point: {ex.Message}"; return false; }
            for (int q = 0; q < d.P; q++)
            {
                double cur = r.I[q] + alpha * r.Q[q];
                if (d.Ip[q] >= 0) iota[d.Ip[q]] += cur;
                if (d.Im[q] >= 0) iota[d.Im[q]] -= cur;
                for (int u = 0; u < d.P; u++)
                {
                    double dd = r.Dg[q, u] + alpha * r.Dc[q, u];
                    if (dd == 0) continue;
                    Corner(D, d.Ip[q], d.Im[q], d.Ip[u], d.Im[u], dd);
                }
            }
        }
        failure = "";
        return true;
    }

    private static void Corner(double[,] D, int rp, int rm, int cp, int cm, double v)
    {
        if (rp >= 0 && cp >= 0) D[rp, cp] += v;
        if (rp >= 0 && cm >= 0) D[rp, cm] -= v;
        if (rm >= 0 && cp >= 0) D[rm, cp] -= v;
        if (rm >= 0 && cm >= 0) D[rm, cm] += v;
    }

    /// <summary>The full path: Newton on F(x) = A x + ι(x) − rhs with the whole sparse Jacobian factored each iteration.</summary>
    private double[]? SolveFull(Stage st, double alpha, double[] rhs, double[] guess, out string failure)
    {
        var x = (double[])guess.Clone();
        double[]? dx = null;
        bool updateOk = false;
        var ax = new double[_n];
        for (int it = 0; it < MaxNewton; it++)
        {
            _newton++;
            // F and its row scales
            Array.Clear(ax);
            Mul(st.A, x, ax);
            var scale = new double[_n];
            MulAbs(st.A, x, scale);
            var f = new double[_n];
            for (int i = 0; i < _n; i++) { f[i] = ax[i] - rhs[i]; scale[i] += Math.Abs(rhs[i]); }
            var trip = new List<(int, int, double)>();
            foreach (var d in _devs)
            {
                var pv = new double[d.P];
                for (int q = 0; q < d.P; q++) pv[q] = (d.Rp[q] >= 0 ? x[d.Rp[q]] : 0) - (d.Rm[q] >= 0 ? x[d.Rm[q]] : 0);
                NonlinearResult r;
                try { r = Evaluate(d, pv); }
                catch (ExternalDeviceException ex) when (ex is not DeviceWorkerExitedException) { failure = $"{d.Ec.InstancePath} refused a point: {ex.Message}"; return null; }
                for (int q = 0; q < d.P; q++)
                {
                    double cur = r.I[q] + alpha * r.Q[q];
                    if (d.Rp[q] >= 0) { f[d.Rp[q]] += cur; scale[d.Rp[q]] += Math.Abs(cur); }
                    if (d.Rm[q] >= 0) { f[d.Rm[q]] -= cur; scale[d.Rm[q]] += Math.Abs(cur); }
                    for (int u = 0; u < d.P; u++)
                    {
                        double dd = r.Dg[q, u] + alpha * r.Dc[q, u];
                        if (dd == 0) continue;
                        int rp = d.Rp[q], rm = d.Rm[q], cp = d.Rp[u], cm = d.Rm[u];
                        if (rp >= 0 && cp >= 0) trip.Add((rp, cp, dd));
                        if (rp >= 0 && cm >= 0) trip.Add((rp, cm, -dd));
                        if (rm >= 0 && cp >= 0) trip.Add((rm, cp, -dd));
                        if (rm >= 0 && cm >= 0) trip.Add((rm, cm, dd));
                    }
                }
            }
            bool resOk = true;
            for (int i = 0; i < _n; i++)
                if (!(Math.Abs(f[i]) <= _p.RelTol * scale[i] + (i < _nNode ? _p.ITol : _p.VTol))) { resOk = false; break; }
            if (it > 0 && updateOk && resOk) { failure = ""; return x; }
            var J = trip.Count > 0 ? Add(st.A, trip) : st.A;
            SparseLU lu;
            try { lu = trip.Count > 0 ? Factor(J) : st.Lu; }
            catch (Exception) { failure = "The Jacobian was singular."; return null; }
            dx = new double[_n];
            lu.Solve(f, dx);
            updateOk = true;
            for (int i = 0; i < _n; i++)
            {
                x[i] -= dx[i];
                if (!(Math.Abs(dx[i]) <= _p.RelTol * Math.Abs(x[i]) + _absCol[i])) updateOk = false;
            }
            if (!Finite(x)) { failure = "Newton diverged."; return null; }
        }
        failure = NotConverged(dx, i => i);
        return null;
    }

    /// <summary>The sentence naming where Newton's last update was largest: the node, and the devices on it.</summary>
    private string NotConverged(double[]? delta, Func<int, int> rowOf)
    {
        if (delta is null || delta.Length == 0) return "Newton did not converge.";
        int worst = 0; double big = -1;
        for (int j = 0; j < delta.Length; j++)
        {
            double tol = _p.VTol + _p.RelTol;
            double e = Math.Abs(delta[j]) / tol;
            if (e > big) { big = e; worst = j; }
        }
        int row = rowOf(worst);
        if (row >= _nNode) return string.Format(CultureInfo.InvariantCulture, "Newton did not converge; its largest update was on branch unknown #{0}.", row - _nNode);
        string net = _nl.Nodes.NameOf(row + 1);
        var on = _devs.Where(d => d.Rp.Contains(row) || d.Rm.Contains(row)).Select(d => d.Ec.InstancePath).ToList();
        return string.Format(CultureInfo.InvariantCulture, "Newton did not converge; its largest update was at net {0}{1}.",
            net, on.Count > 0 ? " (" + string.Join(", ", on) + ")" : "");
    }

    private NonlinearResult Evaluate(Dev d, double[] pv)
    {
        if (d.LastV is { } lv && lv.AsSpan().SequenceEqual(pv)) return d.Last;
        var r = d.Ec.Evaluate(new PortVoltages(pv));
        _evaluations++;
        if (r.Terms.Count > 0)
            throw new TransientRefusedException(TransientElements.NotYetSupported(d.Ec, "it has a frequency-domain weighting term (H[w], w ≥ 2, such as a delay)"));
        if (r.BranchResidual is not null)
            throw new TransientRefusedException(TransientElements.NotYetSupported(d.Ec, "it constrains a voltage (a behavioural voltage source)"));
        d.LastV = (double[])pv.Clone();
        d.Last = r;
        return r;
    }

    private NonlinearResult Eval(Dev d, double[] x)
    {
        var pv = new double[d.P];
        for (int q = 0; q < d.P; q++) pv[q] = (d.Rp[q] >= 0 ? x[d.Rp[q]] : 0) - (d.Rm[q] >= 0 ? x[d.Rm[q]] : 0);
        return Evaluate(d, pv);
    }

    // ── linear algebra ────────────────────────────────────────────────────

    private Stage StageFor(double alpha, double h, bool keep)
    {
        var key = (alpha, _lines?.CouplingKey(h) ?? -1);
        if (keep && _stages.TryGetValue(key, out var s)) return s;
        var a = Combine(_g, _c, alpha);
        if (_lines is not null && _lines.Coupling(h) is { Count: > 0 } k) a = Add(a, k);
        s = new Stage { Lu = Factor(a), A = a };
        if (keep) _stages[key] = s;
        return s;
    }

    private void EnsureInterface(Stage st)
    {
        if (st.Zcol.Length == _nIf) return;      // filled once per stage (nothing to fill with no interface)
        st.Zcol = new double[_nIf][];
        st.Zp = new double[_nIf, _nIf];
        for (int j = 0; j < _nIf; j++)
        {
            var e = new double[_n]; e[_ifRows[j]] = 1;
            var z = new double[_n];
            st.Lu.Solve(e, z);
            st.Zcol[j] = z;
            for (int i = 0; i < _nIf; i++) st.Zp[i, j] = z[_ifRows[i]];
        }
    }

    /// <summary>Whether the linear partition holds <paramref name="row"/>'s node: a conductance in its column above a thousand times
    /// gmin (gmin alone condenses to an interface impedance of a teraohm), or any capacitance.</summary>
    private bool LinearlyTouched(int row)
    {
        static bool Has(CompressedColumnStorage<double> m, int c, double floor)
        {
            for (int k = m.ColumnPointers[c]; k < m.ColumnPointers[c + 1]; k++) if (Math.Abs(m.Values[k]) > floor) return true;
            return false;
        }
        return Has(_g, row, 1e3 * Math.Max(_settings.Gmin, 1e-15)) || Has(_c, row, 0);
    }

    private SparseLU Factor(CompressedColumnStorage<double> a)
    {
        _factorizations++;
        return SparseLU.Create(a, ColumnOrdering.MinimumDegreeAtPlusA, 1.0);
    }

    /// <summary>The real part of a complex matrix: what a stamp affine in jw recovers G and C as.</summary>
    private static CompressedColumnStorage<double> Real(CompressedColumnStorage<Complex> m)
    {
        var tri = new CoordinateStorage<double>(m.RowCount, m.ColumnCount, Math.Max(1, m.NonZerosCount));
        for (int j = 0; j < m.ColumnCount; j++)
            for (int p = m.ColumnPointers[j]; p < m.ColumnPointers[j + 1]; p++)
                if (m.Values[p].Real != 0) tri.At(m.RowIndices[p], j, m.Values[p].Real);
        return SparseMatrix.OfIndexed(tri);
    }

    private static CompressedColumnStorage<double> Combine(CompressedColumnStorage<double> a, CompressedColumnStorage<double> b, double beta)
    {
        var extra = new List<(int, int, double)>();
        for (int j = 0; j < b.ColumnCount; j++)
            for (int p = b.ColumnPointers[j]; p < b.ColumnPointers[j + 1]; p++)
                extra.Add((b.RowIndices[p], j, beta * b.Values[p]));
        return Add(a, extra);
    }

    private static CompressedColumnStorage<double> Add(CompressedColumnStorage<double> a, List<(int r, int c, double v)> extra)
    {
        int n = a.RowCount;
        var tri = new CoordinateStorage<double>(n, a.ColumnCount, a.NonZerosCount + extra.Count + 1);
        for (int j = 0; j < a.ColumnCount; j++)
            for (int p = a.ColumnPointers[j]; p < a.ColumnPointers[j + 1]; p++)
                tri.At(a.RowIndices[p], j, a.Values[p]);
        foreach (var (r, c, v) in extra) tri.At(r, c, v);
        return SparseMatrix.OfIndexed(tri);   // duplicates are summed
    }

    private static void Mul(CompressedColumnStorage<double> m, double[] x, double[] y)
    {
        for (int j = 0; j < m.ColumnCount; j++)
        {
            double xj = x[j];
            if (xj == 0) continue;
            for (int p = m.ColumnPointers[j]; p < m.ColumnPointers[j + 1]; p++) y[m.RowIndices[p]] += m.Values[p] * xj;
        }
    }

    private static void MulAbs(CompressedColumnStorage<double> m, double[] x, double[] y)
    {
        for (int j = 0; j < m.ColumnCount; j++)
        {
            double xj = Math.Abs(x[j]);
            if (xj == 0) continue;
            for (int p = m.ColumnPointers[j]; p < m.ColumnPointers[j + 1]; p++) y[m.RowIndices[p]] += Math.Abs(m.Values[p]) * xj;
        }
    }

    private static double[]? DenseSolve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c; double best = Math.Abs(m[c, c]);
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > best) { best = Math.Abs(m[r, c]); p = r; }
            if (best == 0 || !double.IsFinite(best)) return null;
            if (p != c)
            {
                for (int j = 0; j < n; j++) (m[c, j], m[p, j]) = (m[p, j], m[c, j]);
                (x[c], x[p]) = (x[p], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                if (f == 0) continue;
                for (int j = c; j < n; j++) m[r, j] -= f * m[c, j];
                x[r] -= f * x[c];
            }
        }
        for (int c = n - 1; c >= 0; c--)
        {
            double s = x[c];
            for (int j = c + 1; j < n; j++) s -= m[c, j] * x[j];
            x[c] = s / m[c, c];
        }
        return x;
    }

    private static bool Finite(double[] x)
    {
        foreach (var v in x) if (!double.IsFinite(v)) return false;
        return true;
    }
}
