namespace CircuitRF.Core.Devices.Mos;

/// <summary>
/// The level-1 MOS transistor — the Shichman-Hodges square law. The original compact MOSFET model
/// and still the one every later level is written as a departure from: a threshold with the body
/// effect on it, a quadratic channel current, and a single fitted parameter for the output slope.
///
/// <code>
///   Vth  = Vto + Gamma·(√(Phi − Vbs) − √Phi)
///   Vgt  = Vgs − Vth
///   Id   = 0                                                  Vgt ≤ 0        cutoff
///        = Beta·(Vgt − Vds/2)·Vds·(1 + Lambda·Vds)            Vds &lt; Vgt     linear
///        = (Beta/2)·Vgt²·(1 + Lambda·Vds)                     Vds ≥ Vgt      saturation
///   Beta = Kp·W/Leff
/// </code>
///
/// <para><b>Lambda is a fitted output conductance, not channel-length modulation done properly.</b>
/// It multiplies the current in BOTH regions — which is how the published law is stated, and which
/// is why it puts a small kink in <c>gds</c> at the saturation boundary rather than none at all.
/// Level 3 replaces it with an effective-length calculation; this one does not, and pretending
/// otherwise would make the two levels the same model with different names.</para>
///
/// <para><b>Cutoff is genuinely off: zero current AND zero derivatives.</b> There is no leakage
/// floor and no fudge conductance — the engine's own gmin already keeps the node alive, and a device
/// that added its own would double it exactly where it matters. Subthreshold conduction is a real
/// mechanism this law does not have; see <see cref="MosfetModelBase"/> for what that costs.</para>
/// </summary>
public sealed class MosfetLevel1Model : MosfetModelBase
{
    private readonly double _lambda;
    private readonly double _beta;

    /// <param name="lambda">
    /// Output-slope coefficient, 1/V. Zero means the output conductance is NOT modelled — never
    /// "the current saturates flat at zero volts of slope", which is the same thing said from the
    /// other side but is what a reader assumes a sentinel means.
    /// </param>
    public MosfetLevel1Model(
        Channel4 channel = Channel4.N,
        double vto = 1.0, double kp = 2.0e-5, double gamma = 0.0, double phi = 0.6,
        double lambda = 0.0, double nsub = 0.0,
        double w = 100e-6, double l = 100e-6, double ld = 0.0, double tox = 0.0, double uo = 600.0,
        double cgso = 0.0, double cgdo = 0.0, double cgbo = 0.0,
        double saturationCurrent = 1e-14, double saturationCurrentDensity = 0.0,
        double bulkEmission = 1.0,
        double cbd = 0.0, double cbs = 0.0, double cj = 0.0, double cjsw = 0.0,
        double ad = 0.0, double @as = 0.0, double pd = 0.0, double ps = 0.0,
        double pb = 0.8, double mj = 0.5, double mjsw = 0.33, double fc = 0.5,
        double rd = 0.0, double rs = 0.0, double rsh = 0.0, double nrd = 0.0, double nrs = 0.0,
        double tempC = Temperature.NominalC, double tnomC = Temperature.NominalC,
        double xti = 3.0, double eg = Temperature.SiliconBandgapEv)
        : base(channel, vto, kp, gamma, phi, nsub, w, l, ld, tox, uo,
               cgso, cgdo, cgbo,
               saturationCurrent, saturationCurrentDensity, bulkEmission,
               cbd, cbs, cj, cjsw, ad, @as, pd, ps, pb, mj, mjsw, fc,
               rd, rs, rsh, nrd, nrs, tempC, tnomC, xti, eg)
    {
        _lambda = lambda;
        // Beta is formed once here rather than per evaluation: W, Leff and the temperature-scaled
        // Kp are all constant for the device's lifetime, and Channel is on the Newton inner loop.
        _beta = Kp * Width / Leff;
    }

    /// <inheritdoc/>
    protected override (double Id, double Gm, double Gds, double Gmbs) Channel(
        double vgs, double vds, double vbs)
    {
        var (vth, dVthDVbs) = Threshold(vbs);
        double vgt = vgs - vth;
        if (vgt <= 0.0) return (0.0, 0.0, 0.0, 0.0);

        double lam  = 1.0 + _lambda * vds;
        double id, gm, gds;

        if (vds < vgt)
        {
            // Linear. Written as Beta·(Vgt − Vds/2)·Vds so the two factors of the derivative are
            // visible rather than recovered from an expanded polynomial.
            double core = (vgt - 0.5 * vds) * vds;
            id  = _beta * core * lam;
            gm  = _beta * vds * lam;
            gds = _beta * ((vgt - vds) * lam + core * _lambda);
        }
        else
        {
            double core = 0.5 * vgt * vgt;
            id  = _beta * core * lam;
            gm  = _beta * vgt * lam;
            gds = _beta * core * _lambda;
        }

        // The body effect reaches the current ONLY through the threshold, so its derivative is the
        // gate's, scaled by how far the threshold moves. Writing it any other way would let the two
        // disagree.
        return (id, gm, gds, -gm * dVthDVbs);
    }
}
