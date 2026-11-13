// brief-cxp-4 — a COAXIAL lumped port on openEMS's Cartesian grid. openEMS's LumpedElement and circuitRF's lumped port run along
// one axis, and a ring's field is radial, so the port is built from parts openEMS already has, as the coax wave port's terminals
// are (FdtdWavePorts): on the ring's own grid plane,
//
//   * a soft E-field source over the ring's square, its weights radial and 1/r about the centre and cut to zero outside the ring
//     (FdtdWavePorts.RadialWeights — the coax wave port's own expression);
//   * its termination: a conducting slab filling the ring's annulus over the dual cell of that plane, so every bit of its
//     conductance lands on the plane's own in-plane edges. A sheet of conductance κ·t between radii a and b is ln(b/a)/(2π·κ·t)
//     ohms, so κ = ln(b/a)/(2π·Z0·t) terminates the port in Z0. It stops a thousandth of the gap inside both circles, so it
//     never overlaps the pin or the shell (it takes the port's priority, above every solid);
//   * its voltage probe: a line along the plane's first axis from the negative circle to the positive one, with every lumped
//     probe's weight −1 (V = φ(+) − φ(−));
//   * its current probe: a square round the INNER conductor on the half-cell plane inward of the ring, its corners on dual
//     lines a cell clear of the pin, and refused unless it is also a cell clear of the shell — the wave port's rule (113-a
//     measured a probe touching the shield reading down to an open circuit). Its weight makes I flow into the device on the
//     positive conductor.
//
// A ring on a face of the box is refused: openEMS's wall (PEC, or the first cell of a PML or Mur boundary) is no place to put a
// source and a resistive sheet.

namespace CircuitRF.Engine.Em3d;

/// <summary>Everything a coaxial port writes on openEMS's grid, world metres; axes 0 x, 1 y, 2 z.</summary>
/// <param name="Normal">The axis the ring faces.</param>
/// <param name="PlaneM">The grid line the ring lies on.</param>
/// <param name="Source">The soft source's box and weights.</param>
/// <param name="ShellP1">The termination slab's axis start (centre, at the dual cell's lower side).</param>
/// <param name="ShellP2">Its axis end.</param>
/// <param name="ShellRadiusM">Its mid-radius (CSXCAD's CylindricalShell radius).</param>
/// <param name="ShellWidthM">Its radial width.</param>
/// <param name="KappaSm">Its conductivity, S/m.</param>
/// <param name="SlabM">The slab's thickness, the dual cell's: what κ was computed for.</param>
/// <param name="Voltage">The voltage probe's line.</param>
/// <param name="CurrentMin">The current probe's box, one corner.</param>
/// <param name="CurrentMax">The other corner.</param>
/// <param name="CurrentWeight">±1: the probe's sign, so I is into the device.</param>
/// <param name="ClearanceCells">How many cells the current probe's corners clear the shell by.</param>
/// <param name="SideProbe">A point a quarter of the slab inward of the ring, midway between the radii: where its dielectric is read.</param>
public sealed record FdtdRingPortElements(
    Em3dPort Port, int Normal, double PlaneM, FdtdSourceBox Source, Point3 ShellP1, Point3 ShellP2, double ShellRadiusM,
    double ShellWidthM, double KappaSm, double SlabM, FdtdVoltageProbe Voltage, Point3 CurrentMin, Point3 CurrentMax,
    int CurrentWeight, double ClearanceCells, Point3 SideProbe);

public static class FdtdRingPorts
{
    /// <summary>How far inside both circles, as a fraction of the gap, the termination slab stops.</summary>
    public const double ShellInset = 1e-3;

    /// <summary>The ring's elements on <paramref name="grid"/>, or why it cannot be written there.</summary>
    public static (FdtdRingPortElements? Ring, string? Refusal) Place(Em3dPort p, Em3dAnnulus ring, FdtdGridResult grid)
    {
        int n = Em3dAnnulus.NormalAxis(p.Min, p.Max);
        var (u, v) = Em3dAnnulus.PlaneAxes(n);
        var ln = grid.Axis((FdtdAxis)n).Lines;
        var lu = grid.Axis((FdtdAxis)u).Lines;
        var lv = grid.Axis((FdtdAxis)v).Lines;
        var c = Em3dAnnulus.Centre(p.Min, p.Max);
        double h = FdtdWavePorts.Snap(ln, Em3dAnnulus.Get(c, n));
        int k = Index(ln, h);
        string label = p.SourceLabel ?? $"Port {p.SourceNumber ?? p.Number}";
        if (k <= 0 || k >= ln.Count - 1)
            return (null, $"{label} is a coaxial port on a face of the air box. openEMS's wall forces the field along it to zero (and an " +
                          "absorbing face's first cells are its boundary), which would short the port. Move the ring one cell in, with " +
                          "nothing behind it but air or the coax's own continuation, or run the setup on Palace or EMerge.");
        double cu = FdtdWavePorts.Snap(lu, Em3dAnnulus.Get(c, u)), cv = FdtdWavePorts.Snap(lv, Em3dAnnulus.Get(c, v));
        double a = ring.InnerRadiusM, b = ring.OuterRadiusM;
        double below = h - ln[k - 1], above = ln[k + 1] - h, slab = (below + above) / 2;
        int inward = Math.Sign(Em3dAnnulus.Get(p.ReferencePlane.Normal, n)) is var s and not 0 ? s : 1;

        Point3 At(double an, double au, double av)
        {
            var q = new double[3];
            q[n] = an; q[u] = au; q[v] = av;
            return new Point3(q[0], q[1], q[2]);
        }

        // The source: E outward from the pin for a positive voltage, inward when the pin is the negative end.
        var weight = FdtdWavePorts.RadialWeights(n, u, v, cu, cv, a, b, inward: ring.Outward);
        var e = new int[3];
        e[u] = 1; e[v] = 1;
        var source = new FdtdSourceBox("", At(h, FdtdWavePorts.BelowLine(lu, cu - b), FdtdWavePorts.BelowLine(lv, cv - b)),
                                       At(h, FdtdWavePorts.AboveLine(lu, cu + b), FdtdWavePorts.AboveLine(lv, cv + b)), e, weight);

        // The termination slab over the plane's dual cell.
        double inset = ShellInset * (b - a);
        double kappa = Math.Log(b / a) / (2 * Math.PI * p.Z0.Real * slab);

        // The voltage line: negative circle to positive, along u, each end snapped to its line (inside the metal it reads 0).
        double rNeg = ring.Outward ? a : b, rPos = ring.Outward ? b : a;
        var voltage = new FdtdVoltageProbe("", At(h, FdtdWavePorts.Snap(lu, cu + rNeg), cv), At(h, FdtdWavePorts.Snap(lu, cu + rPos), cv));

        // The current probe: a square round the inner conductor, a cell clear of it, on the half-cell plane inward.
        double bu0 = FdtdWavePorts.DualOutLine(lu, cu - a, -1), bu1 = FdtdWavePorts.DualOutLine(lu, cu + a, +1);
        double bv0 = FdtdWavePorts.DualOutLine(lv, cv - a, -1), bv1 = FdtdWavePorts.DualOutLine(lv, cv + a, +1);
        double corner = Math.Sqrt(Math.Pow(Math.Max(cu - bu0, bu1 - cu), 2) + Math.Pow(Math.Max(cv - bv0, bv1 - cv), 2));
        double cell = Math.Max(Math.Max(FdtdWavePorts.CellAtLine(lu, bu0), FdtdWavePorts.CellAtLine(lu, bu1)),
                               Math.Max(FdtdWavePorts.CellAtLine(lv, bv0), FdtdWavePorts.CellAtLine(lv, bv1)));
        double clearance = (b - corner) / cell;
        if (!(clearance >= 1))
            return (null, $"{label}'s current probe has no room: its square round '{(ring.Outward ? p.NegativeObject : p.PositiveObject)}' must " +
                          $"lie in the dielectric a cell clear of the pin and of '{(ring.Outward ? p.PositiveObject : p.NegativeObject)}', corners " +
                          $"included, and its corners reach {FdtdGrid.FormatLength(corner)} from the axis where the shell is at " +
                          $"{FdtdGrid.FormatLength(b)} and the cell there is {FdtdGrid.FormatLength(cell)} ({clearance:0.##} cells clear). " +
                          "Refine the openEMS grid near the coax (CellsPerWavelength, MinCellUm, or a grading ratio such as 1.15), or run the " +
                          "setup on Palace or EMerge.");
        double probePlane = h + inward * (inward > 0 ? above : below) / 2;
        int currentWeight = inward * (ring.Outward ? -1 : 1);

        double side = h + inward * (inward > 0 ? above : below) / 4;
        var elements = new FdtdRingPortElements(p, n, h, source,
            At(h - below / 2, cu, cv), At(h + above / 2, cu, cv), (a + b) / 2, b - a - 2 * inset, kappa, slab, voltage,
            At(probePlane, bu0, bv0), At(probePlane, bu1, bv1), currentWeight, clearance, At(side, cu + (a + b) / 2, cv));
        return (elements, null);
    }

    private static int Index(IReadOnlyList<double> lines, double at)
    {
        for (int i = 0; i < lines.Count; i++) if (lines[i] == at) return i;
        return -1;
    }
}
