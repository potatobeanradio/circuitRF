using System.Collections.Concurrent;
using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Devices.Coupled;

/// <summary>
/// The cross-section of a broadside-coupled pair: strip B at height <see cref="Hb"/> over the bottom
/// ground, strip A above it with <see cref="Hs"/> between their facing surfaces, laterally offset by
/// <see cref="Offset"/> centre to centre (A to +x). Every length in metres.
/// </summary>
/// <param name="W">Strip width (both strips).</param>
/// <param name="Offset">Lateral centre-to-centre offset, ≥ 0.</param>
/// <param name="Hs">Separation between the strips' facing surfaces (B's top to A's bottom).</param>
/// <param name="Hb">Bottom ground to strip B's bottom surface.</param>
/// <param name="Ta">Strip A's thickness.</param>
/// <param name="Tb">Strip B's thickness.</param>
/// <param name="EpsR">Relative permittivity of the dielectric.</param>
/// <param name="Ha">Strip A's top surface to the top ground — null when there is none (open above).</param>
/// <param name="Hd">Height of the dielectric's top surface over the bottom ground. With a top ground and
/// <c>Hd</c> at (or above) it, the medium is homogeneous (stripline).</param>
public sealed record BroadsideGeometry(double W, double Offset, double Hs, double Hb, double Ta, double Tb,
                                       double EpsR, double? Ha, double Hd)
{
    /// <summary>Height of strip A's bottom surface.</summary>
    public double YaBottom => Hb + Tb + Hs;

    /// <summary>The top ground's height, or null.</summary>
    public double? TopGround => Ha is { } ha ? YaBottom + Ta + ha : null;

    /// <summary>True when one dielectric fills everything between two grounds.</summary>
    public bool Homogeneous => TopGround is { } top && Hd >= top;

    public LayeredCrossSection ToCrossSection() => new(
        [new DielectricLayer(0.0, TopGround is { } top ? Math.Min(Hd, top) : Hd, EpsR)],
        TopGround,
        [
            new StripRect(Offset / 2 - W / 2, Offset / 2 + W / 2, YaBottom, YaBottom + Ta),     // A
            new StripRect(-Offset / 2 - W / 2, -Offset / 2 + W / 2, Hb, Hb + Tb),               // B
        ]);
}

/// <summary>
/// What one broadside cross-section solves to: the Maxwell capacitance matrices with and without the
/// dielectric, and the even/odd modes built from them.
/// </summary>
/// <param name="C">Maxwell C with the dielectric, F/m: [A, B] order.</param>
/// <param name="C0">Maxwell C with every dielectric replaced by vacuum, F/m.</param>
/// <param name="Ze">Even-mode impedance, Ω.</param>
/// <param name="Zo">Odd-mode impedance, Ω.</param>
/// <param name="EeffE">Even-mode effective permittivity.</param>
/// <param name="EeffO">Odd-mode effective permittivity.</param>
/// <param name="LossFactorE">Even mode's ∮σ₀²dl / (4·Q̄₀²), 1/m: the conductor attenuation is
/// α_c = Rs·Kr·LossFactor / Z (see <see cref="BroadsideCoupledPair"/>).</param>
/// <param name="LossFactorO">The odd mode's.</param>
/// <param name="Asymmetry">|C11 − C22| / ((C11 + C22)/2): zero for a symmetric pair.</param>
public sealed record BroadsideModes(double[,] C, double[,] C0, double Ze, double Zo, double EeffE, double EeffO,
                                    double LossFactorE, double LossFactorO, double Asymmetry);

/// <summary>
/// The physics BCLIN (offset broadside-coupled stripline) and MBCLIN (broadside-coupled microstrip)
/// share: a quasi-static solve of the cross-section by <see cref="QuasiStaticSolver2D"/>, the even/odd
/// modes it implies, and their loss.
///
/// <para><b>Modes.</b> With C (dielectric) and C₀ (vacuum) the 2×2 Maxwell matrices in [A, B] order,
/// each mode m is a uniform TEM line with C_m and C₀_m: Z_m = 1/(c·√(C_m·C₀_m)), εeff,m = C_m/C₀_m. A
/// homogeneous stripline has C₀ = C/εr exactly, so both modes travel at c/√εr (only one solve is
/// needed). An inhomogeneous medium gives the modes different velocities — the directivity limit.
/// For a symmetric pair (C11 = C22) the even and odd modes are exact: C_e = C11 + C12,
/// C_o = C11 − C12 (C12 &lt; 0 in Maxwell form). <b>An asymmetric pair</b> (Ha ≠ Hb, unequal
/// thicknesses, and always MBCLIN, whose strips sit at different heights) has no even/odd modes; its
/// true normal modes are the c and π modes of an asymmetric pair. Here it is described by the
/// SYMMETRIC-AVERAGE convention: C11 and C22 are replaced by their mean before the modes are formed
/// (likewise for C₀). That keeps the stamp a symmetric even/odd pair — so CLIN, MCLIN, BCLIN and MBCLIN
/// share one — at the cost of the small A/B port-impedance difference. The instance reports the
/// asymmetry when it exceeds 5 %.</para>
///
/// <para><b>Conductor loss</b> by the perturbational (power-loss) method on the solved field: a TEM
/// mode's surface current follows the surface charge of the vacuum-filled line, so with σ₀ that charge
/// density for the mode's excitation, the power lost per length is ½·Rs·κ²·∮σ₀²dl over every conducting
/// surface (both strips and the ground planes) with κ = I/Q̄₀, against ½·Z·I² carried by each of the two
/// lines: α_c = Rs·∮σ₀²dl / (4·Z·Q̄₀²) — Wheeler's incremental-inductance result evaluated directly on the
/// field. Rs = √(πfμ₀/σ), times the Hammerstad-Bekkadal roughness factor when a roughness is given. A
/// ZERO-thickness strip's edge makes ∮σ² diverge logarithmically, so its loss depends on the grid and is
/// an estimate; give T. <b>Dielectric loss</b>: MLIN's filling-factor form at the mode's εeff, which is
/// π·√εr·tanδ/λ₀ exactly in the homogeneous case.</para>
///
/// <para><b>Cost.</b> A cross-section is solved once and cached process-wide by its exact geometry, so
/// a frequency sweep, and every sweep point that leaves the geometry alone, re-uses it.</para>
/// </summary>
public static class BroadsideCoupledPair
{
    private static readonly ConcurrentDictionary<(BroadsideGeometry, bool), BroadsideModes> Cache = new();
    private const int CacheLimit = 512;

    /// <summary>
    /// The solved modes of <paramref name="g"/>, from the cache when this geometry was solved before.
    /// <paramref name="useHomogeneity"/> false always solves the vacuum line separately (MBCLIN's general
    /// path) even where one dielectric fills the box and C₀ = C/εr would do — which is what lets a test
    /// hold the two paths to each other.
    /// </summary>
    public static BroadsideModes Solve(BroadsideGeometry g, bool useHomogeneity = true)
    {
        var key = (g, useHomogeneity);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var modes = SolveUncached(g, useHomogeneity);
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[key] = modes;
        return modes;
    }

    private static BroadsideModes SolveUncached(BroadsideGeometry g, bool useHomogeneity)
    {
        var cs = g.ToCrossSection();
        var sol = QuasiStaticSolver2D.Solve(cs);
        CrossSectionSolution air;
        double[,] c0;
        bool homogeneous = useHomogeneity && g.Homogeneous;
        if (homogeneous)
        {
            // Vacuum is the same field with ε scaled out: C₀ = C/εr, and the charge distribution (so the
            // loss integral's ratio) is unchanged.
            c0 = new double[2, 2];
            var lam = new double[2, 2];
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    c0[i, j] = sol.C[i, j] / g.EpsR;
                    lam[i, j] = sol.SurfaceChargeSquare[i, j] / (g.EpsR * g.EpsR);
                }
            air = new CrossSectionSolution(c0, lam, sol.Nx, sol.Ny);
        }
        else
        {
            air = QuasiStaticSolver2D.Solve(cs, airOnly: true);
            c0 = air.C;
        }

        const double c = MicrostripLoss.SpeedOfLight;
        double avg = (sol.C[0, 0] + sol.C[1, 1]) / 2.0, avg0 = (c0[0, 0] + c0[1, 1]) / 2.0;
        double ce = avg + sol.C[0, 1], co = avg - sol.C[0, 1];
        double ce0 = avg0 + c0[0, 1], co0 = avg0 - c0[0, 1];
        double ze = 1.0 / (c * Math.Sqrt(ce * ce0)), zo = 1.0 / (c * Math.Sqrt(co * co0));
        double eeffE = homogeneous ? g.EpsR : ce / ce0, eeffO = homogeneous ? g.EpsR : co / co0;

        var l = air.SurfaceChargeSquare;
        double lossE = (l[0, 0] + l[1, 1] + 2.0 * l[0, 1]) / (4.0 * ce0 * ce0);
        double lossO = (l[0, 0] + l[1, 1] - 2.0 * l[0, 1]) / (4.0 * co0 * co0);
        double asym = Math.Abs(sol.C[0, 0] - sol.C[1, 1]) / avg;
        return new BroadsideModes(sol.C, c0, ze, zo, eeffE, eeffO, lossE, lossO, asym);
    }

    /// <summary>
    /// Per-mode propagation constants γ = α + jβ (1/m) at <paramref name="freqHz"/> for the solved
    /// <paramref name="m"/>. Zero at DC.
    /// </summary>
    public static (Complex Even, Complex Odd) Gamma(BroadsideModes m, double freqHz, double epsR,
                                                    double sigmaSPerM, double tanD, double roughnessMeters)
    {
        if (!(freqHz > 0.0)) return (Complex.Zero, Complex.Zero);
        double rs = sigmaSPerM > 0 && !double.IsPositiveInfinity(sigmaSPerM) ? MicrostripLoss.SurfaceResistance(freqHz, sigmaSPerM) : 0.0;
        double kr = rs > 0 && roughnessMeters > 0
            ? MicrostripLoss.RoughnessFactor(roughnessMeters, MicrostripLoss.SkinDepth(freqHz, sigmaSPerM)) : 1.0;
        Complex One(double z, double eeff, double lossFactor)
        {
            double alphaC = rs * kr * lossFactor / z;
            double alphaD = DielectricLoss(freqHz, epsR, eeff, tanD);
            return new Complex(alphaC + alphaD, 2.0 * Math.PI * freqHz / MicrostripLoss.SpeedOfLight * Math.Sqrt(eeff));
        }
        return (One(m.Ze, m.EeffE, m.LossFactorE), One(m.Zo, m.EeffO, m.LossFactorO));
    }

    /// <summary>MLIN's filling-factor dielectric loss, with its homogeneous limit π·√εr·tanδ/λ₀ taken
    /// exactly (the general form is 0/0 at εr = 1).</summary>
    public static double DielectricLoss(double freqHz, double epsR, double eeff, double tanD)
    {
        if (tanD == 0.0) return 0.0;
        if (eeff >= epsR || epsR <= 1.0)
            return Math.PI * Math.Sqrt(epsR) * tanD * freqHz / MicrostripLoss.SpeedOfLight;
        return MicrostripLoss.DielectricLossNpPerM(freqHz, epsR, eeff, tanD);
    }

    /// <summary>
    /// Reports what the solved modes cannot vouch for: the symmetric-average convention on an asymmetric
    /// pair, and a cross-section no longer small against the wavelength (the quasi-static limit).
    /// </summary>
    public static void ReportLimits(string part, BroadsideModes m, BroadsideGeometry g, double freqHz,
                                    MicrostripValidityReporter reporter)
    {
        if (m.Asymmetry > 0.05)
            reporter.ReportOnce($"{part}:asymmetric",
                $"the two strips are not symmetric (C11 and C22 differ by {m.Asymmetry * 100:G3} %), so the pair has no exact " +
                "even/odd modes; it is modelled by the symmetric-average convention (C11 and C22 replaced by their mean).");
        ReportQuasiStatic(part, Math.Max(g.W + g.Offset, g.TopGround ?? (g.YaBottom + g.Ta)), g.EpsR, freqHz, reporter);
    }

    /// <summary>Reports a cross-section of <paramref name="size"/> (m) no longer small against the wavelength in
    /// the dielectric at <paramref name="freqHz"/>: past a tenth of it, a quasi-static (TEM) solve is past its range.
    /// Shared by every part whose line constants come from <see cref="QuasiStaticSolver2D"/>.</summary>
    public static void ReportQuasiStatic(string part, double size, double epsR, double freqHz,
                                         MicrostripValidityReporter reporter)
    {
        double lambda = MicrostripLoss.SpeedOfLight / (freqHz * Math.Sqrt(epsR));
        if (freqHz > 0 && size > lambda / 10.0)
            reporter.ReportOnce($"{part}:quasi-static",
                $"the cross-section ({size * 1e3:G3} mm) exceeds a tenth of the wavelength in the dielectric at " +
                $"{freqHz / 1e9:G4} GHz; the quasi-static (TEM) model is past its range there.");
    }
}
