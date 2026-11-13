// HZN-9 R-hzn9-1 — a design held in a Hertzon project (.hzn), placed as an instance in a .c3d.
//
// The instance names the FILE like any cell reference (relative to the .c3d's folder, or ws://alias/…) and the DESIGN in
// the new `Design` key, with View = Hertzon. The elaborator asks this file for the placed design's document: the
// translation HznToC3d makes (brief 2), cached against the file's modification time and content hash, so an edit made in
// Hertzon or by brief 5 shows the next time the parent elaborates, and an unchanged file is translated once however many
// instances place it.
//
// Three rules live here and nowhere else:
//   * OVERRIDES. The design's variables are the placed document's overridable names, each spelled as the .hzn spells it.
//     A project variable is overridable when the design reaches it (the translation holds it under a reserved prefix, and
//     the override is still written with the name the .hzn gives it). A name the design does not have is refused, listing
//     the ones it does. Overrides are evaluated in the PARENT's scope by the resolver, as for any instance.
//   * MATERIALS. The placed design's materials are matched by name against the technology of the document that places it:
//     identical values -> shared; missing or different -> a refusal listing every one. Placing never writes a technology.
//     A conductor (pec, copper, anything at 1e6 S/m or more) is exempt: a difference is a note, never a refusal.
//   * REFUSAL. A design that would be refused when opened is refused as an instance with brief 2's list, naming the
//     instance. Nothing is placed from half a design.

using System.Globalization;
using System.Security.Cryptography;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>One design of a <c>.hzn</c>, translated for placing: its document, the in-memory technology, the synthetic
/// "cell" whose parameters are the design's variables, and the refusal when it cannot be placed.</summary>
public sealed record HznInstanceChild(string Path, string Design, string Stamp, C3dDocument Document, TechResolution Tech, C3dCell Cell,
                                      IReadOnlyDictionary<string, string> Names, IReadOnlyList<string> Notes, string? Refusal,
                                      IReadOnlySet<string>? Quantities = null)
{
    /// <summary>The placed document's variables (by its own names) that carry a unit, as <c>3mm</c> does: an override of
    /// one must state its unit, and an override of any other must not.</summary>
    public IReadOnlySet<string> Quantities { get; } = Quantities ?? new HashSet<string>();

    public bool Ok => Refusal is null;
}

/// <summary>The translations of placed <c>.hzn</c> designs, kept while the file is unchanged. One per elaborator.</summary>
public sealed class HznInstanceCache
{
    private sealed record Entry(DateTime Modified, long Length, byte[] Hash, HznFile? File, string? FileRefusal,
                                Dictionary<string, HznInstanceChild> Designs);

    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (C3dDocument Doc, C3dResolution Res)> _resolved = new(StringComparer.Ordinal);

    /// <summary>Designs translated because the cache missed.</summary>
    public long Translations { get; private set; }

    /// <summary>Overrides resolutions made because no earlier instance had the same values.</summary>
    public long Resolutions { get; private set; }

    /// <summary>
    /// <paramref name="design"/> of the project at <paramref name="path"/>, translated (or the cached translation when the
    /// file's modification time and length are unchanged, or its content hash is). Never throws: a missing file, a file
    /// that is not a Hertzon project, a missing design and a design that cannot be represented all come back as
    /// <see cref="HznInstanceChild.Refusal"/>.
    /// </summary>
    public HznInstanceChild Load(string path, string design, GeometryKernel? kernel = null)
    {
        string full = System.IO.Path.GetFullPath(path);
        FileInfo info;
        try { info = new FileInfo(full); }
        catch (Exception e) { return Failed(full, design, "", $"cannot be read: {e.Message}"); }
        if (!info.Exists) return Failed(full, design, "", "does not exist");

        _files.TryGetValue(full, out var entry);
        if (entry is null || entry.Modified != info.LastWriteTimeUtc || entry.Length != info.Length)
        {
            byte[] hash;
            try { hash = SHA256.HashData(File.ReadAllBytes(full)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return Failed(full, design, "", $"cannot be read: {e.Message}"); }
            if (entry is not null && entry.Hash.AsSpan().SequenceEqual(hash))
                _files[full] = entry = entry with { Modified = info.LastWriteTimeUtc, Length = info.Length };
            else
            {
                var read = HznFile.Read(full);
                entry = _files[full] = new Entry(info.LastWriteTimeUtc, info.Length, hash, read.File, read.Refusal?.Render(), []);
                foreach (var key in _resolved.Keys.Where(k => k.StartsWith(full + "|", StringComparison.Ordinal)).ToList()) _resolved.Remove(key);
            }
        }

        string stamp = Convert.ToHexString(entry.Hash)[..16];
        if (entry.File is null) return Failed(full, design, stamp, entry.FileRefusal ?? "cannot be read");
        if (entry.Designs.TryGetValue(design, out var hit)) return hit;
        Translations++;
        return entry.Designs[design] = Translate(entry.File, full, design, stamp, kernel);
    }

    private static HznInstanceChild Failed(string path, string design, string stamp, string why) =>
        new(path, design, stamp, new C3dDocument(), new TechResolution(null, null, TechResolutionSource.None, []), C3dCell.None,
            new Dictionary<string, string>(), [], why);

    private static HznInstanceChild Translate(HznFile file, string path, string design, string stamp, GeometryKernel? kernel)
    {
        var t = HznToC3d.Translate(file.Project, design, kernel);
        if (!t.Ok || t.Document is null || t.Technology is null)
        {
            var d = t.Refusal;
            return Failed(path, design, stamp, d is null ? "cannot be opened" : d.Render());
        }
        // The in-memory technology answers to the file and design in notes and in a material-conflict sentence.
        t.Technology.Name = $"{System.IO.Path.GetFileName(path)}:{design}";
        var tech = new TechResolution(t.Technology, null, TechResolutionSource.None, []);

        // The overridable names: every variable of the translated document, spelled as the .hzn spells it.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in t.Provenance.Variables) names[v.HznName] = v.C3dName;
        // A variable that carries a unit is bound as a quantity (the base unit marks it), so an override's value keeps its
        // meaning; any other stays a plain number.
        var scratch = C3dPersistence.Deserialize(C3dPersistence.Serialize(t.Document));
        var scratchResolution = C3dResolver.Resolve(scratch, C3dCell.None);
        var quantities = new HashSet<string>(
            t.Document.Variables.Where(v => scratchResolution.Ok && scratchResolution.SkipsSiteUnit(v.Expression)).Select(v => v.Name),
            StringComparer.Ordinal);
        var parameters = t.Document.Variables
            .Select(v => new CcellParameter
            {
                Name = v.Name, DefaultExpression = v.Expression, Unit = quantities.Contains(v.Name) ? C3dResolver.BaseUnit : v.Unit ?? "",
            }).ToList();
        return new HznInstanceChild(path, design, stamp, t.Document, tech, new C3dCell(null, null, parameters), names, t.Notes, null, quantities);
    }

    /// <summary>The child resolved under one set of override VALUES: a copy of the translated document with its numbers
    /// written, shared by every instance whose values are equal however they were spelled.</summary>
    public (C3dDocument Doc, C3dResolution Res) Resolved(HznInstanceChild child, IReadOnlyDictionary<string, C3dOverride> overrides)
    {
        string key = child.Path + "|" + child.Stamp + "|" + child.Design + "|" +
                     string.Join(";", overrides.OrderBy(o => o.Key, StringComparer.Ordinal)
                                               .Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.Key}={o.Value.Value}:{o.Value.Unit}")));
        if (_resolved.TryGetValue(key, out var hit)) return hit;
        Resolutions++;
        var copy = C3dPersistence.Deserialize(C3dPersistence.Serialize(child.Document));
        return _resolved[key] = (copy, C3dResolver.Resolve(copy, child.Cell, overrides));
    }
}

/// <summary>The rules around a placed <c>.hzn</c> design that do not need the elaborator's state.</summary>
public static class HznInstances
{
    /// <summary>The <c>.hzn</c> file <paramref name="cellRef"/> names from <paramref name="baseDir"/>, spelled as any cell
    /// reference is (relative, or <c>ws://alias/…</c>); null when the reference cannot be worked out at all.</summary>
    public static string? ResolveFile(string? cellRef, string? baseDir)
        => ExternalCellRef.ResolveCellDir(cellRef, baseDir, out _, out _) is { } p ? System.IO.Path.GetFullPath(p) : null;

    /// <summary>
    /// The Hertzon projects under <paramref name="root"/> with the 3D designs each holds, for a picker: bounded by depth and
    /// by file count (a project that cannot be read is left out, not an error), dot-folders and links not followed.
    /// </summary>
    public static List<(string Path, IReadOnlyList<string> Designs)> Discover(string root, int maxDepth = 6, int maxFiles = 100)
    {
        var found = new List<(string, IReadOnlyList<string>)>();
        if (!Directory.Exists(root)) return found;
        Walk(root, 0);
        return found;

        void Walk(string dir, int depth)
        {
            if (found.Count >= maxFiles) return;
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir, "*.hzn").Order(StringComparer.OrdinalIgnoreCase).ToList(); dirs = Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var f in files)
            {
                if (found.Count >= maxFiles) return;
                if (!IsHertzonFile(f) || HznFile.Read(f).File is not { } read) continue;
                var designs = HznToC3d.ThreeDDesigns(read.Project);
                if (designs.Count > 0) found.Add((System.IO.Path.GetFullPath(f), designs));
            }
            if (depth >= maxDepth) return;
            foreach (var d in dirs)
            {
                if (System.IO.Path.GetFileName(d).StartsWith('.')) continue;
                try { if (new DirectoryInfo(d).LinkTarget is not null) continue; } catch (IOException) { continue; }
                Walk(d, depth + 1);
            }
        }
    }

    /// <summary>True for the extension a placed Hertzon project has.</summary>
    public static bool IsHertzonFile(string? path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".hzn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="inst"/>'s overrides keyed by the placed document's own variable names, or a copy of the instance
    /// that <c>OverridesFor</c> can evaluate. An override naming something the design does not have goes to
    /// <paramref name="errors"/>, listing what it does have.
    /// </summary>
    public static C3dInstance? MapOverrides(C3dInstance inst, string instPath, HznInstanceChild child, Func<string, bool> carriesUnit,
                                            List<string> errors)
    {
        var mapped = new Dictionary<string, C3dExpr>(StringComparer.Ordinal);
        bool ok = true;
        foreach (var (name, expr) in inst.Params ?? [])
        {
            if (child.Names.TryGetValue(name, out string? own))
            {
                // A quantity takes a quantity and a plain number a plain number: a bare 4 for a variable that is 3mm in the
                // design would otherwise be four metres, silently.
                bool quantity = child.Quantities.Contains(own), states = expr.Unit is { Length: > 0 } || carriesUnit(expr.Expr);
                if (quantity != states)
                {
                    ok = false;
                    errors.Add(quantity
                        ? $"Instance '{instPath}' overrides '{name}' with '{expr.Expr}', which states no unit; '{name}' is a quantity in " +
                          $"design '{child.Design}', so write its unit (for example 4mm)."
                        : $"Instance '{instPath}' overrides '{name}' with '{expr.Expr}', which states a unit; '{name}' is a plain number in " +
                          $"design '{child.Design}'.");
                    continue;
                }
                mapped[own] = expr;
                continue;
            }
            ok = false;
            errors.Add($"Instance '{instPath}' overrides '{name}', which is not a variable of design '{child.Design}' in " +
                       $"'{System.IO.Path.GetFileName(child.Path)}' (" +
                       (child.Names.Count == 0 ? "it has none" : "its variables: " + string.Join(", ", child.Names.Keys.Order(StringComparer.Ordinal))) + ").");
        }
        return ok ? new C3dInstance { Name = inst.Name, CellRef = inst.CellRef, View = inst.View, Design = inst.Design, Params = mapped } : null;
    }

    /// <summary>
    /// The non-conductor materials of the placed design that <paramref name="parent"/> (the technology of the document placing
    /// it) does not state identically, one sentence each; empty when every one is shared. Free space (Air, Vacuum) is the
    /// built-in every technology holds. A conductor (<see cref="IsConductor"/>) is never a conflict: the placed design is
    /// elaborated against its own materials, so a conductor the parent lacks or states differently changes nothing about the
    /// placement and is only reported (<see cref="ConductorDifferences"/>).
    /// </summary>
    public static IReadOnlyList<string> MaterialConflicts(Technology placed, Technology? parent, double tempC)
    {
        var found = new List<string>();
        foreach (var m in placed.ResolvedMaterials)
        {
            if (IsConductor(m, tempC)) continue;
            var mine = Values(m, tempC);
            var theirs = ThreeD.C3dProblemAssembly.ObjectMaterial(parent, m.Name);
            if (theirs is null) { found.Add($"'{m.Name}' ({Describe(mine)}) is not defined"); continue; }
            var other = Values(theirs, tempC);
            if (!Same(mine, other)) found.Add($"'{m.Name}' is {Describe(mine)} in the placed design and {Describe(other)} here");
        }
        return found;
    }

    /// <summary>The placed design's conductors that <paramref name="parent"/> lacks or states differently, one note each. They
    /// do not stop the placement: the placed design keeps its own values.</summary>
    public static IReadOnlyList<string> ConductorDifferences(Technology placed, Technology? parent, double tempC)
    {
        var found = new List<string>();
        foreach (var m in placed.ResolvedMaterials)
        {
            if (!IsConductor(m, tempC)) continue;
            var mine = Values(m, tempC);
            var theirs = ThreeD.C3dProblemAssembly.ObjectMaterial(parent, m.Name);
            if (theirs is null) { found.Add($"'{m.Name}' ({Describe(mine)}) is not defined here; the placed design keeps its own"); continue; }
            var other = Values(theirs, tempC);
            if (!Same(mine, other)) found.Add($"'{m.Name}' is {Describe(mine)} in the placed design and {Describe(other)} here; the placed design keeps its own");
        }
        return found;
    }

    /// <summary>A perfect conductor, copper, or any material conducting at least 1e6 S/m (the rule the translation uses
    /// for a conductor).</summary>
    public static bool IsConductor(TechMaterial m, double tempC) =>
        m.Name.Equals("pec", StringComparison.OrdinalIgnoreCase) || m.Name.Equals("copper", StringComparison.OrdinalIgnoreCase) ||
        Values(m, tempC).SigmaSm >= 1e6;

    private static Em3dMaterial Values(TechMaterial m, double tempC) => C3dElaborator.MaterialValues(m, tempC);

    private static bool Close(double a, double b) => Math.Abs(a - b) <= 1e-12 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    private static bool Same(Em3dMaterial a, Em3dMaterial b) =>
        Close(a.Epsr, b.Epsr) && Close(a.TanD, b.TanD) && Close(a.Mur, b.Mur) && Close(a.SigmaSm, b.SigmaSm) &&
        (a.EpsrTensor ?? []).Count == (b.EpsrTensor ?? []).Count &&
        (a.EpsrTensor ?? []).Zip(b.EpsrTensor ?? [], Close).All(x => x);

    private static string Describe(Em3dMaterial m) => string.Create(CultureInfo.InvariantCulture,
        $"εr {m.Epsr:G6}, tanδ {m.TanD:G6}, μr {m.Mur:G6}, σ {m.SigmaSm:G6} S/m");

    /// <summary>The refusal sentence for a design that cannot be placed: the instance, the design and file, then the reason
    /// (brief 2's list, or the missing file or design).</summary>
    public static string Refused(string instPath, HznInstanceChild child) =>
        $"Instance '{instPath}' places design '{child.Design}' of '{child.Path}', which cannot be placed: {child.Refusal}";

    /// <summary>The materials refusal, naming the instance and the technology the placed materials were matched against.</summary>
    public static string MaterialsRefused(string instPath, HznInstanceChild child, string techName, IReadOnlyList<string> conflicts) =>
        $"Instance '{instPath}' places design '{child.Design}' of '{System.IO.Path.GetFileName(child.Path)}', whose materials do not match " +
        $"technology '{techName}': {string.Join("; ", conflicts)}. Placing never changes a technology; define or correct " +
        $"{(conflicts.Count == 1 ? "it" : "them")} there.";
}
