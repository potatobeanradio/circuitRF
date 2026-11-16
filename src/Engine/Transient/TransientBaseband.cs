// ================================================================
//  TransientBaseband.cs  —  a modulated transient kept at baseband (brief MTR-2; docs/design/transient.md §8.1)
//
//  The run reports a uniform RF-rate output grid, 4(K+1) points a carrier period, to a sink and keeps none of it. The
//  sink forms, for every recorded quantity x and harmonic k = 0 … K, the envelope
//
//      X_k(t_m) = c_k · Σ w(t_j − t_m) x(t_j) e^(−jk2πFc·t_j) / Σ w(t_j − t_m),   c_0 = 1, c_k = 2 above,
//
//  w a Kaiser-windowed sinc low-pass flat to the signal band's edge (half the signal rate) and 80 dB down by the envelope's
//  Nyquist frequency. The envelopes are packed as an envelope run result and measured by EnvelopeDataSet.Build, so a
//  modulated transient has the envelope analysis's cubes (PSD, ACLR, EVM, AM-AM, …), read by the same code.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Engine.Transient;

internal static class TransientBaseband
{
    /// <summary>The low-pass's stopband attenuation (dB) and the Kaiser shape that gives it.</summary>
    private const double StopbandDb = 80;
    private static readonly double Beta = 0.1102 * (StopbandDb - 8.7);

    /// <summary>The cube that marks a baseband transient result (1).</summary>
    public const string StoreCube = "__TranStore";

    public static DataSet Run(TransientParams p, ElaboratedNetlist nl, AnalysisSettings? settings, RunControl? control)
    {
        string who = p.Name.Length > 0 ? $"transient '{p.Name}'" : "transient";
        var mods = nl.Components.Where(c => c.Model is ModSourceModel).ToArray();
        if (mods.Length == 0)
            throw new TransientRefusedException(
                $"{who}: Store=baseband takes its envelopes about a MOD_Source's Fc, and this bench has none; Store=rf keeps the waveform.");
        var models = mods.Select(c => (ModSourceModel)c.Model).ToArray();
        double fc = models[0].FcHz;
        if (models.Any(m => Math.Abs(m.FcHz - fc) > 1e-9 * fc))
            throw new TransientRefusedException(string.Format(CultureInfo.InvariantCulture,
                "{0}: Store=baseband takes the envelopes about one centre frequency, and {1} differ in Fc; Store=rf keeps the waveform.",
                who, string.Join(", ", mods.Select(c => c.InstancePath))));
        double recordStart = Key(mods[0], "RecordStart");
        if (mods.Any(c => Key(c, "RecordStart") != recordStart))
            throw new TransientRefusedException(
                $"{who}: Store=baseband reads every carrier from one place in its record, and {string.Join(", ", mods.Select(c => c.InstancePath))} differ in RecordStart.");

        // ── the envelope's own sampling rule (single fundamental) ───────────
        double need = 0, edge = 0;
        foreach (var m in models)
            foreach (var c in m.Signal.Carriers)
            {
                need = Math.Max(need, 2 * (Math.Abs(m.FcHz + c.OffsetHz - fc) + 2.5 * c.Spec.BandwidthHz));
                need = Math.Max(need, c.NativeRateHz);
                edge = Math.Max(edge, Math.Abs(m.FcHz + c.OffsetHz - fc) + 0.5 * c.Spec.BandwidthHz);
            }
        double rate = SignalRate.AtLeast(need, models.Select(m => m.Signal).ToArray());
        int osr = Math.Max(2, EnvelopeEngine.Oversample(new EnvelopeParams().Oversample, rate, edge));
        double rEnv = rate * osr, pass = rate / 2, stop = rEnv - pass;
        if (fc < rEnv)
            throw new TransientRefusedException(string.Format(CultureInfo.InvariantCulture,
                "{0}: Fc ({1:G4} Hz) is below the envelope's sample rate ({2:G4} Hz), so a neighbouring harmonic would fold into the "
                + "envelopes; Store=rf keeps the waveform.", who, fc, rEnv));
        int K = p.StoreHarm;
        double halfW = (StopbandDb - 7.95) / (2.285 * 2 * Math.PI * (stop - pass)) / 2;
        long m0 = (long)Math.Ceiling(halfW * rEnv - 1e-9), m1 = (long)Math.Floor((p.StopS - halfW) * rEnv + 1e-9);
        if (m1 - m0 + 1 < EnvelopeDataSet.MinimumSamples)
            throw new TransientRefusedException(string.Format(CultureInfo.InvariantCulture,
                "{0}: Stop ({1:G4} s) leaves fewer than {2} envelope samples at {3:G4} Hz once the downconverter's {4:G3} s window "
                + "is taken from each end; lengthen Stop.", who, p.StopS, EnvelopeDataSet.MinimumSamples, rEnv, 2 * halfW));

        // ── what is recorded: nets, supplies, each IProbe's first net; the engine appends the probes' currents ──
        (string name, int row)[] nets = [];
        ElaboratedComponent[] supplies = [];
        string[] probeNames = [];
        int nNet = 0, nSup = 0, nPr = 0;
        int[] Rows(int[] _)
        {
            nets = TransientEngine.RecordedNodes(nl);
            supplies = [.. nl.Components.Where(c => BranchOf(c.Model) >= 0 && c.Parameters.TryGetValue("Vdc", out var v)
                                                   && v.Kind == ValueKind.Real && v.AsReal() != 0)];
            var (names, _, probeNodes) = EnvelopeEngine.IProbesOf(nl);
            probeNames = names;
            nNet = nets.Length; nSup = supplies.Length; nPr = names.Length;
            return [.. nets.Select(n => n.row), .. supplies.Select(c => BranchOf(c.Model)), .. probeNodes];
        }

        var dc = new Downconverter(fc, K, rEnv, m0, m1, halfW, (pass + stop) / 2);
        // The RF grid, 4(K+1) points a carrier period. With MaxStep automatic the engine steps on a whole subdivision of it, at
        // least 20 steps a period of the highest frequency, so every output point is an accepted step rather than an
        // interpolation between two (whose error, ~1e-3 of the carrier at 20 steps a period, would land in the envelopes).
        double hRf = 1 / (4.0 * (K + 1) * fc);
        double fHi = models.Max(TransientModulated.HighestHz);
        double maxStep = p.MaxStepS > 0 ? p.MaxStepS : hRf / Math.Ceiling(20 * fHi * hRf - 1e-9);
        var tp = p with
        {
            StepS = hRf, StartS = 0, MaxStepS = maxStep, RecordRows = Rows, OutputSink = dc.Push, DiscardOutput = true,
        };
        var engine = new TransientEngine(nl, settings);
        var run = engine.Run(tp, control);
        foreach (var note in run.Notes) nl.AddNoteOnce("tran:" + note, note);
        if (dc.Times.Count == 0)
            throw new TransientRefusedException(run.StopReason ?? $"{who}: the run produced no envelope sample.");
        if (run.StopReason is { } stopped) nl.AddWarning(stopped);

        // ── the envelope run result ──────────────────────────────────────────
        int nt = dc.Times.Count, k1 = K + 1;
        var time = dc.Times.ToArray();
        Complex At(int t, int ch, int k) => dc.Env[t][ch * k1 + k];
        Complex[][][] Block(int from, int count)
        {
            var o = new Complex[nt][][];
            for (int t = 0; t < nt; t++)
            {
                o[t] = new Complex[count][];
                for (int c = 0; c < count; c++)
                {
                    o[t][c] = new Complex[k1];
                    for (int k = 0; k < k1; k++) o[t][c][k] = At(t, from + c, k);
                }
            }
            return o;
        }
        var supplyCurrent = new double[nt][];
        for (int t = 0; t < nt; t++)
        {
            supplyCurrent[t] = new double[nSup];
            for (int s = 0; s < nSup; s++) supplyCurrent[t][s] = At(t, nNet + s, 0).Real;
        }
        var played = engine.Modulated;
        var drive = played.Select(md => time.Select(md.Drive).ToArray()).ToArray();
        var signals = models.Select(m => m.Signal).ToArray();
        var er = new EnvelopeRunResult
        {
            Time = time, ProbeNames = [.. nets.Select(n => n.name)], Probe = Block(0, nNet),
            SupplyNames = [.. supplies.Select(c => c.InstancePath)], SupplyCurrent = supplyCurrent,
            SupplyVoltage = [.. supplies.Select(c => c.Parameters["Vdc"].AsReal())],
            IProbeNames = probeNames, IProbeCurrent = Block(nNet + nSup + nPr, nPr), IProbeVoltage = Block(nNet + nSup, nPr),
            Drive = drive, DriveRe = [.. played.Select(md => md.Model.Z.Real)],
            F0Hz = fc, StepS = 1 / rEnv, MaxHarm = K, SignalRateHz = rate, Sources = [.. played.Select(md => md.Model)],
            CarrierMix = [.. played.SelectMany(md => md.Model.Signal.Carriers.Select(_ => 1))],
            CarrierOffsetHz = [.. played.SelectMany(md => md.Model.Signal.Carriers.Select(c => md.Model.FcHz + c.OffsetHz - fc))],
        };

        double measureFrom = double.IsNaN(p.MeasureFromS) ? mods.Max(c => Key(c, "Ramp")) : p.MeasureFromS;
        double skip = Math.Max(0, measureFrom - time[0]);
        var plan = new EnvelopeRunner.Plan(new EnvelopeParams { StopS = p.StopS, StepS = 1 / rEnv, MaxHarm = K, StartS = recordStart + time[0] },
                                           1, 0, true, Name: p.Name);
        var ds = EnvelopeDataSet.Build(er, plan, skip, note: s => nl.AddNoteOnce("tran-baseband:" + s, s));
        ds.Add("Tran.AcceptedSteps", DataCube.Scalar(run.AcceptedSteps));
        ds.Add("Tran.RejectedSteps", DataCube.Scalar(run.RejectedSteps));
        ds.Add("Tran.NewtonIterations", DataCube.Scalar(run.NewtonIterations));
        ds.Add("Tran.Factorizations", DataCube.Scalar(run.Factorizations));
        ds.Add("Tran.DeviceEvaluations", DataCube.Scalar(run.DeviceEvaluations));
        ds.Add("__TranMaxStep_s", DataCube.Scalar(run.MaxStepS));
        ds.Add(StoreCube, DataCube.Scalar(1));
        if (run.StoppedAtS is double stoppedAt) ds.Add(TransientRunner.StoppedCube, DataCube.Scalar(stoppedAt));
        nl.AddNoteOnce("tran-baseband:" + p.Name, string.Format(CultureInfo.InvariantCulture,
            "{0}: stored at baseband — harmonics 0 to {1} of {2:G6} Hz at {3:G4} samples/s, {4:G4} s to {5:G4} s; measured from {6:G4} s. "
            + "Store=rf keeps the waveform.", who, K, fc, rEnv, time[0], time[^1], Math.Max(measureFrom, time[0])));
        return ds;
    }

    private static int BranchOf(ComponentModel m) => m switch { VdcModel v => v.LastBranchIndex, ToneSourceModel t => t.LastBranchIndex, _ => -1 };

    private static double Key(ElaboratedComponent ec, string key) =>
        ec.Parameters.TryGetValue(key, out var v) && v.Kind is ValueKind.Real or ValueKind.Complex
            ? (v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real) : 0;

    /// <summary>
    /// The streaming downconverter: every RF output point mixed down by each harmonic as it arrives, held only while an
    /// envelope sample's window still needs it, and each envelope sample formed once its window has passed.
    /// </summary>
    internal sealed class Downconverter(double fc, int k, double rEnv, long m0, long m1, double halfW, double cutoffHz)
    {
        private readonly List<(double T, Complex[] Mixed)> _buf = [];
        private long _m = m0;
        public List<double> Times { get; } = [];
        /// <summary>[envelope sample][channel · (K+1) + k].</summary>
        public List<Complex[]> Env { get; } = [];
        private static readonly double I0Beta = I0(Beta);

        private double Tm(long m) => m / rEnv;

        public void Push(double t, double[] row)
        {
            int k1 = k + 1;
            var mixed = new Complex[row.Length * k1];
            var rot = new Complex[k1];
            for (int h = 0; h < k1; h++) rot[h] = Complex.FromPolarCoordinates(1, -h * 2 * Math.PI * fc * t);
            for (int c = 0; c < row.Length; c++)
                for (int h = 0; h < k1; h++) mixed[c * k1 + h] = row[c] * rot[h];
            _buf.Add((t, mixed));
            while (_m <= m1 && t >= Tm(_m) + halfW * (1 - 1e-12))
            {
                Emit(_m++);
                double keep = Tm(_m) - halfW;
                int drop = 0;
                while (drop < _buf.Count && _buf[drop].T < keep) drop++;
                if (drop > 0) _buf.RemoveRange(0, drop);
            }
        }

        private void Emit(long m)
        {
            double tm = Tm(m);
            int n = _buf.Count == 0 ? 0 : _buf[0].Mixed.Length;
            var acc = new Complex[n];
            double sw = 0;
            foreach (var (t, mixed) in _buf)
            {
                double tau = t - tm;
                if (Math.Abs(tau) > halfW) continue;
                double x = 2 * cutoffHz * tau;
                double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double r = tau / halfW;
                double w = sinc * I0(Beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / I0Beta;
                sw += w;
                for (int i = 0; i < n; i++) acc[i] += w * mixed[i];
            }
            int k1 = k + 1;
            for (int i = 0; i < n; i++) acc[i] *= (i % k1 == 0 ? 1.0 : 2.0) / sw;
            Times.Add(tm);
            Env.Add(acc);
        }

        /// <summary>The modified Bessel function of the first kind, order 0 (its series).</summary>
        private static double I0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int j = 1; j < 60; j++)
            {
                term *= q / (j * j);
                sum += term;
                if (term < 1e-17 * sum) break;
            }
            return sum;
        }
    }
}
