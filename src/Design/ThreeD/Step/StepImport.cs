// brief-em3d-68 — STEP import: the ONE function File ▸ Import ▸ STEP… and `circuitrf convert x.step`
// both call. Nothing here interprets STEP: the worker reads the file (OCCT's reader, with names, colours, units and
// healing) and answers with a description; this file decides what becomes an object, and writes it.
//
//   Read        the file → a plan: one row per part, its proposed name, its material and WHY, whether it can be imported
//   Apply       a plan → the file copied into the 3D view's folder (D7) and one Step object per checked part
//   Import      Read + Apply + save, into a NEW .c3d — `convert`'s whole verb, so the CLI holds no import logic
//   PlanReload  a revised source file → which references into it still land, re-pointed by GEOMETRY, never by index
//   ApplyReload a reload plan → the new copy, its hash, and every reference re-pointed
//
// ONE OBJECT PER SOLID (R-em3d68-1b, brief-em3d-128). A Step object is one solid with one name, one material, one
// placement and one face namespace, so every rule a Box obeys a Step part obeys. A product of one solid is one row with no
// Solid, as brief 68 wrote it; a product of several is one row per solid. The assembly's transform is the worker's,
// applied from the occurrence path; the placement starts at identity, so an imported part lands exactly where its file
// puts it.
//
// AN IMPORT OF MORE THAN ONE OBJECT IS ONE GROUP (R-em3d128-2), named after the file, so the package acts as one thing
// (C3dGroups) and each piece is still its own object. In a file of several products, a product of several solids is a
// group inside it. One object created: no group.
//
// MATERIALS BY NAME, THEN BY EXACT COLOUR, THEN NONE (R-em3d68-3a). Nothing fuzzier: a near-colour match would give a
// gold-coloured plastic a metal's conductivity in silence. No material is not a refusal — it is the ordinary unmapped
// state, drawn, ignored by the solver and named by `check`.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;

namespace CircuitRF.Design.ThreeD.Step;

/// <summary>Why a part's material is what it is (R-em3d68-3c).</summary>
public enum StepMatch
{
    /// <summary>The part's name equals a material's, ignoring case.</summary>
    ByName,
    /// <summary>The part's colour equals a material's <c>Color</c>, exactly, at 8 bits a channel.</summary>
    ByColour,
    /// <summary>Neither: the part has no material until someone gives it one.</summary>
    Unmatched,
    /// <summary>Someone chose it (the dialog's combo, or <c>--material</c>).</summary>
    Chosen,
}

/// <summary>One importable object of the file: a row of the dialog's table — a product of one solid, or one solid of a
/// product of several (brief-em3d-128).</summary>
public sealed class StepImportPart
{
    /// <summary>The occurrence path in the file's assembly (<c>1/2</c>) — the object's <c>Part</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The STEP product name, as the file gives it; empty when it gives none.</summary>
    public required string ProductName { get; init; }

    /// <summary>brief-em3d-128 — which solid of the product, 1-based: the object's <c>Solid</c>. Null for a product of one
    /// solid, which is imported whole.</summary>
    public int? Solid { get; init; }

    /// <summary>How many solids the product has: 1 for a whole-product row.</summary>
    public int Solids { get; init; } = 1;

    /// <summary>The solid's own name, as the file gives it; empty when it gives none, and for a whole-product row.</summary>
    public string SolidName { get; init; } = "";

    /// <summary>The solid's faces disagree on a colour, so it has none to match by (overview D6).</summary>
    public bool Mixed { get; init; }

    /// <summary>R-em3d128-2b — in a file of several products, the group a product of several solids gathers in, inside the
    /// file's; null otherwise.</summary>
    public string? SubGroup { get; init; }

    /// <summary><c>#rrggbb</c>, or null when the file gives the part (or the solid) no colour, or the solid is mixed.</summary>
    public string? Colour { get; init; }

    /// <summary>A closed solid after healing — importable. Anything else is listed, unchecked and disabled (R-em3d68-4b).</summary>
    public bool Closed { get; init; }

    /// <summary>Why it is not a closed solid, when it is not.</summary>
    public string Why { get; init; } = "";

    /// <summary>What shape healing changed, when it ran.</summary>
    public string Healing { get; init; } = "";

    public int Faces { get; init; }
    public long Triangles { get; init; }

    /// <summary>The object's name: the product name made valid and unique, editable before OK (R-em3d68-1d).</summary>
    public string Name { get; set; } = "";

    /// <summary>The material, or null for none.</summary>
    public string? Material { get; set; }

    public StepMatch Match { get; set; }

    /// <summary>Checked: imported on OK. Always false for a part that is not a closed solid.</summary>
    public bool Import { get; set; }

    /// <summary>The match's reason in the table's words.</summary>
    public string MatchText => Match switch
    {
        StepMatch.ByName => "by name",
        StepMatch.ByColour => "by colour",
        StepMatch.Chosen => "chosen",
        _ => "unmatched",
    };
}

/// <summary>What <see cref="StepImport.Read"/> found: the rows, the file's units and the notes the import will make.</summary>
public sealed class StepImportPlan
{
    public required string SourcePath { get; init; }
    public required byte[] Bytes { get; init; }

    /// <summary><c>sha256:&lt;hex&gt;</c> of <see cref="Bytes"/> — the copy's hash, since the copy is these bytes.</summary>
    public required string Hash { get; init; }

    /// <summary>The file's length units as the reader names them (<c>inch</c>, <c>millimetre</c>), and µm per each.</summary>
    public required IReadOnlyList<string> Units { get; init; }
    public required IReadOnlyList<double> UnitMicrons { get; init; }

    public required IReadOnlyList<StepImportPart> Parts { get; init; }

    /// <summary>The materials a part may be given: the destination technology's own and its libraries'.</summary>
    public required IReadOnlyList<string> Materials { get; init; }

    /// <summary>Why no part can be matched at all — the destination has no materials (R-em3d68-3d) — or null.</summary>
    public string? NoMaterials { get; init; }

    /// <summary>Healing, one line per part it changed (R-em3d68-4a).</summary>
    public required IReadOnlyList<string> Healing { get; init; }

    /// <summary>Dimensions, tolerances and datums the file carried; none is imported.</summary>
    public int Pmi { get; init; }

    /// <summary>What is recorded as each object's <c>Unit</c>.</summary>
    public string Unit => string.Join(", ", Units);

    /// <summary>The dialog's units line, in words (R-em3d68-2b).</summary>
    public string UnitsLine => StepImport.UnitsLine(Units, UnitMicrons);

    public long Triangles => Parts.Where(p => p.Import).Sum(p => p.Triangles);

    /// <summary>R-em3d128-2 — the group the import gathers its objects in when it creates more than one: the file's stem,
    /// made a legal group name and unique among the document's groups. Null or empty: no group.</summary>
    public string? Group { get; set; }
}

/// <summary>What <see cref="StepImport.Apply"/> did.</summary>
/// <param name="CopiedPath">The copy in the 3D view's folder.</param>
/// <param name="Created">True when this import wrote the copy; false when an identical file was already there.</param>
/// <param name="Objects">The objects added, in the order they were appended.</param>
/// <param name="Notes">What the import says it did: healing, parts skipped, parts left without a material, PMI.</param>
public sealed record StepImportResult(string CopiedPath, bool Created, byte[] Bytes, IReadOnlyList<C3dStep> Objects, IReadOnlyList<string> Notes)
{
    /// <summary>The rows each object was made from, parallel to <see cref="Objects"/>: what <c>convert</c>'s JSON reports of
    /// each (its match, R-em3d128-3b).</summary>
    public IReadOnlyList<StepImportPart> Rows { get; init; } = [];
}

/// <summary>A refusal of the import (or a reload) as a whole: a coded diagnostic, its sentence the message.</summary>
public sealed class StepImportException(Diagnostic diagnostic) : Exception(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>brief-em3d-68 — what STEP import and Reload from Source refuse, as coded diagnostics (<c>C3dDiagnostics</c>'
/// pattern). Returned in the exception; the dialog, the status line and <c>convert</c>'s stderr all render the same sentence.</summary>
public static class StepDiagnostics
{
    public static Diagnostic NothingChecked() => new(
        "step.import.nothing-checked", DiagnosticSeverity.Error, "No part is checked, so there is nothing to import.");

    public static Diagnostic NoSolid(string file, string why) => Diagnostic.Create(
        "step.import.no-solid", DiagnosticSeverity.Error,
        "'{file}' holds no closed solid, so there is nothing to import.{why}", ("file", file), ("why", why.Length > 0 ? " " + why : ""));

    public static Diagnostic Names(string why) => Diagnostic.Create(
        "step.import.name", DiagnosticSeverity.Error, "{why}", ("why", why));

    public static Diagnostic GroupName(string why) => Diagnostic.Create(
        "step.import.group", DiagnosticSeverity.Error, "{why}", ("why", why));

    public static Diagnostic TargetExists(string path) => Diagnostic.Create(
        "step.import.target-exists", DiagnosticSeverity.Error,
        "'{path}' exists; importing into an existing document is writing its Step objects — see `reference topic=c3d`.", ("path", path));

    public static Diagnostic TechnologyMissing(string path) => Diagnostic.Create(
        "step.import.technology-missing", DiagnosticSeverity.Error, "The technology '{path}' does not exist.", ("path", path));

    public static Diagnostic TechnologyUnreadable(string path, string why) => Diagnostic.Create(
        "step.import.technology-unreadable", DiagnosticSeverity.Error, "The technology '{path}' could not be read: {why}", ("path", path), ("why", why));

    public static Diagnostic NoSuchPart(string path, string parts) => Diagnostic.Create(
        "step.import.no-such-part", DiagnosticSeverity.Error, "The file has no part '{path}'. Its parts: {parts}.", ("path", path), ("parts", parts));

    public static Diagnostic MaterialPartUnknown(string key, string parts) => Diagnostic.Create(
        "step.import.material-part", DiagnosticSeverity.Error,
        "--material names '{key}', which is no part of the file. Its parts: {parts}.", ("key", key), ("parts", parts));

    public static Diagnostic MaterialUnknown(string material, string known) => Diagnostic.Create(
        "step.import.material-unknown", DiagnosticSeverity.Error,
        "--material names '{material}', which the technology does not define. {known}", ("material", material), ("known", known));

    /// <summary>brief-em3d-129 — Split into Solids refused, every reason in one sentence list.</summary>
    public static Diagnostic Split(string why) => Diagnostic.Create(
        "step.split", DiagnosticSeverity.Error, "{why}", ("why", why));

    /// <summary>brief-em3d-130 — Replace with Prism / Box or Convert to Polyhedron refused, every reason in one sentence list.</summary>
    public static Diagnostic Convert(string why) => Diagnostic.Create(
        "step.convert", DiagnosticSeverity.Error, "{why}", ("why", why));

    public static Diagnostic NotNamed(string file) => Diagnostic.Create(
        "step.reload.not-named", DiagnosticSeverity.Error, "No object names '{file}'.", ("file", file));

    public static Diagnostic SourceUnreadable(string file, string source) => Diagnostic.Create(
        "step.reload.source", DiagnosticSeverity.Error,
        "The source of '{file}' ({source}) cannot be read, so it cannot be reloaded.", ("file", file), ("source", source));
}

/// <summary>What <c>convert</c> answers with flags where the dialog asks (R-em3d68-7b).</summary>
public sealed record StepImportOptions
{
    /// <summary><c>--material &lt;part&gt;=&lt;name&gt;</c>: a part (occurrence path, product name or object name) → material.</summary>
    public IReadOnlyList<(string Part, string Material)> Materials { get; init; } = [];

    /// <summary><c>--part &lt;path&gt;</c>: import only these occurrence paths, every solid of each; <c>&lt;path&gt;#&lt;k&gt;</c>
    /// is one solid (a CLI spelling only — the document writes <c>Part</c> and <c>Solid</c>). Empty imports every solid.</summary>
    public IReadOnlyList<string> Parts { get; init; } = [];

    /// <summary><c>--group &lt;name&gt;</c>: the group's name; <c>""</c> is no group; null takes the dialog's default.</summary>
    public string? Group { get; init; }

    /// <summary><c>--tech &lt;path&gt;</c>: the new document's <c>TechRef</c>; null takes the workspace's default.</summary>
    public string? TechPath { get; init; }
}

/// <summary>brief-em3d-68 — STEP import and Reload from Source.</summary>
public static class StepImport
{
    /// <summary>The display tessellation's deflection as a fraction of a part's diagonal — the elaborator's own
    /// (<see cref="C3dElaborator.DisplayRelativeDeflection"/>), so the triangle count the dialog shows is what is drawn.</summary>
    public static double DisplayRelative => C3dElaborator.DisplayRelativeDeflection;

    /// <summary>R-em3d68-3d — said when the destination can match nothing.</summary>
    public const string NoMaterialsReason = "This document's technology defines no materials.";

    // ── what a file is (R-em3d68-7d) ────────────────────────────────────────────────────────────

    /// <summary>A STEP file by extension: <c>.step</c> or <c>.stp</c>.</summary>
    public static bool IsStepExtension(string path)
        => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".step" or ".stp";

    /// <summary>
    /// A STEP file by CONTENT: its first line is ISO 10303-21's header, <c>ISO-10303-21;</c>. The one line of STEP text
    /// read outside the worker (§10), and the one rule — <c>convert</c> and <c>check</c> both reach it through
    /// <c>LayoutConvert.DetectSource</c>.
    /// </summary>
    public static bool LooksLikeStep(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var buf = new byte[64];
            int n = s.Read(buf, 0, buf.Length);
            string head = Encoding.ASCII.GetString(buf, 0, n).TrimStart('﻿', 'ï', '»', '¿', ' ', '\t', '\r', '\n');
            return head.StartsWith("ISO-10303-21;", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary><c>sha256:&lt;hex&gt;</c> of some bytes — the spelling <see cref="C3dValidation.StepHash"/> gives a file.</summary>
    public static string HashOf(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ── Read ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="sourcePath"/> through the kernel and proposes a row per product of one solid and per solid of
    /// a product of several: its name made valid and unique in <paramref name="target"/>, its material from
    /// <paramref name="tech"/> (null is an empty technology, never "no destination"), and whether it is checked — and the
    /// group the import gathers in. Nothing is written.
    /// </summary>
    /// <exception cref="GeometryKernelException">The kernel refused the file (not STEP, a unit it cannot resolve), crashed, or is absent.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the worker was killed and nothing changed.</exception>
    public static StepImportPlan Read(string sourcePath, GeometryKernel kernel, Technology? tech, C3dDocument target, RunControl? control = null)
    {
        byte[] bytes = File.ReadAllBytes(sourcePath);
        var read = kernel.ImportStep(bytes, control, DisplayRelative);
        var materials = tech?.ResolvedMaterials ?? [];
        var used = UsedNames(target);
        string stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        // R-em3d128-2a — the file's group, and (2b) in a file of several products one inside it per product of several
        // solids; group names are unique among groups, never among objects (C3dGroups).
        var groups = C3dGroups.Names(target);
        string group = ProposeName(stem, C3dGroups.DefaultStem, groups);
        var parts = new List<StepImportPart>();
        for (int i = 0; i < read.Parts.Count; i++)
        {
            var p = read.Parts[i];
            if (p.Solids.Count <= 1)
            {
                string? colour = p.Colour is { Length: 3 } c ? Hex(c) : null;
                var (material, match) = AutoMatch(p.Name, colour, materials);
                parts.Add(new StepImportPart
                {
                    Path = p.Path, ProductName = p.Name, Colour = colour, Closed = p.Closed, Why = p.Why, Healing = p.Healing,
                    Faces = p.Faces, Triangles = p.Triangles, Material = material, Match = match, Import = p.Closed,
                    Name = ProposeName(p.Name, $"{stem}_{i + 1}", used),
                });
                continue;
            }
            string? sub = read.Parts.Count > 1 ? ProposeName(p.Name, $"{stem}_{i + 1}", groups) : null;
            foreach (var s in p.Solids)
            {
                string? colour = s.Colour is { Length: 3 } c ? Hex(c) : null;
                var (material, match) = AutoMatch(s.Name, colour, materials);
                parts.Add(new StepImportPart
                {
                    Path = p.Path, ProductName = p.Name, Solid = s.Index, Solids = p.Solids.Count, SolidName = s.Name, Mixed = s.Mixed,
                    SubGroup = sub, Colour = colour, Closed = s.Closed, Why = s.Why, Healing = p.Healing, Faces = s.Faces,
                    Triangles = s.Triangles, Material = material, Match = match, Import = s.Closed,
                    Name = SolidName(s.Name, $"{sub ?? group}_{s.Index}", used),
                });
            }
        }
        return new StepImportPlan
        {
            SourcePath = System.IO.Path.GetFullPath(sourcePath), Bytes = bytes, Hash = HashOf(bytes),
            Units = read.Units, UnitMicrons = read.UnitMicrons, Parts = parts, Healing = read.Healing, Pmi = read.Pmi,
            Materials = [.. materials.Select(m => m.Name)],
            NoMaterials = materials.Count == 0 ? NoMaterialsReason : null,
            Group = group,
        };
    }

    /// <summary>Overview D5 — a solid's object name: its own name when the file gives one that is legal and unused, else
    /// <paramref name="fallback"/> (<c>&lt;group&gt;_&lt;k&gt;</c>) made unique. The name is added to <paramref name="used"/>.</summary>
    private static string SolidName(string own, string fallback, ISet<string> used)
    {
        own = own.Trim();
        if (own.Length > 0 && NameValidator.Validate(own) is null && used.Add(own)) return own;
        return ProposeName(fallback, fallback, used);
    }

    /// <summary>R-em3d68-3a — by name (ignoring case), then by exact colour, then none. Nothing fuzzier.</summary>
    public static (string? Material, StepMatch Match) AutoMatch(string partName, string? colour, IReadOnlyList<TechMaterial> materials)
    {
        if (partName.Length > 0 && materials.FirstOrDefault(m => string.Equals(m.Name, partName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } byName)
            return (byName.Name, StepMatch.ByName);
        if (colour is not null && materials.FirstOrDefault(m => m.Color is { } mc && string.Equals(NormaliseHex(mc), colour, StringComparison.Ordinal)) is { } byColour)
            return (byColour.Name, StepMatch.ByColour);
        return (null, StepMatch.Unmatched);
    }

    /// <summary>R-em3d68-3c — <i>Map all of this colour</i>: every row of the file sharing <paramref name="part"/>'s colour —
    /// a part or a solid, whichever product it is in — gets <paramref name="material"/>. A row with no colour (a mixed
    /// solid among them) maps alone. Returns how many rows changed.</summary>
    public static int MapColour(StepImportPlan plan, StepImportPart part, string? material)
    {
        int n = 0;
        foreach (var p in plan.Parts)
        {
            if (!ReferenceEquals(p, part) && (part.Colour is null || p.Colour != part.Colour)) continue;
            p.Material = material;
            p.Match = material is null ? StepMatch.Unmatched : StepMatch.Chosen;
            n++;
        }
        return n;
    }

    /// <summary>R-em3d128-1b — a product's header row: every solid of the product at <paramref name="path"/> takes
    /// <paramref name="material"/>, as chosen. Returns how many rows changed.</summary>
    public static int SetProductMaterial(StepImportPlan plan, string path, string? material)
    {
        int n = 0;
        foreach (var p in plan.Parts.Where(p => p.Path == path && p.Solid is not null))
        {
            p.Material = material;
            p.Match = material is null ? StepMatch.Unmatched : StepMatch.Chosen;
            n++;
        }
        return n;
    }

    /// <summary>R-em3d128-1b — a product's header check box: every closed solid of the product checked or unchecked.</summary>
    public static void SetProductImport(StepImportPlan plan, string path, bool import)
    {
        foreach (var p in plan.Parts.Where(p => p.Path == path && p.Solid is not null)) p.Import = import && p.Closed;
    }

    /// <summary>A colour, RGB 0–1, as <c>#rrggbb</c>: the 8-bit spelling a material's <c>Color</c> is written in.</summary>
    public static string Hex(double[] rgb)
    {
        static int B(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"#{B(rgb[0]):x2}{B(rgb[1]):x2}{B(rgb[2]):x2}");
    }

    private static string NormaliseHex(string s) => s.Trim().ToLowerInvariant() is var t && t.Length == 9 && t[0] == '#' ? t[..7] : s.Trim().ToLowerInvariant();

    // ── names (R-em3d68-1d) ─────────────────────────────────────────────────────────────────────

    /// <summary>Every name the document already uses — objects, Tools at any depth, instances — case-insensitively.</summary>
    public static HashSet<string> UsedNames(C3dDocument doc)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { C3dValidation.ReservedName };
        foreach (var o in doc.Objects)
            foreach (var d in C3dOperands.SelfAndDescendants(o))
                if (d.Name.Length > 0) used.Add(d.Name);
        foreach (var i in doc.Instances) used.Add(i.Name);
        return used;
    }

    /// <summary>
    /// A part's object name: the product name made valid by <see cref="NameValidator"/>'s rule (each character it forbids
    /// becomes <c>_</c>; a trailing space or dot is dropped) — <paramref name="fallback"/> when nothing is left — then made
    /// unique by <c>_2</c>, <c>_3</c>. The name is added to <paramref name="used"/>. A product name's own trailing number
    /// is never incremented, as Duplicate increments one: <c>part-1</c> becoming <c>part-2</c> would name a different
    /// product.
    /// </summary>
    public static string ProposeName(string productName, string fallback, ISet<string> used)
    {
        var sb = new StringBuilder();
        foreach (char ch in productName.Trim())
            sb.Append(ch <= 0x1F || "<>:\"/\\|?*".Contains(ch) ? '_' : ch);
        string name = sb.ToString().TrimEnd(' ', '.');
        if (name.Length == 0 || NameValidator.Validate(name) is not null) name = fallback;
        string unique = name;
        for (int n = 2; used.Contains(unique); n++) unique = $"{name}_{n}";
        used.Add(unique);
        return unique;
    }

    /// <summary>Why the checked rows' names cannot be used as they stand, or null: each valid, none used twice or already in
    /// the document.</summary>
    public static string? NamesRefusal(StepImportPlan plan, C3dDocument target)
    {
        var used = UsedNames(target);
        var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plan.Parts.Where(p => p.Import))
        {
            if (NameValidator.Validate(p.Name) is { } why) return $"'{p.Name}' ({Where(p)}) cannot be a name: {why}";
            if (used.Contains(p.Name)) return $"'{p.Name}' ({Where(p)}) is already a name in this document.";
            if (!mine.Add(p.Name)) return $"'{p.Name}' names two of the parts being imported.";
        }
        return null;
    }

    /// <summary>R-em3d128-2c — why the plan's group cannot be used as it stands, or null: no group (empty) is always
    /// usable; a name is refused as Group Objects refuses a typed one, and so is one a product's own group takes.</summary>
    public static string? GroupRefusal(StepImportPlan plan, C3dDocument target)
    {
        if (string.IsNullOrEmpty(plan.Group)) return null;
        if (C3dGroups.NameRefusal(target, plan.Group) is { } why) return why;
        if (plan.Parts.FirstOrDefault(p => p.SubGroup == plan.Group) is { } sub)
            return $"'{plan.Group}' is the name of the group the solids of {Label(sub)} are gathered in.";
        return null;
    }

    /// <summary>The group path each object of <paramref name="chosen"/> is in (R-em3d128-2): none for one object or no group;
    /// the file's group; or, for a product of several solids in a file of several products, the product's group inside it
    /// — when more than one of its solids is imported (a group of one is no gathering).</summary>
    public static string? GroupPathOf(StepImportPlan plan, IReadOnlyList<StepImportPart> chosen, StepImportPart part)
    {
        if (chosen.Count < 2 || string.IsNullOrEmpty(plan.Group)) return null;
        return part.SubGroup is { } sub && chosen.Count(p => p.Path == part.Path) > 1 ? plan.Group + C3dGroups.Separator + sub : plan.Group;
    }

    // ── Apply ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies the file into <paramref name="c3dPath"/>'s folder (D7, R-em3d68-4e) and appends one Step object per checked
    /// row to <paramref name="doc"/>, gathered in the plan's group when there is more than one (R-em3d128-2). The only
    /// step that writes: the dialog's OK, and <see cref="Import"/>.
    /// </summary>
    /// <exception cref="StepImportException">No row is checked, or a name or the group's name cannot be used.</exception>
    public static StepImportResult Apply(StepImportPlan plan, C3dDocument doc, string c3dPath)
    {
        var chosen = plan.Parts.Where(p => p.Import && p.Closed).ToList();
        if (chosen.Count == 0)
            throw new StepImportException(plan.Parts.Any(p => p.Closed)
                ? StepDiagnostics.NothingChecked()
                : StepDiagnostics.NoSolid(System.IO.Path.GetFileName(plan.SourcePath), ""));
        if (NamesRefusal(plan, doc) is { } bad) throw new StepImportException(StepDiagnostics.Names(bad));
        if (chosen.Count > 1 && GroupRefusal(plan, doc) is { } badGroup) throw new StepImportException(StepDiagnostics.GroupName(badGroup));

        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(c3dPath))!;
        var (copy, created) = CopyInto(dir, System.IO.Path.GetFileName(plan.SourcePath), plan.Bytes, plan.Hash, write: true);
        string source = SourceSpelling(plan.SourcePath, c3dPath);
        var added = new List<C3dStep>();
        foreach (var p in chosen)
        {
            var step = new C3dStep
            {
                Name = p.Name, Material = p.Material, Group = GroupPathOf(plan, chosen, p), File = System.IO.Path.GetFileName(copy),
                Part = p.Path, Solid = p.Solid, Hash = plan.Hash, Unit = plan.Unit, SourcePath = source,
            };
            doc.Objects.Add(step);
            added.Add(step);
        }
        return new StepImportResult(copy, created, plan.Bytes, added, Notes(plan, chosen)) { Rows = chosen };
    }

    /// <summary>What the import says it did (R-em3d68-4a/-4b/-3b, §10, R-em3d128-4): healing, each product imported as
    /// several solids and the group they are in, parts skipped and why, parts with no material, each mixed solid, and the
    /// product-manufacturing information it did not bring.</summary>
    public static IReadOnlyList<string> Notes(StepImportPlan plan, IReadOnlyList<StepImportPart> chosen)
    {
        var notes = new List<string>(plan.Healing);
        foreach (var product in chosen.Where(p => p.Solid is not null).GroupBy(p => p.Path))
            if (GroupPathOf(plan, chosen, product.First()) is { } group)
            {
                var first = product.First();
                string name = first.ProductName.Length > 0 ? first.ProductName : $"part {first.Path}";
                notes.Add($"'{name}' is {first.Solids} solids; each is its own object in group '{C3dGroups.NameOf(group)}'.");
            }
        var solids = plan.Parts.Where(p => !p.Closed).ToList();
        if (solids.Count > 0)
            notes.Add($"{solids.Count} part{(solids.Count == 1 ? " is" : "s are")} not a closed solid and {(solids.Count == 1 ? "was" : "were")} not imported: " +
                      string.Join("; ", solids.Select(p => $"{Label(p)} — {p.Why}")) + ".");
        var unmatched = chosen.Where(p => p.Material is null).ToList();
        if (unmatched.Count > 0)
            notes.Add($"{unmatched.Count} part{(unmatched.Count == 1 ? " has" : "s have")} no material, so {(unmatched.Count == 1 ? "it is" : "they are")} drawn " +
                      $"and ignored by the solver until given one: {string.Join(", ", unmatched.Select(p => p.Name))}." +
                      (plan.NoMaterials is { } why ? " " + why : ""));
        foreach (var p in chosen.Where(p => p.Mixed))
            notes.Add($"'{p.Name}' has faces of several colours, so it was not matched by colour.");
        if (plan.Pmi > 0)
            notes.Add($"The file carries {plan.Pmi} dimension{(plan.Pmi == 1 ? "" : "s")}, tolerance{(plan.Pmi == 1 ? "" : "s")} or datum{(plan.Pmi == 1 ? "" : "s")}; none is imported.");
        return notes;
    }

    private static string Label(StepImportPart p) => p.ProductName.Length > 0 ? $"'{p.ProductName}' ({Where(p)})" : Where(p);

    /// <summary>A row's place in the file: <c>part 1/2</c>, or <c>part 1/2, solid 3</c>.</summary>
    private static string Where(StepImportPart p) => p.Solid is { } k ? $"part {p.Path}, solid {k}" : $"part {p.Path}";

    /// <summary>
    /// R-em3d68-4e — where the copy goes: <c>&lt;dir&gt;/&lt;name&gt;</c>, reused when a file of that name holds the same
    /// bytes, else <c>&lt;stem&gt;_2&lt;ext&gt;</c> and so on. Writes only when <paramref name="write"/> and the copy is new.
    /// </summary>
    public static (string Path, bool Created) CopyInto(string dir, string fileName, byte[] bytes, string hash, bool write)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName), ext = System.IO.Path.GetExtension(fileName);
        for (int n = 1; ; n++)
        {
            string path = System.IO.Path.Combine(dir, n == 1 ? fileName : $"{stem}_{n}{ext}");
            if (File.Exists(path))
            {
                if (string.Equals(C3dValidation.StepHash(path), hash, StringComparison.OrdinalIgnoreCase)) return (path, false);
                continue;
            }
            if (write)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, bytes);
            }
            return (path, true);
        }
    }

    /// <summary><c>SourcePath</c>: relative to the <c>.c3d</c> (with <c>/</c>) when the source is inside the document's
    /// workspace, absolute otherwise (R-em3d68-1a).</summary>
    public static string SourceSpelling(string sourcePath, string c3dPath)
    {
        string full = System.IO.Path.GetFullPath(sourcePath);
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(c3dPath))!;
        if (WorkspaceRootFinder.FindAncestorCws(dir) is { } cws)
        {
            string root = System.IO.Path.GetDirectoryName(cws)! + System.IO.Path.DirectorySeparatorChar;
            if (full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return System.IO.Path.GetRelativePath(dir, full).Replace('\\', '/');
        }
        return full;
    }

    /// <summary>Where a Step object's <c>SourcePath</c> points, or null when it names nothing.</summary>
    public static string? ResolveSource(C3dStep step, string c3dPath)
    {
        if (string.IsNullOrWhiteSpace(step.SourcePath)) return null;
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(c3dPath))!;
        return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(step.SourcePath) ? step.SourcePath : System.IO.Path.Combine(dir, step.SourcePath));
    }

    // ── Import: convert's whole verb (R-em3d68-7) ──────────────────────────────────────────────

    /// <summary>
    /// A NEW <c>.c3d</c> at <paramref name="c3dPath"/> from <paramref name="sourcePath"/>: the dialog's defaults (§3a
    /// auto-match; non-solids skipped), <paramref name="options"/> where the dialog would have asked, the file copied beside
    /// it, and the document saved. <c>circuitrf convert x.step -o y.c3d</c> is this call and argument parsing.
    /// </summary>
    /// <exception cref="StepImportException">The target exists, a flag names nothing, or the file holds no solid.</exception>
    /// <exception cref="GeometryKernelException">The kernel refused the file, crashed, or is absent.</exception>
    public static StepImportResult Import(string sourcePath, string c3dPath, StepImportOptions options, GeometryKernel kernel,
                                          TechnologyCache? cache = null, RunControl? control = null)
    {
        string full = System.IO.Path.GetFullPath(c3dPath);
        if (File.Exists(full) || Directory.Exists(full))
            throw new StepImportException(StepDiagnostics.TargetExists(c3dPath));
        var (plan, doc) = PlanImport(sourcePath, full, options, kernel, cache, control);
        string dir = System.IO.Path.GetDirectoryName(full)!;
        bool inCell = string.Equals(System.IO.Path.GetFileName(dir), CellFolder.ThreeDSubFolder, StringComparison.OrdinalIgnoreCase);
        if (!plan.Parts.Any(p => p.Closed))
            throw new StepImportException(StepDiagnostics.NoSolid(System.IO.Path.GetFileName(sourcePath),
                                                                  string.Join(" ", plan.Parts.Select(p => $"{Label(p)}: {p.Why}."))));

        Directory.CreateDirectory(dir);
        var result = Apply(plan, doc, full);
        C3dPersistence.SaveToFile(full, doc);
        var notes = result.Notes.ToList();
        if (!inCell) notes.Add("The document belongs to no cell (it is not in a cell's 3d folder); render, check and em all accept it.");
        return result with { Notes = notes };
    }

    /// <summary>
    /// What <see cref="Import"/> would create at <paramref name="c3dPath"/>, without writing: the new document (its
    /// technology resolved as the import resolves it) and the plan with <paramref name="options"/> applied. <c>convert
    /// --list-parts</c> prints this plan; <see cref="Import"/> applies it.
    /// </summary>
    /// <exception cref="StepImportException">A flag names nothing, or the technology cannot be read.</exception>
    public static (StepImportPlan Plan, C3dDocument Doc) PlanImport(string sourcePath, string c3dPath, StepImportOptions options,
                                                                    GeometryKernel kernel, TechnologyCache? cache = null, RunControl? control = null)
    {
        string full = System.IO.Path.GetFullPath(c3dPath);
        string dir = System.IO.Path.GetDirectoryName(full)!;
        bool inCell = string.Equals(System.IO.Path.GetFileName(dir), CellFolder.ThreeDSubFolder, StringComparison.OrdinalIgnoreCase);
        string cellDir = inCell ? System.IO.Path.GetDirectoryName(dir)! : dir;
        string? techRef = null;
        if (options.TechPath is { } tp)
        {
            string techFull = System.IO.Path.GetFullPath(tp);
            if (!File.Exists(techFull)) throw new StepImportException(StepDiagnostics.TechnologyMissing(tp));
            techRef = System.IO.Path.GetRelativePath(dir, techFull).Replace('\\', '/');
        }
        var (resolution, _) = TechnologyResolver.ResolveForDocument(techRef, full, null, cache ?? new TechnologyCache());
        if (techRef is not null && resolution.Tech is null)
            throw new StepImportException(StepDiagnostics.TechnologyUnreadable(options.TechPath!, string.Join(" ", resolution.Diagnostics)));

        var doc = CellCreate.NewThreeDView(cellDir, resolution.Tech);
        doc.TechRef = techRef;
        var plan = Read(sourcePath, kernel, resolution.Tech, doc, control);

        // R-em3d128-3a — `--part <path>` is every solid of that product, `--part <path>#<k>` one of them.
        foreach (string path in options.Parts)
            if (plan.Parts.All(p => !Selects(path, p)))
                throw new StepImportException(StepDiagnostics.NoSuchPart(path, string.Join(", ", PartKeys(plan))));
        if (options.Parts.Count > 0)
            foreach (var p in plan.Parts) p.Import = p.Closed && options.Parts.Any(k => Selects(k, p));
        if (options.Group is { } g) plan.Group = g;
        foreach (var (key, material) in options.Materials)
        {
            var hits = plan.Parts.Where(p => Selects(key, p) || string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(p.ProductName, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
                throw new StepImportException(StepDiagnostics.MaterialPartUnknown(key, string.Join(", ", plan.Parts.Select(p => $"{KeyOf(p)} ({p.Name})"))));
            string? known = plan.Materials.FirstOrDefault(m => string.Equals(m, material, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                throw new StepImportException(StepDiagnostics.MaterialUnknown(material,
                    plan.Materials.Count == 0 ? NoMaterialsReason : "Its materials: " + string.Join(", ", plan.Materials) + "."));
            foreach (var p in hits) { p.Material = known; p.Match = StepMatch.Chosen; }
        }
        return (plan, doc);
    }

    /// <summary>R-em3d128-3a — the CLI's spelling of a row: <c>&lt;path&gt;</c> for a whole product, <c>&lt;path&gt;#&lt;k&gt;</c> for
    /// one solid of it.</summary>
    public static string KeyOf(StepImportPart p) => p.Solid is { } k ? $"{p.Path}#{k.ToString(CultureInfo.InvariantCulture)}" : p.Path;

    /// <summary>Whether <c>--part</c> (or <c>--material</c>'s key) <paramref name="key"/> selects <paramref name="p"/>: its
    /// product's path selects every solid of it, <c>&lt;path&gt;#&lt;k&gt;</c> one.</summary>
    public static bool Selects(string key, StepImportPart p) => key == p.Path || key == KeyOf(p);

    private static IEnumerable<string> PartKeys(StepImportPlan plan)
        => plan.Parts.SelectMany(p => p.Solid == 1 ? new[] { p.Path, KeyOf(p) } : [KeyOf(p)]);

    // ── units (R-em3d68-2) ──────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> Plurals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inch"] = "inches", ["millimetre"] = "millimetres", ["micrometre"] = "micrometres", ["metre"] = "metres",
        ["centimetre"] = "centimetres", ["decimetre"] = "decimetres", ["nanometre"] = "nanometres", ["kilometre"] = "kilometres",
        ["foot"] = "feet", ["mil"] = "mils", ["thou"] = "thou",
    };

    /// <summary>The dialog's units line: <i>File is in inches; imported exactly</i>. A unit the table does not know is
    /// said with its size, which is what makes "exactly" checkable.</summary>
    public static string UnitsLine(IReadOnlyList<string> units, IReadOnlyList<double> microns)
    {
        string Say(int i) => Plurals.TryGetValue(units[i], out var p) ? p
            : string.Create(CultureInfo.InvariantCulture, $"'{units[i]}' ({(i < microns.Count ? microns[i] : 0):G10} µm each)");
        return units.Count switch
        {
            0 => "File states no length unit.",
            1 => $"File is in {Say(0)}; imported exactly.",
            _ => $"File uses {string.Join(" and ", Enumerable.Range(0, units.Count).Select(Say))}; each part imported exactly in its own.",
        };
    }

    // ── Reload from Source (R-em3d68-5) ─────────────────────────────────────────────────────────

    /// <summary>Every Step object in <paramref name="doc"/>, at any depth, naming <paramref name="file"/>.</summary>
    public static IReadOnlyList<C3dStep> NamingFile(C3dDocument doc, string file)
        => [.. doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dStep>()
                .Where(s => string.Equals(s.File, file, StringComparison.Ordinal))];

    /// <summary>Whether Reload from Source can run for <paramref name="step"/>: its source resolves to a readable file.</summary>
    public static bool CanReload(C3dStep step, string c3dPath) => ResolveSource(step, c3dPath) is { } p && File.Exists(p);

    /// <summary>
    /// R-em3d68-5 — reads the revised source of every object naming <paramref name="file"/> and decides, per object,
    /// whether it moves to the new bytes: its part must still exist, and every reference into its faces must land on
    /// exactly one face of the new file with the same fingerprint (surface kind, area, centroid, normal at the centroid)
    /// within the document's tolerance. Nothing is written.
    /// </summary>
    public static StepReloadPlan PlanReload(C3dDocument doc, string c3dPath, string file, GeometryKernel kernel, RunControl? control = null)
    {
        var objects = NamingFile(doc, file);
        if (objects.Count == 0) throw new StepImportException(StepDiagnostics.NotNamed(file));
        string? source = objects.Select(o => ResolveSource(o, c3dPath)).FirstOrDefault(p => p is not null && File.Exists(p));
        if (source is null)
            throw new StepImportException(StepDiagnostics.SourceUnreadable(file, objects[0].SourcePath ?? "not recorded"));
        byte[] bytes = File.ReadAllBytes(source);
        string hash = HashOf(bytes);
        if (objects.All(o => string.Equals(o.Hash, hash, StringComparison.OrdinalIgnoreCase)))
            return new StepReloadPlan(file, source, bytes, hash, true, [], [], [], []);

        var read = kernel.ImportStep(bytes, control);
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(c3dPath))!;
        double tolUm = Math.Max(1.0 / Math.Max(doc.DbuPerMicron, 1), 1e-6);
        var accepted = new List<C3dStep>();
        var refusals = new List<string>();
        var repoints = new List<StepRepoint>();
        var solidMoves = new List<StepSolidMove>();
        var byPart = read.Parts.ToDictionary(p => p.Path, StringComparer.Ordinal);
        // brief-em3d-127 R-em3d127-4: the current copy's own read, for the box and volume of each solid an object names —
        // read once, and only when some object names a Solid.
        GeometryKernelImport? current = null;
        foreach (var obj in objects)
        {
            if (!byPart.TryGetValue(obj.Part, out var np))
            {
                refusals.Add($"'{obj.Name}' is part {obj.Part} of '{file}', which the revised file no longer has.");
                continue;
            }
            int? solid = null;
            if (obj.Solid is { } k)
            {
                // A Solid index is not trusted across a revision: the revised part's ONE solid with the same box and volume.
                current ??= kernel.ImportStep(File.ReadAllBytes(System.IO.Path.Combine(dir, obj.File)), control);
                var was = current.Parts.FirstOrDefault(p => p.Path == obj.Part) is { } cp && k >= 1 && k <= cp.Solids.Count ? cp.Solids[k - 1] : null;
                string named = $"'{obj.Name}' is solid {k} of part {obj.Part} of '{file}'";
                if (was is null)
                {
                    refusals.Add($"{named}, which the file it was imported from never had.");
                    continue;
                }
                var hits = np.Solids.Where(n => SameSolid(was, n, tolUm)).ToList();
                if (hits.Count != 1)
                {
                    refusals.Add(hits.Count == 0
                        ? $"{named}, which the revised file no longer has: no solid of it has the same box and volume. A solid that " +
                          "changed shape keeps the old file, so whatever is on it can be checked by a person first."
                        : $"{named}, which matches {hits.Count} solids of the revised file; circuitRF does not choose one.");
                    continue;
                }
                if (!hits[0].Closed)
                {
                    refusals.Add($"{named}, which in the revised file is not a closed solid: {hits[0].Why}.");
                    continue;
                }
                solid = hits[0].Index;
                if (solid != k) solidMoves.Add(new StepSolidMove(obj.Name, obj.Part, k, hits[0].Index));
            }
            else if (!np.Closed)
            {
                refusals.Add($"'{obj.Name}' is part {obj.Part} of '{file}', which in the revised file is not a closed solid: {np.Why}.");
                continue;
            }
            var refs = References(doc, obj);
            if (refs.Count == 0) { accepted.Add(obj); continue; }
            var oldFaces = kernel.Faces(StepTree(obj, System.IO.Path.Combine(dir, obj.File), obj.Hash, obj.Solid, doc.DbuPerMicron));
            var newFaces = kernel.Faces(StepTree(obj, source, hash, solid, doc.DbuPerMicron));
            var map = new Dictionary<int, int>();
            bool ok = true;
            foreach (int n in refs.Select(r => r.Face).Distinct().Order())
            {
                if (n < 1 || n > oldFaces.Count)
                {
                    refusals.Add($"{refs.First(r => r.Face == n).What} is on {obj.Name}/face{n}, which the file it was imported from never had.");
                    ok = false;
                    continue;
                }
                var old = oldFaces[n - 1];
                var hits = Enumerable.Range(0, newFaces.Count).Where(k => SameFace(old, newFaces[k], tolUm)).ToList();
                if (hits.Count == 1) { map[n] = hits[0] + 1; continue; }
                string what = refs.First(r => r.Face == n).What;
                refusals.Add(hits.Count == 0
                    ? $"{what} is on {obj.Name}/face{n}, which the revised file no longer has."
                    : $"{what} is on {obj.Name}/face{n}, which matches {hits.Count} faces of the revised file; circuitRF does not choose one.");
                ok = false;
            }
            if (!ok) continue;
            accepted.Add(obj);
            foreach (var r in refs)
                if (map[r.Face] != r.Face) repoints.Add(new StepRepoint(obj.Name, r.What, r.Face, map[r.Face]));
        }
        var usedParts = objects.Select(o => o.Part).ToHashSet(StringComparer.Ordinal);
        var newParts = read.Parts.Where(p => !usedParts.Contains(p.Path)).ToList();
        // R-em3d127-4c: a solid of a part that objects address only by Solid, which no accepted object now lands on.
        var wholeParts = objects.Where(o => o.Solid is null).Select(o => o.Part).ToHashSet(StringComparer.Ordinal);
        var landed = accepted.Where(o => o.Solid is not null)
                             .Select(o => (o.Part, solidMoves.FirstOrDefault(m => m.Object == o.Name)?.To ?? o.Solid!.Value)).ToHashSet();
        var newSolids = read.Parts.Where(p => usedParts.Contains(p.Path) && !wholeParts.Contains(p.Path))
                                  .SelectMany(p => p.Solids.Where(x => !landed.Contains((p.Path, x.Index))).Select(x => new StepNewSolid(p.Path, x)))
                                  .ToList();
        var accepting = accepted.ToHashSet();
        return new StepReloadPlan(file, source, bytes, hash, false, accepted, refusals, repoints, newParts)
        {
            Maps = accepted.ToDictionary(o => o, o => repoints.Where(r => r.Object == o.Name).GroupBy(r => r.From).ToDictionary(g => g.Key, g => g.First().To)),
            SolidMoves = [.. solidMoves.Where(m => accepting.Any(o => o.Name == m.Object))],
            NewSolids = newSolids,
        };
    }

    /// <summary>
    /// R-em3d68-5e — an accepted reload: the revised file copied beside the old one (the copy rule of §4e — a changed file
    /// under the same name is <c>&lt;stem&gt;_2.step</c>), every accepted object moved to it with its new hash, and every
    /// reference re-pointed. Refused objects keep the old file. Returns the copy and whether it was written.
    /// </summary>
    public static (string Path, bool Created) ApplyReload(StepReloadPlan plan, C3dDocument doc, string c3dPath)
    {
        if (plan.NoChange || plan.Accepted.Count == 0) return ("", false);
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(c3dPath))!;
        var (copy, created) = CopyInto(dir, plan.File, plan.Bytes, plan.Hash, write: true);
        foreach (var obj in plan.Accepted)
        {
            if (plan.Maps.TryGetValue(obj, out var map) && map.Count > 0) Repoint(doc, obj, n => map.TryGetValue(n, out int m) ? (null, FaceName(m)) : null);
            if (plan.SolidMoves.FirstOrDefault(m => m.Object == obj.Name) is { } moved) obj.Solid = moved.To;
            obj.File = System.IO.Path.GetFileName(copy);
            obj.Hash = plan.Hash;
        }
        return (copy, created);
    }

    /// <summary>The kernel tree of a Step object's part in <paramref name="file"/>, at identity — the file's own frame, which
    /// is the frame the old and the revised faces are compared in.</summary>
    internal static GeometryKernelTree StepTree(C3dStep obj, string file, string hash, int? solid, int dbuPerMicron)
        => GeometryKernelTree.From(new C3dStep { Name = obj.Name, File = System.IO.Path.GetFullPath(file), Hash = hash, Part = obj.Part, Solid = solid },
                                   dbuPerMicron);

    /// <summary>brief-em3d-127 R-em3d127-4a — two solids are one when their boxes agree within the tolerance and their volumes
    /// within 1e-6 relative. A solid that changed shape matches nothing, on purpose (R-em3d127-4b).</summary>
    public static bool SameSolid(GeometryKernelImportSolid a, GeometryKernelImportSolid b, double tolUm)
    {
        if (a.BoxUm.Length != 6 || b.BoxUm.Length != 6) return false;
        for (int i = 0; i < 6; i++) if (Math.Abs(a.BoxUm[i] - b.BoxUm[i]) > tolUm) return false;
        return Math.Abs(a.VolumeUm3 - b.VolumeUm3) <= 1e-6 * Math.Max(Math.Abs(a.VolumeUm3), Math.Abs(b.VolumeUm3));
    }

    /// <summary>Two faces are one when the kind is the same and the area, centroid and normal agree within the tolerance.</summary>
    public static bool SameFace(GeometryKernelFace a, GeometryKernelFace b, double tolUm)
    {
        if (a.Kind != b.Kind || a.Centroid.Length != 3 || b.Centroid.Length != 3) return false;
        if (Math.Abs(a.Area - b.Area) > Math.Max(1e-6 * Math.Max(a.Area, b.Area), tolUm * tolUm)) return false;
        double d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a.Centroid[i] - b.Centroid[i]) * (a.Centroid[i] - b.Centroid[i])));
        if (d > tolUm) return false;
        if (a.Normal.Length == 3 && b.Normal.Length == 3)
        {
            double dot = a.Normal[0] * b.Normal[0] + a.Normal[1] * b.Normal[1] + a.Normal[2] * b.Normal[2];
            bool none = a.Normal.All(v => v == 0) && b.Normal.All(v => v == 0);
            if (!none && dot < 1 - 1e-6) return false;
        }
        return true;
    }

    // ── references into a Step part's faces ─────────────────────────────────────────────────────

    private static readonly Regex FaceField = new(@"^(?<p>.*?)face(?<n>[0-9]+)(?<s>([#.][0-9]+)?)$", RegexOptions.CultureInvariant);

    /// <summary>One reference into <c>face&lt;n&gt;</c> of a Step object: the face, in words for a refusal, and how to write it
    /// onto another face — of the same object (owner null), or of <c>Owner</c>, a top-level object taking it over (brief 129's
    /// split). Null for a fillet's or chamfer's edge, which <see cref="Repoint"/> rewrites whole.</summary>
    internal sealed record FaceRef(int Face, string What, Action<C3dObject?, string>? Move);

    /// <summary>Where a Step object's faces appear, and under what prefix: each (owner name, prefix) its faces are named by.</summary>
    private static List<(string Owner, string Prefix)> Owners(C3dDocument doc, C3dStep step)
    {
        var list = new List<(string, string)>();
        void Walk(C3dObject o, string owner, string prefix)
        {
            if (ReferenceEquals(o, step)) { list.Add((owner, prefix)); return; }
            switch (o)
            {
                case C3dBoolean b:
                    if (b.Blank is { } blank) Walk(blank, owner, prefix);
                    foreach (var t in b.Tools)
                    {
                        Walk(t, owner, prefix + t.Name + ":");
                        Walk(t, t.Name, "");   // a disabled boolean's Tool, named by its own name (R-em3d64-2f)
                    }
                    break;
                case C3dFillet { Target: { } ft }: Walk(ft, owner, prefix); break;
                case C3dChamfer { Target: { } ct }: Walk(ct, owner, prefix); break;
                case C3dSplit { Target: { } st }: Walk(st, owner, prefix); break;
            }
        }
        foreach (var top in doc.Objects) Walk(top, top.Name, "");
        return list;
    }

    /// <summary>The fillets and chamfers whose target holds <paramref name="step"/>, each with the prefix its faces carry there.</summary>
    private static List<(C3dObject Op, string Prefix)> EdgeOwners(C3dDocument doc, C3dStep step)
    {
        var list = new List<(C3dObject, string)>();
        string? PrefixIn(C3dObject o, string prefix)
        {
            if (ReferenceEquals(o, step)) return prefix;
            foreach (var (_, child) in C3dOperands.Of(o))
            {
                string p = o is C3dBoolean b && !ReferenceEquals(child, b.Blank) ? prefix + child.Name + ":" : prefix;
                if (PrefixIn(child, p) is { } hit) return hit;
            }
            return null;
        }
        foreach (var top in doc.Objects)
            foreach (var o in C3dOperands.SelfAndDescendants(top))
            {
                var target = (o as C3dFillet)?.Target ?? (o as C3dChamfer)?.Target;
                if (target is not null && PrefixIn(target, "") is { } p) list.Add((o, p));
            }
        return list;
    }

    /// <summary>
    /// Every reference into <paramref name="step"/>'s faces, wherever the document spells one (brief-em3d-129 audit): an EM face
    /// boundary, a fillet's or chamfer's edge, a face image on the object holding it, a probe's face and its spot, a field
    /// plot's face, and an embedded thermal setup's boundary. A port names none — it is a rectangle placed by geometry, and its
    /// conductors are objects — and neither does a mesh region or an effective block, which are boxes.
    /// </summary>
    internal static List<FaceRef> References(C3dDocument doc, C3dStep step)
    {
        var refs = new List<FaceRef>();
        var owners = Owners(doc, step);
        foreach (var fb in doc.FaceBoundaries)
            foreach (var (owner, prefix) in owners)
                if (string.Equals(fb.Object, owner, StringComparison.OrdinalIgnoreCase) && FaceNumber(fb.Face, prefix) is { } n)
                {
                    string p = prefix;
                    refs.Add(new FaceRef(n, $"the {fb.Kind} boundary on '{fb.Object}'", (to, m) =>
                    {
                        if (to is not null) fb.Object = to.Name;
                        fb.Face = Renamed(fb.Face, p, m);
                    }));
                }
        foreach (var (op, prefix) in EdgeOwners(doc, step))
        {
            var edges = op is C3dFillet f ? f.Edges : ((C3dChamfer)op).Edges;
            string word = op is C3dFillet ? "fillet" : "chamfer";
            string name = op.Name.Length > 0 ? $"'{op.Name}'" : "(inner)";
            foreach (string e in edges)
                foreach (string field in e.Split('|').Take(2))
                    if (FaceNumber(field, prefix) is { } n) refs.Add(new FaceRef(n, $"the {word} {name}'s edge {e}", null));
        }
        foreach (var (owner, prefix) in owners)
            if (Holder(doc, owner) is { FaceImages: { } images } holder)
                foreach (var img in images.ToList())
                    if (FaceNumber(img.Face, prefix) is { } n)
                    {
                        string p = prefix;
                        refs.Add(new FaceRef(n, $"the image on '{owner}'", (to, m) =>
                        {
                            img.Face = Renamed(img.Face, p, m);
                            if (to is null || ReferenceEquals(to, holder)) return;
                            holder.FaceImages!.Remove(img);
                            if (holder.FaceImages.Count == 0) holder.FaceImages = null;
                            (to.FaceImages ??= []).Add(img);
                        }));
                    }
        // "object/face" spellings: a probe's faces and spot, a field plot's faces, a thermal setup's boundaries.
        void Spelled(string spelled, string what, Action<string> write)
        {
            int slash = spelled.LastIndexOf('/');
            if (slash <= 0) return;
            string obj = spelled[..slash], face = spelled[(slash + 1)..];
            foreach (var (owner, prefix) in owners)
                if (string.Equals(obj, owner, StringComparison.OrdinalIgnoreCase) && FaceNumber(face, prefix) is { } n)
                {
                    string p = prefix;
                    refs.Add(new FaceRef(n, what, (to, m) => write($"{to?.Name ?? obj}/{Renamed(face, p, m)}")));
                }
        }
        foreach (var probe in doc.Probes)
        {
            if (probe.Face is { } faces)
                for (int i = 0; i < faces.Count; i++)
                {
                    int k = i;
                    Spelled(faces[i], $"the probe '{probe.Name}'", v => faces[k] = v);
                }
            if (probe.Spot is { } spot) Spelled(spot.Face, $"the probe '{probe.Name}''s spot", v => spot.Face = v);
        }
        foreach (var plot in doc.FieldPlots)
            foreach (var face in plot.Faces)
                Spelled(face.Face, $"the field plot '{plot.Name}'", v => face.Face = v);
        for (int i = 0; i < doc.Setups.Count; i++)
        {
            if (doc.Setups[i] is not { ValueKind: JsonValueKind.Object } setup) continue;
            string setupName = setup.TryGetProperty("Name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()! : $"#{i + 1}";
            var faces = ThermalBoundaryFaces(setup);
            for (int b = 0; b < faces.Count; b++)
            {
                var (si, bi) = (i, b);
                Spelled(faces[b], $"the thermal boundary of setup '{setupName}'",
                        v => doc.Setups[si] = WithThermalBoundaryFace(doc.Setups[si], bi, v));
            }
        }
        return refs;
    }

    /// <summary>The object the name <paramref name="owner"/> names, at any depth — the one a face image is stored on.</summary>
    private static C3dObject? Holder(C3dDocument doc, string owner)
        => doc.Objects.SelectMany(C3dOperands.SelfAndDescendants)
                      .FirstOrDefault(o => string.Equals(o.Name, owner, StringComparison.OrdinalIgnoreCase));

    /// <summary>An embedded setup's thermal boundaries' <c>Face</c> strings, in order (a boundary with none is "").</summary>
    private static List<string> ThermalBoundaryFaces(JsonElement setup)
    {
        var list = new List<string>();
        if (Property(setup, "Thermal") is { ValueKind: JsonValueKind.Object } thermal
            && Property(thermal, "Boundaries") is { ValueKind: JsonValueKind.Array } boundaries)
            foreach (var b in boundaries.EnumerateArray())
                list.Add(b.ValueKind == JsonValueKind.Object && Property(b, "Face") is { ValueKind: JsonValueKind.String } f ? f.GetString()! : "");
        return list;
    }

    /// <summary><paramref name="setup"/> with its <paramref name="index"/>-th thermal boundary's Face set to <paramref name="face"/>.</summary>
    private static JsonElement WithThermalBoundaryFace(JsonElement setup, int index, string face)
    {
        var root = JsonNode.Parse(setup.GetRawText())!.AsObject();
        static JsonNode? Child(JsonObject o, string key) => o.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        var boundary = ((Child(root, "Thermal") as JsonObject) is { } t ? Child(t, "Boundaries") as JsonArray : null)?[index] as JsonObject;
        if (boundary is null) return setup;
        string key = boundary.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, "Face", StringComparison.OrdinalIgnoreCase)) ?? "Face";
        boundary[key] = face;
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonElement? Property(JsonElement o, string name)
    {
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static int? FaceNumber(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var m = FaceField.Match(name[prefix.Length..]);
        return m.Success && m.Groups["p"].Value.Length == 0 && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static string Renamed(string name, string prefix, string to)
    {
        if (FaceNumber(name, prefix) is null) return name;
        var m = FaceField.Match(name[prefix.Length..]);
        return prefix + to + m.Groups["s"].Value;
    }

    /// <summary><c>face&lt;n&gt;</c>: how a Step object's n-th face is named.</summary>
    internal static string FaceName(int n) => "face" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes every reference into <paramref name="step"/>'s faces onto the face <paramref name="to"/> gives for its number, BY
    /// NAME — on the same object (a null owner: a reload), or on another top-level object (a split, or brief 130's replacement,
    /// whose faces are <c>xmin</c>, <c>side3</c> …). A number <paramref name="to"/> has no answer for is left as it is. An
    /// edge's two faces stay on the operation that names them.
    /// </summary>
    internal static void Repoint(C3dDocument doc, C3dStep step, Func<int, (C3dObject? Owner, string Face)?> to)
    {
        var edgeOwners = EdgeOwners(doc, step);
        foreach (var r in References(doc, step))
            if (r.Move is { } move && to(r.Face) is { } t) move(t.Owner, t.Face);
        foreach (var (op, prefix) in edgeOwners)
        {
            var edges = op is C3dFillet f ? f.Edges : ((C3dChamfer)op).Edges;
            for (int i = 0; i < edges.Count; i++)
            {
                var fields = edges[i].Split('|');
                for (int k = 0; k < Math.Min(2, fields.Length); k++)
                    if (FaceNumber(fields[k], prefix) is { } n && to(n) is { } t) fields[k] = Renamed(fields[k], prefix, t.Face);
                // An edge's name sorts its two faces (overview §1g); a re-pointed face can change which comes first.
                if (fields.Length >= 2 && string.CompareOrdinal(fields[0], fields[1]) > 0) (fields[0], fields[1]) = (fields[1], fields[0]);
                edges[i] = string.Join('|', fields);
            }
        }
    }
}

/// <summary>One reference Reload from Source re-points: <paramref name="What"/> on <paramref name="Object"/>, from
/// <c>face&lt;From&gt;</c> to <c>face&lt;To&gt;</c>.</summary>
public sealed record StepRepoint(string Object, string What, int From, int To)
{
    public override string ToString() => $"{What}: {Object}/face{From} is face{To} in the revised file.";
}

/// <summary>brief-em3d-127 R-em3d127-4a — an object whose solid is numbered <paramref name="To"/> in the revised file, matched
/// by box and volume.</summary>
public sealed record StepSolidMove(string Object, string Part, int From, int To)
{
    public override string ToString() => $"'{Object}' is solid {From} of part {Part}, which is solid {To} in the revised file.";
}

/// <summary>R-em3d127-4c — a solid of a revised part that objects address solid by solid, which no object names: offered
/// like a new part, with its index.</summary>
public sealed record StepNewSolid(string Part, GeometryKernelImportSolid Solid);

/// <summary>What <see cref="StepImport.PlanReload"/> decided.</summary>
/// <param name="NoChange">The source's bytes are the ones every object already names: nothing to do (R-em3d68-5b).</param>
/// <param name="Accepted">Objects that move to the revised file.</param>
/// <param name="Refusals">Why each other object does not — the reload is refused for it (R-em3d68-5c/-5d).</param>
/// <param name="Repoints">Every reference whose face number changes.</param>
/// <param name="NewParts">Parts the revised file has that no object names: offered, unchecked (R-em3d68-5d).</param>
public sealed record StepReloadPlan(string File, string SourcePath, byte[] Bytes, string Hash, bool NoChange,
                                    IReadOnlyList<C3dStep> Accepted, IReadOnlyList<string> Refusals,
                                    IReadOnlyList<StepRepoint> Repoints, IReadOnlyList<GeometryKernelImportPart> NewParts)
{
    /// <summary>Per accepted object, old face number → new.</summary>
    public IReadOnlyDictionary<C3dStep, Dictionary<int, int>> Maps { get; init; } = new Dictionary<C3dStep, Dictionary<int, int>>();

    /// <summary>brief-em3d-127 — every accepted object whose <c>Solid</c> is numbered differently in the revised file.</summary>
    public IReadOnlyList<StepSolidMove> SolidMoves { get; init; } = [];

    /// <summary>brief-em3d-127 R-em3d127-4c — solids of a part addressed solid by solid that no object lands on.</summary>
    public IReadOnlyList<StepNewSolid> NewSolids { get; init; } = [];
}
