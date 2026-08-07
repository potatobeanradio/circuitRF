using CircuitRF.Core;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Harmonica;

/// <summary>One DC I–V curve: a gate bias and the drain current it draws across the Vds sweep.</summary>
public sealed record DcivCurve(double Vgs, double[] Vds, double[] Ids);

/// <summary>
/// §7.3's DCIV family — the static I–V curves the loadline is drawn over, and §6.8's <b>tier C</b>.
///
/// <para><b>Tier C is computed once and held</b> (owner-confirmed, §6.8): the family depends only on
/// the model, its parameters and the bias sweep range — <b>never on terminations</b>. That is what
/// makes it a clean cache boundary, and it is why <see cref="Key"/> exists: the scheduler compares
/// keys rather than re-deciding what a family depends on.</para>
///
/// <para><b>Conduction current, like everything else on the intrinsic plane (D1).</b> This reads the
/// model's own <c>i</c> from the <c>(i, q, dg, dc)</c> contract at a static operating point, so a
/// DCIV curve and the loadline drawn over it are the same quantity — which is the whole reason they
/// can share a plot without misleading.</para>
///
/// <para><b>Not an HB solve.</b> The device is evaluated directly at DC port voltages; there is no
/// Newton loop, no termination and no drive. A 9 × 200 family is 1,800 model evaluations, which is
/// why the whole family is cheap enough to compute eagerly and hold.</para>
/// </summary>
public static class DcivFamily
{
    /// <summary>
    /// What a family depends on, and nothing else. Two requests with equal keys must produce equal
    /// families — that equality IS tier C's cache rule, so it lives with the computation rather than
    /// being re-derived by whoever caches it.
    /// </summary>
    /// <param name="StructuralKey">The model's own structural identity — DUT, parameters, embedding.</param>
    public readonly record struct Key(
        string StructuralKey, double VgsMin, double VgsMax, int VgsSteps,
        double VdsMin, double VdsMax, int VdsSteps, int DrainPort);

    /// <summary>The default sweep: gate from pinch-off to a little past the operating bias, drain
    /// from 0 to about 1.4× the supply, which is the window a loadline actually occupies.</summary>
    public static Key DefaultKey(CircuitModel model, int vgsSteps = 9, int vdsSteps = 200)
    {
        // A current-biased document (Idq) has no Vgs to centre on until the DC solve has run, so
        // the window falls back to a conventional depletion-FET span rather than guessing one.
        double vgs = model.Bias.Vgs ?? -3.0;
        double vds = model.Bias.Vds;
        return new Key(
            model.StructuralKey,
            VgsMin: vgs - 2.0, VgsMax: vgs + 2.0, VgsSteps: Math.Max(2, vgsSteps),
            VdsMin: 0.0,       VdsMax: Math.Max(1.0, vds * 1.4), VdsSteps: Math.Max(2, vdsSteps),
            DrainPort: 1);
    }

    /// <summary>
    /// Sweeps the DUT's static I–V. <paramref name="ctx"/> supplies the elaborated device; nothing
    /// about the context's terminations, drive or converged state is read, which is what makes the
    /// result termination-independent by construction rather than by promise.
    /// </summary>
    public static IReadOnlyList<DcivCurve> Compute(HarmonicaContext ctx, Key key)
    {
        var dut = ctx.DutComponent;
        int ports = dut.Model.PortCount;
        int drain = Math.Clamp(key.DrainPort, 0, Math.Max(0, ports - 1));

        var vds = new double[key.VdsSteps];
        for (int i = 0; i < key.VdsSteps; i++)
            vds[i] = key.VdsMin + (key.VdsMax - key.VdsMin) * i / (key.VdsSteps - 1);

        var curves = new List<DcivCurve>(key.VgsSteps);
        for (int g = 0; g < key.VgsSteps; g++)
        {
            double vgs = key.VgsMin + (key.VgsMax - key.VgsMin) * g / (key.VgsSteps - 1);

            var points = new double[key.VdsSteps][];
            for (int i = 0; i < key.VdsSteps; i++)
            {
                var pv = new double[ports];
                if (ports > 0) pv[0] = vgs;                       // port 0 = gate–source
                if (ports > drain) pv[drain] = vds[i];            // port 1 = drain–source
                points[i] = pv;
            }

            var ids = new double[key.VdsSteps];
            var batch = dut.PrefersBatchEvaluate ? dut.EvaluateBatch(points) : null;
            for (int i = 0; i < key.VdsSteps; i++)
            {
                var res = batch is not null ? batch[i] : dut.Evaluate(new PortVoltages(points[i]));
                ids[i] = res.I.Length > drain ? res.I[drain] : 0.0;
            }

            curves.Add(new DcivCurve(vgs, vds, ids));
        }

        return curves;
    }
}
