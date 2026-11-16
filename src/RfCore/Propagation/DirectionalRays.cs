// ================================================================
//  DirectionalRays.cs  —  a user ray list with directions and polarization (brief PCH-8 R-pch8-6)
//
//  A ray tracer's output, or any list of rays, as a CSV whose header names each column with its unit:
//
//    delay (ns), power (dB), theta_d (deg), phi_d (deg), theta_a (deg), phi_a (deg)
//
//  and optionally phase (deg), and EITHER xpr (dB) — the ray's cross-polar ratio κ, its matrix then
//  [[e^jΦθθ, κ^-½ e^jΦθφ], [κ^-½ e^jΦφθ, e^jΦφφ]] with the four phases drawn per drop, TR 38.901's form —
//  OR the matrix itself as tt_re, tt_im, tp_re, tp_im, pt_re, pt_im, pp_re, pp_im (no unit). theta/phi_d
//  is the departure direction, theta/phi_a the arrival direction (pointing back along the ray, where it
//  came from), zenith from +z and azimuth from +x towards +y, in the frame RayFrame names: global, or
//  each end's own (departure in the transmit end's, arrival in the receive end's). A ray with neither
//  carries the line of sight's matrix. Blank lines and lines starting with # are skipped.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace RfCore.Propagation
{
    /// <summary>One row of a directional ray list.</summary>
    public sealed record DirectionalRayRow(double DelayS, double PowerDb, double PhaseDeg, double ThetaDDeg, double PhiDDeg, double ThetaADeg,
                                           double PhiADeg, double? XprDb, PolarizationMatrix? Matrix);

    public static class DirectionalRayFile
    {
        public static IReadOnlyList<DirectionalRayRow> Read(string path)
        {
            string name = Path.GetFileName(path);
            if (!File.Exists(path)) throw new PropagationRefusal($"The ray list '{path}' does not exist.");
            var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').ToList();
            if (lines.Count < 2) throw new PropagationRefusal($"{name}: a ray list needs a header line and at least one ray.");
            var head = lines[0].Split(',').Select(h => h.Trim()).ToArray();
            int Col(string key) => Array.FindIndex(head, h => h.Split('(')[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
            string Unit(int c) { string h = head[c]; int a = h.IndexOf('('), b = h.IndexOf(')'); return a >= 0 && b > a ? h[(a + 1)..b].Trim() : ""; }
            double Scale(int c, (string Unit, double F)[] units, string what)
            {
                string u = Unit(c);
                if (u.Length == 0)
                    throw new PropagationRefusal($"{name}: the column '{head[c]}' states no unit; write {what} ({string.Join(", ", units.Select(x => x.Unit))}).");
                foreach (var (unit, f) in units) if (u == unit) return f;
                throw new PropagationRefusal($"{name}: the column '{head[c]}' is in {u}; {what} is in {string.Join(", ", units.Select(x => x.Unit))}.");
            }
            int cDelay = Col("delay"), cPower = Col("power"), cPhase = Col("phase");
            int cTd = Col("theta_d"), cPd = Col("phi_d"), cTa = Col("theta_a"), cPa = Col("phi_a"), cXpr = Col("xpr");
            string[] mNames = ["tt_re", "tt_im", "tp_re", "tp_im", "pt_re", "pt_im", "pp_re", "pp_im"];
            int[] cM = [.. mNames.Select(Col)];
            if (cDelay < 0 || cPower < 0 || cTd < 0 || cPd < 0 || cTa < 0 || cPa < 0)
                throw new PropagationRefusal($"{name}: a directional ray list's header names delay, power, theta_d, phi_d, theta_a and phi_a, each with its unit, "
                                             + "such as 'delay (ns), power (dB), theta_d (deg), phi_d (deg), theta_a (deg), phi_a (deg)'.");
            bool matrix = cM.Any(c => c >= 0);
            if (matrix && cM.Any(c => c < 0))
                throw new PropagationRefusal($"{name}: a polarization matrix is all eight of {string.Join(", ", mNames)}.");
            if (matrix && cXpr >= 0) throw new PropagationRefusal($"{name}: a ray's polarization is an xpr or a matrix, not both.");
            double sDelay = Scale(cDelay, [("s", 1), ("ms", 1e-3), ("us", 1e-6), ("µs", 1e-6), ("ns", 1e-9)], "delay");
            Scale(cPower, [("dB", 1)], "power");
            foreach (int c in new[] { cTd, cPd, cTa, cPa, cPhase }.Where(c => c >= 0)) Scale(c, [("deg", 1)], head[c].Split('(')[0].Trim());
            if (cXpr >= 0) Scale(cXpr, [("dB", 1)], "xpr");
            var rows = new List<DirectionalRayRow>();
            for (int i = 1; i < lines.Count; i++)
            {
                var cells = lines[i].Split(',').Select(c => c.Trim()).ToArray();
                double Num(int c, string what)
                {
                    if (c >= cells.Length || !double.TryParse(cells[c], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                        throw new PropagationRefusal($"{name}, row {i + 1}: the {what} is not a number.");
                    return v;
                }
                double delay = Num(cDelay, "delay") * sDelay;
                if (delay < 0) throw new PropagationRefusal($"{name}, row {i + 1}: a ray's excess delay must not be negative.");
                PolarizationMatrix? m = null;
                if (matrix)
                {
                    var v = cM.Select((c, k) => Num(c, mNames[k])).ToArray();
                    m = new PolarizationMatrix(new Complex(v[0], v[1]), new Complex(v[2], v[3]), new Complex(v[4], v[5]), new Complex(v[6], v[7]));
                }
                rows.Add(new DirectionalRayRow(delay, Num(cPower, "power"), cPhase >= 0 ? Num(cPhase, "phase") : 0,
                    Num(cTd, "theta_d"), Num(cPd, "phi_d"), Num(cTa, "theta_a"), Num(cPa, "phi_a"), cXpr >= 0 ? Num(cXpr, "xpr") : null, m));
            }
            return rows;
        }
    }
}
