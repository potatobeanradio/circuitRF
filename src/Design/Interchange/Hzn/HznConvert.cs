// HZN-6 R-hzn6-1 — a Hertzon 3D design written into a cell as a native 3D view.
//
// This is the ONE function the GUI's Convert to .c3d dialog and `convert <file.hzn> -o <cell folder>` both call: the
// translation is HznToC3d's, the cell is CellCreate's, and nothing here decides what a Hertzon property means. What it
// adds is the placement (a new cell, or an existing cell's 3D view), the rule for the technology's materials, and the
// STEP files a design names. The source `.hzn` is only ever read.
//
// Every refusal is decided BEFORE anything is written, so a refused conversion leaves no half-made cell behind.

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>What to convert and where it goes.</summary>
/// <param name="HznPath">The Hertzon project. Read, never written.</param>
/// <param name="Design">The 3D design; null takes the project's only one, and refuses when there are several.</param>
/// <param name="CellDir">The cell folder. Absent: a new cell is created there (its parent must exist). Present: the cell's 3D
/// view is written into it.</param>
/// <param name="AddMaterials">Add the materials the workspace technology lacks to it. Off: they are reported and nothing is
/// written to the technology.</param>
/// <param name="ReplaceView">Overwrite the cell's 3D view of this name when it exists. Off: that is a refusal.</param>
public sealed record HznConvertRequest(string HznPath, string? Design, string CellDir, bool AddMaterials = false, bool ReplaceView = false);

/// <summary>What <see cref="HznConvert.Convert"/> did, or the refusal that stopped it before anything was written.</summary>
public sealed class HznConvertResult
{
    public Diagnostic? Refusal { get; init; }
    public bool Ok => Refusal is null;

    /// <summary>The design converted.</summary>
    public string Design { get; init; } = "";
    public string? CellDir { get; init; }
    /// <summary>The written <c>.c3d</c>.</summary>
    public string? ViewPath { get; init; }
    /// <summary>True when the cell did not exist and was created.</summary>
    public bool CellCreated { get; init; }
    /// <summary>True when a 3D view of that name was overwritten.</summary>
    public bool ViewReplaced { get; init; }
    /// <summary>The materials the design uses that the workspace technology does not define.</summary>
    public IReadOnlyList<string> MissingMaterials { get; init; } = [];
    /// <summary>The materials written into the technology (a subset of <see cref="MissingMaterials"/>; empty unless asked).</summary>
    public IReadOnlyList<string> AddedMaterials { get; init; } = [];
    /// <summary>The technology file the materials went into.</summary>
    public string? TechnologyPath { get; init; }
    /// <summary>The STEP files copied into the cell's <c>3d/</c> folder.</summary>
    public IReadOnlyList<string> StepFiles { get; init; } = [];
    /// <summary>Everything worth saying that is not a refusal: the translation's notes, the missing materials.</summary>
    public IReadOnlyList<Diagnostic> Findings { get; init; } = [];
}

public static class HznConvert
{
    /// <summary>Converts <paramref name="request"/>'s design. Reads the project and writes the cell; nothing else.</summary>
    public static HznConvertResult Convert(HznConvertRequest request, GeometryKernel? kernel = null, TechnologyCache? cache = null)
    {
        static HznConvertResult Refuse(Diagnostic d, string design = "") => new() { Refusal = d, Design = design };

        var read = HznFile.Read(request.HznPath);
        if (!read.Ok) return Refuse(read.Refusal!);
        var project = read.File!.Project;

        // The design: the one the project has, or the one named. Several and none named is a question the dialog asks and
        // the CLI cannot, so it lists them (as convert lists a STEP file's parts).
        var threeD = HznToC3d.ThreeDDesigns(project);
        string? design = request.Design;
        if (design is null)
        {
            if (threeD.Count == 0) return Refuse(HznConvertDiagnostics.NoThreeDDesign(request.HznPath));
            if (threeD.Count > 1) return Refuse(HznConvertDiagnostics.DesignRequired(request.HznPath, threeD));
            design = threeD[0];
        }

        var translation = HznToC3d.Translate(project, design, kernel);
        if (translation.Refusal is { } refused) return Refuse(refused, design);
        return ConvertDocument(request, design, translation.Document!, translation.Technology, translation.Notes, kernel, cache);
    }

    /// <summary>
    /// HZN-5 — the same conversion for a design already held in memory, edits and all: an open design's Save As writes the document
    /// it holds, not the file's. <paramref name="document"/> is written as it is (the caller hands over a copy whose
    /// <c>TechRef</c> is clear); <paramref name="technology"/> is the materials it names, which the workspace technology is compared
    /// with exactly as for a design read from the file.
    /// </summary>
    public static HznConvertResult ConvertDocument(HznConvertRequest request, string design, C3dDocument document, Technology? technology,
                                                   IReadOnlyList<string> notes, GeometryKernel? kernel = null, TechnologyCache? cache = null)
    {
        static HznConvertResult Refuse(Diagnostic d, string design = "") => new() { Refusal = d, Design = design };
        var doc = document;

        // ── the cell ──────────────────────────────────────────────────────────────────────────────
        string cellDir = Path.GetFullPath(request.CellDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string cellName = Path.GetFileName(cellDir);
        string parentDir = Path.GetDirectoryName(cellDir) ?? "";
        if (NameValidator.Validate(cellName) is { } badName)
            return Refuse(HznConvertDiagnostics.InvalidCellName(cellName, badName), design);

        bool cellExists = Directory.Exists(cellDir);
        if (File.Exists(cellDir) || (cellExists && !File.Exists(Path.Combine(cellDir, CellFolder.CcellFileName))))
            return Refuse(HznConvertDiagnostics.NotACell(cellDir), design);
        if (!cellExists)
        {
            if (!Directory.Exists(parentDir)) return Refuse(HznConvertDiagnostics.ParentMissing(parentDir), design);
            if (WorkspaceCreate.UnwritableParentRefusal(parentDir, "The design was not converted") is { } readOnly)
                return Refuse(HznConvertDiagnostics.Failed(readOnly), design);
        }

        string viewPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.ThreeD), cellName + CellFolder.ViewExtension(ViewType.ThreeD));
        bool replaces = File.Exists(viewPath);
        if (replaces && !request.ReplaceView) return Refuse(HznConvertDiagnostics.ViewExists(viewPath), design);

        // ── the materials ─────────────────────────────────────────────────────────────────────────
        var (tech, techPath) = ResolveTechnology(viewPath, cache ?? new TechnologyCache());
        var missing = new List<TechMaterial>();
        var conflicts = new List<string>();
        foreach (var m in technology?.Materials ?? [])
        {
            var have = tech?.FindMaterial(m.Name);
            if (have is null) { missing.Add(m); continue; }
            if (Differences(have, m) is { Count: > 0 } why)
                conflicts.Add($"{m.Name}: the technology has {Describe(have)}; the design has {Describe(m)} ({string.Join(", ", why)} differ).");
        }
        if (conflicts.Count > 0) return Refuse(HznConvertDiagnostics.MaterialConflict(conflicts), design);
        if (missing.Count > 0 && request.AddMaterials && (tech is null || techPath is null))
            return Refuse(HznConvertDiagnostics.NoTechnologyToAddTo(string.Join(", ", missing.Select(m => m.Name))), design);

        // ── the STEP files a design names ─────────────────────────────────────────────────────────
        var steps = doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dStep>().ToList();
        string threeDDir = CellFolder.SubFolderPath(cellDir, ViewType.ThreeD);
        var stepPlan = PlanStepCopies(steps, Path.GetDirectoryName(Path.GetFullPath(request.HznPath))!, threeDDir, out var stepRefusal);
        if (stepRefusal is not null) return Refuse(stepRefusal, design);

        // ── write ─────────────────────────────────────────────────────────────────────────────────
        var findings = new List<Diagnostic>();
        foreach (string note in notes) findings.Add(HznTranslationDiagnostics.Note($"design '{design}': {note}"));
        try
        {
            if (!cellExists) CellCreate.Create(parentDir, cellName, CellViews.None);
            var copied = CopySteps(stepPlan, steps);
            CellCreate.WriteThreeDView(cellDir, cellName, doc);

            var added = new List<string>();
            if (missing.Count > 0)
            {
                if (request.AddMaterials)
                {
                    // A CLONE is edited and saved whole, as `import part --add-layers` does, so a failed write leaves the
                    // technology as it was; a name already present is skipped rather than duplicated.
                    var clone = TechPersistence.Clone(tech!);
                    foreach (var m in missing)
                    {
                        if (clone.Materials.Any(x => string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase))) continue;
                        clone.Materials.Add(m);
                        added.Add(m.Name);
                    }
                    if (added.Count > 0) TechPersistence.SaveToFile(techPath!, clone);
                }
                else
                {
                    findings.Add(HznConvertDiagnostics.MaterialsMissing(string.Join(", ", missing.Select(m => m.Name))));
                }
            }

            var others = Directory.GetFiles(threeDDir, "*" + CellFolder.ViewExtension(ViewType.ThreeD))
                                  .Count(f => !string.Equals(f, viewPath, StringComparison.Ordinal));
            if (others > 0) findings.Add(HznTranslationDiagnostics.Note(
                $"The cell now has {others + 1} 3D views; the project tree asks which is primary."));

            return new HznConvertResult
            {
                Design = design, CellDir = cellDir, ViewPath = viewPath, CellCreated = !cellExists, ViewReplaced = replaces,
                MissingMaterials = [.. missing.Select(m => m.Name)], AddedMaterials = added,
                TechnologyPath = added.Count > 0 ? techPath : null, StepFiles = copied, Findings = findings,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refuse(HznConvertDiagnostics.Failed(ex.Message + (Directory.Exists(cellDir) && !cellExists ? $" The cell folder '{cellDir}' was created and may be incomplete." : "")), design);
        }
    }

    // ── technology ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The technology the new view would resolve — the workspace's default, found by the ancestor walk every
    /// document takes — and the file it came from. Null for a cell outside any workspace, or one with no default.</summary>
    private static (Technology? Tech, string? Path) ResolveTechnology(string viewPath, TechnologyCache cache)
    {
        var (res, _) = TechnologyResolver.ResolveForDocument(null, viewPath, null, cache);
        return (res.Tech, res.ResolvedPath);
    }

    // ── materials ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The properties on which two materials of one name differ. Only the electromagnetic ones count: a technology
    /// material also carries thermal and display properties a Hertzon material has no way to state, and those are the
    /// workspace's to keep. A permeability not stated is 1 and a loss tangent or conductivity not stated is 0.</summary>
    internal static List<string> Differences(TechMaterial technology, TechMaterial design)
    {
        var why = new List<string>();
        static bool Same(double? a, double? b) =>
            a is null || b is null ? a is null && b is null
                                   : Math.Abs(a.Value - b.Value) <= 1e-9 * Math.Max(Math.Abs(a.Value), Math.Abs(b.Value));
        if (!Same(technology.Epsr, design.Epsr)) why.Add("relative permittivity");
        if (!Same(technology.TanD ?? 0, design.TanD ?? 0)) why.Add("loss tangent");
        if (!Same(technology.Mur ?? 1, design.Mur ?? 1)) why.Add("relative permeability");
        if (!Same(technology.Sigma20 ?? 0, design.Sigma20 ?? 0)) why.Add("conductivity");
        return why;
    }

    /// <summary>A material's electromagnetic properties as one phrase, the stated ones only.</summary>
    internal static string Describe(TechMaterial m)
    {
        var parts = new List<string>();
        string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        if (m.Epsr is { } e) parts.Add($"epsr {N(e)}");
        if (m.TanD is { } t) parts.Add($"tan d {N(t)}");
        if (m.Mur is { } u) parts.Add($"mur {N(u)}");
        if (m.Sigma20 is { } s) parts.Add($"sigma {N(s)} S/m");
        return parts.Count == 0 ? "no properties stated" : string.Join(", ", parts);
    }

    // ── STEP files ──────────────────────────────────────────────────────────────────────────────────

    private sealed record StepCopy(C3dStep Step, string Source, string Destination, bool AlreadyThere);

    /// <summary>Finds each STEP file an object names — beside the <c>.hzn</c> when the name is relative — and where it goes in the
    /// cell: <c>3d/&lt;file name&gt;</c>, the place <see cref="C3dStep.File"/> is read from. A file already there with other
    /// bytes is a refusal, never overwritten.</summary>
    private static List<StepCopy> PlanStepCopies(IReadOnlyList<C3dStep> steps, string hznDir, string threeDDir, out Diagnostic? refusal)
    {
        refusal = null;
        var plan = new List<StepCopy>();
        foreach (var step in steps)
        {
            string source = Path.IsPathRooted(step.File) ? step.File : Path.GetFullPath(Path.Combine(hznDir, step.File));
            if (!File.Exists(source)) { refusal = HznConvertDiagnostics.StepMissing(step.Name, source); return plan; }
            string dest = Path.Combine(threeDDir, Path.GetFileName(source));
            bool there = File.Exists(dest);
            if (there && !File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(dest)))
            { refusal = HznConvertDiagnostics.StepDiffers(Path.GetFileName(source), dest); return plan; }
            plan.Add(new StepCopy(step, source, dest, there));
        }
        return plan;
    }

    private static List<string> CopySteps(List<StepCopy> plan, IReadOnlyList<C3dStep> steps)
    {
        var copied = new List<string>();
        foreach (var c in plan)
        {
            if (!c.AlreadyThere)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(c.Destination)!);
                File.Copy(c.Source, c.Destination);
                copied.Add(c.Destination);
            }
            c.Step.SourcePath ??= c.Source;
            c.Step.File = Path.GetFileName(c.Destination);
        }
        return copied;
    }

    /// <summary>Copies the STEP files <paramref name="doc"/>'s objects name into <paramref name="threeDDir"/> and repoints each
    /// object at its copy. For a caller that holds a translated document and its source folder; <see cref="Convert"/> does
    /// the same on the way to the cell.</summary>
    public static IReadOnlyList<string> CopyStepFiles(C3dDocument doc, string hznDir, string threeDDir, out Diagnostic? refusal)
    {
        var steps = doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dStep>().ToList();
        var plan = PlanStepCopies(steps, hznDir, threeDDir, out refusal);
        return refusal is null ? CopySteps(plan, steps) : [];
    }
}

/// <summary>Every refusal and finding <see cref="HznConvert"/> returns (HZN-6). Returned, never posted: the dialog shows them, the
/// verb writes them to stderr. The ids are the durable part.</summary>
public static class HznConvertDiagnostics
{
    public static Diagnostic NoThreeDDesign(string file) => Diagnostic.Create(
        "hzn.convert.no-3d-design", DiagnosticSeverity.Error,
        "'{file}' has no 3D design to convert; circuitRF converts a Hertzon project's 3D designs.", ("file", file));

    public static Diagnostic DesignRequired(string file, IReadOnlyList<string> designs) => Diagnostic.Create(
        "hzn.convert.design-required", DiagnosticSeverity.Error,
        "'{file}' holds {count} 3D designs; name one with --design. Its 3D designs: {designs}.",
        ("file", file), ("count", designs.Count), ("designs", string.Join(", ", designs)));

    public static Diagnostic InvalidCellName(string name, string reason) => Diagnostic.Create(
        "hzn.convert.invalid-name", DiagnosticSeverity.Error,
        "'{name}' cannot be a cell name: {reason}", ("name", name), ("reason", reason));

    public static Diagnostic NotACell(string path) => Diagnostic.Create(
        "hzn.convert.not-a-cell", DiagnosticSeverity.Error,
        "'{path}' exists and is not a cell folder (it holds no .ccell), so nothing was written.", ("path", path));

    public static Diagnostic ParentMissing(string parent) => Diagnostic.Create(
        "hzn.convert.parent-missing", DiagnosticSeverity.Error,
        "The folder '{parent}' does not exist, so the cell could not be created in it. Nothing was written.", ("parent", parent));

    public static Diagnostic ViewExists(string view) => Diagnostic.Create(
        "hzn.convert.view-exists", DiagnosticSeverity.Error,
        "The cell already has a 3D view '{view}'. Nothing was written; choose to replace it, or convert into another cell.", ("view", view));

    public static Diagnostic MaterialConflict(IReadOnlyList<string> conflicts) => Diagnostic.Create(
        "hzn.convert.material-conflict", DiagnosticSeverity.Error,
        "{count} material(s) of the design share a name with a different material of the technology, so nothing was written:\n{conflicts}",
        ("count", conflicts.Count), ("conflicts", string.Join("\n", conflicts.Select(c => "  " + c))));

    public static Diagnostic NoTechnologyToAddTo(string names) => Diagnostic.Create(
        "hzn.convert.no-technology", DiagnosticSeverity.Error,
        "Materials {names} would be added to the technology, but the cell's workspace has no technology file to add them to. Nothing was written.",
        ("names", names));

    public static Diagnostic MaterialsMissing(string names) => Diagnostic.Create(
        "hzn.convert.materials-missing", DiagnosticSeverity.Warning,
        "The technology does not define {names}. The design was converted without them; add them to the technology (--add-materials) before it runs.",
        ("names", names));

    public static Diagnostic StepMissing(string obj, string file) => Diagnostic.Create(
        "hzn.convert.step-missing", DiagnosticSeverity.Error,
        "Object '{obj}' names the STEP file '{file}', which is not there. Nothing was written.", ("obj", obj), ("file", file));

    public static Diagnostic StepDiffers(string name, string dest) => Diagnostic.Create(
        "hzn.convert.step-differs", DiagnosticSeverity.Error,
        "The cell already holds a different file named '{name}' ({dest}); it was not overwritten. Nothing was written.",
        ("name", name), ("dest", dest));

    public static Diagnostic Failed(string reason) => Diagnostic.Create(
        "hzn.convert.failed", DiagnosticSeverity.Error, "The design was not converted: {reason}", ("reason", reason));
}
