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
//     the solid's construction order, written from the problem and never re-derived (R-em3d9-2b).
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
    Nf2ffSurface?         FarField = null)
{
    public bool Ok => Refusal is null;
}

public static class CsxcadWriter
{
    /// <summary>The model file's name, in the run directory and in each port's.</summary>
    public const string ModelFile = "model.xml";

    /// <summary>brief-em3d-29 — the <paramref name="k"/>-th field dump's name, and so the stem of its files.</summary>
    public static string FieldDump(int k) => $"efield{k + 1}";

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
    public static CsxcadLowering Write(Em3dProblem problem, FdtdGridResult grid, OpenEmsGridSettings gridSettings,
                                      OpenEmsRunSettings run, IReadOnlyList<double>? farFieldHz = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(gridSettings);
        ArgumentNullException.ThrowIfNull(run);
        CsxcadLowering No(string why) => new(null, [], [], 0, 0, 0, 0, [], [], [], why);

        // ── What openEMS cannot be told ──────────────────────────────────────────────────────────
        var f = problem.Boundary.Faces;
        var faceKinds = new[] { f.XMin, f.XMax, f.YMin, f.YMax, f.ZMin, f.ZMax };
        for (int k = 0; k < 6; k++)
            if (faceKinds[k] == Em3dBoundaryKind.Symmetry)
                return No($"The air box's {FaceKeys[k]} face is a Symmetry face, which does not say whether the " +
                          "field's electric or magnetic wall lies there. Set it to Pec (an electric wall) or Pmc " +
                          "(a magnetic one) in the setup's AirBox.");
        if (problem.Ports.Count == 0)
            return No("The 3D problem has no ports, so there is nothing for openEMS to excite.");
        foreach (var p in problem.Ports)
        {
            if (!(p.Z0.Real > 0))
                return No($"Port {p.Number}'s reference impedance is {Ohms(p.Z0)}. An openEMS lumped port is " +
                          "terminated in a resistance, which must be positive: set the port's Z0 to a value with a " +
                          "positive real part.");
            if (ExcitationAxis(p) is null)
                return No($"Port {p.Number}'s direction is not along an axis in the port's own sheet, so openEMS's " +
                          "lumped port cannot state it.");
        }
        if (run.Problems() is { Count: > 0 } bad) return No(string.Join(" ", bad));

        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        materials.TryAdd(GmshGeoWriter.FreeSpace.Name, GmshGeoWriter.FreeSpace);

        // ── The band: the pulse, and the frequency tanδ is exact at ─────────────────────────────
        double fMin = problem.Frequency.StartHz, fMax = problem.Frequency.StopHz;
        double f0 = (fMin + fMax) / 2, fc = (fMax - fMin) / 2;
        if (!(fc > 0)) { f0 = fMax / 2; fc = fMax / 2; }          // FdtdGrid.ExcitationLength's rule
        double fitHz = (fMin + fMax) / 2;
        long maxSteps = run.StepCeiling(grid.Steps);

        var notes = new List<string>();
        var pec = new List<string>();
        var thin = new List<string>();
        var extended = new List<string>();

        var ctx = new Context(problem, grid, faceKinds, gridSettings.PmlCells);
        var props = new StringBuilder();
        int id = 0;

        // ── Solids and sheets, in construction order (priority = order) ─────────────────────────
        var items = problem.Solids.Select(s => (s.Order, Solid: (Em3dSolid?)s, Sheet: (Em3dSheet?)null))
                           .Concat(problem.Sheets.Select(s => (s.Order, Solid: (Em3dSolid?)null, Sheet: (Em3dSheet?)s)))
                           .OrderBy(x => x.Order).ToList();
        foreach (var item in items)
        {
            if (item.Solid is { } s)
            {
                var m = materials[s.Material];
                bool reached = false;
                string prim = Primitive(s, ctx, thin, ref reached);
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
                    props.Append("                ").Append(MaterialProperty(m, fitHz)).Append('\n');
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
                    Open(props, "ConductingSheet", id++, sh.Name, Colors.Metal,
                         $" Conductivity=\"{R(m.SigmaSm)}\" Thickness=\"{R(sh.ThicknessM)}\"");
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
        int portPriority = items.Count == 0 ? 1 : items.Max(x => x.Order) + 1;
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var excitations = new List<string>();
        foreach (var p in ports)
        {
            var (start, stop, axis, sign) = Terminals(p);
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
        for (int k = 0; k < saves.Count; k++)
        {
            props.Append($"            <DumpBox ID=\"{id++}\" Name=\"{FieldDump(k)}\" Visible=\"0\" Number=\"0\" Type=\"0\" Weight=\"1\" " +
                         "NormDir=\"-1\" StartTime=\"0\" StopTime=\"0\" DumpType=\"10\" DumpMode=\"2\" FileType=\"0\" MultiGridLevel=\"0\">\n");
            var c = Colors.Probe;
            props.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
            props.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
            props.Append($"                <FD_Samples>{R(saves[k])}</FD_Samples>\n");
            AppendPrimitives(props, Box(0, problem.Boundary.Min, problem.Boundary.Max));
            props.Append("            </DumpBox>\n");
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
            var (placed, why) = OpenEmsFarField.Place(problem, grid, farFieldHz);
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

        // ── Notes ────────────────────────────────────────────────────────────────────────────────
        notes.Add("openEMS boundaries: " + string.Join(", ", Enumerable.Range(0, 6).Select(k =>
            $"{FaceKeys[k]} {BoundaryName(faceKinds[k], ctx.Pml)}")) + ".");
        var lossy = problem.Solids.Where(s => s.Role != Em3dRole.Conductor && materials[s.Material].TanD > 0)
                                  .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        if (lossy.Count > 0)
            notes.Add($"Dielectric loss ({string.Join(", ", lossy.Select(q => $"'{q}'"))}) is a constant conductivity fitted " +
                      $"at the band centre, {G(fitHz / 1e9)} GHz: FDTD reproduces a loss tangent at one frequency only, " +
                      "so the loss is exact there and grows as 1/f away from it, where Palace holds tanδ constant. This " +
                      "is the largest expected difference between the two solvers on a lossy substrate.");
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
        if (extended.Count > 0)
            notes.Add($"{Names(extended)} reach{(extended.Count == 1 ? "es" : "")} an absorbing face and " +
                      (extended.Count == 1 ? "is" : "are") + " continued through its PML, so the absorber terminates the " +
                      "medium rather than a step into free space.");

        // ── The file ─────────────────────────────────────────────────────────────────────────────
        var air = problem.Solids.FirstOrDefault(s => s.Role == Em3dRole.Air) is { } a ? materials[a.Material] : GmshGeoWriter.FreeSpace;
        string head = Head(grid, faceKinds, ctx.Pml, maxSteps, run.EndCriterionDb, f0, fc, air, fitHz);
        const string tail = "        </Properties>\n    </ContinuousStructure>\n</openEMS>\n";
        string body = props.ToString();
        string model = head + body + tail;
        var files = excitations.Select((e, k) =>
            head + body + e.Replace("{ID}", (id).ToString(CultureInfo.InvariantCulture)) + tail).ToList();

        return new CsxcadLowering(model, files, [.. ports.Select(p => p.Number)], fitHz, f0, fc, maxSteps,
                                  pec, thin, notes, null, surface);
    }

    // ── The file's head: the run, the boundaries, the grid, the background ───────────────────────

    private static string Head(FdtdGridResult grid, Em3dBoundaryKind[] faces, int pml, long maxSteps, double endDb,
                               double f0, double fc, Em3dMaterial air, double fitHz)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>\n");
        sb.Append("<!-- Generated by circuitRF for openEMS (brief-em3d-9). Do not edit: circuitRF rewrites this file from the setup.\n");
        sb.Append("     Units: metres. Run by circuitRF once per port, as  openEMS model.xml  in that port's directory. -->\n");
        sb.Append("<openEMS>\n");
        sb.Append($"    <FDTD NumberOfTimesteps=\"{maxSteps.ToString(CultureInfo.InvariantCulture)}\" endCriteria=\"{R(Math.Pow(10, endDb / 10))}\" " +
                  $"OverSampling=\"{OverSampling}\" TimeStepMethod=\"{TimeStepMethod}\">\n");
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

    /// <summary>The excited port's property, with an <c>{ID}</c> placeholder: it is always the last.</summary>
    private static string ExcitationProperty(Em3dPort p, Point3 start, Point3 stop, int axis, int sign, int priority)
    {
        // A soft E-field source on the port sheet pointing from the positive terminal to the negative —
        // the field of a positive port voltage, E = −∇V (openEMS's own lumped-port convention).
        var e = new int[3];
        e[axis] = -sign;
        var sb = new StringBuilder();
        var c = Colors.Excitation;
        sb.Append($"            <Excitation ID=\"{{ID}}\" Name=\"port{p.Number}_excite\" Number=\"0\" Enabled=\"1\" Frequency=\"0\" " +
                  $"Delay=\"0\" Type=\"0\" Excite=\"{e[0]},{e[1]},{e[2]}\" PropDir=\"0,0,0\">\n");
        sb.Append($"                <FillColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
        sb.Append($"                <EdgeColor R=\"{c.R}\" G=\"{c.G}\" B=\"{c.B}\" a=\"{c.A}\" />\n");
        AppendPrimitives(sb, Box(priority, start, stop));
        sb.Append("                <Weight X=\"1\" Y=\"1\" Z=\"1\" />\n");
        sb.Append("            </Excitation>\n");
        return sb.ToString();
    }

    private static string MaterialProperty(Em3dMaterial m, double fitHz)
    {
        var eps = m.EpsrTensor is { Count: 3 } t ? t : [m.Epsr, m.Epsr, m.Epsr];
        string k = string.Join(",", eps.Select(e => R(Kappa(m, e, fitHz))));
        return $"<Property Epsilon=\"{string.Join(",", eps.Select(R))}\" Mue=\"{R(m.Mur)},{R(m.Mur)},{R(m.Mur)}\" " +
               $"Kappa=\"{k}\" Sigma=\"0,0,0\" Density=\"0\" />";
    }

    /// <summary>R-em3d9-2d: κ = σ + 2π·f_c·ε₀·εr·tanδ — the loss tangent exact at f_c only.</summary>
    public static double Kappa(Em3dMaterial m, double epsr, double fitHz)
        => m.SigmaSm + 2 * Math.PI * fitHz * Epsilon0 * epsr * m.TanD;

    // ── Primitives ──────────────────────────────────────────────────────────────────────────────

    private static string Primitive(Em3dSolid s, Context ctx, List<string> thin, ref bool reached)
    {
        int pr = s.Order;
        switch (s.Primitive)
        {
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
                return $"                    <Cylinder Priority=\"{pr}\" Radius=\"{R(c.Radius)}\">\n" +
                       $"                        {P("P1", c.AxisStart)}\n" +
                       $"                        {P("P2", c.AxisEnd)}\n" +
                       "                    </Cylinder>\n";
            case Em3dSphere sp:
                return $"                    <Sphere Priority=\"{pr}\" Radius=\"{R(sp.Radius)}\">\n" +
                       $"                        {P("Center", sp.Center)}\n" +
                       "                    </Sphere>\n";
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
                foreach (var v in mesh.Vertices) sb.Append($"                        <Vertex>{R(v.X)},{R(v.Y)},{R(v.Z)}</Vertex>\n");
                foreach (var t in mesh.Triangles) sb.Append($"                        <Face>{t.A},{t.B},{t.C}</Face>\n");
                sb.Append("                    </Polyhedron>\n");
                return sb.ToString();
            }
        }
    }

    private static string SheetPolygon(Em3dSheet sh, Context ctx, ref bool reached)
    {
        var ring = Keyhole(sh.Outline, sh.Holes);
        for (int i = 0; i < ring.Count; i++) ring[i] = ctx.Out2(ring[i], ref reached);
        var sb = new StringBuilder();
        sb.Append($"                    <Polygon Priority=\"{sh.Order}\" Elevation=\"{R(sh.Z)}\" NormDir=\"2\" QtyVertices=\"{ring.Count}\">\n");
        foreach (var q in ring) sb.Append($"                        <Vertex X1=\"{R(q.X)}\" X2=\"{R(q.Y)}\" />\n");
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

        public Context(Em3dProblem problem, FdtdGridResult grid, Em3dBoundaryKind[] faces, int pmlCells)
        {
            _grid = grid;
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
        public Point2 Out2(Point2 q, ref bool reached) => new(Out(q.X, 0, ref reached), Out(q.Y, 1, ref reached));
        public double OutZ(double z, ref bool reached) => Out(z, 2, ref reached);

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
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string Ohms(Complex z) => z.Imaginary == 0 ? $"{G(z.Real)} Ω" : $"{G(z.Real)}{(z.Imaginary < 0 ? "−" : "+")}j{G(Math.Abs(z.Imaginary))} Ω";
    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    private static string Esc(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
