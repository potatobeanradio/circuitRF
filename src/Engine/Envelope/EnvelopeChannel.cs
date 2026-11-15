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

    /// <summary>The channels of one run: puts them back on <see cref="Dispose"/>, and carries their history.</summary>
    public sealed class Run : IDisposable
    {
        private readonly List<Ch> _chs = [];
        private readonly double _h;
        private readonly long _i0;
        private int _n;

        internal Run(double h, long i0) { _h = h; _i0 = i0; }

        public bool Any => _chs.Count > 0;

        /// <summary>The longest simulated delay, h + τ_max (s): the receive side's start that has no history of its own.</summary>
        public double LatencyS { get; private set; }

        /// <summary>The product being stamped: each channel stamps its two ends alone. Null: the steady state, H frozen.</summary>
        public int? Product { set { foreach (var c in _chs) c.Model.Stepping = value is not null; } }

        /// <summary>Takes each channel's branch rows from the stamp just made.</summary>
        public void Bind() { foreach (var c in _chs) { c.BTx = c.Model.PortBranches[0]; c.BRx = c.Model.PortBranches[1]; } }

        private static Complex Wave(Ch c, int k, Complex[] x)
        {
            Complex v = c.Tx > 0 ? x[c.Tx - 1] : Complex.Zero;
            return (v + c.Z0[k] * x[c.BTx]) / Math.Sqrt(c.Z0[k].Real);
        }

        /// <summary>The history before t = 0: the start's steady state.</summary>
        public void Start(Complex[][] x)
        {
            _n = 0;
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
                sum += r.A * Complex.FromPolarCoordinates(1, 2 * Math.PI * r.Nu * tt + Math.PI * r.Drift * tt * tt) * w;
            }
            return sum;
        }

        /// <summary>The receive rows' sources for product <paramref name="k"/> of the full step being taken.</summary>
        public void AddMain(int k, Complex[] b)
        {
            foreach (var c in _chs)
                if (c.Rays[k].Length > 0) b[c.BRx] += Drive(c, k, _n + 1, _n);
        }

        /// <summary>The same for sub-step <paramref name="i"/> of <paramref name="split"/> (still on the solved full steps:
        /// the latency is at least one step).</summary>
        public void AddSplit(int k, Complex[] b, int split, int i)
        {
            foreach (var c in _chs)
                if (c.Rays[k].Length > 0) b[c.BRx] += Drive(c, k, _n + (double)i / split, _n);
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
            _chs.Clear();
        }

        internal void Add(Ch c)
        {
            _chs.Add(c);
            LatencyS = Math.Max(LatencyS, c.Rays.SelectMany(r => r).Select(r => r.Steps * _h).DefaultIfEmpty(_h).Max());
        }
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
            var rays = new Ray[productHz.Count][];
            var z0 = new Complex[productHz.Count];
            double worst = 0;
            for (int k = 0; k < productHz.Count; k++)
            {
                double f = productHz[k];
                z0[k] = m.End(0, f).Z0;
                rays[k] = [.. m.Terms(f).Select(t => new Ray { A = t.Amplitude, Steps = 1 + t.DelayS / h, Nu = t.DopplerHz, Drift = t.DriftHzps })];
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
