// ================================================================
//  ItuP1238.cs  —  ITU-R P.1238-13 (09/2025), indoor
//
//  The site-general model (§3.1, Table 2): both stations on one floor,
//    Lb(d, f) = 10·α·log10(d) + β + 10·γ·log10(f)      d in m, f in GHz
//  with a zero-mean Gaussian of standard deviation σ, by environment
//  (office, corridor, industrial, conference/lecture room) and line of
//  sight; each row's frequency and distance ranges are its validity.
//
//  Stations on different floors add the floor penetration loss factor
//  Lf(n) of §3.2 (Table 5), by building type and band, where the table
//  gives a value: office 0.9, 1.8-2, 2.4, 3.5, 5.2 and 5.8 GHz;
//  commercial 1.8-2 GHz; residential 1.8-2 GHz (4n), and elsewhere the
//  office value, as the recommendation says to use where it states none
//  for residential buildings. A band is matched within ±5 % of the
//  frequency the table names (1.8-2 GHz as a range). A floor count or a
//  band the table does not give is refused unless the floor loss is
//  stated.
// ================================================================

using System;
using System.Collections.Generic;

namespace RfCore.Propagation
{
    public enum IndoorEnvironment { Office, Corridor, Industrial, Conference }

    /// <summary>Table 5's columns; a residential building's own rows at 2.4 and 5.2 GHz are an apartment's (concrete walls)
    /// and a house's (wooden mortar).</summary>
    public enum FloorBuilding { Office, Commercial, Residential, Apartment, House }

    public static class ItuP1238
    {
        public const string Document = "ITU-R P.1238-13 (09/2025)";

        private sealed record Row(double FLo, double FHi, double DLo, double DHi, double Alpha, double Beta, double Gamma, double Sigma);

        private static Row Coefficients(IndoorEnvironment env, bool los) => (env, los) switch
        {
            (IndoorEnvironment.Office, true) => new(0.3, 294.0, 2, 27, 1.47, 34.17, 2.08, 3.68),
            (IndoorEnvironment.Office, false) => new(0.3, 255.0, 4, 30, 2.39, 30.13, 2.40, 5.01),
            (IndoorEnvironment.Corridor, true) => new(0.3, 300.0, 2, 160, 1.57, 29.46, 2.24, 3.77),
            (IndoorEnvironment.Corridor, false) => new(0.625, 159.0, 3, 94, 2.78, 28.62, 2.54, 7.58),
            (IndoorEnvironment.Industrial, true) => new(0.625, 294.0, 2, 102, 2.27, 24.79, 2.10, 2.62),
            (IndoorEnvironment.Industrial, false) => new(0.625, 255.0, 3, 110, 2.80, 23.55, 2.16, 5.70),
            (IndoorEnvironment.Conference, true) => new(0.45, 300.0, 2, 21, 1.56, 30.47, 2.23, 2.92),
            _ => new(0.45, 159.0, 4, 25, 1.40, 39.53, 2.37, 3.33),
        };

        /// <summary>The floor penetration loss factor Lf(n) of Table 5, or null where the table gives none.</summary>
        public static double? FloorLoss(FloorBuilding building, double fHz, int floors)
        {
            if (floors <= 0) return 0;
            double f = fHz / 1e9;
            bool Near(double g) => Math.Abs(f - g) <= 0.05 * g;
            bool band182 = f >= 1.8 * 0.95 && f <= 2.0 * 1.05;
            switch (building)
            {
                case FloorBuilding.Commercial:
                    return band182 ? 6 + 3 * (floors - 1) : null;
                case FloorBuilding.Residential or FloorBuilding.Apartment or FloorBuilding.House when band182:
                    return 4.0 * floors;
                case FloorBuilding.Apartment when Near(2.4) || Near(5.2):
                    return floors == 1 ? (Near(2.4) ? 10 : 13) : null;
                case FloorBuilding.House when Near(2.4) || Near(5.2):
                    return floors == 1 ? (Near(2.4) ? 5 : 7) : null;
                case FloorBuilding.Residential when Near(2.4) || Near(5.2):
                    return null;   // Table 5 gives an apartment's and a house's: Evaluate asks which
                default:   // office, and a residential building where Table 5 states none takes the office value, as it says to
                    if (band182) return 15 + 4 * (floors - 1);
                    if (Near(0.9)) return floors switch { 1 => 9, 2 => 19, 3 => 24, _ => null };
                    if (Near(2.4)) return floors == 1 ? 14 : null;
                    if (Near(3.5)) return floors switch { 1 => 18, 2 => 26, _ => null };
                    if (Near(5.2)) return floors == 1 ? 16 : null;
                    if (Near(5.8)) return floors switch { 1 => 22, 2 => 28, _ => null };
                    return null;
            }
        }

        /// <param name="distance3DM">The 3D distance between the stations, m.</param>
        /// <param name="floors">Floors between the stations (0: the same floor).</param>
        /// <param name="floorLossDb">A stated floor loss; NaN takes Table 5's.</param>
        public static PathLossResult Evaluate(IndoorEnvironment env, bool los, double fHz, double distance3DM,
                                              int floors = 0, FloorBuilding building = FloorBuilding.Office, double floorLossDb = double.NaN)
        {
            const string m = "P1238";
            var r = Coefficients(env, los);
            string row = $"{env.ToString().ToLowerInvariant()} {(los ? "line of sight" : "non-line-of-sight")}";
            PropagationMath.Range(m, $"the frequency ({row})", fHz / 1e9, r.FLo, r.FHi, "GHz");
            PropagationMath.Range(m, $"the 3D distance ({row})", distance3DM, r.DLo, r.DHi, "m");
            if (floors < 0) throw new PropagationRefusal($"{m}: the floor count must not be negative; it is {floors}.");

            double lb = 10 * r.Alpha * Math.Log10(distance3DM) + r.Beta + 10 * r.Gamma * Math.Log10(fHz / 1e9);
            double fs = PropagationMath.FreeSpaceDb(fHz, distance3DM);
            var terms = new List<LossTerm> { new("free space", fs), new($"P.1238 {row} excess", lb - fs) };
            var notes = new List<string>();
            if (floors > 0)
            {
                double lf;
                if (!double.IsNaN(floorLossDb)) lf = floorLossDb;
                else
                {
                    bool ownRow = Math.Abs(fHz / 2.4e9 - 1) <= 0.05 || Math.Abs(fHz / 5.2e9 - 1) <= 0.05;
                    if (building == FloorBuilding.Residential && ownRow)
                        throw new PropagationRefusal(
                            $"{m}: at {PropagationMath.Ghz(fHz)} Table 5 gives a residential floor loss for an apartment (concrete walls) and for a house (wooden mortar): state Building=apartment or Building=house, or FloorLoss.");
                    lf = FloorLoss(building, fHz, floors)
                         ?? throw new PropagationRefusal(
                             $"{m}: Table 5 gives no floor penetration loss for {floors} floor(s) of a {building.ToString().ToLowerInvariant()} building at {PropagationMath.Ghz(fHz)}; state FloorLoss.");
                    if (building is FloorBuilding.Residential or FloorBuilding.Apartment or FloorBuilding.House
                        && !(fHz >= 1.71e9 && fHz <= 2.1e9) && !ownRow)
                        notes.Add("Residential floor loss where Table 5 states none (only 1.8-2, 2.4 and 5.2 GHz) takes the office value, as P.1238 says to.");
                }
                terms.Add(new($"floor penetration ({floors} floor{(floors == 1 ? "" : "s")})", lf));
                lb += lf;
                notes.Add("The site-general model is for one floor; the floor penetration factor of the site-specific model is added for the floors between.");
            }
            return new PathLossResult(m, Document, lb, terms, r.Sigma, Notes: notes);
        }
    }
}
