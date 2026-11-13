// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-icp-3 R-icp3-1 … R-icp3-5 — a cell instance cut out of a 3D problem, on a .c3d placing a layout and on a 3D .cem (whose problem
// is built in an in-memory 3D view of its layout, Em3dSetupProblem), by one construction.
//
// ONE CUT. Each layout instance of the view gets the setup's entries in the layout's own frame — as written when the view places one
// layout, the instance's name stripped off the front (Board/X1/Q3 → X1/Q3) when it places several — and the cut is ICP-2's
// (EmPlanarComponentPorts.Of → EmCellCutout.Apply): the same instances left out, the same pin verdicts, names and numbers as a
// Planar run of the same entries. The elaborator lowers that layout through EmGeometry.ForSetup with the setup (C3dElaborationOptions
// .LayoutCuts), so the metal and vias leave the lowered problem and never the .clay.
//
// THE PORT IS A DRAWN EDGE PORT'S SHEET. The cut labels are edge ports driving their whole face; the generator builds a sheet at each
// exactly as it builds one at a drawn edge port (Em3dGenerator.GenerateCutPorts — the same return walk, width rule and refusals), and
// the sheet is carried into the view through the layout instance's own placement (its element transform, the one its solids were
// lowered with). No landing, no new port kind.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>One placed layout's cut: the instance frame it is placed by, the setup in its own frame, and ICP-2's build of it.</summary>
public sealed record C3dCellCutLayout(C3dInstanceFrame Frame, EmSetup Setup, EmLayoutSource Source, Technology Technology,
                                      EmPlanarComponentPortBuild Build)
{
    /// <summary>What a port name and a sentence are prefixed with: the instance's path and a slash when the view places several
    /// layouts, nothing when it places one.</summary>
    public string Prefix { get; init; } = "";
}

/// <summary>A setup's cut over every layout a 3D view places, or why it cannot be made.</summary>
/// <param name="Layouts">Each placed layout whose entries name its cell instances, in the view's instance order.</param>
/// <param name="Claimed">The setup's entries the cut read: the footprint build leaves them out of its own list.</param>
/// <param name="Refusal">Why the setup's list cannot be cut (ICP-1's selection refusals, a layout placed twice and cut two ways).</param>
public sealed record C3dCellCutPlan(IReadOnlyList<C3dCellCutLayout> Layouts, IReadOnlySet<EmComponentPort> Claimed, string? Refusal)
{
    /// <summary>Whether any layout has an instance cut out of it.</summary>
    public bool Cuts => Layouts.Any(l => l.Build.Cut is not null);

    /// <summary>The setup each cut layout is lowered with, by full path (<see cref="C3dElaborationOptions.LayoutCuts"/>).</summary>
    public IReadOnlyDictionary<string, EmSetup> LayoutCuts
        => Layouts.Where(l => l.Build.Cut is not null)
                  .GroupBy(l => Path.GetFullPath(l.Frame.DocumentPath), StringComparer.Ordinal)
                  .ToDictionary(g => g.Key, g => g.First().Setup, StringComparer.Ordinal);
}

public static class C3dCellCuts
{
    /// <summary>
    /// The cut <paramref name="setup"/> makes of the layouts <paramref name="drawn"/> places. Null when the setup names no component
    /// port or the view places no layout (the footprint build says so). Reads the layouts — the one
    /// <paramref name="layoutsInMemory"/> holds for a path first, as the elaborator does — and writes nothing.
    /// </summary>
    /// <param name="layoutRegions">The elaborator's <see cref="C3dElaborator.LayoutRegions"/>: a layout solved only inside a region is
    /// cut inside it, as a Planar <c>.cem</c>'s is.</param>
    public static C3dCellCutPlan? Plan(EmSetup setup, C3dElaboration drawn, TechnologyCache cache, string? workspaceCws,
                                       IReadOnlyDictionary<string, LayoutView>? layoutsInMemory = null,
                                       IReadOnlyDictionary<string, EmSolveRegion>? layoutRegions = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(drawn);
        if (setup.ComponentPorts.Count == 0) return null;
        var frames = drawn.Instances.Where(f => f.Layout && File.Exists(f.DocumentPath)).ToList();
        if (frames.Count == 0) return null;
        bool several = frames.Count > 1;
        var claimed = new HashSet<EmComponentPort>();
        var layouts = new List<C3dCellCutLayout>();
        C3dCellCutPlan Refuse(string why) => new([], claimed, why);

        foreach (var frame in frames)
        {
            // The entries in this layout's frame: as written with one layout; with several, those that start with this instance's name.
            var mine = new List<(EmComponentPort Written, EmComponentPort Original)>();
            foreach (var c in setup.ComponentPorts)
            {
                if (!several || c.Cell is not null || c.Refdes.Trim() == EmSetup.AllComponents) { mine.Add((c, c)); continue; }
                string key = c.Refdes.Trim().Trim('/');
                string prefix = frame.Path + "/";
                if (key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    mine.Add((c with { Refdes = key[prefix.Length..] }, c));
            }
            if (mine.Count == 0) continue;

            string full = Path.GetFullPath(frame.DocumentPath);
            LayoutView view;
            if (layoutsInMemory?.GetValueOrDefault(full) is { } inMemory) view = inMemory;
            else try { view = LayoutPersistence.LoadFromFile(frame.DocumentPath); }
                catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
                { continue; }     // the elaboration refused it already, naming the instance
            if (TechnologyResolver.ResolveForDocument(view.TechRef, frame.DocumentPath, workspaceCws, cache).Resolution.Tech is not { } tech)
                continue;

            var own = setup.Clone();
            own.ComponentPorts = [.. mine.Select(m => m.Written)];
            // A .c3d instance takes the whole layout; only the in-memory view a .cem is built through hands its layout a region.
            own.SolveRegion = layoutRegions?.GetValueOrDefault(full);
            var source = new EmLayoutSource(frame.DocumentPath, view, tech, view.DbuPerMicron);
            if (EmPlanarComponentPorts.Of(own, source) is not { } build) continue;
            string at = several ? frame.Path + "/" : "";
            if (build.Refusal is { } refused) return Refuse(several ? $"{frame.Path}: {refused}" : refused);
            foreach (var (written, original) in mine)
                if (!ReferenceEquals(written, original) || build.InstanceEntries.Contains(written)) claimed.Add(original);
            layouts.Add(new C3dCellCutLayout(frame, own, source, tech, build) { Prefix = at });
        }

        // A layout placed twice is lowered once per cut: two placements cut two ways cannot share it.
        foreach (var g in layouts.Where(l => l.Build.Cut is not null).GroupBy(l => Path.GetFullPath(l.Frame.DocumentPath), StringComparer.Ordinal))
            if (g.Select(l => EmPlanarComponentPorts.SetupKey(l.Setup)).Distinct(StringComparer.Ordinal).Skip(1).Any())
                return Refuse($"'{Path.GetFileName(g.Key)}' is placed more than once in this 3D view ({string.Join(", ", g.Select(l => l.Frame.Path))}), " +
                              "and the cell instances named for each placement differ; a layout placed several times is cut the same way in each.");
        return new C3dCellCutPlan(layouts, claimed, null);
    }

    /// <summary>
    /// R-icp3-2 — the sheet at each routed pin face of the plan's cut layouts, as component ports numbered from
    /// <paramref name="firstNumber"/> in the cut's own order (ICP-2's: entry, then hierarchy, then pin), each carried into the view
    /// through its layout instance's placement. A pin the cut left out is not here (its sentence is among the warnings, word for word
    /// as a Planar run says it); a face the generator cannot put a sheet at is here, refused naming <c>path.pin</c>.
    /// </summary>
    public static (List<C3dComponentPort> Ports, List<string> Notes, List<string> Warnings) Ports(
        C3dCellCutPlan plan, C3dElaboration drawn, C3dDocument document, int firstNumber)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var ports = new List<C3dComponentPort>();
        var notes = new List<string>();
        var warnings = new List<string>();
        int next = firstNumber;
        double per = C3dLowering.Metres(1, document.DbuPerMicron);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);

        foreach (var layout in plan.Layouts)
        {
            var build = layout.Build;
            string at = layout.Prefix;
            notes.AddRange(build.Notes.Select(n => at.Length == 0 ? n : $"{layout.Frame.Path}: {n}"));
            warnings.AddRange(build.Warnings.Select(w => at.Length == 0 ? w : $"{layout.Frame.Path}: {w}"));
            var cut = build.Ports.Where(p => p.Cutout && p.Ok).ToList();
            if (cut.Count == 0) continue;

            // The generator over the cut layout: its drawn ports are built again and dropped; a cut port's number states no wave
            // kind or return of the setup's own ports (those name the setup's numbers, not these).
            var genSetup = layout.Setup.Clone();
            int firstCut = cut.Min(p => p.Number);
            genSetup.Ports3D = [.. genSetup.Ports3D.Where(p => p.Port < firstCut)];
            var g = Em3dGenerator.GenerateCutPorts(genSetup, layout.Source, layout.Technology);
            // The element transform the layout's solids were lowered with: its metres to the view's.
            var element = drawn.Provenance.Values.FirstOrDefault(p => p.InstancePath == layout.Frame.Path && p.Element is not null)?.Element;

            foreach (var cp in cut)
            {
                string name = at + cp.Name;
                C3dComponentPort Refused(string why)
                    => new(at + cp.Refdes, cp.Pin, null, null, 0, cp.WidthM, cp.AlongX, [], $"{name}: {why}", false)
                       { Cutout = true, Cell = cp.Cell, ClayPath = layout.Frame.DocumentPath, SchematicId = cp.SchematicId, Verdict = why };
                string? why = g.Problem is null ? g.Refusal ?? "the layout's 3D problem could not be built."
                            : g.PortsLeftOut.FirstOrDefault(l => l.Port == cp.Number)?.Why;
                var sheet = g.Problem?.Ports.FirstOrDefault(p => p.Number == cp.Number);
                if (why is null && sheet is null) why = "no sheet was built at its face.";
                if (why is null && element is null) why = "its layout instance places no solid to carry the sheet's position by.";
                if (why is not null) { ports.Add(Refused(why)); continue; }

                var t = element!.Value;
                (double X, double Y, double Z) W(Point3 q) => (t.M00 * q.X + t.M01 * q.Y + t.M02 * q.Z + t.Tx,
                                                               t.M10 * q.X + t.M11 * q.Y + t.M12 * q.Z + t.Ty,
                                                               t.M20 * q.X + t.M21 * q.Y + t.M22 * q.Z + t.Tz);
                var (ax, ay, az) = W(sheet!.Min);
                var (bx, by, bz) = W(sheet.Max);
                var ca = new C3dPoint3(D(Math.Min(ax, bx)), D(Math.Min(ay, by)), D(Math.Min(az, bz)));
                var cb = new C3dPoint3(D(Math.Max(ax, bx)), D(Math.Max(ay, by)), D(Math.Max(az, bz)));
                int flat = ca.X == cb.X ? 0 : ca.Y == cb.Y ? 1 : ca.Z == cb.Z ? 2 : -1;
                if (sheet.Annulus is not null || sheet.Quad is not null || flat < 0)
                { ports.Add(Refused("its sheet is not one flat rectangle in the 3D view (the layout instance is turned off the axes).")); continue; }
                var (plane, offset, u0, v0, u1, v1) = flat switch
                {
                    0 => (C3dPlane.YZ, ca.X, ca.Y, ca.Z, cb.Y, cb.Z),
                    1 => (C3dPlane.XZ, ca.Y, ca.X, ca.Z, cb.X, cb.Z),
                    _ => (C3dPlane.XY, ca.Z, ca.X, ca.Y, cb.X, cb.Y),
                };
                var port = new C3dPort
                {
                    Number = next++,
                    Name = name,
                    Kind = Em3dPortKind.Lumped,
                    Plane = plane,
                    Offset = offset,
                    Rect = new C3dRect { Min = new C3dPoint2(u0, v0), Size = new C3dPoint2(u1 - u0, v1 - v0) },
                    Z0 = cp.Z0 is { } z ? C3dPorts.FormatZ0(z) : "50",
                };
                ports.Add(new C3dComponentPort(at + cp.Refdes, cp.Pin, null, port, Math.Abs(sheet.Max.Z - sheet.Min.Z), cp.WidthM, cp.AlongX, [],
                                               null, false)
                {
                    Cutout = true, Cell = cp.Cell, ClayPath = layout.Frame.DocumentPath, SchematicId = cp.SchematicId, Verdict = "verified",
                });
            }
        }
        foreach (var r in ports.Where(p => p.Refusal is not null))
            warnings.Add($"{r.Refusal} {PinName(r)} is left out of this run; the other component ports are built.");
        int built = ports.Count(p => p.Ok);
        if (built > 0)
            notes.Add($"{built} cut face(s) of cell instances left out of the problem carry a lumped sheet from the face to its return, as a " +
                      "drawn edge port's does, as wide as the face; the instances and the sheets are in the solved problem only, not the layout.");
        return (ports, notes, warnings);

        static string PinName(C3dComponentPort p) => p.Pin is { } pin ? $"{p.Refdes}.{pin}" : p.Refdes;
    }
}
