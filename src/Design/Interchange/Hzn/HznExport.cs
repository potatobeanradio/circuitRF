// HZN-4 R-hzn4-2, -3 — the export as an operation: prepare a document (flatten what a `.hzn` has no hierarchy for), write it as a
// design, and put the design in a new project or in an existing one.
//
// This is the one function the File ▸ Export ▸ Hertzon Project… dialog and `convert x.c3d -o y.hzn` both call. The verb and the dialog
// hold no export logic of their own (a source scan holds it).

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>What the export wrote, or why it did not.</summary>
/// <param name="Path">The project written to; null when nothing was.</param>
/// <param name="Design">The design's name in the project.</param>
/// <param name="Wrote">True when the file now holds the design.</param>
/// <param name="Notes">What was flattened, written as built or left out as display state.</param>
/// <param name="Error">Why nothing was written.</param>
/// <param name="NameTaken">The error is that the project already has a design of that name (the GUI asks again; the CLI names the flag).</param>
public sealed record HznExportOutcome(string? Path, string Design, bool Wrote, IReadOnlyList<string> Notes, Diagnostic? Error, bool NameTaken = false)
{
    public bool Ok => Error is null && Wrote;
}

public static class HznExport
{
    /// <summary>The design name an export uses when none is given: the cell's folder name, else the file's.</summary>
    public static string DefaultDesignName(string c3dPath)
    {
        string full = Path.GetFullPath(c3dPath);
        string? dir = Path.GetDirectoryName(full);
        string? cell = dir is null ? null : C3dHierarchy.CellDirOf(full) is { } c ? Path.GetFileName(c) : null;
        return !string.IsNullOrEmpty(cell) ? cell : Path.GetFileNameWithoutExtension(full);
    }

    // ── the document to a design ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>.c3d</c> read from <paramref name="path"/> as a design. Flattens its instances (<see cref="Flatten"/>), resolves its
    /// technology and cell as the elaborator does, and writes it.
    /// </summary>
    public static C3dToHznResult FromFile(string path, string? designName = null, string? workspaceCws = null, GeometryKernel? kernel = null)
    {
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new(designName ?? DefaultDesignName(path), null, [],
                       [new HznRefusalItem(HznRefusalKind.Setting, "document", $"'{path}' could not be read: {e.Message}", [])], []);
        }
        return FromDocument(doc, path, designName, workspaceCws, kernel);
    }

    /// <summary><paramref name="doc"/> (not changed) as it stands, saved or not, which lives at <paramref name="path"/>.</summary>
    public static C3dToHznResult FromDocument(C3dDocument doc, string path, string? designName = null, string? workspaceCws = null,
                                              GeometryKernel? kernel = null, Technology? technology = null)
    {
        string name = designName is { Length: > 0 } ? designName : DefaultDesignName(path);
        var cache = new TechnologyCache();
        // A design held in a Hertzon project has no technology file: its materials are the project's, handed in.
        var tech = technology ?? TechnologyResolver.ResolveForDocument(doc.TechRef, path, workspaceCws, cache).Resolution.Tech;
        var notes = new List<string>();
        var flat = doc;
        if (doc.Instances.Count > 0)
        {
            var (flattened, refusal, flatNotes, flatTech) = Flatten(doc, path, tech, workspaceCws, cache);
            if (flattened is null)
                return new(name, null, [], [new HznRefusalItem(HznRefusalKind.Setting, "placed cells", refusal ?? "they cannot be flattened", [])], []);
            flat = flattened;
            notes.AddRange(flatNotes);
            tech = flatTech ?? tech;
        }
        var options = new HznExportOptions
        {
            DesignName = name, Technology = tech, Kernel = kernel, Notes = notes,
            Cell = C3dCell.Of(Path.GetFullPath(path)),
        };
        return C3dToHzn.Write(flat, options);
    }

    // ── flattening (D8) ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every level of <paramref name="doc"/>'s instances written into it as ordinary objects named <c>&lt;instance&gt;/&lt;object&gt;</c> (nested
    /// <c>&lt;a&gt;/&lt;b&gt;/&lt;object&gt;</c>), placements composed. A layout instance becomes the solids and sheets it elaborates to, as built,
    /// with the current values. A material the document's technology lacks is added to a COPY of it (the export writes the project's own
    /// materials, D6). The notes say what was flattened, exactly.
    /// </summary>
    public static (C3dDocument? Flat, string? Refusal, IReadOnlyList<string> Notes, Technology? Technology) Flatten(
        C3dDocument doc, string path, Technology? tech, string? workspaceCws, TechnologyCache cache)
    {
        var work = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
        int originalObjects = work.Objects.Count;
        int instances = 0;
        var fromLayout = new List<string>();
        var elaborator = new C3dElaborator();
        var materialsAdded = new List<TechMaterial>();
        for (int level = 0; work.Instances.Count > 0; level++)
        {
            if (level == 64) return (null, "The hierarchy is deeper than 64 levels.", [], tech);
            var e = elaborator.Elaborate(work, Path.GetFullPath(path), workspaceCws);
            var next = new List<C3dInstance>();
            var added = new List<C3dObject>();
            var nextGroups = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < work.Instances.Count; i++)
            {
                var inst = work.Instances[i];
                var r = C3dHierarchy.FlattenOne(work, path, i, e, tech, cache, workspaceCws, takenGroups: nextGroups);
                // A material the technology lacks: the export writes the project's own, so a copy of the technology that has it will do.
                for (int attempt = 0; r.Refusal is not null && r.MissingMaterials.Count > 0 && attempt < 4; attempt++)
                {
                    tech = tech is null ? new Technology() : TechPersistence.Clone(tech);
                    foreach (var m in r.MissingMaterials)
                        if (tech.FindMaterial(m.Name) is null) { tech.Materials.Add(m); materialsAdded.Add(m); }
                    r = C3dHierarchy.FlattenOne(work, path, i, e, tech, cache, workspaceCws, takenGroups: nextGroups);
                }
                if (r.Refusal is { } why) return (null, why, [], tech);
                instances++;
                if (inst.View == C3dInstanceView.Layout) fromLayout.Add(inst.Name);
                foreach (var o in r.Objects) o.Name = Slashed(o.Name, inst.Name);
                foreach (var ni in r.Instances) ni.Name = Slashed(ni.Name, inst.Name);
                added.AddRange(r.Objects);
                next.AddRange(r.Instances);
                foreach (var ni in r.Instances) nextGroups.UnionWith(C3dGroups.Segments(ni.Group));
                work.Objects.AddRange(r.Objects);
            }
            work.Instances = next;
        }
        var notes = new List<string>();
        int objects = work.Objects.Count - originalObjects;
        string line = $"flattened: {instances} instance{(instances == 1 ? "" : "s")}, {objects} object{(objects == 1 ? "" : "s")}";
        if (fromLayout.Count > 0) line += $"; built from layout: {string.Join(", ", fromLayout)}";
        notes.Add(line);
        if (materialsAdded.Count > 0)
            notes.Add($"materials the technology lacked were taken from the cells flattened: {string.Join(", ", materialsAdded.Select(m => m.Name))}");
        return (work, null, notes, tech);
    }

    /// <summary>A flattened part is named <c>&lt;instance&gt;_&lt;name&gt;</c> by the flatten; a Hertzon design names it <c>&lt;instance&gt;/&lt;name&gt;</c>.</summary>
    private static string Slashed(string flattened, string instance) =>
        flattened.StartsWith(instance + "_", StringComparison.Ordinal) ? instance + "/" + flattened[(instance.Length + 1)..] : flattened;

    /// <summary><paramref name="design"/> under another name: the design's name is a string in its tree and nothing else depends on it.</summary>
    public static C3dToHznResult Renamed(C3dToHznResult design, string name)
    {
        design.Json?.SetString("name", name);
        return design with { Design = name };
    }

    // ── a new project ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The text of an empty project, laid out as Hertzon lays one out: one space of indent, the format and version last, no
    /// final newline.</summary>
    private const string EmptyProject = "{\n \"name\": \"\",\n \"designs\": [],\n \"variables\": {},\n \"materials\": [],\n \"format\": \"hertzon-project\",\n \"version\": 2\n}";

    /// <summary>Writes <paramref name="design"/> as the only design of a new project at <paramref name="path"/> (the project's name is the
    /// file's). An existing file at the path is replaced, as any Save As.</summary>
    public static HznExportOutcome ToNewFile(C3dToHznResult design, string path)
    {
        if (design.Json is null) return Refused(design);
        var doc = HznJsonDocument.Parse(EmptyProject);
        var root = (HznJsonObject)doc.Root;
        root.SetString("name", Path.GetFileNameWithoutExtension(path));
        root.GetArray("designs")!.Add(design.Json);
        var materials = root.GetArray("materials")!;
        foreach (var m in design.Materials) materials.Add(m);
        try
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            AtomicFile.WriteAllBytes(full, doc.ToBytes());
            return new(full, design.Design, true, design.Notes, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, design.Design, false, design.Notes, HznExportDiagnostics.WriteFailed(path, e.Message));
        }
    }

    // ── an existing project ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends <paramref name="design"/> to the project at <paramref name="path"/> through <see cref="HznFile"/>: every byte of the
    /// existing file is kept, nothing is overwritten (D9). A design name already in the file is <see cref="HznExportOutcome.NameTaken"/>;
    /// a material of the same name with other properties is a refusal listing each conflict; one with identical properties is reused.
    /// </summary>
    public static HznExportOutcome IntoExisting(C3dToHznResult design, string path)
    {
        if (design.Json is null) return Refused(design);
        var read = HznFile.Read(path);
        if (!read.Ok) return new(null, design.Design, false, design.Notes, read.Refusal);
        var file = read.File!;
        var project = file.Project;

        if (project.Designs.Any(d => string.Equals(d.Name, design.Design, StringComparison.Ordinal)))
            return new(null, design.Design, false, design.Notes, HznExportDiagnostics.DesignNameTaken(path, design.Design, [.. project.Designs.Select(d => d.Name ?? "")]), NameTaken: true);

        var conflicts = new List<HznRefusalItem>();
        var toAdd = new List<HznJsonObject>();
        var ours = design.Materials.Select(m => (Json: m, Values: HznMaterialValues.Of(new HznMaterial(m))!)).ToDictionary(t => t.Values.Name, StringComparer.Ordinal);
        var used = design.Json.GetArray("objects")!.Items.OfType<HznJsonObject>().Select(o => o.GetString("material")).OfType<string>().Distinct(StringComparer.Ordinal);
        foreach (string name in used)
        {
            var theirs = project.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            HznMaterialValues? mine = ours.TryGetValue(name, out var o) ? o.Values : C3dToHznWriter.BuiltIn.FirstOrDefault(b => b.Name == name);
            if (mine is null) continue;
            if (theirs is null)
            {
                if (ours.TryGetValue(name, out var add)) toAdd.Add(add.Json);
                continue;
            }
            var theirValues = HznMaterialValues.Of(theirs);
            var diff = theirValues is null
                ? ["its properties are expressions, a dispersion or a tensor that cannot be compared"]
                : mine.DifferencesFrom(theirValues, "here", "in the project");
            if (diff.Count > 0)
                conflicts.Add(new HznRefusalItem(HznRefusalKind.Material, $"material '{name}'", string.Join("; ", diff), [design.Design]));
        }
        if (conflicts.Count > 0)
            return new(null, design.Design, false, design.Notes, HznExportDiagnostics.MaterialConflicts(path, conflicts));

        if (project.Json.GetArray("designs") is not { } designs)
        {
            designs = new HznJsonArray();
            project.Json.Set("designs", designs);
        }
        designs.Add(design.Json);
        if (toAdd.Count > 0)
        {
            if (project.Json.GetArray("materials") is not { } materials)
            {
                materials = new HznJsonArray();
                project.Json.Set("materials", materials);
            }
            foreach (var m in toAdd) materials.Add(m);
        }
        var saved = file.Save();
        return saved.Saved
            ? new(file.Path, design.Design, true, design.Notes, null)
            : new(null, design.Design, false, design.Notes, saved.Refusal);
    }

    private static HznExportOutcome Refused(C3dToHznResult design) =>
        new(null, design.Design, false, design.Notes, HznExportDiagnostics.Refused(design.Design, design.Refusals));

    /// <summary>The design's own refusal, as a diagnostic.</summary>
    public static Diagnostic? Refusal(C3dToHznResult result) =>
        result.Ok ? null : HznExportDiagnostics.Refused(result.Design, result.Refusals);
}

/// <summary>Every refusal the export returns. Returned, never posted: the GUI raises them, the CLI writes them to stderr.</summary>
public static class HznExportDiagnostics
{
    /// <summary>R-hzn4-1 — the document cannot be written as a Hertzon design; every item is listed.</summary>
    public static Diagnostic Refused(string design, IReadOnlyList<HznRefusalItem> items) => Diagnostic.Create(
        "hzn.export.refused",
        DiagnosticSeverity.Error,
        "Design '{design}' cannot be written as a Hertzon design: {count} unsupported item(s), so nothing was written.\n{items}",
        ("design", design), ("count", items.Count), ("items", HznRefusalText.Render(items)));

    /// <summary>R-hzn4-3 — D9: a design name already in the project is never overwritten.</summary>
    public static Diagnostic DesignNameTaken(string file, string design, IReadOnlyList<string> known) => Diagnostic.Create(
        "hzn.export.name-taken",
        DiagnosticSeverity.Error,
        "'{file}' already has a design named '{design}' (its designs: {known}). Nothing was written; name the new design with --design-name.",
        ("file", file), ("design", design), ("known", string.Join(", ", known)));

    /// <summary>R-hzn4-3 — D9: a material of the same name with other properties.</summary>
    public static Diagnostic MaterialConflicts(string file, IReadOnlyList<HznRefusalItem> conflicts) => Diagnostic.Create(
        "hzn.export.material-conflict",
        DiagnosticSeverity.Error,
        "'{file}' defines materials this design also uses, with other properties, so nothing was written.\n{items}",
        ("file", file), ("items", HznRefusalText.Render(conflicts)));

    public static Diagnostic WriteFailed(string file, string reason) => Diagnostic.Create(
        "hzn.export.write-failed",
        DiagnosticSeverity.Error,
        "'{file}' could not be written: {reason}",
        ("file", file), ("reason", reason));
}
