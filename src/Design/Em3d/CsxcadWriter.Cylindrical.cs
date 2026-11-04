// brief-em3d-120 R-em3d120-4 — the lowering on a CYLINDRICAL grid (FdtdGrid.Build with OpenEms.Grid: Cylindrical).
//
// A separate path from the Cartesian one, so a setup that does not ask for a cylindrical grid writes the same bytes as
// before (series rule 3). What it writes, each from the spike's measurements (src/Design/RESOLVED.md § "brief-em3d-120"):
//   * The head: CylinderCoords="1" on <FDTD>, CoordSystem="1" on <ContinuousStructure> and <RectilinearGrid>, lines in
//     ρ (m), α (rad, 0 to 2π exactly — the closing line must be 2π to the last digit, R-em3d120-1d) and z (m).
//   * Every primitive circuitRF draws in world coordinates carries CoordSystem="0" (R-em3d120-1a: without it CSXCAD reads a
//     Box's corners, a Cylinder's, a Sphere's, a Wire's and a Curve's points as (ρ, α, z)), in openEMS's frame on this grid:
//     its z along the axis, x and y across it from the origin (FdtdCylinder.ToCsx).
//   * A cylinder COAXIAL with the axis is a Box in (ρ, α, z): that fills the grid's circle exactly, where a Cartesian Cylinder
//     on a grid radius loses the edges its cos/sin rounding puts a hair outside (39 of 8,658 α-edges, measured).
//   * A Subtract of plain primitives is its operands with priorities (R-em3d120-4b) when that is equivalent; every
//     priority is then written DOUBLED so a hole fits at 2·p + 1, between its blank and anything above it.
//   * A wave port's coaxial terminal: FdtdWavePorts.Place on this grid, every element in (ρ, α, z).
//   * brief-sar-8b — a sheet on a cylinder about the axis is one Box of zero radial extent in (ρ, α, z) (two where its azimuths
//     cross the grid's α seam), each edge that lies on a grid line to rounding written as that line's own value (openEMS's box
//     test has no tolerance, sar.md §10a.6); a lumped element or lumped port on one is openEMS's own lumped element, probes
//     and source in (ρ, α, z), as the tutorial's AddLumpedElement and AddLumpedPort state them — its radius and the two edges
//     across its width ON the grid's own lines (openEMS's excitation box has no tolerance, sar.md §10.2). A voxelized phantom is
//     one DiscMaterial, as on a Cartesian grid, its file in openEMS's frame; the raw SAR dump and B1's H dump cover the tissue's
//     radii, the whole circle and its axial extent, both HDF5 (circuitRF reads openEMS's VTK on a Cartesian grid only).
//     openEMS's MultiGrid radii (OpenEms.MultiGridRadiiUm) are written on <FDTD>.
// What a cylindrical grid cannot carry is refused, each naming the setting that removes it: a radiation pattern (openEMS's
// near-to-far-field box is written on a Cartesian grid only). The 3D view's field files are not dumped: its reader takes a
// Cartesian grid, and the run says so.

using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static partial class CsxcadWriter
{
    private static CsxcadLowering WriteCylindrical(Em3dProblem problem, FdtdGridResult grid, OpenEmsGridSettings gridSettings,
                                                   OpenEmsRunSettings run, IReadOnlyList<double>? farFieldHz, FdtdCylinder cyl,
                                                   Em3dBoundaryKind[] faceKinds)
    {
        CsxcadLowering No(string why) => new(null, [], [], 0, 0, 0, 0, [], [], [], why);
        if (grid.Refusal is { } refused) return No(refused);
        // brief-sar-11 — a DiscMaterial's voxels are Cartesian; a cylindrical grid would sample them on its own cells unsaid.
        if (problem.Solids.FirstOrDefault(s => s.Labels is not null) is { Labels: { } voxelBody })
            return No($"The voxel body '{voxelBody.Body}' is a Cartesian voxel model, which circuitRF writes on a Cartesian grid only. " +
                      "Set OpenEms.Grid to Cartesian.");
        if (farFieldHz is { Count: > 0 })
            return No("A radiation pattern needs openEMS's near-to-far-field box, which circuitRF writes on a Cartesian grid only. " +
                      "Set OpenEms.Grid to Cartesian for the pattern, or turn RadiationPattern off for the cylindrical run.");
        var plan = grid.WavePorts;
        if (plan.Refusal is { } noWave) return No(noWave);
        if (problem.HasWavePorts && plan.Feeds.Count == 0)
            return No("The 3D problem has wave ports, and the openEMS grid was built without their feeds, so they cannot be written.");
        foreach (var (b, pieces) in problem.FaceBoundaryPieces())
            if (pieces.Any(pc => pc.CurvedKind is not null))
                return No($"The boundary on '{b.Object}', face '{b.Face}', is on a curved face, which openEMS cannot state as a " +
                          "sheet. Palace can; or put the boundary on a planar face.");
            else if (pieces.Any(pc => pc.NormalAxis is null))
                return No($"The boundary on face '{b.Face}' of '{b.Object}' lies in a plane no axis is normal to, and openEMS " +
                          "states a surface as a polygon normal to x, y or z on its grid. Solve it with Palace.");
        problem = Em3dFaceSheets.Apply(problem);
        if (problem.Sheets.FirstOrDefault(sh => sh.Frame is { NormalAxis: null }) is { } oblique)
            return No($"Sheet '{oblique.Name}' lies in a plane no axis is normal to, and openEMS's sheet is a polygon " +
                      "normal to x, y or z on its grid. Solve it with Palace, or draw the sheet on XY, YZ or XZ.");

        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        materials.TryAdd(GmshGeoWriter.FreeSpace.Name, GmshGeoWriter.FreeSpace);
        foreach (var feed in plan.Feeds) faceKinds[Array.IndexOf(FaceKeys, feed.Face)] = Em3dBoundaryKind.Absorbing;
        var (waveTerminals, cannot) = FdtdWavePorts.Place(problem, plan, grid);
        if (cannot is not null) return No(cannot);

        double fMin = problem.Frequency.StartHz, fMax = problem.Frequency.StopHz;
        double f0 = (fMin + fMax) / 2, fc = (fMax - fMin) / 2;
        if (!(fc > 0)) { f0 = fMax / 2; fc = fMax / 2; }
        double fitHz = (fMin + fMax) / 2;
        long maxSteps = run.StepCeiling(grid.Steps);
        var air = problem.Boundary.Material is { } fill && materials.TryGetValue(fill, out var filled) ? filled
                : problem.Solids.FirstOrDefault(s => s.Role == Em3dRole.Air) is { } a ? materials[a.Material] : GmshGeoWriter.FreeSpace;

        var notes = new List<string>();
        var pec = new List<string>();
        var thin = new List<string>();
        var extended = new List<string>();
        var asOperands = new List<string>();
        var ctx = new CylContext(problem, grid, cyl, faceKinds, gridSettings.PmlCells);
        // brief-sar-8b — a gap on a cylinder is written in (ρ, α, z); a planar one never reaches here (FdtdGrid refused it).
        foreach (var le in problem.LumpedElements)
            if (le.Inexpressible() is { } inexpressible) return No(inexpressible);
        var props = new StringBuilder();
        int id = 0;

        // ── Kernel solids: the operands where that is equivalent, else a PLY fitted to this grid ─────────────────
        var kernelFiles = new List<Em3dKernelFile>();
        var kernelPrimitive = new Dictionary<string, string>(StringComparer.Ordinal);
        var holesFor = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);     // kept-tool solid → its hole primitives
        var backgroundHoles = new Dictionary<string, string>(StringComparer.Ordinal);      // result solid → background hole properties
        var kernelSaid = new List<string>();
        foreach (var solid in problem.Solids)
        {
            if (solid.Primitive is not Em3dShapeSolid k) continue;
            if (k.Operands is { } ops)
            {
                bool reachedOps = false;
                var (prims, why) = Operands(solid, ops, problem, ctx, holesFor, backgroundHoles, air, fitHz, ref reachedOps);
                if (prims is not null)
                {
                    kernelPrimitive[solid.Name] = prims;
                    asOperands.Add(solid.Name);
                    if (reachedOps) extended.Add(solid.Name);
                    continue;
                }
                notes.Add($"'{solid.Name}' is written as its kernel tessellation, not as its Blank and Tools: {why} Its curved faces are " +
                          "then polygons, off the grid's circles by up to the tessellation's chord error.");
            }
            var (file, refusal) = CylKernelPly(solid, k, ctx);
            if (file is null) return No(refusal!);
            if (!kernelFiles.Any(x => x.FileName == file.FileName)) kernelFiles.Add(file);
            kernelPrimitive[solid.Name] =
                $"                    <PolyhedronReader FileName=\"{Esc(file.FileName)}\" FileType=\"PLY\" Priority=\"{ctx.P(solid)}\" CoordSystem=\"0\" />\n";
            kernelSaid.Add($"'{solid.Name}' at {FdtdGrid.FormatLength(file.DeflectionM!.Value)}");
        }

        // ── Solids and sheets, in construction order ──────────────────────────────────────────────────────────
        var items = problem.Solids.Select(s => (s.Order, Solid: (Em3dSolid?)s, Sheet: (Em3dSheet?)null, Cyl: (Em3dCylinderSheet?)null))
                           .Concat(problem.Sheets.Select(s => (s.Order, Solid: (Em3dSolid?)null, Sheet: (Em3dSheet?)s, Cyl: (Em3dCylinderSheet?)null)))
                           .Concat(problem.CylinderSheets.Select(s => (s.Order, Solid: (Em3dSolid?)null, Sheet: (Em3dSheet?)null, Cyl: (Em3dCylinderSheet?)s)))
                           .OrderBy(x => x.Order).ToList();
        var voxelsWritten = new HashSet<string>(StringComparer.Ordinal);
        var onCylinder = new List<string>();
        foreach (var item in items)
        {
            // brief-sar-8b — a voxelized phantom's layers are ONE DiscMaterial, as on a Cartesian grid (CsxcadWriter.Voxels).
            if (item.Solid is { Voxel: { } lattice })
            {
                if (!voxelsWritten.Add(lattice.Phantom)) continue;
                var layers = problem.Solids.Where(x => x.Voxel?.Phantom == lattice.Phantom).OrderBy(x => x.Order).ToList();
                var (xml, file, refusal) = VoxelPhantom(lattice, layers, materials, fitHz, run.Sar?.Densities, id++, cyl);
                if (refusal is not null) return No(refusal);
                props.Append(xml);
                kernelFiles.Add(file!);
                notes.Add($"The phantom '{lattice.Phantom}' is written as a DiscMaterial of {FdtdGrid.FormatLength(lattice.PitchM)} voxels " +
                          $"({file!.FileName}), each the innermost layer holding its centre, under every other object; openEMS looks each " +
                          "cylindrical cell's centre up in it, in Cartesian coordinates.");
                continue;
            }
            if (item.Cyl is { } cs)
            {
                pec.Add(cs.Name);
                onCylinder.Add(cs.Name);
                Open(props, "Metal", id++, cs.Name, Colors.Metal, "");
                // An edge on a line to rounding is written as the line's own value (FdtdGrid.LineOrSelf): openEMS's box test has no
                // tolerance, and the birdcage's rungs — ends a base plus a length in metres, 2e-18 m short of the line; azimuths
                // degrees turned to radians, ulps past the line's fifteen digits — lost a line of edges each and resonated 2.6 MHz
                // low (sar.md §10a.6).
                double r = FdtdGrid.LineOrSelf(grid.X.Lines, cs.Patch.Radius);
                double lo = FdtdGrid.LineOrSelf(grid.Z.Lines, cs.Patch.Lo), hi = FdtdGrid.LineOrSelf(grid.Z.Lines, cs.Patch.Hi);
                foreach (var (a0, a1) in AlphaSpans(cs.Patch, grid))
                    AppendPrimitives(props, Box(ctx.P(cs), new Point3(r, FdtdGrid.LineOrSelf(grid.Y.Lines, a0), lo),
                                                          new Point3(r, FdtdGrid.LineOrSelf(grid.Y.Lines, a1), hi)));
                props.Append("            </Metal>\n");
                continue;
            }
            if (item.Solid is { } s)
            {
                var m = materials[s.Material];
                bool reached = false;
                string prim = kernelPrimitive.TryGetValue(s.Name, out var read) ? read : CylPrimitive(s.Primitive, ctx.P(s), ctx, s.Name, thin, ref reached);
                if (holesFor.TryGetValue(s.Name, out var holes)) prim += holes.ToString();
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
                    // brief-sar-8b — a tissue's density, for the raw SAR dump, as on a Cartesian grid.
                    props.Append("                ").Append(MaterialProperty(m, fitHz, run.Sar?.Densities.GetValueOrDefault(s.Name) ?? 0)).Append('\n');
                    props.Append("                <Weight Epsilon=\"1,1,1\" Mue=\"1,1,1\" Kappa=\"1,1,1\" Sigma=\"1,1,1\" Density=\"1\" />\n");
                    props.Append("            </Material>\n");
                }
                if (backgroundHoles.TryGetValue(s.Name, out var bg))
                    props.Append(bg.Replace("{ID}", (id++).ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                var sh = item.Sheet!;
                var m = materials[sh.Material];
                string prim = CylSheet(sh, ctx);
                if (m.SigmaSm > 0 && double.IsFinite(m.SigmaSm) && sh.ThicknessM > 0)
                {
                    var (sheetSigma, sheetThickness) = ConductingSheetValues(m.SigmaSm, sh.ThicknessM, grid.TimeStepEstimateS, problem.Frequency.StopHz, sh.Name, notes);
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

        // ── Ports: every one a coaxial wave-port terminal (FdtdGrid refused the rest) ───────────────────────────
        int portPriority = 2 * (items.Count == 0 ? 1 : ctx.Precedence.Max + 1);
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var excitations = new List<string>();
        var probeNames = new List<OpenEmsProbeNames>();
        foreach (var p in ports)
        {
            if (p.Cylinder is { } gap)
            {
                // brief-sar-8b R-sar8b-1 — the tutorial's AddLumpedPort in (ρ, α, z): resistor, voltage probe, current probe, and the
                // source in the port's own file.
                var (start, stop, along, sign, gapRefused) = CylGap($"Port {p.SourceNumber ?? p.Number}", gap, grid);
                if (gapRefused is not null) return No(gapRefused);
                probeNames.Add(new OpenEmsProbeNames(p.Number, [VoltageProbe(p.Number)], [CurrentProbe(p.Number)]));
                Open(props, "LumpedElement", id++, $"port{p.Number}_resist", Colors.Port, $" Direction=\"{along}\" Caps=\"1\" R=\"{R(p.Z0.Real)}\" LEtype=\"0\"");
                AppendPrimitives(props, Box(portPriority, start, stop));
                props.Append("            </LumpedElement>\n");
                Point3 mid = Mid(start, stop);
                OpenProbe(props, id++, VoltageProbe(p.Number), type: 0, weight: -1, normDir: -1);
                AppendPrimitives(props, Box(0, With(mid, along, Get(start, along)), With(mid, along, Get(stop, along))));
                props.Append("            </ProbeBox>\n");
                double m = Get(mid, along);
                OpenProbe(props, id++, CurrentProbe(p.Number), type: 1, weight: sign, normDir: along);
                AppendPrimitives(props, Box(0, With(start, along, m), With(stop, along, m)));
                props.Append("            </ProbeBox>\n");
                excitations.Add(ExcitationProperty(p, start, stop, along, sign, portPriority));
                continue;
            }
            var t = waveTerminals.Single(w => w.Port == p.Number);
            probeNames.Add(CylWaveProbes(props, ref id, t));
            excitations.Add(WaveExcitation(t, portPriority));
        }

        // ── brief-sar-8b R-sar8b-1 — lumped elements on the cylinder: the port's resistor element with R, C and L as stated ──────
        foreach (var le in problem.LumpedElements)
        {
            var (start, stop, along, _, gapRefused) = CylGap($"Lumped element '{le.Name}'", le.Cylinder!, grid);
            if (gapRefused is not null) return No(gapRefused);
            Open(props, "LumpedElement", id++, le.Name, Colors.Lumped, LumpedAttributes(le, along));
            AppendPrimitives(props, Box(portPriority, start, stop));
            props.Append("            </LumpedElement>\n");
        }

        // ── brief-sar-8b R-sar8b-4 — per SAR frequency the raw SAR dump (DumpType 29) and, for B1, an H dump (DumpType 11), both HDF5,
        // cell interpolation, over the tissue's radii, the whole circle and its axial extent, one cell more each way. ──────────
        if (run.Sar is { } sar)
        {
            var (lo, hi) = Em3dSar.DumpBox(sar, grid);
            var c = Colors.Probe;
            for (int k = 0; k < sar.Settings.FrequenciesHz.Count; k++)
                foreach (var (name, type) in sar.Settings.B1 ? new[] { (Em3dSar.RawDump(k), 29), (Em3dSar.HDump(k), 11) } : [(Em3dSar.RawDump(k), 29)])
                {
                    props.Append($"            <DumpBox ID=\"{id++}\" Name=\"{name}\" Visible=\"0\" Number=\"0\" Type=\"0\" Weight=\"1\" " +
                                 $"NormDir=\"-1\" StartTime=\"0\" StopTime=\"0\" DumpType=\"{type}\" DumpMode=\"2\" FileType=\"1\" MultiGridLevel=\"0\" OverSampling=\"{OverSampling}\">\n");
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

        // ── Notes ───────────────────────────────────────────────────────────────────────────────────────────────
        notes.AddRange(plan.Notes);
        if (gridSettings.ExplicitLines is { Cylindrical: true } given) notes.Add(FdtdGrid.GivenLinesNote(given, grid));
        if (onCylinder.Count > 0)
            notes.Add($"{Names(onCylinder)} {(onCylinder.Count == 1 ? "is a sheet" : "are sheets")} on a cylinder about the grid's axis, written as " +
                      "boxes of zero radial extent in (ρ, α, z), exact on the grid's circle.");
        if (problem.LumpedElements.Count > 0)
            notes.Add($"{Names(problem.LumpedElements.Select(l => l.Name))} {(problem.LumpedElements.Count == 1 ? "is a lumped element" : "are lumped elements")} " +
                      "on a cylinder: R, L and C across a gap in (ρ, α, z), excited by nothing and with no row in S.");
        if (cyl.Axis is var _ && gridSettings.Cylindrical?.MultiGridRadiiM is { Count: > 0 } mg)
            notes.Add($"openEMS splits the grid at ρ = {string.Join(", ", mg.Select(FdtdGrid.FormatLength))} (MultiGrid): inside each radius it takes " +
                      "every second azimuth line, so the cells near the axis are not the thin wedges that would set the time step.");
        if (run.CellConstantMaterial)
            notes.Add("OpenEms.CellConstantMaterial is on: each FDTD cell takes the material at its centre rather than an average over its edges.");
        string axial = FdtdGrid.AxisName(cyl.Axis);
        notes.Add($"openEMS boundaries on the cylindrical grid: ρmin PEC ({(cyl.RhoMinSetBy is { } pin ? $"the surface of '{pin}'" : "the axis itself, which openEMS treats")}), " +
                  $"ρmax {BoundaryName(cyl.RhoMaxKind, ctx.Pml)} (the {string.Join(", ", cyl.SideFaces)} faces), " +
                  $"{axial}min {BoundaryName(faceKinds[2 * cyl.A], ctx.Pml)}, {axial}max {BoundaryName(faceKinds[2 * cyl.A + 1], ctx.Pml)}; α runs the full circle.");
        if (SaveFrequenciesHz(problem.Frequency, run).Count > 0)
            notes.Add("No field is saved for the 3D view on a cylindrical grid: its field plots read openEMS's Cartesian dump. S-parameters are " +
                      "unaffected; set OpenEms.Grid to Cartesian to see the field.");
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
                      " written as openEMS's thin conductor on grid edges, whose effective radius is set by the cell, not the wire.");
        if (asOperands.Count > 0)
            notes.Add($"{Names(asOperands)} {(asOperands.Count == 1 ? "is" : "are")} written as {(asOperands.Count == 1 ? "its" : "their")} Blank " +
                      "with each Tool cut out by priority, not as a tessellation, so a bore coaxial with the axis lies on the grid's own circle.");
        if (kernelSaid.Count > 0)
            notes.Add($"Kernel solids are read by openEMS from a tessellation fitted to the grid — {string.Join(", ", kernelSaid)}, a " +
                      "quarter of the smallest cell inside each.");
        if (extended.Count > 0)
            notes.Add($"{Names(extended)} reach{(extended.Count == 1 ? "es" : "")} an absorbing face and " +
                      (extended.Count == 1 ? "is" : "are") + " continued through its PML, so the absorber terminates the " +
                      "medium rather than a step into free space.");

        // ── The file ───────────────────────────────────────────────────────────────────────────────────────────
        string head = CylHead(grid, cyl, faceKinds, ctx.Pml, maxSteps, run.EndCriterionDb, f0, fc, air, fitHz, run.CellConstantMaterial,
                              gridSettings.Cylindrical?.MultiGridRadiiM);
        const string tail = "        </Properties>\n    </ContinuousStructure>\n</openEMS>\n";
        string body = props.ToString();
        var files = excitations.Select(e =>
            head + body + e.Replace("{ID0}", id.ToString(CultureInfo.InvariantCulture))
                           .Replace("{ID1}", (id + 1).ToString(CultureInfo.InvariantCulture)) + tail).ToList();
        return new CsxcadLowering(head + body + tail, files, [.. ports.Select(p => p.Number)], fitHz, f0, fc, maxSteps,
                                  pec, thin, notes, null, null, kernelFiles)
               { Probes = probeNames, WaveTerminals = waveTerminals };
    }

    private static string CylHead(FdtdGridResult grid, FdtdCylinder cyl, Em3dBoundaryKind[] faces, int pml, long maxSteps, double endDb,
                                  double f0, double fc, Em3dMaterial air, double fitHz, bool cellConstant, IReadOnlyList<double>? multiGrid = null)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>\n");
        sb.Append("<!-- Generated by circuitRF for openEMS (brief-em3d-9, brief-em3d-120). Do not edit: circuitRF rewrites this file from the setup.\n");
        sb.Append($"     A cylindrical grid about the {FdtdGrid.AxisName(cyl.Axis)} axis: XLines are ρ (metres), YLines α (radians), ZLines {FdtdGrid.AxisName(cyl.Axis)} (metres).\n");
        sb.Append("     Run by circuitRF once per port, as  openEMS model.xml  in that port's directory. -->\n");
        sb.Append("<openEMS>\n");
        sb.Append($"    <FDTD NumberOfTimesteps=\"{maxSteps.ToString(CultureInfo.InvariantCulture)}\" CylinderCoords=\"1\" endCriteria=\"{R(Math.Pow(10, endDb / 10))}\" " +
                  $"OverSampling=\"{OverSampling}\" TimeStepMethod=\"{TimeStepMethod}\"{CellConstantAttribute(cellConstant)}" +
                  (multiGrid is { Count: > 0 } mg ? $" MultiGrid=\"{string.Join(",", mg.Select(R))}\"" : "") + ">\n");
        sb.Append($"        <Excitation Type=\"0\" f0=\"{R(f0)}\" fc=\"{R(fc)}\" />\n");
        // ρmin is PEC on a pin's surface and ignored on the axis (openEMS's own axis handling); α closes on itself, so its
        // two entries are placeholders openEMS does not apply, written PEC as upstream's own examples write them.
        string[] tokens = ["PEC", BoundaryToken(cyl.RhoMaxKind, pml), "PEC", "PEC",
                           BoundaryToken(faces[2 * cyl.A], pml), BoundaryToken(faces[2 * cyl.A + 1], pml)];
        sb.Append("        <BoundaryCond");
        for (int k = 0; k < 6; k++) sb.Append($" {FaceKeys[k]}=\"{tokens[k]}\"");
        sb.Append(" />\n");
        sb.Append("    </FDTD>\n");
        sb.Append("    <ContinuousStructure CoordSystem=\"1\">\n");
        sb.Append("        <RectilinearGrid DeltaUnit=\"1\" CoordSystem=\"1\">\n");
        foreach (var (tag, a) in new[] { ("XLines", grid.X), ("YLines", grid.Y), ("ZLines", grid.Z) })
            sb.Append($"            <{tag} Qty=\"{a.Lines.Count}\">{string.Join(",", a.Lines.Select(R))}</{tag}>\n");
        sb.Append("        </RectilinearGrid>\n");
        sb.Append($"        <BackgroundMaterial Epsilon=\"{R(air.Epsr)}\" Mue=\"{R(air.Mur)}\" Kappa=\"{R(Kappa(air, air.Epsr, fitHz))}\" Sigma=\"0\" />\n");
        sb.Append("        <ParameterSet />\n");
        sb.Append("        <Properties>\n");
        return sb.ToString();
    }

    // ── brief-sar-8b — patches of the cylinder ───────────────────────────────────────────────────────────────

    /// <summary>A patch's azimuths in the grid's own turn — one span, or two where it crosses the grid's α seam; a full circle is
    /// the grid's whole α range.</summary>
    private static IEnumerable<(double A0, double A1)> AlphaSpans(Em3dCylinderPatch p, FdtdGridResult grid)
    {
        var lines = grid.Y.Lines;
        if (p.FullCircle) { yield return (lines[0], lines[^1]); yield break; }
        double a0 = FdtdGrid.Wrap(p.Alpha0, lines), a1 = a0 + (p.Alpha1 - p.Alpha0);
        if (a1 <= lines[^1] + 1e-12) { yield return (a0, Math.Min(a1, lines[^1])); yield break; }
        yield return (a0, lines[^1]);
        yield return (lines[0], a1 - 2 * Math.PI);
    }

    /// <summary>
    /// A gap on the cylinder as openEMS's lumped element states it: start on the NEGATIVE end, stop on the positive, in (ρ, α, z);
    /// the axis its current runs along (1 α, 2 z) and the sign of stop − start along it — or why it cannot be written. Its radius
    /// and the two edges across its width are written as the grid's own lines (NearestLine), so the source, the element and the
    /// probes span the same edges.
    /// </summary>
    private static (Point3 Start, Point3 Stop, int Along, int Sign, string? Refusal) CylGap(string who, Em3dCylinderGap gap, FdtdGridResult grid)
    {
        var p = gap.Patch;
        var spans = AlphaSpans(p, grid).ToList();
        if (spans.Count != 1)
            return (default, default, 0, 0, $"{who}'s gap crosses the grid's α seam at {G(grid.Y.Lines[0] * 180 / Math.PI)}°, and openEMS's lumped element " +
                                            "is one box: give the grid lines (OpenEms.GridLines) whose circle starts elsewhere, or move the gap.");
        var (a0, a1) = spans[0];
        double r = FdtdGrid.NearestLine(grid.X.Lines, p.Radius);
        double z0 = p.Lo, z1 = p.Hi;
        int along = gap.Along == Em3dCylinderGapAxis.Axial ? 2 : 1;
        if (along == 2) { a0 = FdtdGrid.NearestLine(grid.Y.Lines, a0); a1 = FdtdGrid.NearestLine(grid.Y.Lines, a1); }
        else { z0 = FdtdGrid.NearestLine(grid.Z.Lines, z0); z1 = FdtdGrid.NearestLine(grid.Z.Lines, z1); }
        Point3 lo = new(r, a0, z0), hi = new(r, a1, z1);
        return gap.Sign > 0 ? (lo, hi, along, 1, null) : (With(lo, along, Get(hi, along)), With(hi, along, Get(lo, along)), along, -1, null);
    }

    // ── the operands (R-em3d120-4b) ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Subtract written as its Blank (at its own priority) with each Tool cut out above it (at 2·p + 1): in the kept Tool's
    /// own material (the hole joins that solid's property), or in the background's. Equivalent — and so written — only when
    /// every solid overlapping a Tool and below the Blank in precedence would lose to the kept Tool there anyway; otherwise
    /// the reason, and the caller writes the tessellation. A coaxial cylinder with one coaxial bore through its whole length,
    /// not kept, is a tube: one annulus Box in (ρ, α, z), equivalent whatever else is near.
    /// </summary>
    private static (string? Primitives, string? Why) Operands(Em3dSolid result, Em3dOperands ops, Em3dProblem problem, CylContext ctx,
                                                              Dictionary<string, StringBuilder> holesFor, Dictionary<string, string> backgroundHoles,
                                                              Em3dMaterial air, double fitHz, ref bool reached)
    {
        int pb = ctx.Precedence.Of(result);
        // A tube.
        if (ops is { Blank: Em3dCylinder blank, Tools: [{ Primitive: Em3dCylinder bore, KeptAs: null }] } &&
            ctx.Coaxial(blank) && ctx.Coaxial(bore) && bore.Radius < blank.Radius && ctx.AxialRange(bore) is var (t0, t1) &&
            ctx.AxialRange(blank) is var (b0, b1) && t0 <= b0 + ctx.Tol && t1 >= b1 - ctx.Tol)
            return (ctx.NativeAnnulus(bore.Radius, blank.Radius, b0, b1, 2 * pb, ref reached), null);

        var others = problem.Solids.Where(s => s.Name != result.Name).ToList();
        var holes = new List<(Em3dOperandTool Tool, Em3dSolid? Kept)>();
        foreach (var tool in ops.Tools)
        {
            if (tool.Primitive is not Em3dCylinder tc || !ctx.Coaxial(tc))
                return (null, $"its Tool '{tool.Name}' is not a cylinder coaxial with the grid's axis, which is what a cut by priority can state exactly.");
            var kept = tool.KeptAs is { } kn ? others.FirstOrDefault(s => s.Name == kn) : null;
            if (tool.KeptAs is not null && kept is null)
                return (null, $"its kept Tool '{tool.Name}' is not in the problem.");
            if (kept is not null && ctx.Precedence.Of(kept) > pb) continue;     // the kept Tool already wins over the Blank
            var box = Em3dProblem.Bounds(tool.Primitive);
            foreach (var x in others.Where(s => s != kept))
            {
                if (!Overlap(box, Em3dProblem.Bounds(x.Primitive), ctx.Tol)) continue;
                int px = ctx.Precedence.Of(x);
                if (px > pb) continue;
                if (kept is not null && ctx.Precedence.Of(kept) > px) continue;
                return (null, $"'{x.Name}' overlaps its Tool '{tool.Name}' and lies below '{result.Name}' in precedence, so cutting the Tool " +
                              $"out by priority would put {(kept is null ? "the background" : $"'{kept.Name}'")} where '{x.Name}' is.");
            }
            foreach (var sh in problem.Sheets)
                if (Overlap(box, sh.WorldBounds(), ctx.Tol) && ctx.Precedence.Of(sh) < pb && !(kept is not null && ctx.Precedence.Of(kept) > ctx.Precedence.Of(sh)))
                    return (null, $"sheet '{sh.Name}' overlaps its Tool '{tool.Name}' below '{result.Name}' in precedence.");
            foreach (var (other, otherKept) in holes)
                if (Overlap(box, Em3dProblem.Bounds(other.Primitive), ctx.Tol) && otherKept?.Name != kept?.Name)
                    return (null, $"its Tools '{other.Name}' and '{tool.Name}' overlap and would be cut out in different materials.");
            holes.Add((tool, kept));
        }

        string blankXml = CylPrimitive(ops.Blank, 2 * pb, ctx, result.Name, [], ref reached);
        var background = new StringBuilder();
        foreach (var (tool, kept) in holes)
        {
            // The hole stops a hair short of its own surface (FaceOffsetCells of the local cell), so the grid nodes ON the bore stay
            // the Blank's: a hole at the higher priority that included them would move the metal's surface out by a cell (measured:
            // the housing read 51.3 Ω and ε_eff 2.17, its surface on the next radial line, where the same coax drawn as a holed
            // prism read 50.39 Ω and 2.10). Only an end that lies inside the Blank — a bore's floor — is pulled in; a through end
            // is carried as far as the Blank's own.
            bool r2 = false;
            var c = (Em3dCylinder)tool.Primitive;
            var (h0, h1) = ctx.AxialRange(c);
            var (k0, k1) = BlankAxial(ops.Blank, ctx);
            double dz0 = FaceOffsetCells * ctx.AxialCellAt(h0), dz1 = FaceOffsetCells * ctx.AxialCellAt(h1);
            string hole = ctx.NativeAnnulus(0, c.Radius - FaceOffsetCells * ctx.RadialCellAt(c.Radius),
                                            h0 > k0 + ctx.Tol ? h0 + dz0 : h0, h1 < k1 - ctx.Tol ? h1 - dz1 : h1, 2 * pb + 1, ref r2);
            if (kept is not null)
            {
                if (!holesFor.TryGetValue(kept.Name, out var sb)) holesFor[kept.Name] = sb = new StringBuilder();
                sb.Append(hole);
                continue;
            }
            var bg = new StringBuilder();
            Open(bg, "Material", 0, $"{result.Name}:{tool.Name}", Colors.Dielectric, " Isotropy=\"1\"");
            AppendPrimitives(bg, hole);
            bg.Append("                ").Append(MaterialProperty(air, fitHz)).Append('\n');
            bg.Append("                <Weight Epsilon=\"1,1,1\" Mue=\"1,1,1\" Kappa=\"1,1,1\" Sigma=\"1,1,1\" Density=\"1\" />\n");
            bg.Append("            </Material>\n");
            background.Append(bg.ToString().Replace(" ID=\"0\"", " ID=\"{ID}\""));
        }
        if (background.Length > 0) backgroundHoles[result.Name] = background.ToString();
        return (blankXml, null);
    }

    /// <summary>The Blank's stretch along the grid's axis.</summary>
    private static (double Lo, double Hi) BlankAxial(Em3dPrimitive blank, CylContext ctx)
    {
        var b = Em3dProblem.Bounds(blank);
        return ctx.Cyl.A switch { 0 => (b.X0, b.X1), 1 => (b.Y0, b.Y1), _ => (b.Z0, b.Z1) };
    }

    private static bool Overlap((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a,
                                (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, double tol)
        => Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0) > tol && Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0) > tol &&
           Math.Min(a.Z1, b.Z1) - Math.Max(a.Z0, b.Z0) > tol;

    // ── primitives ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One primitive at <paramref name="pr"/> in openEMS's frame on this grid (R-em3d120-1a's table).</summary>
    private static string CylPrimitive(Em3dPrimitive prim, int pr, CylContext ctx, string name, List<string> thin, ref bool reached)
    {
        const string C0 = " CoordSystem=\"0\"";
        switch (prim)
        {
            case Em3dCylinder c when ctx.Coaxial(c):
            {
                var (z0, z1) = ctx.AxialRange(c);
                return ctx.NativeAnnulus(0, c.Radius, z0, z1, pr, ref reached);
            }
            case Em3dBox b:
                return CylBox(pr, ctx.Map(b.Min, ref reached), ctx.Map(b.Max, ref reached));
            case Em3dPolyhedron ph when AxisBox(ph) is var (lo, hi):
                return CylBox(pr, ctx.Map(lo, ref reached), ctx.Map(hi, ref reached));
            case Em3dPolyhedron ph when AxisPrism(ph) is { } prism:
            {
                var (axis, bottom, top, outline, holes) = prism;
                return CylLinPoly(pr, axis, bottom, top, Keyhole(outline, holes), ctx, ref reached);
            }
            case Em3dExtrudedPolygon e:
                return CylLinPoly(pr, 2, e.ZBottom, e.ZTop, Keyhole(e.Outline, e.Holes), ctx, ref reached);
            case Em3dCylinder c:
                return $"                    <Cylinder Priority=\"{pr}\"{C0} Radius=\"{R(c.Radius)}\">\n" +
                       $"                        {P("P1", ctx.Map(c.AxisStart, ref reached))}\n" +
                       $"                        {P("P2", ctx.Map(c.AxisEnd, ref reached))}\n" +
                       "                    </Cylinder>\n";
            case Em3dSphere sp:
                return $"                    <Sphere Priority=\"{pr}\"{C0} Radius=\"{R(sp.Radius)}\">\n" +
                       $"                        {P("Center", ctx.ToCsx(sp.Center))}\n" +
                       "                    </Sphere>\n";
            case Em3dSweep w when ctx.SubCell(w):
            {
                thin.Add(name);
                var sb = new StringBuilder();
                sb.Append($"                    <Curve Priority=\"{pr}\"{C0}>\n");
                foreach (var q0 in w.Path) { var q = ctx.ToCsx(q0); sb.Append($"                        <Vertex X=\"{R(q.X)}\" Y=\"{R(q.Y)}\" Z=\"{R(q.Z)}\" />\n"); }
                sb.Append("                    </Curve>\n");
                return sb.ToString();
            }
            case Em3dSweep { Section: Em3dSection.Circle } w:
            {
                var sb = new StringBuilder();
                sb.Append($"                    <Wire Priority=\"{pr}\"{C0} WireRadius=\"{R(w.Diameter / 2)}\">\n");
                foreach (var q0 in w.Path) { var q = ctx.ToCsx(q0); sb.Append($"                        <Vertex X=\"{R(q.X)}\" Y=\"{R(q.Y)}\" Z=\"{R(q.Z)}\" />\n"); }
                sb.Append("                    </Wire>\n");
                return sb.ToString();
            }
            default:
            {
                var mesh = Em3dTessellation.Of(new Em3dSolid(name, "", Em3dRole.Dielectric, prim, 0));
                var sb = new StringBuilder();
                sb.Append($"                    <Polyhedron Priority=\"{pr}\"{C0}>\n");
                foreach (var v0 in mesh.Vertices)
                {
                    var v = ctx.Map(v0, ref reached);
                    sb.Append($"                        <Vertex>{R(v.X)},{R(v.Y)},{R(v.Z)}</Vertex>\n");
                }
                foreach (var t in mesh.Triangles) sb.Append($"                        <Face>{t.A},{t.B},{t.C}</Face>\n");
                sb.Append("                    </Polyhedron>\n");
                return sb.ToString();
            }
        }
    }

    private static string CylBox(int pr, Point3 a, Point3 b)
        => $"                    <Box Priority=\"{pr}\" CoordSystem=\"0\">\n" +
           $"                        {P("P1", new Point3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)))}\n" +
           $"                        {P("P2", new Point3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)))}\n" +
           "                    </Box>\n";

    /// <summary>A right prism along world axis <paramref name="axis"/>: its ring is in that axis' cyclic in-plane axes, which the
    /// rotation into openEMS's frame keeps, so only each coordinate's shift changes.</summary>
    private static string CylLinPoly(int pr, int axis, double bottom, double top, List<Point2> ring, CylContext ctx, ref bool reached)
    {
        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3, n = ctx.Cyl.CsxAxis(axis);
        double lo = ctx.OutWorld(bottom, axis, ref reached) - ctx.Cyl.Shift(axis), hi = ctx.OutWorld(top, axis, ref reached) - ctx.Cyl.Shift(axis);
        var sb = new StringBuilder();
        sb.Append($"                    <LinPoly Priority=\"{pr}\" CoordSystem=\"0\" Elevation=\"{R(lo)}\" NormDir=\"{n}\" QtyVertices=\"{ring.Count}\" Length=\"{R(hi - lo)}\">\n");
        foreach (var q in ring)
            sb.Append($"                        <Vertex X1=\"{R(ctx.OutWorld(q.X, a1, ref reached) - ctx.Cyl.Shift(a1))}\" " +
                      $"X2=\"{R(ctx.OutWorld(q.Y, a2, ref reached) - ctx.Cyl.Shift(a2))}\" />\n");
        sb.Append("                    </LinPoly>\n");
        return sb.ToString();
    }

    private static string CylSheet(Em3dSheet sh, CylContext ctx)
    {
        int n = sh.Frame?.NormalAxis ?? 2;
        int a1 = (n + 1) % 3, a2 = (n + 2) % 3;
        var ring = Keyhole(sh.Outline, sh.Holes).Select(q => sh.Frame is null ? new Point3(q.X, q.Y, sh.Z) : sh.World(q)).ToList();
        double elevation = Get(ring[0], n) - ctx.Cyl.Shift(n);
        var sb = new StringBuilder();
        sb.Append($"                    <Polygon Priority=\"{ctx.P(sh)}\" CoordSystem=\"0\" Elevation=\"{R(elevation)}\" NormDir=\"{ctx.Cyl.CsxAxis(n)}\" QtyVertices=\"{ring.Count}\">\n");
        foreach (var q in ring)
            sb.Append($"                        <Vertex X1=\"{R(Get(q, a1) - ctx.Cyl.Shift(a1))}\" X2=\"{R(Get(q, a2) - ctx.Cyl.Shift(a2))}\" />\n");
        sb.Append("                    </Polygon>\n");
        return sb.ToString();
    }

    /// <summary>A kernel solid as a PLY in openEMS's frame, tessellated at a quarter of the smallest cell it reaches.</summary>
    private static (Em3dKernelFile? File, string? Refusal) CylKernelPly(Em3dSolid s, Em3dShapeSolid k, CylContext ctx)
    {
        double deflection = 0.25 * ctx.SmallestCellIn(k.Bounds());
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
        var index = new Dictionary<Point3, int>();
        var welded = new List<Point3>();
        var map = new int[mesh.Vertices.Count];
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var v = mesh.Vertices[i];
            if (!index.TryGetValue(v, out int w)) { index[v] = w = welded.Count; welded.Add(v); }
            map[i] = w;
        }
        var ply = new StringBuilder();
        ply.Append("ply\nformat ascii 1.0\n");
        ply.Append($"comment circuitRF brief-em3d-120: kernel solid {k.BrepHash[..Math.Min(16, k.BrepHash.Length)]}, metres, openEMS's frame on a cylindrical grid about {FdtdGrid.AxisName(ctx.Cyl.Axis)}, linear deflection {R(deflection)}\n");
        ply.Append($"element vertex {welded.Count}\nproperty float x\nproperty float y\nproperty float z\n");
        ply.Append($"element face {mesh.Triangles.Count}\nproperty list uchar int vertex_indices\nend_header\n");
        bool reached = false;
        foreach (var v in welded) { var q = ctx.Map(v, ref reached); ply.Append(R(q.X)).Append(' ').Append(R(q.Y)).Append(' ').Append(R(q.Z)).Append('\n'); }
        foreach (var t in mesh.Triangles) ply.Append("3 ").Append(map[t.A]).Append(' ').Append(map[t.B]).Append(' ').Append(map[t.C]).Append('\n');
        string name = $"kernel-{k.BrepHash[..Math.Min(16, k.BrepHash.Length)]}-cyl-{R(Math.Round(deflection * 1e6, 6))}.ply";
        return (new Em3dKernelFile(name, Encoding.UTF8.GetBytes(ply.ToString()), s.Name, deflection), null);
    }

    // ── the coaxial terminal's probes (R-em3d120-4d) ─────────────────────────────────────────────────────────

    private static OpenEmsProbeNames CylWaveProbes(StringBuilder props, ref int id, FdtdTerminalElements t)
    {
        int k = t.Port;
        var names = new List<string>[3];
        for (int plane = 0; plane < 3; plane++)
        {
            var q = t.Voltage[plane];
            string name = VoltageProbe(k, PlaneNames[plane], q.Suffix);
            names[plane] = [name];
            OpenProbe(props, id++, name, type: 0, weight: -1, normDir: -1);
            AppendPrimitives(props, Box(0, q.From, q.To));
            props.Append("            </ProbeBox>\n");
        }
        var (r0, a0, r1, a1) = t.CurrentBox;
        foreach (var (plane, at) in new[] { ("ia", t.IaAtM), ("ib", t.IbAtM) })
        {
            OpenProbe(props, id++, CurrentProbe(k, plane), type: 1, weight: t.CurrentWeight, normDir: 2);
            AppendPrimitives(props, Box(0, new Point3(r0, a0, at), new Point3(r1, a1, at)));
            props.Append("            </ProbeBox>\n");
        }
        return new OpenEmsProbeNames(k, names[1], [CurrentProbe(k, "ia"), CurrentProbe(k, "ib")])
        {
            Ua = names[0], Uc = names[2], SpacingM = t.PlaneSpacingM, Terminal = t.Terminal.Label,
            Group = t.Terminal.Port.FaceGroup is null ? null : t.Terminal.Port.FaceGroupLabel,
        };
    }

    // ── the grid, as the primitives see it ───────────────────────────────────────────────────────────────────

    private sealed class CylContext
    {
        private readonly FdtdGridResult _grid;
        private readonly double _boxLo, _boxHi, _outerLo, _outerHi;
        private readonly bool _absorbLo, _absorbHi;
        public FdtdCylinder Cyl { get; }
        public Em3dPrecedence Precedence { get; }
        public int Pml { get; }
        public double Tol { get; }

        public CylContext(Em3dProblem problem, FdtdGridResult grid, FdtdCylinder cyl, Em3dBoundaryKind[] faces, int pmlCells)
        {
            _grid = grid;
            Cyl = cyl;
            Precedence = Em3dPrecedence.Of(problem);
            var b = problem.Boundary;
            _boxLo = Get(b.Min, cyl.A);
            _boxHi = Get(b.Max, cyl.A);
            _outerLo = grid.Z.Lines[0];
            _outerHi = grid.Z.Lines[^1];
            Pml = pmlCells;
            _absorbLo = pmlCells > 0 && faces[2 * cyl.A] == Em3dBoundaryKind.Absorbing;
            _absorbHi = pmlCells > 0 && faces[2 * cyl.A + 1] == Em3dBoundaryKind.Absorbing;
            Tol = 1e-9 * Math.Max(1e-6, Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z)));
        }

        /// <summary>A solid's priority on this grid: doubled, so a Subtract's hole fits at 2·p + 1.</summary>
        public int P(Em3dSolid s) => 2 * Precedence.Of(s);
        public int P(Em3dSheet sh) => 2 * Precedence.Of(sh);
        public int P(Em3dCylinderSheet sh) => 2 * Precedence.Of(sh);

        public bool Coaxial(Em3dCylinder c) => FdtdGrid.IsCoaxial(c, Cyl.A, Cyl.U, Cyl.V, Get(Cyl.Origin, Cyl.U), Get(Cyl.Origin, Cyl.V), Tol);

        public (double Lo, double Hi) AxialRange(Em3dCylinder c)
        {
            double s0 = Get(c.AxisStart, Cyl.A), s1 = Get(c.AxisEnd, Cyl.A);
            return (Math.Min(s0, s1), Math.Max(s0, s1));
        }

        /// <summary>A world coordinate on axis <paramref name="axis"/>, carried through an absorbing END face to the grid's edge.</summary>
        public double OutWorld(double v, int axis, ref bool reached)
        {
            if (axis != Cyl.A) return v;
            if (_absorbLo && Math.Abs(v - _boxLo) <= Tol && _outerLo < v) { reached = true; return _outerLo; }
            if (_absorbHi && Math.Abs(v - _boxHi) <= Tol && _outerHi > v) { reached = true; return _outerHi; }
            return v;
        }

        public Point3 ToCsx(Point3 w) => Cyl.ToCsx(w);

        public Point3 Map(Point3 w, ref bool reached)
        {
            var q = Cyl.ToCsx(w);
            return q with { Z = OutWorld(q.Z, Cyl.A, ref reached) };
        }

        /// <summary>A coaxial annulus (a solid cylinder from ρ = 0) as a Box in the grid's own (ρ, α, z): exact on its circles.</summary>
        public string NativeAnnulus(double r0, double r1, double z0, double z1, int pr, ref bool reached)
        {
            double a0 = _grid.Y.Lines[0], a1 = _grid.Y.Lines[^1];
            return $"                    <Box Priority=\"{pr}\">\n" +
                   $"                        {CsxcadWriter.P("P1", new Point3(r0, a0, OutWorld(z0, Cyl.A, ref reached)))}\n" +
                   $"                        {CsxcadWriter.P("P2", new Point3(r1, a1, OutWorld(z1, Cyl.A, ref reached)))}\n" +
                   "                    </Box>\n";
        }

        /// <summary>R-em3d9-2c on this grid: a wire is below the grid where its diameter is smaller than the coarsest side of the
        /// cell holding a vertex of its path — Δρ, the arc ρ·Δα there, or Δz.</summary>
        public bool SubCell(Em3dSweep w)
        {
            foreach (var q in w.Path)
            {
                double r = Cyl.RadiusOf(q);
                double cell = Math.Max(Cell(_grid.X.Lines, r), Math.Max(Math.Max(r, Cyl.SmallestArcAtM) * 2 * Math.PI / Cyl.AzimuthCells,
                                                                    Cell(_grid.Z.Lines, Get(q, Cyl.A))));
                if (w.Diameter < cell) return true;
            }
            return false;
        }

        /// <summary>The smallest cell side a box reaches: the radial and axial cells inside it, and the arc at its nearest radius.</summary>
        public double SmallestCellIn((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
        {
            double[] lo = [b.X0, b.Y0, b.Z0], hi = [b.X1, b.Y1, b.Z1];
            double ou = Get(Cyl.Origin, Cyl.U), ov = Get(Cyl.Origin, Cyl.V);
            double nu = Math.Max(0, Math.Max(lo[Cyl.U] - ou, ou - hi[Cyl.U])), nv = Math.Max(0, Math.Max(lo[Cyl.V] - ov, ov - hi[Cyl.V]));
            double fu = Math.Max(Math.Abs(lo[Cyl.U] - ou), Math.Abs(hi[Cyl.U] - ou)), fv = Math.Max(Math.Abs(lo[Cyl.V] - ov), Math.Abs(hi[Cyl.V] - ov));
            double rIn = Math.Max(Math.Sqrt(nu * nu + nv * nv), Cyl.SmallestArcAtM), rOut = Math.Sqrt(fu * fu + fv * fv);
            return Math.Min(Em3dFidelity.SmallestCell(_grid.X.Lines, rIn, Math.Max(rIn, rOut)),
                   Math.Min(Em3dFidelity.SmallestCell(_grid.Z.Lines, lo[Cyl.A], hi[Cyl.A]), rIn * 2 * Math.PI / Cyl.AzimuthCells));
        }

        public double RadialCellAt(double r) => Cell(_grid.X.Lines, r);
        public double AxialCellAt(double z) => Cell(_grid.Z.Lines, z);

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
}
