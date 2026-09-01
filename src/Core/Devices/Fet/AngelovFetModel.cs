namespace CircuitRF.Core.Devices.Fet;

/// <summary>
/// Angelov (Chalmers) FET. Built around the bias of PEAK transconductance rather than pinch-off,
/// which is what makes it fit HEMTs — whose gm rises and then falls — where the pinch-off-referenced
/// laws cannot.
///
/// <code>
///   psi = P1·(Vgs − Vpk) + P2·(Vgs − Vpk)² + P3·(Vgs − Vpk)³
///   Id  = Ipk·(1 + tanh(psi))·(1 + Lambda·Vds)·tanh(Alpha·Vds)
/// </code>
///
/// <para><c>Ipk</c> is the drain current at peak gm and <c>Vpk</c> the gate bias where that occurs;
/// both are read straight off a measured gm curve, which is the model's practical appeal.</para>
///
/// <para><b>n-channel only, and deliberately so.</b> The other laws in this family take a
/// <c>Channel</c> and mirror cleanly, because their gate dependence is anchored to a threshold and
/// is even in it. This one's is the polynomial <c>P1-P3</c>, fitted directly against the gate
/// voltage: mirroring it would have to negate the ODD-order coefficients and leave the even ones
/// alone, and no published convention says that a p-channel card's coefficients are written that
/// way. Guessing would give a device that simulates and is quantitatively wrong — the one outcome
/// this family exists to refuse. A p-channel part is imported as the Curtice quadratic or Statz,
/// which is what a p-channel MESFET card is read as anyway.</para>
/// </summary>
public sealed class AngelovFetModel(
    double ipk = 0.1, double vpk = -1.0, double p1 = 1.0, double p2 = 0.0, double p3 = 0.0,
    double alpha = 2.0, double lambda = 0.0,
    double cgs = 0.0, double cgd = 0.0,
    double gateSaturationCurrent = 0.0, double gateEmissionCoefficient = 1.0,
    int capModel = 1, double vbi = 1.0, double mGrading = 0.5, double fc = 0.5,
    double tempC = FetModelBase.NominalTemperatureC,
    double tnomC = FetModelBase.NominalTemperatureC,
    double xti = 0.0, double eg = 1.16,
    double alphatc = 0.0, double vtotc = 0.0)
    : FetModelBase(cgs, cgd, gateSaturationCurrent, gateEmissionCoefficient,
                   capModel, vbi, mGrading, fc, tempC, tnomC, xti, eg)
{
    private readonly double _alpha = ScalePercentPerDegree(alpha, alphatc, DeltaT(tempC, tnomC));
    private readonly double _vpk   = ShiftPerDegree(vpk, vtotc, DeltaT(tempC, tnomC));

    protected override (double Id, double Gm, double Gds) DrainCurrent(double vgs, double vds)
    {
        double x    = vgs - _vpk;
        double psi  = x * (p1 + x * (p2 + x * p3));                   // Horner
        double dpsi = p1 + x * (2.0 * p2 + x * 3.0 * p3);

        double thG = System.Math.Tanh(psi);
        double thD = System.Math.Tanh(_alpha * vds);
        double lam = 1.0 + lambda * vds;

        double id  = ipk * (1.0 + thG) * lam * thD;
        double gm  = ipk * (1.0 - thG * thG) * dpsi * lam * thD;
        double gds = ipk * (1.0 + thG) * (lambda * thD + lam * _alpha * (1.0 - thD * thD));
        return (id, gm, gds);
    }
}
