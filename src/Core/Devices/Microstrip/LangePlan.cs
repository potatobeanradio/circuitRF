using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a four-finger interdigitated coupler comes out to on one substrate.</summary>
/// <param name="PairZe">Even-mode impedance of two adjacent fingers, as Ou's equations ask for it.</param>
/// <param name="PairZo">Odd-mode impedance of two adjacent fingers.</param>
/// <param name="Ze">The coupler's own even-mode impedance, from the pair as drawn (Ou).</param>
/// <param name="Zo">The coupler's own odd-mode impedance, from the pair as drawn (Ou).</param>
/// <param name="Modes">MCLIN's modes at F of two adjacent fingers as drawn.</param>
public sealed record LangeDesign(
    double CouplingDb, double Z0, double FreqHz, int Fingers, double PairZe, double PairZo, double Ze, double Zo,
    double WMeters, double SMeters, double LengthMeters, KirschningJansenCoupled.Modes Modes, PassivePlan Plan);

/// <summary>
/// The interdigitated quadrature coupler (J. Lange, "Interdigitated stripline quadrature hybrid," IEEE Trans. MTT,
/// vol. 17, no. 12, pp. 1150-1151, Dec. 1969) in its unfolded four-finger form: four quarter-wave strips side by side,
/// alternate strips joined at both ends into two conductors, so that each conductor couples to the other across three
/// gaps instead of one. Pure: Coupling (dB), Z0 and F plus a substrate in, <see cref="LangeDesign"/> out.
///
/// <para><b>Ou's equations</b> (W. P. Ou, "Design equations for an interdigitated directional coupler," IEEE Trans.
/// MTT, vol. 23, no. 2, pp. 253-255, Feb. 1975). With nearest neighbours only, each finger has a capacitance to ground
/// and a mutual capacitance to each neighbour, both read off one pair of adjacent fingers: the pair's even-mode
/// admittance Ye = 1/Ze is an outer finger's own, Ym = (Yo − Ye)/2 is the mutual, and a finger between two others keeps
/// Ye·Ye/(Ye + Ym) of the ground share. Summing over N fingers with each conductor at one potential gives
/// Ze,N = Ze(Ze + Zo)/(Ze + (N − 1)Zo) and Zo,N = Zo(Ze + Zo)/(Zo + (N − 1)Ze), so the coupling is
/// (N − 1)(R² − 1)/((N − 1)(R² + 1) + 2R) with R = Ze/Zo (<see cref="Equivalent"/>). Inverting it for a coupling c and
/// Ze,N·Zo,N = Z0² gives R = (c + q)/((N − 1)(1 − c)), q = √(c² + (N − 1)²(1 − c²)), and Zo from Z0
/// (<see cref="PairFor"/>); <see cref="CoupledLineSynthesis"/> then finds the W and S whose MCLIN modes at F are that
/// pair. The length is λg/4 in the pair's mean εeff, as for the two-strip coupler.</para>
///
/// <para><b>Pins.</b> 1 the input and 2 the through port are the ends of the top finger, 3 the coupled port and 4
/// the isolated port the ends of the bottom finger: the order of <see cref="CoupledLineCouplerPlan"/>. The joins
/// across the fingers are NOT part of this part, as the Wilkinson's resistor is not: J1-J4 are the inner fingers' ends,
/// and a jumper from pin k to Jk joins that conductor (J1/J2 the third finger's near/far ends, J3/J4 the second's).
/// On a board that jumper is a bond wire, so layout, LVS and EM see it as the separate part it is.</para>
///
/// <para><b>Limits.</b> Ou's equations are TEM; in microstrip the even and odd εeff are taken as the pair's, and the
/// mode-velocity difference costs directivity as in any microstrip coupler. Non-adjacent fingers are not coupled in the
/// model, and the jumpers are assumed short against λ. Tight coupling needs narrow gaps: on 20 mil RO4350B at
/// 2.4 GHz, 3 dB needs 69 µm gaps between 138 µm fingers, under common board rules, and 4 dB needs 105 µm
/// (docs/design/rf-passives.md). Gaps under 0.1·h leave the coupled-line model's range and are refused.</para>
/// </summary>
public static class LangePlan
{
    /// <summary>The finger count this plan draws.</summary>
    public const int Fingers = 4;

    /// <summary>The part's pins, in terminal order: the four ports, then the jumper landings J1-J4 (Jk joins pin k).</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "4", "J1", "J2", "J3", "J4"];

    /// <summary>Ou's coupler modes for <paramref name="fingers"/> strips from the modes of two adjacent ones.</summary>
    public static (double Ze, double Zo) Equivalent(double ze, double zo, int fingers)
    {
        double n1 = fingers - 1;
        return (ze * (ze + zo) / (ze + n1 * zo), zo * (ze + zo) / (zo + n1 * ze));
    }

    /// <summary>The adjacent pair whose <see cref="Equivalent"/> couples <paramref name="couplingDb"/> between ports of
    /// <paramref name="z0"/>.</summary>
    public static (double Ze, double Zo) PairFor(double couplingDb, double z0, int fingers)
    {
        double c = Math.Pow(10.0, -couplingDb / 20.0), n1 = fingers - 1;
        double r = (c + Math.Sqrt(c * c + n1 * n1 * (1.0 - c * c))) / (n1 * (1.0 - c));
        double zo = z0 * Math.Sqrt((n1 + r) * (n1 * r + 1.0) / (r * (r + 1.0) * (r + 1.0)));
        return (r * zo, zo);
    }

    public static LangeDesign Build(double couplingDb, double z0, double fHz, MicrostripSubstrate s)
    {
        foreach (var (name, value) in new[] { ("Coupling", couplingDb), ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.lange.spec-not-positive",
                    DiagnosticSeverity.Error, "Lange coupler: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));

        var (pe, po) = PairFor(couplingDb, z0, Fingers);
        var g = CoupledLineSynthesis.Synthesize(pe, po, fHz, s, "Lange coupler");
        double w = PassivePlan.OnGrid(g.WMeters), gap = PassivePlan.OnGrid(g.SMeters), l = PassivePlan.OnGrid(g.QuarterWaveMeters);
        var modes = CoupledLineCouplerPlan.Modes(w, gap, l, fHz, s);
        var (ze, zo) = Equivalent(modes.Ze, modes.Zo, Fingers);
        // Fingers top to bottom: 1-2 (conductor A, the ports), J3-J4 (B), J1-J2 (A), 3-4 (B, the ports).
        var sections = new PlanSection[] { new PlanCoupledLine([1, 2, 7, 8, 5, 6, 3, 4], 0.0, 0.0, 0.0, w, gap, l, Fingers) };
        return new LangeDesign(couplingDb, z0, fHz, Fingers, pe, po, ze, zo, w, gap, l, modes,
            new PassivePlan(Pins, sections, NodeCount: 8));
    }
}
