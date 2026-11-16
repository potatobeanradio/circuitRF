// ================================================================
//  TransientSources.cs  —  what a transient bench's sources say about the run, read with nothing solved
//  (docs/design/transient.md §8, §11; brief-trn-5)
//
//  The fundamental the Transient preset reads its spectrum at, the steering line explain prints when a
//  transient is the wrong analysis for the question, and the waveform sources whose DC value and t = 0
//  value differ — check's info.
// ================================================================

using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.Transient;

public static class TransientSources
{
    /// <summary>Periods of the highest source frequency past which a transient is steered to harmonic balance or envelope
    /// (note §11, R-trn5-5).</summary>
    public const double SteeringPeriods = 1e5;

    /// <summary>Every positive carrier frequency a source plays: the tone sources', a P1Tone's and a PnTone's tones, a
    /// MOD_Source's carrier and an SFFM's carrier. A pulse's edges are not a carrier and are not counted.</summary>
    public static IReadOnlyList<double> CarrierFrequencies(ElaboratedNetlist nl)
    {
        var hz = new List<double>();
        foreach (var ec in nl.Components)
        {
            IEnumerable<double> f = ec.Model switch
            {
                ToneSourceModelBase ts => ts.ToneFreqsHz,
                P1ToneModel p1 => [p1.FreqHz],
                PnToneModel pn => pn.ToneFreqsHz,
                ModSourceModel mod => [mod.FcHz],
                WaveformSourceModel w => [w.CarrierHz],
                _ => [],
            };
            hz.AddRange(f.Where(v => v > 0 && double.IsFinite(v)));
        }
        return hz;
    }

    /// <summary>The frequency a transient result's spectrum is read at: the lowest carrier any source plays, else a repeating
    /// pulse's repetition rate; null when the bench has neither.</summary>
    public static double? Fundamental(ElaboratedNetlist nl)
    {
        var carriers = CarrierFrequencies(nl);
        if (carriers.Count > 0) return carriers.Min();
        var periods = nl.Components.Select(c => c.Model).OfType<WaveformSourceModel>().Select(w => w.RepeatPeriodS).Where(p => p > 0).ToList();
        return periods.Count > 0 ? 1 / periods.Max() : null;
    }

    /// <summary>
    /// The steering line (note §11, R-trn5-5): when <paramref name="stopS"/> spans more than <see cref="SteeringPeriods"/>
    /// periods of the highest carrier, the analysis that answers the question in a fraction of the time — envelope when a
    /// source is modulated (a MOD_Source, an SFFM), harmonic balance otherwise. Null when the transient is a fair choice.
    /// </summary>
    public static string? Steering(ElaboratedNetlist nl, double stopS)
    {
        var carriers = CarrierFrequencies(nl);
        if (carriers.Count == 0 || !(stopS > 0)) return null;
        double fHi = carriers.Max(), periods = stopS * fHi;
        if (periods <= SteeringPeriods) return null;
        bool modulated = nl.Components.Any(c => c.Model is ModSourceModel || c.Model is WaveformSourceModel { IsModulatedCarrier: true });
        string count = Power(periods), f = Hz(fHi);
        return modulated
            ? $"this bench is {count} periods of a {f} carrier, and a source is modulated: an envelope analysis answers a modulated question in a fraction of the time."
            : $"this bench is {count} periods of a {f} carrier: harmonic balance answers a steady-state question in a fraction of the time.";
    }

    /// <summary>Each waveform source whose DC value (what every other analysis, and the operating point, sees) differs from its
    /// value at t = 0 (R-trn5-6): the DC point and the first step disagree on purpose, which is worth saying.</summary>
    public static IEnumerable<(string Instance, double Dc, double AtZero)> DcDiffersFromStart(ElaboratedNetlist nl)
    {
        foreach (var ec in nl.Components)
        {
            if (ec.Model is not WaveformSourceModel w) continue;
            double dc = w.SteadyValue, at0;
            try { at0 = w.ValueAt(0); }
            catch (Exception) { continue; }   // an Expr= that cannot be read at t = 0 is the run's to refuse
            if (Math.Abs(dc - at0) > 1e-12 * Math.Max(1, Math.Max(Math.Abs(dc), Math.Abs(at0))))
                yield return (ec.InstancePath, dc, at0);
        }
    }

    /// <summary>2.4·10⁶: two figures and a power of ten, as the note writes it.</summary>
    private static string Power(double v)
    {
        int e = (int)Math.Floor(Math.Log10(v));
        double m = Math.Round(v / Math.Pow(10, e), 1);
        if (m >= 10) { m /= 10; e++; }
        const string sup = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        string exp = string.Concat(e.ToString(CultureInfo.InvariantCulture).Select(c => c == '-' ? '⁻' : sup[c - '0']));
        return m.ToString("0.#", CultureInfo.InvariantCulture) + "·10" + exp;
    }

    private static string Hz(double f) => f switch
    {
        >= 1e9 => (f / 1e9).ToString("G4", CultureInfo.InvariantCulture) + " GHz",
        >= 1e6 => (f / 1e6).ToString("G4", CultureInfo.InvariantCulture) + " MHz",
        >= 1e3 => (f / 1e3).ToString("G4", CultureInfo.InvariantCulture) + " kHz",
        _ => f.ToString("G4", CultureInfo.InvariantCulture) + " Hz",
    };
}
