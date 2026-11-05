using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>explain amp.cem</c> on a 3D setup (brief-em3d-5 R-em3d5-3): the solids, materials, ports, air
/// box and wires of the problem the backend would receive, and the size of the run, IN ADDITION to
/// what a planar <c>.cem</c> reports (its two walks, the solve region, the return plane).
///
/// <para><b>Everything reported is what the generator resolved</b> — the problem itself and the
/// provenance it kept beside it — never a rule restated here. A restated rule is one that can
/// disagree with the run, which is the one thing a caller asks this verb to rule out.</para>
///
/// <para><b>It starts no process and writes nothing</b> (R-em3d5-3d). The size is arithmetic on the
/// resolved problem; no mesher is run to count anything.</para>
/// </summary>
internal static class ExplainEm3d
{
    /// <summary>The report's rows for <paramref name="src"/>, which must be a 3D setup.</summary>
    /// <param name="device">brief-em3d-136 — the device a run would use (<c>explain --device</c>, as <c>em --device</c>).</param>
    /// <param name="memoryLimitBytes">brief-emi-10 — the limit a run would be given (<c>explain --memory-limit</c>, as
    /// <c>em --memory-limit</c>); null for this machine's memory.</param>
    public static ExplainEm3dJson Build(Em3dSetupSource src, PalaceDevice device = PalaceDevice.Cpu, long? memoryLimitBytes = null)
    {
        // EMB-5 R-emb5-3 — sizing (an openEMS grid, a SAR plan) is the report's own long step; a cancelled call stops here.
        if (RunHost.Control is { } control) { control.ThrowIfCancellationRequested(); control.BeginStage("sizing the 3D problem for each solver"); }
        var r = BuildCore(src, device);
        RunHost.Control?.ThrowIfCancellationRequested();
        if (Device(src.Setup, device, r.Solvers) is { } d) r = r with { Device = d };
        if (src.Generated is { WantsDc: true }) r = r with { DcRow = DcRow() };
        if (!src.Setup.Solver3D.IsNone && !src.Setup.IsThermal)
            r = r with { Limits = Em3dMemoryLimit.Describe(MachineMemory.PhysicalBytes, memoryLimitBytes,
                                                           System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(src.Path)) ?? ".") };
        return Compare(src.Setup, r.Solvers) is { } c ? r with { Compare = c } : r;
    }

    /// <summary>brief-emdc-2 R-emdc2-5 — how a run answers the sweep's 0 Hz point (Em3dDcRow's sentence, the generator's own
    /// note), and the Gmsh the conduction solve would mesh with, or why a run would write no 0 Hz row.</summary>
    private static string DcRow()
    {
        var gmsh = SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh));
        return gmsh.Proceeds
            ? $"{Em3dDcRow.ZeroHzNote} It meshes the conductors with Gmsh {gmsh.Installation!.DescribeVersion()}, found at {gmsh.Installation.Path}."
            : $"{Em3dDcRow.ZeroHzNote} Gmsh was not found, so a run would write no 0 Hz row: {gmsh.Refusal}";
    }

    /// <summary>brief-emi-6 R-emi6-5 — a comparison's reference and, per solver, whether a run would start it: its own refusal of
    /// the setup first, then the first of its programs a run would stop at. Null for one solver.</summary>
    private static Em3dCompareJson? Compare(EmSetup setup, IReadOnlyList<Em3dSolverJson> programs)
    {
        if (!setup.Solver3D.IsCompare) return null;
        return new Em3dCompareJson(Em3dSolvers.Get(setup.Solver3D.Reference).DisplayName,
            [.. Em3dSolvers.Of(setup.Solver3D).Select(d =>
            {
                string? why = d.Refusal(setup)
                    ?? programs.FirstOrDefault(p => d.Tools.Any(t => SolverDiscovery.For(t).Name == p.Tool) && !p.Proceeds)?.Refusal;
                return new Em3dCompareSolverJson(d.DisplayName, why is null, why);
            })]);
    }

    private static ExplainEm3dJson BuildCore(Em3dSetupSource src, PalaceDevice device)
    {
        var setup = src.Setup;
        string solver = setup.Solver3D.IsNone ? "none" : Em3dSolvers.Names(Em3dSolvers.Of(setup.Solver3D));
        var notes    = src.Generated?.Notes ?? [];
        var warnings = src.Generated?.Warnings ?? [];
        var solvers  = Solvers(setup.Solver3D, setup, device);

        if (src.Generated?.Problem is not { } p)
            return new ExplainEm3dJson(solver, "m", 1.0, [], null, [], [], [], [], null, Size(null, setup, default), solvers, notes, warnings,
                                       src.Refusal ?? "the 3D problem could not be built.");
        var g = src.Generated;

        var guidance = Em3dSolverGuidance.For(p)
            .Select(x => new Em3dGuidanceJson(x.Row, $"{Em3dSolverDescriptor.MethodNameOf(x.Favours)} ({Em3dSolvers.NamesOf(x.Favours)})",
                                              x.Sentence))
            .ToList();

        // ── temperature: the conductors' σ at it, in the order the problem first uses each metal ──
        var byName = p.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var metals = p.Solids.Where(s => s.Role == Em3dRole.Conductor).OrderBy(s => s.Order).Select(s => (s.Order, s.Material))
                      .Concat(p.Sheets.Select(s => (s.Order, s.Material)))
                      .OrderBy(x => x.Order).Select(x => x.Material).Distinct(StringComparer.Ordinal)
                      .Select(m => new Em3dConductivityJson(m, byName[m].SigmaSm)).ToList();
        var temperature = new Em3dTemperatureJson(
            p.OperatingTempC, setup.OperatingTempC is null ? "default" : "field", metals, g.NoAlpha, g.UnknownTemperature);

        // brief-sar-2 R-sar2-7 — which problem materials came from a frequency-dependent record, and where they were evaluated:
        // the one frequency C3dProblemAssembly evaluated them at, by the same function.
        var dispersion = new Dictionary<string, string>(StringComparer.Ordinal);
        if (src.Elaboration is { } el)
            foreach (var s in p.Solids)
                if (el.SolidMaterials.TryGetValue(s.Name, out var rec) && rec.Technology.FindMaterial(rec.Material) is { } tm
                    && MaterialDispersion.IsDispersive(tm))
                    dispersion[s.Material] = tm.ColeCole is not null ? "Cole-Cole" : "table";
        // brief-sar-6 — a setup that requests SAR evaluates them at its (first) SAR frequency instead
        var sarSettings = setup.RequestsSar ? SarSettings.Resolve(setup.Sar!) : null;
        double evaluatedAt = sarSettings is { FrequenciesHz: [var sarHz, ..] } ? sarHz : C3dProblemAssembly.MaterialFrequency(p.Frequency);
        var materials = p.Materials.Select(m => new Em3dMaterialJson(
            m.Name, m.Epsr, m.EpsrTensor, m.TanD, m.Mur, m.SigmaSm,
            g.MaterialSources.TryGetValue(m.Name, out var from) ? from : "unrecorded",
            dispersion.GetValueOrDefault(m.Name), dispersion.ContainsKey(m.Name) ? evaluatedAt : null)).ToList();

        var solids = p.Solids.Select(s =>
            {
                var b = Em3dProblem.Bounds(s.Primitive);
                return (s.Order, Row: new Em3dSolidJson(s.Name, "solid", Role(s.Role), s.Material, Primitive(s.Primitive),
                                                        s.Order, b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1, null, null));
            })
            .Concat(p.Sheets.Select(sh =>
            {
                var (x0, y0, z0, x1, y1, z1) = sh.WorldBounds();
                string? reason = g.Origins.TryGetValue(sh.Name, out var o) ? o.SheetReason : null;
                return (sh.Order, Row: new Em3dSolidJson(sh.Name, "sheet", "conductor", sh.Material, "sheet", sh.Order,
                                                         x0, y0, z0, x1, y1, z1, reason, sh.ThicknessM));
            }))
            .OrderBy(x => x.Order).Select(x => x.Row).ToList();

        var wires = g.Wires.Select(w => new Em3dWireJson(
            w.Name, w.Material, w.Section == Em3dSection.Hexagon ? "hexagon" : "circle", w.DiameterM,
            End(w.Start), End(w.End),
            w.Process.FootLength.Nm * 1e-9, Level(w.Process.FootLength.Source),
            w.Process.BallDiameter.Nm * 1e-9, Level(w.Process.BallDiameter.Source),
            w.Process.BallHeight.Nm * 1e-9, Level(w.Process.BallHeight.Source),
            w.AssemblyLoopHeightM, w.WBondLoopHeightM)).ToList();

        // R-em3d4-2e — the section is not corrected for, so say what it costs, beside the wire rows.
        if (g.Wires.Any(w => w.Section == Em3dSection.Hexagon))
            notes = [.. notes,
                "A hexagonal wire dissipates more than the round wire of equal perimeter that kernel W models — " +
                "+1.7 % at 1 GHz, +5.0 % at 10 GHz, +2.5 % at 40 GHz, measured with Palace when circuitRF's 3D " +
                "solvers were validated (the 10 GHz figure is 5 ± 3 %). The 3D model does not " +
                "correct for it, so that much of a loss difference against kernel W is the section."];

        var ports = p.Ports.Select(q => new Em3dPortJson(
            q.Number, q.Name, q.PositiveObject, q.NegativeObject, q.Z0.Real, q.Z0.Imaginary,
            V(q.Min), V(q.Max), V(q.ReferencePlane.Origin), V(q.ReferencePlane.Normal), q.ReferencePlane.ShiftM)
        {
            // brief-em3d-23 R-em3d23-2c — a wave port says where its reference plane is.
            Kind = q.Kind == Em3dPortKind.Wave ? "wave" : null,
            // brief-dfr28-4 R-dfr28-4-5 — the layer it returns through, and whether that was walked or stated.
            Return = g.PortReturns.TryGetValue(q.Number, out var ret)
                ? ret.Coplanar ? $"{ret.Layer}, beside the line on its own layer"     // brief-dfr34-1 — Return = Same layer
                  : $"{ret.Layer}{(ret.Floor ? " (the air box's floor)" : "")}, {(ret.Above ? "above" : "below")}" : null,
            ReturnDecidedBy = ret?.DecidedBy,
            ReferencePlane = q.Kind != Em3dPortKind.Wave ? null
                : $"on the air box's {p.FaceOf(q.Min, q.Max)} face" + (q.ReferencePlane.ShiftM > 0
                    ? $", moved {(q.ReferencePlane.ShiftM * 1e6).ToString("G6", CultureInfo.InvariantCulture)} µm into the structure by the port's Offset (both solvers measure there)"
                    : " (Offset 0: the S-parameters are referred to the face itself)"),
        }).ToList();

        // brief-dfr35-1 R-dfr35-1-11 — the openEMS grid is what an uncropped board's explain spent its time on. A setup openEMS runs always
        // builds it; another reads it only as a comparison, so past a whole board's size it says so rather than taking minutes.
        int outlinePoints = OutlinePoints(p);
        var grid = setup.Solver3D.Runs(OpenEmsSolver.Id) || outlinePoints <= OpenEmsGridComparisonPoints
            ? Grid(p, setup)
            : (null, $"this setup does not run on openEMS and its outlines hold {outlinePoints:N0} points, so its grid was not built. Set the solver to openEMS to size it.");
        var box = p.Boundary;
        var stated = setup.AirBox;
        // Designer feedback round 11 — every face says how far it stands from the content, and where that distance came from:
        // a face that stated only its boundary used to read "setup", which hid that its padding was the default's.
        var (contentLo, contentHi) = Em3dGenerator.ContentBounds(p);
        var defaults = Em3dGenerator.DefaultFacePadding(setup, p.Frequency.StartHz,
            Math.Max(Math.Max(contentHi.X - contentLo.X, contentHi.Y - contentLo.Y), contentHi.Z - contentLo.Z), p.PaddingHeightM,
            p.PaddingHeightBelowM);
        double Span(string face) => face[0] switch
        {
            'x' => contentHi.X - contentLo.X, 'y' => contentHi.Y - contentLo.Y, _ => contentHi.Z - contentLo.Z,
        };
        string Mm(double m) => (m * 1e3).ToString("G4", CultureInfo.InvariantCulture) + " mm";
        Em3dFaceJson Face(string name, Em3dBoundaryKind kind, EmAirBoxFace? face)
        {
            double at = name switch
            {
                "xmin" => contentLo.X - box.Min.X, "xmax" => box.Max.X - contentHi.X, "ymin" => contentLo.Y - box.Min.Y,
                "ymax" => box.Max.Y - contentHi.Y, "zmin" => contentLo.Z - box.Min.Z, _ => box.Max.Z - contentHi.Z,
            };
            return new(name, Boundary(kind),
                face?.PaddingPercent is { } pct ? $"setup: {Mm(at)}, {pct.ToString("G6", CultureInfo.InvariantCulture)} % of the content's extent"
                : face?.PaddingUm is not null ? $"setup: {Mm(at)}"
                : name == "zmin" && kind == Em3dBoundaryKind.Pec ? "floor"
                : Math.Abs(at - defaults.For(name, Span(name))) <= 1e-6 * Math.Max(defaults.For(name, Span(name)), 1e-3) ? $"default: {Mm(at)}, {defaults.Basis(name, Span(name))}"
                : Mm(at));
        }
        var airBox = new Em3dAirBoxJson(V(box.Min), V(box.Max),
        [
            Face("xmin", box.Faces.XMin, stated?.XMin), Face("xmax", box.Faces.XMax, stated?.XMax),
            Face("ymin", box.Faces.YMin, stated?.YMin), Face("ymax", box.Faces.YMax, stated?.YMax),
            Face("zmin", box.Faces.ZMin, stated?.ZMin), Face("zmax", box.Faces.ZMax, stated?.ZMax),
        ], Enlargements(grid.Grid));

        return new ExplainEm3dJson(solver, "m", 1.0, guidance, temperature, materials, solids, wires, ports, airBox,
                                   Size(p, setup, grid), solvers, notes, warnings, null)
        {
            Static = Static(p, setup),
            OpenEmsWavePorts = setup.Solver3D.Runs(OpenEmsSolver.Id) ? WavePortLines(p, grid.Grid) : null,
            PalaceTerminalPorts = setup.Solver3D.Runs(PalaceSolver.Id) ? PalaceTerminalLines(p, setup.Solver3D) : null,
            Emerge = setup.Solver3D.Runs(EmergeSolver.Id) ? Emerge(p, setup) : null,
            Eigenmode = p.Type != Em3dProblemType.Eigenmode ? null
                : new Em3dEigenmodeJson(p.EigenmodeCount, setup.Eigenmode?.Count is null ? "default" : "field",
                                        p.EigenmodeTargetHz, setup.Eigenmode?.TargetGHz is null ? "default (the sweep's start)" : "field"),
            Sar = sarSettings is null ? null : Sar(sarSettings, p, g, grid.Grid, setup.Sar!),
        };
    }

    /// <summary>brief-sar-6 R-sar6-8 — the Sar group as a run resolves it (Em3dSar.Resolve), with its dump box on the grid.</summary>
    private static Em3dSarJson Sar(SarSettings s, Em3dProblem p, Em3dGenerationResult g, FdtdGridResult? grid, CemSar stated)
    {
        // brief-sar-8b — on a cylindrical grid, the run's own rule: local SAR and B1 only
        if (grid?.Cylinder is not null)
        {
            var (onCylinder, cylinderRefusal) = Em3dSar.OnCylindricalGrid(s, stated);
            if (onCylinder is null)
                return new Em3dSarJson(s.FrequenciesHz, s.MassesGrams, s.Method.ToString(), "", [], s.B1, s.B0AxisName, null, s.CrossCheck, [],
                                       null, null, cylinderRefusal) { PerPortFields = s.PerPortFields };
            s = onCylinder;
        }
        var (plan, refusal) = Em3dSar.Resolve(s, p, g.Phantoms);
        if (plan is null)
            return new Em3dSarJson(s.FrequenciesHz, s.MassesGrams, s.Method.ToString(), "", [], s.B1, s.B0AxisName, null, s.CrossCheck, [],
                                   null, null, refusal) { PerPortFields = s.PerPortFields };
        var materials = p.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var tissue = p.Solids.Where(x => plan.Densities.ContainsKey(x.Name)).OrderBy(x => x.Order)
                      .Select(x => new Em3dTissueJson(x.Name, x.Material, plan.Densities[x.Name], materials[x.Material].Epsr, materials[x.Material].SigmaSm))
                      .ToList();
        long? cells = grid is not null ? Em3dSar.DumpCells(plan, grid) : null;
        return new Em3dSarJson(s.FrequenciesHz, s.MassesGrams, s.Method.ToString(), plan.RegionName, plan.Region, s.B1, s.B0AxisName,
                               s.B1 ? plan.B1RegionName : null, s.CrossCheck, tissue, cells,
                               cells is { } c ? Em3dSar.EstimateBytes(plan, c, p.Ports.Count, grid?.Cylinder is not null) : null, null) { PerPortFields = s.PerPortFields };
    }

    /// <summary>brief-emi-4 R-emi4-4 — the EMerge section resolved and the lowering's objects, through the lowering a run
    /// writes (EmergeScriptWriter.Write), so explain cannot describe an input the run would not write.</summary>
    private static Em3dEmergeJson Emerge(Em3dProblem p, EmSetup setup)
    {
        var s = EmergeSettings.Resolve(setup.Emerge);
        string quality = setup.Emerge?.Quality is { } q ? $"{q} (field)" : "Standard (default)";
        var low = s.Problems() is { Count: > 0 } bad ? null : EmergeScriptWriter.Write(p, s);
        return new Em3dEmergeJson(quality, s.MaxElementWavelengths, s.EdgeRefinement, s.ElementOrder, s.AdaptiveSweep, s.Tolerance,
                                  s.InitialSamples, s.MaxSamples, s.AdaptiveMesh,
                                  low is { Ok: true } ? low.EdgeSizeM : null, low is { Ok: true } ? low.LargestSizeM : null,
                                  [.. (low?.Objects ?? []).Select(o => new Em3dEmergeObjectJson(o.Name, o.Kind, o.Mapping))],
                                  low is null ? string.Join(" ", s.Problems()) : low.Refusal);
    }

    /// <summary>
    /// brief-em3d-115 R-em3d115-6 — per multi-terminal wave port on Palace: the route a run takes (one face, an entry per
    /// terminal, its Mode, the Active one, the face's one MaxSize, Offset 0) and that whether the face is degenerate is decided
    /// from the run's kₙ — or the sentence the run refuses it with (on Both, the note that Palace is skipped).
    /// </summary>
    private static IReadOnlyList<string>? PalaceTerminalLines(Em3dProblem p, Em3dSolverChoice solver)
    {
        var facts = Em3dRunService.TerminalFacts(p);
        if (facts.Ports.Count == 0) return null;
        if ((solver.IsCompare
                ? Em3dRunService.TerminalPortsSkipPalace(facts.Ports, facts.LumpedPorts, facts.Shifted)
                : Em3dRunService.TerminalPortRefusal(solver, facts.Ports, facts.LumpedPorts, facts.Shifted)) is { } no)
            return [$"a run would stop here: {no}"];
        var lines = new List<string>();
        foreach (var g in p.Ports.Where(q => q.FaceGroup is not null).GroupBy(q => q.FaceGroup!.Value).OrderBy(g => g.Key))
        {
            var ts = g.OrderBy(q => q.Number).ToList();
            int maxSize = Math.Max(2 * ts.Count, ts.Count + 15);
            lines.Add($"port '{ts[0].FaceGroupLabel}': terminals {string.Join(", ", ts.Select(t => t.SourceLabel ?? t.Name))} as modes " +
                      $"{string.Join(" and ", Enumerable.Range(1, ts.Count))} of one Palace wave port on the {p.FaceOf(ts[0].Min, ts[0].Max)} face: " +
                      string.Join("; ", ts.Select((t, k) => $"{t.SourceLabel ?? t.Name} Mode {k + 1}{(k == 0 ? ", Active" : ", not Active")}")) +
                      $"; MaxSize {maxSize} on every entry, so the face's modes come from one eigen-solve; Offset 0; each entry is its own " +
                      "excitation. A run first solves the face's modes on their own: if they travel at one speed (a stripline, any homogeneous " +
                      "fill) it is refused, naming openEMS; otherwise the modal S is converted to terminal S, each terminal at its own Z0.");
        }
        return lines;
    }

    /// <summary>
    /// brief-em3d-116 R-em3d116-3 — per wave port on openEMS: the feed and how its length was set, the source plane and the
    /// source's shape, the three voltage planes, each terminal's current planes and the clearance of its box, and the notes
    /// (the face's termination, PEC side walls). Read from the grid a run writes and the placement its writer makes.
    /// </summary>
    private static IReadOnlyList<string>? WavePortLines(Em3dProblem p, FdtdGridResult? g)
    {
        if (g is null || !p.HasWavePorts) return null;
        var plan = g.WavePorts;
        if (plan.Refusal is { } no) return [$"a run would stop here: {no}"];
        var lines = new List<string>();
        var (placed, cannot) = FdtdWavePorts.Place(p, plan, g);
        string L(double m) => FdtdGrid.FormatLength(m);
        foreach (var f in plan.Feeds)
        {
            string a = FdtdGrid.AxisName(f.Axis);
            lines.Add($"{f.Face} face ({string.Join(", ", f.Ports.Select(x => $"'{x}'"))}): feed {L(f.FeedM)} from the source to the reference plane, " +
                      $"{f.LengthSetBy}; the grid grows {L(f.ExtensionM)} past the face before its PML; source plane at {a} = {L(f.SourceAtM)}; " +
                      $"voltage planes at {a} = {L(f.At(f.ReferenceIndex - 1))}, {L(f.ReferenceAtM)} (the reference plane) and " +
                      $"{L(f.At(f.ReferenceIndex + 1))}.");
            foreach (var e in placed.Where(e => e.Feed == f))
            {
                var t = e.Terminal;
                string shape = t.Shape switch
                {
                    FdtdTerminalShape.Coaxial    => $"coaxial: a radial source ∝ 1/ρ over the annulus from {L(t.InnerRadiusM)} to {L(t.OuterRadiusM)}",
                    FdtdTerminalShape.Stripline when t.Interface is { } m
                                                 => "a strip between two reference planes, on an interface (" +
                                                    $"{(m.Below is { } b ? $"'{b}'" : "vacuum")} on its − side, {(m.Above is { } o ? $"'{o}'" : "vacuum")} on its + side): " +
                                                    "two sheets across it, strip to each plane, pointing away from it; its voltage is the half from the strip " +
                                                    $"to its reference ({(t.VoltageHalf == "_up" ? "+" : "−")} side), since on an interface the two halves differ",
                    FdtdTerminalShape.Stripline  => "a strip between two reference planes: two sheets across it, strip to each plane, pointing away " +
                                                    "from it; its voltage is the mean of the two halves",
                    FdtdTerminalShape.Microstrip => "a strip over one reference plane: one sheet across it, strip to the plane",
                    _                            => "a source along its voltage path",
                };
                if (g.Cylinder is not null)
                {
                    lines.Add($"  terminal {t.Label} ('{t.Port.PositiveObject}', port {t.Port.Number}): coaxial, on the cylindrical grid: a radial source " +
                              $"E_ρ ∝ 1/ρ in the grid's own ρ component over the annulus from {L(e.Sources[0].Min.X)} to {L(e.Sources[0].Max.X)}; " +
                              $"voltage along ρ at α = 0; current planes at {a} = {L(e.IaAtM)} and {L(e.IbAtM)}, each a disc to ρ = {L(e.CurrentBox.U1)} " +
                              $"round the full circle, clear of '{e.NearestConductor}' by {e.ClearanceCells.ToString("0.##", CultureInfo.InvariantCulture)} cells " +
                              $"({L(e.ClearanceM)}).");
                    continue;
                }
                string u = FdtdGrid.AxisName((FdtdAxis)t.U), v = FdtdGrid.AxisName((FdtdAxis)t.V);
                lines.Add($"  terminal {t.Label} ('{t.Port.PositiveObject}', port {t.Port.Number}): {shape}; current planes at {a} = {L(e.IaAtM)} and " +
                          $"{L(e.IbAtM)}, the loop {u} {L(e.CurrentBox.U0)} .. {L(e.CurrentBox.U1)}, {v} {L(e.CurrentBox.V0)} .. {L(e.CurrentBox.V1)}" +
                          (e.NearestConductor is { } n ? $", clear of '{n}' by {e.ClearanceCells.ToString("0.##", CultureInfo.InvariantCulture)} cells ({L(e.ClearanceM)})" : "") + ".");
            }
        }
        if (cannot is not null) lines.Add($"a run would stop here: {cannot}");
        lines.AddRange(plan.Notes);
        return lines;
    }

    /// <summary>brief-em3d-22 R-em3d22-2b — the terminals, the ground and what floats.</summary>
    private static Em3dStaticJson? Static(Em3dProblem p, EmSetup setup)
    {
        if (!p.IsStatic) return null;
        bool es = p.Type == Em3dProblemType.Electrostatic;
        var terminals = p.Terminals.Where(t => !t.Floating).Select((t, k) => new Em3dTerminalJson(
            k + 1, t.Name, setup.Terminals3D.FirstOrDefault(s => s.Name == t.Name)?.Net ?? "", t.Objects, t.SourcePort)).ToList();
        // brief-dfr31-1 — a floating body is solved as a terminal and eliminated, so it floats rather than being a row.
        var floating = p.Terminals.Where(t => t.Floating).SelectMany(t => t.Objects).Concat(p.FloatingConductors()).ToList();
        return new Em3dStaticJson(p.Type.ToString(), terminals, p.GroundObjects, floating,
            es ? "A floating conductor carries no net charge: each piece of metal is solved as one more terminal and eliminated " +
                 "from the matrix, which is exact (UnassignedConductors3D: Float)."
               : "A floating conductor carries no source current, only the screening current a perfect conductor carries.");
    }

    /// <summary>
    /// R-em3d6-4c. Each program the setup's solver needs, through <c>SolverDiscovery.ReadinessFor</c> —
    /// the call the run itself makes at its top, so this says what a run would do rather than restating
    /// the rule. It starts version probes and, for an uncached Palace, one dry run; it never starts a
    /// mesher or a solver (gate 6 counts that).
    /// </summary>
    private static IReadOnlyList<Em3dSolverJson> Solvers(Em3dSolverChoice solver, EmSetup setup, PalaceDevice device)
        => SolverDiscovery.ReadinessFor(solver, setup, device).Select(r => new Em3dSolverJson(
               r.Name, r.Installation is not null, r.Installation?.Path, r.Installation?.Version,
               r.Installation?.Release, r.Installation?.Validated ?? false,
               r.Installation?.HowFound switch
               {
                   SolverHowFound.Settings    => "settings",
                   SolverHowFound.Environment => "environment",
                   SolverHowFound.Path        => "path",
                   SolverHowFound.Spack       => "spack",
                   SolverHowFound.Installed   => "installed",
                   SolverHowFound.Conda       => "conda",
                   null                       => null,
                   _                          => "default-directory",
               },
               r.Capabilities.Select(c => new Em3dCapabilityJson(
                   c.Capability == SolverCapability.DrivenLumpedPorts ? "driven-lumped-ports" : c.Capability.ToString(),
                   c.Available, c.Detail, c.FromCache) { Probe = SolverDiscovery.ProbeKind(c.Capability), Devices = c.Devices }).ToList(),
               r.Rejected, r.Proceeds, r.Refusal) { Warning = r.Warning }).ToList();

    /// <summary>
    /// brief-em3d-136 R-em3d136-4c — which device a run's Palace would use and why, or null when the setup runs no Palace
    /// (the report is then unchanged). A GPU's process count comes from the readiness check's own GPU verdict.
    /// </summary>
    private static Em3dDeviceJson? Device(EmSetup setup, PalaceDevice device, IReadOnlyList<Em3dSolverJson> solvers)
    {
        if (!Em3dSolvers.Any(setup.Solver3D, c => c.Gpu)) return null;
        if (device == PalaceDevice.Cpu)
            return new("cpu", "the default: a headless run solves on the CPU unless --device gpu asks for a GPU (the GUI " +
                              "uses this machine's Device setting); one Palace process per physical core, or the Cores setting.");
        var verdict = solvers.FirstOrDefault(s => s.Tool == SolverDiscovery.Palace.Name)?.Capabilities
                             .FirstOrDefault(c => c.Capability == nameof(SolverCapability.Gpu));
        return new("gpu", verdict is { Available: true, Devices: { } n }
            ? $"--device gpu: one Palace process per GPU, {n} found."
            : "--device gpu: a run would be refused before meshing; the Palace row below says why.");
    }

    /// <summary>
    /// R-em3d5-3c. Each row says which kind of number it is, and an unavailable one says why rather than
    /// printing a zero. Palace's is an ESTIMATE: each meshed region's volume divided by the volume of an
    /// element at the Palace section's largest size for its material (brief-em3d-7's
    /// <see cref="GmshGeoWriter.MaxElementSizeM"/>, the formula the script itself uses). openEMS's count
    /// is EXACT: <see cref="FdtdGrid.Build"/> is the grid a run writes (brief-em3d-8 R-em3d8-5c). Memory is
    /// printed beside a count and never without one.
    /// </summary>
    private static IReadOnlyList<Em3dSizeJson> Size(Em3dProblem? p, EmSetup setup,
                                                    (FdtdGridResult? Grid, string? Why) grid)
    {
        var settings = PalaceSettings.Resolve(setup.Palace);
        Em3dSizeJson palace;
        if (p is null)
            palace = new("palace", "unavailable", null, null, null, null, "there is no 3D problem to size.");
        else if (Em3dRunService.EstimatePalace(p, settings) is not { } est)
            palace = new("palace", "unavailable", null, null, null, null,
                $"no estimate at element order {settings.ElementOrder}: the unknowns per element are measured at " +
                "orders 1 and 2 only.");
        else
        {
            palace = new("palace", "estimate", est.Tetrahedra, est.Unknowns, null, est.MemoryBytes,
                $"about {est.Tetrahedra:N0} elements and {est.Unknowns:N0} unknowns at order {est.Order}" +
                (est.MemoryBytes is { } b ? $", about {b / 1e9:0.#} GB" +
                    (settings.AdaptiveMaxIterations > 0 ? " with refinement passes allowed" : "") +
                    (settings.LinearSolver == PalaceLinearSolver.Direct ? " on the direct linear solver" : "") : "") +
                ", from each meshed region's volume at its largest element and the refined shell round each " +
                "conductor, sheet and port" +
                // EMB-4 R-emb4-1 — and the outlines' edges shorter than the element at metal, each of which Gmsh meshes.
                (est.OutlineTetrahedra > 0
                    ? $", {est.OutlineTetrahedra:N0} of the elements for the outlines' edges shorter than the element at metal"
                    : "") +
                ". Palace's adaptive passes add to " +
                "it, and the run reports the real counts. No mesher is run to get it." +
                (est.OutlineDominates
                    ? " The estimate is low on imported outlines with many short edges, and this one has them: the " +
                      "count Gmsh reports when the run meshes is the figure to go by."
                    : ""));
        }
        return setup.Solver3D.Runs(EmergeSolver.Id)
            ? [palace, OpenEmsSize(grid, p?.Ports.Count ?? 0), EmergeSize(p)]
            : [palace, OpenEmsSize(grid, p?.Ports.Count ?? 0)];
    }

    /// <summary>brief-emi-4 R-emi4-4 — EMerge's size is not estimated: Palace's volume-and-shell estimate read 6 to 15 times
    /// EMerge's real count on the Cavity, because EMerge refines round the EDGES of each conductor face rather than over it
    /// (src/Design/RESOLVED.md, EMI-4). The row says so rather than print a number that far off.</summary>
    private static Em3dSizeJson EmergeSize(Em3dProblem? p)
        => new("emerge", "unavailable", null, null, null, null, p is null
            ? "there is no 3D problem to size."
            : "no estimate: EMerge refines round the edges of each conductor and sheet face, which circuitRF does not price " +
              "yet. A run reports its mesh, and EMerge checks its own memory figure (0.25 MB per element) against free " +
              "memory before it solves.");

    /// <summary>
    /// The openEMS grid for the problem, through the generator a run uses, with the section's grid
    /// fields — or why there is none. It is arithmetic on the resolved problem: no process.
    /// </summary>
    private static (FdtdGridResult? Grid, string? Why) Grid(Em3dProblem p, EmSetup setup)
    {
        var settings = CemOpenEms.ResolveGrid(setup.OpenEms);
        if (settings.Problems() is { Count: > 0 } bad) return (null, string.Join(" ", bad));
        if (p.Validate() is { Count: > 0 }) return (null, "the 3D problem is not sound (see the warnings).");
        try
        {
            return (FdtdGrid.Build(p, settings), null);
        }
        catch (InvalidOperationException e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Outline points past which <c>explain</c> builds no openEMS grid for a setup that does not run on openEMS. A cropped board has
    /// a few thousand; the 83,164-point whole board this was measured on took over a minute.</summary>
    private const int OpenEmsGridComparisonPoints = 20_000;

    private static int OutlinePoints(Em3dProblem p)
        => p.Solids.Sum(s => s.Primitive is Em3dExtrudedPolygon e ? e.Outline.Count + e.Holes.Sum(h => h.Count) : 0)
         + p.Sheets.Sum(s => s.Outline.Count + s.Holes.Sum(h => h.Count));

    /// <summary>R-em3d8-5c — cells per axis, total, smallest cell and its feature, Δt, steps, memory, merges.</summary>
    private static Em3dSizeJson OpenEmsSize((FdtdGridResult? Grid, string? Why) grid, int ports)
    {
        if (grid.Grid is not { } g)
            return new("openems", "unavailable", null, null, null, null,
                       $"no grid: {grid.Why ?? "there is no 3D problem to size."}");
        if (g.Cylinder is { } cyl) return CylindricalSize(g, cyl, ports);
        var s = g.Smallest;
        string axis = FdtdGrid.AxisName(s.Axis);
        var features = s.SmallestCellFeatures.Select(f => f.Describe(s.Axis)).ToList();
        string note =
            $"{g.X.Lines.Count:N0} × {g.Y.Lines.Count:N0} × {g.Z.Lines.Count:N0} = {g.Cells:N0} cells, the grid a run " +
            $"writes; smallest cell {FdtdGrid.FormatLength(s.SmallestCellM)} on {axis}, set by {string.Join("; ", features)}. " +
            $"Time step about {g.TimeStepEstimateS:G3} s (the Courant estimate; openEMS computes its own), about " +
            $"{g.Steps:N0} steps for the pulse and a nominal ring-down, about {g.MemoryBytes / 1e6:N0} MB. " +
            (g.Merges.Count == 0 ? "No lines merged." : $"{g.Merges.Count} merge(s) of lines closer than MinCell " +
                                                         $"({FdtdGrid.FormatLength(g.MinCellM)}) or than a quarter of their local cell.") +
            // brief-em3d-9 R-em3d9-3a: openEMS excites one port per simulation.
            (ports > 1 ? $" openEMS runs once per port: {ports} runs, one after another, so the whole takes about " +
                         $"{ports} times one run." : "") +
            // brief-dfr27-5 R-dfr27-5-2 — the estimate a run states before it starts, on this machine's cores.
            " " + OpenEmsTimeEstimate.Sentence(g, ports, OpenEmsTimeEstimate.Cores(null));
        return new("openems", "exact", g.Cells, null, g.TimeStepEstimateS, g.MemoryBytes, note,
                   [g.X.Lines.Count, g.Y.Lines.Count, g.Z.Lines.Count], s.SmallestCellM, axis, features, g.Steps,
                   g.Merges.Select(m => m.Sentence).ToList(), g.Warnings, g.Refusal,
                   g.Snaps.Count == 0 ? null : g.Snaps.Select(m => m.Sentence).ToList());
    }

    /// <summary>brief-em3d-120 R-em3d120-5 — a cylindrical grid: its axis and origin, ρ range, azimuth count and why, the smallest
    /// arc, the Courant estimate on it.</summary>
    private static Em3dSizeJson CylindricalSize(FdtdGridResult g, FdtdCylinder cyl, int ports)
    {
        string L(double m) => FdtdGrid.FormatLength(m);
        string axial = FdtdGrid.AxisName(cyl.Axis);
        string start = cyl.RhoMinSetBy is { } pin ? $"the surface of '{pin}'" : "the axis itself (no metal contains it along the whole box)";
        string feature = cyl.RhoMinSetBy is { } by ? $"'{by}' at ρ = {L(cyl.RhoMinM)}" : $"the first ring, ρ = {L(cyl.SmallestArcAtM)}";
        string note =
            $"a cylindrical grid about {cyl.Describe()}: {g.X.Lines.Count:N0} ρ × {cyl.AzimuthCells:N0} α × {g.Z.Lines.Count:N0} {axial} = " +
            $"{g.Cells:N0} cells, the grid a run writes; ρ from {L(cyl.RhoMinM)} ({start}) to {L(cyl.RhoMaxM)} (the circle inscribed in the " +
            $"{string.Join(", ", cyl.SideFaces)} faces, {cyl.RhoMaxKind switch { Em3dBoundaryKind.Pec => "PEC", Em3dBoundaryKind.Pmc => "PMC", _ => "absorbing" }}); α in {cyl.AzimuthCells} cells, set by {cyl.AzimuthSetBy}; smallest arc " +
            $"{L(cyl.SmallestArcM)} at ρ = {L(cyl.SmallestArcAtM)}, smallest radial cell {L(g.X.SmallestCellM)}, smallest {axial} cell " +
            $"{L(g.Z.SmallestCellM)}. Time step about {g.TimeStepEstimateS:G3} s (the Courant estimate on the smallest arc, in the medium " +
            $"that ring lies in; openEMS computes its own), about {g.Steps:N0} steps for the pulse and a nominal ring-down, about " +
            $"{g.MemoryBytes / 1e6:N0} MB. " +
            (g.Merges.Count == 0 ? "No lines merged." : $"{g.Merges.Count} merge(s) of lines closer than MinCell ({L(g.MinCellM)}).") +
            (ports > 1 ? $" openEMS runs once per port: {ports} runs, one after another, so the whole takes about {ports} times one run." : "");
        return new("openems", "exact", g.Cells, null, g.TimeStepEstimateS, g.MemoryBytes, note,
                   [g.X.Lines.Count, cyl.AzimuthCells, g.Z.Lines.Count], cyl.SmallestArcM, "α", [feature], g.Steps,
                   g.Merges.Select(m => m.Sentence).ToList(), g.Warnings, g.Refusal);
    }

    /// <summary>R-em3d8-4c — how far the openEMS grid reaches outside each absorbing face for its PML.</summary>
    private static IReadOnlyList<string> Enlargements(FdtdGridResult? g)
    {
        if (g is null) return [];
        var list = new List<string>();
        foreach (var a in new[] { g.X, g.Y, g.Z })
        {
            string n = FdtdGrid.AxisName(a.Axis);
            if (a.PmlLowM > 0)
                list.Add($"openEMS PML: {n}min grows outward by {FdtdGrid.FormatLength(a.PmlLowM)} " +
                         $"({FdtdGrid.FormatLength(a.Lines[1] - a.Lines[0])} cells)");
            if (a.PmlHighM > 0)
                list.Add($"openEMS PML: {n}max grows outward by {FdtdGrid.FormatLength(a.PmlHighM)} " +
                         $"({FdtdGrid.FormatLength(a.Lines[^1] - a.Lines[^2])} cells)");
        }
        return list;
    }

    // ── the human report ─────────────────────────────────────────────────────────────────────

    /// <summary>brief-em3d-64 R-em3d64-6b — the kernel, and each object it built, as the elaboration recorded them. Every
    /// word about an absent kernel is <see cref="GeometryKernel"/>'s own (R-em3d63-3b).</summary>
    public static Em3dGeometryKernelJson Kernel(CircuitRF.Design.ThreeD.C3dElaboration e)
    {
        var cap = GeometryKernel.Shared.Capability;
        return new Em3dGeometryKernelJson(cap.Available, cap.HowFound is { Length: > 0 } h ? h : null, cap.WorkerPath, cap.OcctVersion,
            cap.Available ? null : $"{cap.Reason} {cap.Action}",
            [.. e.KernelBuilds.Select(k => new Em3dKernelObjectJson(k.Name, k.Kind, k.Operands, k.Built ? "built" : "cache",
                                                                    k.Faces, k.Edges, k.MinRadiusM, k.Notes, k.Refusal))]);
    }

    private static void PrintKernel(Em3dGeometryKernelJson k)
    {
        Console.WriteLine($"  {GeometryKernel.SettingsTitle.ToLowerInvariant()}  " +
                          (k.Available ? $"Open CASCADE Technology {k.OcctVersion}, {k.HowFound} ({k.WorkerPath})" : k.Reason));
        foreach (var o in k.Objects)
        {
            foreach (string line in o.Operands) Console.WriteLine($"    {line}");
            if (o.Refusal is { } why) { Console.WriteLine($"      refused: {why}"); continue; }
            string radius = o.MinRadiusM is { } r ? $", smallest radius {Em3dSectionScene.FormatLength(r)}" : ", every face flat";
            Console.WriteLine($"      {(o.Build == "built" ? "built" : "from the cache")}: {o.Faces} faces, {o.Edges} edges{radius}");
            foreach (string n in o.Notes) Console.WriteLine($"      note: {n}");
        }
    }

    /// <summary>brief-sar-2 R-sar2-7 — <c>explain --materials</c>: the problem's materials alone, each dispersive one with the
    /// frequency it was evaluated at.</summary>
    public static void PrintMaterials(ExplainEm3dJson r)
    {
        if (r.Refusal is { } refusal) { Console.WriteLine($"  materials    the 3D problem could not be built: {refusal}"); return; }
        PrintMaterialRows(r);
        if (r.Materials.FirstOrDefault(m => m.EvaluatedAtHz is not null)?.EvaluatedAtHz is { } f)
            Console.WriteLine($"  {"",-12} frequency-dependent materials are evaluated once, at {MaterialDispersion.Hz(f)} " +
                              $"({(r.Sar is null ? "the sweep's centre" : "the SAR frequency")}), and the solver sees those values at every " +
                              "frequency of the sweep.");
        if (r.Sar is { } sar) PrintSar(sar);
    }

    /// <summary>brief-sar-6 R-sar6-8 — the Sar group: what a run computes, over which tissue, and what it writes.</summary>
    private static void PrintSar(Em3dSarJson s)
    {
        static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        Console.WriteLine($"  sar          at {string.Join(", ", s.FrequenciesHz.Select(MaterialDispersion.Hz))}; " +
                          (s.MassesGrams.Count == 0 ? "local SAR only (a cylindrical grid has no cubes to average over)"
                                                    : $"{string.Join(", ", s.MassesGrams.Select(m => G(m) + " g"))} averaged ({s.Method})") +
                          (s.B1 ? $"; B1 with B0 along {s.B0Axis}" : "; no B1") + (s.CrossCheck ? "; cross-checked with sar_calc" : "") +
                          (s.PerPortFields ? "; every port's fields kept beside the .sNp" : ""));
        if (s.Refusal is { } no) { Console.WriteLine($"  {"",-12} a run would stop here: {no}"); return; }
        Console.WriteLine($"  {"",-12} region {s.Region}: {string.Join(", ", s.RegionObjects)}" + (s.B1Region is { } b ? $"; B1's mean over {b}" : ""));
        foreach (var t in s.Tissue)
            Console.WriteLine($"    {t.Object,-28} {t.Material,-20} ρ {G(t.DensityKgM3)} kg/m³  εr {G(t.Epsr)}  σ {G(t.SigmaSm)} S/m");
        if (s.DumpCells is { } cells && s.DumpBytes is { } bytes)
            Console.WriteLine($"  {"",-12} dump {cells:N0} cells, about {MachineMemory.Format(bytes)} on disk; values are reported per 1 W accepted");
    }

    private static void PrintMaterialRows(ExplainEm3dJson r)
    {
        static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        Console.WriteLine("  materials");
        foreach (var m in r.Materials)
            Console.WriteLine($"    {m.Name,-24} εr {G(m.Epsr)}" +
                              (m.EpsrTensor is { } tensor ? $" (xx/yy/zz {string.Join("/", tensor.Select(G))})" : "") +
                              $"  tanδ {G(m.TanD)}  μr {G(m.Mur)}  σ {G(m.SigmaSm)} S/m" +
                              (m.EvaluatedAtHz is { } at ? $" at {MaterialDispersion.Hz(at)} ({m.Dispersion})" : "") +
                              $"   from {m.From}");
    }

    private static void PrintEmerge(Em3dEmergeJson e)
    {
        Console.WriteLine($"  EMerge       quality {e.Quality}: element order {e.ElementOrder}, each material at most " +
                          $"{G(e.MaxElementWavelengths)} of its wavelength, edges at {G(e.EdgeRefinement)} of the smallest" +
                          (e.EdgeSizeM is { } edge ? $" ({L(edge)})" : "") +
                          (e.LargestSizeM is { } big ? $", largest {L(big)}" : "") + "; " +
                          (e.AdaptiveSweep ? $"adaptive sweep to {G(e.Tolerance)} on |S| from {e.InitialSamples} samples, at most {e.MaxSamples}"
                                           : "every frequency solved") +
                          $"; {e.AdaptiveMesh} mesh refinement pass(es)");
        if (e.Refusal is { } no) Console.WriteLine($"  {"",-12} a run would stop here: {no}");
        foreach (var o in e.Objects) Console.WriteLine($"    {o.Name,-28} {o.Kind,-14} {o.Mapping}");

        static string L(double m) => Em3dSectionScene.FormatLength(m);
        static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
    }

    public static void Print(ExplainEm3dJson r)
    {
        static string L(double m) => Em3dSectionScene.FormatLength(m);
        static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

        if (r.GeometryKernel is { } kernel) PrintKernel(kernel);
        Console.WriteLine($"  3D setup     solver {r.Solver}");
        if (r.Compare is { } compare)
        {
            Console.WriteLine($"  {"",-12} compared against {compare.Reference}");
            foreach (var c in compare.Solvers)
                Console.WriteLine($"  {"",-12} {c.Solver}: " + (c.Runnable ? "would run" : $"would not run — {c.Refusal}"));
        }
        if (r.DcRow is { } dcRow) Console.WriteLine($"  {"",-12} 0 Hz: {dcRow}");
        if (r.Refusal is { } refusal)
        {
            Console.WriteLine($"  {"",-12} the 3D problem could not be built: {refusal}");
            PrintDevice(r.Device);
            PrintLimits(r.Limits);
            PrintSolvers(r.Solvers);
            return;
        }
        foreach (var g in r.Guidance)
            Console.WriteLine($"  {"",-12} guidance: {g.Sentence}");

        if (r.Temperature is { } t)
        {
            Console.WriteLine($"  temperature  {G(t.OperatingTempC)} °C ({(t.From == "field" ? "this .cem's OperatingTempC" : "the default; this .cem states no OperatingTempC")})");
            foreach (var c in t.Conductors)
                Console.WriteLine($"  {"",-12} σ {c.Material}: {c.SigmaSm.ToString("G4", CultureInfo.InvariantCulture)} S/m");
            if (t.NoAlpha.Count > 0)
                Console.WriteLine($"  {"",-12} no α₂₀ (σ₂₀ used at every temperature): {string.Join(", ", t.NoAlpha)}");
            if (t.UnknownTemperature.Count > 0)
                Console.WriteLine($"  {"",-12} σ of unknown temperature, used as given: {string.Join(", ", t.UnknownTemperature)}");
        }

        PrintMaterialRows(r);

        Console.WriteLine("  solids       (construction order: where two overlap, the later one wins)");
        foreach (var s in r.Solids)
        {
            Console.WriteLine($"    {s.Order,3} {s.Name,-28} {s.Role,-10} {s.Material,-18} {s.Primitive,-16} " +
                              $"x {L(s.X0)} .. {L(s.X1)}, y {L(s.Y0)} .. {L(s.Y1)}, z {L(s.Z0)} .. {L(s.Z1)}");
            if (s.Kind == "sheet")
                Console.WriteLine($"    {"",3} {"",-28} sheet, {L(s.ThicknessM ?? 0)} thick: {s.SheetReason ?? "(no reason recorded)"}");
        }

        if (r.Wires.Count > 0)
        {
            Console.WriteLine("  wires");
            foreach (var w in r.Wires)
            {
                Console.WriteLine($"    {w.Name,-20} {w.Material}, {w.Section}, d {L(w.DiameterM)}; " +
                                  $"foot {L(w.FootLengthM)} ({w.FootLengthFrom}), ball {L(w.BallDiameterM)} × {L(w.BallHeightM)} " +
                                  $"({w.BallDiameterFrom}/{w.BallHeightFrom})");
                Console.WriteLine($"    {"",-20} start {EndText(w.Start)}; end {EndText(w.End)}");
                Console.WriteLine($"    {"",-20} loop height: assembly {L(w.AssemblyLoopHeightM)} (wire top at the apex over " +
                                  $"the lower pad), wBond {L(w.WBondLoopHeightM)} (the axis polyline's rise)");
            }
        }

        Console.WriteLine("  ports");
        foreach (var q in r.Ports)
            Console.WriteLine($"    {q.Number,3} {q.Name,-10} {q.Positive} → {q.Negative}, Z0 {G(q.Z0Re)}" +
                              (q.Z0Im != 0 ? $"{(q.Z0Im < 0 ? "-" : "+")}j{G(Math.Abs(q.Z0Im))}" : "") + " Ω, " +
                              $"reference plane at ({L(q.ReferenceOrigin[0])}, {L(q.ReferenceOrigin[1])}, {L(q.ReferenceOrigin[2])}), " +
                              $"normal ({G(q.ReferenceNormal[0])}, {G(q.ReferenceNormal[1])}, {G(q.ReferenceNormal[2])}), " +
                              $"shift {L(q.ReferenceShiftM)}" +
                              (q.Kind is { } kind ? $"; {kind} port, reference plane {q.ReferencePlane}" : "") +
                              (q.Return is { } ret
                                  ? $"; returns through {ret}, " + q.ReturnDecidedBy switch
                                  {
                                      "port"  => "as the port's Return states",
                                      "setup" => "as this setup's return plane names",
                                      _       => "walked from the artwork",
                                  }
                                  : ""));
        if (r.OpenEmsWavePorts is { Count: > 0 } waveLines)
        {
            Console.WriteLine("  openEMS wave ports");
            foreach (string l in waveLines) Console.WriteLine($"    {l}");
        }
        if (r.PalaceTerminalPorts is { Count: > 0 } palaceLines)
        {
            Console.WriteLine("  Palace terminal ports");
            foreach (string l in palaceLines) Console.WriteLine($"    {l}");
        }
        if (r.Emerge is { } em) PrintEmerge(em);
        if (r.Eigenmode is { } eig)
            Console.WriteLine($"  eigenmode    {eig.Count} mode(s) ({eig.CountFrom}) above {G(eig.TargetHz / 1e9)} GHz ({eig.TargetFrom})");
        if (r.Sar is { } sarGroup) PrintSar(sarGroup);

        if (r.Static is { } st)
        {
            Console.WriteLine($"  problem      {st.Problem}");
            Console.WriteLine("  terminals");
            foreach (var term in st.Terminals)
                Console.WriteLine($"    {term.Index,3} {term.Name,-12} net '{term.Net}'" + (term.Source is { } src ? $", driven through {src}" : "") +
                                  $": {string.Join(", ", term.Conductors)}");
            Console.WriteLine($"    ground       {(st.Ground.Count == 0 ? "no conductor (a PEC face of the air box, if any)" : string.Join(", ", st.Ground))}");
            Console.WriteLine($"    floating     {(st.Floating.Count == 0 ? "none" : string.Join(", ", st.Floating))}");
            if (st.Floating.Count > 0) Console.WriteLine($"    {"",-12} {st.FloatingMeans}");
        }

        if (r.AirBox is { } b)
        {
            Console.WriteLine($"  air box      x {L(b.Min[0])} .. {L(b.Max[0])}, y {L(b.Min[1])} .. {L(b.Max[1])}, " +
                              $"z {L(b.Min[2])} .. {L(b.Max[2])}");
            Console.WriteLine($"  {"",-12} " + string.Join("  ", b.Faces.Select(f => $"{f.Face} {f.Boundary} ({f.From})")));
            Console.WriteLine($"  {"",-12} enlargements: " +
                              (b.Enlargements.Count == 0 ? "none"
                                                         : string.Join("; ", b.Enlargements)));
        }

        Console.WriteLine("  size");
        foreach (var z in r.Size)
        {
            Console.WriteLine($"    {z.Backend,-10} {z.Kind}: {z.Note}");
            foreach (var m in z.Merges ?? []) Console.WriteLine($"    {"",-10} merged: {m}");
            foreach (var m in z.Snaps ?? []) Console.WriteLine($"    {"",-10} snapped: {m}");
            foreach (var w in z.GridWarnings ?? []) Console.WriteLine($"    {"",-10} warning: {w}");
            if (z.Refusal is { } no) Console.WriteLine($"    {"",-10} a run would stop here: {no}");
        }

        PrintDevice(r.Device);
        PrintLimits(r.Limits);
        PrintSolvers(r.Solvers);

        foreach (var w in r.Warnings) Console.WriteLine($"  warning: {w}");
        foreach (var n in r.Notes)    Console.WriteLine($"  note: {n}");
    }

    private static void PrintDevice(Em3dDeviceJson? device)
    {
        if (device is not null) Console.WriteLine($"  device: {device.Device} — {device.Why}");
    }

    private static void PrintLimits(IReadOnlyList<string>? limits)
    {
        foreach (string line in limits ?? []) Console.WriteLine($"  {line}");
    }

    private static void PrintSolvers(IReadOnlyList<Em3dSolverJson> solvers)
    {
        Console.WriteLine("  solvers");
        foreach (var s in solvers)
        {
            if (!s.Found)
                Console.WriteLine($"    {s.Tool,-10} not found");
            else
                Console.WriteLine($"    {s.Tool,-10} {s.Version}" +
                                  (s.Release is not { } rel ? (s.Warning is null ? ", NOT validated" : ", not tested")
                                   : (rel == s.Version ? ", " : $" = {rel}, ") + (s.Tool == SolverDiscovery.Palace.Name ? SolverDiscovery.Palace.ListWord : "tested")) +
                                  $" — {(s.Path is { } path ? SpackInstalls.FoldPadding(path) : null)} ({s.HowFound})");   // --json keeps the full path
            foreach (var c in s.Capabilities)
                Console.WriteLine($"    {"",-10} {c.Capability}: {(c.Available ? "yes" : "no")}" +
                                  $"{(c.FromCache ? " (cached for this binary)" : "")} — {c.Detail}" +
                                  (c.Probe is { } probe ? $" Asked by {probe}." : ""));
            if (s.Warning is { } warning) Console.WriteLine($"    {"",-10} warning: {warning}");
            Console.WriteLine($"    {"",-10} " + (s.Proceeds ? "a run would proceed past this program" : $"a run would stop here: {s.Refusal}"));
        }
    }

    private static string EndText(Em3dWireEndJson e)
        => $"{e.Style} on '{e.Pad}'" + (e.FootLengthM is { } f ? $", foot {Em3dSectionScene.FormatLength(f)}" : "")
         + (e.Neck ? ", with a neck" : "") + (e.OverhangM > 0 ? $", overhangs its pad by {Em3dSectionScene.FormatLength(e.OverhangM)}" : "");

    private static Em3dWireEndJson End(Em3dWireEnd e)
        => new(e.Style.ToString().ToLowerInvariant(), e.Pad, e.FootLengthM, e.Neck, e.OverhangM);

    private static string Level(WireBondValueSource s) => s switch
    {
        WireBondValueSource.Wire          => "wire",
        WireBondValueSource.Array         => "array",
        WireBondValueSource.AssemblyRules => "assembly-rules",
        _                                 => "built-in",
    };

    private static string Role(Em3dRole r) => r switch
    {
        Em3dRole.Conductor => "conductor", Em3dRole.Air => "air", _ => "dielectric",
    };

    private static string Boundary(Em3dBoundaryKind k) => k switch
    {
        Em3dBoundaryKind.Pec => "PEC", Em3dBoundaryKind.Pmc => "PMC",
        Em3dBoundaryKind.Symmetry => "symmetry", _ => "absorbing",
    };

    private static string Primitive(Em3dPrimitive p) => p switch
    {
        Em3dExtrudedPolygon => "extruded-polygon",
        Em3dBox             => "box",
        Em3dCylinder        => "cylinder",
        Em3dSweep           => "sweep",
        Em3dSphere          => "sphere",
        Em3dTruncatedSphere => "truncated-sphere",
        Em3dEllipsoid       => "ellipsoid",
        Em3dPolyhedron      => "polyhedron",
        Em3dShapeSolid      => "kernel-solid",
        _                   => p.GetType().Name,
    };

    private static IReadOnlyList<double> V(Point3 q) => [q.X, q.Y, q.Z];
}
