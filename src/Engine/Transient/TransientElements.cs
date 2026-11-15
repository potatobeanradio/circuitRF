// ================================================================
//  TransientElements.cs  —  which elements a transient run can step, and the
//  sentence for each it cannot (docs/design/transient.md §5; brief-trn-1 R-trn1-9)
//
//  A linear element runs when its stamp is affine in jw — Y(w) = G + jw C with G
//  and C real — because then AffineStamp's two stamps recover it exactly at every
//  frequency. That is measured, element by element, not listed: each is stamped on
//  its own at 1 and 2 Hz, G and C recovered, and the prediction G + jw C compared
//  with its true stamp from 1 kHz to 100 GHz, terminated in 50 ohm (the envelope's
//  tangent measure, EnvelopeTangentCheck.TerminatedDiff). A lumped model comes out
//  at round-off; a line's delay, a Touchstone file's data or a frequency-dependent
//  resistance does not, and is refused by name until TRN-4 realizes it.
//
//  A few elements are refused by what they are, each naming the analysis that
//  does support it (note §5.5).
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.Envelope;

namespace CircuitRF.Engine.Transient;

public static class TransientElements
{
    /// <summary>The terminated relative error above which an element is not affine. Lumped models measure at ~1e-15; the
    /// shortest line the shipped models allow is many orders above this at 100 GHz.</summary>
    public const double AffineTolerance = 1e-6;

    /// <summary>The frequencies (Hz) the affine prediction is checked at.</summary>
    internal static readonly double[] CheckHz = [1e3, 1e6, 1e8, 1e9, 1e10, 1e11];

    /// <summary>The refusal, by what the element is, of one that no transient brief will step; null for every other.</summary>
    public static string? RefusalByKind(ElaboratedComponent ec) => ec.Model switch
    {
        ModSourceModel => $"{ec.InstancePath} (MOD_Source): modulated signals are simulated by the envelope analysis (type=env), which steps the signal's envelope instead of every carrier cycle.",
        MixerModel => $"{ec.InstancePath} (Mixer): a behavioural mixer is defined on the spectrum; harmonic balance (type=hb) and the envelope analysis support it.",
        PhasedArrayModel => $"{ec.InstancePath} (PhasedArray): a phased array is simulated by harmonic balance, S-parameter and envelope analyses.",
        PropagationChannelModel => $"{ec.InstancePath} (PropagationChannel): a propagation channel is simulated by S-parameter, harmonic balance and envelope analyses.",
        TunerModel => $"{ec.InstancePath} (Tuner): a tuner presents an impedance per harmonic; loadpull and harmonic balance support it.",
        WSProbeModel => $"{ec.InstancePath} (WSProbe): the stability probe is read by S-parameter and harmonic balance analyses.",
        OscProbeModel => $"{ec.InstancePath} (OscProbe): the oscillator probe belongs to the oscillator analysis (type=osc).",
        _ when ec.ComponentType == EmFieldDataRecord.TypeName =>
            $"{ec.InstancePath} (EmFieldData): an EM field record is read by the fields analysis (type=fields) and S-parameter analysis.",
        _ => null,
    };

    /// <summary>The sentence for an element whose stamp is not affine in jw (TRN-4 realizes these).</summary>
    public static string NotYetSupported(ElaboratedComponent ec, string why) =>
        $"{ec.InstancePath} ({ec.ComponentType}) is not yet supported in transient: {why}.";

    /// <summary>
    /// Every element of <paramref name="nl"/> a transient run cannot step, each with its sentence, in netlist order. Empty when
    /// the run can go ahead.
    /// </summary>
    public static List<string> Problems(ElaboratedNetlist nl)
    {
        var list = new List<string>();
        foreach (var ec in nl.Components)
        {
            if (RefusalByKind(ec) is { } byKind) { list.Add(byKind); continue; }
            if (ec.IsNonlinear)
            {
                if (ec.Model is SddModel sdd && sdd.ControlRefs.Length > 0)
                    list.Add(NotYetSupported(ec, "its equations read a control current"));
                continue;
            }
            if (ec.Model is MutualInductanceModel) continue;   // jwM between two inductor branches: affine by its stamp
            if (AffineError(ec) is { } why) list.Add(NotYetSupported(ec, why));
        }
        return list;
    }

    /// <summary>Why <paramref name="ec"/>'s stamp is not affine in jw, or null when it is.</summary>
    internal static string? AffineError(ElaboratedComponent ec)
    {
        try
        {
            const double d = 2 * Math.PI;
            var a1 = Stamped(ec, d);
            var a2 = Stamped(ec, 2 * d);
            // C = (A(2d) - A(d)) / jd, G = A(d) - jd C, over every entry either stamp has
            var keys = a1.Keys.Union(a2.Keys).ToList();
            var g = new Dictionary<long, Complex>();
            var c = new Dictionary<long, Complex>();
            double scale = 0, imag = 0;
            foreach (var k in keys)
            {
                var v1 = a1.GetValueOrDefault(k);
                var v2 = a2.GetValueOrDefault(k);
                var ck = (v2 - v1) / new Complex(0, d);
                var gk = v1 - new Complex(0, d) * ck;
                c[k] = ck; g[k] = gk;
                scale = Math.Max(scale, Math.Max(Math.Abs(gk.Real), Math.Abs(ck.Real) * 2 * Math.PI * 1e9));
                imag = Math.Max(imag, Math.Max(Math.Abs(gk.Imaginary), Math.Abs(ck.Imaginary) * 2 * Math.PI * 1e9));
            }
            if (scale > 0 && imag > 1e-9 * scale)
                return "its stamp has a reactance that falls with frequency, such as a capacitor in series on one branch (1/jωC), which needs a state of its own";
            double worst = 0, worstHz = 0;
            foreach (double hz in CheckHz)
            {
                double w = 2 * Math.PI * hz;
                var actual = Stamped(ec, w);
                var pred = new Dictionary<long, Complex>();
                foreach (var k in actual.Keys.Union(keys))
                    pred[k] = g.GetValueOrDefault(k) + new Complex(0, w) * c.GetValueOrDefault(k);
                double e = EnvelopeTangentCheck.TerminatedDiff(actual, pred);
                if (!(e <= worst)) { worst = e; worstHz = hz; }
            }
            if (!(worst <= AffineTolerance))
                return string.Format(CultureInfo.InvariantCulture,
                    "its response is not a fixed resistance, capacitance and inductance (it departs from one by {0:0.###}% at {1})",
                    100 * worst, Hz(worstHz));
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"it could not be stamped on its own to check it ({ex.Message})";
        }
        finally
        {
            // What the probe's own stamps queued (a line at its 1 Hz degeneracy, a model off its validity range at 100 GHz) is
            // about frequencies this check chose, not the run: it is dropped rather than reported.
            if (ec.Model is IReportsWarnings rw) rw.DrainWarnings();
        }
    }

    /// <summary>The element's own stamp at <paramref name="w"/>, as the linear partition enters it: a Port or Term is its
    /// termination admittance, as HbLinearExtractor stamps it.</summary>
    private static Dictionary<long, Complex> Stamped(ElaboratedComponent ec, double w)
    {
        if (ec.Model is PortModel or TermModel)
            return EnvelopeTangentCheck.Recorded(r =>
            {
                if (PortModel.TerminationAdmittance(ec, w) is { } y) r.AddAdmittance(ec.Nodes[0], ec.Nodes[1], y);
            });
        return EnvelopeTangentCheck.Stamped(ec, w);
    }

    private static string Hz(double hz) => hz switch
    {
        >= 1e9 => (hz / 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " GHz",
        >= 1e6 => (hz / 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " MHz",
        >= 1e3 => (hz / 1e3).ToString("0.###", CultureInfo.InvariantCulture) + " kHz",
        _ => hz.ToString("0.###", CultureInfo.InvariantCulture) + " Hz",
    };
}
