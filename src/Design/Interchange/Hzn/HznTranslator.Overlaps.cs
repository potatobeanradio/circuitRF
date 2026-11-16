// brief-hzn-21 — overlapping solids, ordered so a .c3d states what Hertzon solves.
//
// Hertzon's rule, from its tutorial ("Overlapping objects") and feature matrix, never its source: an object completely inside another
// applies there, and with the design setting Material override (on by default) a metal overrides a dielectric or air whatever its
// size; any other partial overlap of two materials is an error Hertzon refuses to solve. A .c3d's rule is construction order: a later
// object wins where solids overlap. So an object that wins in Hertzon must come after the one it overlaps. Translated in the file's own
// order, a drawn air box listed after the arms it contains swallowed them (the half dipole of brief-hzn-21 read S11 0.995).
//
// Only what is out of order moves, and only between solids whose extents circuitRF knows; each move is a note.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznTranslator
{
    private void Overlaps()
    {
        bool metalOverrides = !string.Equals(_design.Json.GetObject("settings")?.GetString("MaterialOverride"), "False", StringComparison.OrdinalIgnoreCase);
        var list = _doc.Objects;
        bool Solid(C3dObject o) => C3dOperands.IsSolid(o) && o is not C3dStep;
        bool Metal(C3dObject o) => IsConductorMaterial(C3dValidation.EffectiveMaterial(o));
        // whether a must come after b
        bool Wins(C3dObject a, Box3 ab, C3dObject b, Box3 bb)
        {
            if (!Overlap(ab, bb)) return false;
            if (string.Equals(C3dValidation.EffectiveMaterial(a), C3dValidation.EffectiveMaterial(b), StringComparison.Ordinal)) return false;
            bool am = Metal(a), bm = Metal(b);
            if (metalOverrides && am != bm) return am;
            return Inside(ab, bb) && !Inside(bb, ab);
        }
        for (int pass = 0; pass < list.Count; pass++)
        {
            bool moved = false;
            for (int i = 0; i < list.Count && !moved; i++)
            {
                if (!Solid(list[i]) || Bounds(list[i]) is not { } ib) continue;
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (!Solid(list[j]) || Bounds(list[j]) is not { } jb || !Wins(list[i], ib, list[j], jb)) continue;
                    var later = list[j];
                    list.RemoveAt(j);
                    list.Insert(i, later);
                    _notes.Add($"'{later.Name}' is listed before '{list[i + 1].Name}': in Hertzon {(metalOverrides && Metal(list[i + 1]) != Metal(later) ? "a metal overrides a dielectric" : "an object inside another applies there")}, " +
                               "and in a .c3d the later of two overlapping objects wins.");
                    moved = true;
                    break;
                }
            }
            if (!moved) break;
        }
    }

    private static bool Overlap(Box3 a, Box3 b)
    {
        for (int k = 0; k < 3; k++)
            if (Math.Min(a.Max[k], b.Max[k]) <= Math.Max(a.Min[k], b.Min[k])) return false;
        return true;
    }

    /// <summary>Whether <paramref name="inner"/> lies within <paramref name="outer"/>.</summary>
    private static bool Inside(Box3 inner, Box3 outer)
    {
        for (int k = 0; k < 3; k++)
            if (inner.Min[k] < outer.Min[k] || inner.Max[k] > outer.Max[k]) return false;
        return true;
    }
}
