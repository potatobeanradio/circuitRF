using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>A synthesized edge-coupled pair: its width, gap and quarter-wave length, and the MCLIN modes it has at F.</summary>
public sealed record CoupledLineGeometry(double WMeters, double SMeters, double QuarterWaveMeters,
    KirschningJansenCoupled.Modes Modes);

/// <summary>
/// Even/odd-mode impedances to an edge-coupled microstrip pair, shared by every planned part built on coupled lines.
/// <see cref="ModeImpedances"/> is the textbook coupler: voltage coupling c = 10^(−C/20) at the quarter wave with
/// Ze·Zo = Z0², so Ze = Z0·√((1 + c)/(1 − c)) and Zo = Z0·√((1 − c)/(1 + c)) (D. M. Pozar, Microwave Engineering, the
/// coupled-line coupler section). <see cref="Synthesize"/> inverts MCLIN's OWN modes at F
/// (<see cref="KirschningJansenCoupled.Compute"/>, what <see cref="MicrostripCoupledLineModel.ModesAt"/> returns), so
/// the pair a plan draws is the pair the model then stamps: nothing is synthesized from a second set of formulas.
///
/// <para><b>The search.</b> Two nested bisections in log space over the coupled-line model's published range
/// (0.1 ≤ W/h ≤ 10, 0.1 ≤ S/h ≤ 10). For a gap, the width whose √(Ze·Zo) is the target: both modes fall as the strips
/// widen. Over the gap, the ratio Ze/Zo at that width: it falls as the strips part. A target outside that range is a
/// coded refusal naming the bound, never an extrapolated pair.</para>
///
/// <para><b>The length.</b> λg/4 at F in the mean of the two modes' εeff. Microstrip's odd mode runs faster than its
/// even mode, so no length is a quarter wave for both: the mean centres the two, and the leftover phase difference is
/// what limits a microstrip coupler's directivity.</para>
/// </summary>
public static class CoupledLineSynthesis
{
    private const int Steps = 60;

    /// <summary>The mode impedances of a matched coupler of <paramref name="couplingDb"/> (positive dB) between ports of
    /// <paramref name="z0"/>.</summary>
    public static (double Ze, double Zo) ModeImpedances(double couplingDb, double z0)
    {
        double c = Math.Pow(10.0, -couplingDb / 20.0);
        return (z0 * Math.Sqrt((1.0 + c) / (1.0 - c)), z0 * Math.Sqrt((1.0 - c) / (1.0 + c)));
    }

    /// <summary>The voltage coupling of modes <paramref name="ze"/>, <paramref name="zo"/>: (Ze − Zo)/(Ze + Zo).</summary>
    public static double Coupling(double ze, double zo) => (ze - zo) / (ze + zo);

    /// <summary>The pair whose MCLIN modes at <paramref name="fHz"/> are <paramref name="ze"/> and <paramref name="zo"/>
    /// on <paramref name="s"/>; <paramref name="part"/> names the part in a refusal. Not snapped to the plan grid.</summary>
    public static CoupledLineGeometry Synthesize(double ze, double zo, double fHz, MicrostripSubstrate s, string part)
    {
        if (!(zo > 0) || !(ze > zo) || !(fHz > 0))
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line.modes-invalid",
                DiagnosticSeverity.Error, "{part}: no coupled pair has Ze = {ze} ohm and Zo = {zo} ohm at F = {f} Hz; "
              + "Ze must exceed Zo and both, and F, be positive.", ("part", part), ("ze", ze), ("zo", zo), ("f", fHz)));

        var quiet = new MicrostripValidityReporter("(coupled-line synthesis search, not reported)");
        KirschningJansenCoupled.Modes Modes(double w, double gap)
            => KirschningJansenCoupled.Compute(fHz, w, gap, s.HMeters, s.TMeters, s.EpsR, quiet);

        double z0 = Math.Sqrt(ze * zo), ratio = ze / zo;
        double wLo = Math.Log(KirschningJansenCoupled.WOverHRange.Min * s.HMeters);
        double wHi = Math.Log(KirschningJansenCoupled.WOverHRange.Max * s.HMeters);

        // The width at this gap whose √(Ze·Zo) is z0, or NaN when no width in range reaches it.
        double WidthAt(double gap)
        {
            double Mean(double lnW) { var m = Modes(Math.Exp(lnW), gap); return Math.Sqrt(m.Ze * m.Zo); }
            if (z0 > Mean(wLo) || z0 < Mean(wHi)) return double.NaN;
            double lo = wLo, hi = wHi;
            for (int i = 0; i < Steps; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Mean(mid) > z0) lo = mid; else hi = mid;
            }
            return Math.Exp(0.5 * (lo + hi));
        }

        double RatioAt(double gap) { var m = Modes(WidthAt(gap), gap); return m.Ze / m.Zo; }

        double sMin = KirschningJansenCoupled.GapOverHRange.Min * s.HMeters;
        double sMax = KirschningJansenCoupled.GapOverHRange.Max * s.HMeters;
        foreach (double gap in new[] { sMin, sMax })
            if (double.IsNaN(WidthAt(gap)))
                throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line.width-unreachable",
                    DiagnosticSeverity.Error, "{part}: no strip width between {lo} m and {hi} m gives sqrt(Ze*Zo) = {z} ohm "
                  + "on this substrate.", ("part", part), ("lo", Math.Exp(wLo)), ("hi", Math.Exp(wHi)), ("z", z0)));
        if (ratio > RatioAt(sMin))
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line.gap-unreachable",
                DiagnosticSeverity.Error, "{part}: coupling {c} needs a gap under {smin} m (S/h = 0.1, the coupled-line "
              + "model's range); that gap reaches only {cmax}. Ask for weaker coupling, or a thinner substrate.",
                ("part", part), ("c", Coupling(ze, zo)), ("smin", sMin),
                ("cmax", (RatioAt(sMin) - 1.0) / (RatioAt(sMin) + 1.0))));
        if (ratio < RatioAt(sMax))
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line.gap-too-wide",
                DiagnosticSeverity.Error, "{part}: coupling {c} needs a gap over {smax} m (S/h = 10, the coupled-line "
              + "model's range). Ask for stronger coupling.", ("part", part), ("c", Coupling(ze, zo)), ("smax", sMax)));

        double sLo = Math.Log(sMin), sHi = Math.Log(sMax);
        for (int i = 0; i < Steps; i++)
        {
            double mid = 0.5 * (sLo + sHi);
            if (RatioAt(Math.Exp(mid)) > ratio) sLo = mid; else sHi = mid;
        }
        double sMeters = Math.Exp(0.5 * (sLo + sHi)), wMeters = WidthAt(sMeters);
        var modes = Modes(wMeters, sMeters);
        double quarter = PlanarLineAngle.LengthMeters(0.5 * (modes.EeffE + modes.EeffO), Math.PI / 2.0, fHz);
        return new CoupledLineGeometry(wMeters, sMeters, quarter, modes);
    }
}
