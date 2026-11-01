// brief-em3d-95 — copying objects out of one .c3d and pasting them into another (or the same one), across documents,
// workspaces and running instances. This file holds EVERY "what does a paste mean" decision; src/Ui/Clipboard/C3dClipboard
// is clipboard traffic only, the split LayoutFragment/LayoutClipboard already makes — which is what lets the whole paste be
// tested with no clipboard and no window, and reached headlessly later.
//
// WHAT A COPY IS (R-em3d95-1). A self-describing text with a marker: the copied records as a .c3d (written by C3dPersistence,
// never a second writer — so every key, Transparency, Model, expressions, operands, features and Unread keys travel
// exactly as a file carries them), the source's DbuPerMicron, every material the copied objects name AS THE SOURCE
// TECHNOLOGY RESOLVED IT (values, not a pointer to a library), the VARs the copied expressions name to closure, each placed
// cell's reference in two base-independent forms, and the source thermal setup's name for the boundaries copied from it.
//
// WHAT A PASTE MEANS (R-em3d95-3 / -3a / -4), in C3dFragment.Apply: same world coordinates rescaled to the target's DBU;
// every name made unique with the _2 rule and every reference inside the fragment following it; variables created,
// reused or renamed BY TOKEN; ports renumbered where their number is taken; boundaries and planes bound or skipped by ONE
// rule; materials matched by the elaborator's own lookup, else created or left unassigned. Nothing here writes a
// technology: materials to create are RETURNED, and the caller commits them through the Materials dialog's own function.
//
// PURE: Apply works on a copy of the target and copies it back only when nothing refused it, so a refused paste (a cycle a
// Reuse made) leaves the target byte for byte as it was.

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What a copy takes from its document: the rows the user chose, each kind by where it sits.</summary>
public sealed record C3dCopySelection
{
    /// <summary>Indices into <see cref="C3dDocument.Objects"/>. A group row is its members; each index is copied once.</summary>
    public IReadOnlyList<int> Objects { get; init; } = [];
    public IReadOnlyList<int> Instances { get; init; } = [];
    /// <summary>Indices into <see cref="C3dDocument.Ports"/>.</summary>
    public IReadOnlyList<int> Ports { get; init; } = [];
    /// <summary>Indices into <see cref="C3dDocument.FaceBoundaries"/>.</summary>
    public IReadOnlyList<int> FaceBoundaries { get; init; } = [];
    /// <summary>Heat sources, probes, mesh regions and effective blocks, by name.</summary>
    public IReadOnlyList<string> Places { get; init; } = [];
    public IReadOnlyList<C3dAxis> SymmetryPlanes { get; init; } = [];
    /// <summary>Faces (<c>object/face</c>, or <c>*exposed*</c>) of the source's active thermal setup's boundaries.</summary>
    public IReadOnlyList<string> ThermalBoundaries { get; init; } = [];

    public bool IsEmpty => Objects.Count + Instances.Count + Ports.Count + FaceBoundaries.Count + Places.Count +
                           SymmetryPlanes.Count + ThermalBoundaries.Count == 0;
}

/// <summary>Where a copy is made: the source document's path (its cell and its instances' base), its workspace root, and
/// its active thermal setup (the one whose boundaries a thermal-boundary row names).</summary>
public sealed record C3dCopyContext(string SourcePath, string? WorkspaceRootDir, C3dCell Cell, string? ActiveSetupName);

/// <summary>Where a paste lands: the target's path, its workspace root, its cell and its active setup.</summary>
public sealed record C3dPasteContext(string TargetPath, string? WorkspaceRootDir, C3dCell Cell, string? ActiveSetupName);

/// <summary>A pasted variable whose name the target already has, with the two definitions side by side.</summary>
public sealed record C3dVariableConflict(string Name, string Here, string Copy, string Suggested, bool FromParameter);

/// <summary>A pasted boundary or symmetry plane landing where the target already has one of the same family. <see cref="Key"/>
/// is what <see cref="C3dPasteChoices.ReplaceBoundaries"/> names.</summary>
public sealed record C3dBoundaryConflict(string Key, string What, string Here, string Copy);

/// <summary>A material the copied objects name and the target technology does not have.</summary>
public sealed record C3dMissingMaterial(string Name, string Summary, TechMaterial Material);

/// <summary>What the Paste dialog asks: the conflicts and the missing materials. Empty — no dialog.</summary>
public sealed class C3dPastePlan
{
    public List<C3dVariableConflict> Variables { get; } = [];
    public List<C3dBoundaryConflict> Boundaries { get; } = [];
    public List<C3dMissingMaterial> Materials { get; } = [];
    public bool NeedsDialog => Variables.Count + Boundaries.Count + Materials.Count > 0;

    /// <summary>The dialog's own defaults: Reuse every variable, Keep every boundary, create every missing material.</summary>
    public C3dPasteChoices Defaults()
    {
        var c = new C3dPasteChoices();
        foreach (var m in Materials) c.CreateMaterials.Add(m.Name);
        return c;
    }
}

/// <summary>The answers to a plan. Anything not answered takes the safe side: a variable is reused, a boundary kept, a
/// missing material not created (the object is left unassigned).</summary>
public sealed class C3dPasteChoices
{
    /// <summary>A conflicting variable's answer: null (or absent) reuses the target's, a name renames the pasted one.</summary>
    public Dictionary<string, string?> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>The <see cref="C3dBoundaryConflict.Key"/>s answered Replace; the rest keep the target's.</summary>
    public HashSet<string> ReplaceBoundaries { get; } = new(StringComparer.Ordinal);

    /// <summary>The missing materials to create; the rest leave their objects unassigned.</summary>
    public HashSet<string> CreateMaterials { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What a paste did: the report, the materials the caller must create, and what was pasted (to select it).</summary>
public sealed class C3dPasteResult
{
    /// <summary>Why nothing was pasted (the target is unchanged); null when it was.</summary>
    public string? Refusal { get; init; }
    public List<string> Report { get; } = [];
    /// <summary>Materials to add to the target technology, whole — the caller commits them.</summary>
    public List<TechMaterial> MaterialsToCreate { get; } = [];
    public List<string> Objects { get; } = [];
    public List<string> Instances { get; } = [];
    public List<int> Ports { get; } = [];
    public List<string> Places { get; } = [];
    public List<C3dAxis> SymmetryPlanes { get; } = [];
    public List<string> FaceBoundaries { get; } = [];
    public List<string> ThermalBoundaries { get; } = [];

    /// <summary>The report's first line, e.g. <c>Pasted 5 objects; renamed box1 → box1_2</c>.</summary>
    public string Summary => Report.Count > 0 ? Report[0] : "";
}

public static class C3dFragment
{
    public const string Marker = "circuitrf/3d-clipboard-v1";

    /// <summary>The clipboard payload. <see cref="Content"/> is a <c>.c3d</c> holding exactly the copied records.</summary>
    public sealed class Payload
    {
        public string? Marker { get; set; }
        public int DbuPerMicron { get; set; }
        public string? TechName { get; set; }

        /// <summary>The copied records, as C3dPersistence writes a <c>.c3d</c>. A copied thermal boundary rides in one embedded
        /// thermal setup named after the one it came from.</summary>
        public string Content { get; set; } = "";

        /// <summary>Every material the copied objects and face boundaries name, as the source technology resolved it — a
        /// <c>.cmat</c>'s text. Null with none.</summary>
        public string? Materials { get; set; }

        /// <summary>Parallel to the content's instances: each one's cell directory at copy time, and it relative to the source
        /// workspace (CellRefRebase.Capture).</summary>
        public List<string?> InstanceCellDirs { get; set; } = [];
        public List<string?> InstanceWorkspaceRelativeDirs { get; set; } = [];

        /// <summary>For each carried VAR: whether it was a cell parameter in the source, and its value there (for the report).</summary>
        public List<CarriedVariable> VariableNotes { get; set; } = [];
    }

    public sealed class CarriedVariable
    {
        public string Name { get; set; } = "";
        public bool FromParameter { get; set; }
        public string? Value { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = C3dBindings.Encoder,
    };

    /// <summary>The reserved object name (the air box's faces are named after it).</summary>
    private const string AirBox = "airbox";

    // ── Build (R-em3d95-1) ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fragment for <paramref name="selection"/> of <paramref name="source"/>. <paramref name="sourceTech"/> is the
    /// technology the source resolved (its materials are copied with their values); null copies none.
    /// </summary>
    public static Payload Build(C3dDocument source, Technology? sourceTech, C3dCopySelection selection, C3dCopyContext context)
    {
        var content = new C3dDocument { DbuPerMicron = source.DbuPerMicron, DisplayUnit = source.DisplayUnit };
        foreach (int i in selection.Objects.Distinct().Where(i => i >= 0 && i < source.Objects.Count).Order())
            content.Objects.Add(C3dBooleans.Copy(source.Objects[i]));
        // brief-em3d-101 R-em3d101-5a — an image's path made absolute on copy, so the paste can write it relative to ITS .c3d.
        if (context.SourcePath.Length > 0)
            foreach (var img in C3dImages.AllImages(content.Objects))
                if (C3dImages.Resolve(context.SourcePath, img.Path) is { } abs) img.Path = abs;

        string? sourceDir = context.SourcePath.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(context.SourcePath)) : null;
        var cellDirs = new List<string?>();
        var wsDirs = new List<string?>();
        foreach (int i in selection.Instances.Distinct().Where(i => i >= 0 && i < source.Instances.Count).Order())
        {
            var inst = C3dPersistence.DeserializeInstance(C3dPersistence.SerializeInstance(source.Instances[i]));
            content.Instances.Add(inst);
            var (dir, rel) = CellRefRebase.Capture(inst.CellRef, sourceDir, context.WorkspaceRootDir);
            cellDirs.Add(dir);
            wsDirs.Add(rel);
        }
        var ports = C3dPersistence.DeserializePorts(C3dPersistence.SerializePorts(source.Ports));
        foreach (int i in selection.Ports.Distinct().Where(i => i >= 0 && i < ports.Count).Order()) content.Ports.Add(ports[i]);
        var faces = C3dPersistence.DeserializeFaceBoundaries(C3dPersistence.SerializeFaceBoundaries(source.FaceBoundaries));
        foreach (int i in selection.FaceBoundaries.Distinct().Where(i => i >= 0 && i < faces.Count).Order()) content.FaceBoundaries.Add(faces[i]);

        // the places and planes, through the in-memory spelling (each bound field keeps its number)
        var places = new C3dDocument();
        C3dPersistence.ApplyThermalPlaces(places, C3dPersistence.SerializeThermalPlaces(source));
        var wanted = selection.Places.ToHashSet(StringComparer.Ordinal);
        content.HeatSources = [.. places.HeatSources.Where(h => wanted.Contains(h.Name))];
        content.Probes = [.. places.Probes.Where(p => wanted.Contains(p.Name))];
        content.MeshRegions = [.. places.MeshRegions.Where(m => wanted.Contains(m.Name))];
        content.EffectiveBlocks = [.. places.EffectiveBlocks.Where(b => wanted.Contains(b.Name))];
        content.SymmetryPlanes = [.. places.SymmetryPlanes.Where(p => selection.SymmetryPlanes.Contains(p.Axis))];

        // thermal boundaries: the source's active thermal setup's, the faces named, in one setup of the same name
        if (selection.ThermalBoundaries.Count > 0 && context.ActiveSetupName is { } setupName &&
            C3dThermal.ThermalSetups(source).FirstOrDefault(s => s.Setup.Name == setupName) is { Thermal: { } t } &&
            (t.Boundaries ?? []).Where(b => selection.ThermalBoundaries.Contains(b.Face)).ToList() is { Count: > 0 } bs)
            content.Setups = [EmSetupPersistence.ToEmbedded(new EmSetup
            {
                Name = setupName, Solver3D = Em3dSolverChoice.None, Problem3D = Em3dProblemType.Thermal,
                Thermal = new CemThermal { Boundaries = bs },
            })];

        // the VARs every carried expression names, to closure; a cell parameter travels as a VAR of its value here
        var notes = new List<CarriedVariable>();
        var scope = C3dResolver.Resolve(new C3dDocument { Variables = C3dPersistence.DeserializeVariables(C3dPersistence.SerializeVariables(source.Variables)) },
                                        context.Cell);
        var queue = new Queue<string>(ExpressionTexts(content).SelectMany(C3dExpressionText.Names));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out string? name))
        {
            if (!seen.Add(name)) continue;
            var v = source.Variables.FirstOrDefault(x => x.Name == name);
            var p = context.Cell.Parameter(name);
            C3dVariable carried;
            bool fromParameter;
            if (v is not null && (p is null || v.Linked == false))
            {
                carried = C3dPersistence.DeserializeVariables(C3dPersistence.SerializeVariables([v]))[0];
                fromParameter = false;
            }
            else if (p is not null)
            {
                carried = new C3dVariable { Name = name, Expression = p.DefaultExpression, Unit = p.Unit is { Length: > 0 } u ? u : null };
                fromParameter = true;
            }
            else continue;                                    // defined nowhere in the source: nothing to carry
            content.Variables.Add(carried);
            notes.Add(new CarriedVariable
            {
                Name = name, FromParameter = fromParameter,
                Value = scope.Names.TryGetValue(name, out var n) ? Spell(n) : null,
            });
            foreach (string next in C3dExpressionText.Names(carried.Expression)) queue.Enqueue(next);
        }

        // the materials, as the source technology resolved them
        var materials = new List<TechMaterial>();
        foreach (string m in MaterialNames(content).Distinct(StringComparer.OrdinalIgnoreCase))
            if (sourceTech?.FindMaterial(m) is { } found)
                materials.Add(MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize([found]))[0]);

        return new Payload
        {
            Marker = Marker,
            DbuPerMicron = source.DbuPerMicron,
            TechName = sourceTech?.Name,
            Content = C3dPersistence.Serialize(content),
            Materials = materials.Count > 0 ? MaterialLibraryPersistence.Serialize(materials) : null,
            InstanceCellDirs = cellDirs,
            InstanceWorkspaceRelativeDirs = wsDirs,
            VariableNotes = notes,
        };
    }

    public static string Serialize(Payload payload) => JsonSerializer.Serialize(payload, JsonOpts);

    /// <summary>Marker-guarded: any other text — a layout copy, a schematic's, plain text — is a clean false, never a throw.</summary>
    public static bool TryDeserialize(string? text, out Payload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var candidate = JsonSerializer.Deserialize<Payload>(text, JsonOpts);
            if (candidate is null || candidate.Marker != Marker) return false;
            C3dPersistence.Deserialize(candidate.Content);   // a payload whose content does not read is not one
            payload = candidate;
            return true;
        }
        catch { return false; }
    }

    /// <summary>What a fragment holds, for a menu's tooltip: <c>3 objects, 1 port</c>.</summary>
    public static string Describe(Payload payload)
    {
        var d = C3dPersistence.Deserialize(payload.Content);
        var parts = new List<string>();
        void Add(int n, string what) { if (n > 0) parts.Add($"{n} {what}{(n == 1 ? "" : "s")}"); }
        Add(d.Objects.Count, "object");
        Add(d.Instances.Count, "placed cell");
        Add(d.Ports.Count, "port");
        Add(d.FaceBoundaries.Count + C3dThermal.ThermalSetups(d).Sum(s => s.Thermal.Boundaries?.Count ?? 0), "boundary");
        Add(d.HeatSources.Count + d.Probes.Count + d.MeshRegions.Count + d.EffectiveBlocks.Count, "thermal place");
        Add(d.SymmetryPlanes.Count, "symmetry plane");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    // ── Plan and Apply (R-em3d95-3 / -3a / -4) ───────────────────────────────────────────────

    /// <summary>What the Paste dialog must ask before <paramref name="fragment"/> can go into <paramref name="target"/>. The
    /// target is not changed.</summary>
    public static C3dPastePlan Plan(C3dDocument target, Technology? targetTech, C3dPasteContext context, Payload fragment)
    {
        var plan = new C3dPastePlan();
        Run(Clone(target), targetTech, context, fragment, null, plan);
        return plan;
    }

    /// <summary>
    /// Pastes <paramref name="fragment"/> into <paramref name="target"/> with <paramref name="choices"/>. The target changes
    /// only when the result has no <see cref="C3dPasteResult.Refusal"/>; the materials to create are returned, not created.
    /// </summary>
    public static C3dPasteResult Apply(C3dDocument target, Technology? targetTech, C3dPasteContext context, Payload fragment,
                                       C3dPasteChoices choices)
    {
        var work = Clone(target);
        var result = Run(work, targetTech, context, fragment, choices, null);
        if (result.Refusal is null) CopyInto(work, target);
        return result;
    }

    /// <summary>brief-em3d-95 D2 — the pasted planes that do not lie on the model's extent as pasted: one sentence each. A
    /// plane pastes anyway; the document's own rule (the Inspector, <c>check</c>, a run) decides from then on.</summary>
    public static IReadOnlyList<string> PlanesOffExtent(C3dElaboration e, C3dDocument doc, IEnumerable<C3dAxis> axes)
    {
        var list = new List<string>();
        double m = C3dLowering.Metres(1, doc.DbuPerMicron);
        foreach (var axis in axes)
            if (doc.SymmetryPlanes.FirstOrDefault(p => p.Axis == axis) is { } p &&
                C3dThermal.SymmetryPlaneRefusal(e, axis, p.At * m, doc.DbuPerMicron, out _) is { } why)
                list.Add($"{why}: a run refuses it until it is.");
        return list;
    }

    private static C3dPasteResult Run(C3dDocument target, Technology? targetTech, C3dPasteContext context, Payload fragment,
                                      C3dPasteChoices? choices, C3dPastePlan? plan)
    {
        var result = new C3dPasteResult();
        var src = C3dPersistence.Deserialize(fragment.Content);
        var report = new List<string>();

        // 1. the target's DBU
        if (fragment.DbuPerMicron > 0 && fragment.DbuPerMicron != target.DbuPerMicron)
        {
            long from = fragment.DbuPerMicron, to = target.DbuPerMicron;
            long F(long v) => (long)Math.Round((decimal)v * to / from, MidpointRounding.AwayFromZero);
            foreach (object item in Rescalable(src)) Rescale(item, F);
            report.Add($"Rescaled from {from} to {to} DBU/µm: the same world coordinates.");
        }

        // 2. names: unique in the target, and every reference inside the fragment following them
        var taken = TakenNames(target);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var renames = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);          // every copied name → its pasted name
        string Unique(string name)
        {
            if (name.Length == 0) return name;
            string n = name;
            for (int k = 2; taken.Contains(n) || assigned.Contains(n) || string.Equals(n, AirBox, StringComparison.OrdinalIgnoreCase); k++)
                n = $"{name}_{k.ToString(CultureInfo.InvariantCulture)}";
            assigned.Add(n);
            if (n != name) renames.Add($"{name} → {n}");
            return n;
        }
        foreach (var o in src.Objects.SelectMany(C3dOperands.SelfAndDescendants).Where(o => o.Name.Length > 0))
            o.Name = names[o.Name] = Unique(o.Name);
        foreach (var i in src.Instances) i.Name = names[i.Name] = Unique(i.Name);
        var copiedNames = names.Keys.ToHashSet(StringComparer.Ordinal);
        var tools = names.Where(kv => kv.Key != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var o in src.Objects.SelectMany(C3dOperands.SelfAndDescendants))
            switch (o)
            {
                case C3dFillet f: f.Edges = [.. f.Edges.Select(e => RenameToolPrefixes(e, tools))]; break;
                case C3dChamfer c: c.Edges = [.. c.Edges.Select(e => RenameToolPrefixes(e, tools))]; break;
            }
        foreach (var p in src.Ports) if (p.Name.Length > 0) p.Name = Unique(p.Name);
        foreach (var h in src.HeatSources) h.Name = Unique(h.Name);
        foreach (var p in src.Probes) p.Name = Unique(p.Name);
        foreach (var m in src.MeshRegions) m.Name = Unique(m.Name);
        foreach (var b in src.EffectiveBlocks) b.Name = Unique(b.Name);

        // group paths: a pasted group whose name the target already uses is a NEW group (pa → pa_2), never merged
        var groupNames = C3dGroups.Names(target);
        var pastedGroups = src.Objects.Select(o => o.Group).Concat(src.Instances.Select(i => i.Group)).SelectMany(C3dGroups.Segments)
                              .ToHashSet(StringComparer.Ordinal);
        var groupMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string seg in pastedGroups.Where(groupNames.Contains).Order(StringComparer.Ordinal))
        {
            string n = FreeName(seg, x => groupNames.Contains(x) || pastedGroups.Contains(x) || groupMap.ContainsValue(x));
            groupMap[seg] = n;
            renames.Add($"{seg} → {n}");
        }
        string? Path2(string? path) => path is null ? null : C3dGroups.Join(C3dGroups.Segments(path).Select(g => groupMap.GetValueOrDefault(g, g)));
        foreach (var o in src.Objects) o.Group = Path2(o.Group);
        foreach (var i in src.Instances) i.Group = Path2(i.Group);

        // 3. variables: created, reused, or renamed by token
        var targetScope = C3dResolver.Resolve(new C3dDocument { Variables = C3dPersistence.DeserializeVariables(C3dPersistence.SerializeVariables(target.Variables)) },
                                              context.Cell);
        var notes = fragment.VariableNotes.ToDictionary(n => n.Name, StringComparer.Ordinal);
        var varRenames = new Dictionary<string, string>(StringComparer.Ordinal);
        var created = new List<C3dVariable>();
        var reusedIdentical = new List<string>();
        var reused = new List<string>();
        var carriedNames = src.Variables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var v in src.Variables)
        {
            notes.TryGetValue(v.Name, out var note);
            var here = TargetDefinition(target, context.Cell, v.Name);
            if (here is null) { created.Add(v); continue; }
            if (SameDefinition(here.Value, v)) { reusedIdentical.Add(v.Name); continue; }
            string suggested = FreeName(v.Name, n => IsNameTaken(target, context.Cell, n) || carriedNames.Contains(n));
            plan?.Variables.Add(new C3dVariableConflict(v.Name,
                $"{v.Name} = {here.Value.Expression}{UnitText(here.Value.Unit)}{ValueText(targetScope, v.Name)} here",
                $"{v.Expression}{UnitText(v.Unit)}{(note?.Value is { } val ? $" = {val}" : "")} in the copy", suggested, note?.FromParameter == true));
            if (choices is null || !choices.Variables.TryGetValue(v.Name, out var to) || to is null) { reused.Add(v.Name); continue; }
            varRenames[v.Name] = to;
        }
        if (choices is not null)
        {
            var finals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (from, to) in varRenames)
            {
                if (C3dResolver.ValidateName(to) is { } bad) return Refused($"'{to}' cannot rename the pasted '{from}': {bad}");
                if (IsNameTaken(target, context.Cell, to)) return Refused($"'{to}' cannot rename the pasted '{from}': this 3D view already has that name.");
                if ((carriedNames.Contains(to) && !varRenames.ContainsKey(to)) || !finals.Add(to))
                    return Refused($"'{to}' cannot rename the pasted '{from}': another pasted variable takes that name.");
            }
        }
        foreach (var (from, to) in varRenames)
        {
            RenameInExpressions(src, from, to);
            foreach (var v in src.Variables) v.Expression = C3dExpressionText.Rename(v.Expression, from, to);
            var rv = src.Variables.First(v => v.Name == from);
            rv.Name = to;
            created.Add(rv);
        }
        foreach (var v in created) target.Variables.Add(v);

        // 4. materials: the target technology's lookup; found → used (different values reported); missing → created or unassigned
        var fragmentMaterials = fragment.Materials is { } mt ? MaterialLibraryPersistence.Deserialize(mt) : [];
        foreach (var m in fragmentMaterials)
        {
            if (targetTech?.FindMaterial(m.Name) is { } found)
            {
                if (Difference(found, m) is { } diff)
                    report.Add($"'{m.Name}' in this workspace differs: {diff} — this workspace's is used.");
                continue;
            }
            plan?.Materials.Add(new C3dMissingMaterial(m.Name, Summary(m), m));
            if (choices?.CreateMaterials.Contains(m.Name) == true) { result.MaterialsToCreate.Add(m); continue; }
            foreach (var o in src.Objects.SelectMany(C3dOperands.SelfAndDescendants))
                if (string.Equals(o.Material, m.Name, StringComparison.OrdinalIgnoreCase)) o.Material = null;
            foreach (var b in src.FaceBoundaries)
                if (string.Equals(b.Material, m.Name, StringComparison.OrdinalIgnoreCase)) b.Material = null;
            report.Add($"'{m.Name}' was not created: what named it has no material here (listed under No material) until one is given.");
        }
        if (result.MaterialsToCreate.Count > 0)
            report.Add($"Created {string.Join(", ", result.MaterialsToCreate.Select(m => $"'{m.Name}'"))} in the technology, which is now unsaved; " +
                       "undoing the paste does not remove a created material (it is an edit of that document, with its own undo).");

        // 5. references (R-em3d95-3a): copied → follows the rename; else the target's own, reported; else unbound
        (string? Ref, bool Here) Bind(string reference)
        {
            int slash = reference.IndexOf('/');
            string head = slash < 0 ? reference : reference[..slash];
            string rest = slash < 0 ? "" : reference[slash..];
            int bracket = head.IndexOf('[');
            string baseName = bracket < 0 ? head : head[..bracket];
            string suffix = bracket < 0 ? "" : head[bracket..];
            if (string.Equals(baseName, AirBox, StringComparison.OrdinalIgnoreCase)) return (reference, false);
            if (copiedNames.Contains(baseName)) return (names[baseName] + suffix + RenameToolPrefixes(rest, tools), false);
            if (target.Instances.Any(i => i.Name == baseName)) return (reference, true);
            if (target.Objects.SelectMany(C3dOperands.SelfAndDescendants).FirstOrDefault(o => o.Name == baseName) is not { } obj) return (null, false);
            if (slash < 0) return (reference, true);
            string face = rest[1..];
            int hash = face.IndexOf('#');
            if (hash >= 0) face = face[..hash];
            var faces = obj.FaceNames();
            return faces.Contains(face) || (faces.Count == 0 && obj is C3dStep) ? (reference, true) : (null, false);
        }
        void Here(string what, string reference)
        {
            string head = reference.Split('/')[0];
            report.Add($"{what} names '{reference}': this document's '{head}', not a copied one.");
        }

        // ports: numbers kept when free, else the next free, in source order; conductors bound, or both cleared (inferred)
        // brief-em3d-114 — a terminal is a port: each one numbered as one
        var usedNumbers = C3dPorts.UsedNumbers(target).ToHashSet();
        int next = usedNumbers.Count == 0 ? 1 : usedNumbers.Max() + 1;
        var numbering = new List<string>();
        foreach (var p in src.Ports)
        {
            p.FromLayout = null;   // a pasted port is a copy, not the layout label's own record
            int Renumber(int was)
            {
                if (!usedNumbers.Contains(was)) { usedNumbers.Add(was); return was; }
                while (usedNumbers.Contains(next)) next++;
                numbering.Add($"P{was}→P{next}");
                usedNumbers.Add(next);
                return next;
            }
            if (p.Terminals is { Count: > 0 } terminals)
            {
                foreach (var t in terminals) t.Number = Renumber(t.Number);
                bool refBound = p.Reference is null || Bind(p.Reference).Item1 is not null;
                if (!refBound || terminals.Any(t => Bind(t.Conductor).Item1 is null))
                    report.Add($"{C3dPorts.Label(p)}'s conductors are not all here: its terminals name conductors this document lacks, so it " +
                               "is refused until they are renamed.");
                else
                {
                    if (p.Reference is { } rf) p.Reference = Bind(rf).Item1;
                    foreach (var t in terminals) t.Conductor = Bind(t.Conductor).Item1!;
                }
                target.Ports.Add(p);
                result.Ports.AddRange(terminals.Select(t => t.Number));
                continue;
            }
            p.Number = Renumber(p.Number);
            if (p.Positive is { } pos && p.Negative is { } neg)
            {
                var (bp, hp) = Bind(pos);
                var (bn, hn) = Bind(neg);
                if (bp is null || bn is null)
                {
                    p.Positive = p.Negative = null;
                    report.Add($"Port {p.Number}'s conductors ('{pos}', '{neg}') are not all here: they will be inferred from what its rectangle touches.");
                }
                else
                {
                    if (hp) Here($"Port {p.Number}'s + conductor", pos);
                    if (hn) Here($"Port {p.Number}'s − conductor", neg);
                    p.Positive = bp;
                    p.Negative = bn;
                }
            }
            target.Ports.Add(p);
            result.Ports.Add(p.Number);
        }
        if (numbering.Count > 0) report.Add($"Ports renumbered, since their numbers are taken here: {string.Join(", ", numbering)}.");

        // EM face boundaries: bound or skipped; a face already bounded here is a conflict (Keep by default)
        foreach (var b in src.FaceBoundaries)
        {
            var (bound, here) = Bind($"{b.Object}/{b.Face}");
            if (bound is null) { report.Add($"The {b.Kind} boundary on '{b.Object}/{b.Face}' was not pasted: this document has no such face."); continue; }
            if (here) Here($"The {b.Kind} boundary", bound);
            int slash = bound.IndexOf('/');
            b.Object = bound[..slash];
            b.Face = bound[(slash + 1)..];
            string key = "em:" + bound;
            if (target.FaceBoundaries.FirstOrDefault(x => x.Object == b.Object && x.Face == b.Face) is { } existing)
            {
                plan?.Boundaries.Add(new C3dBoundaryConflict(key, $"EM boundary on {bound}", Spell(existing), Spell(b)));
                if (choices?.ReplaceBoundaries.Contains(key) != true) continue;
                target.FaceBoundaries[target.FaceBoundaries.IndexOf(existing)] = b;
            }
            else target.FaceBoundaries.Add(b);
            result.FaceBoundaries.Add(bound);
        }

        // thermal boundaries: into the target's ACTIVE thermal setup, never a new one
        var carriedThermal = C3dThermal.ThermalSetups(src).FirstOrDefault();
        if (carriedThermal.Thermal?.Boundaries is { Count: > 0 } thermal)
        {
            var active = context.ActiveSetupName is { } an ? C3dThermal.ThermalSetups(target).FirstOrDefault(s => s.Setup.Name == an) : default;
            if (active.Setup is null)
            {
                var thermalNames = C3dThermal.ThermalSetups(target).Select(s => $"'{s.Setup.Name}'").ToList();
                report.Add($"{thermal.Count} thermal boundar{(thermal.Count == 1 ? "y was" : "ies were")} not pasted (from '{carriedThermal.Setup.Name}'): " +
                           (thermalNames.Count == 0 ? "this document has no thermal setup; add one and make it active to paste them."
                                                    : $"no thermal setup is active; make {string.Join(" or ", thermalNames)} active to paste them."));
            }
            else
            {
                var list = active.Thermal.Boundaries ??= [];
                bool changed = false;
                foreach (var b in thermal)
                {
                    string face = b.Face;
                    if (face != C3dThermal.ExposedFaces)
                    {
                        var (bound, here) = Bind(face);
                        if (bound is null) { report.Add($"The thermal boundary on '{face}' was not pasted: this document has no such face."); continue; }
                        if (here) Here("A thermal boundary", bound);
                        face = bound;
                    }
                    b.Face = face;
                    string key = "thermal:" + face;
                    if (list.FirstOrDefault(x => x.Face == face) is { } existing)
                    {
                        plan?.Boundaries.Add(new C3dBoundaryConflict(key, $"thermal boundary on {face} ({active.Setup.Name})", Spell(existing), Spell(b)));
                        if (choices?.ReplaceBoundaries.Contains(key) != true) continue;
                        list[list.IndexOf(existing)] = b;
                    }
                    else list.Add(b);
                    changed = true;
                    result.ThermalBoundaries.Add(face);
                }
                if (changed) target.Setups[active.Index] = EmSetupPersistence.ToEmbedded(active.Setup);
            }
        }

        // symmetry planes: one per axis; the same position is the same plane, another is a conflict (Keep by default)
        foreach (var p in src.SymmetryPlanes)
        {
            string key = "symmetry:" + p.Axis;
            if (target.SymmetryPlanes.FirstOrDefault(x => x.Axis == p.Axis) is { } existing)
            {
                if (existing.At == p.At && SameExpr(existing, p)) continue;
                plan?.Boundaries.Add(new C3dBoundaryConflict(key, $"symmetry plane {p.Axis}", SpellAt(existing, target), SpellAt(p, target)));
                if (choices?.ReplaceBoundaries.Contains(key) != true) continue;
                target.SymmetryPlanes[target.SymmetryPlanes.IndexOf(existing)] = p;
            }
            else target.SymmetryPlanes.Add(p);
            result.SymmetryPlanes.Add(p.Axis);
        }

        // thermal places: a heat source's solid and a probe's solid/face/wire bound as a port's conductors are
        foreach (var h in src.HeatSources)
        {
            if (h.Solid is { } s)
            {
                var (bound, here) = Bind(s);
                if (bound is null) { report.Add($"Heat source '{h.Name}' was not pasted: its solid '{s}' is not here."); continue; }
                if (here) Here($"Heat source '{h.Name}'", bound);
                h.Solid = bound;
            }
            target.HeatSources.Add(h);
            result.Places.Add(h.Name);
        }
        foreach (var p in src.Probes)
        {
            var refs = new List<(string Text, Action<string> Set)>();
            if (p.Solid is { } ps) refs.Add((ps, v => p.Solid = v));
            if (p.Wire is { } pw) refs.Add((pw, v => p.Wire = v));
            if (p.Spot is { } spot) refs.Add((spot.Face, v => spot.Face = v));
            for (int k = 0; k < (p.Face?.Count ?? 0); k++) { int kk = k; refs.Add((p.Face![k], v => p.Face![kk] = v)); }
            var bound = refs.Select(r => (r.Text, r.Set, Result: Bind(r.Text))).ToList();
            if (bound.FirstOrDefault(b => b.Result.Ref is null) is { Text: { } missing })
            {
                report.Add($"Probe '{p.Name}' was not pasted: '{missing}' is not here.");
                continue;
            }
            foreach (var (_, set, (rf, here)) in bound)
            {
                if (here) Here($"Probe '{p.Name}'", rf!);
                set(rf!);
            }
            target.Probes.Add(p);
            result.Places.Add(p.Name);
        }
        foreach (var m in src.MeshRegions) { target.MeshRegions.Add(m); result.Places.Add(m.Name); }
        foreach (var b in src.EffectiveBlocks) { target.EffectiveBlocks.Add(b); result.Places.Add(b.Name); }

        // 6. placed cells: the layout paste's rebase, shared
        // a document never saved has no folder: the rebase then takes the base-independent forms
        string targetDir = context.TargetPath.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(context.TargetPath)) ?? "" : "";
        for (int i = 0; i < src.Instances.Count; i++)
        {
            var inst = src.Instances[i];
            string? dir = i < fragment.InstanceCellDirs.Count ? fragment.InstanceCellDirs[i] : null;
            string? rel = i < fragment.InstanceWorkspaceRelativeDirs.Count ? fragment.InstanceWorkspaceRelativeDirs[i] : null;
            inst.CellRef = CellRefRebase.Rebase(inst.CellRef, dir, rel, targetDir, context.WorkspaceRootDir);
            var resolved = Workspace.ExternalCellRef.ResolveCellDir(inst.CellRef, targetDir.Length > 0 ? targetDir : null, out _, out bool exists);
            if (resolved is null || !exists)
                report.Add($"Placed cell '{inst.Name}' refers to '{inst.CellRef}', which resolves to nothing here: it pastes as a broken reference.");
            else if (context.WorkspaceRootDir is { } root && !Path.GetFullPath(resolved).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                report.Add($"Placed cell '{inst.Name}' refers to a cell outside this workspace ({resolved}).");
        }

        // brief-em3d-101 R-em3d101-5a — an image copied with an absolute path is written as the target stores one (relative to
        // the target .c3d inside its workspace, absolute outside it); a document never saved keeps it absolute.
        if (context.TargetPath.Length > 0)
            foreach (var img in C3dImages.AllImages(src.Objects))
                if (Path.IsPathRooted(img.Path)) img.Path = C3dImages.Store(context.TargetPath, img.Path);

        // 7. the objects and instances themselves, after everything else
        foreach (var o in src.Objects) { target.Objects.Add(o); result.Objects.Add(o.Name); }
        foreach (var i in src.Instances) { target.Instances.Add(i); result.Instances.Add(i.Name); }

        // 8. cycle detection (mandatory): a Reuse can close one (the target's a names b, the pasted b names a)
        if (plan is null)
        {
            var check = C3dResolver.Resolve(target, context.Cell);
            if (check.Errors.FirstOrDefault(e => e.StartsWith("The names cycle", StringComparison.Ordinal)) is { } cycle)
                return Refused($"Nothing was pasted. {cycle} Rename the pasted variable instead of reusing this document's.");
        }

        // the report: its first line is the summary
        int count = result.Objects.Count + result.Instances.Count;
        var head = new List<string>();
        if (count > 0) head.Add($"Pasted {count} object{(count == 1 ? "" : "s")}");
        if (result.Ports.Count > 0) head.Add($"{result.Ports.Count} port{(result.Ports.Count == 1 ? "" : "s")}");
        int bounds = result.FaceBoundaries.Count + result.ThermalBoundaries.Count + result.SymmetryPlanes.Count;
        if (bounds > 0) head.Add($"{bounds} boundar{(bounds == 1 ? "y" : "ies")}");
        if (result.Places.Count > 0) head.Add($"{result.Places.Count} thermal place{(result.Places.Count == 1 ? "" : "s")}");
        string summary = head.Count == 0 ? "Pasted nothing" : (count > 0 ? "" : "Pasted ") + string.Join(", ", head);
        if (renames.Count > 0) summary += "; renamed " + string.Join(", ", renames);
        result.Report.Add(summary);
        if (created.Count > 0)
            result.Report.Add("Variables created: " + string.Join(", ", created.Select(v =>
                notes.TryGetValue(varRenames.FirstOrDefault(kv => kv.Value == v.Name).Key ?? v.Name, out var n) && n.FromParameter
                    ? $"{v.Name} (a cell parameter in the copy's view; a plain VAR here)" : v.Name)));
        if (varRenames.Count > 0) result.Report.Add("Variables renamed: " + string.Join(", ", varRenames.Select(kv => $"{kv.Key} → {kv.Value}")));
        if (reused.Count > 0) result.Report.Add("Variables reused (this document's definitions): " + string.Join(", ", reused));
        if (reusedIdentical.Count > 0) result.Report.Add("Variables reused, identical: " + string.Join(", ", reusedIdentical));
        result.Report.AddRange(report);
        return result;

        C3dPasteResult Refused(string why) => new() { Refusal = why };
    }

    // ── names ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every name an object, instance, port or thermal place already has in <paramref name="doc"/>: one namespace.</summary>
    private static HashSet<string> TakenNames(C3dDocument doc)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in doc.Objects.SelectMany(C3dOperands.SelfAndDescendants)) if (o.Name.Length > 0) set.Add(o.Name);
        foreach (var i in doc.Instances) set.Add(i.Name);
        foreach (var p in doc.Ports) if (p.Name.Length > 0) set.Add(p.Name);
        foreach (var h in doc.HeatSources) set.Add(h.Name);
        foreach (var p in doc.Probes) set.Add(p.Name);
        foreach (var m in doc.MeshRegions) set.Add(m.Name);
        foreach (var b in doc.EffectiveBlocks) set.Add(b.Name);
        return set;
    }

    /// <summary>A face spelling with each Tool name that prefixes a face (<c>tool:zmax</c>) renamed — the only place a Tool's
    /// name appears inside another name. By segment, never by substring.</summary>
    private static string RenameToolPrefixes(string text, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0 || !text.Contains(':')) return text;
        var sb = new System.Text.StringBuilder();
        int start = 0;
        for (int k = 0; k <= text.Length; k++)
        {
            if (k < text.Length && text[k] is not (':' or '|' or '(' or ')' or '/' or '#')) continue;
            string seg = text[start..k];
            sb.Append(k < text.Length && text[k] == ':' && map.TryGetValue(seg, out var to) ? to : seg);
            if (k < text.Length) sb.Append(text[k]);
            start = k + 1;
        }
        return sb.ToString();
    }

    private static string FreeName(string name, Func<string, bool> taken)
    {
        for (int k = 2; ; k++)
        {
            string n = $"{name}_{k.ToString(CultureInfo.InvariantCulture)}";
            if (!taken(n)) return n;
        }
    }

    // ── variables ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Every expression the fragment carries, as text: object and place dimensions, instance overrides, a port's Z0,
    /// a heat source's power and a thermal boundary's values.</summary>
    private static IEnumerable<string> ExpressionTexts(C3dDocument d)
    {
        foreach (var f in C3dBindings.Bound(d)) yield return f.Expr.Expr;
        foreach (var i in d.Instances) foreach (var e in i.Params?.Values ?? Enumerable.Empty<C3dExpr>()) yield return e.Expr;
        foreach (var p in d.Ports) yield return p.Z0;
        foreach (var t in d.Ports.SelectMany(p => p.Terminals ?? [])) yield return t.Z0;
        foreach (var h in d.HeatSources) if (h.Power is { Length: > 0 } hp) yield return hp;
        foreach (var (_, _, t) in C3dThermal.ThermalSetups(d)) foreach (var f in C3dThermal.ExpressionFields(t)) yield return f.Text;
    }

    /// <summary>Renames <paramref name="from"/> in every pasted expression (not the VARs'), through the tokenizer.</summary>
    private static void RenameInExpressions(C3dDocument d, string from, string to)
    {
        foreach (var f in C3dBindings.Bound(d).ToList())
            if (C3dExpressionText.Rename(f.Expr.Expr, from, to) is var r && r != f.Expr.Expr)
                C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, f.Expr with { Expr = r });
        foreach (var i in d.Instances)
            if (i.Params is { } ps)
                foreach (string k in ps.Keys.ToList()) ps[k] = ps[k] with { Expr = C3dExpressionText.Rename(ps[k].Expr, from, to) };
        foreach (var p in d.Ports) p.Z0 = C3dExpressionText.Rename(p.Z0, from, to);
        foreach (var t in d.Ports.SelectMany(p => p.Terminals ?? [])) t.Z0 = C3dExpressionText.Rename(t.Z0, from, to);
        foreach (var h in d.HeatSources) if (h.Power is { Length: > 0 } hp) h.Power = C3dExpressionText.Rename(hp, from, to);
        foreach (var (index, setup, t) in C3dThermal.ThermalSetups(d).ToList())
        {
            foreach (var f in C3dThermal.ExpressionFields(t).ToList())
                if (C3dExpressionText.Rename(f.Text, from, to) is var r && r != f.Text) f.Set(r);
            d.Setups[index] = EmSetupPersistence.ToEmbedded(setup);
        }
    }

    /// <summary>The target's definition of <paramref name="name"/> — a VAR's own, or its cell parameter's (a linked VAR takes the
    /// parameter's) — or null when the target has no such name.</summary>
    private static (string Expression, string? Unit)? TargetDefinition(C3dDocument target, C3dCell cell, string name)
    {
        var v = target.Variables.FirstOrDefault(x => x.Name == name);
        var p = cell.Parameter(name);
        if (v is not null && (p is null || v.Linked == false)) return (v.Expression, v.Unit);
        if (p is not null) return (p.DefaultExpression, p.Unit is { Length: > 0 } u ? u : null);
        return null;
    }

    private static bool IsNameTaken(C3dDocument target, C3dCell cell, string name)
        => target.Variables.Any(v => v.Name == name) || cell.Parameter(name) is not null;

    /// <summary>brief-em3d-95 D5a — the same expression after tokenizing (whitespace aside) and the same unit.</summary>
    private static bool SameDefinition((string Expression, string? Unit) here, C3dVariable copy)
        => C3dUnits.Engine(here.Unit, out _) == C3dUnits.Engine(copy.Unit, out _) && SameTokens(here.Expression, copy.Expression);

    private static bool SameTokens(string a, string b)
    {
        try
        {
            var ta = new Tokenizer(a).Tokenize();
            var tb = new Tokenizer(b).Tokenize();
            return ta.Length == tb.Length && ta.Zip(tb).All(p => p.First.Kind == p.Second.Kind && p.First.Text == p.Second.Text && p.First.Unit == p.Second.Unit);
        }
        catch (ExpressionException) { return a.Trim() == b.Trim(); }
    }

    private static bool SameExpr(IC3dBindable a, IC3dBindable b)
        => (a.Exprs is null && b.Exprs is null) || (a.Exprs is { } ea && b.Exprs is { } eb &&
           ea.Count == eb.Count && ea.All(kv => eb.TryGetValue(kv.Key, out var o) && kv.Value.SequenceEqual(o)));

    private static string UnitText(string? unit) => unit is { Length: > 0 } u ? $" {u}" : "";

    private static string ValueText(C3dResolution scope, string name)
        => scope.Names.TryGetValue(name, out var n) && Spell(n) is { } s && s != n.Expression ? $" = {s}" : "";

    /// <summary>A resolved name's value in its own unit: <c>250 um</c>, or a plain number.</summary>
    private static string? Spell(C3dName n)
    {
        if (n.Value is not { } v) return null;
        if (n.Unit is { } u && u != C3dResolver.BaseUnit && Units.Scale(u) is { } scale and not 0)
            return (v / scale).ToString("G6", CultureInfo.InvariantCulture) + " " + u;
        return v.ToString("G6", CultureInfo.InvariantCulture) + (n.Unit == C3dResolver.BaseUnit ? " m" : "");
    }

    // ── boundaries, spelled for the dialog ────────────────────────────────────────────────────

    private static string Spell(C3dFaceBoundary b) => b.Kind + (b.Material is { } m ? $" ({m})" : "");

    private static string Spell(CemThermalBoundary b) => b.Kind switch
    {
        ThermalBoundaryKind.FixedT => $"{b.TempC} °C",
        ThermalBoundaryKind.Convection => $"h {b.H} W/(m²·K) to {b.AmbientC} °C",
        _ => b.Kind.ToString(),
    };

    private static string SpellAt(C3dSymmetryPlane p, C3dDocument doc)
    {
        string at = (p.At / (double)doc.DbuPerMicron).ToString("G6", CultureInfo.InvariantCulture) + " µm";
        return C3dBindings.GetExpr(p, nameof(C3dSymmetryPlane.At), 0) is { } e ? $"{p.Axis} = {e.Expr} ({at})" : $"{p.Axis} = {at}";
    }

    // ── materials ─────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> MaterialNames(C3dDocument d)
    {
        foreach (var o in d.Objects.SelectMany(C3dOperands.SelfAndDescendants)) if (o.Material is { Length: > 0 } m) yield return m;
        foreach (var b in d.FaceBoundaries) if (b.Material is { Length: > 0 } m) yield return m;
    }

    /// <summary>A material's key values in a line: <c>εr 4.5, tanδ 0.02</c>.</summary>
    public static string Summary(TechMaterial m)
    {
        var parts = Fields(m).Where(f => f.Value is not null).Select(f => $"{f.Label} {f.Value}").ToList();
        return parts.Count == 0 ? "no values" : string.Join(", ", parts);
    }

    /// <summary>Where two definitions of one material differ, <c>εr 4.3 here, 4.5 in the copy</c>; null when they do not.</summary>
    private static string? Difference(TechMaterial here, TechMaterial copy)
    {
        string Norm(TechMaterial m)
        {
            var c = MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize([m]))[0];
            c.Name = c.Name.ToLowerInvariant();
            return MaterialLibraryPersistence.Serialize([c]);
        }
        if (Norm(here) == Norm(copy)) return null;
        var diffs = Fields(here).Zip(Fields(copy)).Where(p => p.First.Value != p.Second.Value)
                                 .Select(p => $"{p.First.Label} {p.First.Value ?? "unset"} here, {p.Second.Value ?? "unset"} in the copy").ToList();
        return diffs.Count > 0 ? string.Join("; ", diffs) : "a value other than the key ones differs";
    }

    private static IEnumerable<(string Label, string? Value)> Fields(TechMaterial m)
    {
        static string? N(double? v) => v?.ToString("G6", CultureInfo.InvariantCulture);
        yield return ("εr", m.EpsrTensor is { } t ? "[" + string.Join(", ", t.Select(x => N(x))) + "]" : N(m.Epsr));
        yield return ("tanδ", N(m.TanD));
        yield return ("μr", N(m.Mur));
        yield return ("σ₂₀", N(m.Sigma20) is { } s ? s + " S/m" : null);
        yield return ("k", m.ThermalKTensor is { } k ? "[" + string.Join(", ", k.Select(x => N(x))) + "]" : N(m.ThermalK) is { } kk ? kk + " W/(m·K)" : null);
        yield return ("ρ", N(m.DensityKgM3) is { } r ? r + " kg/m³" : null);
        yield return ("c", N(m.SpecificHeat) is { } c ? c + " J/(kg·K)" : null);
    }

    // ── DBU rescale ───────────────────────────────────────────────────────────────────────────

    private static IEnumerable<object> Rescalable(C3dDocument d)
        => d.Objects.Cast<object>().Concat(d.Instances).Concat(d.Ports).Concat(d.HeatSources).Concat(d.Probes)
                    .Concat(d.MeshRegions).Concat(d.EffectiveBlocks).Concat(d.SymmetryPlanes);

    /// <summary>
    /// Every DBU coordinate of <paramref name="item"/>, and of every record inside it, through <paramref name="f"/>. A
    /// coordinate is a <c>long</c>, a point or a list of points in a .c3d type — every length the format stores is one of
    /// those, while a micrometre value is a double and a count an int; the one long that is a count
    /// (<see cref="C3dWireArray.Count"/>) is named. A walker rather than a list, so a coordinate added later rescales with
    /// no second place to remember it; <c>C3dFragmentTests</c>' DBU gate measures it in metres.
    /// </summary>
    private static void Rescale(object item, Func<long, long> f)
    {
        foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
            if (p.DeclaringType == typeof(C3dWireArray) && p.Name == nameof(C3dWireArray.Count)) continue;
            object? v = p.GetValue(item);
            switch (v)
            {
                case null: break;
                case long l: p.SetValue(item, f(l)); break;
                case C3dPoint3 q: p.SetValue(item, P3(q, f)); break;
                case C3dPoint2 q: p.SetValue(item, P2(q, f)); break;
                case List<C3dPoint3> l: for (int k = 0; k < l.Count; k++) l[k] = P3(l[k], f); break;
                case List<C3dPoint2> l: for (int k = 0; k < l.Count; k++) l[k] = P2(l[k], f); break;
                case List<List<C3dPoint2>> ll: foreach (var l in ll) for (int k = 0; k < l.Count; k++) l[k] = P2(l[k], f); break;
                case System.Collections.IList list when IsRecord(list.GetType().GetGenericArguments().FirstOrDefault()):
                    foreach (object? e in list) if (e is not null) Rescale(e, f);
                    break;
                default:
                    if (IsRecord(v.GetType())) Rescale(v, f);
                    break;
            }
        }

        static bool IsRecord(Type? t) => t is { IsClass: true } && t != typeof(string) && t.Namespace == typeof(C3dDocument).Namespace;
        static C3dPoint3 P3(C3dPoint3 q, Func<long, long> f) => new(f(q.X), f(q.Y), f(q.Z));
        static C3dPoint2 P2(C3dPoint2 q, Func<long, long> f) => new(f(q.U), f(q.V));
    }

    // ── the target, copied and copied back ────────────────────────────────────────────────────

    /// <summary>A working copy, as the file spells it (a bound field's number is resolved again at the end).</summary>
    private static C3dDocument Clone(C3dDocument d) => C3dPersistence.Deserialize(C3dPersistence.Serialize(d));

    private static void CopyInto(C3dDocument from, C3dDocument to)
    {
        to.FormatVersion = from.FormatVersion;
        to.DbuPerMicron = from.DbuPerMicron;
        to.DisplayUnit = from.DisplayUnit;
        to.SnapDbu = from.SnapDbu;
        to.TechRef = from.TechRef;
        to.Objects = from.Objects;
        to.Instances = from.Instances;
        to.Variables = from.Variables;
        to.Ports = from.Ports;
        to.FaceBoundaries = from.FaceBoundaries;
        to.HeatSources = from.HeatSources;
        to.Probes = from.Probes;
        to.MeshRegions = from.MeshRegions;
        to.ContactResistances = from.ContactResistances;
        to.EffectiveBlocks = from.EffectiveBlocks;
        to.SymmetryPlanes = from.SymmetryPlanes;
        to.WireGroundPlane = from.WireGroundPlane;
        to.FieldPlots = from.FieldPlots;
        to.AirBoxMaterial = from.AirBoxMaterial;
        to.AirBoxHidden = from.AirBoxHidden;
        to.Look = from.Look;
        to.Setups = from.Setups;
        to.Unread = from.Unread;
    }
}
