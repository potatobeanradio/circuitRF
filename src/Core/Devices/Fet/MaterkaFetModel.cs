namespace CircuitRF.Core.Devices.Fet;

/// <summary>
/// Materka–Kacprzak FET. Distinctive in making the pinch-off voltage itself drain-dependent, which
/// is how it follows the saturation knee without a separate output-slope term.
///
/// <code>
///   Vp = Vp0 + Gamma·Vds
///   Id = Idss·(1 − Vgs/Vp)²·tanh(Alpha·Vds / (Vgs − Vp))
/// </code>
/// </summary>
public sealed class MaterkaFetModel(
    double idss = 0.1, double vp0 = -2.0, double gamma = 0.0, double alpha = 2.0,
    double cgs = 0.0, double cgd = 0.0,
    double gateSaturationCurrent = 0.0, double gateEmissionCoefficient = 1.0,
    int capModel = 1, double vbi = 1.0, double mGrading = 0.5, double fc = 0.5,
    double tempC = FetModelBase.NominalTemperatureC,
    double tnomC = FetModelBase.NominalTemperatureC,
    double xti = 0.0, double eg = 1.16,
    double alphatc = 0.0, double gammatc = 0.0, double vtotc = 0.0)
    : FetModelBase(cgs, cgd, gateSaturationCurrent, gateEmissionCoefficient,
                   capModel, vbi, mGrading, fc, tempC, tnomC, xti, eg)
{
    private readonly double _alpha = ScalePercentPerDegree(alpha, alphatc, DeltaT(tempC, tnomC));
    private readonly double _gamma = ScaleLinear(gamma, gammatc, DeltaT(tempC, tnomC));
    private readonly double _vp0   = ShiftPerDegree(vp0, vtotc, DeltaT(tempC, tnomC));

    protected override (double Id, double Gm, double Gds) DrainCurrent(double vgs, double vds)
    {
        double vp = _vp0 + _gamma * vds;
        double sep = vgs - vp;
        // Vp = 0 or Vgs = Vp are genuine singularities of the published form, not numerical noise.
        // Report the device as off rather than dividing: a large finite current here would be an
        // invention, and Newton would chase it.
        if (System.Math.Abs(vp) < 1e-12 || System.Math.Abs(sep) < 1e-12) return (0.0, 0.0, 0.0);

        double u = 1.0 - vgs / vp;
        if (u <= 0.0) return (0.0, 0.0, 0.0);                 // beyond pinch-off

        double w     = _alpha * vds / sep;
        double th    = System.Math.Tanh(w);
        double sech2 = 1.0 - th * th;

        double duDvgs = -1.0 / vp;
        double duDvds = vgs * _gamma / (vp * vp);              // via dVp/dVds = Gamma
        double dwDvgs = -_alpha * vds / (sep * sep);
        double dwDvds = _alpha * (sep + _gamma * vds) / (sep * sep);

        double id  = idss * u * u * th;
        double gm  = idss * (2.0 * u * duDvgs * th + u * u * sech2 * dwDvgs);
        double gds = idss * (2.0 * u * duDvds * th + u * u * sech2 * dwDvds);
        return (id, gm, gds);
    }
}
