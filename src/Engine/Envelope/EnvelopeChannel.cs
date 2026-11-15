// ================================================================
//  EnvelopeChannel.cs  —  a Propagation Channel in the circuit envelope (brief PCH-6)
//
//  Explicit and one-way (design note §2.2, §5.4, D4). For the run the
//  channel stamps its two ends alone (S21 left out); the receive pin's
//  wave row takes, on its right-hand side, every ray's delayed, rotated,
//  scaled transmit wave,
//
//    b_rx(t) = Σ_r A_r · e^(j2π·ν_r·t + jπ·ν̇·t²) · w_tx(t − h − τ_r),
//    w_tx    = (v_tx + Z0·i_tx)/√Re(Z0)          (twice the incident wave)
//
//  per product, A_r the ray's term at the product's frequency (large
//  scale, antennas, gain, and e^(−jω_k·τ_r)), τ_r its excess delay, ν_r
//  and ν̇ its Doppler and drift at that frequency, t the run time from
//  TimeOffset. The simulated latency is one step h plus the excess
//  delay, so the delayed wave is always on steps already solved: the
//  channel adds a source to the receive side and nothing to any matrix,
//  and nothing on the transmit side reads the receive side. The bulk
//  delay is never simulated, and the step's latency is a pure envelope
//  delay (no carrier phase), so a constant envelope reads H(f_k, t)
//  exactly — what the harmonic-balance start stamps. Across a product's
//  band the free-space amplitude λ/4πd goes as 1/f: it is carried to
//  first order, (1 − Δf/f_k), the Δf as a time derivative of the delayed
//  wave (as EnvelopeLineDelay carries a line's loss slope); the second
//  order, (Δf/f_k)², is 1e-7 for ±0.7 MHz at 2 GHz.
//
//  The delayed wave is quadratic Lagrange on the step grid, as
//  EnvelopeLineDelay's (passive, |L| ≤ 1 to Nyquist), on solved steps
//  only, sub-steps included. Before t = 0 the history is the start's
//  steady state; the receive side's first h + τ_max is that history's
//  and is left out of the measurement (EnvelopeRunner.SettleSkip).
//
//  A recorded transmit wave can drive the receive side alone (the
//  partition drops reuse, PCH-10): the receive pin then reads the
//  recording instead of its own transmit pin.
//
//  Fading (brief PCH-7): each ray's gain is multiplied by its tap's
//  fading at the receive time — the sum of sinusoids evaluated at t,
//  with no state — at the reference frequency's f_D in every product
//  (the spread of f_D across a product's band is f_D·Δf/f, 1e-4 of it
//  for a 200 MHz band at 2 GHz).
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

/// <summary>A channel's transmit wave over a run, per product: <c>Waves[k][i]</c> is product k at absolute record sample
/// <c>FirstSample + i</c> (the run's step grid).</summary>
public sealed record ChannelRecording(long FirstSample, Complex[][] Waves);

public static class EnvelopeChannel
{
    internal sealed class Ray
    {
        public Complex A;
        public double Steps;            // h + τ, in steps
        public double Nu, Drift;
        public PropagationChannelModel.RayTerm Term;   // its fading (brief PCH-7), read at the receive time
    }

    internal sealed class Ch
    {
        public required ElaboratedComponent Ec;
        public required PropagationChannelModel Model;
        public required int Tx;                    // the transmit net (0 = ground)
        public required Complex[] Z0;              // the transmit end's reference, per product
        public required Ray[][] Rays;              // per product
        public required double[] Hz;               // each product's frequency
        public required int Ring;
        public Complex[][] W = null!;              // [product][ring]
        public ChannelRecording? Drive;
        public List<Complex>[]? Record;
        public int BTx = -1, BRx = -1;
    }

    /// <summary>brief-pch-8 R-pch8-3 — a channel with an instance end: each transmit element's wave, each ray's two coupling
    /// vectors per product, and the receive end's rows.</summary>
    internal sealed class ICh
    {
        public required ElaboratedComponent Ec;
        public required PropagationChannelModel Model;
        public required int[] TxNode;                       // per transmit source (a pin is one)
        public required int[] TxRef;
        public required Complex Z0Tx;
        public int[] TxBranch = [];
        public required int[] RxSource;                     // an array receive end's source nodes; empty for a pin
        public int BRx = -1;
        public required PropagationChannelModel.RayCoupling[][] Rays;   // per product
        public required double[][] Steps;                   // per product, per ray: 1 + τ/h
        public required double[] Hz;
        public required int Ring;
        public Complex[][][] W = null!;                     // [product][source][ring]
        public long MultiplyAdds;                           // per step, all products
    }

    /// <summary>brief-pch-8 — the multiply-adds per step, rays and elements of the last run's instance-end channels (R-pch8-3's counters).</summary>
    public static (long MultiplyAddsPerStep, int Rays, int TxElements, int RxElements) LastInstanceCost { get; private set; }

    /// <summary>brief-pch-8b R-pch8b-2 — receive elements the last instance-end channel did not project onto (the collapse took its feed).</summary>
    public static int CollapsedElements { get; private set; }

    /// <summary>The channels of one run: puts them back on <see cref="Dispose"/>, and carries their history.</summary>
    public sealed class Run : IDisposable
    {
        private readonly List<Ch> _chs = [];
        private readonly List<ICh> _ichs = [];
        private readonly double _h;
        private readonly long _i0;
        private int _n;

        internal Run(double h, long i0) { _h = h; _i0 = i0; }

        public bool Any => _chs.Count > 0 || _ichs.Count > 0;

        /// <summary>The longest simulated delay, h + τ_max (s): the receive side's start that has no history of its own.</summary>
        public double LatencyS { get; private set; }

        /// <summary>The product being stamped: each channel stamps its two ends alone. Null: the steady state, H frozen.</summary>
        public int? Product { set { foreach (var c in _chs) c.Model.Stepping = value is not null; foreach (var c in _ichs) c.Model.Stepping = value is not null; } }

        /// <summary>Takes each channel's branch rows from the stamp just made.</summary>
        public void Bind()
        {
            foreach (var c in _chs) { c.BTx = c.Model.PortBranches[0]; c.BRx = c.Model.PortBranches[1]; }
            foreach (var c in _ichs)
            {
                c.TxBranch = c.Model.TxArray is { } ta ? [.. ta.Array.PortBranchIndices] : [c.Model.PortBranches[0]];
                c.BRx = c.Model.PortBranches[1];
            }
        }

        private static Complex IWave(ICh c, int s, Complex[] x)
        {
            Complex v = c.TxNode[s] > 0 ? x[c.TxNode[s] - 1] : Complex.Zero;
            if (c.TxRef[s] > 0) v -= x[c.TxRef[s] - 1];
            return (v + c.Z0Tx * x[c.TxBranch[s]]) / Math.Sqrt(c.Z0Tx.Real);
        }

        private static Complex Wave(Ch c, int k, Complex[] x)
        {
            Complex v = c.Tx > 0 ? x[c.Tx - 1] : Complex.Zero;
            return (v + c.Z0[k] * x[c.BTx]) / Math.Sqrt(c.Z0[k].Real);
        }

        /// <summary>The history before t = 0: the start's steady state.</summary>
        public void Start(Complex[][] x)
        {
            _n = 0;
            foreach (var c in _ichs)
            {
                c.W = new Complex[c.Rays.Length][][];
                for (int k = 0; k < c.Rays.Length; k++)
                {
                    c.W[k] = new Complex[c.TxNode.Length][];
                    for (int s = 0; s < c.TxNode.Length; s++) { c.W[k][s] = new Complex[c.Ring]; Array.Fill(c.W[k][s], IWave(c, s, x[k])); }
                }
            }
            foreach (var c in _chs)
            {
                c.W = new Complex[c.Rays.Length][];
                for (int k = 0; k < c.Rays.Length; k++)
                {
                    c.W[k] = new Complex[c.Ring];
                    Array.Fill(c.W[k], Wave(c, k, x[k]));
                    c.Record?[k].Add(c.W[k][0]);
                }
            }
        }

        /// <summary>An accepted full step.</summary>
        public void Push(Complex[][] x)
        {
            _n++;
            foreach (var c in _ichs)
                for (int k = 0; k < c.Rays.Length; k++)
                    for (int s = 0; s < c.TxNode.Length; s++) c.W[k][s][_n % c.Ring] = IWave(c, s, x[k]);
            foreach (var c in _chs)
                for (int k = 0; k < c.Rays.Length; k++)
                {
                    var w = Wave(c, k, x[k]);
                    c.W[k][_n % c.Ring] = w;
                    c.Record?[k].Add(w);
                }
        }

        /// <summary>The transmit wave of product <paramref name="k"/> at step <paramref name="s"/> (≤ the last accepted).</summary>
        private Complex At(Ch c, int k, int s)
        {
            if (c.Drive is { } d)
            {
                long i = _i0 + Math.Max(s, 0) - d.FirstSample;
                if (i < 0 || i >= d.Waves[k].Length)
                    throw new InvalidOperationException(PropagationChannelDiagnostics.RecordingTooShort(c.Ec.InstancePath, _i0 + s).Render());
                return d.Waves[k][i];
            }
            return s <= 0 ? c.W[k][0] : c.W[k][s % c.Ring];
        }

        /// <summary>Each ray's contribution to the receive row, at step position <paramref name="pos"/> (in steps from the run's
        /// start, fractional for a sub-step), reading only steps up to <paramref name="last"/>.</summary>
        private Complex Drive(Ch c, int k, double pos, int last)
        {
            double tt = c.Model.FrozenTimeS + pos * _h;
            Complex sum = 0;
            var l = new double[3]; var ld = new double[3];
            // the free-space amplitude goes as 1/f across the product's band: (1 − Δf/f_k) to first order, Δf as d/dt / j2π
            Complex g1 = new Complex(0, 1 / (2 * Math.PI * c.Hz[k]));
            foreach (var r in c.Rays[k])
            {
                double q = last - (pos - r.Steps);              // steps back from the last solved step
                int n0 = (int)Math.Floor(q);
                EnvelopeLineDelay.Lagrange(q - n0, l, ld);
                Complex w = 0;
                for (int j = 0; j < 3; j++)
                    w += (l[j] - g1 * ld[j] / _h) * At(c, k, last - n0 - j);   // the nodes run back in time: d/dt is −ld/h
                sum += r.A * r.Term.FadeAt(tt) * Complex.FromPolarCoordinates(1, 2 * Math.PI * r.Nu * tt + Math.PI * r.Drift * tt * tt) * w;
            }
            return sum;
        }

        /// <summary>brief-pch-8 — an instance-end channel's sources for product <paramref name="k"/> at step position <paramref name="pos"/>:
        /// per ray, the departure field from each transmit element's wave at t − h − τ (quadratic Lagrange on solved steps), turned by the
        /// ray's fading and Doppler, then projected on each receive element (½·(j)·F_rxᵀ·y on its source row) or on the pin's wave row.</summary>
        private void DriveInstance(ICh c, int k, double pos, int last, Complex[] b)
        {
            double tt = c.Model.FrozenTimeS + pos * _h;
            var l = new double[3]; var ld = new double[3];
            Complex g1 = new Complex(0, 1 / (2 * Math.PI * c.Hz[k]));
            var rays = c.Rays[k];
            for (int ri = 0; ri < rays.Length; ri++)
            {
                var rc = rays[ri];
                double q = last - (pos - c.Steps[k][ri]);
                int n0 = (int)Math.Floor(q);
                EnvelopeLineDelay.Lagrange(q - n0, l, ld);
                Complex c0 = l[0] - g1 * ld[0] / _h, c1 = l[1] - g1 * ld[1] / _h, c2 = l[2] - g1 * ld[2] / _h;
                int s0 = last - n0, s1 = s0 - 1, s2 = s0 - 2;
                int i0 = s0 <= 0 ? 0 : s0 % c.Ring, i1 = s1 <= 0 ? 0 : s1 % c.Ring, i2 = s2 <= 0 ? 0 : s2 % c.Ring;
                Complex yt = 0, yp = 0;
                var waves = c.W[k];
                for (int m = 0; m < waves.Length; m++)
                {
                    Complex tT = rc.TxTheta[m], tP = rc.TxPhi[m];
                    if (tT == Complex.Zero && tP == Complex.Zero) continue;
                    var wm = waves[m];
                    Complex w = c0 * wm[i0] + c1 * wm[i1] + c2 * wm[i2];
                    yt += tT * w; yp += tP * w;
                }
                Complex turn = rc.Term.FadeAt(tt) * Complex.FromPolarCoordinates(1, 2 * Math.PI * rc.DopplerHz * tt + Math.PI * rc.DriftHzps * tt * tt);
                yt *= turn; yp *= turn;
                if (c.RxSource.Length == 0) b[c.BRx] += rc.RxTheta[0] * yt + rc.RxPhi[0] * yp;
                else
                    for (int n = 0; n < c.RxSource.Length; n++)
                        if (c.RxSource[n] > 0) b[c.RxSource[n] - 1] += 0.5 * (rc.RxTheta[n] * yt + rc.RxPhi[n] * yp);
            }
        }

        /// <summary>The receive rows' sources for product <paramref name="k"/> of the full step being taken.</summary>
        public void AddMain(int k, Complex[] b)
        {
            foreach (var c in _chs)
                if (c.Rays[k].Length > 0) b[c.BRx] += Drive(c, k, _n + 1, _n);
            foreach (var c in _ichs) DriveInstance(c, k, _n + 1, _n, b);
        }

        /// <summary>The same for sub-step <paramref name="i"/> of <paramref name="split"/> (still on the solved full steps:
        /// the latency is at least one step).</summary>
        public void AddSplit(int k, Complex[] b, int split, int i)
        {
            foreach (var c in _chs)
                if (c.Rays[k].Length > 0) b[c.BRx] += Drive(c, k, _n + (double)i / split, _n);
            foreach (var c in _ichs) DriveInstance(c, k, _n + (double)i / split, _n, b);
        }

        /// <summary>Each channel's recorded transmit wave (instance path to recording), when the run asked for them.</summary>
        public IReadOnlyDictionary<string, ChannelRecording> Recordings
            => _chs.Where(c => c.Record is not null)
                   .ToDictionary(c => c.Ec.InstancePath, c => new ChannelRecording(_i0, [.. c.Record!.Select(l => l.ToArray())]), StringComparer.Ordinal);

        /// <summary>The true channel for the genie equalizer (R-pch6-9), as a snapshot that outlives the run; H at the carrier plus the offset at the window time after record sample start, with the run's latency h as the phase slope a received envelope carries: (IProbe path, carrier Hz, window start) to the
        /// channel receiving at that probe's node. Null when the run has no channel.</summary>
        public Func<string, double, long, IChannelResponse?>? Responder(ElaboratedNetlist nl)
        {
            if (_chs.Count == 0) return null;
            var chs = _chs.ToList();
            double h = _h;
            var island = PropagationChannelGraph.Islands(nl);
            var probeNode = nl.Components.Where(c => c.Model is IProbeModel && c.Nodes.Length > 0)
                              .ToDictionary(c => c.InstancePath, c => c.Nodes[0], StringComparer.Ordinal);
            return (probe, carrierHz, start) =>
            {
                if (!probeNode.TryGetValue(probe, out int node)) return null;
                if (PropagationChannelGraph.ReceivingAt(nl, node, island) is not { } ec) return null;
                var c = chs.FirstOrDefault(x => ReferenceEquals(x.Ec.Model, ec.Model));
                return c is null ? null : new ChannelResponseOf(c.Model, carrierHz, c.Model.TimeOffsetS + start * h, h);
            };
        }

        public void Dispose()
        {
            foreach (var c in _chs) { c.Model.Stepping = false; c.Model.FrozenTimeS = c.Model.TimeOffsetS; }
            foreach (var c in _ichs) { c.Model.Stepping = false; c.Model.FrozenTimeS = c.Model.TimeOffsetS; }
            _chs.Clear();
            _ichs.Clear();
        }

        internal void Add(ICh c)
        {
            _ichs.Add(c);
            LatencyS = Math.Max(LatencyS, c.Steps.SelectMany(r => r).Select(x => x * _h).DefaultIfEmpty(_h).Max());
        }

        internal void Add(Ch c)
        {
            _chs.Add(c);
            LatencyS = Math.Max(LatencyS, c.Rays.SelectMany(r => r).Select(r => r.Steps * _h).DefaultIfEmpty(_h).Max());
        }
    }

    /// <summary>brief-pch-8 R-pch8-3 — a channel with an instance end in its envelope form, with its cost counted and said.</summary>
    private static ICh Instance(ElaboratedNetlist nl, ElaboratedComponent ec, PropagationChannelModel m, IReadOnlyList<double> productHz, double h)
    {
        int[] txNode, txRef;
        Complex z0;
        if (m.TxArray is { } ta)
        {
            int n = ta.Array.PortCount;
            txNode = [.. Enumerable.Range(0, n).Select(i => ta.Array.Radiates(i) ? ta.Ec.Nodes[i] : 0)];
            txRef = [.. Enumerable.Repeat(ta.Ec.ReferenceNode, n)];
            z0 = ta.Array.Z0;
        }
        else { txNode = [ec.Nodes[m.TxPin]]; txRef = [0]; z0 = m.End(0, productHz.FirstOrDefault(f => f > 0, 1e9)).Z0; }
        int[] rxSrc = m.RxArray is { } ra ? [.. Enumerable.Range(0, ra.Array.PortCount).Select(i => PhasedArrayModel.SourceNode(ra.Ec, i, ra.Array.PortCount))] : [];
        CollapsedElements = 0;
        if (m.TxEnd is not null && m.TxArray is null || m.RxEnd is not null && m.RxArray is null)
            throw new InvalidOperationException(PropagationChannelDiagnostics.FieldDataInEnvelope(ec.InstancePath).Render());
        var rays = new PropagationChannelModel.RayCoupling[productHz.Count][];
        var steps = new double[productHz.Count][];
        double worst = 1;
        for (int k = 0; k < productHz.Count; k++)
        {
            rays[k] = productHz[k] > 0 ? m.InstanceCouplings(productHz[k]) : [];
            steps[k] = [.. rays[k].Select(r => 1 + r.DelayS / h)];
            foreach (double s in steps[k]) worst = Math.Max(worst, s);
        }
        // brief-pch-8b R-pch8b-1 — a receive array on the fast path takes each ray as one source at its feed, in every product
        string collapseNote = "";
        if (m.RxArray is { } rca && PropagationChannelModel.CollapseEnabled)
        {
            var cols = productHz.Select(f => f > 0 ? AntennaArray.PhasedArrayFastPath.Receive(nl, rca.Ec, f) : (null, "")).ToArray();
            if (cols.All(c => c.Item1 is not null || c.Item2 == "") && cols.Any(c => c.Item1 is not null))
            {
                int feedNode = cols.First(c => c.Item1 is not null).Item1!.FeedNode;
                for (int k = 0; k < productHz.Count; k++)
                {
                    if (cols[k].Item1 is not { } col) continue;
                    Complex norton = 2 / (Math.Sqrt(col.Z0) * (1 + col.GammaF));
                    rays[k] = [.. rays[k].Select(rc =>
                    {
                        Complex t = 0, p = 0;
                        for (int n = 0; n < rc.RxTheta.Length; n++) { t += col.G[n] * rc.RxTheta[n]; p += col.G[n] * rc.RxPhi[n]; }
                        return rc with { RxTheta = [norton * t], RxPhi = [norton * p] };
                    })];
                }
                CollapsedElements = rxSrc.Length;
                rxSrc = [feedNode];
                collapseNote = $" The receive array '{rca.Ec.InstancePath}' takes them as one source at its feed: {cols.First(c => c.Item1 is not null).Item2}";
            }
            else collapseNote = " " + cols.FirstOrDefault(c => c.Item1 is null && c.Item2 != "").Item2;
        }
        int nr = rays.Select(r => r.Length).DefaultIfEmpty(0).Max();
        long madds = rays.Sum(r => (long)r.Length * (3L * txNode.Length + 2L * txNode.Length + 2L * Math.Max(1, rxSrc.Length)));
        LastInstanceCost = (madds, nr, txNode.Length, Math.Max(1, rxSrc.Length));
        var real = m.At(m.ReferenceHz(productHz.FirstOrDefault(f => f > 0, 1e9)));
        nl.AddNoteOnce("env-channel:" + ec.InstancePath, string.Format(CultureInfo.InvariantCulture,
            "Propagation Channel '{0}': {1} ray(s) between {2} transmit and {3} receive element(s), stepped explicitly and one way: each ray's departure field "
            + "from the transmit waves one step plus its excess delay (up to {4:G4} s) earlier, projected on each receive element; {5:N0} multiply-adds a step. "
            + "The bulk delay τ0 = {6:G4} s is reported and not simulated.{7}",
            ec.InstancePath, nr, txNode.Length, Math.Max(1, rxSrc.Length), real.MaxExcessDelayS, madds, real.BulkDelayS, collapseNote));
        return new ICh
        {
            Ec = ec, Model = m, TxNode = txNode, TxRef = txRef, Z0Tx = z0, RxSource = rxSrc, Rays = rays, Steps = steps, Hz = [.. productHz],
            Ring = (int)Math.Ceiling(worst) + 4, MultiplyAdds = madds,
        };
    }

    private sealed class ChannelResponseOf(PropagationChannelModel m, double carrierHz, double t0, double h) : IChannelResponse
    {
        public Complex Response(double offsetHz, double timeS)
            => m.H(carrierHz + offsetHz, t0 + timeS - m.FrozenTimeS) * Complex.FromPolarCoordinates(1, -2 * Math.PI * offsetHz * h);
    }

    /// <summary>
    /// Puts every Propagation Channel of <paramref name="nl"/> into its envelope form for one run whose products sit at
    /// <paramref name="productHz"/> (each ≥ 0, the frame the run steps the product in), stepped at <paramref name="h"/>, its step 0
    /// at absolute record sample <paramref name="i0"/>. <paramref name="drive"/> replaces a channel's transmit pin by a recording;
    /// <paramref name="record"/> keeps each transmit wave. Refuses a cycle of channels and a receive pin two channels drive.
    /// </summary>
    public static Run Apply(ElaboratedNetlist nl, IReadOnlyList<double> productHz, double h, long i0,
                            IReadOnlyDictionary<string, ChannelRecording>? drive = null, bool record = false)
    {
        var run = new Run(h, i0);
        var chans = nl.Components.Where(c => c.Model is PropagationChannelModel).ToList();
        if (chans.Count == 0) return run;
        if (PropagationChannelGraph.Cycles(nl) is { Count: > 0 } cycles)
            throw new InvalidOperationException(PropagationChannelDiagnostics.Cycle(cycles[0]).Render());
        if (PropagationChannelGraph.SharedReceivePins(nl) is { Count: > 0 } shared)
            throw new InvalidOperationException(PropagationChannelDiagnostics.SharedReceivePin(shared[0].Net, shared[0].Channels).Render());
        var inv = CultureInfo.InvariantCulture;
        foreach (var ec in chans)
        {
            var m = (PropagationChannelModel)ec.Model;
            m.FrozenTimeS = m.TimeOffsetS + i0 * h;
            if (m.HasInstanceEnd) { run.Add(Instance(nl, ec, m, productHz, h)); continue; }
            var rays = new Ray[productHz.Count][];
            var z0 = new Complex[productHz.Count];
            double worst = 0;
            for (int k = 0; k < productHz.Count; k++)
            {
                double f = productHz[k];
                z0[k] = m.End(0, f).Z0;
                rays[k] = [.. m.Terms(f).Select(t => new Ray { A = t.Amplitude, Steps = 1 + t.DelayS / h, Nu = t.DopplerHz, Drift = t.DriftHzps, Term = t })];
                foreach (var r in rays[k]) worst = Math.Max(worst, r.Steps);
            }
            ChannelRecording? d = null;
            if (drive is not null && drive.TryGetValue(ec.InstancePath, out var rec))
            {
                if (rec.Waves.Length != productHz.Count)
                    throw new InvalidOperationException(PropagationChannelDiagnostics.RecordingProducts(ec.InstancePath, rec.Waves.Length, productHz.Count).Render());
                d = rec;
            }
            run.Add(new Ch
            {
                Ec = ec, Model = m, Tx = ec.Nodes[0], Z0 = z0, Rays = rays, Hz = [.. productHz], Ring = (int)Math.Ceiling(worst) + 4, Drive = d,
                Record = record ? [.. Enumerable.Range(0, productHz.Count).Select(_ => new List<Complex>())] : null,
            });
            var real = m.At(m.ReferenceHz(productHz.FirstOrDefault(f => f > 0, 1e9)));
            nl.AddNoteOnce("env-channel:" + ec.InstancePath, string.Format(inv,
                "Propagation Channel '{0}': {1} ray(s) stepped explicitly, one way, with a latency of one step ({2:G4} s) plus each ray's excess " +
                "delay (up to {3:G4} s); the bulk delay τ0 = {4:G4} s is reported and not simulated{5}.",
                ec.InstancePath, real.Rays.Count, h, real.MaxExcessDelayS, real.BulkDelayS,
                d is null ? "" : "; its receive pin is driven by a recorded transmit wave"));
        }
        nl.AddNoteOnce("env-channel-settle", string.Format(inv,
            "envelope: the receive side of a Propagation Channel reads the start's steady state for its first {0:G4} s (one step plus the longest " +
            "excess delay); that stretch is left out of every measurement window.", run.LatencyS));
        return run;
    }
}
