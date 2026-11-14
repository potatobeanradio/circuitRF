// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-8 R-sdl8-2/3/4 — one evaluation of an EM cell whose EM view is a setup embedded in its 3D view (.c3d). The
// layout is re-planned exactly as for a .cem (SDL-6 D2, in memory, hand edits kept); the 3D view is then elaborated at the
// point with that layout in place of its .clay:
//   D2 — a LIVE layout instance follows the regenerated copper with no extra work; a FLATTENED one is a copy and follows
//        the schematic only through linked VARs, which the evaluation says per flattened group.
//   D3 — a 3D VAR named like a cell parameter or a schematic VAR takes that name's value at the point (unless it says
//        Linked: false), so one name drives the copper and any 3D-only body that uses it.
//   D4 — the 3D view's ports are refreshed from the regenerated layout's port labels (Update Ports from Layout's path).
//   D5 — the key covers the whole 3D problem (EmCellCache.KeyOf3D).
//   D6 — the conductor bodies, after elaboration, join the connectivity signature: a body that leaves its pad or touches
//        copper it did not touch at the start is infeasible, and costs no solve.
//   D7 — the mesh is held (EmMeshHold).
// The user's .c3d, .clay and .csch are never written: the document is read and changed in memory only.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

/// <summary>brief-sdl-8 — the 3D half of one evaluation's geometry.</summary>
/// <param name="Document">The point's 3D document: ports refreshed from the regenerated layout, VARs and fields resolved at
/// the point, mesh regions held. In memory only.</param>
/// <param name="Elaborator">The elaborator that reads the regenerated layout from memory and binds the point's values; a
/// solve reuses it, so the problem is built once.</param>
/// <param name="Setup">The setup as every evaluation solves it: the embedded setup with its mesh held (D7).</param>
/// <param name="Estimate">What solving it costs, from the solver's own size estimate; null when the solver gives none.</param>
/// <param name="SmallestElementM">The smallest element the held mesh makes, metres: D7's step floor.</param>
/// <param name="Bodies">D6 — each conductor body holding a body of the 3D view's own, by its members.</param>
/// <param name="Notes">D2's line per flattened group and D7's line on what is held.</param>
public sealed record EmThreeDGeometry(
    C3dDocument Document, C3dElaborator Elaborator, EmSetup Setup, EmSolveEstimate? Estimate, double? SmallestElementM,
    IReadOnlyList<string> Bodies, IReadOnlyList<string> Notes)
{
    /// <summary>brief-icp-8 R-icp8-4 — the problem's ports in its Touchstone's order, a component port with its part: what a Path A
    /// bench wires the parts on.</summary>
    public IReadOnlyList<CircuitRF.Design.Layout.Em.EmCoSimPort>? ResultPorts { get; init; }
}

public static partial class EmEvaluation
{
    private static EmCellGeometry? ThreeDGeometry(
        EmCellTarget target, SchematicEditModel model, IReadOnlyDictionary<string, (double Value, string Unit)> saved,
        EmLayoutSource source, IReadOnlyDictionary<string, Value> applied, List<string> notes, IReadOnlyList<string> unreached,
        ConnectivitySignature connectivity, out string? refusal)
    {
        refusal = null;
        string c3dPath = target.C3dPath!;
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(c3dPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            refusal = $"{target.CellName}'s 3D view {Path.GetFileName(c3dPath)} could not be read ({e.Message}).";
            return null;
        }

        var now = CellVariables.Values(model);
        var layouts = new Dictionary<string, LayoutView>(StringComparer.Ordinal) { [Path.GetFullPath(target.ClayPath)] = source.View };
        var cache = new TechnologyCache();

        // D4 — the ports follow the regenerated layout's labels. A port that cannot follow is a refusal, not a stale port.
        var translated = C3dHierarchy.PortsFromLayouts(doc, c3dPath, target.CwsPath, cache, layouts);
        if (PortsThatCannotFollow(doc, c3dPath, target, translated, cache) is { } stuck)
        {
            refusal = $"{target.CellName}'s 3D view cannot follow its layout's ports at these values: {stuck}";
            return null;
        }
        C3dHierarchy.UpdatePorts(doc, translated);

        // D3 — the names the 3D view takes from the cell at this point, and at the design's own values (D7's anchor).
        var (cell, overrides, linked) = LinkedNames(doc, c3dPath, now);
        var (_, savedOverrides, _) = LinkedNames(doc, c3dPath, saved);

        // D7 — mesh regions at the design's own values; the setup's refinement held.
        var savedDoc = CopyOf(doc);
        C3dResolver.Resolve(savedDoc, cell, savedOverrides);
        var heldRegions = EmMeshHold.HoldRegions(doc, savedDoc);
        var holdNotes = new List<string>();
        // brief-icp-8 R-icp8-4 — the parts that are not copper are component ports here too, built through ICP-3's cut.
        var runSetup = target.RunSetup;
        double? savedMinCell = runSetup.Solver3D.Runs(OpenEmsSolver.Id) && runSetup.OpenEms?.MinCellUm is null
            ? SavedMinCell(target, savedDoc, cell, savedOverrides) : null;
        var held = EmMeshHold.Hold(runSetup, savedMinCell, holdNotes);
        if (heldRegions.Count > 0)
            holdNotes.Add($"Mesh region(s) {string.Join(", ", heldRegions.Select(r => $"'{r}'"))} keep the element size the design's own values give.");

        var elaborator = new C3dElaborator(cache) { LayoutsInMemory = layouts, TopCell = cell, TopOverrides = overrides };
        var generated = C3dProblemAssembly.Assemble(held, doc, c3dPath, target.CwsPath, elaborator);
        if (!generated.Ok || generated.Problem is not { } problem)
        {
            refusal = $"{target.CellName}'s 3D view does not build at these values: {generated.Refusal}";
            return null;
        }
        if (EmCellParts.Refusal(target.CellName, generated.ComponentPorts) is { } partsRefused)
        {
            refusal = partsRefused;
            return null;
        }

        // D6 — which bodies of the 3D view's own touch what, after elaboration.
        var bodies = Bodies(problem, doc);
        connectivity = connectivity with { Bodies = bodies };

        // D2 — flattened groups, and names that reach nothing in the 3D view.
        var resolution = C3dResolver.Resolve(CopyOf(doc), cell, overrides);
        var used = linked.Where(n => !resolution.Infos.Contains($"VAR '{n}' is used by no dimension in this 3D view.")).ToList();
        var lines = FlattenedLines(doc, c3dPath, target, used);
        var reached = unreached.ToList();
        if (!PlacesLayoutLive(doc, c3dPath, target))
            foreach (var row in CellVariables.Of(target.CellDir))
                if (!used.Contains(row.Name, StringComparer.Ordinal))
                    reached.Add($"{row.Name} does not reach {target.CellName}'s 3D view: it places no live instance of the layout, " +
                                $"and no VAR of the view linked to {row.Name} is used by a dimension.");

        var (techDoc, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, c3dPath, target.CwsPath, cache);
        string key = EmCellCache.KeyOf3D(source, doc, held, techDoc.Tech, SolverVersion(held),
                                         KeyInputs(generated, target.ClayPath, c3dPath, cell.CcellPath, techDoc.ResolvedPath),
                                         target.WorkspaceDir);
        var three = new EmThreeDGeometry(doc, elaborator, held, Estimate3D(held, problem), EmMeshHold.SmallestElementM(held, problem),
                                         bodies, [.. lines, .. holdNotes])
        {
            ResultPorts = generated.ResultPorts ?? CircuitRF.Design.Layout.Em.EmCoSimNetlist.PortsOf(problem.Ports),
        };
        return new EmCellGeometry(target, source, key, EmCellCache.Find(target.WorkspaceDir, target.CellName, key),
                                  reached.Distinct(StringComparer.Ordinal).ToList(), [.. notes, .. lines], applied, connectivity)
        {
            ThreeD = three,
        };
    }

    /// <summary>
    /// D3 — the 3D view's cell as an evaluation states it: its own parameters, plus one for each VAR of the view that is
    /// named like a value of <paramref name="values"/> and not unlinked; and every such name's value at the point, as an
    /// override in base SI. Returns the names a VAR of the view takes from the schematic.
    /// </summary>
    internal static (C3dCell Cell, Dictionary<string, C3dOverride> Overrides, List<string> Linked) LinkedNames(
        C3dDocument doc, string c3dPath, IReadOnlyDictionary<string, (double Value, string Unit)> values)
    {
        var own = C3dCell.Of(c3dPath);
        var parameters = own.Parameters.ToList();
        var overrides = new Dictionary<string, C3dOverride>(StringComparer.Ordinal);
        var linked = new List<string>();
        foreach (var p in own.Parameters)
            if (values.TryGetValue(p.Name, out var v)) overrides[p.Name] = Override(v);
        foreach (var var3 in doc.Variables)
        {
            if (var3.Linked == false || !values.TryGetValue(var3.Name, out var v)) continue;
            if (own.Parameter(var3.Name) is null)
                parameters.Add(new CcellParameter { Name = var3.Name, DefaultExpression = var3.Expression, Unit = var3.Unit ?? "" });
            overrides[var3.Name] = Override(v);
            linked.Add(var3.Name);
        }
        string? cellDir = own.CellDir ?? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(c3dPath)));
        return (new C3dCell(cellDir, own.CcellPath, parameters), overrides, linked);

        static C3dOverride Override((double Value, string Unit) v)
        {
            // The value is base SI; its binding is marked with the base unit (scale 1), so a field's site unit is skipped.
            string? unit = null;
            if (v.Unit is { Length: > 0 } u)
            {
                string engine = C3dUnits.Engine(u, out _) ?? UnitNormalizer.ToEngineUnit(u);
                string b = Units.BaseUnit(engine);
                unit = b == "metre" ? C3dResolver.BaseUnit : b.Length > 0 && Units.IsKnown(b) ? b : null;
            }
            return new C3dOverride(new Value(v.Value), unit);
        }
    }

    /// <summary>D6 — every conductor body of <paramref name="problem"/> that holds an object of the 3D view's own (not one
    /// made from a placed layout), as its members joined by <c>+</c>, sorted.</summary>
    internal static IReadOnlyList<string> Bodies(Em3dProblem problem, C3dDocument doc)
    {
        var layoutInstances = doc.Instances.Where(i => i.View == C3dInstanceView.Layout).Select(i => i.Name).ToList();
        bool FromLayout(string m) => layoutInstances.Any(n => m.StartsWith(n + "/", StringComparison.Ordinal) ||
                                                              m.StartsWith(n + "[", StringComparison.Ordinal));
        var result = new List<string>();
        foreach (var b in Em3dBodies.Group(problem).Bodies)
            if (b.Members.Any(m => !FromLayout(m)))
                result.Add(string.Join("+", b.Members.Order(StringComparer.Ordinal)));
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>Whether the 3D view places <paramref name="target"/>'s own layout as a live instance.</summary>
    private static bool PlacesLayoutLive(C3dDocument doc, string c3dPath, EmCellTarget target)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(c3dPath))!;
        return doc.Instances.Any(i => i.View == C3dInstanceView.Layout &&
                                      ExternalCellRef.ResolveCellDir(i.CellRef, baseDir) is { } d &&
                                      string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)),
                                                    Path.TrimEndingDirectorySeparator(target.CellDir), StringComparison.Ordinal));
    }

    /// <summary>
    /// D2 — one line per group of objects flattened out of <paramref name="target"/>'s layout: they are a copy, and follow the
    /// schematic only where their dimensions use a linked VAR.
    /// </summary>
    internal static IReadOnlyList<string> FlattenedLines(C3dDocument doc, string c3dPath, EmCellTarget target, IReadOnlyList<string> linked)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(c3dPath))!;
        bool Mine(string? cellRef) => cellRef is { Length: > 0 } && ExternalCellRef.ResolveCellDir(cellRef, baseDir) is { } d &&
                                      string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)),
                                                    Path.TrimEndingDirectorySeparator(target.CellDir), StringComparison.Ordinal);
        string follows = linked.Count == 0
            ? "no VAR of the 3D view linked to the schematic is used, so it follows none of the schematic's values"
            : $"it follows {Join(linked)} only where its dimensions use them";
        var lines = new List<string>();
        foreach (var g in doc.Objects.Where(o => Mine(o.FlattenedFrom)).GroupBy(o => o.Group ?? "", StringComparer.Ordinal))
        {
            string what = g.Key.Length > 0
                ? $"Group {C3dGroups.NameOf(g.Key)} was flattened from the layout"
                : $"{g.Count()} object{(g.Count() == 1 ? "" : "s")} ({string.Join(", ", g.Take(3).Select(o => $"'{o.Name}'"))}{(g.Count() > 3 ? ", …" : "")}) " +
                  $"{(g.Count() == 1 ? "was" : "were")} flattened from the layout";
            lines.Add($"{what}; {follows}.");
        }
        return lines;

        static string Join(IReadOnlyList<string> n) => n.Count == 1 ? n[0] : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[^1];
    }

    private static C3dDocument CopyOf(C3dDocument d) => C3dPersistence.Deserialize(C3dPersistence.Serialize(d));

    /// <summary>
    /// D5 — the files the elaboration read that the key does not already hold: everything but <paramref name="keyedAbove"/>
    /// (the regenerated layout, which the elaboration took from memory, and the top document, its cell and its technology,
    /// whose effect the key holds as resolved numbers and physics).
    /// </summary>
    internal static IReadOnlyList<string> KeyInputs(CircuitRF.Design.Layout.Em3d.Em3dGenerationResult generated, params string?[] keyedAbove)
    {
        var skip = keyedAbove.Where(p => p is { Length: > 0 }).Select(p => Path.GetFullPath(p!)).ToHashSet(StringComparer.Ordinal);
        return [.. generated.FilesRead.Where(f => !skip.Contains(Path.GetFullPath(f)))];
    }

    /// <summary>
    /// D4 — why the 3D view's ports cannot be refreshed from the layout at this point, or null when they can. Compared
    /// with the same translation of the saved layout: a port label that translates there but is left out here would leave
    /// its port where the saved layout put it; and a port linked to a label that moves here, but rotated in the 3D view,
    /// is one Update Ports from Layout does not move. Either way the solve would put a port where the copper no longer is.
    /// </summary>
    internal static string? PortsThatCannotFollow(C3dDocument doc, string c3dPath, EmCellTarget target, C3dLayoutPorts translated,
                                                  TechnologyCache cache)
    {
        var rotated = doc.Ports.Where(p => p.Angle != 0 && p.FromLayout is not null).ToList();
        if (translated.LeftOut.Count == 0 && rotated.Count == 0) return null;
        var saved = C3dHierarchy.PortsFromLayouts(doc, c3dPath, target.CwsPath, cache);
        static string Key(string instance, int number) => instance + "\u0001" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = new Dictionary<string, C3dPort>(StringComparer.Ordinal);
        foreach (var p in translated.Ports) now.TryAdd(Key(p.Instance, p.LayoutNumber), p.Port);
        var before = new Dictionary<string, C3dPort>(StringComparer.Ordinal);
        foreach (var p in saved.Ports) before.TryAdd(Key(p.Instance, p.LayoutNumber), p.Port);

        var reasons = new List<string>();
        var lost = saved.Ports.Where(p => !now.ContainsKey(Key(p.Instance, p.LayoutNumber)))
                              .Select(p => $"{p.Instance}'s label {p.LayoutNumber}").ToList();
        if (lost.Count > 0)
            reasons.Add($"port {(lost.Count == 1 ? "label" : "labels")} {string.Join(", ", lost)} could not be placed " +
                        $"({string.Join("; ", translated.LeftOut)})");
        foreach (var p in rotated)
        {
            string k = Key(p.FromLayout!.Instance, p.FromLayout.Port);
            if (now.TryGetValue(k, out var here) && before.TryGetValue(k, out var there) &&
                !(here.Plane == there.Plane && here.Offset == there.Offset && here.Rect.Min == there.Rect.Min && here.Rect.Size == there.Rect.Size))
                reasons.Add($"port {p.Number} is rotated in the 3D view, so it stays where it is drawn while {p.FromLayout.Instance}'s " +
                            $"label {p.FromLayout.Port} moves");
        }
        return reasons.Count == 0 ? null : string.Join("; ", reasons) + ".";
    }

    /// <summary>D5 — the solvers' identities and versions, as this machine has them; memoised per choice. A stand-in solver is
    /// its own version.</summary>
    private static string SolverVersion(EmSetup setup)
    {
        if (SolverStandIn is not null) return "stand-in";
        string k = setup.Solver3D.ToString();
        lock (_versionsGate)
        {
            if (_versions.TryGetValue(k, out var hit)) return hit;
            string v;
            try
            {
                v = string.Join(",", SolverDiscovery.ReadinessFor(setup.Solver3D, setup)
                    .Select(r => $"{r.Tool}={r.Installation?.Version ?? "absent"}"));
            }
            catch (Exception e) when (e is not OperationCanceledException) { v = "unknown"; }
            return _versions[k] = v;
        }
    }

    private static readonly Dictionary<string, string> _versions = new(StringComparer.Ordinal);
    private static readonly Lock _versionsGate = new();

    /// <summary>D8 — the solver's own size estimate for the point: Palace's element count and memory, openEMS's exact grid and
    /// its time; EMerge gives none, and neither does a setup whose estimate cannot be made.</summary>
    private static EmSolveEstimate? Estimate3D(EmSetup setup, Em3dProblem p)
    {
        int points = 0;
        try { points = setup.ExpandFrequencies().Length; } catch (Exception) { }
        try
        {
            if (setup.Solver3D.Runs(PalaceSolver.Id) && Em3dRunService.EstimatePalace(p, PalaceSettings.Resolve(setup.Palace)) is { } est)
                return new EmSolveEstimate((int)Math.Min(int.MaxValue, est.Unknowns), est.MemoryBytes ?? 0, null, points, "Palace");
            if (setup.Solver3D.Runs(OpenEmsSolver.Id))
            {
                var g = FdtdGrid.Build(p, CemOpenEms.ResolveGrid(setup.OpenEms));
                if (g.Refusal is null)
                    return new EmSolveEstimate((int)Math.Min(int.MaxValue, g.Cells), g.MemoryBytes,
                                               OpenEmsTimeEstimate.Seconds(g, p.Ports.Count, OpenEmsTimeEstimate.Cores(null)), points, "openEMS");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return null;
    }

    /// <summary>D7 — openEMS's merge distance at the design's own values: its saved layout and the view resolved there.
    /// Memoised on the files it reads.</summary>
    private static double? SavedMinCell(EmCellTarget target, C3dDocument savedDoc, C3dCell cell, Dictionary<string, C3dOverride> savedOverrides)
    {
        string k = string.Join("|", target.C3dPath, Stamp(target.C3dPath!), target.ClayPath, Stamp(target.ClayPath),
                               target.SchematicPath, Stamp(target.SchematicPath), target.C3dSetup);
        lock (_versionsGate) if (_minCells.TryGetValue(k, out var hit)) return hit;
        double? m = null;
        try
        {
            var e = new C3dElaborator { TopCell = cell, TopOverrides = savedOverrides };
            var g = C3dProblemAssembly.Assemble(target.RunSetup, savedDoc, target.C3dPath!, target.CwsPath, e);
            if (g.Problem is { } p && FdtdGrid.Build(p, CemOpenEms.ResolveGrid(target.RunSetup.OpenEms)) is { Refusal: null } grid) m = grid.MinCellM;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        lock (_versionsGate) _minCells[k] = m;
        return m;

        static string Stamp(string path) { try { return File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture); } catch { return "-"; } }
    }

    private static readonly Dictionary<string, double?> _minCells = new(StringComparer.Ordinal);
}
