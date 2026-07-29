namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// MKlopf's alternate parameter-entry routes (brief-mtaper-mklopf.md R-klp-3/R-klp-3a): Z1/Z2 or
/// W1/W2 for the taper's endpoint impedances; L or F3db for its length. <see cref="ComponentModelFactory"/>
/// picks whichever route is present at simulation time; this class is the SAME conversion math,
/// exposed so a UI-layer "switch entry mode" affordance (which must show the equivalent value in the
/// other route, not a fresh default) computes it identically rather than re-deriving it — one
/// implementation, reused, per this codebase's own standing rule for exactly this class of duplication.
/// </summary>
public static class MicrostripKlopfEntryConversion
{
    /// <summary>Z1/Z2 → the microstrip widths that produce those impedances on the given substrate
    /// (Hammerstad-Jensen inverse synthesis — the same one <c>ComponentModelFactory</c> uses when
    /// W1/W2 is the authoritative route).</summary>
    public static (double W1Meters, double W2Meters) ImpedanceToWidth(
        double z1, double z2, double hMeters, double tMeters, double epsR, MicrostripValidityReporter reporter)
    {
        double w1 = HammerstadJensen.SynthesizeWidth(z1, hMeters, tMeters, epsR, reporter);
        double w2 = HammerstadJensen.SynthesizeWidth(z2, hMeters, tMeters, epsR, reporter);
        return (w1, w2);
    }

    /// <summary>W1/W2 → the impedances those widths present on the given substrate (forward
    /// Hammerstad-Jensen — the same one <c>ComponentModelFactory</c> uses when Z1/Z2 is the
    /// authoritative route).</summary>
    public static (double Z1, double Z2) WidthToImpedance(
        double w1Meters, double w2Meters, double hMeters, double tMeters, double epsR, MicrostripValidityReporter reporter)
    {
        double z1 = HammerstadJensen.Compute(w1Meters, hMeters, tMeters, epsR, reporter).Z0;
        double z2 = HammerstadJensen.Compute(w2Meters, hMeters, tMeters, epsR, reporter).Z0;
        return (z1, z2);
    }

    /// <summary>L → the 3dB cutoff frequency that same length implies, at the taper's own endpoint
    /// impedances and substrate — the exact same eeff-at-center approach
    /// <c>ComponentModelFactory.CreateMicrostripKlopfModel</c> uses when L is the authoritative
    /// route (Z0(0)=√(Z1·Z2), R-klp's own geometric-mean center point, stands in for the taper's
    /// representative effective permittivity in the length↔cutoff duality).</summary>
    public static double LengthToF3db(
        double z1, double z2, double gammaMax, double lengthMeters,
        double hMeters, double tMeters, double epsR, MicrostripValidityReporter reporter)
    {
        double eeffCenter = CenterEeff(z1, z2, hMeters, tMeters, epsR, reporter);
        return KlopfensteinTaper.F3dbFromLength(z1, z2, gammaMax, lengthMeters, eeffCenter);
    }

    /// <summary>F3db → the length that cutoff implies, at the taper's own endpoint impedances and
    /// substrate — the same conversion <c>CreateMicrostripKlopfModel</c> uses when F3db is the
    /// authoritative route.</summary>
    public static double F3dbToLength(
        double z1, double z2, double gammaMax, double f3dbHz,
        double hMeters, double tMeters, double epsR, MicrostripValidityReporter reporter)
    {
        double eeffCenter = CenterEeff(z1, z2, hMeters, tMeters, epsR, reporter);
        return KlopfensteinTaper.LengthFromF3db(z1, z2, gammaMax, f3dbHz, eeffCenter);
    }

    private static double CenterEeff(
        double z1, double z2, double hMeters, double tMeters, double epsR, MicrostripValidityReporter reporter)
    {
        double zCenter = Math.Sqrt(z1 * z2);
        double wCenter = HammerstadJensen.SynthesizeWidth(zCenter, hMeters, tMeters, epsR, reporter);
        return HammerstadJensen.Compute(wCenter, hMeters, tMeters, epsR, reporter).Eeff;
    }
}
