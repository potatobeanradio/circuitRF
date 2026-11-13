// ================================================================
//  EnvelopeEngineMt.cs  —  multi-fundamental circuit envelope
//
//  Widely spaced carriers (e.g. two 20 MHz carriers 400 MHz apart, IBW
//  420 MHz) each get their OWN fundamental: the state is the T-tone
//  harmonic-balance lattice (mixing products |k1|+|k2| <= MaxMixOrder),
//  and every product carries its own slowly varying envelope.  The step
//  is set by each carrier's own bandwidth, not by the spacing — a single
//  envelope around the centre would need the step to resolve +-200 MHz
//  carriers and +-600 MHz IM3 (measured with a reference tool: 64x the
//  native rate to converge, 8.7x the cost of this formulation, and 5 dB
//  wrong on IM3 at 16x).
//
//  Same discretisation as the single-tone engine (Gear-2, s-domain
//  linear stamps factored once per product, chord Newton).  Products at
//  a NEGATIVE frequency reuse the factorization at |w| with conjugated
//  inputs and outputs (a real network: A(-jw + a) = conj(A(jw + a))),
//  the same w >= 0 contract the harmonic-balance engine keeps.  The
//  device Jacobian is HbNewtonNd.BuildJNd with dg + alpha*dc as the
//  conductance waveform, which adds exactly the alpha*C triple product.
//  A device's w >= 2 weighted terms (a 90-degree hybrid's PIM quadrature,
//  an SDD's H[w]) join each step's injection with H[w] at the product's
//  signed carrier frequency, so the residual and the linear state agree.
//  Lines are stepped as their delay and SnP blocks and other non-affine
//  elements are rational fits for the run, exactly as in EnvelopeEngine;
//  a product at a negative frequency sees a line at |w|, as its state is.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.HarmonicBalance;
using CSparse.Complex.Factorization;
using CSparse.Storage;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

public sealed class EnvelopeEngineMt
{
    private readonly ElaboratedNetlist _nl;
    private readonly AnalysisSettings _settings;

    /// <summary>What one envelope Newton evaluation hands on (see <see cref="EnvelopeNewton"/>).</summary>
    private sealed record StepEval(Complex[,] Q, Complex[,] Inj, double[][] Dg, double[][] Dc,
                                   IReadOnlyList<HbNewtonNd.BucketNd> Buckets);

    public EnvelopeEngineMt(ElaboratedNetlist netlist, AnalysisSettings? settings = null)
    {
        _nl = netlist;
        _settings = settings ?? new AnalysisSettings();
    }

    /// <summary>Carrier clusters: one fundamental per group of carriers closer than 3x the larger bandwidth, with the rate its
    /// envelope needs and its band edge, the furthest any member's band reaches from the centre (EnvelopeEngine.Oversample).</summary>
    public static List<(double centreHz, List<(int src, int car)> members, double needRate, double edgeHz)> Plan(ModSourceModel[] sources)
    {
        var all = new List<(double f, double bw, int s, int c)>();
        for (int s = 0; s < sources.Length; s++)
        {
            var sig = sources[s].Signal;
            for (int c = 0; c < sig.Carriers.Count; c++)
                all.Add((sources[s].FcHz + sig.Carriers[c].OffsetHz, Math.Max(sig.Carriers[c].Spec.BandwidthHz, 1e6), s, c));
        }
        all.Sort((a, b) => a.f.CompareTo(b.f));
        var groups = new List<List<(double f, double bw, int s, int c)>> { new() { all[0] } };
        for (int i = 1; i < all.Count; i++)
        {
            var prev = groups[^1][^1];
            if (all[i].f - prev.f > 3 * Math.Max(all[i].bw, prev.bw)) groups.Add(new());
            groups[^1].Add(all[i]);
        }
        var res = new List<(double, List<(int, int)>, double, double)>();
        foreach (var g in groups)
        {
            double centre = 0.5 * (g.Min(x => x.f) + g.Max(x => x.f));
            double need = g.Max(x => 2 * (Math.Abs(x.f - centre) + 2.5 * x.bw));
            foreach (var x in g) need = Math.Max(need, sources[x.s].Signal.Carriers[x.c].NativeRateHz);
            res.Add((centre, g.Select(x => (x.s, x.c)).ToList(), need, g.Max(x => Math.Abs(x.f - centre) + 0.5 * x.bw)));
        }
        return res;
    }

    /// <summary>Every P1Tone of the netlist with the frequency it is driven at: its own <c>Freq</c> (brief NZ-11).</summary>
    public static List<(string Name, double Hz)> ToneSources(ElaboratedNetlist nl) =>
        [.. nl.Components.Where(c => c.Model is P1ToneModel).Select(c => (c.InstancePath, ToneHz(c))).Where(t => t.Item2 > 0)];

    /// <summary>A P1Tone's drive frequency, Hz: its <c>Freq</c>.</summary>
    internal static double ToneHz(ElaboratedComponent ec) =>
        ec.Parameters.TryGetValue("Freq", out var f) && f.Kind == Core.Expressions.ValueKind.Real ? f.AsReal() : ((P1ToneModel)ec.Model).FreqHz;

    /// <summary>
    /// Brief NZ-11: the run's fundamentals with its CW tone sources in them. The carriers' groups are <see cref="Plan(ModSourceModel[])"/>'s;
    /// a P1Tone on a frequency they already reach (a harmonic of the one envelope, or a product of the two within
    /// <paramref name="maxMixOrder"/>) adds nothing, and one on any other frequency — an LO — is a fundamental of its own,
    /// with no carriers and no signal rate of its own. A tone inside a carrier group's band but not on one of its
    /// frequencies is refused, as is a third fundamental: either would otherwise run with the tone driving nothing.
    /// </summary>
    /// <param name="f0Single">The single envelope's fundamental (the first MOD_Source's Fc), when the carriers make one group.</param>
    public static List<(double centreHz, List<(int src, int car)> members, double needRate, double edgeHz)> Plan(
        ModSourceModel[] sources, IReadOnlyList<(string Name, double Hz)> tones, double f0Single, int maxHarm, int maxMixOrder)
    {
        var plan = Plan(sources);
        if (tones.Count == 0 || plan.Count > 2) return plan;
        var groupCentres = plan.Select(g => g.centreHz).ToArray();
        bool Reached(double f)
        {
            if (groupCentres.Length == 1)
            {
                for (int k = 1; k <= Math.Max(1, maxHarm); k++) if (Math.Abs(f - k * f0Single) < 1.0) return true;
                return false;
            }
            for (int a = -maxMixOrder; a <= maxMixOrder; a++)
                for (int b = -(maxMixOrder - Math.Abs(a)); b <= maxMixOrder - Math.Abs(a); b++)
                    if (Math.Abs(f - (a * groupCentres[0] + b * groupCentres[1])) < 1.0) return true;
            return false;
        }

        var added = new List<(string Name, double Hz)>();
        foreach (var (name, f) in tones)
        {
            if (Reached(f) || added.Any(x => Math.Abs(x.Hz - f) < 1.0)) continue;
            // A fundamental must stand apart from every carrier as the carriers' own groups do: closer, its envelope would
            // overlap theirs and count the same spectrum twice.
            for (int s = 0; s < sources.Length; s++)
                foreach (var c in sources[s].Signal.Carriers)
                {
                    double fc = sources[s].FcHz + c.OffsetHz, bw = Math.Max(c.Spec.BandwidthHz, 1e6);
                    if (Math.Abs(f - fc) <= 3 * bw)
                        throw new InvalidOperationException(EnvelopeToneDiagnostics.InsideCarrier(name, f, fc).Render());
                }
            added.Add((name, f));
        }
        if (plan.Count + added.Count > 2)
            throw new InvalidOperationException(
                EnvelopeToneDiagnostics.TooManyFundamentals(plan.Count, added, plan.Count + added.Count).Render());
        foreach (var (_, f) in added) plan.Add((f, [], 0.0, 0.0));
        plan.Sort((x, y) => x.centreHz.CompareTo(y.centreHz));
        return plan;
    }

    private sealed class Prod
    {
        public double W;            // signed
        public bool Neg;            // works at |W| with conjugation
        public CompressedColumnStorage<Complex> A0 = null!, C = null!;
        public Complex[] BStatic = null!;
        public Complex[][] BUnit = null!;                // [source] unit drive at this product (tone products only)
        public EnvelopeEngine.Stage Be = null!, G2 = null!;
        /// <summary>Backward Euler at h/m, factored the first time a step is split m ways.</summary>
        public readonly Dictionary<int, EnvelopeEngine.Stage> Split = new();
        public Complex[] Xm1 = null!, Xm2 = null!;       // canonical (|W|) state history
    }

    public EnvelopeRunResult Run(EnvelopeParams p, int maxMixOrder, RunControl? control = null)
    {
        var sw = Stopwatch.StartNew();
        var srcEcs = _nl.Components.Where(c => c.Model is ModSourceModel).ToArray();
        var sources = srcEcs.Select(c => (ModSourceModel)c.Model).ToArray();
        var toneSources = ToneSources(_nl);
        var plan = Plan(sources, toneSources, sources[0].FcHz, p.MaxHarm, maxMixOrder);
        if (plan.Count != 2)
            throw new InvalidOperationException(plan.Count > 2
                ? $"envelope: {plan.Count} widely spaced carrier groups need {plan.Count} fundamentals; up to 2 are supported"
                : "envelope: the multi-fundamental engine needs two fundamentals (two carrier groups, or a carrier group and a P1Tone)");
        int T = 2;
        var tones = plan.Select(g => g.centreHz).ToArray();
        double need = plan.Max(g => g.needRate);
        double rate = SignalRate.AtLeast(need, sources.Select(s => s.Signal).ToArray());
        int osr = EnvelopeEngine.Oversample(p.Oversample, rate, plan.Max(g => g.edgeHz));
        double h = p.StepS > 0 ? p.StepS : 1.0 / (rate * osr);
        double recordRate = 1.0 / h;

        var apft = HbApft.Get(T, maxMixOrder, _settings.HbApftOversample);
        var lat = apft.Lattice;
        int M = lat.MixCount;
        var wMix = new double[M];
        for (int m = 0; m < M; m++) wMix[m] = 2 * Math.PI * lat.FrequencyOf(m, tones);
        for (int a = 0; a < M; a++)
            for (int b = a + 1; b < M; b++)
                if (Math.Abs(Math.Abs(wMix[a]) - Math.Abs(wMix[b])) < 2 * Math.PI * 1.0)
                    throw new InvalidOperationException($"envelope: mixing products {lat.Label(a)} and {lat.Label(b)} fall on the same frequency " +
                        $"({Math.Abs(wMix[a]) / 2 / Math.PI / 1e9:0.######} GHz) at order {maxMixOrder}; lower MaxMixOrder or move a carrier");
        int[] toneMix = new int[T];
        for (int t = 0; t < T; t++) { var k = new int[T]; k[t] = 1; toneMix[t] = lat.IndexOf(k); }

        // Noise (brief NZ-7), from every part's own stamp, before a line or an S-parameter block is replaced for the run.
        // A product at a negative frequency is stepped in the frame of |w|, so its noise is read there.
        var thermal = p.Noise ? EnvelopeNoise.Thermal(_nl, _settings, [.. wMix.Select(w => Math.Abs(w) / (2 * Math.PI))], h, p.Seed, tones.Average()) : null;
        var phase = !p.PhaseNoise ? null : EnvelopePhaseNoise.Of(_nl, (int)Math.Round(sources.Min(x => x.Signal.RecordSeconds) * recordRate), recordRate, p.Seed);

        // drive windows: per source, per tone (its carriers in that group, centred at the tone)
        int nWarm = (int)Math.Round(p.WarmupS / h);
        int nSteps = (int)Math.Round(p.StopS / h);
        long i0 = (long)Math.Round(p.StartS / h) - nWarm;
        int total = nWarm + nSteps + 1;
        var drv = new Complex[sources.Length, T][];
        for (int s = 0; s < sources.Length; s++)
            for (int t = 0; t < T; t++)
            {
                var idx = plan[t].members.Where(x => x.src == s).Select(x => x.car).ToList();
                if (idx.Count == 0) { drv[s, t] = new Complex[total]; continue; }
                var x = sources[s].Signal.CompositeWindow(recordRate, tones[t] - sources[s].FcHz, i0, total, idx);
                double vs = sources[s].VsPeak;
                for (int i = 0; i < x.Length; i++) x[i] *= vs;
                phase?.Apply(sources[s], x, i0);
                drv[s, t] = x;
            }

        // Every line is its delay and every SnP its rational fit for the whole run, the multi-tone start included (as in
        // EnvelopeEngine).
        var stampHz = wMix.Select(w => Math.Abs(w) / (2 * Math.PI)).Where(f => f > 0).Distinct().ToArray();
        using var lines = EnvelopeLineDelay.Apply(_nl, [.. wMix.Select(w => Math.Abs(w) / (2 * Math.PI))], h, 0.5 * need);
        using var snpFit = EnvelopeSnpFit.Apply(_nl, stampHz.DefaultIfEmpty(0).Max() + 0.5 * rate, stampHz, 0.5 * rate);
        using var baseband = EnvelopeBaseband.Apply(_nl, Math.PI * need);    // as in EnvelopeEngine

        var extractor = new HbLinearExtractor(_nl, _settings);
        int N = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        double fc = tones.Average();
        // Each P1Tone is driven at its own Freq (brief NZ-11): it is on a product, or a fundamental of its own. Driving
        // every one at the first fundamental put an LO at the carrier's frequency.
        foreach (var ec in _nl.Components)
            if (ec.Model is P1ToneModel p1) p1.SetToneContext(fc: fc, driveFreqHz: ToneHz(ec) is > 0 and var hz ? hz : tones[0]);
            else if (ec.Model is PnToneModel pn) pn.SetToneContext(fc: fc);
        foreach (var s in sources) { s.SetToneContext(fc); s.EnvelopeDrive = Complex.Zero; }
        // Before the products are stamped: stamping an element alone re-numbers a source's branch.
        // Measured out to the signal's own extent (+-need/2), as in EnvelopeEngine.
        EnvelopeTangentCheck.Warn(_nl, EnvelopeTangentCheck.Measure(_nl, wMix.Select(Math.Abs), Math.PI * need), 0.5 * need);
        double tanErrWorst = 0;

        double aBe = 1.0 / h, aG2 = 1.5 / h, b1G2 = -2.0 / h, b2G2 = 0.5 / h;
        var prods = new Prod[M];
        var mna = extractor.NewSmallSignalMna();
        try
        {
            for (int m = 0; m < M; m++)
            {
                double w = Math.Abs(wMix[m]);
                double dlt = Math.Max(1e-6 * Math.Max(w, 2 * Math.PI * 1e6), 2 * Math.PI * 1.0);
                CompressedColumnStorage<Complex> A0, C;
                baseband.Holding = m == 0;            // the whole baseband product, its right-hand side included
                lines.Product = m;
                if (m == 0)
                {
                    var a1 = EnvelopeEngine.Stamp(extractor, mna, dlt);
                    var a2 = EnvelopeEngine.Stamp(extractor, mna, 2 * dlt);
                    C = EnvelopeEngine.Combine(a2, a1, 1.0 / new Complex(0, dlt), -1.0 / new Complex(0, dlt));
                    A0 = EnvelopeEngine.Combine(a1, C, 1.0, -new Complex(0, dlt));
                }
                else
                {
                    var ap = EnvelopeEngine.Stamp(extractor, mna, w + dlt);
                    var am = EnvelopeEngine.Stamp(extractor, mna, w - dlt);
                    C = EnvelopeEngine.Combine(ap, am, 1.0 / new Complex(0, 2 * dlt), -1.0 / new Complex(0, 2 * dlt));
                    A0 = EnvelopeEngine.Stamp(extractor, mna, w);
                    // the band-edge tangent check the single-fundamental engine makes (affine models: ~round-off)
                    if (w - Math.PI * rate > 0)
                    {
                        var aEdge = EnvelopeEngine.Stamp(extractor, mna, w + Math.PI * rate);
                        var tan = EnvelopeEngine.Combine(A0, C, 1.0, new Complex(0, Math.PI * rate));
                        tanErrWorst = Math.Max(tanErrWorst, EnvelopeEngine.RelDiff(aEdge, tan));
                    }
                }
                var bStatic = EnvelopeEngine.RhsAt(extractor, mna, w, sources, null);
                int tone = Array.IndexOf(toneMix, m);
                var bUnit = new Complex[sources.Length][];
                for (int s = 0; s < sources.Length; s++)
                {
                    if (tone < 0) { bUnit[s] = new Complex[bStatic.Length]; continue; }
                    var b = EnvelopeEngine.RhsAt(extractor, mna, w, sources, s);
                    for (int r = 0; r < b.Length; r++) b[r] -= bStatic[r];
                    bUnit[s] = b;
                }
                bUnit = EnvelopeEngine.ToneUnits(bUnit, bStatic, phase, () => EnvelopeEngine.RhsAt(extractor, mna, w, sources, null));
                var pr = new Prod { W = wMix[m], Neg = wMix[m] < 0, A0 = A0, C = C, BStatic = bStatic, BUnit = bUnit };
                baseband.Holding = false;
                lines.Product = null;
                lines.Bind();
                var delayNow = lines.Coupling(m, 1, A0.RowCount);
                pr.Be = EnvelopeEngine.Stage.Of(EnvelopeEngine.Prepare(A0, C, w, aBe, ifNodes, bStatic, bUnit, delayNow));
                pr.G2 = EnvelopeEngine.Stage.Of(EnvelopeEngine.Prepare(A0, C, w, aG2, ifNodes, bStatic, bUnit, delayNow));
                prods[m] = pr;
            }
        }
        finally { baseband.Holding = false; lines.Product = null; foreach (var s in sources) s.EnvelopeDrive = null; }

        // ── start: multi-tone HB with the t0 drives ─────────────────────────
        var (V, X0, q0) = Start(extractor, baseband, sources, apft, tones, wMix, toneMix, N, ifNodes, p, (s, t) => drv[s, t][0],
                                (s, t) => EnvelopeEngine.Rms(drv[s, t]), out double tolRun);
        for (int m = 0; m < M; m++) { prods[m].Xm1 = X0[m]; prods[m].Xm2 = X0[m]; }
        lines.Start(X0);
        var qm1 = q0; var qm2 = q0;

        var probes = EnvelopeEngine.RecordedNodes(_nl, p.Probes, srcEcs);
        int P = probes.Length;
        var probeIf = EnvelopeEngine.DeviceOnlyInterfaceIndexOf(_nl, probes, ifNodes);   // a device-only node is read from V (issue 66)
        var (iprobes, iprobeRows, iprobeNodes) = EnvelopeEngine.IProbesOf(_nl);
        var (wbonds, wbondRows) = EnvelopeWireTemperature.Rows(_nl);
        var wOut = new Complex[wbonds.Length > 0 ? nSteps + 1 : 0][][];
        var iOut = new Complex[nSteps + 1][][];
        var ivOut = new Complex[nSteps + 1][][];
        var tOut = new double[nSteps + 1];
        var pOut = new Complex[nSteps + 1][][];
        // one row per (source, tone group), s·T + t: the groups are different lattice products, so their POWERS add, never
        // their phasors (issue 66: two equal CW carriers summed as phasors read the input 3.01 dB high)
        var dOut = new Complex[sources.Length * T][];
        for (int r = 0; r < dOut.Length; r++) dOut[r] = new Complex[nSteps + 1];
        void Record(int step)
        {
            int o = step - nWarm;
            if (o < 0) return;
            tOut[o] = o * h;
            if (wbonds.Length > 0)
                wOut[o] = EnvelopeWireTemperature.Sample(wbondRows, M, (m, row) => prods[m].Neg ? Complex.Conjugate(prods[m].Xm1[row]) : prods[m].Xm1[row]);
            var pr = new Complex[P][];
            for (int pi = 0; pi < P; pi++)
            {
                pr[pi] = new Complex[M];
                int row = probes[pi].node - 1, j = probeIf[pi];
                for (int m = 0; m < M; m++)
                {
                    var v = j >= 0 ? V[j, m] : row >= 0 ? prods[m].Xm1[row] : Complex.Zero;
                    pr[pi][m] = prods[m].Neg ? Complex.Conjugate(v) : v;
                }
                pr[pi][0] = new Complex(pr[pi][0].Real, 0);
            }
            pOut[o] = pr;
            var ic = new Complex[iprobes.Length][];
            var iv = new Complex[iprobes.Length][];
            for (int i = 0; i < iprobes.Length; i++)
            {
                ic[i] = new Complex[M];
                iv[i] = new Complex[M];
                for (int m = 0; m < M; m++)
                {
                    var c = prods[m].Xm1[iprobeRows[i]];
                    var v = iprobeNodes[i] >= 0 ? prods[m].Xm1[iprobeNodes[i]] : Complex.Zero;
                    ic[i][m] = prods[m].Neg ? Complex.Conjugate(c) : c;
                    iv[i][m] = prods[m].Neg ? Complex.Conjugate(v) : v;
                }
                ic[i][0] = new Complex(ic[i][0].Real, 0);
                iv[i][0] = new Complex(iv[i][0].Real, 0);
            }
            iOut[o] = ic;
            ivOut[o] = iv;
            for (int s = 0; s < sources.Length; s++) for (int t = 0; t < T; t++) dOut[s * T + t][o] = drv[s, t][step];
        }
        Record(0);

        int dof = 2 * N * M;
        var judge = HbNewton.JudgeFor(_nl, ifNodes);
        var newton = new EnvelopeNewton(dof, judge);
        var vHist = new List<Complex[,]> { (Complex[,])V.Clone() };
        int splitSteps = 0, finestSplit = 1;

        // One step from the history to the new state of every product (see EnvelopeEngine.Run's own Advance); drv(s, t)
        // is source s's drive at tone t. Null when Newton did not converge.
        Complex[][]? Advance(EnvelopeNewton nw, double alpha, double b1, double b2, Func<int, EnvelopeEngine.Stage> stageOf,
                             Func<int, int, Complex> drvAt, Complex[][] x1, Complex[][] x2, Complex[,] q1, Complex[,] q2,
                             Complex[,] V, Action<int, Complex[]>? delayed, long sample, out Complex[,] qNow)
        {
            delayed = EnvelopeEngine.WithNoise(delayed, thermal, sample);
            var yEff = new Complex[M][,];
            var iSrc = new Complex[M][];
            var xoc = new Complex[M][];
            for (int m = 0; m < M; m++)
            {
                var pr = prods[m];
                var sg = stageOf(m);
                int n = sg.Xs.Length;
                var hv = new Complex[n];
                for (int r = 0; r < n; r++) hv[r] = b1 * x1[m][r] + b2 * x2[m][r];
                var bh = new Complex[n];
                EnvelopeEngine.MulCsc(pr.C, hv, bh, -1.0);
                delayed?.Invoke(m, bh);
                var xh = new Complex[n];
                sg.Lu.Solve(bh, xh);
                var x = xoc[m] = new Complex[n];
                for (int r = 0; r < n; r++) x[r] = sg.Xs[r] + xh[r];
                int tone = Array.IndexOf(toneMix, m);
                if (tone >= 0)
                    for (int s = 0; s < sources.Length; s++)
                    {
                        Complex a = drvAt(s, tone);
                        if (a == Complex.Zero) continue;
                        var u = sg.Xu[s];
                        for (int r = 0; r < n; r++) x[r] += a * u[r];
                    }
                EnvelopeEngine.AddTonePhase(phase, sg.Xu, sources.Length, Math.Abs(pr.W) / (2 * Math.PI), sample, x);
                var Y = pr.Neg ? Conj(sg.Y) : sg.Y;
                var isrc = new Complex[N];
                for (int a = 0; a < N; a++)
                {
                    Complex acc = 0;
                    for (int b = 0; b < N; b++)
                    {
                        var vb = x[ifNodes[b] - 1];
                        acc += Y[a, b] * (pr.Neg ? Complex.Conjugate(vb) : vb);
                    }
                    isrc[a] = m == 0 ? new Complex(-acc.Real, 0) : -acc;
                }
                iSrc[m] = isrc; yEff[m] = Y;
            }
            var qh = new Complex[N, M];
            for (int a = 0; a < N; a++) for (int m = 0; m < M; m++) qh[a, m] = b1 * q1[a, m] + b2 * q2[a, m];

            var accepted = nw.Solve(V, tolRun, v =>
            {
                var (iNl, qNl, dg, dc, buckets) = HbNewtonNd.EvaluateNonlinearNd(v, apft, N, _nl, ifNodes);
                var injNow = new Complex[N, M];
                for (int a = 0; a < N; a++) for (int m = 0; m < M; m++) injNow[a, m] = iNl[a, m] + alpha * qNl[a, m] + qh[a, m];
                // A w>=2 bucket is memoryless in the envelope (H[w] at each product's carrier), so it joins the
                // injection here: the residual and the linear state's update below then both carry it.
                HbNewtonNd.AddBucketInjection(injNow, buckets, wMix);
                return (HbNewtonNd.BuildFNd(v, yEff, iSrc, injNow, qNl, lat, N, tones), new StepEval(qNl, injNow, dg, dc, buckets));
            }, e =>
            {
                var dgA = new double[e.Dg.Length][];
                for (int i = 0; i < e.Dg.Length; i++)
                {
                    dgA[i] = new double[e.Dg[i].Length];
                    for (int j = 0; j < e.Dg[i].Length; j++) dgA[i][j] = e.Dg[i][j] + alpha * e.Dc[i][j];
                }
                return HbNewtonNd.BuildJNd(yEff, dgA, e.Dc, apft, N, tones, buckets: e.Buckets);
            }, p.MinNewton);
            if (accepted is null) { qNow = null!; return null; }
            qNow = accepted.Q;

            for (int m = 0; m < M; m++)
            {
                var pr = prods[m];
                var zc = stageOf(m).Zcol;
                var x = xoc[m];
                for (int j = 0; j < N; j++)
                {
                    Complex tot = accepted.Inj[j, m] + (m > 0 ? new Complex(0, wMix[m]) * qNow[j, m] : Complex.Zero);
                    if (m == 0) tot = new Complex(tot.Real, 0);
                    if (pr.Neg) tot = Complex.Conjugate(tot);
                    var z = zc[j];
                    for (int r = 0; r < x.Length; r++) x[r] -= z[r] * tot;
                }
            }
            return xoc;
        }

        // A failed step retaken as split backward-Euler sub-steps, exactly as EnvelopeEngine.Run's own Split.
        Complex[][] Split(int step, Complex[,] V, out Complex[,] qNow)
        {
            for (int sub = 2; sub <= EnvelopeEngine.MaxStepSplit; sub *= 2)
            {
                double aSub = sub / h;
                var nw = new EnvelopeNewton(dof, judge);
                var x = prods.Select(pr => pr.Xm1).ToArray();
                var q = qm1;
                var vs = (Complex[,])vHist[^1].Clone();
                bool ok = true;
                lines.BeginSplit();
                for (int i = 1; i <= sub && ok; i++)
                {
                    double f = (double)i / sub;
                    var xn = Advance(nw, aSub, -aSub, 0.0, m => SplitStage(m, sub, aSub),
                                     (s, t) => drv[s, t][step - 1] + f * (drv[s, t][step] - drv[s, t][step - 1]),
                                     x, x, q, q, vs, lines.Any ? (m, b) => lines.AddSplit(m, b, sub, i) : null, i0 + step, out var qn);
                    if (xn is null) ok = false;
                    else { x = xn; q = qn; lines.PushSub(xn); }
                }
                if (!ok) continue;
                Array.Copy(vs, V, V.Length);
                newton.Invalidate();                        // built at the failed full step's last iterate
                splitSteps++; finestSplit = Math.Max(finestSplit, sub);
                qNow = q;
                return x;
            }
            throw new InvalidOperationException(
                $"envelope step {step} (t = {p.StartS + (step - nWarm) * h:G4} s) did not converge, even split into {EnvelopeEngine.MaxStepSplit} sub-steps");
        }

        EnvelopeEngine.Stage SplitStage(int m, int sub, double aSub)
        {
            var pr = prods[m];
            if (!pr.Split.TryGetValue(sub, out var sg))
                pr.Split[sub] = sg = EnvelopeEngine.Stage.Of(
                    EnvelopeEngine.Prepare(pr.A0, pr.C, Math.Abs(pr.W), aSub, ifNodes, pr.BStatic, pr.BUnit, lines.Coupling(m, sub, pr.A0.RowCount)));
            return sg;
        }

        for (int step = 1; step < total; step++)
        {
            if ((step & 1023) == 0) control?.ThrowIfCancellationRequested();
            bool be = step == 1;
            double alpha = be ? aBe : aG2, b1 = be ? -aBe : b1G2, b2 = be ? 0.0 : b2G2;
            if (step <= 2) newton.Invalidate();
            if (vHist.Count >= 3)
            {
                var v1 = vHist[^1]; var v2 = vHist[^2]; var v3 = vHist[^3];
                for (int a = 0; a < N; a++) for (int m = 0; m < M; m++) V[a, m] = 3 * v1[a, m] - 3 * v2[a, m] + v3[a, m];
            }

            var xNew = Advance(newton, alpha, b1, b2, m => be ? prods[m].Be : prods[m].G2, (s, t) => drv[s, t][step],
                               prods.Select(pr => pr.Xm1).ToArray(), prods.Select(pr => pr.Xm2).ToArray(), qm1, qm2, V,
                               lines.Any ? lines.AddMain : null, i0 + step, out var qNow)
                       ?? Split(step, V, out qNow);
            for (int m = 0; m < M; m++) { prods[m].Xm2 = prods[m].Xm1; prods[m].Xm1 = xNew[m]; }
            lines.Push(xNew);
            qm2 = qm1; qm1 = qNow;
            vHist.Add((Complex[,])V.Clone()); if (vHist.Count > 3) vHist.RemoveAt(0);
            Record(step);
        }

        var carMix = new List<int>(); var carOff = new List<double>(); var carLab = new List<string>();
        for (int s = 0; s < sources.Length; s++)
            for (int c = 0; c < sources[s].Signal.Carriers.Count; c++)
            {
                int t = plan.FindIndex(g => g.members.Contains((s, c)));
                var cr = sources[s].Signal.Carriers[c];
                carMix.Add(toneMix[t]);
                carOff.Add(sources[s].FcHz + cr.OffsetHz - tones[t]);
                carLab.Add($"{sources[s].InstanceName}:{cr.Spec.Label} @ {(sources[s].FcHz + cr.OffsetHz) / 1e9:0.####} GHz");
            }
        var res = new EnvelopeRunResult
        {
            Time = tOut, ProbeNames = probes.Select(x => x.name).ToArray(), Probe = pOut,
            SupplyNames = Array.Empty<string>(), SupplyCurrent = Enumerable.Range(0, nSteps + 1).Select(_ => Array.Empty<double>()).ToArray(),
            SupplyVoltage = Array.Empty<double>(), IProbeNames = iprobes, IProbeCurrent = iOut, IProbeVoltage = ivOut,
            WBondPaths = wbonds, WBondCurrent = wOut,
            Drive = dOut, DriveRe = sources.SelectMany(s => Enumerable.Repeat(s.Z.Real, T)).ToArray(),
            F0Hz = tones[0], StepS = h, MaxHarm = M - 1, SignalRateHz = rate, Sources = sources,
            WallSeconds = sw.Elapsed.TotalSeconds, MeanNewton = (double)newton.Iterations / Math.Max(1, total - 1), JacobianBuilds = newton.JacobianBuilds,
            WorstTangentError = tanErrWorst, SnpFits = snpFit.Reports,
            CarrierMix = carMix.ToArray(), CarrierOffsetHz = carOff.ToArray(), CarrierLabels = carLab.ToArray(),
            ToneFreqsHz = tones, MixLabels = Enumerable.Range(0, M).Select(m => lat.Label(m)).ToArray(),
            MixFreqHz = wMix.Select(w => w / (2 * Math.PI)).ToArray(),
            MixTones = Enumerable.Range(0, M).Select(m => lat.ToneOf(m)).ToArray(),
        };
        if (splitSteps > 0)
            res.Notes.Add($"envelope: {splitSteps} step(s) did not converge at the full step and were retaken as up to {finestSplit} sub-steps");
        return res;
    }

    private static Complex[,] Conj(Complex[,] a)
    {
        int n = a.GetLength(0), m = a.GetLength(1);
        var r = new Complex[n, m];
        for (int i = 0; i < n; i++) for (int j = 0; j < m; j++) r[i, j] = Complex.Conjugate(a[i, j]);
        return r;
    }

    /// <remarks>The DC solve and the baseband extraction see the held elements, as in <see cref="EnvelopeEngine"/>.</remarks>
    /// <param name="driveRms">Each source's RMS drive per tone over the run, which the tolerance is set against.</param>
    /// <param name="tol">The run's tolerance (<see cref="EnvelopeEngine.RunTolerance"/>).</param>
    private (Complex[,] V, Complex[][] X, Complex[,] q) Start(HbLinearExtractor ex, EnvelopeBaseband.Hold baseband, ModSourceModel[] srcs, HbApft apft, double[] tones,
        double[] wMix, int[] toneMix, int N, int[] ifNodes, EnvelopeParams p, Func<int, int, Complex> drive0,
        Func<int, int, double> driveRms, out double tol)
    {
        int M = wMix.Length;
        var yNN = new Complex[M][,]; var iSrc = new Complex[M][]; var bSrc = new Complex[M][];
        void ExtractAll(double scale, Func<int, int, Complex>? at = null)
        {
            for (int m = 0; m < M; m++)
            {
                int t = Array.IndexOf(toneMix, m);
                for (int s = 0; s < srcs.Length; s++) srcs[s].EnvelopeDrive = t >= 0 ? (at ?? drive0)(s, t) * scale : Complex.Zero;
                double w = Math.Abs(wMix[m]);
                Complex[,] y; Complex[] isr;
                baseband.Holding = m == 0;
                if (m == 0) (y, isr) = ex.ExtractDC(); else (y, isr) = ex.Extract(w);
                bSrc[m] = ex.BuildSourceRhs(m == 0 ? 0.0 : w);
                baseband.Holding = false;
                if (wMix[m] < 0) { y = Conj(y); isr = isr.Select(Complex.Conjugate).ToArray(); }
                yNN[m] = y; iSrc[m] = isr;
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
            var V = new Complex[N, M];
            for (int a = 0; a < N; a++)
                V[a, 0] = dc.Converged && ifNodes[a] - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[ifNodes[a] - 1] : 0.0;
            // the run's tolerance, from the RF excitation at the record's RMS drive (as EnvelopeEngine)
            ExtractAll(1.0, (s, t) => driveRms(s, t));
            tol = EnvelopeEngine.RunTolerance(p.Tol, iSrc, m => m > 0, _nl);
            ExtractAll(1.0);
            var V0 = (Complex[,])V.Clone();
            var sol = HbNewtonNd.Solve(V, yNN, iSrc, apft, tones, N, _nl, ifNodes, _settings, tol);
            if (!sol.Converged)
            {
                Array.Copy(V0, V, V0.Length);
                for (int r = 1; r <= 10; r++)                                   // drive ramp
                {
                    ExtractAll(r / 10.0);
                    sol = HbNewtonNd.Solve(V, yNN, iSrc, apft, tones, N, _nl, ifNodes, _settings, tol);
                }
            }
            if (sol is null || !sol.Converged) throw new InvalidOperationException("envelope: the multi-tone harmonic-balance start point did not converge");
            var X = new Complex[M][];
            for (int m = 0; m < M; m++)
            {
                var inl = new Complex[N];
                for (int a = 0; a < N; a++) inl[a] = wMix[m] < 0 ? Complex.Conjugate(sol.INl[a, m]) : sol.INl[a, m];
                baseband.Holding = m == 0;
                X[m] = ex.SolveFullNetwork(m == 0 ? 0.0 : Math.Abs(wMix[m]), inl, bSrc[m]);  // canonical |w| state
                baseband.Holding = false;
            }
            var (_, qNl, _, _, _) = HbNewtonNd.EvaluateNonlinearNd(V, apft, N, _nl, ifNodes);
            return (V, X, qNl);
        }
        finally { baseband.Holding = false; foreach (var s in srcs) s.EnvelopeDrive = null; }
    }
}
