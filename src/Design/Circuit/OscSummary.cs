// Brief NZ-9 — the oscillator analysis's one-line summaries, shared by Simulate, the `osc` verb and `explain --analysis`, so
// every surface says the same thing about the same run.

using System.Globalization;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Design.Circuit;

/// <summary>The oscillator analysis's sentences, in one place.</summary>
public static class OscSummary
{
    private static string Ghz(double hz) => (hz / 1e9).ToString("G7", CultureInfo.InvariantCulture);

    /// <summary>An offset from the carrier with its own unit: <c>100 Hz</c>, <c>4.64 kHz</c>, <c>10 MHz</c>.</summary>
    public static string Offset(double hz)
    {
        var (v, u) = hz >= 1e9 ? (hz / 1e9, "GHz") : hz >= 1e6 ? (hz / 1e6, "MHz") : hz >= 1e3 ? (hz / 1e3, "kHz") : (hz, "Hz");
        return v.ToString("G4", CultureInfo.InvariantCulture) + " " + u;
    }

    /// <summary>What a run will do, before it does it.</summary>
    public static string Describe(OscParams p)
        => $"Oscillator '{p.Analysis}': start-up search {Ghz(p.FstartHz)}-{Ghz(p.FstopHz)} GHz around {Ghz(p.FguessHz)} GHz, " +
           $"MaxHarm={p.MaxHarmonic}, tol={p.Tol.ToString("G3", CultureInfo.InvariantCulture)}" +
           (p.Probe.Length > 0 ? $", probe {p.Probe}" : "") +
           (p.PhaseNoise is { } pn
               ? $", phase noise at {pn.Npts} offsets {Offset(pn.StartHz)} to {Offset(pn.StopHz)}"
               : "");

    /// <summary>The start-up search's own answer: where the circuit starts, or that it does not.</summary>
    public static string Startup(OscStartup s, OscParams p)
        => s.StartHz is { } f
            ? $"start-up at {Ghz(f)} GHz: the small-signal {(s.Shunt ? "admittance" : "impedance")} at {s.Probe} is " +
              $"{s.Immittance.Real.ToString("G4", CultureInfo.InvariantCulture)} {(s.Shunt ? "S" : "Ohm")} there " +
              "(negative: the circuit starts)"
            : $"no start-up between {Ghz(p.FstartHz)} and {Ghz(p.FstopHz)} GHz: the small-signal " +
              $"{(s.Shunt ? "admittance" : "impedance")} at {s.Probe} has no negative real part where its imaginary part is zero";

    /// <summary>A solved oscillation, as one line.</summary>
    public static string Result(string analysis, OscResult r)
        => $"Oscillator '{analysis}': Fosc = {Ghz(r.FoscHz)} GHz, Aosc = {r.Aosc.ToString("G6", CultureInfo.InvariantCulture)} " +
           $"{(r.Startup.Shunt ? "V" : "A")} at {r.Startup.Probe} (start-up at {Ghz(r.Startup.StartHz ?? double.NaN)} GHz, " +
           $"{r.Iterations} Newton step(s))" + PhaseNoise(r);

    /// <summary>Brief NZ-10: the phase noise at the first and last offset, and the flicker corner when there is one.</summary>
    private static string PhaseNoise(OscResult r)
    {
        if (!r.Data.Contains("PN")) return "";
        var pn = r.Data["PN"];
        var f = pn.Axes[0].Values;
        string At(int i) => $"{pn.RealValues[i].ToString("F1", CultureInfo.InvariantCulture)} dBc/Hz at {Offset(f[i])}";
        double corner = r.Data.Contains("PNcorner") ? r.Data["PNcorner"].RealValues[0] : double.NaN;
        return $"; phase noise {At(0)} to {At(f.Length - 1)}" +
               (double.IsFinite(corner) ? $", flicker corner {Offset(corner)}" : "");
    }
}
