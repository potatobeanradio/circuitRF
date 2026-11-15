// ================================================================
//  ItuP526.cs  —  ITU-R P.526-16 (11/2025), diffraction
//
//  knifeedge  §4.1: one knife-edge of height h above the line between the
//             terminals, d1 and d2 from them: ν = h·√(2/λ·(1/d1 + 1/d2))
//             (eq (26)) and the loss J(ν) of eq (30), from the Fresnel
//             integrals.
//  sphere     §3.2: a smooth spherical Earth of effective radius ae at any
//             distance, 10 MHz and up. Beyond the marginal line-of-sight
//             distance, the first term of the residue series (§3.1.1,
//             eqs (13)-(18), self-consistent units); within it the loss
//             interpolated on the clearance h over the clearance hreq
//             that gives no loss, with the radius aem that makes the path
//             marginal (eqs (21)-(25)).
//  profile    §4.5: a general path over a terrain profile — the Bullington
//             loss of the actual profile, Lba, plus whatever the smooth
//             spherical Earth adds beyond the Bullington loss of the
//             smoothed profile: L = Lba + max(Lsph − Lbs, 0) (eq (66)).
//
//  The ground's permittivity and conductivity enter through the surface
//  admittance K (eqs (11), (12)) and β (eq (16)). β is taken as 1 where
//  §3.1.1.2 says it may be — horizontal polarization at every frequency,
//  vertical above 20 MHz over land or 300 MHz over sea (σ ≥ 1 S/m read as
//  sea) — and from eq (16) otherwise; K still sets the floor on G(Y). The
//  defaults are average land (εr = 22, σ = 0.003 S/m), the values P.1812
//  states for land.
//
//  The wavelength is 3e8/f: the published delta-Bullington validation
//  examples are computed with it (and with ae = 8500 km and β = 1), and
//  reproduce to 1e-12 dB only so (src/RfCore/RESOLVED.md).
// ================================================================

using System;
using System.Collections.Generic;

namespace RfCore.Propagation
{
    public static class ItuP526
    {
        public const string Document = "ITU-R P.526-16 (11/2025)";
        private const string M = "P526";
        public const double EarthRadiusKm = 6371;
        private const double C = 3e8;

        /// <summary>Eq (26): the diffraction parameter of a knife-edge h (m) above the terminal line, d1, d2 (m) from the
        /// terminals.</summary>
        public static double KnifeEdgeNu(double fHz, double hM, double d1M, double d2M)
        {
            double lambda = C / fHz;
            return hM * Math.Sqrt(2 / lambda * (1 / d1M + 1 / d2M));
        }

        public static PathLossResult KnifeEdge(double fHz, double hM, double d1M, double d2M)
        {
            PropagationMath.Range(M, "the frequency (knife-edge)", fHz / 1e6, 30, double.PositiveInfinity, "MHz");
            PropagationMath.Positive(M, "the distance d1 to the edge", d1M, "m");
            PropagationMath.Positive(M, "the distance d2 from the edge", d2M, "m");
            double nu = KnifeEdgeNu(fHz, hM, d1M, d2M);
            double j = Diffraction.KnifeEdgeExact(nu);
            double fs = PropagationMath.FreeSpaceDb(fHz, d1M + d2M);
            return new PathLossResult(M, Document, fs + j, new[] { new LossTerm("free space", fs), new LossTerm("knife-edge diffraction", j) }, 0,
                                      Notes: new[] { $"ν = {PropagationMath.F(nu)}." });
        }

        /// <summary>The ground's surface admittance K (eqs (11), (12), self-consistent units) for radius a (m).</summary>
        public static double Admittance(double lambdaM, double aM, double epsR, double sigma, bool vertical)
        {
            double x = 60 * lambdaM * sigma;
            double kh = Math.Pow(2 * Math.PI * aM / lambdaM, -1.0 / 3) * Math.Pow((epsR - 1) * (epsR - 1) + x * x, -0.25);
            return vertical ? kh * Math.Sqrt(epsR * epsR + x * x) : kh;
        }

        /// <summary>The first-term spherical-Earth diffraction loss −20·log10(E/E0) of eq (13) for radius a (m), dB;
        /// distances and heights in m.</summary>
        public static double FirstTerm(double lambdaM, double dM, double h1M, double h2M, double aM, double epsR, double sigma, bool vertical)
        {
            double k = Admittance(lambdaM, aM, epsR, sigma, vertical), k2 = k * k, k4 = k2 * k2;
            double fMhz = C / lambdaM / 1e6;
            bool betaOne = !vertical || fMhz > (sigma >= 1 ? 300 : 20);
            double beta = betaOne ? 1 : (1 + 1.6 * k2 + 0.67 * k4) / (1 + 4.5 * k2 + 1.53 * k4);
            double x = beta * Math.Cbrt(Math.PI / (lambdaM * aM * aM)) * dM;
            double yScale = 2 * beta * Math.Cbrt(Math.PI * Math.PI / (lambdaM * lambdaM * aM));
            double fx = x >= 1.6 ? 11 + 10 * Math.Log10(x) - 17.6 * x : -20 * Math.Log10(x) - 5.6488 * Math.Pow(x, 1.425);
            double G(double y)
            {
                double b = beta * y;
                double g = b > 2 ? 17.6 * Math.Sqrt(b - 1.1) - 5 * Math.Log10(b - 1.1) - 8 : 20 * Math.Log10(b + 0.1 * b * b * b);
                return Math.Max(g, 2 + 20 * Math.Log10(k));
            }
            return -(fx + G(yScale * h1M) + G(yScale * h2M));
        }

        /// <summary>§3.2: the smooth spherical-Earth diffraction loss at any distance, dB; distances and heights in m.</summary>
        public static double SphericalEarth(double fHz, double dM, double h1M, double h2M, double aeM, double epsR, double sigma, bool vertical)
        {
            double lambda = C / fHz;
            double dlos = Math.Sqrt(2 * aeM) * (Math.Sqrt(h1M) + Math.Sqrt(h2M));
            if (dM >= dlos) return FirstTerm(lambda, dM, h1M, h2M, aeM, epsR, sigma, vertical);

            double c = (h1M - h2M) / (h1M + h2M);
            double m = dM * dM / (4 * aeM * (h1M + h2M));
            double b = 2 * Math.Sqrt((m + 1) / (3 * m))
                         * Math.Cos(Math.PI / 3 + Math.Acos(3 * c / 2 * Math.Sqrt(3 * m / Math.Pow(m + 1, 3))) / 3);
            double d1 = dM / 2 * (1 + b), d2 = dM - d1;
            double h = ((h1M - d1 * d1 / (2 * aeM)) * d2 + (h2M - d2 * d2 / (2 * aeM)) * d1) / dM;
            double hreq = 0.552 * Math.Sqrt(d1 * d2 * lambda / dM);
            if (h > hreq) return 0;
            double aem = 0.5 * Math.Pow(dM / (Math.Sqrt(h1M) + Math.Sqrt(h2M)), 2);
            double ah = FirstTerm(lambda, dM, h1M, h2M, aem, epsR, sigma, vertical);
            if (ah < 0) return 0;
            return (1 - h / hreq) * ah;
        }

        public static PathLossResult Sphere(double fHz, double dM, double h1M, double h2M, double earthRadiusFactor,
                                            double epsR, double sigma, bool vertical)
        {
            PropagationMath.Range(M, "the frequency (spherical Earth)", fHz / 1e6, 10, double.PositiveInfinity, "MHz");
            PropagationMath.Positive(M, "the distance", dM, "m");
            PropagationMath.Positive(M, "the transmit height", h1M, "m");
            PropagationMath.Positive(M, "the receive height", h2M, "m");
            PropagationMath.Positive(M, "the effective Earth-radius factor", earthRadiusFactor, "");
            double l = SphericalEarth(fHz, dM, h1M, h2M, EarthRadiusKm * 1000 * earthRadiusFactor, epsR, sigma, vertical);
            double fs = PropagationMath.FreeSpaceDb(fHz, dM);
            return new PathLossResult(M, Document, fs + l, new[] { new LossTerm("free space", fs), new LossTerm("spherical-Earth diffraction", l) }, 0);
        }

        /// <summary>The parts of §4.5's general-path loss, dB.</summary>
        public readonly record struct GeneralPath(double Lba, double Lbs, double Lsph, double L, double HtsEff, double HrsEff);

        /// <summary>§4.5.2 over the profile's surface heights; antenna heights above ground at each end.</summary>
        public static GeneralPath GeneralPathLoss(double fHz, TerrainProfile profile, double txHeightM, double rxHeightM,
                                                  double earthRadiusFactor, double epsR, double sigma, bool vertical)
        {
            double lambda = C / fHz, ae = EarthRadiusKm * earthRadiusFactor;
            var d = profile.DistanceKm;
            var g = profile.SurfaceM;
            double hts = g[0] + txHeightM, hrs = g[^1] + rxHeightM;
            double lba = Diffraction.Bullington(d, g, hts, hrs, ae, lambda);
            var sm = Diffraction.SmoothSurface(d, g, hts, hrs);
            double htsP = hts - sm.Hstd, hrsP = hrs - sm.Hsrd;
            double lbs = Diffraction.Bullington(d, new double[d.Count], htsP, hrsP, ae, lambda);
            double lsph = SphericalEarth(fHz, profile.LengthKm * 1000, htsP, hrsP, ae * 1000, epsR, sigma, vertical);
            return new GeneralPath(lba, lbs, lsph, lba + Math.Max(lsph - lbs, 0), htsP, hrsP);
        }

        public static PathLossResult Profile(double fHz, TerrainProfile profile, double txHeightM, double rxHeightM,
                                             double earthRadiusFactor, double epsR, double sigma, bool vertical)
        {
            PropagationMath.Range(M, "the frequency (general path)", fHz / 1e6, 30, double.PositiveInfinity, "MHz");
            PropagationMath.Positive(M, "the transmit height above ground", txHeightM, "m");
            PropagationMath.Positive(M, "the receive height above ground", rxHeightM, "m");
            PropagationMath.Positive(M, "the effective Earth-radius factor", earthRadiusFactor, "");
            var p = GeneralPathLoss(fHz, profile, txHeightM, rxHeightM, earthRadiusFactor, epsR, sigma, vertical);
            double dts = Math.Sqrt(Math.Pow(profile.LengthKm * 1000, 2) + Math.Pow(profile.SurfaceM[0] + txHeightM - profile.SurfaceM[^1] - rxHeightM, 2));
            double fs = PropagationMath.FreeSpaceDb(fHz, dts);
            return new PathLossResult(M, Document, fs + p.L,
                new[] { new LossTerm("free space", fs), new LossTerm("diffraction over the profile", p.L) }, 0,
                Notes: new[] { $"Bullington loss {PropagationMath.F(p.Lba)} dB over the profile; smooth Earth {PropagationMath.F(p.Lsph)} dB against {PropagationMath.F(p.Lbs)} dB." });
        }
    }
}
