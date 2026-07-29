namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// Microstrip loss (§3.1 layers 4-5 of brief-L5a-pcell-contract-and-microstrip.md /
/// microstrip-models.md §2): conductor loss (skin-effect surface resistance via H. A. Wheeler's
/// incremental-inductance rule, "Formulas for the Skin Effect," Proc. IRE, vol. 30, no. 9,
/// pp. 412–424, Sept. 1942, plus the OPTIONAL Hammerstad-Bekkadal surface-roughness correction —
/// R2: this decision is recorded, not left implicit) and dielectric loss. Cross-checked during
/// implementation research against two independent non-GPL sources (a direct literature summary
/// and the scikit-rf project's own independent `mline.py` implementation, BSD-3-Clause); the
/// dielectric-loss form was additionally hand-verified algebraically equivalent to the
/// filling-factor form quoted from Pozar's "Microwave Engineering."
///
/// <b>R2 decision: surface roughness IS implemented</b> (the correction is cheap, well-attributed,
/// and PCB copper is explicitly not smooth per §2's own framing) via <see cref="RoughnessFactor"/>,
/// applied as a multiplicative factor on the smooth-conductor loss.
/// </summary>
public static class MicrostripLoss
{
    /// <summary>Free-space permeability µ₀ (H/m), exact by SI definition (2019 redefinition:
    /// µ₀ is now a measured quantity very close to, but not exactly, 4π×10⁻⁷; this value is the
    /// CODATA recommended value, accurate to the precision this loss model needs).</summary>
    public const double Mu0 = 4.0 * Math.PI * 1.0e-7;

    public const double SpeedOfLight = 2.99792458e8; // m/s, exact by SI definition

    /// <summary>Skin depth δs = √(1/(π·f·µ₀·σ)).</summary>
    public static double SkinDepth(double freqHz, double sigmaSPerM) => Math.Sqrt(1.0 / (Math.PI * freqHz * Mu0 * sigmaSPerM));

    /// <summary>Surface resistance Rs = √(π·f·µ₀/σ).</summary>
    public static double SurfaceResistance(double freqHz, double sigmaSPerM) => Math.Sqrt(Math.PI * freqHz * Mu0 / sigmaSPerM);

    /// <summary>Hammerstad-Bekkadal roughness correction factor Kr = 1 + (2/π)·arctan[1.4·(Δ/δs)²]
    /// — Δ = RMS surface roughness (metres). Kr = 1 for a smooth (Δ=0) conductor.</summary>
    public static double RoughnessFactor(double roughnessRmsMeters, double skinDepthMeters)
    {
        if (roughnessRmsMeters <= 0) return 1.0;
        double ratio = roughnessRmsMeters / skinDepthMeters;
        return 1.0 + (2.0 / Math.PI) * Math.Atan(1.4 * ratio * ratio);
    }

    /// <summary>
    /// Conductor attenuation αc (Np/m), Wheeler's incremental-inductance rule in its common
    /// per-unit-length surface-resistance form: αc = Rs/(Z₀·W) · Kr. <paramref name="z0Ohms"/> is
    /// the (frequency-dependent) characteristic impedance at this frequency.
    /// </summary>
    public static double ConductorLossNpPerM(double freqHz, double sigmaSPerM, double wMeters, double z0Ohms,
        double roughnessRmsMeters = 0.0)
    {
        double rs = SurfaceResistance(freqHz, sigmaSPerM);
        double kr = roughnessRmsMeters > 0 ? RoughnessFactor(roughnessRmsMeters, SkinDepth(freqHz, sigmaSPerM)) : 1.0;
        return rs / (z0Ohms * wMeters) * kr;
    }

    /// <summary>
    /// Dielectric attenuation αd (Np/m): αd = π·εᵣ/(εᵣ−1) · (εeff−1)/√εeff · tanδ/λ₀, with
    /// λ₀ = c/f — algebraically the same expression as the filling-factor form
    /// q=(εeff−1)/(εᵣ−1), αd=(π/λ₀)·√εeff·q·tanδ·εᵣ/εeff, hand-verified equivalent during
    /// implementation. Weighted by the filling factor so the (partly air-filled) line does not
    /// overstate loss from the raw substrate tanδ (§2 layer 5's own framing).
    /// </summary>
    public static double DielectricLossNpPerM(double freqHz, double epsR, double eeff, double tanD)
    {
        double lambda0 = SpeedOfLight / freqHz;
        return Math.PI * epsR / (epsR - 1.0) * (eeff - 1.0) / Math.Sqrt(eeff) * tanD / lambda0;
    }
}
