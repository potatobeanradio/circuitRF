// ================================================================
//  ItuP1812.cs  —  ITU-R P.1812-8 (09/2025), point-to-area over a
//  terrain profile, 30 MHz to 6 GHz
//
//  The whole of the recommendation's §4 over a profile the user supplies:
//  line of sight with its short-term multipath and focusing corrections,
//  the delta-Bullington diffraction model at the median and at the
//  β0 % effective Earth radius, tropospheric scatter, ducting/layer
//  reflection, their blending into the loss not exceeded for p % of the
//  time and 50 % of locations, and the location variability for pL %
//  (outdoors). Attachment 1's profile analysis gives the horizon angles
//  and distances, the angular distance, the smooth-Earth heights and the
//  terrain roughness; Attachment 2's approximation is the inverse
//  complementary normal distribution I(x) the recommendation prescribes.
//
//  What the recommendation reads from its digital maps is a parameter
//  here, with a stated default: the refractivity lapse rate ΔN
//  (N-units/km) and the sea-level surface refractivity N0 (N-units), and
//  the path-centre latitude the ducting incidence depends on. Nothing
//  reads a map (docs/design/propagation-channel.md D10).
//
//  The profile's terrain heights hi (bare earth) enter the profile
//  analysis and the smooth-Earth fit; its surface heights gi (terrain
//  plus representative clutter at the intermediate points) enter the
//  Bullington construction of the actual path (§3.2, §4.3.1).
//
//  Not here: building entry (an added loss, ITU-R P.2109) and the field
//  strength for 1 kW e.r.p. (eq (70); the basic transmission loss is
//  what a link needs). The wavelength uses c = 2.998e8 m/s, the value the
//  recommendation's 92.4 dB free-space constant (eq (8)) implies.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace RfCore.Propagation
{
    /// <summary>What P.1812 takes beyond the profile.</summary>
    /// <param name="TxHeightM">htg: transmit antenna height above ground, m.</param>
    /// <param name="RxHeightM">hrg: receive antenna height above ground, m.</param>
    /// <param name="DeltaN">ΔN: the average refractivity lapse rate through the lowest 1 km, N-units/km (positive).</param>
    /// <param name="N0">The sea-level surface refractivity, N-units.</param>
    /// <param name="LatitudeDeg">The path centre's latitude, degrees.</param>
    /// <param name="DctKm">The transmitter's distance from the coast toward the receiver, km (for paths over sea).</param>
    /// <param name="DcrKm">The receiver's distance from the coast toward the transmitter, km.</param>
    /// <param name="SigmaLDb">The location variability's standard deviation, dB; NaN takes eq (64) with
    /// <paramref name="ResolutionM"/>.</param>
    /// <param name="ResolutionM">The prediction resolution wa (m) for eq (64).</param>
    public sealed record P1812Inputs(double FrequencyHz, double TimePercent, double LocationPercent, double TxHeightM, double RxHeightM,
                                     bool Vertical = false, double DeltaN = 45, double N0 = 325, double LatitudeDeg = 45,
                                     double DctKm = 500, double DcrKm = 500, double SigmaLDb = double.NaN, double ResolutionM = 100);

    /// <summary>The final and intermediate values of a P.1812 prediction, named as the recommendation names them.</summary>
    public sealed record P1812Result
    {
        public double D, Dlt, Dlr, ThetaT, ThetaR, Theta, Hts, Hrs, Omega, Dtm, Dlm, Beta0, Ae;
        public double Hst, Hsr, Hstd, Hsrd, HtcP, HrcP, Hte, Hre, Hm;
        public double Fi, Fj, Fk, Lbfs, Lb0p, Lb0b, Lbulla, Lbulls, Ldsph, Ld50, Ldb, Ldp, Lbd50, Lbd;
        public double Lminb0p, Lba, Lminbap, Lbda, Lbam, Lbs, Lbc, SigmaLoc, Lb, Ep;
        public bool TransHorizon;
    }

    public static class ItuP1812
    {
        public const string Document = "ITU-R P.1812-8 (09/2025)";
        private const string M = "P1812";
        private const double A = 6371;          // average Earth radius, km
        private const double Cl = 2.998e8;      // speed of light for λ

        /// <summary>Attachment 2: the inverse complementary cumulative normal distribution I(x), its approximation,
        /// x clamped to [1e-6, 1 − 1e-6].</summary>
        public static double I(double x)
        {
            x = Math.Clamp(x, 1e-6, 1 - 1e-6);
            static double Core(double y)
            {
                double t = Math.Sqrt(-2 * Math.Log(y));
                double xi = ((0.010328 * t + 0.802853) * t + 2.515516698) / (((0.001308 * t + 0.189269) * t + 1.432788) * t + 1);
                return t - xi;
            }
            return x <= 0.5 ? Core(x) : -Core(1 - x);
        }

        /// <summary>Lengths of the path over sea and the longest continuous land and inland sections, km: a zone change
        /// between two points is taken midway between them (§3.3).</summary>
        public static (double SeaKm, double Dtm, double Dlm) Zones(TerrainProfile p)
        {
            int n = p.Count;
            var d = p.DistanceKm;
            double sea = 0, land = 0, inland = 0, dtm = 0, dlm = 0;
            for (int i = 0; i < n; i++)
            {
                double lo = i == 0 ? 0 : (d[i - 1] + d[i]) / 2, hi = i == n - 1 ? d[i] : (d[i] + d[i + 1]) / 2, len = hi - lo;
                var z = p.Zones[i];
                if (z == RadioZone.Sea) { sea += len; land = 0; inland = 0; }
                else
                {
                    land += len; dtm = Math.Max(dtm, land);
                    if (z == RadioZone.Inland) { inland += len; dlm = Math.Max(dlm, inland); } else inland = 0;
                }
            }
            return (sea, dtm, dlm);
        }

        /// <summary>The first-term spherical-Earth diffraction loss for radius adft (km), §4.3.3: land and sea weighted
        /// by the fraction of the path over sea.</summary>
        private static double Ldft(double fGhz, double dKm, double htesph, double hresph, double adft, double omega, bool vertical)
        {
            double One(double epsR, double sigma)
            {
                double x18 = 18 * sigma / fGhz;
                double kh = 0.036 * Math.Pow(adft * fGhz, -1.0 / 3) * Math.Pow((epsR - 1) * (epsR - 1) + x18 * x18, -0.25);
                double k = vertical ? kh * Math.Sqrt(epsR * epsR + x18 * x18) : kh;
                double k2 = k * k, k4 = k2 * k2;
                double beta = (1 + 1.6 * k2 + 0.67 * k4) / (1 + 4.5 * k2 + 1.53 * k4);
                double x = 21.88 * beta * Math.Cbrt(fGhz / (adft * adft)) * dKm;
                double yScale = 0.9575 * beta * Math.Cbrt(fGhz * fGhz / adft);
                double fx = x >= 1.6 ? 11 + 10 * Math.Log10(x) - 17.6 * x : -20 * Math.Log10(x) - 5.6488 * Math.Pow(x, 1.425);
                double G(double y)
                {
                    double b = beta * y;
                    double g = b > 2 ? 17.6 * Math.Sqrt(b - 1.1) - 5 * Math.Log10(b - 1.1) - 8 : 20 * Math.Log10(b + 0.1 * b * b * b);
                    return Math.Max(g, 2 + 20 * Math.Log10(k));
                }
                return -fx - G(yScale * htesph) - G(yScale * hresph);
            }
            return omega * One(80, 5) + (1 - omega) * One(22, 0.003);
        }

        /// <summary>§4.3.2: the spherical-Earth diffraction loss for effective heights (m) and radius ap (km).</summary>
        private static double Ldsph(double fGhz, double lambda, double d, double hte, double hre, double ap, double omega, bool vertical)
        {
            double dlos = Math.Sqrt(2 * ap) * (Math.Sqrt(0.001 * hte) + Math.Sqrt(0.001 * hre));
            if (d >= dlos) return Ldft(fGhz, d, hte, hre, ap, omega, vertical);
            double c = (hte - hre) / (hte + hre);
            double mc = 250 * d * d / (ap * (hte + hre));
            double b = 2 * Math.Sqrt((mc + 1) / (3 * mc)) * Math.Cos(Math.PI / 3 + Math.Acos(3 * c / 2 * Math.Sqrt(3 * mc / Math.Pow(mc + 1, 3))) / 3);
            double dse1 = d / 2 * (1 + b), dse2 = d - dse1;
            double hse = ((hte - 500 * dse1 * dse1 / ap) * dse2 + (hre - 500 * dse2 * dse2 / ap) * dse1) / d;
            double hreq = 17.456 * Math.Sqrt(dse1 * dse2 * lambda / d);
            if (hse > hreq) return 0;
            double aem = 500 * Math.Pow(d / (Math.Sqrt(hte) + Math.Sqrt(hre)), 2);
            double ldft = Ldft(fGhz, d, hte, hre, aem, omega, vertical);
            if (ldft < 0) return 0;
            return (1 - hse / hreq) * ldft;
        }

        /// <summary>The whole prediction, every intermediate value kept.</summary>
        public static P1812Result Predict(TerrainProfile profile, P1812Inputs inp)
        {
            double f = inp.FrequencyHz / 1e9, p = inp.TimePercent, pL = inp.LocationPercent;
            PropagationMath.Range(M, "the frequency", f, 0.03, 6, "GHz");
            PropagationMath.Range(M, "the time percentage p", p, 1, 50, "%");
            PropagationMath.Range(M, "the location percentage pL", pL, 1, 99, "%");
            PropagationMath.Range(M, "the transmit height above ground", inp.TxHeightM, 1, 3000, "m");
            PropagationMath.Range(M, "the receive height above ground", inp.RxHeightM, 1, 3000, "m");
            PropagationMath.Range(M, "the path-centre latitude", inp.LatitudeDeg, -80, 80, "deg");
            PropagationMath.Range(M, "the path length", profile.LengthKm, 0.25, 3000, "km");
            if (!(inp.DeltaN >= 0 && inp.DeltaN < 157))
                throw new PropagationRefusal($"{M}: the refractivity lapse rate ΔN must be between 0 and 157 N-units/km; it is {PropagationMath.F(inp.DeltaN)}.");

            var r = new P1812Result();
            int n = profile.Count;
            var di = profile.DistanceKm; var hi = profile.HeightM; var gi = profile.SurfaceM;
            double d = profile.LengthKm, lambda = Cl / inp.FrequencyHz;
            double hts = hi[0] + inp.TxHeightM, hrs = hi[n - 1] + inp.RxHeightM;
            double ae = A * 157 / (157 - inp.DeltaN), ab = 3 * A;
            r.D = d; r.Hts = hts; r.Hrs = hrs; r.Ae = ae;

            // ---- zones, ducting incidence (§3.3, §3.6)
            var (sea, dtm, dlm) = Zones(profile);
            double omega = sea / d;
            r.Omega = omega; r.Dtm = dtm; r.Dlm = dlm;
            double tau = 1 - Math.Exp(-0.000412 * Math.Pow(dlm, 2.41));
            double mu1 = Math.Pow(Math.Pow(10, -dtm / (16 - 6.6 * tau)) + Math.Pow(10, -5 * (0.496 + 0.354 * tau)), 0.2);
            mu1 = Math.Min(mu1, 1);
            double phi = Math.Abs(inp.LatitudeDeg);
            double mu4 = phi <= 70 ? Math.Pow(mu1, -0.935 + 0.0176 * phi) : Math.Pow(mu1, 0.3);
            double beta0 = phi <= 70 ? Math.Pow(10, -0.015 * phi + 1.67) * mu1 * mu4 : 4.17 * mu1 * mu4;
            r.Beta0 = beta0;

            // ---- profile analysis (Attachment 1, §§4-5)
            double ce = 1 / ae;
            double thetaMax = double.NegativeInfinity; int iMax = 1;
            for (int i = 1; i < n - 1; i++)
            {
                double th = 1000 * Math.Atan((hi[i] - hts) / (1000 * di[i]) - di[i] / (2 * ae));
                if (th > thetaMax) { thetaMax = th; iMax = i; }
            }
            double thetaTd = 1000 * Math.Atan((hrs - hts) / (1000 * d) - d / (2 * ae));
            bool trans = thetaMax > thetaTd;
            r.TransHorizon = trans;
            double thetaT = Math.Max(thetaMax, thetaTd), thetaR, dlt, dlr;
            int ilt, ilr;
            if (trans)
            {
                dlt = di[iMax]; ilt = iMax;
                double thR = double.NegativeInfinity; int jMax = n - 2;
                for (int j = 1; j < n - 1; j++)
                {
                    double th = 1000 * Math.Atan((hi[j] - hrs) / (1000 * (d - di[j])) - (d - di[j]) / (2 * ae));
                    if (th >= thR) { thR = th; jMax = j; }   // the latest index of the maximum: the nearest to the receiver
                }
                thetaR = thR; dlr = d - di[jMax]; ilr = jMax;
            }
            else
            {
                double numax = double.NegativeInfinity; int iNu = 1;
                for (int i = 1; i < n - 1; i++)
                {
                    double nu = (hi[i] + 500 * ce * di[i] * (d - di[i]) - (hts * (d - di[i]) + hrs * di[i]) / d)
                                * Math.Sqrt(0.002 * d / (lambda * di[i] * (d - di[i])));
                    if (nu > numax) { numax = nu; iNu = i; }
                }
                dlt = di[iNu]; ilt = iNu;
                thetaR = 1000 * Math.Atan((hts - hrs) / (1000 * d) - d / (2 * ae));
                dlr = d - dlt; ilr = iNu;
            }
            double theta = 1000 * d / ae + thetaT + thetaR;
            r.Dlt = dlt; r.Dlr = dlr; r.ThetaT = thetaT; r.ThetaR = thetaR; r.Theta = theta;

            var sm = Diffraction.SmoothSurface(di, hi, hts, hrs);
            r.Hst = sm.Hst; r.Hsr = sm.Hsr; r.Hstd = sm.Hstd; r.Hsrd = sm.Hsrd;
            double hstR = Math.Min(sm.Hst, hi[0]), hsrR = Math.Min(sm.Hsr, hi[n - 1]);
            double slope = (hsrR - hstR) / d;
            double hte = inp.TxHeightM + hi[0] - hstR, hre = inp.RxHeightM + hi[n - 1] - hsrR;
            double hm = double.NegativeInfinity;
            for (int i = Math.Min(ilt, ilr); i <= Math.Max(ilt, ilr); i++) hm = Math.Max(hm, hi[i] - (hstR + slope * di[i]));
            r.Hte = hte; r.Hre = hre; r.Hm = hm;

            // ---- line of sight (§4.2)
            double dfs = Math.Sqrt(d * d + Math.Pow((hts - hrs) / 1000, 2));
            double lbfs = 92.4 + 20 * Math.Log10(f) + 20 * Math.Log10(dfs);
            double esp = 2.6 * (1 - Math.Exp(-(dlt + dlr) / 10)) * Math.Log10(p / 50);
            double esb = 2.6 * (1 - Math.Exp(-(dlt + dlr) / 10)) * Math.Log10(beta0 / 50);
            double lb0p = lbfs + esp, lb0b = lbfs + esb;
            r.Lbfs = lbfs; r.Lb0p = lb0p; r.Lb0b = lb0b;

            // ---- diffraction (§4.3)
            double htcP = hts - sm.Hstd, hrcP = hrs - sm.Hsrd;
            r.HtcP = htcP; r.HrcP = hrcP;
            double Ld(double ap, out double lbulla, out double lbulls, out double ldsph)
            {
                lbulla = Diffraction.Bullington(di, gi, hts, hrs, ap, lambda);
                lbulls = Diffraction.Bullington(di, new double[n], htcP, hrcP, ap, lambda);
                ldsph = Ldsph(f, lambda, d, htcP, hrcP, ap, omega, inp.Vertical);
                return lbulla + Math.Max(ldsph - lbulls, 0);
            }
            double ld50 = Ld(ae, out double la, out double ls, out double lsph);
            r.Lbulla = la; r.Lbulls = ls; r.Ldsph = lsph; r.Ld50 = ld50;
            double ldb = Ld(ab, out _, out _, out _);   // reported at every p; used only below 50 %
            r.Ldb = ldb;
            double fi = p >= 50 ? 0 : p <= beta0 ? 1 : I(p / 100) / I(beta0 / 100);
            double ldp = ld50 + (ldb - ld50) * fi;
            double lbd50 = lbfs + ld50, lbd = lb0p + ldp;
            r.Fi = fi; r.Ldp = ldp; r.Lbd50 = lbd50; r.Lbd = lbd;

            // ---- tropospheric scatter (§4.4)
            double lf = 25 * Math.Log10(f) - 2.5 * Math.Pow(Math.Log10(f / 2), 2);
            double lbs = 190.1 + lf + 20 * Math.Log10(d) + 0.573 * theta - 0.15 * inp.N0 - 10.125 * Math.Pow(Math.Log10(50 / p), 0.7);
            r.Lbs = lbs;

            // ---- ducting / layer reflection (§4.5)
            double alf = f < 0.5 ? 45.375 - 137.0 * f + 92.5 * f * f : 0;
            double As(double th, double dl)
            {
                double t2 = th - 0.1 * dl;
                return t2 > 0 ? 20 * Math.Log10(1 + 0.361 * t2 * Math.Sqrt(f * dl)) + 0.264 * t2 * Math.Cbrt(f) : 0;
            }
            double Ac(double dc, double dl, double hs)
                => omega >= 0.75 && dc <= dl && dc <= 5 ? -3 * Math.Exp(-0.25 * dc * dc) * (1 + Math.Tanh(0.07 * (50 - hs))) : 0;
            double af = 102.45 + 20 * Math.Log10(f) + 20 * Math.Log10(dlt + dlr) + alf + As(thetaT, dlt) + As(thetaR, dlr)
                        + Ac(inp.DctKm, dlt, hts) + Ac(inp.DcrKm, dlr, hrs);
            double gammaD = 5e-5 * ae * Math.Cbrt(f);
            double thtP = thetaT <= 0.1 * dlt ? thetaT : 0.1 * dlt;
            double thrP = thetaR <= 0.1 * dlr ? thetaR : 0.1 * dlr;
            double thetaP = 1000 * d / ae + thtP + thrP;
            double alpha = Math.Max(-0.6 - 3.5e-9 * Math.Pow(d, 3.1) * tau, -3.4);
            double mu2 = Math.Min(Math.Pow(500 / ae * d * d / Math.Pow(Math.Sqrt(hte) + Math.Sqrt(hre), 2), alpha), 1);
            double dI = Math.Min(d - dlt - dlr, 40);
            double mu3 = hm <= 10 ? 1 : Math.Exp(-4.6e-5 * (hm - 10) * (43 + 6 * dI));
            double beta = beta0 * mu2 * mu3;
            double lb = Math.Log10(beta);
            double gamma = 1.076 / Math.Pow(2.0058 - lb, 1.012) * Math.Exp(-(9.51 - 4.8 * lb + 0.198 * lb * lb) * 1e-6 * Math.Pow(d, 1.13));
            double ap_ = -12 + (1.2 + 3.7e-3 * d) * Math.Log10(p / beta) + 12 * Math.Pow(p / beta, gamma);
            double lba = af + gammaD * thetaP + ap_;
            r.Lba = lba;

            // ---- blending (§4.6)
            double fj = 1.0 - 0.5 * (1.0 + Math.Tanh(3.0 * 0.8 * (theta - 0.3) / 0.3));
            double fk = 1.0 - 0.5 * (1.0 + Math.Tanh(3.0 * 0.5 * (d - 20) / 20));
            double lminb0p = p < beta0 ? lb0p + (1 - omega) * ldp : lbd50 + (lb0b + (1 - omega) * ldp - lbd50) * fi;
            const double eta = 2.5;
            double lminbap = eta * Math.Log(Math.Exp(lba / eta) + Math.Exp(lb0p / eta));
            double lbda = lminbap > lbd ? lbd : lminbap + (lbd - lminbap) * fk;
            double lbam = lbda + (lminb0p - lbda) * fj;
            double lbc = -5 * Math.Log10(Math.Pow(10, -0.2 * lbs) + Math.Pow(10, -0.2 * lbam));
            r.Fj = fj; r.Fk = fk; r.Lminb0p = lminb0p; r.Lminbap = lminbap; r.Lbda = lbda; r.Lbam = lbam; r.Lbc = lbc;

            // ---- location variability, outdoors (§4.7, §4.9)
            double sigmaL = double.IsNaN(inp.SigmaLDb) ? (0.024 * f + 0.52) * Math.Pow(inp.ResolutionM, 0.28) : inp.SigmaLDb;
            double rClutter = profile.ClutterM[n - 2];
            double h = inp.RxHeightM;
            double u = h <= rClutter ? 1 : h >= rClutter + 10 ? 0 : 1 - (h - rClutter) / 10;
            double sigmaLoc = u * sigmaL;
            double lbFinal = Math.Max(lb0p, lbc - I(Math.Clamp(pL / 100, 0.01, 0.99)) * sigmaLoc);
            r.SigmaLoc = sigmaLoc; r.Lb = lbFinal;
            r.Ep = 199.36 + 20 * Math.Log10(f) - lbFinal;
            return r;
        }

        public static PathLossResult Evaluate(TerrainProfile profile, P1812Inputs inp)
        {
            var r = Predict(profile, inp);
            var notes = new List<string>
            {
                $"{(r.TransHorizon ? "Trans-horizon" : "Line-of-sight")} path; diffraction {PropagationMath.F(r.Ldp)} dB at {PropagationMath.F(inp.TimePercent)} % of the time, troposcatter {PropagationMath.F(r.Lbs)} dB, ducting {PropagationMath.F(r.Lba)} dB.",
                $"ΔN {PropagationMath.F(inp.DeltaN)} N-units/km (ae {PropagationMath.F(r.Ae)} km), N0 {PropagationMath.F(inp.N0)} N-units, path-centre latitude {PropagationMath.F(inp.LatitudeDeg)}°: P.1812's map values are parameters here.",
            };
            var terms = new List<LossTerm>
            {
                new("free space", r.Lbfs),
                new("P.1812 terrain, troposcatter and ducting", r.Lbc - r.Lbfs),
            };
            if (Math.Abs(r.Lb - r.Lbc) > 0) terms.Add(new($"P.1812 location variability at {PropagationMath.F(inp.LocationPercent)} %", r.Lb - r.Lbc));
            return new PathLossResult(M, Document, r.Lb, terms, r.SigmaLoc, Notes: notes);
        }
    }
}
