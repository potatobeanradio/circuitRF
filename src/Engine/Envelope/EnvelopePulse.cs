// ================================================================
//  EnvelopePulse.cs  —  what an envelope run on a pulsed-CW source
//  measures inside its pulses (brief ENV-PCW §4)
//
//  Cubes, at every IProbe (brief ENV-MEAS-3), from the power it delivers,
//  1/2 Re(V1 I1*), and the phase of its first node's fundamental:
//    Pout_t_dBm [branch, time]       delivered power, floored at -100 dBm
//    PulseTop_dBm, PulseDroop_dB,
//    PulsePhaseDroop_deg, PulseRise_s,
//    PulseFall_s [branch, pulse]     per whole pulse inside Stop
//    PulseToPulse_dB / _deg [branch] peak-to-peak over the pulses
//  A probe carrying no RF (a supply probe) is NaN in all of them.
//  Rise and fall are 10-90 % of the amplitude, sqrt of the delivered
//  power: at a resistor, the voltage's own 10-90 %.
//  The droop window is the flat top from MeasureStart to MeasureEnd
//  (fractions of the top). Droop is start - end, so a gain falling
//  across the top is positive, and the phase droop is signed the same
//  way: the output phase (relative to the input) at the window start
//  minus at its end.
// ================================================================

using System.Globalization;
using System.Numerics;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

internal static class EnvelopePulse
{
    /// <summary>Pout_t_dBm's floor: the off-time carries no harmonic-1 output at all.</summary>
    internal const double FloorDbm = -100;

    /// <summary>The pulse of a run whose only source plays one pCW carrier; null for every other run.</summary>
    internal static PulseShape? Of(EnvelopeRunResult r)
        => r.Sources.Length == 1 && r.Sources[0].Signal.Carriers.Count == 1 &&
           r.Sources[0].Signal.Carriers[0].Spec.Standard == CarrierStandard.Pcw
            ? SignalSpec.ResolvePulse(r.Sources[0].Signal.Carriers[0].Spec)
            : null;

    /// <summary>The whole pulses inside the run: the run time each rising edge leaves 0. Run time t is record time
    /// <paramref name="startS"/> + t.</summary>
    internal static List<double> Starts(PulseShape s, double startS, double lastT)
    {
        // a train's slots (and each pair's second pulse) in every record; one slot a PRI long for a plain pulse
        var (slots, _, record) = PulsedCw.Layout(s);
        double[] subs = s.Train is { PairSpacing: > 0 } tr ? [0, tr.PairSpacing] : [0];
        var list = new List<double>();
        double dur = s.End - s.Delay;
        for (long n = (long)Math.Floor((startS - s.Delay) / record); ; n++)
            foreach (double slot in slots)
                foreach (double sub in subs)
                {
                    double t = s.Delay + n * record + slot + sub - startS;
                    if (t + dur > lastT + 1e-12) return list;
                    if (t >= -1e-12) list.Add(t);
                }
    }

    /// <summary>Adds the pulse cubes; returns the sentence the Messages pane shows.</summary>
    internal static string Add(DataSet ds, EnvelopeRunResult r, PulseShape s, double startS, Axis branchAxis, bool[] noRf)
    {
        int nt = r.Time.Length, nb = branchAxis.Length;
        double h = r.StepS;
        var d = r.Drive[0];
        static double Power(Complex v, Complex i) => 0.5 * (v.Real * i.Real + v.Imaginary * i.Imaginary);
        static double Dbm(double p) => Math.Max(FloorDbm, 10 * Math.Log10(Math.Max(p, 1e-300) * 1e3));
        Complex At(Complex[] x, double t)
        {
            double u = Math.Clamp(t / h, 0, nt - 1);
            int i = Math.Min((int)Math.Floor(u), nt - 2);
            double a = u - i;
            return x[i] * (1 - a) + x[i + 1] * a;
        }
        static double Wrap(double deg) => deg - 360 * Math.Round(deg / 360);

        double top = s.TopEnd - s.TopStart;
        var starts = Starts(s, startS, r.Time[^1]);
        int n = starts.Count;
        var pout = new double[nb * nt];
        var pTop = new double[nb * n]; var droop = new double[nb * n]; var phDroop = new double[nb * n];
        var rise = new double[nb * n]; var fall = new double[nb * n];
        var p2pDb = new double[nb]; var p2pDeg = new double[nb];
        for (int b = 0; b < nb; b++)
        {
            var v = new Complex[nt]; var c = new Complex[nt]; var amp = new double[nt];
            for (int t = 0; t < nt; t++)
            {
                v[t] = r.IProbeVoltage[t][b][1]; c[t] = r.IProbeCurrent[t][b][1];
                amp[t] = Math.Sqrt(Math.Max(Power(v[t], c[t]), 0));
                pout[b * nt + t] = noRf[b] ? double.NaN : Dbm(Power(v[t], c[t]));
            }
            if (n == 0) continue;
            if (noRf[b])
            {
                foreach (var a in new[] { pTop, droop, phDroop, rise, fall }) Array.Fill(a, double.NaN, b * n, n);
                p2pDb[b] = p2pDeg[b] = double.NaN;
                continue;
            }
            double PowerAt(double t) => Power(At(v, t), At(c, t));
            double Phase(double t) => (At(v, t) / At(d, t)).Phase * 180 / Math.PI;
            var phStart = new double[n];
            for (int i = 0; i < n; i++)
            {
                double t0 = starts[i];
                double ws = t0 + (s.TopStart - s.Delay) + s.MeasureStart * top;
                double we = t0 + (s.TopStart - s.Delay) + s.MeasureEnd * top;
                int o = b * n + i;
                pTop[o] = Dbm(PowerAt(ws));
                droop[o] = pTop[o] - Dbm(PowerAt(we));
                phStart[i] = Phase(ws);
                phDroop[o] = Wrap(phStart[i] - Phase(we));
                double next = i + 1 < n ? starts[i + 1] : r.Time[^1];
                rise[o] = Crossing(amp, h, t0, ws, Math.Sqrt(Math.Max(PowerAt(ws), 0)), rising: true);
                fall[o] = Crossing(amp, h, we, next, Math.Sqrt(Math.Max(PowerAt(we), 0)), rising: false);
            }
            var tops = pTop.AsSpan(b * n, n).ToArray();
            p2pDb[b] = tops.Max() - tops.Min();
            var rel = phStart.Select(p => Wrap(p - phStart[0])).ToArray();
            p2pDeg[b] = rel.Max() - rel.Min();
        }
        ds.Add("Pout_t_dBm", new DataCube([branchAxis, new Axis("time", r.Time, "s")], pout));

        string window = string.Format(CultureInfo.InvariantCulture, "droop window {0:0.##}-{1:0.##} % of the top",
                                      100 * s.MeasureStart, 100 * s.MeasureEnd);
        if (n == 0)
            return $"Pulsed CW: no whole pulse inside Stop, so nothing was measured ({PulsedCw.FormatTime(s.End - s.Delay)} from the rising edge to the end of the fall)";
        var pulseAxis = new Axis("pulse", [.. Enumerable.Range(1, n).Select(i => (double)i)]);
        ds.Add("PulseTop_dBm", new DataCube([branchAxis, pulseAxis], pTop));
        ds.Add("PulseDroop_dB", new DataCube([branchAxis, pulseAxis], droop));
        ds.Add("PulsePhaseDroop_deg", new DataCube([branchAxis, pulseAxis], phDroop));
        ds.Add("PulseRise_s", new DataCube([branchAxis, pulseAxis], rise));
        ds.Add("PulseFall_s", new DataCube([branchAxis, pulseAxis], fall));
        ds.Add("PulseToPulse_dB", new DataCube([branchAxis], p2pDb));
        ds.Add("PulseToPulse_deg", new DataCube([branchAxis], p2pDeg));
        return $"Pulsed CW: {n} pulse{(n == 1 ? "" : "s")} measured, {window}";
    }

    /// <summary>The 10-90 % time of the amplitude <paramref name="y"/> between run times <paramref name="from"/> and
    /// <paramref name="to"/>, against the reference amplitude <paramref name="a"/>: the first upward (or downward) crossing
    /// of each level, interpolated between samples. NaN when either level is never crossed.</summary>
    private static double Crossing(double[] y, double h, double from, double to, double a, bool rising)
    {
        int i0 = Math.Max(0, (int)Math.Floor(from / h)), i1 = Math.Min(y.Length - 1, (int)Math.Ceiling(to / h));
        double First(double level)
        {
            for (int i = i0; i < i1; i++)
            {
                double m0 = y[i], m1 = y[i + 1];
                bool hit = rising ? m0 < level && m1 >= level : m0 > level && m1 <= level;
                if (hit) return (i + (level - m0) / (m1 - m0)) * h;
            }
            return double.NaN;
        }
        double t10 = First(0.1 * a), t90 = First(0.9 * a);
        return rising ? t90 - t10 : t10 - t90;
    }
}
