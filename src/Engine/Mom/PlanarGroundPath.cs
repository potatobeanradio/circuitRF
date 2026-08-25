// THE PORT GROWS ITS OWN PATH TO GROUND — the same rule R-fed-1's calibration feed follows.
//
// An internal port's two terminals are the metal and the ground plane, so SOMETHING has to carry
// its current down there: in this kernel only a vertical (via) basis does, and with no via under the
// port there is nothing for the excitation to drive. PlanarPorts refuses that by name, which is
// correct and is also a chore — what is missing is one cell of geometry, and a user who wants a port
// "inside the metal, referenced to ground" is not asking a different question just because they have
// not drawn the via yet.
//
// So the solver builds it, BEFORE meshing, and the three rules are the feed extension's own:
//
//   * A DRAWN VIA ALWAYS WINS. This only fills in where the artwork has none — it never widens,
//     moves or replaces one, and it never touches a conductor polygon at all.
//   * WHAT IT BUILT IS REPORTED, by port number and by size, because the path is real metal and its
//     inductance is in the answer. A via that appears in a result and nowhere in the drawing is
//     exactly the kind of thing a user must not have to discover.
//   * NOTHING IS INVENTED WITHOUT A SIZE. The width comes from the caller (the technology's own
//     default via size), and a port that names no width is left to the refusal instead — a headless
//     caller with no technology to ask does not get a via of some plausible-looking default.
//
// HOW BIG IS IT, AND WHY NOT A MESH CELL. The path is a SQUARE of the technology's own default via
// drill, centred on the port's label — 0.305 mm on the shipped PCB starter (a 12 mil drill), 60 µm
// on the MMIC one. A mesh cell is the obvious candidate and is the wrong one: this via's inductance
// is part of the port's answer, so sizing it from the mesh would make the answer a function of
// Cells per wavelength, and refining the mesh — the one thing a user does to CONVERGE a result —
// would move it for a reason that has nothing to do with convergence. A process dimension does not
// move, and it is the via that board would really have. (It typically MESHES to about one cell,
// because its own four edges are hard gridlines; that is a consequence, not the definition.)
//
// The problem reaches the mesher BY REFERENCE when nothing was grown (Assert.Same is the gate), so a
// board that draws its own vias is bit-identical to before this existed.

namespace CircuitRF.Engine.Mom;

/// <summary>One path this step created, for the report.</summary>
/// <param name="PortNumber">The port it was grown for.</param>
/// <param name="WidthM">The side of the square footprint.</param>
/// <param name="LayerIndex">The conductor level it attaches to.</param>
public readonly record struct PlanarGrownGroundPath(int PortNumber, double WidthM, int LayerIndex);

public static class PlanarGroundPath
{
    /// <summary>
    /// Adds a via to the ground plane under every <see cref="PlanarPortKind.Internal"/> port that
    /// has none and that names a width. Returns the same problem instance when nothing was grown.
    /// </summary>
    public static (PlanarProblem Problem, IReadOnlyList<PlanarGrownGroundPath> Grown, IReadOnlyList<string> Notes)
        Extend(PlanarProblem problem, IReadOnlyList<PlanarPort> ports)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(ports);

        List<PlanarVia>? added = null;
        var grown = new List<PlanarGrownGroundPath>();
        var notes = new List<string>();

        foreach (var port in ports)
        {
            if (port.Kind != PlanarPortKind.Internal) continue;
            if (port.GroundPathWidthM is not { } w || !(w > 0)) continue;
            if (CoveredByAGroundVia(problem, port.Location)) continue;

            // WHICH LEVEL the path attaches to. An explicit one is honoured as given; otherwise the
            // levels whose metal is actually under the point are the candidates, and more than one
            // is left to PlanarPorts' own ambiguity refusal rather than resolved by a guess here.
            int? level = port.LayerIndex ?? SoleLevelUnder(problem, port.Location);
            if (level is not { } li) continue;

            double h = 0.5 * w;
            var foot = new PlanarPolygon([
                new EmPoint(port.Location.X - h, port.Location.Y - h),
                new EmPoint(port.Location.X + h, port.Location.Y - h),
                new EmPoint(port.Location.X + h, port.Location.Y + h),
                new EmPoint(port.Location.X - h, port.Location.Y + h)]);

            added ??= [.. problem.ViaList];
            added.Add(new PlanarVia(PlanarVia.GroundTerminal, li, [foot],
                                    problem.Layers[li].SigmaSm));
            grown.Add(new PlanarGrownGroundPath(port.Number, w, li));
        }

        if (added is null) return (problem, grown, notes);

        foreach (var g in grown)
            notes.Add(
                $"Port {g.PortNumber} is an internal port and the artwork has no via to the " +
                $"ground plane under it, so one was built for it: a " +
                $"{SurfaceMesher.Eng(g.WidthM)}m square from '{problem.Layers[g.LayerIndex].Name}' " +
                "down to the plane, the size this technology's own vias are. It is REAL metal in " +
                "the solve and its inductance is part of what this port sees — a port to ground has " +
                "to get there somehow. Draw a via yourself where you want a different size or shape, " +
                "and it is used instead of this one.");

        return (problem with { Vias = added }, grown, notes);
    }

    private static bool CoveredByAGroundVia(PlanarProblem problem, EmPoint at)
    {
        foreach (var via in problem.ViaList)
        {
            if (!via.ToGround) continue;
            foreach (var poly in via.Polygons)
                if (poly.Contains(at.X, at.Y)) return true;
        }
        return false;
    }

    private static int? SoleLevelUnder(PlanarProblem problem, EmPoint at)
    {
        int found = -1;
        for (int li = 0; li < problem.Layers.Count; li++)
            foreach (var poly in problem.Layers[li].Polygons)
                if (poly.Contains(at.X, at.Y))
                {
                    if (found >= 0 && found != li) return null;   // ambiguous — PlanarPorts says so
                    found = li;
                    break;
                }

        return found >= 0 ? found : null;
    }
}
