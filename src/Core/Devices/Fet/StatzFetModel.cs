namespace CircuitRF.Core.Devices.Fet;

/// <summary>
/// Statz FET. Replaces the quadratic law's <c>tanh</c> knee with a piecewise-cubic one
/// and adds a transconductance-compression denominator.
///
/// <code>
///   Vg = Vgs − Vto,  Vg &gt; 0
///   Id = Beta·Vg² / (1 + B·Vg) · f(Vds) · (1 + Lambda·Vds)
///   f  = 1 − (1 − Alpha·Vds/3)³      0 &lt; Vds &lt; 3/Alpha
///      = 1                           Vds ≥ 3/Alpha
/// </code>
///
/// <para>The knee is continuous in value AND slope at <c>Vds = 3/Alpha</c> by construction — the
/// cubic's derivative there is zero — so no smoothing is needed and none is applied.</para>
/// </summary>
public sealed class StatzFetModel(
    double vto = -2.0, double beta = 0.02, double b = 0.3, double alpha = 2.0, double lambda = 0.0,
    double cgs = 0.0, double cgd = 0.0,
    double gateSaturationCurrent = 0.0, double gateEmissionCoefficient = 1.0,
    int capModel = 1, double vbi = 1.0, double mGrading = 0.5, double fc = 0.5,
    double tempC = FetModelBase.NominalTemperatureC,
    double tnomC = FetModelBase.NominalTemperatureC,
    double xti = 0.0, double eg = 1.16,
    double betatc = 0.0, double alphatc = 0.0, double vtotc = 0.0,
    FetModelBase.Channel channel = FetModelBase.Channel.N)
    : FetModelBase(cgs, cgd, gateSaturationCurrent, gateEmissionCoefficient,
                   capModel, vbi, mGrading, fc, tempC, tnomC, xti, eg, channel)
{
    // Negated for a p-channel device — see CurticeQuadraticFetModel for why.
    private readonly double _vto   = (double)(int)channel * ShiftPerDegree(vto, vtotc, DeltaT(tempC, tnomC));
    private readonly double _beta  = ScalePercentPerDegree(beta, betatc, DeltaT(tempC, tnomC));
    private readonly double _alpha = ScalePercentPerDegree(alpha, alphatc, DeltaT(tempC, tnomC));

    protected override (double Id, double Gm, double Gds) DrainCurrent(double vgs, double vds)
    {
        double vg = vgs - _vto;
        if (vg <= 0.0 || vds <= 0.0) return (0.0, 0.0, 0.0);

        double den = 1.0 + b * vg;
        double num = _beta * vg * vg / den;
        // d/dVg of Beta·Vg²/(1+B·Vg)  =  Beta·Vg·(2 + B·Vg)/(1+B·Vg)²
        double dnum = _beta * vg * (2.0 + b * vg) / (den * den);

        double knee = 3.0 / _alpha;
        double f, df;
        if (vds < knee)
        {
            double u = 1.0 - _alpha * vds / 3.0;
            f  = 1.0 - u * u * u;
            df = _alpha * u * u;                 // d/dVds of 1−u³ with du/dVds = −Alpha/3
        }
        else { f = 1.0; df = 0.0; }

        double lam = 1.0 + lambda * vds;
        double id  = num * f * lam;
        double gm  = dnum * f * lam;
        double gds = num * (df * lam + f * lambda);
        return (id, gm, gds);
    }
}
