// brief-em3d-31 R-em3d31-2 — the radiation pattern from an openEMS run: where the equivalence surface goes,
// and how its dumps are read back into the samples src/Engine's NearToFarField transforms.
//
// THE SURFACE. A box of six planar faces on grid lines, each a frequency-domain E dump (DumpType 10) and an
// H dump (DumpType 11), node-interpolated (DumpMode 1: E and H at the SAME points, the grid's own nodes), as
// VTK (FileType 0) — openEMS's HDF5 output would need a reader circuitRF does not carry. It sits:
//
//   * PmlMarginCells = 3 cells inside every absorbing face. The first requirement is that no sample's
//     interpolation stencil reaches the PML: a node-interpolated H averages the dual cells either side of
//     the node, so a surface one cell in (openEMS's own tutorial default) already reads the absorber's first
//     graded cell. The other two cells are margin against the absorber's own inner-edge reflection, which is
//     strongest in the cells next to it. Three is not a convergence result; the dipole gate
//     (OpenEmsRadiationTests) is what shows it is enough on the grid circuitRF places.
//   * ConductorClearanceCells = 1 cell clear of every conductor and port, so no node the transform reads
//     sits on metal (where E averages across a conductor and H is a surface current, not a free-space field).
//
// A setup whose air box leaves no room for both is REFUSED, naming the face and the padding to raise —
// never a surface silently pulled inside a conductor or into the PML.
//
// A CONDUCTING FLOOR (the air box's ZMin face Pec or Pmc, with every other face absorbing) leaves the
// surface open at the bottom; the five other faces run down to the floor and the transform closes the
// surface with their image (NearToFarField's header). The pattern is then the upper hemisphere, the domain
// the planar kernel reports. Any other reflecting face is refused: a wall that returns the radiated field
// into the box leaves no closed surface that sees only outgoing waves.
//
// WHAT IS NORMALISED TO WHAT. openEMS's FD dump is Σ 2·x(t)·e^{−jωt}·Δt (processfields_fd.cpp: "*2 for
// single-sided spectrum"); FdtdPortTransform's DFT of the port probes is Σ x(t)·e^{−jωt}·Δt, on the probes'
// own times. The field is halved into the port's convention and then divided by the driven port's own
// voltage phasor, so the pattern is the one a 1 V port voltage produces — the planar kernel's "port driven
// at 1 V" — with every other port terminated in its own Z0 (openEMS's lumped resistor) where the planar
// delta gaps hold theirs at 0 V.

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>One face of the equivalence surface: its key (xmin…zmax), axis (0..2) and side (0 low, 1 high).</summary>
public sealed record Nf2ffFace(string Key, int Axis, int Side)
{
    public string EDump => $"nf2ff_E_{Key}";
    public string HDump => $"nf2ff_H_{Key}";
}

/// <summary>
/// The equivalence surface as placed: its box (every face on a grid line), the faces actually dumped, the
/// floor that closes it (or none), the frequencies it is dumped at, and the sentences a run carries.
/// </summary>
public sealed record Nf2ffSurface(Point3 Min, Point3 Max, IReadOnlyList<Nf2ffFace> Faces, NfFloor Floor, double FloorZ,
                                  IReadOnlyList<double> FrequenciesHz, IReadOnlyList<string> Notes)
{
    /// <summary>Upper hemisphere with a floor; the whole sphere without.</summary>
    public bool Hemisphere => Floor != NfFloor.None;
}

public static class OpenEmsFarField
{
    /// <summary>Cells between an absorbing face and the surface — see the file header for why three.</summary>
    public const int PmlMarginCells = 3;

    /// <summary>Cells between the surface and the nearest conductor or port.</summary>
    public const int ConductorClearanceCells = 1;

    private static readonly string[] Keys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    /// <summary>
    /// The surface for <paramref name="problem"/> on <paramref name="grid"/>, dumped at
    /// <paramref name="frequenciesHz"/>; or a refusal naming what to change.
    /// </summary>
    public static (Nf2ffSurface? Surface, string? Refusal) Place(Em3dProblem problem, FdtdGridResult grid,
                                                                   IReadOnlyList<double> frequenciesHz)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(grid);
        var f = problem.Boundary.Faces;
        var kinds = new[] { f.XMin, f.XMax, f.YMin, f.YMax, f.ZMin, f.ZMax };

        var floor = NfFloor.None;
        for (int k = 0; k < 6; k++)
        {
            if (kinds[k] == Em3dBoundaryKind.Absorbing) continue;
            if (k == 4 && kinds[k] is Em3dBoundaryKind.Pec or Em3dBoundaryKind.Pmc)
            {
                floor = kinds[k] == Em3dBoundaryKind.Pec ? NfFloor.Pec : NfFloor.Pmc;
                continue;
            }
            return (null, $"The radiation pattern needs an absorbing boundary on every face of the air box, except that the " +
                          $"floor (ZMin) may be a conducting plane: the {Keys[k]} face is {kinds[k]}, which returns the radiated " +
                          "field into the box, so no closed surface inside it sees only outgoing waves. Set the AirBox's " +
                          $"{Keys[k]} face to Absorbing, or turn the radiation pattern off.");
        }

        // Every conductor and port, which the surface must enclose with a cell to spare.
        double[] lo = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] hi = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        string[] loWho = ["", "", ""], hiWho = ["", "", ""];
        void Grow(string who, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            double[] a = [x0, y0, z0], b = [x1, y1, z1];
            for (int i = 0; i < 3; i++)
            {
                if (a[i] < lo[i]) { lo[i] = a[i]; loWho[i] = who; }
                if (b[i] > hi[i]) { hi[i] = b[i]; hiWho[i] = who; }
            }
        }
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            var b = Em3dProblem.Bounds(s.Primitive);
            Grow($"'{s.Name}'", b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1);
        }
        foreach (var sh in problem.Sheets)
            Grow($"'{sh.Name}'", sh.Outline.Min(p => p.X), sh.Outline.Min(p => p.Y), sh.Z,
                 sh.Outline.Max(p => p.X), sh.Outline.Max(p => p.Y), sh.Z);
        foreach (var p in problem.Ports)
            Grow($"port {p.Number}", p.Min.X, p.Min.Y, p.Min.Z, p.Max.X, p.Max.Y, p.Max.Z);

        var box = problem.Boundary;
        double[] boxLo = [box.Min.X, box.Min.Y, box.Min.Z], boxHi = [box.Max.X, box.Max.Y, box.Max.Z];
        var planeLo = new double[3];
        var planeHi = new double[3];
        var axes = new[] { grid.X, grid.Y, grid.Z };
        for (int a = 0; a < 3; a++)
        {
            var lines = axes[a].Lines;
            double tol = 1e-9 * Math.Max(1e-6, boxHi[a] - boxLo[a]);
            int iLoFace = Nearest(lines, boxLo[a]), iHiFace = Nearest(lines, boxHi[a]);
            // The last line at or below the conductors' low extent, the first at or above their high one.
            int iLoMetal = lines.Count - 1, iHiMetal = 0;
            for (int i = 0; i < lines.Count; i++) if (lines[i] <= lo[a] + tol) iLoMetal = i;
            for (int i = lines.Count - 1; i >= 0; i--) if (lines[i] >= hi[a] - tol) iHiMetal = i;
            if (double.IsInfinity(lo[a])) { iLoMetal = (iLoFace + iHiFace) / 2; iHiMetal = iLoMetal; }

            bool floorHere = a == 2 && floor != NfFloor.None;
            int want = floorHere ? iLoFace : iLoFace + PmlMarginCells;
            if (!floorHere && want > iLoMetal - ConductorClearanceCells)
                return (null, TooTight(Keys[2 * a], want - (iLoMetal - ConductorClearanceCells), lines, iLoFace, loWho[a], +1));
            planeLo[a] = lines[want];

            int wantHi = iHiFace - PmlMarginCells;
            if (wantHi < iHiMetal + ConductorClearanceCells)
                return (null, TooTight(Keys[2 * a + 1], (iHiMetal + ConductorClearanceCells) - wantHi, lines, iHiFace, hiWho[a], -1));
            planeHi[a] = lines[wantHi];
        }

        var faces = new List<Nf2ffFace>();
        for (int k = 0; k < 6; k++)
            if (!(k == 4 && floor != NfFloor.None)) faces.Add(new Nf2ffFace(Keys[k], k / 2, k % 2));

        var notes = new List<string>
        {
            floor == NfFloor.None
                ? $"Radiation pattern: every face of the air box absorbs, so the pattern is the FULL sphere (θ 0…180°). It is " +
                  $"transformed from the tangential E and H on a closed box {PmlMarginCells} cells inside the PML and at least " +
                  $"{ConductorClearanceCells} cell clear of every conductor ({G((planeHi[0] - planeLo[0]) * 1e3)} × " +
                  $"{G((planeHi[1] - planeLo[1]) * 1e3)} × {G((planeHi[2] - planeLo[2]) * 1e3)} mm)."
                : $"Radiation pattern: the air box's floor is a conducting ({floor.ToString().ToUpperInvariant()}) plane, so the " +
                  "surface is open at the bottom and is closed by its image in that plane; the pattern is the UPPER " +
                  "HEMISPHERE (θ 0…90°), the domain the planar kernel reports, and there is no field below the floor to " +
                  $"report. The five faces sit {PmlMarginCells} cells inside the PML and at least {ConductorClearanceCells} cell " +
                  "clear of every conductor.",
        };
        var crossing = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric)
            .Where(s =>
            {
                var b = Em3dProblem.Bounds(s.Primitive);
                double[] a0 = [b.X0, b.Y0, b.Z0], a1 = [b.X1, b.Y1, b.Z1];
                for (int i = 0; i < 3; i++)
                {
                    bool floorAxis = i == 2 && floor != NfFloor.None;
                    if (a0[i] < planeLo[i] - 1e-12 && !floorAxis) return true;
                    if (a1[i] > planeHi[i] + 1e-12) return true;
                }
                return false;
            }).Select(s => $"'{s.Name}'").Distinct().ToList();
        if (crossing.Count > 0)
            notes.Add($"{string.Join(", ", crossing)} {(crossing.Count == 1 ? "crosses" : "cross")} the equivalence surface, which " +
                      "the transform treats as free space. A thin substrate crossing it costs little near broadside, but what " +
                      "the substrate guides across the surface is counted as radiation, so the pattern near the horizon and the " +
                      "radiated power read HIGH by roughly the surface-wave share the planar kernel reports separately. Draw a " +
                      "board outline inside the air box to keep the substrate within the surface.");
        return (new Nf2ffSurface(new Point3(planeLo[0], planeLo[1], planeLo[2]), new Point3(planeHi[0], planeHi[1], planeHi[2]),
                                 faces, floor, floor == NfFloor.None ? 0 : boxLo[2], frequenciesHz, notes), null);
    }

    private static string TooTight(string face, int shortCells, IReadOnlyList<double> lines, int iFace, string who, int inward)
    {
        // The cell size next to the face, for how much padding the missing cells are.
        int j = Math.Clamp(iFace + inward, 0, lines.Count - 1);
        double cell = Math.Abs(lines[j] - lines[iFace]);
        double need = Math.Ceiling(shortCells * cell * 1e6 * 1.05);
        return $"The air box is too tight for the radiation pattern's surface on its {face} face: the surface must sit " +
               $"{PmlMarginCells} cells inside the absorber and {ConductorClearanceCells} cell clear of every conductor, and " +
               $"{who} leaves {shortCells} cell(s) too few there. Raise the AirBox padding on the {face} face by at least " +
               $"{G(need)} µm (the cells there are {G(cell * 1e6)} µm), or turn the radiation pattern off.";
    }

    private static int Nearest(IReadOnlyList<double> lines, double v)
    {
        int best = 0;
        for (int i = 1; i < lines.Count; i++) if (Math.Abs(lines[i] - v) < Math.Abs(lines[best] - v)) best = i;
        return best;
    }

    // ── Reading back ─────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>_abs.vtr</c> file of dump <paramref name="name"/> at <paramref name="fHz"/> in
    /// <paramref name="dir"/>, or null. openEMS writes the frequency as an integer when it is one and
    /// with six decimals otherwise, so the match is on the parsed number.</summary>
    public static string? AbsFile(string dir, string name, double fHz)
    {
        if (!Directory.Exists(dir)) return null;
        var rx = new Regex("^" + Regex.Escape(name) + @"_f=([0-9.eE+-]+)_abs\.vtr$", RegexOptions.CultureInvariant);
        foreach (var path in Directory.EnumerateFiles(dir, name + "_f=*_abs.vtr"))
        {
            var m = rx.Match(Path.GetFileName(path));
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
                Math.Abs(v - fHz) <= 1e-6 * Math.Max(1, fHz))
                return path;
        }
        return null;
    }

    /// <summary>
    /// The surface's samples at <paramref name="fHz"/> from port run directory <paramref name="dir"/>,
    /// scaled by <paramref name="scale"/> (the half into the port's DFT convention, over the driven port's
    /// voltage): trapezoid weights on each face's node grid, the face's outward normal.
    /// </summary>
    public static List<NfSample>? ReadSamples(string dir, Nf2ffSurface surface, double fHz, Complex scale, out string? error)
    {
        error = null;
        var list = new List<NfSample>();
        foreach (var face in surface.Faces)
        {
            var e = ReadComplex(dir, face.EDump, fHz, out error);
            if (e is null) return null;
            var h = ReadComplex(dir, face.HDump, fHz, out error);
            if (h is null) return null;
            var (ed, ev) = e.Value;
            var (hd, hv) = h.Value;
            if (ed.X.Length != hd.X.Length || ed.Y.Length != hd.Y.Length || ed.Z.Length != hd.Z.Length)
            {
                error = $"openEMS's E and H dumps of the radiation pattern's {face.Key} face are on different grids.";
                return null;
            }
            double[][] c = [ed.X, ed.Y, ed.Z];
            var nrm = new double[3];
            nrm[face.Axis] = face.Side == 0 ? -1 : 1;
            var normal = new Point3(nrm[0], nrm[1], nrm[2]);
            int nx = ed.X.Length, ny = ed.Y.Length, nz = ed.Z.Length;
            double W(double[] a, int i) => a.Length < 2 ? 1 : 0.5 * ((i + 1 < a.Length ? a[i + 1] : a[i]) - (i > 0 ? a[i - 1] : a[i]));
            for (int kz = 0; kz < nz; kz++)
                for (int ky = 0; ky < ny; ky++)
                    for (int kx = 0; kx < nx; kx++)
                    {
                        double w = (face.Axis == 0 ? 1 : W(ed.X, kx)) * (face.Axis == 1 ? 1 : W(ed.Y, ky)) * (face.Axis == 2 ? 1 : W(ed.Z, kz));
                        int n = (kz * ny + ky) * nx + kx;
                        list.Add(new NfSample(new Point3(ed.X[kx], ed.Y[ky], ed.Z[kz]), normal, w,
                                              ev[3 * n] * scale, ev[3 * n + 1] * scale, ev[3 * n + 2] * scale,
                                              hv[3 * n] * scale, hv[3 * n + 1] * scale, hv[3 * n + 2] * scale));
                    }
        }
        return list;
    }

    private static (VtrData Grid, Complex[] Values)? ReadComplex(string dir, string name, double fHz, out string? error)
    {
        error = null;
        string? abs = AbsFile(dir, name, fHz);
        if (abs is null)
        {
            error = $"openEMS finished but wrote no '{name}' dump at {G(fHz / 1e9)} GHz in '{dir}', which the radiation pattern reads.";
            return null;
        }
        string arg = abs[..^"_abs.vtr".Length] + "_arg.vtr";
        try
        {
            var m = VtrFile.Read(abs);
            var p = VtrFile.Read(arg);
            if (m.Components != 3 || p.Values.Length != m.Values.Length)
            {
                error = $"openEMS's '{Path.GetFileName(abs)}' and its phase file do not hold the same three-component field.";
                return null;
            }
            var v = new Complex[m.Values.Length];
            for (int i = 0; i < v.Length; i++) v[i] = Complex.FromPolarCoordinates(m.Values[i], p.Values[i]);
            return (m, v);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = $"openEMS's radiation-pattern dump '{Path.GetFileName(abs)}' could not be read ({ex.Message}).";
            return null;
        }
    }

    /// <summary>
    /// Removes the 21 fixed-phase snapshots openEMS writes beside every VTK frequency-domain dump of the
    /// surface (…_p=000.vtr … _p=342.vtr): the magnitude and phase pair carries the complex field exactly,
    /// and on a sweep the snapshots are over nine tenths of the files. Only the surface's own dumps.
    /// Returns the bytes freed.
    /// </summary>
    public static long DeletePhaseSnapshots(string dir)
    {
        long freed = 0;
        if (!Directory.Exists(dir)) return 0;
        foreach (var path in Directory.EnumerateFiles(dir, "nf2ff_*_p=*.vtr"))
        {
            try { freed += new FileInfo(path).Length; File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return freed;
    }

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
