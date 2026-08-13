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

    /// <summary>
    /// R-h9r2-14 — the default sweep: a FIXED window, Vgs −5 … 2.5 V in 16 steps and Vds 0 … 120 V in
    /// 120 steps, chosen for the SDD equation this document ships with, not centred on this document's
    /// own bias. <c>vgsSteps</c>/<c>vdsSteps</c> stay as parameters for a caller that wants a different
    /// resolution at the same window — only the fallback numbers moved.
    ///
    /// <para><b>This is a real change in kind, not just in numbers.</b> Before this, the default
    /// window followed the document's own bias (gate ±2 V around it, drain to 1.4× the supply) so a
    /// fresh document's DCIV family always bracketed its own operating point. A document biased far
    /// from Vgs −5…2.5 / Vds 0…120 now draws a family that does NOT bracket it. That is what the owner
    /// asked for — these numbers are chosen for the shipped SDD, not derived from any one document's
    /// bias — but it is the direct cost of dropping the old bias-centred behaviour. R-h9b-12's own
    /// override (<see cref="OverrideOf"/>) is untouched and still wins wherever the user has set one.
    /// </para>
    /// </summary>
    public static Key DefaultKey(CircuitModel model, int vgsSteps = 16, int vdsSteps = 120)
    {
        return new Key(
            model.StructuralKey,
            VgsMin: -5.0, VgsMax: 2.5, VgsSteps: Math.Max(2, vgsSteps),
            VdsMin: 0.0,  VdsMax: 120.0, VdsSteps: Math.Max(2, vdsSteps),
            DrainPort: 1);
    }

    /// <summary>
    /// R-h9b-12 — the user's own VGS/VDS sweep from the DCIV Sweeps dialog, or null when nothing has
    /// been set. <see cref="HarmonicaSettings"/>'s six fields are all-or-nothing: a partially-set
    /// group (a document hand-edited outside the dialog) is treated as absent rather than filling the
    /// gaps with <see cref="DefaultKey"/>'s own numbers, which would silently blend two windows into
    /// one nobody chose.
    /// </summary>
    public static Key? OverrideOf(CircuitModel model)
    {
        var s = model.Settings;
        if (s.DcivVgsMin is not { } vgsMin || s.DcivVgsMax is not { } vgsMax ||
            s.DcivVgsSteps is not { } vgsSteps ||
            s.DcivVdsMin is not { } vdsMin || s.DcivVdsMax is not { } vdsMax ||
            s.DcivVdsSteps is not { } vdsSteps)
            return null;

        return new Key(model.StructuralKey, vgsMin, vgsMax, vgsSteps, vdsMin, vdsMax, vdsSteps,
                       DrainPort: 1);
    }

    /// <summary>The key <see cref="HarmonicaSolver"/> actually solves against: the override when set,
    /// else <see cref="DefaultKey"/>.</summary>
    public static Key ResolvedKey(CircuitModel model) => OverrideOf(model) ?? DefaultKey(model);

    /// <summary>
    /// Validates a candidate override BEFORE it is written — R-h9b-12's "a robust validator so that a
    /// DCIV trace is always shown". min &lt; max on both axes, steps ≥ 2 on both, everything finite.
    /// </summary>
    public static bool IsValidOverride(double vgsMin, double vgsMax, int vgsSteps,
                                       double vdsMin, double vdsMax, int vdsSteps)
        => double.IsFinite(vgsMin) && double.IsFinite(vgsMax) && vgsMin < vgsMax && vgsSteps >= 2
        && double.IsFinite(vdsMin) && double.IsFinite(vdsMax) && vdsMin < vdsMax && vdsSteps >= 2;

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
