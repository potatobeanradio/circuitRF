// Brief NZ-9 — the oscillator analysis's one-line summaries, shared by Simulate, the `osc` verb and `explain --analysis`, so
// every surface says the same thing about the same run.

using System.Globalization;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Design.Circuit;

/// <summary>The oscillator analysis's sentences, in one place.</summary>
public static class OscSummary
{
    private static string Ghz(double hz) => (hz / 1e9).ToString("G7", CultureInfo.InvariantCulture);

    /// <summary>What a run will do, before it does it.</summary>
    public static string Describe(OscParams p)
        => $"Oscillator '{p.Analysis}': start-up search {Ghz(p.FstartHz)}-{Ghz(p.FstopHz)} GHz around {Ghz(p.FguessHz)} GHz, " +
           $"MaxHarm={p.MaxHarmonic}, tol={p.Tol.ToString("G3", CultureInfo.InvariantCulture)}" +
           (p.Probe.Length > 0 ? $", probe {p.Probe}" : "");

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
           $"{r.Iterations} Newton step(s))";
}
