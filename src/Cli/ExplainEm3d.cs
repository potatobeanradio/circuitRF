using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
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
    public static ExplainEm3dJson Build(Em3dSetupSource src)
    {
        var setup = src.Setup;
        string solver = setup.Solver3D switch
        {
            Em3dSolver.Palace  => "Palace",
            Em3dSolver.OpenEms => "openEMS",
            Em3dSolver.Both    => "Palace and openEMS",
            _                  => "none",
        };
        var notes    = src.Generated?.Notes ?? [];
        var warnings = src.Generated?.Warnings ?? [];
        var solvers  = Solvers(setup.Solver3D);

        if (src.Generated?.Problem is not { } p)
            return new ExplainEm3dJson(solver, "m", 1.0, [], null, [], [], [], [], null, Size(null, setup, default), solvers, notes, warnings,
                                       src.Refusal ?? "the 3D problem could not be built.");
        var g = src.Generated;

        var guidance = Em3dSolverGuidance.For(p)
            .Select(x => new Em3dGuidanceJson(x.Row, x.Favours == Em3dSolverFit.Fem ? "FEM (Palace)" : "FDTD (openEMS)",
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

        var materials = p.Materials.Select(m => new Em3dMaterialJson(
            m.Name, m.Epsr, m.EpsrTensor, m.TanD, m.Mur, m.SigmaSm,
            g.MaterialSources.TryGetValue(m.Name, out var from) ? from : "unrecorded")).ToList();

        var solids = p.Solids.Select(s =>
            {
                var b = Em3dProblem.Bounds(s.Primitive);
                return (s.Order, Row: new Em3dSolidJson(s.Name, "solid", Role(s.Role), s.Material, Primitive(s.Primitive),
                                                        s.Order, b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1, null, null));
            })
            .Concat(p.Sheets.Select(sh =>
            {
                double x0 = sh.Outline.Min(q => q.X), y0 = sh.Outline.Min(q => q.Y);
                double x1 = sh.Outline.Max(q => q.X), y1 = sh.Outline.Max(q => q.Y);
                string? reason = g.Origins.TryGetValue(sh.Name, out var o) ? o.SheetReason : null;
                return (sh.Order, Row: new Em3dSolidJson(sh.Name, "sheet", "conductor", sh.Material, "sheet", sh.Order,
                                                         x0, y0, sh.Z, x1, y1, sh.Z, reason, sh.ThicknessM));
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
            V(q.Min), V(q.Max), V(q.ReferencePlane.Origin), V(q.ReferencePlane.Normal), q.ReferencePlane.ShiftM)).ToList();

        var grid = Grid(p, setup);
        var box = p.Boundary;
        var stated = setup.AirBox;
        Em3dFaceJson Face(string name, Em3dBoundaryKind kind, EmAirBoxFace? face) => new(
            name, Boundary(kind),
            face is not null ? "setup"
            : name == "zmin" && kind == Em3dBoundaryKind.Pec ? "floor"
            : "default");
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
        };
    }

    /// <summary>brief-em3d-22 R-em3d22-2b — the terminals, the ground and what floats.</summary>
    private static Em3dStaticJson? Static(Em3dProblem p, EmSetup setup)
    {
        if (!p.IsStatic) return null;
        bool es = p.Type == Em3dProblemType.Electrostatic;
        var terminals = p.Terminals.Select((t, k) => new Em3dTerminalJson(
            k + 1, t.Name, setup.Terminals3D.FirstOrDefault(s => s.Name == t.Name)?.Net ?? "", t.Objects, t.SourcePort)).ToList();
        return new Em3dStaticJson(p.Type.ToString(), terminals, p.GroundObjects, p.FloatingConductors(),
            es ? "An electrostatic run refuses a floating conductor: Palace 0.18.1 has no isolated equipotential, " +
                 "only a terminal's potential, ground and a zero-charge wall."
               : "A floating conductor carries no source current, only the screening current a perfect conductor carries.");
    }

    /// <summary>
    /// R-em3d6-4c. Each program the setup's solver needs, through <c>SolverDiscovery.ReadinessFor</c> —
    /// the call the run itself makes at its top, so this says what a run would do rather than restating
    /// the rule. It starts version probes and, for an uncached Palace, one dry run; it never starts a
    /// mesher or a solver (gate 6 counts that).
    /// </summary>
    private static IReadOnlyList<Em3dSolverJson> Solvers(Em3dSolver solver)
        => SolverDiscovery.ReadinessFor(solver).Select(r => new Em3dSolverJson(
               r.Name, r.Installation is not null, r.Installation?.Path, r.Installation?.Version,
               r.Installation?.Release, r.Installation?.Validated ?? false,
               r.Installation?.HowFound switch
               {
                   SolverHowFound.Settings    => "settings",
                   SolverHowFound.Environment => "environment",
                   SolverHowFound.Path        => "path",
                   SolverHowFound.Spack       => "spack",
                   null                       => null,
                   _                          => "default-directory",
               },
               r.Capabilities.Select(c => new Em3dCapabilityJson(
                   c.Capability == SolverCapability.DrivenLumpedPorts ? "driven-lumped-ports" : c.Capability.ToString(),
                   c.Available, c.Detail, c.FromCache)).ToList(),
               r.Rejected, r.Proceeds, r.Refusal)).ToList();

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
                    (settings.AdaptiveMaxIterations > 0 ? " with refinement passes allowed" : "") : "") +
                ", from each meshed region's volume at its largest element. The refinement at " +
                "conductors and ports and Palace's adaptive passes add to it, and the run reports the real " +
                "counts. No mesher is run to get it.");
        }
        return [palace, OpenEmsSize(grid, p?.Ports.Count ?? 0)];
    }

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

    /// <summary>R-em3d8-5c — cells per axis, total, smallest cell and its feature, Δt, steps, memory, merges.</summary>
    private static Em3dSizeJson OpenEmsSize((FdtdGridResult? Grid, string? Why) grid, int ports)
    {
        if (grid.Grid is not { } g)
            return new("openems", "unavailable", null, null, null, null,
                       $"no grid: {grid.Why ?? "there is no 3D problem to size."}");
        var s = g.Smallest;
        string axis = FdtdGrid.AxisName(s.Axis);
        var features = s.SmallestCellFeatures.Select(f => f.Describe(s.Axis)).ToList();
        string note =
            $"{g.X.Lines.Count:N0} × {g.Y.Lines.Count:N0} × {g.Z.Lines.Count:N0} = {g.Cells:N0} cells, the grid a run " +
            $"writes; smallest cell {FdtdGrid.FormatLength(s.SmallestCellM)} on {axis}, set by {string.Join("; ", features)}. " +
            $"Time step about {g.TimeStepEstimateS:G3} s (the Courant estimate; openEMS computes its own), about " +
            $"{g.Steps:N0} steps for the pulse and a nominal ring-down, about {g.MemoryBytes / 1e6:N0} MB. " +
            (g.Merges.Count == 0 ? "No lines merged." : $"{g.Merges.Count} merge(s) of lines closer than MinCell " +
                                                         $"({FdtdGrid.FormatLength(g.MinCellM)}).") +
            // brief-em3d-9 R-em3d9-3a: openEMS excites one port per simulation.
            (ports > 1 ? $" openEMS runs once per port: {ports} runs, one after another, so the whole takes about " +
                         $"{ports} times one run." : "");
        return new("openems", "exact", g.Cells, null, g.TimeStepEstimateS, g.MemoryBytes, note,
                   [g.X.Lines.Count, g.Y.Lines.Count, g.Z.Lines.Count], s.SmallestCellM, axis, features, g.Steps,
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

    public static void Print(ExplainEm3dJson r)
    {
        static string L(double m) => Em3dSectionScene.FormatLength(m);
        static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

        Console.WriteLine($"  3D setup     solver {r.Solver}");
        if (r.Refusal is { } refusal)
        {
            Console.WriteLine($"  {"",-12} the 3D problem could not be built: {refusal}");
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

        Console.WriteLine("  materials");
        foreach (var m in r.Materials)
            Console.WriteLine($"    {m.Name,-24} εr {G(m.Epsr)}" +
                              (m.EpsrTensor is { } tensor ? $" (xx/yy/zz {string.Join("/", tensor.Select(G))})" : "") +
                              $"  tanδ {G(m.TanD)}  μr {G(m.Mur)}  σ {G(m.SigmaSm)} S/m   from {m.From}");

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
                              $"shift {L(q.ReferenceShiftM)}");

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
            foreach (var w in z.GridWarnings ?? []) Console.WriteLine($"    {"",-10} warning: {w}");
            if (z.Refusal is { } no) Console.WriteLine($"    {"",-10} a run would stop here: {no}");
        }

        PrintSolvers(r.Solvers);

        foreach (var w in r.Warnings) Console.WriteLine($"  warning: {w}");
        foreach (var n in r.Notes)    Console.WriteLine($"  note: {n}");
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
                                  (s.Release is not { } rel ? ", NOT validated" : rel == s.Version ? ", validated" : $" = {rel}, validated") +
                                  $" — {s.Path} ({s.HowFound})");
            foreach (var c in s.Capabilities)
                Console.WriteLine($"    {"",-10} {c.Capability}: {(c.Available ? "yes" : "no")}" +
                                  $"{(c.FromCache ? " (cached for this binary)" : "")} — {c.Detail}");
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
        _                   => p.GetType().Name,
    };

    private static IReadOnlyList<double> V(Point3 q) => [q.X, q.Y, q.Z];
}
