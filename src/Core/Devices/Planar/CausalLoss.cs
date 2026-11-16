namespace CircuitRF.Core.Devices.Planar;

/// <summary>
/// brief-trn-4b — the phase a line's loss comes with. A loss with no matching phase is not causal: no time-domain
/// realization can reproduce it, and no physical line has it. Two terms, each the mid-band form of what a causal
/// conductor and dielectric do:
///
/// <para><b>Conductor.</b> A skin-effect resistance comes with an internal reactance equal to it (the internal
/// impedance is proportional to √(jω)). The phase added is the conductor loss above its DC floor,
/// Δβ_c = α_c(f) − α_c(0⁺): a √f law adds all of it; a thickness-aware law, whose loss levels off at the DC sheet
/// resistance when the skin depth exceeds the metal, adds nothing for that floor.</para>
///
/// <para><b>Dielectric.</b> A loss tangent that is flat over many decades comes with a permittivity that falls by
/// (2/π)·tanD·εr per e-fold of frequency (the wideband Debye form between its corner frequencies). The phase added is
/// Δβ_d = −(2/π)·α_d(f)·ln(f/f_ref): zero at f_ref, where the stated Er and TanD hold; more phase below it, less above.
/// First order in tanD.</para>
/// </summary>
public static class CausalLoss
{
    /// <summary>The frequency the planar models' Er and TanD are stated at (Hz): the line's phase there is the lossless
    /// model's plus the conductor term only.</summary>
    public const double DielectricReferenceHz = 1e9;

    /// <summary>The frequency the conductor's DC floor is read at (Hz).</summary>
    public const double ConductorFloorHz = 1.0;

    /// <summary>The phase (rad/m, or rad for whole-line attenuations) added to a line with conductor attenuation
    /// <paramref name="alphaC"/> (its floor <paramref name="alphaC0"/>) and dielectric attenuation
    /// <paramref name="alphaD"/> at <paramref name="freqHz"/>, the dielectric pinned at <paramref name="refHz"/>.</summary>
    public static double Phase(double freqHz, double alphaC, double alphaC0, double alphaD, double refHz = DielectricReferenceHz)
    {
        if (!(freqHz > 0)) return 0;
        double c = Math.Max(0, alphaC - alphaC0);
        double d = alphaD > 0 && refHz > 0 ? -(2 / Math.PI) * alphaD * Math.Log(freqHz / refHz) : 0;
        return double.IsFinite(c + d) ? c + d : 0;
    }
}
