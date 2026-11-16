using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a tee divider comes out to on one substrate: the derived numbers, and the plan the model stamps and the
/// PCell draws.</summary>
/// <param name="W0Meters">Width of the Z0 line the input leaves on.</param>
/// <param name="Lines">The output transformers: one for both outputs when K = 1, else the one to 2 and the one to 3.</param>
public sealed record TeeDividerDesign(double Z0, double FreqHz, double K, double W0Meters, IReadOnlyList<WilkinsonLine> Lines,
    PassivePlan Plan);

/// <summary>
/// The lossless tee divider (D. M. Pozar, Microwave Engineering, 4th ed., §7.2, the lossless T-junction divider, and
/// §5.4, the quarter-wave transformer; a textbook, no DOI): one microstrip MTEE whose two through arms are quarter-wave
/// transformers to the outputs. Pure: specs and a substrate in, <see cref="TeeDividerDesign"/> out.
///
/// <para><b>The numbers.</b> At the junction the two arms are in parallel, and each must take its share: P3/P2 = K²
/// when the arm to 3 looks like Z0·(1 + K²)/K² and the arm to 2 like Z0·(1 + K²), which together are Z0. A quarter wave
/// of Z_t turns an output's Z0 into Z_t²/Z0, so the transformers are Z0·√(1 + K²) to 2 and Z0·√(1 + K²)/K to 3, √2·Z0
/// each for the equal split. Only the input is matched, and the outputs are not isolated: there is no resistor. Widths are
/// MLIN's at F; each transformer is λg/4 in MLIN's own dispersive εeff at F from the junction's reference plane, half
/// the Z0 branch's width from its centre, to its pin.</para>
///
/// <para><b>The plan</b> is that one MTEE, turned so the input (pin 1, one W0 of Z0 line on the branch) arrives from the
/// left and the transformers run up to pin 2 and down to pin 3.</para>
/// </summary>
public static class TeeDividerPlan
{
    /// <summary>The part's pins, in terminal order: the input and the two outputs.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3"];

    public static TeeDividerDesign Build(double z0, double fHz, MicrostripSubstrate s, double k = 1.0)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz), ("K", k) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.teedivider.spec-not-positive",
                    DiagnosticSeverity.Error, "Tee divider: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));

        const string part = "Tee divider", width = "device.teedivider.width-unreachable";
        double w0 = PassivePlan.OnGrid(BranchLinePlan.Width(z0, fHz, s, part, width));
        double z2 = z0 * Math.Sqrt(1.0 + k * k);
        var t2 = RacetrackRing.Quarter(k == 1.0 ? "transformers" : "transformer to 2", z2, fHz, s, part, width);
        var t3 = k == 1.0 ? t2 : RacetrackRing.Quarter("transformer to 3", z2 / k, fHz, s, part, width);
        double l1 = w0 / 2.0 + t2.QuarterWaveMeters, l2 = w0 / 2.0 + t3.QuarterWaveMeters;
        double l3 = w0 + Math.Max(t2.WMeters, t3.WMeters) / 2.0;

        // Pin 1 at the origin on the branch; the junction's centre one W0 plus half the wider arm to its right.
        var tee = new PlanTee(t2.WMeters, t3.WMeters, w0, l1, l2, l3, [2, 3, 1], l3, l1, 270.0);
        return new TeeDividerDesign(z0, fHz, k, w0, k == 1.0 ? [t2] : [t2, t3], new PassivePlan(Pins, [tee], 3));
    }
}
