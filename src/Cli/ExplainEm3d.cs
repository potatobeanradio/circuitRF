using System.Globalization;
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

        if (src.Generated?.Problem is not { } p)
            return new ExplainEm3dJson(solver, "m", 1.0, [], null, [], [], [], [], null, Size(), notes, warnings,
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

        var ports = p.Ports.Select(q => new Em3dPortJson(
            q.Number, q.Name, q.PositiveObject, q.NegativeObject, q.Z0.Real, q.Z0.Imaginary,
            V(q.Min), V(q.Max), V(q.ReferencePlane.Origin), V(q.ReferencePlane.Normal), q.ReferencePlane.ShiftM)).ToList();

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
        ], []);

        return new ExplainEm3dJson(solver, "m", 1.0, guidance, temperature, materials, solids, wires, ports, airBox,
                                   Size(), notes, warnings, null);
    }

    /// <summary>
    /// R-em3d5-3c. <b>Neither backend's size can be computed in this build, and each row says why
    /// rather than printing a zero.</b> Palace's estimate divides each meshed region's volume by an
    /// initial element volume (<see cref="Em3dSizeEstimate.Palace"/>), and the setup's Palace section
    /// does not yet carry the initial mesh-size settings that would give one. openEMS's count is exact
    /// and comes from its grid generator, which does not exist yet. Memory is printed beside a count and
    /// never without one.
    /// </summary>
    private static IReadOnlyList<Em3dSizeJson> Size() =>
    [
        new("palace", "unavailable", null, null, null, null,
            "no estimate yet: this build's Palace section has no initial mesh-size settings to divide the " +
            "meshed volumes by. When it does, the figure is an estimate that adaptive refinement will grow, " +
            "and the run's first line reports the real initial count. No mesher is run to get it."),
        new("openems", "unavailable", null, null, null, null,
            "grid not yet available in this build."),
    ];

    // ── the human report ─────────────────────────────────────────────────────────────────────

    public static void Print(ExplainEm3dJson r)
    {
        static string L(double m) => Em3dSectionScene.FormatLength(m);
        static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

        Console.WriteLine($"  3D setup     solver {r.Solver}");
        if (r.Refusal is { } refusal)
        {
            Console.WriteLine($"  {"",-12} the 3D problem could not be built: {refusal}");
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

        if (r.AirBox is { } b)
        {
            Console.WriteLine($"  air box      x {L(b.Min[0])} .. {L(b.Max[0])}, y {L(b.Min[1])} .. {L(b.Max[1])}, " +
                              $"z {L(b.Min[2])} .. {L(b.Max[2])}");
            Console.WriteLine($"  {"",-12} " + string.Join("  ", b.Faces.Select(f => $"{f.Face} {f.Boundary} ({f.From})")));
            Console.WriteLine($"  {"",-12} enlargements: " +
                              (b.Enlargements.Count == 0 ? "none (no backend section requests one in this build)"
                                                         : string.Join("; ", b.Enlargements)));
        }

        Console.WriteLine("  size");
        foreach (var z in r.Size)
            Console.WriteLine($"    {z.Backend,-10} {z.Kind}: {z.Note}");

        foreach (var w in r.Warnings) Console.WriteLine($"  warning: {w}");
        foreach (var n in r.Notes)    Console.WriteLine($"  note: {n}");
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
