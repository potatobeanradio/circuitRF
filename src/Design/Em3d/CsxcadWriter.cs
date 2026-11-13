// brief-em3d-9 R-em3d9-2 — the resolved 3D problem and its FDTD grid as openEMS's CSXCAD XML.
//
// WRITTEN AS TEXT (R-em3d9-1a; em-3d.md §5.2). openEMS's Python and Octave interfaces are never used by
// product code: they link GPL code into the caller. The structural oracle is F0's case B XML, which
// upstream's own interface wrote (testdata/em3d/f0/B-via/openems/*/case.xml): the same property kinds,
// primitive kinds, port elements and probe types, compared by gate 5. Where they differ in structure,
// F0's file is right until shown otherwise.
//
// BYTE-DETERMINISTIC (overview rule 2): every number in round-trip form, '\n' on every platform,
// properties in construction order, nothing ordered by a hash. The same problem is the same bytes.
//
// WHAT CSXCAD CANNOT SAY, AND WHAT THIS WRITER DOES INSTEAD — every one reported on the run:
//   * No booleans (§6.5). Where solids overlap, the higher PRIORITY wins the cell, and the priority is
//     Em3dPrecedence's — metal over dielectric, then construction order within each (§6.3a; 3D editor
//     round 3) — computed from the problem once and never re-derived here (R-em3d9-2b).
//     A polygon's HOLE is the one subtraction inside a single solid; it is written as one KEYHOLE
//     polygon — the hole joined to the outline by a zero-width slit through the metal — which CSXCAD's
//     winding-number inside test reads exactly (the slit's two edges cancel, and a point on the slit is
//     metal, which it is).
//   * Dielectric loss (R-em3d9-2d). A constant conductivity reproduces tanδ at ONE frequency: κ =
//     σ + 2π·f_c·ε₀·εr·tanδ at the band centre f_c. Palace holds tanδ constant, so this is the largest
//     expected FEM/FDTD difference on a lossy substrate, and the note names f_c.
//   * Conductor loss. A sheet is a conducting sheet (σ, thickness) — the counterpart of Palace's
//     finite-conductivity boundary. A SOLID conductor is a perfect conductor: an FDTD grid cannot
//     resolve a metal's skin depth (F0 case A: 2.5 µm at 1 GHz in gold), and the notes name each one.
//   * Bond wires (§6.6). A hexagonal wire is the mitred prisms of Em3dTessellation as a polyhedron; a
//     round one is CSXCAD's own Wire primitive, which F0 validated (Q2: within 0.1 dB of Palace once the
//     grid step is half the radius). A wire thinner than the grid cell around it is written as a Curve —
//     openEMS's thin conductor on grid edges, whose effective radius is set by the cell, not the wire
//     (F0 Q2: 26 % too much inductance at 25 µm) — and the note names it.
//   * Absorbing faces are PML, in the PmlCells brief 8 added outside the box. A solid reaching an
//     absorbing face is continued through that PML, so the absorber sees the medium it terminates.
//
// PORTS (R-em3d9-2h) follow F0's upstream-written XML element for element: a lumped resistor on the
// port sheet, a voltage probe along its centre line, a current probe across its middle, and — in the
// excited port's file only — a soft E-field excitation. Probe names are port<k>_u / port<k>_i.
//
// WAVE PORTS (brief-em3d-116) are fed, probed lines: per terminal, a source shaped to the line on the source plane behind
// the face, voltage probes port<k>_ua/_u/_uc on three planes round the reference plane and current probes port<k>_ia/_ib
// between them, every coordinate an exact written grid line or the midpoint of two. Where they go is FdtdWavePorts'
// (src/Engine); the feed is lines FdtdGrid.Build already put in the grid, and a solid reaching the face is carried down it
// by the same Context.Out as through any absorbing face (a cylinder too, on a feed face only, so no lumped file changes).
//
// LUMPED ELEMENTS (brief-sar-3 R-sar3-3) are a port's resistor without its probes or its source: openEMS's LumpedElement
// with R, C and L each written only when stated, and LEtype 0 (parallel) or 1 (series), the attributes in the order CSXCAD
// itself writes them. Em3dLumpedElement.Inexpressible names what 0.37.0 cannot state, and the writer refuses it.
//
// ELLIPSOIDS (brief-sar-3 R-sar3-1): one whose axes are the world's is a unit Sphere under a Scale3 then a Translate
// transformation, which IS that ellipsoid (CSXCAD's inside test inverts the transformation) and is what openEMS's own
// Dipole_SAR writes. One rotated off the world's axes would need a rotation between the two, and the grid (FdtdGrid) reads a
// solid's extent from its primitive, not from CSXCAD's transformation chain; it is written as the polyhedron of its
// tessellation instead, which the grid then staircases exactly as it would the transformed sphere.
//
// CELL-CONSTANT MATERIAL (brief-sar-3 R-sar3-4): OpenEms.CellConstantMaterial writes CellConstantMaterial="1" on <FDTD>, so a
// cell takes the material at its centre rather than an average over its edges, as SAR averaging needs. Off writes nothing,
// so a setup without it is the bytes it always was.
//
// ONE MODEL, N FILES (R-em3d9-3b). Model() is the shared model; PortFile(k) is it with port k's
// excitation added as the LAST property, so the N files differ in exactly that element and no ID moves.

using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>
/// The lowering: the shared model and one file per port, what was written instead of what could not
/// be said, or why the problem cannot be stated to openEMS at all.
/// </summary>
/// <param name="Model">The model with no port excited — the run directory's record of the geometry.</param>
/// <param name="PortFiles">The model with port <c>Ports[k]</c> excited, in port-number order.</param>
/// <param name="Ports">The port numbers, in the order of <paramref name="PortFiles"/>.</param>
/// <param name="DielectricFitHz">The frequency tanδ is exact at (the band centre).</param>
/// <param name="PecSolids">Solid conductors written as perfect conductors.</param>
/// <param name="SubCellWires">Wires thinner than their grid cell, written as openEMS's thin conductor.</param>
/// <param name="Notes">The sentences the run carries about all of the above.</param>
/// <param name="FarField">brief-em3d-31 — the radiation pattern's equivalence surface, when one was asked for.</param>
public sealed record CsxcadLowering(
    string?               Model,
    IReadOnlyList<string> PortFiles,
    IReadOnlyList<int>    Ports,
    double                DielectricFitHz,
    double                ExcitationCentreHz,
    double                ExcitationHalfWidthHz,
    long                  MaxTimeSteps,
    IReadOnlyList<string> PecSolids,
    IReadOnlyList<string> SubCellWires,
    IReadOnlyList<string> Notes,
    string?               Refusal,
    Nf2ffSurface?         FarField = null,
    IReadOnlyList<Em3dKernelFile>? KernelFiles = null)
{
    public bool Ok => Refusal is null;

    /// <summary>brief-hzn-12 — a scattering run's plane-wave box: its one run excites the wave, under the pseudo-port 0, with no
    /// probe; null for a port-driven run.</summary>
    public OpenEmsPlaneWaveBox? PlaneWave { get; init; }

    /// <summary>brief-em3d-116 — each port's probe files, in <see cref="Ports"/> order: what <see cref="OpenEmsRun"/> reads.</summary>
    public IReadOnlyList<OpenEmsProbeNames> Probes { get; init; } = [];

    /// <summary>brief-em3d-116 R-em3d116-2 — every wave-port terminal's elements as written (empty with no wave port).</summary>
    public IReadOnlyList<FdtdTerminalElements> WaveTerminals { get; init; } = [];
}

/// <summary>
/// brief-em3d-116 — the probe files of one port. A lumped port's are <c>port{k}_u</c> and <c>port{k}_i</c>. A wave-port
/// terminal's U is its middle voltage plane (the mean of its two halves for a stripline) and its I the sample-wise mean of
/// its two current planes (R-em3d116-2d); <see cref="Ua"/>/<see cref="Uc"/> are the outer voltage planes, read for the line's
/// own Z and ε_eff (R-em3d116-2e), <see cref="SpacingM"/> their spacing.
/// </summary>
public sealed record OpenEmsProbeNames(int Port, IReadOnlyList<string> U, IReadOnlyList<string> I)
{
    public IReadOnlyList<string>? Ua { get; init; }
    public IReadOnlyList<string>? Uc { get; init; }
    public double SpacingM { get; init; }
    /// <summary>The terminal's label (<c>P1</c>) and, for a terminal of a port with several, that port's (<c>Left</c>).</summary>
    public string? Terminal { get; init; }
    public string? Group { get; init; }
    public bool IsWave => Ua is not null;
}

public static partial class CsxcadWriter
{
    /// <summary>The model file's name, in the run directory and in each port's.</summary>
    public const string ModelFile = "model.xml";

    /// <summary>brief-em3d-29 — the <paramref name="k"/>-th field dump's name, and so the stem of its files.</summary>
    public static string FieldDump(int k) => $"efield{k + 1}";

    /// <summary>brief-em3d-82 R-em3d82-4 — the <paramref name="k"/>-th H dump's name (<c>OpenEms.SaveH</c>): the E dump's, with
    /// <c>h</c> for <c>e</c>, so each H file sits beside its E file.</summary>
    public static string HFieldDump(int k) => $"hfield{k + 1}";

    /// <summary>
    /// brief-em3d-29 R-em3d29-6a — the frequencies, Hz, the field is dumped at: the setup's list, or the sweep's
    /// centre (the arithmetic middle of a linear sweep, the geometric of a logarithmic one); empty for none.
    /// </summary>
    public static IReadOnlyList<double> SaveFrequenciesHz(Em3dFrequency f, OpenEmsRunSettings run)
    {
        if (run.SaveFieldsGHz is { } list) return [.. list.Select(g => g * 1e9)];
        return [f.Points == 1 || f.StopHz == f.StartHz ? f.StartHz
                : f.Kind == Em3dSweepKind.Log ? Math.Sqrt(f.StartHz * f.StopHz) : 0.5 * (f.StartHz + f.StopHz)];
    }

    /// <summary>The voltage and current probes' names — and so openEMS's output files.</summary>
    public static string VoltageProbe(int port) => $"port{port}_u";
    public static string CurrentProbe(int port) => $"port{port}_i";

    /// <summary>brief-em3d-116 R-em3d116-2b/c — a wave-port terminal's probes: <c>port{k}_ua</c>, <c>_u</c>, <c>_uc</c> on the three
    /// voltage planes (with <c>_up</c>/<c>_dn</c> for a stripline's halves), <c>port{k}_ia</c>/<c>_ib</c> on the two current planes.</summary>
    public static string VoltageProbe(int port, string plane, string half) => $"port{port}_{plane}{half}";
    public static string CurrentProbe(int port, string plane) => $"port{port}_{plane}";

    /// <summary>ε₀, F/m.</summary>
    public const double Epsilon0 = 8.8541878128e-12;

    /// <summary>
    /// Probe samples per Nyquist interval of the excitation's top frequency — openEMS's own default,
    /// and F0's (<c>OverSampling="4"</c>). Every probe sample feeds the transform's DFT directly.
    /// </summary>
    public const int OverSampling = 4;

    /// <summary>openEMS's time-step method 3 (its default, and F0's): the local, less restrictive limit.</summary>
    public const int TimeStepMethod = 3;

    /// <summary>The lowering of <paramref name="problem"/> on <paramref name="grid"/>.</summary>
    /// <param name="farFieldHz">brief-em3d-31 — the frequencies to dump the radiation pattern's equivalence surface
    /// at (<see cref="OpenEmsFarField"/>), or null for no pattern: a null request writes exactly the bytes it
    /// wrote before the pattern existed.</param>
    /// <summary>How far above the thickness at which the sheet's own capacitor would cut in a sheet must stay.</summary>
    internal const double SheetStabilityMargin = 25;

    /// <summary>
    /// <b>EM-TFR — the (σ, t) a <c>ConductingSheet</c> is written with, keeping σ·t.</b> openEMS stabilises every edge of a
    /// sheet with a capacitance that grows as its stated thickness falls, in parallel with the edge's resistance:
    /// RC ≈ 45·dT²/(t·μ₀·σt). On metal σt is large and this is nothing. On a resistive film it is a low-pass on the film
    /// itself: a 300 Ω strip stated at 50 nm read 54 − 72j Ω at 40 GHz. For a sheet only σ·t is physical, so a sheet that would
    /// cut in below 25 times the top of its sweep is written thicker (RESOLVED, EM-TFR). A thicker statement adds its own
    /// internal inductance, μ₀t/3 a square, which is why it is not simply made large. Metal keeps its values to the bit.
    /// </summary>
    internal static (double Sigma, double ThicknessM) ConductingSheetValues(
        double sigma, double thicknessM, double dtS, double fMaxHz, string name, List<string> notes)
    {
        const double Mu0 = 4e-7 * Math.PI;
        double needed = SheetStabilityMargin * (2 * Math.PI * fMaxHz) * 45.0 * dtS * dtS / (Mu0 * sigma * thicknessM);
        if (!(needed > thicknessM)) return (sigma, thicknessM);
        double conductance = sigma * thicknessM;
        notes.Add($"Sheet '{name}' is written to openEMS at {needed * 1e6:G3} µm and σ = {conductance / needed:G4} S/m, the same " +
                  $"{1.0 / conductance:G4} Ω/sq: at its stated thickness, openEMS's sheet model would add a capacitance across the film.");
        return (conductance / needed, needed);
    }

    public static CsxcadLowering Write(Em3dProblem problem, FdtdGridResult grid, OpenEmsGridSettings gridSettings,
                                      OpenEmsRunSettings run, IReadOnlyList<double>? farFieldHz = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(gridSettings);
        ArgumentNullException.ThrowIfNull(run);
        CsxcadLowering No(string why) => new(null, [], [], 0, 0, 0, 0, [], [], [], why);

        if (problem.Type == Em3dProblemType.Thermal) return No(Em3dProblem.ThermalIsNotEm);
        // ── What openEMS cannot be told ──────────────────────────────────────────────────────────
        var f = problem.Boundary.Faces;
        var faceKinds = new[] { f.XMin, f.XMax, f.YMin, f.YMax, f.ZMin, f.ZMax };
        for (int k = 0; k < 6; k++)
            if (faceKinds[k] == Em3dBoundaryKind.Symmetry)
                return No($"The air box's {FaceKeys[k]} face is a Symmetry face, which does not say whether the " +
                          "field's electric or magnetic wall lies there. Set it to Pec (an electric wall) or Pmc " +
                          "(a magnetic one) in the setup's AirBox.");
        if (problem.Ports.Count == 0 && problem.PlaneWave is null)
            return No("The 3D problem has no ports, so there is nothing for openEMS to excite.");
        if (problem.PlaneWave is not null && problem.Ports.Count > 0)
            return No("The 3D problem has a plane wave and ports; a scattering run is driven by its plane wave alone.");
        foreach (var p in problem.Ports)
        {
            if (!(p.Z0.Real > 0))
                return No(p.Kind == Em3dPortKind.Wave
                    ? $"Port {p.Number}'s reference impedance is {Ohms(p.Z0)}. A wave port's S-parameters are referred to it, " +
                      "which needs a positive real part: set the port's Z0 to one."
                    : $"Port {p.Number}'s reference impedance is {Ohms(p.Z0)}. An openEMS lumped port is " +
                      "terminated in a resistance, which must be positive: set the port's Z0 to a value with a " +
                      "positive real part.");
            // brief-cxp-4 — a ring is written as a coaxial lumped port on the Cartesian grid only (below)
            if (p.Annulus is not null && grid.Cylinder is not null)
                return No($"Port {p.Number} is a coaxial port, which circuitRF writes on openEMS's Cartesian grid only. Set OpenEms.Grid " +
                          "to Cartesian, or run the setup on Palace or EMerge.");
            if (p.Quad is not null)
                return No($"Port {p.Number} is turned in its plane. openEMS's grid and its lumped port run along x, y and z, so a " +
                          "turned port cannot be written for it. Run the setup on Palace or EMerge, or set the port's Angle to 0.");
            if (p.Kind == Em3dPortKind.Lumped && p.Cylinder is null && p.Annulus is null && ExcitationAxis(p) is null)
                return No($"Port {p.Number}'s direction is not along an axis in the port's own sheet, so openEMS's " +
                          "lumped port cannot state it.");
        }
        if (run.Problems() is { Count: > 0 } bad) return No(string.Join(" ", bad));
        // brief-sar-3 R-sar3-3 — what openEMS 0.37.0's lumped element cannot state is refused by name, never written as something else.
        foreach (var le in problem.LumpedElements)
        {
            if (le.Inexpressible() is { } cannot) return No(cannot);
            if (le.Cylinder is null && LumpedAxis(le) is null)
                return No($"Lumped element '{le.Name}''s direction is not along an axis in its own sheet, so openEMS's lumped element cannot state it.");
        }
        // brief-em3d-120 — a cylindrical grid is its own lowering (CsxcadWriter.Cylindrical.cs); nothing below changes for a Cartesian one.
        if (grid.Cylinder is not null && problem.PlaneWave is not null)
            return No("The setup is driven by a plane wave, which circuitRF writes on openEMS's Cartesian grid only. Set OpenEms.Grid to Cartesian.");
        if (grid.Cylinder is { } cylinder) return WriteCylindrical(problem, grid, gridSettings, run, farFieldHz, cylinder, faceKinds);
        // brief-sar-8b — a sheet or a gap on a cylinder is a box of a cylindrical grid only.
        if (problem.FirstCylindrical() is { } curved)
            return No($"{curved} is on a cylinder, which circuitRF writes on openEMS's cylindrical grid about the same axis only. Set " +
                      "OpenEms.Grid to Cylindrical, with the Axis and AxisOriginUm of that cylinder.");
        // brief-em3d-116 — a wave port is a fed, probed line, on the grid built with its feed (FdtdGrid.Build planned it).
        var plan = grid.WavePorts;
        if (plan.Refusal is { } noWave) return No(noWave);
        if (problem.HasWavePorts && plan.Feeds.Count == 0)
            return No("The 3D problem has wave ports, and the openEMS grid was built without their feeds, so they cannot be written.");
        // brief-em3d-49 R-em3d49-4c — a face boundary is a zero-thickness sheet of PEC or of its metal, coincident with
        // the face, at a priority above every solid; the grid (FdtdGrid.Build) was given the same sheets.
        foreach (var (b, pieces) in problem.FaceBoundaryPieces())
            if (pieces.Any(pc => pc.CurvedKind is not null))
                return No($"The boundary on '{b.Object}', face '{b.Face}', is on a curved face, which openEMS cannot state as a " +
                          "sheet. Palace can; or put the boundary on a planar face.");
            else if (pieces.Any(pc => pc.NormalAxis is null))
                return No($"The boundary on face '{b.Face}' of '{b.Object}' lies in a plane no axis is normal to, and openEMS " +
                          "states a surface as a polygon normal to x, y or z on its grid. Solve it with Palace.");
        problem = Em3dFaceSheets.Apply(problem);
        // brief-em3d-42 R-em3d42-1c — CSXCAD's sheet primitive is a polygon normal to one axis.
        if (problem.Sheets.FirstOrDefault(sh => sh.Frame is { NormalAxis: null }) is { } oblique)
            return No($"Sheet '{oblique.Name}' lies in a plane no axis is normal to, and openEMS's sheet is a polygon " +
                      "normal to x, y or z on its grid. Solve it with Palace, or draw the sheet on XY, YZ or XZ.");

        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        materials.TryAdd(GmshGeoWriter.FreeSpace.Name, GmshGeoWriter.FreeSpace);

        // brief-em3d-116 R-em3d116-1c — a face carrying a wave port is absorbing (D9); its feed is in the grid, so a solid
        // reaching the face is carried through the feed and the PML (Context.Out) as through any absorbing face.
        IReadOnlyList<FdtdTerminalElements> waveTerminals = [];
        if (plan.Feeds.Count > 0)
        {
            foreach (var feed in plan.Feeds) faceKinds[Array.IndexOf(FaceKeys, feed.Face)] = Em3dBoundaryKind.Absorbing;
            var (placed, cannot) = FdtdWavePorts.Place(problem, plan, grid);
            if (cannot is not null) return No(cannot);
            waveTerminals = placed;
        }

        // ── The band: the pulse, and the frequency tanδ is exact at ─────────────────────────────
        double fMin = problem.Frequency.StartHz, fMax = problem.Frequency.StopHz;
        double f0 = (fMin + fMax) / 2, fc = (fMax - fMin) / 2;
        if (!(fc > 0)) { f0 = fMax / 2; fc = fMax / 2; }          // FdtdGrid.ExcitationLength's rule
        double fitHz = (fMin + fMax) / 2;
        long maxSteps = run.StepCeiling(grid.Steps);

        var notes = new List<string>();
        var pec = new List<string>();
        var thin = new List<string>();
        var onFaces = new List<string>();
        var extended = new List<string>();

        var ctx = new Context(problem, grid, faceKinds, gridSettings.PmlCells);
        foreach (var feed in plan.Feeds) ctx.Feed[Array.IndexOf(FaceKeys, feed.Face)] = true;
        var props = new StringBuilder();
        int id = 0;

        // ── brief-em3d-65 R-em3d65-3 — each kernel solid as a PLY file, tessellated for THIS grid ─────────
        var kernelFiles = new List<Em3dKernelFile>();
        var kernelPrimitive = new Dictionary<string, string>(StringComparer.Ordinal);
        var kernelSaid = new List<string>();
        foreach (var solid in problem.Solids)
        {
            if (solid.Primitive is not Em3dShapeSolid k || solid.Labels is not null) continue;
            var (file, why) = KernelPly(solid, k, grid, ctx);
            if (file is null) return No(why!);
            if (!kernelFiles.Any(x => x.FileName == file.FileName)) kernelFiles.Add(file);
            kernelPrimitive[solid.Name] =
                $"                    <PolyhedronReader FileName=\"{Esc(file.FileName)}\" FileType=\"PLY\" Priority=\"{ctx.Precedence.Of(solid)}\" />\n";
            kernelSaid.Add($"'{solid.Name}' at {FdtdGrid.FormatLength(file.DeflectionM!.Value)}");
        }

        // ── Solids and sheets, in construction order; priority = Em3dPrecedence (metal over dielectric) ──
        var items = problem.Solids.Select(s => (s.Order, Solid: (Em3dSolid?)s, Sheet: (Em3dSheet?)null))
                           .Concat(problem.Sheets.Select(s => (s.Order, Solid: (Em3dSolid?)null, Sheet: (Em3dSheet?)s)))
                           .OrderBy(x => x.Order).ToList();
        var voxelsWritten = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            // brief-sar-11 R-sar11-3 — a voxel body's tissues are ONE DiscMaterial, written where its first tissue would be.
            if (item.Solid is { Labels: { } volume })
            {
                if (!voxelsWritten.Add(volume.Body)) continue;
                var tissues = problem.Solids.Where(x => ReferenceEquals(x.Labels, volume)).OrderBy(x => x.Label).ToList();
                var (xml, file, refusal) = VoxelBody(volume, tissues, materials, fitHz, run.Sar?.Densities, id++);
                if (refusal is not null) return No(refusal);
                props.Append(xml);
                kernelFiles.Add(file!);
                var over = problem.Solids.Where(x => !ReferenceEquals(x.Labels, volume)).Select(x => (x.Name, B: Em3dProblem.Bounds(x.Primitive)))
                                  .Concat(problem.Sheets.Select(x => (x.Name, B: x.WorldBounds())))
                                  .Where(o => o.B.X0 < volume.X[^1] && o.B.X1 > volume.X[0] && o.B.Y0 < volume.Y[^1] && o.B.Y1 > volume.Y[0] &&
                                              o.B.Z0 < volume.Z[^1] && o.B.Z1 > volume.Z[0])
                                  .Select(o => o.Name).ToList();
                notes.Add($"The voxel body '{volume.Body}' is written as a DiscMaterial of {volume.Nx} × {volume.Ny} × {volume.Nz} voxels ({file!.FileName}), " +
                          "under every other object" +
                          (over.Count > 0 ? $": {Names(over)} reach{(over.Count == 1 ? "es" : "")} into its box and take{(over.Count == 1 ? "s" : "")} the volume they share." : "."));
                continue;
            }
            // brief-sar-8 — a voxelized phantom's layers are ONE DiscMaterial, written where its first layer would be.
            if (item.Solid is { Voxel: { } lattice })
            {
                if (!voxelsWritten.Add(lattice.Phantom)) continue;
                var layers = problem.Solids.Where(x => x.Voxel?.Phantom == lattice.Phantom).OrderBy(x => x.Order).ToList();
                var (xml, file, refusal) = VoxelPhantom(lattice, layers, materials, fitHz, run.Sar?.Densities, id++);
                if (refusal is not null) return No(refusal);
                props.Append(xml);
                kernelFiles.Add(file!);
                var box = layers.Select(x => Em3dProblem.Bounds(x.Primitive)).ToList();
                var over = problem.Solids.Where(x => x.Voxel?.Phantom != lattice.Phantom).Select(x => (x.Name, B: Em3dProblem.Bounds(x.Primitive)))
                                  .Concat(problem.Sheets.Select(x => (x.Name, B: x.WorldBounds())))
                                  .Where(o => o.B.X0 < box.Max(b => b.X1) && o.B.X1 > box.Min(b => b.X0) && o.B.Y0 < box.Max(b => b.Y1) &&
                                              o.B.Y1 > box.Min(b => b.Y0) && o.B.Z0 < box.Max(b => b.Z1) && o.B.Z1 > box.Min(b => b.Z0))
                                  .Select(o => o.Name).ToList();
                notes.Add($"The phantom '{lattice.Phantom}' is written as a DiscMaterial of {FdtdGrid.FormatLength(lattice.PitchM)} voxels " +
                          $"({file!.FileName}), each the innermost layer holding its centre, under every other object" +
                          (over.Count > 0 ? $": {Names(over)} reach{(over.Count == 1 ? "es" : "")} into its box and take{(over.Count == 1 ? "s" : "")} the volume they share." : "."));
                continue;
            }
            if (item.Solid is { } s)
            {
                var m = materials[s.Material];
                bool reached = false;
                string prim = kernelPrimitive.TryGetValue(s.Name, out var read) ? read : Primitive(s, ctx, thin, onFaces, ref reached);
                if (reached) extended.Add(s.Name);
                if (s.Role == Em3dRole.Conductor)
                {
                    pec.Add(s.Name);
                    Open(props, "Metal", id++, s.Name, Colors.Metal, "");
                    AppendPrimitives(props, prim);
                    props.Append("            </Metal>\n");
                }
                else
                {
                    Open(props, "Material", id++, s.Name, Colors.Dielectric, $" Isotropy=\"{(m.EpsrTensor is null ? 1 : 0)}\"");
                    AppendPrimitives(props, prim);
                    // brief-sar-6 R-sar6-2 — a tissue's density, so openEMS's raw SAR dump carries it (Em3dSar's header).
                    double density = run.Sar?.Densities.GetValueOrDefault(s.Name) ?? 0;
                    props.Append("                ").Append(MaterialProperty(m, fitHz, density)).Append('\n');
                    props.Append("                <Weight Epsilon=\"1,1,1\" Mue=\"1,1,1\" Kappa=\"1,1,1\" Sigma=\"1,1,1\" Density=\"1\" />\n");
                    props.Append("            </Material>\n");
                }
            }
            else
            {
                var sh = item.Sheet!;
                var m = materials[sh.Material];
                bool reached = false;
                string prim = SheetPolygon(sh, ctx, ref reached);
                if (reached) extended.Add(sh.Name);
                if (m.SigmaSm > 0 && double.IsFinite(m.SigmaSm) && sh.ThicknessM > 0)
                {
                    var (sheetSigma, sheetThickness) = ConductingSheetValues(m.SigmaSm, sh.ThicknessM, grid.TimeStepEstimateS, fMax, sh.Name, notes);
                    Open(props, "ConductingSheet", id++, sh.Name, Colors.Metal,
                         $" Conductivity=\"{R(sheetSigma)}\" Thickness=\"{R(sheetThickness)}\"");
                    AppendPrimitives(props, prim);
                    props.Append("            </ConductingSheet>\n");
                }
                else
                {
                    pec.Add(sh.Name);
                    Open(props, "Metal", id++, sh.Name, Colors.Metal, "");
                    AppendPrimitives(props, prim);
                    props.Append("            </Metal>\n");
                }
            }
        }

        // ── Ports: resistor, voltage probe, current probe (F0's element order) ──────────────────
        int portPriority = items.Count == 0 ? 1 : ctx.Precedence.Max + 1;
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var excitations = new List<string>();
        var probeNames = new List<OpenEmsProbeNames>();
        foreach (var p in ports)
        {
            if (p.Kind == Em3dPortKind.Wave)
            {
                var t = waveTerminals.Single(w => w.Port == p.Number);
                probeNames.Add(WaveProbes(props, ref id, t));
                excitations.Add(WaveExcitation(t, portPriority));
                continue;
            }
            if (p.Annulus is { } ring)
            {
                if (RingPort(props, ref id, p, ring, grid, problem, materials, portPriority, excitations, notes) is { } refused) return No(refused);
                probeNames.Add(new OpenEmsProbeNames(p.Number, [VoltageProbe(p.Number)], [CurrentProbe(p.Number)]));
                continue;
            }
            probeNames.Add(new OpenEmsProbeNames(p.Number, [VoltageProbe(p.Number)], [CurrentProbe(p.Number)]));
            var (start, stop, axis, sign) = Terminals(p);
            // brief-em3d-123 R-em3d123-2 — an extent inside a thirds pair is not a line (FdtdGrid.Snaps says where it lands).
            // openEMS snaps a LumpedElement's box and the probes to the nearest line, but its excitation takes every E edge
            // lying geometrically inside the box. So the port's width is written ON the line, the same one openEMS would snap
            // the element to, and excitation, element and probes all span the same edges.
            // brief-sar-8 — ON the line means the line's own value, and the sheet's plane too: openEMS's box test has no
            // tolerance, so an extent "on a line to rounding" a hair inside it (given lines in mm against an extent in DBU:
            // the loop coil's feed, 0.3 nm in from both) holds no edge, and the port is excited by nothing, in silence.
            if (FdtdGrid.PortWidthAxis(p) is int across)
            {
                var lines = grid.Axis((FdtdAxis)across).Lines;
                start = With(start, across, FdtdGrid.NearestLine(lines, Get(start, across)));
                stop = With(stop, across, FdtdGrid.NearestLine(lines, Get(stop, across)));
            }
            for (int n = 0; n < 3; n++)
                if (Get(start, n) == Get(stop, n))
                {
                    double plane = FdtdGrid.NearestLine(grid.Axis((FdtdAxis)n).Lines, Get(start, n));
                    start = With(start, n, plane);
                    stop = With(stop, n, plane);
                }
            Open(props, "LumpedElement", id++, $"port{p.Number}_resist", Colors.Port,
                 $" Direction=\"{axis}\" Caps=\"1\" R=\"{R(p.Z0.Real)}\" LEtype=\"0\"");
            AppendPrimitives(props, Box(portPriority, start, stop));
            props.Append("            </LumpedElement>\n");

            Point3 mid = Mid(start, stop);
            Point3 uStart = With(mid, axis, Get(start, axis)), uStop = With(mid, axis, Get(stop, axis));
            OpenProbe(props, id++, VoltageProbe(p.Number), type: 0, weight: -1, normDir: -1);
            AppendPrimitives(props, Box(0, uStart, uStop));
            props.Append("            </ProbeBox>\n");

            double m = Get(mid, axis);
            OpenProbe(props, id++, CurrentProbe(p.Number), type: 1, weight: sign, normDir: axis);
            AppendPrimitives(props, Box(0, With(start, axis, m), With(stop, axis, m)));
            props.Append("            </ProbeBox>\n");

            excitations.Add(ExcitationProperty(p, start, stop, axis, sign, portPriority));
        }

        // ── brief-sar-3 R-sar3-3 — lumped elements: the port's resistor element, with R, C and L as stated ──────────
        foreach (var le in problem.LumpedElements)
        {
            int axis = LumpedAxis(le)!.Value;
            Point3 start = le.Min, stop = le.Max;
            if (FdtdGrid.PortWidthAxis(le) is int across)
            {
                start = With(start, across, FdtdGrid.SnapToLine(grid.Axis((FdtdAxis)across).Lines, Get(start, across)));
                stop = With(stop, across, FdtdGrid.SnapToLine(grid.Axis((FdtdAxis)across).Lines, Get(stop, across)));
            }
            Open(props, "LumpedElement", id++, le.Name, Colors.Lumped, LumpedAttributes(le, axis));
            AppendPrimitives(props, Box(portPriority, start, stop));
            props.Append("            </LumpedElement>\n");
        }

        // ── brief-em3d-29 R-em3d29-6a — the field dumps: one per saved frequency, over the air box ───────
        // openEMS's frequency-domain E dump (DumpType 10) at the CELL CENTRES (DumpMode 2), as VTK (FileType 0):
        // the pinned openEMS writes each component's magnitude and phase, which is the complex field
        // exactly, plus 21 phase snapshots the view does not need. Cell centres, not nodes: the grid puts a
        // line ON every metal face, and a node there averages E across the metal (measured on a stripline:
        // half the gap's field on the ground, and the strip's upper and lower fields cancelling on it).
        var saves = SaveFrequenciesHz(problem.Frequency, run);
        if (saves.FirstOrDefault(s => s < fMin * (1 - 1e-9) || s > fMax * (1 + 1e-9)) is var outside && outside > 0)
            return No($"OpenEms.SaveFieldsGHz asks for the field at {G(outside / 1e9)} GHz, outside the sweep ({G(fMin / 1e9)} to " +
                      $"{G(fMax / 1e9)} GHz): the excitation carries no energy there to show. Choose a frequency inside the sweep.");
        // brief-em3d-82 R-em3d82-4 — with OpenEms.SaveH, one H dump (DumpType 11) beside each E dump: the same box, mode
        // and file type, so the two share a grid and the view reads them as one step. Off by default (twice the disk); a
        // setup that saves no field (SaveFieldsGHz: []) writes neither, and a setup without SaveH writes exactly what it did.
        for (int k = 0; k < saves.Count; k++)
            foreach (var (name, type) in run.SaveH ? new[] { (FieldDump(k), 10), (HFieldDump(k), 11) } : [(FieldDump(k), 10)])
            {
                props.Append($"            <DumpBox ID=\"{id++}\" Name=\"{name}\" Visible=\"0\" Number=\"0\" Type=\"0\" Weight=\"1\" " +
                             $"NormDir=\"-1\" StartTime=\"0\" StopTime=\"0\" DumpType=\"{type}\" DumpMode=\"2\" FileType=\"0\" MultiGridLevel=\"0\">\n");
                var c = Colors.Probe;
                props.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
                props.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
                props.Append($"                <FD_Samples>{R(saves[k])}</FD_Samples>\n");
                AppendPrimitives(props, Box(0, problem.Boundary.Min, problem.Boundary.Max));
                props.Append("            </DumpBox>\n");
            }

        // ── brief-sar-6 R-sar6-2 — per SAR frequency, the raw SAR dump (DumpType 29, HDF5, cell interpolation) and, for B1, an H
        // dump (DumpType 11, VTK, cell centres, as the E path's) over the same box: the tissue's bounding box plus one cell. Both
        // take the probes' oversampling, for the reason the far-field dumps below give.
        if (run.Sar is { } sar)
        {
            var (lo, hi) = Em3dSar.DumpBox(sar, grid);
            var c = Colors.Probe;
            for (int k = 0; k < sar.Settings.FrequenciesHz.Count; k++)
                foreach (var (name, type, file) in sar.Settings.B1
                             ? new[] { (Em3dSar.RawDump(k), 29, 1), (Em3dSar.HDump(k), 11, 0) } : [(Em3dSar.RawDump(k), 29, 1)])
                {
                    props.Append($"            <DumpBox ID=\"{id++}\" Name=\"{name}\" Visible=\"0\" Number=\"0\" Type=\"0\" Weight=\"1\" " +
                                 $"NormDir=\"-1\" StartTime=\"0\" StopTime=\"0\" DumpType=\"{type}\" DumpMode=\"2\" FileType=\"{file}\" MultiGridLevel=\"0\" OverSampling=\"{OverSampling}\">\n");
                    props.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
                    props.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
                    props.Append($"                <FD_Samples>{R(sar.Settings.FrequenciesHz[k])}</FD_Samples>\n");
                    AppendPrimitives(props, Box(0, lo, hi));
                    props.Append("            </DumpBox>\n");
                }
            notes.AddRange(sar.Notes);
            if (sar.Densities.Count > 0)
                notes.Add($"Tissue written with its density for SAR: {string.Join("; ", sar.Densities.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"'{d.Key}' {G(d.Value)} kg/m³"))}.");
        }

        // ── brief-em3d-31 R-em3d31-2 — the radiation pattern's equivalence surface ──────────────────
        // One E and one H frequency-domain dump per face (DumpType 10/11), node-interpolated (DumpMode 1) so E
        // and H share the grid's own nodes, as VTK. Where it sits, and why, is OpenEmsFarField's header.
        // OverSampling: openEMS accumulates a frequency-domain dump at the PLAIN Nyquist rate of the pulse's top
        // frequency unless told otherwise — two samples a period at the top of the sweep, where the rectangle-rule
        // DFT cannot separate a frequency from its own negative image. Measured on the half-wave dipole: the
        // directivity drifted up the band (2.10 → 2.37 dBi) and the top point read 6.7 % efficiency. The dumps take
        // the probes' own oversampling, so field and port are summed on equally fine samples.
        Nf2ffSurface? surface = null;
        if (farFieldHz is { Count: > 0 })
        {
            var (placed, why) = OpenEmsFarField.Place(problem, grid, farFieldHz, ctx.Pml);
            if (placed is null) return No(why!);
            surface = placed;
            string fd = string.Join(",", farFieldHz.Select(R));
            var c = Colors.Probe;
            foreach (var face in placed.Faces)
                foreach (var (name, type) in new[] { (face.EDump, 10), (face.HDump, 11) })
                {
                    props.Append($"            <DumpBox ID=\"{id++}\" Name=\"{name}\" Visible=\"0\" Number=\"0\" Type=\"0\" Weight=\"1\" " +
                                 $"NormDir=\"-1\" StartTime=\"0\" StopTime=\"0\" DumpType=\"{type}\" DumpMode=\"1\" FileType=\"0\" MultiGridLevel=\"0\" OverSampling=\"{OverSampling}\">\n");
                    props.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
                    props.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
                    props.Append($"                <FD_Samples>{fd}</FD_Samples>\n");
                    var lo = placed.Min;
                    var hi = placed.Max;
                    lo = With(lo, face.Axis, face.Side == 0 ? Get(placed.Min, face.Axis) : Get(placed.Max, face.Axis));
                    hi = With(hi, face.Axis, face.Side == 0 ? Get(placed.Min, face.Axis) : Get(placed.Max, face.Axis));
                    AppendPrimitives(props, Box(0, lo, hi));
                    props.Append("            </DumpBox>\n");
                }
            notes.AddRange(placed.Notes);
        }

        // ── brief-hzn-12 — a scattering run: the plane wave's box between the model and the surface, as the run's one excitation ──
        OpenEmsPlaneWaveBox? waveBox = null;
        if (problem.PlaneWave is { } wave)
        {
            if (surface is null) return No("The setup is driven by a plane wave, and its far-field surface was not asked for.");
            var (box, why) = OpenEmsPlaneWave.Place(problem, grid, surface);
            if (box is null) return No(why!);
            waveBox = box;
            // the frequency openEMS matches the injected wave's numerical phase velocity at: the band's centre, where the answer is
            // read, not the pulse's (which runs from 0 Hz for one frequency), or the wave leaks out of its box at the top of the band
            excitations.Add(OpenEmsPlaneWave.Excitation(wave, box, fitHz, Colors.Excitation));
            probeNames.Add(new OpenEmsProbeNames(0, [], []));
            notes.Add($"The plane wave is injected on a box {G((box.Max.X - box.Min.X) * 1e3)} × {G((box.Max.Y - box.Min.Y) * 1e3)} × " +
                      $"{G((box.Max.Z - box.Min.Z) * 1e3)} mm around the model (openEMS's total-field/scattered-field source): inside it " +
                      "the field is the total field, outside it the scattered field alone, which the far-field surface reads.");
        }

        // ── Notes ────────────────────────────────────────────────────────────────────────────────
        notes.AddRange(plan.Notes);
        if (gridSettings.ExplicitLines is { } given) notes.Add(FdtdGrid.GivenLinesNote(given, grid));
        if (run.CellConstantMaterial)
            notes.Add("OpenEms.CellConstantMaterial is on: each FDTD cell takes the material at its centre rather than an average over " +
                      "its edges (openEMS's CellConstantMaterial), as averaging SAR over cubes needs; it is less accurate at a material's face.");
        if (problem.LumpedElements.Count > 0)
            notes.Add($"{Names(problem.LumpedElements.Select(l => l.Name))} {(problem.LumpedElements.Count == 1 ? "is a lumped element" : "are lumped elements")}: " +
                      "R, L and C across a gap, excited by nothing and with no row in S.");
        if (CartesianCoaxNote(problem, plan, grid) is { } staircase) notes.Add(staircase);
        notes.Add("openEMS boundaries: " + string.Join(", ", Enumerable.Range(0, 6).Select(k =>
            $"{FaceKeys[k]} {BoundaryName(faceKinds[k], ctx.Pml)}")) + ".");
        var lossy = problem.Solids.Where(s => s.Role != Em3dRole.Conductor && materials[s.Material].TanD > 0)
                                  .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        if (lossy.Count > 0)
            notes.Add($"Dielectric loss ({string.Join(", ", lossy.Select(q => $"'{q}'"))}) is a constant conductivity fitted " +
                      $"at the band centre, {G(fitHz / 1e9)} GHz: FDTD reproduces a loss tangent at one frequency only, " +
                      "so the loss is exact there and grows as 1/f away from it, where Palace holds tanδ constant. This " +
                      "is the largest expected difference between the two solvers on a lossy substrate.");
        var conducting = problem.Solids.Where(s => s.Role != Em3dRole.Conductor && materials[s.Material].SigmaSm > 0)
                                       .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        if (conducting.Count > 0)
            notes.Add($"{string.Join(", ", conducting.Select(q => $"'{q}'"))} states a conductivity, written as it is: " +
                      "a constant conductivity is what FDTD reproduces exactly, at every frequency.");
        if (pec.Count > 0)
            notes.Add($"{Names(pec)} {(pec.Count == 1 ? "is" : "are")} written as {(pec.Count == 1 ? "a perfect conductor" : "perfect conductors")}: " +
                      "an FDTD grid does not resolve a metal's skin depth, so openEMS's answer has no conductor loss in " +
                      (pec.Count == 1 ? "it" : "them") + ", where Palace gives each its conductivity.");
        if (thin.Count > 0)
            notes.Add($"{Names(thin)} {(thin.Count == 1 ? "is" : "are")} thinner than the grid cell around " +
                      (thin.Count == 1 ? "it" : "them") + " and " + (thin.Count == 1 ? "is" : "are") +
                      " written as openEMS's thin conductor on grid edges, whose effective radius is set by the cell, " +
                      "not the wire: expect too much inductance (F0 measured 26 % on a 1 mil wire at a 25 µm cell). " +
                      "A grid step of half the wire's radius around it reached Palace within 0.1 dB.");
        if (onFaces.Count > 0)
            notes.Add($"{Names(onFaces)} {(onFaces.Count == 1 ? "is a polyhedron" : "are polyhedra")} with faces on grid " +
                      "lines, written as openEMS's polyhedron primitive — which, measured against an equal Box, leaves out " +
                      "the grid edges lying exactly on its faces (518 of the Box's edges became 78 on a 35 µm strip). " +
                      "Expect that metal to act a cell smaller on those faces; Palace meshes it exactly.");
        if (kernelSaid.Count > 0)
            notes.Add($"Kernel solids are read by openEMS from a tessellation fitted to the grid — {string.Join(", ", kernelSaid)}, a " +
                      "quarter of the smallest cell inside each. Their faces normal to an axis are written 1e-4 of the local cell " +
                      "outward of where they are, so the grid nodes lying on them are inside the solid: openEMS's polyhedron leaves " +
                      "out nodes exactly on its faces (518 of an equal Box's grid edges became 78 on a 35 µm strip).");
        // brief-em3d-65 R-em3d65-4d — the rounded features the grid will not respect: the WARNINGS are the grid's own
        // (FdtdGridResult.Warnings), the notes are here, so a run states each row once.
        notes.AddRange(Em3dFidelity.For(problem, Em3dFidelitySolver.OpenEms, grid)
                                   .Where(r => r.Severity == Em3dFidelitySeverity.Note).Select(r => r.Sentence));
        if (extended.Count > 0)
            notes.Add($"{Names(extended)} reach{(extended.Count == 1 ? "es" : "")} an absorbing face and " +
                      (extended.Count == 1 ? "is" : "are") + " continued through its PML, so the absorber terminates the " +
                      "medium rather than a step into free space.");

        // ── The file ─────────────────────────────────────────────────────────────────────────────
        // 3D editor round 3 — the box's own fill when the problem states one (a 3D view's: Air, or what the user chose).
        var air = problem.Boundary.Material is { } fill && materials.TryGetValue(fill, out var filled) ? filled
                : problem.Solids.FirstOrDefault(s => s.Role == Em3dRole.Air) is { } a ? materials[a.Material] : GmshGeoWriter.FreeSpace;
        string head = Head(grid, faceKinds, ctx.Pml, maxSteps, run.EndCriterionDb, f0, fc, air, fitHz, run.CellConstantMaterial);
        const string tail = "        </Properties>\n    </ContinuousStructure>\n</openEMS>\n";
        string body = props.ToString();
        string model = head + body + tail;
        var files = excitations.Select((e, k) =>
            head + body + e.Replace("{ID0}", (id).ToString(CultureInfo.InvariantCulture))
                           .Replace("{ID1}", (id + 1).ToString(CultureInfo.InvariantCulture)) + tail).ToList();

        return new CsxcadLowering(model, files, waveBox is null ? [.. ports.Select(p => p.Number)] : [0], fitHz, f0, fc, maxSteps,
                                  pec, thin, notes, null, surface, kernelFiles)
               { Probes = probeNames, WaveTerminals = waveTerminals, PlaneWave = waveBox };
    }

    /// <summary>
    /// brief-em3d-120 R-em3d120-5 — on a Cartesian grid, a coaxial wave-port terminal's staircase: its cells across the pin,
    /// what 113-a measured such a coax to carry, and the setting that removes it — or why it cannot be removed here.
    /// </summary>
    public static string? CartesianCoaxNote(Em3dProblem problem, FdtdWavePortPlan plan, FdtdGridResult grid)
    {
        if (grid.Cylinder is not null) return null;
        var coax = plan.Feeds.SelectMany(f => f.Terminals).Where(t => t.Shape == FdtdTerminalShape.Coaxial).ToList();
        if (coax.Count == 0) return null;
        var t = coax.MinBy(x => x.InnerRadiusM)!;
        double cell = 0;
        foreach (int a in new[] { t.U, t.V })
        {
            var lines = grid.Axis((FdtdAxis)a).Lines;
            double c = t.Centre(a);
            for (int i = 1; i < lines.Count; i++)
                if (lines[i] > c - t.InnerRadiusM && lines[i - 1] < c + t.InnerRadiusM) cell = Math.Max(cell, lines[i] - lines[i - 1]);
        }
        int across = cell > 0 ? (int)Math.Round(2 * t.InnerRadiusM / cell) : 0;
        string who = coax.Count == 1 ? $"Terminal {coax[0].Label} is" : $"Terminals {string.Join(", ", coax.Select(x => x.Label))} are";
        string remedy = FdtdWavePorts.CommonCoaxialAxis(problem, plan, out string? whyNot) is var (axis, origin)
            ? $"A cylindrical grid removes it: set the openEMS section's Grid to Cylindrical, Axis {FdtdGrid.AxisName(axis).ToUpperInvariant()}, " +
              $"AxisOriginUm [{G(origin.X * 1e6)}, {G(origin.Y * 1e6)}, {G(origin.Z * 1e6)}]."
            : $"A cylindrical grid cannot remove it here: {whyNot} (the problem is not round about one axis).";
        return $"{who} coaxial, and openEMS's Cartesian grid staircases the round conductors ({across} cells across '{t.Port.PositiveObject}'). " +
               "Brief 113-a measured such a coax 2.4 % slow in phase velocity at 20 cells across its pin and 1.0 % at 40, its impedance " +
               $"within about 0.5 Ω of the closed form. {remedy}";
    }

    /// <summary>A face normal to an axis is written this fraction of its local cell outward (R-em3d65-3d).</summary>
    public const double FaceOffsetCells = 1e-4;

    /// <summary>brief-em3d-65 R-em3d65-3a — the linear deflection, metres, a kernel solid is tessellated at for
    /// <paramref name="grid"/>: a quarter of the smallest cell inside its box.</summary>
    public static double KernelDeflectionM(Em3dShapeSolid k, FdtdGridResult grid)
    {
        var (x0, y0, z0, x1, y1, z1) = k.Bounds();
        double cell = Math.Min(Em3dFidelity.SmallestCell(grid.X.Lines, x0, x1),
                      Math.Min(Em3dFidelity.SmallestCell(grid.Y.Lines, y0, y1), Em3dFidelity.SmallestCell(grid.Z.Lines, z0, z1)));
        return 0.25 * cell;
    }

    /// <summary>
    /// brief-em3d-65 R-em3d65-3a/b/d — <paramref name="k"/> tessellated at <see cref="KernelDeflectionM"/> and written as
    /// ASCII PLY in metres: vertices welded by exact coordinate (the kernel writes a shared edge's nodes to identical
    /// coordinates on both faces, brief 61 Q10), each vertex on a planar face normal to an axis moved outward along that
    /// axis by <see cref="FaceOffsetCells"/> of its local cell — or, on an absorbing air-box face, out through the PML as
    /// every other primitive is — and every number in round-trip form. Or the refusal.
    /// </summary>
    private static (Em3dKernelFile? File, string? Refusal) KernelPly(Em3dSolid s, Em3dShapeSolid k, FdtdGridResult grid, Context ctx)
    {
        double deflection = KernelDeflectionM(k, grid);
        if (!(deflection > 0) || double.IsInfinity(deflection))
            return (null, $"'{s.Name}' is a kernel solid the openEMS grid has no cell inside, so there is nothing to fit its tessellation to.");
        if (k.Tessellator is not { } tessellate)
            return (null, $"'{s.Name}' is a kernel solid with no way to be tessellated for openEMS's grid (it was not built by the geometry kernel).");
        Em3dTriangleMesh mesh;
        try { mesh = tessellate(deflection, Em3dFidelity.OpenEmsAngularRad); }
        catch (CircuitRF.Design.ThreeD.Occ.GeometryKernelException e)
        {
            return (null, $"'{s.Name}' could not be tessellated for openEMS: {e.Message}");
        }
        if (mesh.Triangles.Count == 0)
            return (null, $"'{s.Name}' was tessellated for openEMS into no triangles, so openEMS would solve without it.");

        // The outward sign of each planar axis-normal face, from its triangles' winding in THIS mesh.
        var sum = new double[k.Faces.Count];
        foreach (var t in mesh.Triangles)
        {
            if (t.Face < 0 || t.Face >= k.Faces.Count || Em3dShapeSolid.NormalAxis(k.Faces[t.Face]) is not int a) continue;
            Point3 p = mesh.Vertices[t.A], q = mesh.Vertices[t.B], r = mesh.Vertices[t.C];
            sum[t.Face] += a switch
            {
                0 => (q.Y - p.Y) * (r.Z - p.Z) - (q.Z - p.Z) * (r.Y - p.Y),
                1 => (q.Z - p.Z) * (r.X - p.X) - (q.X - p.X) * (r.Z - p.Z),
                _ => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X),
            };
        }
        var faceAt = new double?[k.Faces.Count];
        var faceAxis = new int[k.Faces.Count];
        for (int f = 0; f < k.Faces.Count; f++)
        {
            if (Em3dShapeSolid.NormalAxis(k.Faces[f]) is not int a || sum[f] == 0) continue;
            var box = k.Faces[f].Box;
            double at = a == 0 ? box.X0 : a == 1 ? box.Y0 : box.Z0;
            var lines = grid.Axis((FdtdAxis)a).Lines;
            bool reached = false;
            double carried = ctx.OutAxis(at, a, ref reached);
            faceAt[f] = reached ? carried : at + Math.Sign(sum[f]) * FaceOffsetCells * Em3dFidelity.SmallestCell(lines, at, at);
            faceAxis[f] = a;
        }

        // Weld by exact coordinate, then place each welded vertex on every axis-normal face it lies on.
        var index = new Dictionary<Point3, int>();
        var welded = new List<double[]>();
        var map = new int[mesh.Vertices.Count];
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var v = mesh.Vertices[i];
            if (!index.TryGetValue(v, out int w))
            {
                w = welded.Count;
                index[v] = w;
                welded.Add([v.X, v.Y, v.Z]);
            }
            map[i] = w;
        }
        var placed = new bool[welded.Count * 3];
        foreach (var t in mesh.Triangles)
        {
            if (t.Face < 0 || t.Face >= k.Faces.Count || faceAt[t.Face] is not double at) continue;
            int a = faceAxis[t.Face];
            foreach (int v in (int[])[t.A, t.B, t.C])
            {
                int w = map[v];
                if (placed[3 * w + a]) continue;
                placed[3 * w + a] = true;
                welded[w][a] = at;
            }
        }

        var ply = new StringBuilder();
        ply.Append("ply\nformat ascii 1.0\n");
        ply.Append($"comment circuitRF brief-em3d-65: kernel solid {k.BrepHash[..Math.Min(16, k.BrepHash.Length)]}, metres, linear deflection {R(deflection)}\n");
        ply.Append($"element vertex {welded.Count}\nproperty float x\nproperty float y\nproperty float z\n");
        ply.Append($"element face {mesh.Triangles.Count}\nproperty list uchar int vertex_indices\nend_header\n");
        foreach (var v in welded) ply.Append(R(v[0])).Append(' ').Append(R(v[1])).Append(' ').Append(R(v[2])).Append('\n');
        foreach (var t in mesh.Triangles)
            ply.Append("3 ").Append(map[t.A]).Append(' ').Append(map[t.B]).Append(' ').Append(map[t.C]).Append('\n');
        string name = $"kernel-{k.BrepHash[..Math.Min(16, k.BrepHash.Length)]}-{R(Math.Round(deflection * 1e6, 6))}.ply";
        return (new Em3dKernelFile(name, Encoding.UTF8.GetBytes(ply.ToString()), s.Name, deflection), null);
    }

    // ── The file's head: the run, the boundaries, the grid, the background ───────────────────────

    private static string Head(FdtdGridResult grid, Em3dBoundaryKind[] faces, int pml, long maxSteps, double endDb,
                               double f0, double fc, Em3dMaterial air, double fitHz, bool cellConstant)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>\n");
        sb.Append("<!-- Generated by circuitRF for openEMS (brief-em3d-9). Do not edit: circuitRF rewrites this file from the setup.\n");
        sb.Append("     Units: metres. Run by circuitRF once per port, as  openEMS model.xml  in that port's directory. -->\n");
        sb.Append("<openEMS>\n");
        sb.Append($"    <FDTD NumberOfTimesteps=\"{maxSteps.ToString(CultureInfo.InvariantCulture)}\" endCriteria=\"{R(Math.Pow(10, endDb / 10))}\" " +
                  $"OverSampling=\"{OverSampling}\" TimeStepMethod=\"{TimeStepMethod}\"{CellConstantAttribute(cellConstant)}>\n");
        sb.Append($"        <Excitation Type=\"0\" f0=\"{R(f0)}\" fc=\"{R(fc)}\" />\n");
        sb.Append("        <BoundaryCond");
        for (int k = 0; k < 6; k++) sb.Append($" {FaceKeys[k]}=\"{BoundaryToken(faces[k], pml)}\"");
        sb.Append(" />\n");
        sb.Append("    </FDTD>\n");
        sb.Append("    <ContinuousStructure CoordSystem=\"0\">\n");
        sb.Append("        <RectilinearGrid DeltaUnit=\"1\" CoordSystem=\"0\">\n");
        foreach (var (tag, a) in new[] { ("XLines", grid.X), ("YLines", grid.Y), ("ZLines", grid.Z) })
            sb.Append($"            <{tag} Qty=\"{a.Lines.Count}\">{string.Join(",", a.Lines.Select(R))}</{tag}>\n");
        sb.Append("        </RectilinearGrid>\n");
        sb.Append($"        <BackgroundMaterial Epsilon=\"{R(air.Epsr)}\" Mue=\"{R(air.Mur)}\" Kappa=\"{R(Kappa(air, air.Epsr, fitHz))}\" Sigma=\"0\" />\n");
        sb.Append("        <ParameterSet />\n");
        sb.Append("        <Properties>\n");
        return sb.ToString();
    }

    /// <summary>brief-sar-3 R-sar3-4 — the attribute on &lt;FDTD&gt;, or nothing when off.</summary>
    private static string CellConstantAttribute(bool on) => on ? " CellConstantMaterial=\"1\"" : "";

    /// <summary>
    /// brief-sar-3 R-sar3-6 — an absorbing face is <c>PML_&lt;n&gt;</c> with <c>PmlCells</c> n, and <c>MUR</c> with PmlCells 0: the
    /// two spellings the SAR tutorials pass openEMS (<c>['PML_8']*6</c>, <c>['MUR']*6</c>), which its BoundaryCond reads as text.
    /// </summary>
    private static string BoundaryToken(Em3dBoundaryKind k, int pml) => k switch
    {
        Em3dBoundaryKind.Absorbing => pml > 0 ? $"PML_{pml}" : "MUR",
        Em3dBoundaryKind.Pmc       => "PMC",
        _                          => "PEC",
    };

    private static string BoundaryName(Em3dBoundaryKind k, int pml) => k switch
    {
        Em3dBoundaryKind.Absorbing => pml > 0 ? $"PML ({pml} cells)" : "first-order Mur (PmlCells is 0)",
        Em3dBoundaryKind.Pmc       => "PMC",
        _                          => "PEC",
    };

    // ── Properties ──────────────────────────────────────────────────────────────────────────────

    private static void Open(StringBuilder sb, string kind, int id, string name, (int R, int G, int B, int A) c, string extra)
    {
        sb.Append($"            <{kind} ID=\"{id}\" Name=\"{Esc(name)}\"{extra}>\n");
        sb.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
        sb.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
    }

    private static void OpenProbe(StringBuilder sb, int id, string name, int type, int weight, int normDir)
    {
        sb.Append($"            <ProbeBox ID=\"{id}\" Name=\"{Esc(name)}\" Visible=\"0\" Number=\"0\" Type=\"{type}\" " +
                  $"Weight=\"{weight}\" NormDir=\"{normDir}\" StartTime=\"0\" StopTime=\"0\">\n");
        var c = Colors.Probe;
        sb.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
        sb.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
    }

    private static void AppendPrimitives(StringBuilder sb, string primitives)
    {
        sb.Append("                <Primitives>\n");
        sb.Append(primitives);
        sb.Append("                </Primitives>\n");
    }

    /// <summary>
    /// brief-cxp-4 — a coaxial port (<see cref="FdtdRingPorts"/>): its termination slab, its voltage and current probes, and its
    /// radial source as the excited file's last property. The slab takes the permittivity of the dielectric on the ring's inward
    /// side, so the termination changes nothing but the conductance there. The refusal, or null.
    /// </summary>
    private static string? RingPort(StringBuilder props, ref int id, Em3dPort p, Em3dAnnulus ring, FdtdGridResult grid, Em3dProblem problem,
                                    IReadOnlyDictionary<string, Em3dMaterial> materials, int priority, List<string> excitations, List<string> notes)
    {
        var (el, why) = FdtdRingPorts.Place(p, ring, grid);
        if (el is null) return why;
        var q = el.SideProbe;
        var fill = problem.Solids.Where(s => s.Role != Em3dRole.Conductor && Inside(Em3dProblem.Bounds(s.Primitive), q))
                          .OrderByDescending(s => s.Order).FirstOrDefault();
        double er = fill is not null && materials.TryGetValue(fill.Material, out var fm) ? fm.Epsr
                  : problem.Boundary.Material is { } bm && materials.TryGetValue(bm, out var am) ? am.Epsr : 1.0;

        Open(props, "Material", id++, $"port{p.Number}_resist", Colors.Port, " Isotropy=\"1\"");
        AppendPrimitives(props,
            $"                    <CylindricalShell Priority=\"{priority}\" Radius=\"{R(el.ShellRadiusM)}\" ShellWidth=\"{R(el.ShellWidthM)}\">\n" +
            $"                        {P("P1", el.ShellP1)}\n" +
            $"                        {P("P2", el.ShellP2)}\n" +
            "                    </CylindricalShell>\n");
        props.Append($"                <Property Epsilon=\"{R(er)},{R(er)},{R(er)}\" Mue=\"1,1,1\" Kappa=\"{R(el.KappaSm)},{R(el.KappaSm)},{R(el.KappaSm)}\" " +
                     "Sigma=\"0,0,0\" Density=\"0\" />\n");
        props.Append("                <Weight Epsilon=\"1,1,1\" Mue=\"1,1,1\" Kappa=\"1,1,1\" Sigma=\"1,1,1\" Density=\"1\" />\n");
        props.Append("            </Material>\n");

        OpenProbe(props, id++, VoltageProbe(p.Number), type: 0, weight: -1, normDir: -1);
        AppendPrimitives(props, Box(0, el.Voltage.From, el.Voltage.To));
        props.Append("            </ProbeBox>\n");
        OpenProbe(props, id++, CurrentProbe(p.Number), type: 1, weight: el.CurrentWeight, normDir: el.Normal);
        AppendPrimitives(props, Box(0, el.CurrentMin, el.CurrentMax));
        props.Append("            </ProbeBox>\n");

        var src = el.Source;
        var c = Colors.Excitation;
        var sb = new StringBuilder();
        sb.Append($"            <Excitation ID=\"{{ID0}}\" Name=\"port{p.Number}_excite\" Number=\"0\" Enabled=\"1\" Frequency=\"0\" " +
                  $"Delay=\"0\" Type=\"0\" Excite=\"{src.Excite[0]},{src.Excite[1]},{src.Excite[2]}\" PropDir=\"0,0,0\">\n");
        sb.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
        sb.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
        AppendPrimitives(sb, Box(priority, src.Min, src.Max));
        sb.Append($"                <Weight X=\"{Esc(src.Weight![0])}\" Y=\"{Esc(src.Weight[1])}\" Z=\"{Esc(src.Weight[2])}\" />\n");
        sb.Append("            </Excitation>\n");
        excitations.Add(sb.ToString());
        notes.Add($"Port {p.SourceNumber ?? p.Number} is coaxial: a radial source on its ring, terminated in {G(p.Z0.Real)} Ω by a conducting slab " +
                  $"of κ = {G(el.KappaSm)} S/m over the {FdtdGrid.FormatLength(el.SlabM)} cell at its plane; its current probe clears the shell by " +
                  $"{el.ClearanceCells:0.#} cells. The grid staircases the ring with its pin and shell.");
        return null;

        static bool Inside((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, Point3 q)
            => q.X >= b.X0 && q.X <= b.X1 && q.Y >= b.Y0 && q.Y <= b.Y1 && q.Z >= b.Z0 && q.Z <= b.Z1;
    }

    /// <summary>The excited port's property, with an <c>{ID0}</c> placeholder: it is always the last.</summary>
    private static string ExcitationProperty(Em3dPort p, Point3 start, Point3 stop, int axis, int sign, int priority)
    {
        // A soft E-field source on the port sheet pointing from the positive terminal to the negative —
        // the field of a positive port voltage, E = −∇V (openEMS's own lumped-port convention).
        var e = new int[3];
        e[axis] = -sign;
        var sb = new StringBuilder();
        var c = Colors.Excitation;
        sb.Append($"            <Excitation ID=\"{{ID0}}\" Name=\"port{p.Number}_excite\" Number=\"0\" Enabled=\"1\" Frequency=\"0\" " +
                  $"Delay=\"0\" Type=\"0\" Excite=\"{e[0]},{e[1]},{e[2]}\" PropDir=\"0,0,0\">\n");
        sb.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
        sb.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
        AppendPrimitives(sb, Box(priority, start, stop));
        sb.Append("                <Weight X=\"1\" Y=\"1\" Z=\"1\" />\n");
        sb.Append("            </Excitation>\n");
        return sb.ToString();
    }

    // ── brief-em3d-116 — a wave-port terminal: three voltage planes, two current planes, a shaped source ─────────

    private static readonly string[] PlaneNames = ["ua", "u", "uc"];

    /// <summary>
    /// R-em3d116-2b/c — the terminal's probes: on each voltage plane the path from the reference to the conductor with the
    /// lumped port's weight −1 (a stripline's two halves as <c>_up</c>/<c>_dn</c>, each weight 1 in magnitude, averaged by the
    /// reader), and on each half-cell plane between them the current box, its sign making I flow into the device. A strip
    /// on an interface (brief-em3d-125, <see cref="FdtdTerminal.VoltageHalf"/>) still writes both halves, so the model is
    /// unchanged, but names only the half toward its reference for the reader.
    /// </summary>
    private static OpenEmsProbeNames WaveProbes(StringBuilder props, ref int id, FdtdTerminalElements t)
    {
        int k = t.Port;
        int halves = t.Voltage.Count / 3;
        var names = new List<string>[3];
        for (int plane = 0; plane < 3; plane++)
        {
            names[plane] = [];
            for (int h = 0; h < halves; h++)
            {
                var q = t.Voltage[plane * halves + h];
                string name = VoltageProbe(k, PlaneNames[plane], q.Suffix);
                if (t.Terminal.VoltageHalf is not { } half || q.Suffix == half) names[plane].Add(name);
                OpenProbe(props, id++, name, type: 0, weight: -1, normDir: -1);
                AppendPrimitives(props, Box(0, q.From, q.To));
                props.Append("            </ProbeBox>\n");
            }
        }
        int axis = (int)t.Feed.Axis, u = t.Terminal.U, v = t.Terminal.V;
        var (b0, c0, b1, c1) = t.CurrentBox;
        foreach (var (plane, at) in new[] { ("ia", t.IaAtM), ("ib", t.IbAtM) })
        {
            OpenProbe(props, id++, CurrentProbe(k, plane), type: 1, weight: t.CurrentWeight, normDir: axis);
            AppendPrimitives(props, Box(0, Place(axis, at, u, b0, v, c0), Place(axis, at, u, b1, v, c1)));
            props.Append("            </ProbeBox>\n");
        }
        return new OpenEmsProbeNames(k, names[1], [CurrentProbe(k, "ia"), CurrentProbe(k, "ib")])
        {
            Ua = names[0], Uc = names[2], SpacingM = t.PlaneSpacingM, Terminal = t.Terminal.Label,
            Group = t.Terminal.Port.FaceGroup is null ? null : t.Terminal.Port.FaceGroupLabel,
        };
    }

    /// <summary>R-em3d116-2a — the terminal's source(s), as the excited file's last properties (<c>{ID0}</c>, <c>{ID1}</c>).</summary>
    private static string WaveExcitation(FdtdTerminalElements t, int priority)
    {
        var sb = new StringBuilder();
        var c = Colors.Excitation;
        for (int i = 0; i < t.Sources.Count; i++)
        {
            var src = t.Sources[i];
            var e = src.Excite;
            sb.Append($"            <Excitation ID=\"{{ID{i}}}\" Name=\"port{t.Port}_excite{src.Suffix}\" Number=\"0\" Enabled=\"1\" Frequency=\"0\" " +
                      $"Delay=\"0\" Type=\"0\" Excite=\"{e[0]},{e[1]},{e[2]}\" PropDir=\"0,0,0\">\n");
            sb.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
            sb.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
            AppendPrimitives(sb, Box(priority, src.Min, src.Max));
            sb.Append(src.Weight is { } w
                ? $"                <Weight X=\"{Esc(w[0])}\" Y=\"{Esc(w[1])}\" Z=\"{Esc(w[2])}\" />\n"
                : "                <Weight X=\"1\" Y=\"1\" Z=\"1\" />\n");
            sb.Append("            </Excitation>\n");
        }
        return sb.ToString();
    }

    private static Point3 Place(int a, double av, int b, double bv, int c, double cv)
    {
        var x = new double[3];
        x[a] = av; x[b] = bv; x[c] = cv;
        return new Point3(x[0], x[1], x[2]);
    }

    private static string MaterialProperty(Em3dMaterial m, double fitHz, double density = 0)
    {
        var eps = m.EpsrTensor is { Count: 3 } t ? t : [m.Epsr, m.Epsr, m.Epsr];
        string k = string.Join(",", eps.Select(e => R(Kappa(m, e, fitHz))));
        return $"<Property Epsilon=\"{string.Join(",", eps.Select(R))}\" Mue=\"{R(m.Mur)},{R(m.Mur)},{R(m.Mur)}\" " +
               $"Kappa=\"{k}\" Sigma=\"0,0,0\" Density=\"{(density > 0 ? R(density) : "0")}\" />";
    }

    /// <summary>R-em3d9-2d: κ = σ + 2π·f_c·ε₀·εr·tanδ — the loss tangent exact at f_c only.</summary>
    public static double Kappa(Em3dMaterial m, double epsr, double fitHz)
        => m.SigmaSm + 2 * Math.PI * fitHz * Epsilon0 * epsr * m.TanD;

    // ── Primitives ──────────────────────────────────────────────────────────────────────────────

    private static string Primitive(Em3dSolid s, Context ctx, List<string> thin, List<string> onFaces, ref bool reached)
    {
        int pr = ctx.Precedence.Of(s);
        switch (s.Primitive)
        {
            // brief-em3d-42 — openEMS's polyhedron leaves out grid nodes lying exactly on its faces, where a Box or
            // a LinPoly includes them (measured, RESOLVED.md §brief-em3d-42), and the grid puts lines on every
            // axis-aligned face. So a polyhedron that IS a box, or a right prism along an axis, is written as one.
            case Em3dPolyhedron ph when AxisBox(ph) is var (lo, hi):
                return Box(pr, ctx.Out(lo, ref reached), ctx.Out(hi, ref reached));
            case Em3dPolyhedron ph when AxisPrism(ph) is { } prism:
            {
                var (axis, bottom, top, outline, holes) = prism;
                int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                double lo = ctx.OutAxis(bottom, axis, ref reached), hi = ctx.OutAxis(top, axis, ref reached);
                var ring = Keyhole(outline, holes);
                var sb = new StringBuilder();
                sb.Append($"                    <LinPoly Priority=\"{pr}\" Elevation=\"{R(lo)}\" NormDir=\"{axis}\" QtyVertices=\"{ring.Count}\" Length=\"{R(hi - lo)}\">\n");
                foreach (var q in ring)
                    sb.Append($"                        <Vertex X1=\"{R(ctx.OutAxis(q.X, a1, ref reached))}\" X2=\"{R(ctx.OutAxis(q.Y, a2, ref reached))}\" />\n");
                sb.Append("                    </LinPoly>\n");
                return sb.ToString();
            }
            case Em3dPolyhedron ph when ph.Faces.Any(f => IsAxisNormal(ph.Normal(f))):
                onFaces.Add(s.Name);
                goto default;
            case Em3dBox b:
            {
                var (lo, hi) = (ctx.Out(b.Min, ref reached), ctx.Out(b.Max, ref reached));
                return Box(pr, lo, hi);
            }
            case Em3dExtrudedPolygon e:
            {
                var ring = Keyhole(e.Outline, e.Holes);
                for (int i = 0; i < ring.Count; i++) ring[i] = ctx.Out2(ring[i], ref reached);
                double zb = ctx.OutZ(e.ZBottom, ref reached), zt = ctx.OutZ(e.ZTop, ref reached);
                var sb = new StringBuilder();
                sb.Append($"                    <LinPoly Priority=\"{pr}\" Elevation=\"{R(zb)}\" NormDir=\"2\" QtyVertices=\"{ring.Count}\" Length=\"{R(zt - zb)}\">\n");
                foreach (var q in ring) sb.Append($"                        <Vertex X1=\"{R(q.X)}\" X2=\"{R(q.Y)}\" />\n");
                sb.Append("                    </LinPoly>\n");
                return sb.ToString();
            }
            case Em3dCylinder c:
                // brief-em3d-116 R-em3d116-1a — an axis end on a wave port's face runs on down its feed (only there, so a
                // lumped problem's cylinders are written exactly as before).
                return $"                    <Cylinder Priority=\"{pr}\" Radius=\"{R(c.Radius)}\">\n" +
                       $"                        {P("P1", ctx.OutFeed(c.AxisStart, ref reached))}\n" +
                       $"                        {P("P2", ctx.OutFeed(c.AxisEnd, ref reached))}\n" +
                       "                    </Cylinder>\n";
            case Em3dSphere sp:
                return $"                    <Sphere Priority=\"{pr}\" Radius=\"{R(sp.Radius)}\">\n" +
                       $"                        {P("Center", sp.Center)}\n" +
                       "                    </Sphere>\n";
            case Em3dEllipsoid el when el.AlignedSemiAxes() is var (ax, ay, az):
                return ScaledSphere(pr, el.Center, ax, ay, az);
            case Em3dSweep w when ctx.SubCell(w):
            {
                thin.Add(s.Name);
                var sb = new StringBuilder();
                sb.Append($"                    <Curve Priority=\"{pr}\">\n");
                foreach (var q in w.Path) sb.Append($"                        <Vertex X=\"{R(q.X)}\" Y=\"{R(q.Y)}\" Z=\"{R(q.Z)}\" />\n");
                sb.Append("                    </Curve>\n");
                return sb.ToString();
            }
            case Em3dSweep { Section: Em3dSection.Circle } w:
            {
                var sb = new StringBuilder();
                sb.Append($"                    <Wire Priority=\"{pr}\" WireRadius=\"{R(w.Diameter / 2)}\">\n");
                foreach (var q in w.Path) sb.Append($"                        <Vertex X=\"{R(q.X)}\" Y=\"{R(q.Y)}\" Z=\"{R(q.Z)}\" />\n");
                sb.Append("                    </Wire>\n");
                return sb.ToString();
            }
            default:
            {
                // The hexagonal wire's mitred prisms and a flattened ball: brief 5's one tessellation.
                var mesh = Em3dTessellation.Of(s);
                var sb = new StringBuilder();
                sb.Append($"                    <Polyhedron Priority=\"{pr}\">\n");
                foreach (var v0 in mesh.Vertices)
                {
                    var v = ctx.OutFeed(v0, ref reached);
                    sb.Append($"                        <Vertex>{R(v.X)},{R(v.Y)},{R(v.Z)}</Vertex>\n");
                }
                foreach (var t in mesh.Triangles) sb.Append($"                        <Face>{t.A},{t.B},{t.C}</Face>\n");
                sb.Append("                    </Polyhedron>\n");
                return sb.ToString();
            }
        }
    }

    /// <summary>brief-sar-3 R-sar3-1 — an axis-aligned ellipsoid as openEMS's Dipole_SAR writes one: a unit sphere at the origin,
    /// scaled per axis, then moved to its centre.</summary>
    public static string ScaledSphere(int priority, Point3 centre, double ax, double ay, double az)
        => $"                    <Sphere Priority=\"{priority}\" Radius=\"1\">\n" +
           "                        <Transformation>\n" +
           $"                            <Scale3 Argument=\"{R(ax)},{R(ay)},{R(az)}\" />\n" +
           $"                            <Translate Argument=\"{R(centre.X)},{R(centre.Y)},{R(centre.Z)}\" />\n" +
           "                        </Transformation>\n" +
           "                        <Center X=\"0\" Y=\"0\" Z=\"0\" />\n" +
           "                    </Sphere>\n";

    /// <summary>brief-sar-3 R-sar3-3 — the axis a lumped element's current runs along, in its own sheet; null when there is none.</summary>
    private static int? LumpedAxis(Em3dLumpedElement le)
    {
        double[] d = [le.Direction.X, le.Direction.Y, le.Direction.Z];
        double[] ext = [le.Max.X - le.Min.X, le.Max.Y - le.Min.Y, le.Max.Z - le.Min.Z];
        for (int a = 0; a < 3; a++)
            if (Math.Abs(d[a]) > 1 - 1e-9 && ext[a] > 0) return a;
        return null;
    }

    /// <summary>
    /// brief-sar-3 R-sar3-3 — a lumped element's attributes as CSXCAD writes them (<c>CSPropLumpedElement::Write2XML</c>):
    /// Direction, Caps, then R, C and L — each only when stated, since an absent one reads back as not present — then LEtype.
    /// </summary>
    public static string LumpedAttributes(Em3dLumpedElement le, int axis)
    {
        var sb = new StringBuilder($" Direction=\"{axis}\" Caps=\"1\"");
        if (le.R is { } r) sb.Append($" R=\"{R(r)}\"");
        if (le.C is { } c) sb.Append($" C=\"{R(c)}\"");
        if (le.L is { } l) sb.Append($" L=\"{R(l)}\"");
        sb.Append($" LEtype=\"{(le.Topology == Em3dRlcTopology.Series ? 1 : 0)}\"");
        return sb.ToString();
    }

    private static bool IsAxisNormal(Point3 n)
        => (Math.Abs(n.X) > 1 - 1e-12 ? 1 : 0) + (Math.Abs(n.Y) > 1 - 1e-12 ? 1 : 0) + (Math.Abs(n.Z) > 1 - 1e-12 ? 1 : 0) == 1;

    /// <summary>The corners of a polyhedron that is an axis-aligned box — six axis-normal faces over eight
    /// vertices — or null.</summary>
    private static (Point3 Lo, Point3 Hi)? AxisBox(Em3dPolyhedron ph)
    {
        if (ph.Faces.Count != 6 || ph.Vertices.Count != 8 || ph.Faces.Any(f => f.Holes.Count > 0 || !IsAxisNormal(ph.Normal(f))))
            return null;
        var lo = new Point3(ph.Vertices.Min(v => v.X), ph.Vertices.Min(v => v.Y), ph.Vertices.Min(v => v.Z));
        var hi = new Point3(ph.Vertices.Max(v => v.X), ph.Vertices.Max(v => v.Y), ph.Vertices.Max(v => v.Z));
        foreach (var v in ph.Vertices)
            if ((v.X != lo.X && v.X != hi.X) || (v.Y != lo.Y && v.Y != hi.Y) || (v.Z != lo.Z && v.Z != hi.Z)) return null;
        return (lo, hi);
    }

    /// <summary>
    /// A polyhedron that is a right prism along a world axis: two faces normal to it at two coordinates, every
    /// other face parallel to it, and every vertex on one of the two caps — its axis, the caps' coordinates, and
    /// the lower cap's rings in CSXCAD's cyclic in-plane axes; or null.
    /// </summary>
    private static (int Axis, double Bottom, double Top, List<Point2> Outline, List<IReadOnlyList<Point2>> Holes)? AxisPrism(Em3dPolyhedron ph)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            var caps = ph.Faces.Where(f => Math.Abs(Get(ph.Normal(f), axis)) > 1 - 1e-12).ToList();
            if (caps.Count != 2) continue;
            if (ph.Faces.Except(caps).Any(f => Math.Abs(Get(ph.Normal(f), axis)) > 1e-12)) return null;
            double c0 = Get(ph.Vertices[caps[0].Outer[0]], axis), c1 = Get(ph.Vertices[caps[1].Outer[0]], axis);
            if (ph.Vertices.Any(v => Get(v, axis) != c0 && Get(v, axis) != c1) || c0 == c1) return null;
            var low = c0 < c1 ? caps[0] : caps[1];
            int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
            Point2 P(int i) => new(Get(ph.Vertices[i], a1), Get(ph.Vertices[i], a2));
            return (axis, Math.Min(c0, c1), Math.Max(c0, c1), [.. low.Outer.Select(P)],
                    [.. low.Holes.Select(h => (IReadOnlyList<Point2>)[.. h.Select(P)])]);
        }
        return null;
    }

    private static string SheetPolygon(Em3dSheet sh, Context ctx, ref bool reached)
    {
        if (sh.Frame is not null) return FramedSheetPolygon(sh, ctx, ref reached);
        var ring = Keyhole(sh.Outline, sh.Holes);
        for (int i = 0; i < ring.Count; i++) ring[i] = ctx.Out2(ring[i], ref reached);
        var sb = new StringBuilder();
        sb.Append($"                    <Polygon Priority=\"{ctx.Precedence.Of(sh)}\" Elevation=\"{R(sh.Z)}\" NormDir=\"2\" QtyVertices=\"{ring.Count}\">\n");
        foreach (var q in ring) sb.Append($"                        <Vertex X1=\"{R(q.X)}\" X2=\"{R(q.Y)}\" />\n");
        sb.Append("                    </Polygon>\n");
        return sb.ToString();
    }

    /// <summary>
    /// brief-em3d-42 — a sheet on YZ or XZ (or on XY through a frame): CSXCAD's Polygon normal to that axis, its
    /// vertices in the two remaining axes in CSXCAD's cyclic order ((n+1) mod 3, (n+2) mod 3), at the
    /// plane's coordinate. The keyhole is built in the sheet's own frame, then placed.
    /// </summary>
    private static string FramedSheetPolygon(Em3dSheet sh, Context ctx, ref bool reached)
    {
        int n = sh.Frame!.NormalAxis!.Value;
        int a1 = (n + 1) % 3, a2 = (n + 2) % 3;
        var ring = new List<Point3>();
        foreach (var q in Keyhole(sh.Outline, sh.Holes)) ring.Add(ctx.Out(sh.World(q), ref reached));
        double elevation = Get(sh.World(sh.Outline[0]), n);
        var sb = new StringBuilder();
        sb.Append($"                    <Polygon Priority=\"{ctx.Precedence.Of(sh)}\" Elevation=\"{R(elevation)}\" NormDir=\"{n}\" QtyVertices=\"{ring.Count}\">\n");
        foreach (var q in ring) sb.Append($"                        <Vertex X1=\"{R(Get(q, a1))}\" X2=\"{R(Get(q, a2))}\" />\n");
        sb.Append("                    </Polygon>\n");
        return sb.ToString();
    }

    private static string Box(int priority, Point3 a, Point3 b)
        => $"                    <Box Priority=\"{priority}\">\n" +
           $"                        {P("P1", a)}\n" +
           $"                        {P("P2", b)}\n" +
           "                    </Box>\n";

    private static string P(string tag, Point3 q) => $"<{tag} X=\"{R(q.X)}\" Y=\"{R(q.Y)}\" Z=\"{R(q.Z)}\" />";

    // ── Holes as one keyhole ring ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The outline with every hole joined in by a zero-width slit: a single ring whose winding number
    /// is 1 in the metal and 0 in each hole. Each hole, taken in order of its rightmost vertex (right
    /// first), is bridged from that vertex along +x to the nearest edge of the ring built so far —
    /// nearest, so the slit crosses nothing, and right first, so no hole still to come lies in its way
    /// (every one of them lies at or left of the bridge's start).
    /// </summary>
    public static List<Point2> Keyhole(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes)
    {
        var ring = Oriented(outline, counterClockwise: true);
        var order = holes.Select((h, i) => (Ring: Oriented(h, counterClockwise: false), i))
                         .Where(h => h.Ring.Count >= 3)
                         .Select(h => (h.Ring, h.i, M: h.Ring.Select((q, k) => (q, k)).OrderByDescending(v => v.q.X).ThenBy(v => v.q.Y).First()))
                         .OrderByDescending(h => h.M.q.X).ThenBy(h => h.M.q.Y).ThenBy(h => h.i).ToList();
        foreach (var (hole, _, (m, mk)) in order)
        {
            int best = -1;
            double bestX = double.PositiveInfinity;
            for (int k = 0; k < ring.Count; k++)
            {
                var p = ring[k];
                var q = ring[(k + 1) % ring.Count];
                if (p.Y == q.Y) continue;
                if (m.Y < Math.Min(p.Y, q.Y) || m.Y > Math.Max(p.Y, q.Y)) continue;
                double x = p.X + (m.Y - p.Y) * (q.X - p.X) / (q.Y - p.Y);
                if (x < m.X || x >= bestX) continue;
                bestX = x;
                best = k;
            }
            if (best < 0) continue;          // a hole outside its outline: nothing to cut
            var pt = new Point2(bestX, m.Y);
            var bridge = new List<Point2> { pt };
            for (int j = 0; j <= hole.Count; j++) bridge.Add(hole[(mk + j) % hole.Count]);
            bridge.Add(pt);
            ring.InsertRange(best + 1, bridge);
        }
        return ring;
    }

    private static List<Point2> Oriented(IReadOnlyList<Point2> ring, bool counterClockwise)
    {
        double area = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        var list = ring.ToList();
        if (area > 0 != counterClockwise) list.Reverse();
        return list;
    }

    // ── Ports ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The axis the port's voltage lies along, or null when its direction is not an axis in its sheet.</summary>
    private static int? ExcitationAxis(Em3dPort p)
    {
        double[] d = [p.Direction.X, p.Direction.Y, p.Direction.Z];
        double[] ext = [p.Max.X - p.Min.X, p.Max.Y - p.Min.Y, p.Max.Z - p.Min.Z];
        int? axis = null;
        for (int a = 0; a < 3; a++)
        {
            if (Math.Abs(d[a]) > 1 - 1e-9) axis = a;
            else if (Math.Abs(d[a]) > 1e-9) return null;
        }
        return axis is { } k && ext[k] > 0 ? axis : null;
    }

    /// <summary>
    /// The port's two ends as openEMS's lumped port states them: <c>start</c> on the NEGATIVE object,
    /// <c>stop</c> on the positive, the excitation axis, and the sign of stop − start along it.
    /// </summary>
    private static (Point3 Start, Point3 Stop, int Axis, int Sign) Terminals(Em3dPort p)
    {
        int axis = ExcitationAxis(p)!.Value;
        int sign = Get(p.Direction, axis) > 0 ? 1 : -1;
        Point3 start = p.Min, stop = p.Max;
        if (sign < 0)
        {
            start = With(p.Min, axis, Get(p.Max, axis));
            stop  = With(p.Max, axis, Get(p.Min, axis));
        }
        return (start, stop, axis, sign);
    }

    private static double Get(Point3 q, int axis) => axis switch { 0 => q.X, 1 => q.Y, _ => q.Z };
    private static Point3 With(Point3 q, int axis, double v) => axis switch
    {
        0 => q with { X = v }, 1 => q with { Y = v }, _ => q with { Z = v },
    };
    private static Point3 Mid(Point3 a, Point3 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

    // ── The grid, as the primitives see it ──────────────────────────────────────────────────────

    private sealed class Context
    {
        private readonly FdtdGridResult _grid;
        private readonly double[] _box;       // xmin xmax ymin ymax zmin zmax
        private readonly double[] _outer;     // the grid's own extremes
        private readonly bool[] _absorbing;
        private readonly double _tol;
        public int Pml { get; }
        /// <summary>3D editor round 3 — metal over dielectric, construction order within each (em-3d.md §6.3a).</summary>
        public Em3dPrecedence Precedence { get; }

        public Context(Em3dProblem problem, FdtdGridResult grid, Em3dBoundaryKind[] faces, int pmlCells)
        {
            _grid = grid;
            Precedence = Em3dPrecedence.Of(problem);
            var b = problem.Boundary;
            _box = [b.Min.X, b.Max.X, b.Min.Y, b.Max.Y, b.Min.Z, b.Max.Z];
            _outer = [grid.X.Lines[0], grid.X.Lines[^1], grid.Y.Lines[0], grid.Y.Lines[^1], grid.Z.Lines[0], grid.Z.Lines[^1]];
            Pml = pmlCells;
            _absorbing = [.. faces.Select(k => k == Em3dBoundaryKind.Absorbing)];
            _tol = 1e-9 * Math.Max(1e-6, Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z)));
        }

        /// <summary>A coordinate on an absorbing face, carried out to the grid's edge through the PML.</summary>
        private double Out(double v, int axis, ref bool reached)
        {
            if (Pml == 0) return v;
            if (_absorbing[2 * axis] && Math.Abs(v - _box[2 * axis]) <= _tol && _outer[2 * axis] < v)
            { reached = true; return _outer[2 * axis]; }
            if (_absorbing[2 * axis + 1] && Math.Abs(v - _box[2 * axis + 1]) <= _tol && _outer[2 * axis + 1] > v)
            { reached = true; return _outer[2 * axis + 1]; }
            return v;
        }

        public Point3 Out(Point3 q, ref bool reached)
            => new(Out(q.X, 0, ref reached), Out(q.Y, 1, ref reached), Out(q.Z, 2, ref reached));

        /// <summary>brief-em3d-116 — the faces carrying a wave port's feed (xmin … zmax order); set by the writer.</summary>
        public bool[] Feed { get; } = new bool[6];

        /// <summary>brief-em3d-116 — <see cref="Out(Point3, ref bool)"/>, on the faces carrying a feed only.</summary>
        public Point3 OutFeed(Point3 q, ref bool reached)
        {
            if (!Feed.Any(f => f)) return q;
            double[] v = [q.X, q.Y, q.Z];
            for (int a = 0; a < 3; a++)
                if ((Feed[2 * a] && Math.Abs(v[a] - _box[2 * a]) <= _tol) || (Feed[2 * a + 1] && Math.Abs(v[a] - _box[2 * a + 1]) <= _tol))
                    v[a] = Out(v[a], a, ref reached);
            return new Point3(v[0], v[1], v[2]);
        }
        public Point2 Out2(Point2 q, ref bool reached) => new(Out(q.X, 0, ref reached), Out(q.Y, 1, ref reached));
        public double OutZ(double z, ref bool reached) => Out(z, 2, ref reached);
        public double OutAxis(double v, int axis, ref bool reached) => Out(v, axis, ref reached);

        /// <summary>
        /// R-em3d9-2c: a wire is below the grid when its diameter is smaller than the coarsest side of
        /// the grid cell holding any vertex of its path — there, no cell lies wholly inside it.
        /// </summary>
        public bool SubCell(Em3dSweep w)
        {
            foreach (var q in w.Path)
            {
                double cell = Math.Max(Cell(_grid.X.Lines, q.X), Math.Max(Cell(_grid.Y.Lines, q.Y), Cell(_grid.Z.Lines, q.Z)));
                if (w.Diameter < cell) return true;
            }
            return false;
        }

        private static double Cell(IReadOnlyList<double> lines, double v)
        {
            int lo = 0, hi = lines.Count - 1;
            if (v <= lines[0]) return lines[1] - lines[0];
            if (v >= lines[hi]) return lines[hi] - lines[hi - 1];
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (lines[mid] <= v) lo = mid; else hi = mid;
            }
            return lines[hi] - lines[lo];
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] FaceKeys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    /// <summary>Fixed colours per kind: upstream draws random ones, which would break determinism.</summary>
    private static class Colors
    {
        public static readonly (int R, int G, int B, int A) Dielectric = (167, 241, 217, 123);
        public static readonly (int R, int G, int B, int A) Metal      = (42, 130, 200, 255);
        public static readonly (int R, int G, int B, int A) Port       = (140, 226, 179, 255);
        public static readonly (int R, int G, int B, int A) Probe      = (152, 84, 47, 255);
        public static readonly (int R, int G, int B, int A) Excitation = (71, 23, 17, 255);
        public static readonly (int R, int G, int B, int A) Lumped     = (216, 254, 67, 255);
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string Ohms(Complex z) => z.Imaginary == 0 ? $"{G(z.Real)} Ω" : $"{G(z.Real)}{(z.Imaginary < 0 ? "−" : "+")}j{G(Math.Abs(z.Imaginary))} Ω";
    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    private static string Esc(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
