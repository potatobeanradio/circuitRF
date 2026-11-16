using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a coupled-line coupler comes out to on one substrate: the asked modes, the pair that has them, and the
/// plan.</summary>
/// <param name="Ze">Even-mode impedance asked for, Z0·√((1 + c)/(1 − c)).</param>
/// <param name="Zo">Odd-mode impedance asked for, Z0·√((1 − c)/(1 + c)).</param>
/// <param name="Modes">MCLIN's modes at F of the pair as drawn (W, S and L on the plan grid).</param>
public sealed record CoupledLineCouplerDesign(
    double CouplingDb, double Z0, double FreqHz, double Ze, double Zo,
    double WMeters, double SMeters, double LengthMeters, KirschningJansenCoupled.Modes Modes, PassivePlan Plan);

/// <summary>
/// The single-section backward-wave coupler: one quarter-wave edge-coupled pair (D. M. Pozar, Microwave Engineering,
/// the coupled-line coupler section). Pure: Coupling (dB), Z0 and F plus a substrate in,
/// <see cref="CoupledLineCouplerDesign"/> out; the pair is <see cref="CoupledLineSynthesis"/>'s inversion of MCLIN's own
/// modes at F, so the plan is one MCLIN section and the part simulates exactly as that MCLIN does.
///
/// <para><b>Pins</b>, MCLIN's own order: 1 the input (strip A's near end), 2 the through port (A's far end), 3 the
/// coupled port (strip B's near end, beside 1), 4 the isolated port (B's far end). Driven at 1, port 3 carries
/// −Coupling dB in quadrature with port 2, and the coupler is symmetric, so any port can be the input.</para>
///
/// <para><b>Limit: directivity.</b> In microstrip the odd mode is faster than the even mode, so the two cannot both
/// be a quarter wave long; the length is λg/4 at their mean εeff. Coupling and match hold at F, but the isolated
/// port sees the mode-velocity difference: on 20 mil RO4350B at 2.4 GHz the directivity is about 11 dB at 10 dB
/// coupling and under 5 dB at 20 dB, since the leak stays while the coupling falls. Tight coupling needs narrow
/// gaps: below 0.1·h the coupled-line model leaves its range and the coupling is refused
/// (<see cref="CoupledLineSynthesis"/>); on that board 10 dB needs 115 µm and 8 dB already 65 µm.</para>
/// </summary>
public static class CoupledLineCouplerPlan
{
    /// <summary>The part's pins, in terminal order: input, through, coupled, isolated.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "4"];

    public static CoupledLineCouplerDesign Build(double couplingDb, double z0, double fHz, MicrostripSubstrate s)
    {
        foreach (var (name, value) in new[] { ("Coupling", couplingDb), ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line-coupler.spec-not-positive",
                    DiagnosticSeverity.Error, "Coupled-line coupler: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));

        var (ze, zo) = CoupledLineSynthesis.ModeImpedances(couplingDb, z0);
        var g = CoupledLineSynthesis.Synthesize(ze, zo, fHz, s, "Coupled-line coupler");
        double w = PassivePlan.OnGrid(g.WMeters), gap = PassivePlan.OnGrid(g.SMeters), l = PassivePlan.OnGrid(g.QuarterWaveMeters);
        var sections = new PlanSection[] { new PlanCoupledLine([1, 2, 3, 4], 0.0, 0.0, 0.0, w, gap, l) };
        return new CoupledLineCouplerDesign(couplingDb, z0, fHz, ze, zo, w, gap, l, Modes(w, gap, l, fHz, s),
            new PassivePlan(Pins, sections, NodeCount: 4));
    }

    /// <summary>MCLIN's modes at F for a pair as drawn: what the model will stamp.</summary>
    internal static KirschningJansenCoupled.Modes Modes(double w, double gap, double l, double fHz, MicrostripSubstrate s)
        => new MicrostripCoupledLineModel(w, gap, l, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD,
            "(coupled-line plan)", s.RoughnessMeters).ModesAt(fHz);
}
