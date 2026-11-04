// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-10 D1 — one evaluation of a 3D-PARAMETRIC cell: no schematic drives its geometry, so nothing is planned. Its
// 3D view is elaborated at the point with every VAR linked to a value of the cell taking that value (SDL-8 D3's rule), the
// mesh held at the view's own values (SDL-8 D7), and the problem keyed as any 3D cell's is (SDL-8 D5). A layout the view
// places is read as saved: it cannot follow the cell's variables. The user's .c3d is read and changed in memory only.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

public static partial class EmEvaluation
{
    private static EmCellGeometry? ThreeDOnlyGeometry(EmCellTarget target, IReadOnlyDictionary<string, Value> values, out string? refusal)
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

        // The point's values, each with the unit the 3D view states for it (the cell's parameter's otherwise), so a base-SI
        // value is bound as base SI.
        var cellParams = C3dCell.OfFolder(target.CellDir).Parameters;
        var now = new Dictionary<string, (double Value, string Unit)>(StringComparer.Ordinal);
        foreach (var (name, v) in values)
        {
            if (v.Kind != ValueKind.Real) continue;
            string unit = doc.Variables.FirstOrDefault(x => x.Name == name)?.Unit
                          ?? cellParams.FirstOrDefault(p => p.Name == name)?.Unit ?? "";
            now[name] = (v.AsReal(), unit);
        }

        var cache = new TechnologyCache();
        var (cell, overrides, linked) = LinkedNames(doc, c3dPath, now);
        var (_, ownOverrides, _) = LinkedNames(doc, c3dPath, new Dictionary<string, (double, string)>());

        // SDL-8 D7 — mesh regions at the view's own values; the setup's refinement held.
        var savedDoc = CopyOf(doc);
        C3dResolver.Resolve(savedDoc, cell, ownOverrides);
        var heldRegions = EmMeshHold.HoldRegions(doc, savedDoc);
        var holdNotes = new List<string>();
        var held = EmMeshHold.Hold(target.Setup, null, holdNotes);
        if (heldRegions.Count > 0)
            holdNotes.Add($"Mesh region(s) {string.Join(", ", heldRegions.Select(r => $"'{r}'"))} keep the element size the 3D view's own values give.");

        var elaborator = new C3dElaborator(cache) { TopCell = cell, TopOverrides = overrides };
        var generated = C3dProblemAssembly.Assemble(held, doc, c3dPath, target.CwsPath, elaborator);
        if (!generated.Ok || generated.Problem is not { } problem)
        {
            refusal = $"{target.CellName}'s 3D view does not build at these values: {generated.Refusal}";
            return null;
        }

        var bodies = Bodies(problem, doc);
        var connectivity = new ConnectivitySignature([], [], []) { Bodies = bodies };
        var (techDoc, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, c3dPath, target.CwsPath, cache);
        var empty = new EmLayoutSource(c3dPath, new LayoutView(), null, LayoutUnits.DefaultDbuPerMicron);
        string key = EmCellCache.KeyOf3D(empty, doc, held, techDoc.Tech, SolverVersion(held),
                                         KeyInputs(generated, c3dPath, cell.CcellPath, techDoc.ResolvedPath), target.WorkspaceDir);

        var notes = new List<string>();
        if (doc.Instances.Any(i => i.View == C3dInstanceView.Layout))
            notes.Add($"{target.CellName}'s 3D view places a layout, which it reads as saved: no schematic drives that copper.");
        var three = new EmThreeDGeometry(doc, elaborator, held, Estimate3D(held, problem), EmMeshHold.SmallestElementM(held, problem),
                                         bodies, holdNotes);
        var applied = values.Where(kv => linked.Contains(kv.Key) || cellParams.Any(p => p.Name == kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new EmCellGeometry(target, empty, key, EmCellCache.Find(target.WorkspaceDir, target.CellName, key),
                                  [], notes, applied, connectivity)
        {
            ThreeD = three,
        };
    }
}
