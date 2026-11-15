// ================================================================
//  TerrainProfile.cs  —  a terrain profile a user supplies, as CSV
//
//  The first non-comment line is a header naming the columns, each with
//  its unit in parentheses; a column with no unit is refused, since
//  "distance" alone could be metres or kilometres and both are plausible.
//
//    # comment lines start with '#'
//    distance (km), height (m), clutter (m), zone
//    0,     120,   0, inland
//    0.05,  122,  10, inland
//    …
//
//    distance  required; m or km. From the transmitter: the first point
//              is 0 (the transmitter), the last is the receiver, and the
//              distances strictly increase.
//    height    required; terrain height above sea level, m or ft.
//    clutter   optional; a representative clutter height added to the
//              terrain at the intermediate points (never at the
//              terminals), m or ft.
//    surface   optional; the surface height above sea level directly
//              (terrain plus clutter) at the intermediate points; not
//              together with clutter.
//    zone      optional; the radio-climatic zone of each point: sea,
//              coastal or inland (or B, A1, A2). Default inland.
//
//  At least three points: one intermediate point is the fewest any
//  profile method works with. Nothing is fetched or interpolated: the
//  profile is exactly the points given.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RfCore.Propagation
{
    /// <summary>A radio-climatic zone of a profile point (ITU-R P.1812 Table 3).</summary>
    public enum RadioZone { Inland, Coastal, Sea }

    public sealed class TerrainProfile
    {
        /// <summary>Distance of each point from the transmitter, km.</summary>
        public IReadOnlyList<double> DistanceKm { get; }

        /// <summary>Terrain height of each point above sea level, m.</summary>
        public IReadOnlyList<double> HeightM { get; }

        /// <summary>The surface height above sea level of each point, m: the terrain plus the clutter at the intermediate
        /// points, the terrain at the terminals.</summary>
        public IReadOnlyList<double> SurfaceM { get; }

        /// <summary>The representative clutter height at each point, m (0 where none was given).</summary>
        public IReadOnlyList<double> ClutterM { get; }

        public IReadOnlyList<RadioZone> Zones { get; }

        public int Count => DistanceKm.Count;
        public double LengthKm => DistanceKm[^1];

        public TerrainProfile(IReadOnlyList<double> distanceKm, IReadOnlyList<double> heightM,
                              IReadOnlyList<double>? clutterM = null, IReadOnlyList<double>? surfaceM = null,
                              IReadOnlyList<RadioZone>? zones = null, string source = "the profile")
        {
            int n = distanceKm.Count;
            if (n < 3) throw new PropagationRefusal($"{source}: a terrain profile needs at least 3 points (one between the terminals); it has {n}.");
            if (heightM.Count != n || clutterM is not null && clutterM.Count != n || surfaceM is not null && surfaceM.Count != n
                || zones is not null && zones.Count != n)
                throw new PropagationRefusal($"{source}: every column must have one value per point.");
            if (clutterM is not null && surfaceM is not null)
                throw new PropagationRefusal($"{source}: give clutter heights or surface heights, not both.");
            if (distanceKm[0] != 0)
                throw new PropagationRefusal($"{source}: the first point must be the transmitter, at distance 0; it is at {PropagationMath.F(distanceKm[0])} km.");
            for (int i = 1; i < n; i++)
                if (!(distanceKm[i] > distanceKm[i - 1]))
                    throw new PropagationRefusal(
                        $"{source}: distances must increase from the transmitter; point {i + 1} ({PropagationMath.F(distanceKm[i])} km) follows {PropagationMath.F(distanceKm[i - 1])} km.");
            if (clutterM is not null && clutterM.Any(c => c < 0))
                throw new PropagationRefusal($"{source}: a clutter height must not be negative.");

            DistanceKm = distanceKm.ToArray();
            HeightM = heightM.ToArray();
            ClutterM = clutterM?.ToArray() ?? new double[n];
            Zones = zones?.ToArray() ?? Enumerable.Repeat(RadioZone.Inland, n).ToArray();
            var g = new double[n];
            for (int i = 0; i < n; i++)
            {
                bool terminal = i == 0 || i == n - 1;
                g[i] = terminal ? heightM[i] : surfaceM is not null ? surfaceM[i] : heightM[i] + ClutterM[i];
            }
            SurfaceM = g;
        }

        /// <summary>Refuses unless the profile's length matches a stated path length (to 1 m).</summary>
        public void RequireLength(double distanceM, string model)
        {
            if (Math.Abs(LengthKm * 1000 - distanceM) > 1.0)
                throw new PropagationRefusal(
                    $"{model}: the profile must end at the receiver: it is {PropagationMath.F(LengthKm)} km long and the path is {PropagationMath.F(distanceM / 1000)} km.");
        }

        public static TerrainProfile Read(string path)
        {
            if (!File.Exists(path)) throw new PropagationRefusal($"The terrain profile '{path}' does not exist.");
            return Parse(File.ReadAllText(path), Path.GetFileName(path));
        }

        public static TerrainProfile Parse(string text, string source = "the profile")
        {
            var lines = text.Replace("\r", "").Split('\n')
                            .Select((l, i) => (Text: l.Trim(), Line: i + 1))
                            .Where(l => l.Text.Length > 0 && !l.Text.StartsWith('#'))
                            .ToList();
            if (lines.Count == 0) throw new PropagationRefusal($"{source}: the profile is empty.");

            var header = lines[0].Text.Split(',').Select(h => h.Trim()).ToArray();
            int iD = -1, iH = -1, iC = -1, iS = -1, iZ = -1;
            double sD = 1, sH = 1, sC = 1, sS = 1;
            for (int c = 0; c < header.Length; c++)
            {
                string h = header[c];
                int open = h.IndexOf('(');
                string name = (open < 0 ? h : h[..open]).Trim().ToLowerInvariant();
                string unit = open < 0 ? "" : h[(open + 1)..].TrimEnd(')').Trim().ToLowerInvariant();
                double Scale(params (string U, double S)[] allowed)
                {
                    if (unit.Length == 0)
                        throw new PropagationRefusal($"{source}: the column '{name}' needs its unit in parentheses ({string.Join(" or ", allowed.Select(a => a.U))}).");
                    foreach (var (u, s) in allowed) if (u == unit) return s;
                    throw new PropagationRefusal($"{source}: the column '{name}' has unit '{unit}'; it takes {string.Join(" or ", allowed.Select(a => a.U))}.");
                }
                switch (name)
                {
                    case "distance": iD = c; sD = Scale(("km", 1), ("m", 1e-3)); break;
                    case "height": iH = c; sH = Scale(("m", 1), ("ft", 0.3048)); break;
                    case "clutter": iC = c; sC = Scale(("m", 1), ("ft", 0.3048)); break;
                    case "surface": iS = c; sS = Scale(("m", 1), ("ft", 0.3048)); break;
                    case "zone": iZ = c; break;
                    default: throw new PropagationRefusal($"{source}: unknown column '{h}'; the columns are distance, height, clutter, surface and zone.");
                }
            }
            if (iD < 0 || iH < 0) throw new PropagationRefusal($"{source}: the header must name a distance and a height column.");

            var d = new List<double>(); var hgt = new List<double>(); var clt = new List<double>(); var srf = new List<double>(); var zones = new List<RadioZone>();
            foreach (var (row, line) in lines.Skip(1))
            {
                var cells = row.Split(',').Select(x => x.Trim()).ToArray();
                double Num(int col, double scale, string what)
                {
                    if (col >= cells.Length || !double.TryParse(cells[col], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        throw new PropagationRefusal($"{source}, line {line}: the {what} is not a number.");
                    return v * scale;
                }
                d.Add(Num(iD, sD, "distance"));
                hgt.Add(Num(iH, sH, "height"));
                if (iC >= 0) clt.Add(Num(iC, sC, "clutter height"));
                if (iS >= 0) srf.Add(Num(iS, sS, "surface height"));
                if (iZ >= 0)
                {
                    string z = iZ < cells.Length ? cells[iZ].ToLowerInvariant() : "";
                    zones.Add(z switch
                    {
                        "sea" or "b" => RadioZone.Sea,
                        "coastal" or "a1" => RadioZone.Coastal,
                        "inland" or "a2" => RadioZone.Inland,
                        _ => throw new PropagationRefusal($"{source}, line {line}: the zone '{z}' is not sea, coastal or inland."),
                    });
                }
            }
            return new TerrainProfile(d, hgt, iC >= 0 ? clt : null, iS >= 0 ? srf : null, iZ >= 0 ? zones : null, source);
        }
    }
}
