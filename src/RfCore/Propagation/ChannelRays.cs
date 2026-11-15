// ================================================================
//  ChannelRays.cs  —  the deterministic rays between two antenna ends (brief PCH-6)
//
//  The Propagation Channel's eq. (1) for pin ends is a sum over rays:
//
//    H(f, t) = Σ_r A_r · e^(−j2πf·τ_r) · e^(j2πν_r(t)·t)
//
//  with τ_r a ray's EXCESS delay over the line of sight (the bulk delay
//  τ0 = d/c is reported, never simulated), ν_r its Doppler and A_r its
//  complex gain before the large-scale loss and the antennas. This file
//  builds that list from geometry, with no draw anywhere:
//
//    the line of sight          A = 1, τ = 0
//    tworay's ground ray        A = Γ(ψ)·r1/r2, τ = (r2 − r1)/c, ψ the
//                               grazing angle, Γ the Fresnel coefficient
//                               TwoRayModel uses, so |H(f0)| is that
//                               model's own loss to round-off
//    a ray list file            each row's delay, power and phase as
//                               written, its Doppler when the file has
//                               a doppler column
//
//  Frames. The global frame has z up. With Distance and heights the
//  transmit end is at (0, 0, TxHeight) and the receive end at
//  (Distance, 0, RxHeight); TxPos and RxPos state both instead. An end's
//  antenna frame has its pattern's +z along its boresight (azimuth from
//  +x towards +y, elevation above the horizontal), its +x horizontal, to
//  the left of the boresight seen from behind, and its +y completing a
//  right-handed set (up, at zero elevation), then rolled about the
//  boresight. An end whose orientation is not stated points its
//  boresight at the other end.
//
//  Doppler. A ray's path length changes at dℓ/dt and its Doppler is
//  ν = −(f/c)·dℓ/dt: the line of sight from the two velocities along it,
//  the ground ray from the receive end against the transmit end's image
//  in the ground. A satellite path's Doppler and its drift are the
//  geometry's own (SatelliteGeometry), common to every ray.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace RfCore.Propagation
{
    /// <summary>A real 3-vector.</summary>
    public readonly record struct Vec3(double X, double Y, double Z)
    {
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(double s, Vec3 a) => new(s * a.X, s * a.Y, s * a.Z);
        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(Dot(this));
        public Vec3 Unit => (1 / Length) * this;
        public Vec3 Image => new(X, Y, -Z);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:G6}, {1:G6}, {2:G6})", X, Y, Z);
    }

    /// <summary>An antenna end's frame: its boresight (+z), +x and +y in the global frame.</summary>
    public sealed record AntennaFrame(Vec3 Xl, Vec3 Yl, Vec3 Zl)
    {
        /// <summary>The frame whose boresight points at azimuth/elevation (degrees), rolled by <paramref name="rollDeg"/>.</summary>
        public static AntennaFrame Of(double azDeg, double elDeg, double rollDeg)
        {
            double az = azDeg * Math.PI / 180, el = elDeg * Math.PI / 180, roll = rollDeg * Math.PI / 180;
            var z = new Vec3(Math.Cos(el) * Math.Cos(az), Math.Cos(el) * Math.Sin(az), Math.Sin(el));
            var x0 = new Vec3(-Math.Sin(az), Math.Cos(az), 0);
            var y0 = z.Cross(x0);
            var x = Math.Cos(roll) * x0 + Math.Sin(roll) * y0;
            var y = -Math.Sin(roll) * x0 + Math.Cos(roll) * y0;
            return new AntennaFrame(x, y, z);
        }

        /// <summary>The frame pointing along the global unit vector <paramref name="u"/>, rolled by <paramref name="rollDeg"/>.</summary>
        public static AntennaFrame Toward(Vec3 u, double rollDeg)
        {
            var d = u.Unit;
            return Of(Math.Atan2(d.Y, d.X) * 180 / Math.PI, Math.Asin(Math.Clamp(d.Z, -1, 1)) * 180 / Math.PI, rollDeg);
        }

        /// <summary>The direction (θ, φ) in radians, in this frame, of the global unit vector <paramref name="u"/>.</summary>
        public (double Theta, double Phi) Angles(Vec3 u)
        {
            double x = u.Dot(Xl), y = u.Dot(Yl), z = u.Dot(Zl);
            return (Math.Acos(Math.Clamp(z, -1, 1)), Math.Atan2(y, x));
        }

        /// <summary>A field given as (Eθ, Eφ) in this frame at (θ, φ), as a global complex vector (x, y, z).</summary>
        public (Complex X, Complex Y, Complex Z) ToGlobal(double theta, double phi, Complex eTheta, Complex ePhi)
        {
            double ct = Math.Cos(theta), st = Math.Sin(theta), cp = Math.Cos(phi), sp = Math.Sin(phi);
            // local Cartesian components
            Complex lx = eTheta * ct * cp - ePhi * sp;
            Complex ly = eTheta * ct * sp + ePhi * cp;
            Complex lz = -eTheta * st;
            return (lx * Xl.X + ly * Yl.X + lz * Zl.X,
                    lx * Xl.Y + ly * Yl.Y + lz * Zl.Y,
                    lx * Xl.Z + ly * Yl.Z + lz * Zl.Z);
        }
    }

    /// <summary>
    /// One ray of a channel: its complex gain before the large-scale loss and the antennas, its excess delay over the line
    /// of sight (s), its Doppler at the reference frequency (Hz), and its departure and arrival directions as global unit
    /// vectors (the arrival direction points from the receive end back along the ray, towards where it came from).
    /// </summary>
    public sealed record ChannelRay(string Name, Complex Gain, double ExcessDelayS, double DopplerHz, Vec3 Departure, Vec3 ArrivalFrom,
                                    PolarizationMatrix? Pol = null)
    {
        /// <summary>The ray with its ends exchanged: what the reverse link of the same realization carries (its polarization
        /// matrix transposed).</summary>
        public ChannelRay Transposed => this with { Departure = ArrivalFrom, ArrivalFrom = Departure, Pol = Pol?.Transposed };

        /// <summary>brief-pch-8 R-pch8-8 — the ray's 2×2 polarization matrix, the line of sight's when it states none.</summary>
        public PolarizationMatrix Polarization => Pol ?? PolarizationMatrix.LineOfSight;
    }

    /// <summary>
    /// brief-pch-8 R-pch8-8 — a ray's polarization: the field it delivers at the receive end, in the (θ̂, φ̂) of its arrival
    /// direction, from the field it left with, in the (θ̂, φ̂) of its departure direction, both bases the global frame's
    /// spherical ones (TR 38.901's convention). The line of sight is diag(1, −1): φ̂ of a direction and of its opposite are
    /// opposite, θ̂ the same, so the field vector itself is carried unchanged.
    /// </summary>
    public readonly record struct PolarizationMatrix(Complex Tt, Complex Tp, Complex Pt, Complex Pp)
    {
        public static readonly PolarizationMatrix LineOfSight = new(1, 0, 0, -1);

        public PolarizationMatrix Transposed => new(Tt, Pt, Tp, Pp);

        /// <summary>M·(θ, φ).</summary>
        public (Complex Theta, Complex Phi) Apply(Complex theta, Complex phi) => (Tt * theta + Tp * phi, Pt * theta + Pp * phi);

        /// <summary>M rotated by a Faraday angle Ω (radians) about the propagation direction: R(Ω)·M.</summary>
        public PolarizationMatrix Faraday(double omega)
        {
            double c = Math.Cos(omega), s = Math.Sin(omega);
            return new(c * Tt - s * Pt, c * Tp - s * Pp, s * Tt + c * Pt, s * Tp + c * Pp);
        }

        public PolarizationMatrix Conjugate() => new(Complex.Conjugate(Tt), Complex.Conjugate(Tp), Complex.Conjugate(Pt), Complex.Conjugate(Pp));
    }

    /// <summary>The global frame's spherical unit vectors at a direction (θ from +z, φ from +x towards +y).</summary>
    public static class GlobalBasis
    {
        public static (Vec3 Theta, Vec3 Phi) At(Vec3 u)
        {
            var d = u.Unit;
            double th = Math.Acos(Math.Clamp(d.Z, -1, 1)), ph = Math.Atan2(d.Y, d.X);
            double ct = Math.Cos(th), st = Math.Sin(th), cp = Math.Cos(ph), sp = Math.Sin(ph);
            return (new Vec3(ct * cp, ct * sp, -st), new Vec3(-sp, cp, 0));
        }

        /// <summary>A global complex vector's (θ̂, φ̂) components at direction <paramref name="u"/>.</summary>
        public static (Complex Theta, Complex Phi) Components((Complex X, Complex Y, Complex Z) e, Vec3 u)
        {
            var (t, p) = At(u);
            return (e.X * t.X + e.Y * t.Y + e.Z * t.Z, e.X * p.X + e.Y * p.Y + e.Z * p.Z);
        }

        /// <summary>The global complex vector of (θ̂, φ̂) components at <paramref name="u"/>.</summary>
        public static (Complex X, Complex Y, Complex Z) Vector(Complex theta, Complex phi, Vec3 u)
        {
            var (t, p) = At(u);
            return (theta * t.X + phi * p.X, theta * t.Y + phi * p.Y, theta * t.Z + phi * p.Z);
        }

        /// <summary>The unit direction at zenith <paramref name="zenithDeg"/> and azimuth <paramref name="azimuthDeg"/>.</summary>
        public static Vec3 Direction(double zenithDeg, double azimuthDeg)
        {
            double th = zenithDeg * Math.PI / 180, ph = azimuthDeg * Math.PI / 180;
            return new Vec3(Math.Sin(th) * Math.Cos(ph), Math.Sin(th) * Math.Sin(ph), Math.Cos(th));
        }
    }

    /// <summary>One row of a ray list or power-delay profile file: its delay, power and phase, its static Doppler, and (brief
    /// PCH-7) its Rician K (dB) and its Doppler spectrum when the file states them.</summary>
    public sealed record RayListRow(double DelayS, double PowerDb, double PhaseDeg, double? DopplerHz, double? KDb = null, string? Spectrum = null);

    /// <summary>
    /// A ray list or power-delay profile: a CSV whose header names each column with its unit in parentheses — <c>delay (ns)</c>,
    /// <c>power (dB)</c>, and optionally <c>phase (deg)</c>, <c>doppler (Hz)</c>, <c>k (dB)</c> (a Rician tap's K) and
    /// <c>spectrum</c> (classical, flat, gaussian or los: that tap fades with this Doppler spectrum). Delay takes s, ms, us or
    /// ns, Doppler Hz or kHz. A sampled impulse response writes <c>re</c> and <c>im</c> (the complex gain) in place of power
    /// and phase. Each row is one tap: its excess delay over the line of sight, its power, its phase, its static Doppler.
    /// Blank lines and lines starting with <c>#</c> are skipped.
    /// </summary>
    public static class RayListFile
    {
        public static IReadOnlyList<RayListRow> Read(string path)
        {
            if (!File.Exists(path)) throw new PropagationRefusal($"The ray list '{path}' does not exist.");
            var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').ToList();
            if (lines.Count < 2) throw new PropagationRefusal($"{Path.GetFileName(path)}: a ray list needs a header line and at least one ray.");
            var head = lines[0].Split(',').Select(h => h.Trim()).ToArray();
            int Col(string name) => Array.FindIndex(head, h => h.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            double Scale(int col, (string Unit, double Factor)[] units, string what)
            {
                string h = head[col];
                int a = h.IndexOf('('), b = h.IndexOf(')');
                if (a < 0 || b < a)
                    throw new PropagationRefusal($"{Path.GetFileName(path)}: the column '{h}' states no unit; write {what} ({string.Join(", ", units.Select(u => u.Unit))}).");
                string u = h[(a + 1)..b].Trim();
                foreach (var (unit, factor) in units) if (u == unit) return factor;
                throw new PropagationRefusal($"{Path.GetFileName(path)}: the column '{h}' is in {u}; {what} is in {string.Join(", ", units.Select(x => x.Unit))}.");
            }
            int Exact(string name) => Array.FindIndex(head, h => h.Split('(')[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            int cDelay = Col("delay"), cPower = Col("power"), cPhase = Col("phase"), cDoppler = Col("doppler");
            int cK = Exact("k"), cSpectrum = Col("spectrum"), cRe = Exact("re"), cIm = Exact("im");
            bool impulse = cRe >= 0 && cIm >= 0;
            if (cDelay < 0 || (cPower < 0 && !impulse))
                throw new PropagationRefusal($"{Path.GetFileName(path)}: a ray list's header names a delay and a power column, such as 'delay (ns), power (dB)', "
                                             + "or a delay and the complex gain as 're, im'.");
            if (impulse && (cPower >= 0 || cPhase >= 0))
                throw new PropagationRefusal($"{Path.GetFileName(path)}: a tap is a power and phase, or re and im, not both.");
            double sDelay = Scale(cDelay, [("s", 1), ("ms", 1e-3), ("us", 1e-6), ("µs", 1e-6), ("ns", 1e-9)], "delay");
            if (cPower >= 0) Scale(cPower, [("dB", 1)], "power");
            if (cPhase >= 0) Scale(cPhase, [("deg", 1)], "phase");
            if (cK >= 0) Scale(cK, [("dB", 1)], "k");
            double sDoppler = cDoppler >= 0 ? Scale(cDoppler, [("Hz", 1), ("kHz", 1e3)], "doppler") : 1;
            var rows = new List<RayListRow>();
            for (int i = 1; i < lines.Count; i++)
            {
                var cells = lines[i].Split(',').Select(c => c.Trim()).ToArray();
                double Num(int c, string what)
                {
                    if (c >= cells.Length || !double.TryParse(cells[c], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                        throw new PropagationRefusal($"{Path.GetFileName(path)}, row {i + 1}: the {what} is not a number.");
                    return v;
                }
                double delay = Num(cDelay, "delay") * sDelay;
                if (delay < 0) throw new PropagationRefusal($"{Path.GetFileName(path)}, row {i + 1}: a ray's excess delay must not be negative.");
                double power, phase;
                if (impulse)
                {
                    var g = new Complex(Num(cRe, "re"), Num(cIm, "im"));
                    if (g == Complex.Zero) throw new PropagationRefusal($"{Path.GetFileName(path)}, row {i + 1}: a tap's gain must not be zero.");
                    power = 20 * Math.Log10(g.Magnitude);
                    phase = g.Phase * 180 / Math.PI;
                }
                else { power = Num(cPower, "power"); phase = cPhase >= 0 ? Num(cPhase, "phase") : 0; }
                string? spectrum = null;
                if (cSpectrum >= 0 && cSpectrum < cells.Length && cells[cSpectrum].Length > 0)
                {
                    spectrum = cells[cSpectrum].Trim().ToLowerInvariant();
                    if (!TapFading.TryParseSpectrum(spectrum, out _))
                        throw new PropagationRefusal($"{Path.GetFileName(path)}, row {i + 1}: the spectrum '{cells[cSpectrum]}' is not one of {string.Join(", ", TapFading.DopplerWords)}.");
                }
                rows.Add(new RayListRow(delay, power, phase, cDoppler >= 0 ? Num(cDoppler, "doppler") * sDoppler : null,
                                        cK >= 0 ? Num(cK, "k") : null, spectrum));
            }
            return rows;
        }
    }

    /// <summary>
    /// The two ends' positions and velocities in the global frame, and the deterministic rays between them (the file
    /// header).
    /// </summary>
    public sealed record ChannelGeometry(Vec3 TxPos, Vec3 RxPos, Vec3 TxVel, Vec3 RxVel)
    {
        /// <summary>The line of sight, transmit end to receive end.</summary>
        public Vec3 Los => RxPos - TxPos;

        /// <summary>The 3D distance between the ends, m.</summary>
        public double Distance => Los.Length;

        /// <summary>The Doppler (Hz at <paramref name="hz"/>) of a path from <paramref name="from"/> moving at
        /// <paramref name="fromVel"/> to the receive end.</summary>
        private double Doppler(Vec3 from, Vec3 fromVel, double hz)
        {
            var u = (RxPos - from).Unit;
            return -hz / PropagationMath.C * (RxVel - fromVel).Dot(u) + 0.0;   // + 0.0: a still path's Doppler is 0, never −0
        }

        /// <summary>The line-of-sight ray.</summary>
        public ChannelRay LineOfSight(double hz)
        {
            var u = Los.Unit;
            return new ChannelRay("line of sight", Complex.One, 0, Doppler(TxPos, TxVel, hz), u, -1 * u);
        }

        /// <summary>The ray reflected by a flat ground at z = 0, with reflection coefficient <paramref name="gamma"/>.</summary>
        public ChannelRay GroundRay(double hz, Complex gamma)
        {
            if (!(TxPos.Z > 0 && RxPos.Z > 0))
                throw new PropagationRefusal("tworay: both ends must be above the ground (z > 0) for a ground-reflected ray.");
            var image = TxPos.Image;
            double r1 = Distance, r2 = (RxPos - image).Length;
            var departure = (RxPos.Image - TxPos).Unit;
            var arrival = (RxPos - image).Unit;
            return new ChannelRay("ground reflection", gamma * (r1 / r2), (r2 - r1) / PropagationMath.C,
                                  Doppler(image, TxVel.Image, hz), departure, -1 * arrival);
        }

        /// <summary>The grazing angle of the ground ray, rad.</summary>
        public double GrazingRad => Math.Atan2(TxPos.Z + RxPos.Z, Math.Sqrt(Math.Pow(RxPos.X - TxPos.X, 2) + Math.Pow(RxPos.Y - TxPos.Y, 2)));

        /// <summary>The rays of a ray list, each along the line of sight at both ends, its Doppler the row's or the line of
        /// sight's when the file has none.</summary>
        public IReadOnlyList<ChannelRay> FromList(IReadOnlyList<RayListRow> rows, double hz)
        {
            var los = LineOfSight(hz);
            return [.. rows.Select((r, i) => new ChannelRay($"ray {i + 1}",
                Complex.FromPolarCoordinates(Math.Pow(10, r.PowerDb / 20), r.PhaseDeg * Math.PI / 180),
                r.DelayS, r.DopplerHz ?? los.DopplerHz, los.Departure, los.ArrivalFrom))];
        }
    }

    /// <summary>The time statistics of a ray list a run's regime is judged by.</summary>
    public static class RayStatistics
    {
        /// <summary>The power-weighted RMS delay spread of <paramref name="rays"/> (s).</summary>
        public static double RmsDelaySpread(IReadOnlyList<ChannelRay> rays)
        {
            double p = 0, m1 = 0, m2 = 0;
            foreach (var r in rays)
            {
                double w = r.Gain.Magnitude * r.Gain.Magnitude;
                p += w; m1 += w * r.ExcessDelayS; m2 += w * r.ExcessDelayS * r.ExcessDelayS;
            }
            if (!(p > 0)) return 0;
            m1 /= p; m2 /= p;
            return Math.Sqrt(Math.Max(0, m2 - m1 * m1));
        }

        /// <summary>The coherence bandwidth at 50 % correlation, 1/(5·σ_τ); infinite for a single ray.</summary>
        public static double CoherenceBandwidthHz(IReadOnlyList<ChannelRay> rays)
        {
            double s = RmsDelaySpread(rays);
            return s > 0 ? 1 / (5 * s) : double.PositiveInfinity;
        }
    }
}
