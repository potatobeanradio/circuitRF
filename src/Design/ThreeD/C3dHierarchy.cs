// brief-em3d-48 — hierarchy in a .c3d, headless: what the 3D editor's Place, Swap View, Flatten, Group into Cell and
// New 3D View from Layout DO, with no display. The editor (src/Ui/ThreeD) only asks, confirms and pushes one undo
// entry; every rule lives here, so `check`, a test and the GUI cannot disagree about one.
//
// CYCLES ARE BY (CELL, VIEW), NOT BY CELL (R-em3d48-6a, §7). A 3D view may hold its own cell's LAYOUT — the views
// differ, and a layout never contains a 3D view — but never its own cell's 3D view, directly or through other 3D
// views. The check walks the child's 3D instances at the pick, before anything is written; the elaborator's own
// cycle refusal stays as the backstop for a file edited by hand.
//
// FLATTEN (R-em3d48-5, L3c's rules carried to 3D). One level of a 3D child is EXACT: each child object keeps its
// primitive and its own frame, and only its placement is composed with the instance's (C3dPlacement.Then, the
// canonical form brief 46 wrote); the child's own instances come up a level the same way. A LAYOUT child has no
// objects to keep — it becomes its ELABORATED solids (prisms, sheets, cylinders, and wires as polyhedra), which is
// why the editor asks first and states the count. A flattened object's material must mean the same in this
// document's technology as it did in its own: a material the technology lacks, or holds with other values, is a
// refusal naming it, never a silent swap of conductivity.
//
// GROUP INTO CELL moves the chosen objects into a new cell's 3D view and puts one instance of it where they were.
// The geometry does not move (R-L3c-5): every grouped placement is moved by −anchor and the instance sits AT the
// anchor, which is an integer translation, so it is exact. The folder is created through CellFolder.CreateCellFolder
// and is never deleted by an undo (R-L3c-6).

using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What flattening produced: the objects to append, the instances that take the flattened one's place, and
/// why not.</summary>
public sealed record C3dFlattenResult(IReadOnlyList<C3dObject> Objects, IReadOnlyList<C3dInstance> Instances,
                                      string? Refusal, IReadOnlyList<string> Notes)
{
    public static C3dFlattenResult Refuse(string why) => new([], [], why, []);

    /// <summary>A refusal for materials this view's technology lacks: the materials that, added to it, would let the flatten
    /// go ahead — each with the values its objects carry where they came from, so adding it changes nothing the solver sees.
    /// Empty when the refusal has another cause, or when one of the materials could not be stated (its values unknown, or
    /// the technology holding that name with other values).</summary>
    public IReadOnlyList<TechMaterial> MissingMaterials { get; init; } = [];

    /// <summary>The path of the group the flatten gathered its parts in, named after the instance; null when it made none (a
    /// single part, or a refusal).</summary>
    public string? Group { get; init; }
}

/// <summary>A New 3D View from Layout: the document, and the ports that could not be carried across (with why).</summary>
public sealed record C3dFromLayout(C3dDocument? Document, string? Refusal, IReadOnlyList<string> SetupsCopied,
                                   IReadOnlyList<string> PortsLeftOut)
{
    /// <summary>brief-port-width-planar-and-c3d R-pw1-6 — set when no 3D <c>.cem</c> names the layout and its port labels
    /// were translated with a default driven setup instead: the note that says so.</summary>
    public string? PortsNote { get; init; }

    /// <summary>The ports moved in from their line's end to where a pulled-back return plane begins, each in a sentence.</summary>
    public IReadOnlyList<string> PortsMoved { get; init; } = [];

    /// <summary>brief-sdl-8 D4 — the VARs added because the layout was made from the cell's schematic, each linked by name to
    /// a cell parameter or a schematic VAR: what drives the layout drives any 3D-only body that uses one of them.</summary>
    public IReadOnlyList<string> LinkedVariables { get; init; } = [];
}

/// <summary>One placed layout's port label, translated into the <c>.c3d</c>'s frame: the instance, the label's own port
/// number, and the record it makes (its <see cref="C3dPort.Number"/> the label's number until a clash renumbers it).</summary>
public sealed record C3dLayoutPort(string Instance, int LayoutNumber, C3dPort Port);

/// <summary>
/// brief-port-width-planar-and-c3d R-pw1-4/5 — a <c>.c3d</c>'s ports as its instanced layouts state them now: every
/// <c>Layout</c> instance's port labels translated (<see cref="C3dHierarchy.PortsFromLayouts"/>) into the document's
/// frame, in instance order then label number — two instances may both state a port 1; what could not be translated,
/// by name with the reason; and the notes the translation posts.
/// </summary>
public sealed record C3dLayoutPorts(IReadOnlyList<C3dLayoutPort> Ports, IReadOnlyList<string> LeftOut,
                                    IReadOnlyList<string> Notes);

/// <summary>R-pw1-4 — one <c>.c3d</c> port whose rectangle differs from what its layout's port label now translates to.</summary>
public sealed record C3dStalePort(int Number, long OldWidthDbu, long NewWidthDbu, C3dLayoutPort Translated);

/// <summary>R-pw1-5 — what Update Ports from Layout did: the records it moved, and the labels it added as new records,
/// each with the number it got (different from the label's own when that number was taken).</summary>
public sealed record C3dPortUpdate(IReadOnlyList<int> Updated, IReadOnlyList<(C3dLayoutPort From, int Number)> Added)
{
    public bool Changed => Updated.Count + Added.Count > 0;

    /// <summary>One sentence for the status line and the Messages panel.</summary>
    public string Describe()
    {
        if (!Changed) return "Every port the placed layouts state already matches.";
        var parts = new List<string>();
        if (Updated.Count > 0) parts.Add($"updated port(s) {string.Join(", ", Updated)}");
        if (Added.Count > 0)
            parts.Add("added " + string.Join(", ", Added.Select(a => a.Number == a.From.LayoutNumber
                ? $"{a.From.Instance}'s port {a.Number}"
                : $"{a.From.Instance}'s port {a.From.LayoutNumber} as port {a.Number} (port {a.From.LayoutNumber} is taken)")));
        string s = string.Join("; ", parts);
        return char.ToUpperInvariant(s[0]) + s[1..] + ".";
    }
}

public static class C3dHierarchy
{
    /// <summary>The prefix of a new instance's name: <c>U1</c>, <c>U2</c> …</summary>
    public const string InstancePrefix = "U";

    // ── names (R-em3d48-1c) ──────────────────────────────────────────────────────────────────

    /// <summary>Why <paramref name="name"/> cannot name an instance of <paramref name="doc"/> (other than
    /// <paramref name="self"/>), or null. An instance's contents are named <c>U1/…</c> and an array element's
    /// <c>U1[i,j,k]/…</c>, so neither separator may appear in it; <c>airbox</c> is reserved.</summary>
    public static string? ValidateInstanceName(C3dDocument doc, string name, C3dInstance? self = null)
    {
        name = name.Trim();
        if (name.Length == 0) return "A name cannot be empty.";
        if (string.Equals(name, C3dValidation.ReservedName, StringComparison.OrdinalIgnoreCase))
            return "'airbox' is reserved: the air box's faces are named after it.";
        if (name.IndexOfAny(['/', '[', ']']) >= 0)
            return "A name cannot hold '/', '[' or ']': an instance's contents are named '<instance>/<object>' and an array element's '<instance>[i,j,k]'.";
        if (doc.Objects.Any(o => o.Name == name) || doc.Instances.Any(i => !ReferenceEquals(i, self) && i.Name == name))
            return $"'{name}' is already the name of something in this 3D view.";
        return null;
    }

    /// <summary>The smallest <c>U&lt;n&gt;</c> nothing in <paramref name="doc"/> is called.</summary>
    public static string NextInstanceName(C3dDocument doc)
    {
        var used = new HashSet<string>(doc.Objects.Select(o => o.Name).Concat(doc.Instances.Select(i => i.Name)), StringComparer.Ordinal);
        for (int n = 1; ; n++)
            if (!used.Contains(InstancePrefix + n)) return InstancePrefix + n;
    }

    // ── views and cycles (R-em3d48-6a) ───────────────────────────────────────────────────────

    /// <summary>The cell folder a <c>.c3d</c> belongs to: the folder above its <c>3d/</c>, or null for a loose file.</summary>
    public static string? CellDirOf(string c3dPath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(c3dPath));
        return dir is not null && string.Equals(Path.GetFileName(dir), CellFolder.SubFolderName(ViewType.ThreeD), StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir) : null;
    }

    private static ViewType TypeOf(C3dInstanceView v) => v == C3dInstanceView.Layout ? ViewType.Layout : ViewType.ThreeD;

    /// <summary>The primary file of <paramref name="view"/> in <paramref name="cellDir"/>, or null.</summary>
    public static string? ViewFile(string cellDir, C3dInstanceView view)
    {
        var type = TypeOf(view);
        var primary = CellFolder.ResolvePrimary(cellDir, type);
        return primary.ResolvedName is { } f ? Path.Combine(CellFolder.SubFolderPath(cellDir, type), f) : null;
    }

    /// <summary>Why <paramref name="view"/> of <paramref name="cellDir"/> cannot be placed, or null: the view is missing.</summary>
    public static string? MissingView(string cellDir, C3dInstanceView view)
        => ViewFile(cellDir, view) is null
            ? $"Cell '{Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}' has no {CellFolder.ViewNoun(TypeOf(view))}."
            : null;

    /// <summary>
    /// R-em3d48-6a — why placing <paramref name="view"/> of <paramref name="cellDir"/> in the document at
    /// <paramref name="parentPath"/> would make the 3D view contain itself, with the path; null when it would not.
    /// A layout never contains a 3D view, so placing one never cycles.
    /// </summary>
    public static string? CycleRefusal(string parentPath, string cellDir, C3dInstanceView view)
    {
        if (view == C3dInstanceView.Layout) return null;
        string parentFile = Norm(parentPath);
        string? parentCell = CellDirOf(parentPath) is { } pc ? Norm(pc) : null;
        string childCell = Norm(cellDir);
        string Name(string dir) => Path.GetFileName(dir);
        string parentName = parentCell is { } p ? Name(p) : Path.GetFileNameWithoutExtension(parentPath);
        if (parentCell is not null && Same(childCell, parentCell))
            return $"The 3D view of '{parentName}' cannot hold its own 3D view ({parentName} → {parentName}): a cell cannot contain itself. " +
                   "Its layout can be placed here — the views differ.";

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { childCell };
        var path = new List<string> { parentName, Name(childCell) };
        return Walk(childCell);

        string? Walk(string cell)
        {
            if (ViewFile(cell, C3dInstanceView.ThreeD) is not { } file) return null;
            if (Same(Norm(file), parentFile)) return Refusal();
            C3dDocument doc;
            try { doc = C3dPersistence.LoadFromFile(file); }
            catch { return null; }
            string baseDir = Path.GetDirectoryName(file)!;
            foreach (var inst in doc.Instances.Where(i => i.View == C3dInstanceView.ThreeD))
            {
                if (ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is not { } d) continue;
                string next = Norm(d);
                if ((parentCell is not null && Same(next, parentCell)) ||
                    (ViewFile(next, C3dInstanceView.ThreeD) is { } nf && Same(Norm(nf), parentFile)))
                {
                    path.Add(parentName);
                    return Refusal();
                }
                if (!visited.Add(next)) continue;
                path.Add(Name(next));
                if (Walk(next) is { } found) return found;
                path.RemoveAt(path.Count - 1);
            }
            return null;
        }

        string Refusal() => $"Placing the 3D view of '{Name(childCell)}' here would make this 3D view contain itself: " +
                            $"{string.Join(" → ", path)}. A cell cannot contain itself.";
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── swap view (R-em3d48-2) ───────────────────────────────────────────────────────────────

    /// <summary>The other view of an instance's cell.</summary>
    public static C3dInstanceView Other(C3dInstanceView v) => v == C3dInstanceView.Layout ? C3dInstanceView.ThreeD : C3dInstanceView.Layout;

    /// <summary>Why <paramref name="inst"/> of the document at <paramref name="docPath"/> cannot swap to its other view, or
    /// null: its cell does not resolve, the other view is missing, or the swap would make a cycle.</summary>
    public static string? SwapRefusal(string docPath, C3dInstance inst)
    {
        var to = Other(inst.View);
        if (ExternalCellRef.ResolveCellDir(inst.CellRef, Path.GetDirectoryName(Path.GetFullPath(docPath))) is not { } cellDir || !Directory.Exists(cellDir))
            return $"'{inst.Name}' places '{inst.CellRef}', which resolves to nothing.";
        return MissingView(cellDir, to) is { } why ? why + " Swap View needs both." : CycleRefusal(docPath, cellDir, to);
    }

    /// <summary>
    /// Every name the document's ports and face boundaries give to something inside <paramref name="instance"/>
    /// (<c>U1/…</c>, <c>U1[i,j,k]/…</c>) — read from the records as they are stored, whatever their later schema,
    /// because any string value naming an instance's object is exactly what a swap can strand.
    /// </summary>
    public static IReadOnlyList<string> NamesInside(C3dDocument doc, string instance)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        void Visit(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Visit(p.Value); break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Visit(x); break;
                case JsonValueKind.String:
                    string s = e.GetString() ?? "";
                    if (s.StartsWith(instance + "/", StringComparison.Ordinal) || s.StartsWith(instance + "[", StringComparison.Ordinal)) found.Add(s);
                    break;
            }
        }
        foreach (var p in doc.Ports) Visit(JsonSerializer.SerializeToElement(p));
        foreach (var b in doc.FaceBoundaries) Visit(JsonSerializer.SerializeToElement(b));
        return [.. found];
    }

    /// <summary>Which of <paramref name="names"/> name no object of <paramref name="elaboration"/>. A name may carry a
    /// face after the object (<c>U1/pad/zmax</c>); it resolves when some prefix of it is an object.</summary>
    public static IReadOnlyList<string> Unresolved(IEnumerable<string> names, C3dElaboration elaboration)
    {
        var missing = new List<string>();
        foreach (string n in names)
        {
            bool ok = false;
            for (int cut = n.Length; cut > 0 && !ok; cut = n.LastIndexOf('/', cut - 1))
                ok = elaboration.Provenance.ContainsKey(n[..cut]);
            if (!ok) missing.Add(n);
        }
        return missing;
    }

    // ── flatten (R-em3d48-5) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One level of <paramref name="doc"/>'s instance at <paramref name="index"/>. A 3D child's objects and instances
    /// come up with their placements composed; a layout child becomes the solids <paramref name="elaboration"/> (this
    /// document's own, current) holds for it. <paramref name="tech"/> is this document's technology, which every
    /// flattened material must mean the same in; <paramref name="tempC"/> the temperature they are compared at.
    /// <paramref name="takenGroups"/> are group names in use that <paramref name="doc"/> does not show (Flatten all's
    /// instances of this level, not yet written back).
    /// </summary>
    public static C3dFlattenResult FlattenOne(C3dDocument doc, string docPath, int index, C3dElaboration elaboration,
                                              Technology? tech, TechnologyCache? cache = null, string? workspaceCws = null,
                                              double tempC = EmSetup.DefaultOperatingTempC, IEnumerable<string>? takenGroups = null)
    {
        var inst = doc.Instances[index];
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(docPath))!;
        if (ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is not { } cellDir || !Directory.Exists(cellDir))
            return C3dFlattenResult.Refuse($"'{inst.Name}' places '{inst.CellRef}', which resolves to nothing: there is nothing to flatten.");
        if (ViewFile(cellDir, inst.View) is not { } file)
            return C3dFlattenResult.Refuse(MissingView(cellDir, inst.View)!);
        var used = new HashSet<string>(doc.Objects.Select(o => o.Name).Concat(doc.Instances.Where(i => i != inst).Select(i => i.Name)),
                                       StringComparer.Ordinal);
        var flat = inst.View == C3dInstanceView.ThreeD
            ? FlattenThreeD(doc, docPath, inst, file, baseDir, used, tech, cache, workspaceCws, tempC)
            : FlattenLayout(doc, inst, elaboration, used, tech);
        // 3D editor groups — what the instance held lands in the instance's group, one level down in a new group named after
        // the instance when it held more than one thing: a flatten's many parts stay one thing to select, move or hide, and
        // Ungroup gives them back. A 3D child's own groups come up beneath that one. A single part needs no group of one.
        if (flat.Objects.Count + flat.Instances.Count > 1)
        {
            var taken = C3dGroups.Names(doc);
            if (takenGroups is not null) taken.UnionWith(takenGroups);
            string into = C3dGroups.Join([.. C3dGroups.Segments(inst.Group), FreeGroupName(inst.Name, taken)])!;
            var beneath = ChildGroupPaths(into, taken);
            foreach (var o in flat.Objects) o.Group = beneath(o.Group);
            foreach (var i in flat.Instances) i.Group = beneath(i.Group);
            flat = flat with { Group = into };
        }
        else
        {
            foreach (var o in flat.Objects) o.Group = inst.Group;
            foreach (var i in flat.Instances) i.Group = inst.Group;
        }
        // brief-em3d-92 — and what the instance's transparency multiplied onto each part is written onto the part itself, so
        // flattening does not change how anything is drawn (C3dTransparency.Compose; a part at its default takes the instance's).
        if (inst.Transparency is not null)
        {
            foreach (var o in flat.Objects.Where(o => o is not C3dPolyline)) o.Transparency = C3dTransparency.Compose(inst.Transparency, o.Transparency);
            foreach (var i in flat.Instances) i.Transparency = C3dTransparency.Compose(inst.Transparency, i.Transparency);
        }
        // brief-em3d-105 — and its appearance UNDER each part's: a part's own stated field wins, and a nested instance's over this
        // one's, which is the resolver's order. (One case differs: a part and the instance both stating a Like keep the part's only,
        // where the resolver would fall through the part's Like to the instance's for a field the first does not state.)
        if (inst.Appearance is not null)
        {
            foreach (var o in flat.Objects.Where(o => o is not C3dPolyline)) o.Appearance = TechAppearance.Over(o.Appearance, inst.Appearance);
            foreach (var i in flat.Instances) i.Appearance = TechAppearance.Over(i.Appearance, inst.Appearance);
        }
        // brief-em3d-93 — an instance that is not modelled flattens to parts that are not, so the solve is unchanged.
        if (!inst.Model)
        {
            foreach (var o in flat.Objects.Where(o => o is not C3dPolyline)) o.Model = false;
            foreach (var i in flat.Instances) i.Model = false;
        }
        return flat;
    }

    /// <summary><paramref name="name"/> as a group name not in <paramref name="taken"/> — else <c>name_2</c>, <c>name_3</c> … — added
    /// to it. Not <c>U2</c> for a clash on <c>U1</c>: that reads as another instance.</summary>
    private static string FreeGroupName(string name, HashSet<string> taken)
        => taken.Add(name) ? name : C3dOperations.NextFreeName(name + "_1", taken);

    /// <summary>A flattened part's group path: a 3D child's group <c>match</c> becomes <c>into/match</c>, each level under a name
    /// free in <paramref name="taken"/> (a group's name is unique in the whole document); no group becomes <paramref name="into"/>.
    /// The same child path always maps the same way, so an array's elements share their child's groups.</summary>
    private static Func<string?, string> ChildGroupPaths(string into, HashSet<string> taken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string Map(string path)
        {
            if (map.TryGetValue(path, out var known)) return known;
            string parent = C3dGroups.ParentOf(path) is { } p ? Map(p) : into;
            return map[path] = parent + C3dGroups.Separator + FreeGroupName(C3dGroups.NameOf(path), taken);
        }
        return path => string.IsNullOrEmpty(path) ? into : Map(path);
    }

    /// <summary>Each element of an instance: its suffix for names (empty for a plain instance) and its placement in the
    /// parent — the instance's own, moved by the pitch in the parent's frame (the elaborator's rule).</summary>
    public static IEnumerable<(string Suffix, C3dPlacement Placement)> Elements(C3dInstance inst)
    {
        var counts = inst.Array?.Counts ?? [1, 1, 1];
        bool array = inst.Array is not null && counts.Count == 3 && counts.Any(n => n > 1);
        if (!array) { yield return ("", inst.Placement.Clone()); yield break; }
        var pitch = inst.Array!.Pitch;
        for (int k = 0; k < counts[2]; k++)
            for (int j = 0; j < counts[1]; j++)
                for (int i = 0; i < counts[0]; i++)
                    yield return ($"_{i}_{j}_{k}", inst.Placement.Translated(new C3dPoint3(i * pitch.X, j * pitch.Y, k * pitch.Z)));
    }

    private static C3dFlattenResult FlattenThreeD(C3dDocument doc, string docPath, C3dInstance inst, string file, string baseDir, HashSet<string> used,
                                                  Technology? tech, TechnologyCache? cache, string? workspaceCws, double tempC)
    {
        C3dDocument child;
        try { child = C3dPersistence.LoadFromFile(file); }
        catch (Exception e) { return C3dFlattenResult.Refuse($"'{file}' cannot be read: {e.Message}"); }
        if (child.DbuPerMicron != doc.DbuPerMicron && doc.DbuPerMicron % child.DbuPerMicron != 0)
            return C3dFlattenResult.Refuse(
                $"'{inst.Name}' is drawn at {child.DbuPerMicron} DBU per µm and this 3D view at {doc.DbuPerMicron}: its coordinates do not " +
                "land on this view's grid, so flattening would move them. Keep the instance.");
        long scale = doc.DbuPerMicron / child.DbuPerMicron;

        // The child's materials resolve in ITS technology; each must mean the same here.
        var (childTech, _) = TechnologyResolver.ResolveForDocument(child.TechRef, file, workspaceCws, cache ?? new TechnologyCache());
        var materials = child.Objects.Select(o => o.Material).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (MaterialMismatch(materials.Select(m => (m, C3dProblemAssembly.ObjectMaterial(childTech.Tech, m))), tech, tempC, inst.Name) is { } refused)
            return refused;

        var objects = new List<C3dObject>();
        var instances = new List<C3dInstance>();
        var notes = new List<string>();
        string childDir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        foreach (var (suffix, placement) in Elements(inst))
        {
            var outer = placement.ToTransform();
            foreach (var o in child.Objects)
            {
                var copy = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(o));
                if (scale != 1) Scale(copy, scale);
                copy.Placement = copy.Placement.Then(outer, out bool exact);
                // brief-em3d-50 — a wire has no placement: the instance's lands on its points. brief-em3d-132 — a bound point keeps
                // its expression (the offset and swap writers), in this document's display unit; any other turn of one is refused.
                if (!exact) return C3dFlattenResult.Refuse(OffGrid(inst, o.Name));
                if (C3dWires.BakePlacement(copy, doc.DisplayUnit, doc.DbuPerMicron, out bool whole) is { } turn)
                    return C3dFlattenResult.Refuse($"Flattening '{inst.Name}' is refused: {turn}");
                if (!whole) return C3dFlattenResult.Refuse(OffGrid(inst, o.Name));
                copy.Name = Unique($"{inst.Name}{suffix}_{o.Name}", used);
                C3dImages.Rebase([copy], file, docPath);     // brief-em3d-101 — an image resolves against its new document
                objects.Add(copy);
            }
            foreach (var ci in child.Instances)
            {
                var copy = C3dPersistence.DeserializeInstance(C3dPersistence.SerializeInstance(ci));
                if (!ExternalCellRef.IsExternalRef(ci.CellRef) && ExternalCellRef.ResolveCellDir(ci.CellRef, childDir) is { } abs)
                    copy.CellRef = ExternalCellRef.MakeCellRef(baseDir, abs);
                var scaled = copy.Placement.Clone();
                if (scale != 1) scaled.Origin = Mul(scaled.Origin, scale);
                copy.Placement = scaled.Then(outer, out bool exact);
                if (!exact) return C3dFlattenResult.Refuse(OffGrid(inst, ci.Name));
                if (copy.Array is { } arr)
                {
                    var p = Mul(arr.Pitch, scale);
                    var (x, y, z) = (outer with { Tx = 0, Ty = 0, Tz = 0 }).Apply(p);
                    if (!Whole(x) || !Whole(y) || !Whole(z)) return C3dFlattenResult.Refuse(OffGrid(inst, ci.Name));
                    copy.Array = new C3dArray { Counts = [.. arr.Counts], Pitch = new C3dPoint3((long)x, (long)y, (long)z) };
                    if (!outer.IsIntegral || outer.M00 != 1 || outer.M11 != 1 || outer.M22 != 1)
                        notes.Add($"'{copy.Name}' is an array under a rotation or mirror: its pitch was turned with it.");
                }
                copy.Name = Unique($"{inst.Name}{suffix}_{ci.Name}", used);
                instances.Add(copy);
            }
        }
        if (child.Ports.Count > 0 || child.FaceBoundaries.Count > 0 || child.Setups.Count > 0)
            notes.Add("The child's ports, face boundaries and setups are not carried up: only the parent says where a signal enters (overview §1k).");
        return new C3dFlattenResult(objects, instances, null, notes);
    }

    private static string OffGrid(C3dInstance inst, string what)
        => $"Flattening '{inst.Name}' would put '{what}' off this view's DBU grid (the instance is turned by an angle that is not a " +
           "quarter turn): its geometry would move. Keep the instance.";

    private static C3dFlattenResult FlattenLayout(C3dDocument doc, C3dInstance inst, C3dElaboration e, HashSet<string> used, Technology? tech)
    {
        bool Mine(string path) => path == inst.Name || path.StartsWith(inst.Name + "[", StringComparison.Ordinal) ||
                                  path.StartsWith(inst.Name + "/", StringComparison.Ordinal);
        var solids = e.Solids.Where(s => e.Provenance.TryGetValue(s.Name, out var p) && Mine(p.InstancePath)).ToList();
        var sheets = e.Sheets.Where(s => e.Provenance.TryGetValue(s.Name, out var p) && Mine(p.InstancePath)).ToList();
        if (solids.Count + sheets.Count == 0)
            return C3dFlattenResult.Refuse($"'{inst.Name}' elaborates to nothing here ({string.Join(" ", e.Refusals.Where(r => r.Contains(inst.Name, StringComparison.Ordinal)))}).".Replace(" ().", "."));

        // A layout's solids are made of its STACKUP's entries (named for the entry, valued from it); an object here names a
        // material of this view's technology. Each entry becomes the technology's material of the same name, else the one
        // with equal values — never one that merely shares a name.
        var byName = e.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var rows = solids.Select(s => s.Name).Concat(sheets.Select(s => s.Name))
                         .Select(n => e.Origins.GetValueOrDefault(n)?.StackupEntry).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmatched = new List<string>();
        var missing = new List<TechMaterial>();
        bool stated = true;
        foreach (string n in solids.Select(s => s.Material).Concat(sheets.Select(s => s.Material)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var values = byName.GetValueOrDefault(n);
            string? match = C3dProblemAssembly.ObjectMaterial(tech, n) is { } same && (values is null || SameValues(C3dElaborator.MaterialValues(same, EmSetup.DefaultOperatingTempC), values))
                ? same.Name
                : values is null ? null
                // A material named after ANOTHER of the stackup's rows is that row's, not this one's: equal values do not make the
                // Dielectric 2 slab a Dielectric 1 slab.
                : tech?.ResolvedMaterials.FirstOrDefault(m => SameValues(C3dElaborator.MaterialValues(m, EmSetup.DefaultOperatingTempC), values) &&
                                                              !(rows.Contains(m.Name) && m.Name != n))?.Name;
            if (match is null)
            {
                unmatched.Add(values is null ? $"'{n}'" : $"'{n}' ({Describe(values)})");
                // A name the technology already holds with other values cannot be added beside it; nor can unknown values.
                if (values is null || C3dProblemAssembly.ObjectMaterial(tech, n) is not null) stated = false;
                // One new material per row, named after it, even where rows agree: each slab keeps saying which row it is. A
                // second flatten of the same cell finds them by name and adds nothing.
                else missing.Add(MaterialOf(n, values));
            }
            else rename[n] = match;
        }
        if (unmatched.Count > 0)
        {
            bool one = unmatched.Count == 1;
            return C3dFlattenResult.Refuse(
                $"'{inst.Name}' cannot be flattened: its layout's stackup makes solids of {List(unmatched)}, and this view's technology " +
                $"has no material of {(one ? "that name" : "those names")} or with {(one ? "those" : "their")} values. Every object in a 3D view " +
                $"names one of its technology's materials, so {(one ? "it" : "they")} must be added to the technology first — or keep the " +
                "instance. Substituting another material would change what the solver sees.") with { MissingMaterials = stated ? missing : [] };
        }

        double per = C3dLowering.Metres(1, doc.DbuPerMicron);
        var ordered = solids.Select(s => (s.Order, (object)s)).Concat(sheets.Select(s => (s.Order, (object)s))).OrderBy(t => t.Order);
        var objects = new List<C3dObject>();
        int rounded = 0, tessellated = 0;
        foreach (var (_, item) in ordered)
        {
            C3dObject? made = item switch
            {
                Em3dSolid s => FromSolid(s, per, ref rounded, ref tessellated),
                Em3dSheet sh => FromSheet(sh, per, ref rounded),
                _ => null,
            };
            if (made is null) return C3dFlattenResult.Refuse($"'{NameOf(item)}' of '{inst.Name}' has a shape a 3D view's objects cannot hold.");
            made.Name = Unique(Flat(NameOf(item)), used);
            made.Material = rename[made.Material!];
            made.DrawingLayer = e.Origins.GetValueOrDefault(NameOf(item))?.DrawingLayer;   // drawn in its layer's colour, as it was
            made.FlattenedFrom = inst.CellRef;                                               // brief-sdl-8 D2: a copy, said so
            objects.Add(made);
        }
        var notes = new List<string>();
        if (tessellated > 0) notes.Add($"{tessellated} curved solid(s) (bond wires, balls) became polyhedra: their facets are the viewer's tessellation.");
        if (rounded > 0) notes.Add($"{rounded} coordinate(s) were not whole DBU and were rounded to the nearest.");
        return new C3dFlattenResult(objects, [], null, notes);

        static string NameOf(object o) => o is Em3dSolid s ? s.Name : ((Em3dSheet)o).Name;
        static string Flat(string n) => n.Replace("/", "_").Replace("[", "_").Replace("]", "").Replace(",", "_");
    }

    private static bool SameValues(Em3dMaterial a, Em3dMaterial b)
        => a.Epsr == b.Epsr && a.TanD == b.TanD && a.Mur == b.Mur && Math.Abs(a.SigmaSm - b.SigmaSm) <= 1e-9 * Math.Abs(b.SigmaSm)
           && (a.EpsrTensor ?? []).SequenceEqual(b.EpsrTensor ?? []);

    private static string Describe(Em3dMaterial m) => m.SigmaSm > 0
        ? string.Create(CultureInfo.InvariantCulture, $"σ {m.SigmaSm:G6} S/m")
        : string.Create(CultureInfo.InvariantCulture, $"εr {m.Epsr:G6}, tanδ {m.TanD:G6}");

    /// <summary>The refusal when the flattened materials would not mean the same in <paramref name="tech"/>, or null.
    /// <paramref name="values"/>: each name with the material it is where it came from (null: unknown there). A material
    /// the technology lacks entirely is offered in <see cref="C3dFlattenResult.MissingMaterials"/>, copied whole.</summary>
    private static C3dFlattenResult? MaterialMismatch(IEnumerable<(string Name, TechMaterial? Source)> values, Technology? tech, double? tempC, string inst)
    {
        var missing = new List<string>();
        var differ = new List<string>();
        var copies = new List<TechMaterial>();
        bool stated = true;
        foreach (var (name, source) in values)
        {
            if (C3dProblemAssembly.ObjectMaterial(tech, name) is not { } here)
            {
                missing.Add($"'{name}'");
                if (source is null) stated = false;
                else copies.Add(MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize([source]))[0]);
                continue;
            }
            if (source is null) continue;
            var v = C3dElaborator.MaterialValues(source, tempC ?? EmSetup.DefaultOperatingTempC);
            var mine = C3dElaborator.MaterialValues(here, tempC ?? EmSetup.DefaultOperatingTempC);
            if (!SameValues(mine, v)) differ.Add($"'{name}'");
        }
        if (missing.Count == 0 && differ.Count == 0) return null;
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"this view's technology does not define {List(missing)}");
        if (differ.Count > 0) parts.Add($"it defines {List(differ)} with other values than the cell's own technology");
        return C3dFlattenResult.Refuse(
            $"'{inst}' cannot be flattened: its objects are made of materials that would not mean the same here — {string.Join("; ", parts)}. " +
            "Add or align them in the technology, or keep the instance. Substituting another material would change what the solver sees.")
            with { MissingMaterials = stated && differ.Count == 0 ? copies : [] };
    }

    /// <summary>A material holding exactly <paramref name="values"/>, as <see cref="C3dElaborator.MaterialValues"/> reads it back.</summary>
    private static TechMaterial MaterialOf(string name, Em3dMaterial values) => new()
    {
        Name = name,
        Epsr = values.Epsr,
        EpsrTensor = values.EpsrTensor is { Count: 3 } t ? [.. t] : null,
        TanD = values.TanD,
        Mur = values.Mur,
        Sigma20 = values.SigmaSm > 0 ? values.SigmaSm : null,
    };

    /// <summary>A material's values as a refusal names them: σ for a conductor, else εr and tanδ.</summary>
    public static string DescribeMaterial(TechMaterial m) => Describe(C3dElaborator.MaterialValues(m, EmSetup.DefaultOperatingTempC));

    /// <summary>'a', 'a' and 'b', 'a', 'b' and 'c'.</summary>
    private static string List(IReadOnlyList<string> items)
        => items.Count <= 1 ? string.Join("", items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    private static C3dObject? FromSolid(Em3dSolid s, double per, ref int rounded, ref int tessellated)
    {
        int r = 0;
        long D(double m)
        {
            double d = m / per, q = Math.Round(d, MidpointRounding.AwayFromZero);
            if (Math.Abs(d - q) > 1e-6) r++;
            return (long)q;
        }
        C3dPoint3 P(Point3 p) => new(D(p.X), D(p.Y), D(p.Z));
        C3dObject? made;
        switch (s.Primitive)
        {
            case Em3dBox b:
                var lo = P(b.Min);
                made = new C3dBox { Min = lo, Size = P(b.Max) - lo };
                break;
            case Em3dExtrudedPolygon x:
                made = new C3dPrism
                {
                    Plane = C3dPlane.XY, Offset = D(x.ZBottom), Height = D(x.ZTop) - D(x.ZBottom),
                    Outline = [.. x.Outline.Select(q => new C3dPoint2(D(q.X), D(q.Y)))],
                    Holes = [.. x.Holes.Select(h => h.Select(q => new C3dPoint2(D(q.X), D(q.Y))).ToList())],
                };
                break;
            case Em3dCylinder c when AxisOf(c) is { } axis:
                var a = P(c.AxisStart);
                var z = P(c.AxisEnd);
                long len = DrawingGet(z, axis) - DrawingGet(a, axis);
                made = new C3dCylinder { Base = len >= 0 ? a : z, Axis = axis, Length = Math.Abs(len), Radius = D(c.Radius) };
                break;
            case Em3dSphere sp:
                // brief-em3d-102 R-em3d102-1e — a flattened sphere (a drawn one, or a layout's ball bond) stays a sphere, not ~500 facets.
                made = new C3dSphere { Centre = P(sp.Center), Radius = D(sp.Radius) };
                break;
            case Em3dEllipsoid el when el.AlignedSemiAxes() is var (ax, ay, az):
                // brief-sar-3 — an ellipsoid on the world's axes stays an ellipsoid; a turned one is the default's triangles.
                made = new C3dEllipsoid { Centre = P(el.Center), SemiAxes = new C3dPoint3(D(ax), D(ay), D(az)) };
                break;
            case Em3dPolyhedron ph:
                made = new C3dPolyhedron
                {
                    Vertices = [.. ph.Vertices.Select(P)],
                    Faces = [.. ph.Faces.Select((f, i) => new C3dFace
                    {
                        Name = f.Name is { Length: > 0 } n ? n : $"face{i}",
                        Outer = [.. f.Outer], Holes = [.. f.Holes.Select(h => h.ToList())],
                    })],
                };
                break;
            default:
                // A wire, a flattened ball, a slanted cylinder: the viewer's own tessellation, as triangles.
                var mesh = Em3dTessellation.Of(s);
                made = new C3dPolyhedron
                {
                    Vertices = [.. mesh.Vertices.Select(P)],
                    Faces = [.. mesh.Triangles.Select((t, i) => new C3dFace { Name = $"t{i}", Outer = [t.A, t.B, t.C] })],
                };
                tessellated++;
                r = 0;                           // a tessellation's vertices are never on the grid; that is what the note says
                break;
        }
        rounded += r;
        made.Material = s.Material;
        made.Role = s.Role;
        return made;

        static C3dAxis? AxisOf(Em3dCylinder c)
        {
            double dx = c.AxisEnd.X - c.AxisStart.X, dy = c.AxisEnd.Y - c.AxisStart.Y, dz = c.AxisEnd.Z - c.AxisStart.Z;
            return dy == 0 && dz == 0 ? C3dAxis.X : dx == 0 && dz == 0 ? C3dAxis.Y : dx == 0 && dy == 0 ? C3dAxis.Z : null;
        }
        static long DrawingGet(C3dPoint3 p, C3dAxis axis) => axis switch { C3dAxis.X => p.X, C3dAxis.Y => p.Y, _ => p.Z };
    }

    private static C3dSheet? FromSheet(Em3dSheet sh, double per, ref int rounded)
    {
        int r = 0;
        long D(double m)
        {
            double d = m / per, q = Math.Round(d, MidpointRounding.AwayFromZero);
            if (Math.Abs(d - q) > 1e-6) r++;
            return (long)q;
        }
        C3dPlane plane;
        double offset;
        Func<Point2, C3dPoint2> uv;
        if (sh.Frame is not { } f)
        {
            plane = C3dPlane.XY; offset = sh.Z;
            uv = q => new C3dPoint2(D(q.X), D(q.Y));
        }
        else
        {
            // A frame whose u and v are world axes is one of the drawing planes; its origin's in-plane part shifts u and v.
            var world = new[] { f.World(0, 0, sh.Z), f.World(1, 0, sh.Z), f.World(0, 1, sh.Z) };
            var du = new Point3(world[1].X - world[0].X, world[1].Y - world[0].Y, world[1].Z - world[0].Z);
            var dv = new Point3(world[2].X - world[0].X, world[2].Y - world[0].Y, world[2].Z - world[0].Z);
            if (Unit(du, 0) && Unit(dv, 1)) { plane = C3dPlane.XY; offset = world[0].Z; uv = q => new C3dPoint2(D(world[0].X + q.X), D(world[0].Y + q.Y)); }
            else if (Unit(du, 1) && Unit(dv, 2)) { plane = C3dPlane.YZ; offset = world[0].X; uv = q => new C3dPoint2(D(world[0].Y + q.X), D(world[0].Z + q.Y)); }
            else if (Unit(du, 0) && Unit(dv, 2)) { plane = C3dPlane.XZ; offset = world[0].Y; uv = q => new C3dPoint2(D(world[0].X + q.X), D(world[0].Z + q.Y)); }
            else return null;
        }
        var made = new C3dSheet
        {
            Plane = plane, Offset = D(offset),
            Outline = [.. sh.Outline.Select(uv)],
            Holes = [.. sh.Holes.Select(h => h.Select(uv).ToList())],
            ThicknessUm = sh.ThicknessM > 0 ? sh.ThicknessM * 1e6 : null,
            Material = sh.Material,
        };
        rounded += r;
        return made;

        static bool Unit(Point3 d, int axis) => axis switch
        {
            0 => Math.Abs(d.X - 1) < 1e-12 && Math.Abs(d.Y) < 1e-12 && Math.Abs(d.Z) < 1e-12,
            1 => Math.Abs(d.Y - 1) < 1e-12 && Math.Abs(d.X) < 1e-12 && Math.Abs(d.Z) < 1e-12,
            _ => Math.Abs(d.Z - 1) < 1e-12 && Math.Abs(d.X) < 1e-12 && Math.Abs(d.Y) < 1e-12,
        };
    }

    /// <summary>A name not in <paramref name="used"/> (the name itself, else with a number), added to it.</summary>
    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        for (int n = 2; ; n++)
            if (used.Add(name + "_" + n)) return name + "_" + n;
    }

    private static C3dPoint3 Mul(C3dPoint3 p, long k) => new(p.X * k, p.Y * k, p.Z * k);
    private static C3dPoint2 Mul(C3dPoint2 p, long k) => new(p.U * k, p.V * k);
    private static bool Whole(double v) => Math.Abs(v - Math.Round(v)) <= 1e-6;

    /// <summary>An object's own-frame geometry multiplied by <paramref name="k"/> (a finer DBU); its origin too.</summary>
    private static void Scale(C3dObject o, long k)
    {
        o.Placement.Origin = Mul(o.Placement.Origin, k);
        switch (o)
        {
            case C3dBox b: b.Min = Mul(b.Min, k); b.Size = Mul(b.Size, k); break;
            case C3dPrism p:
                p.Offset *= k; p.Height *= k; p.Shear = Mul(p.Shear, k);
                p.Outline = [.. p.Outline.Select(q => Mul(q, k))];
                p.Holes = [.. p.Holes.Select(h => h.Select(q => Mul(q, k)).ToList())];
                break;
            case C3dCylinder c: c.Base = Mul(c.Base, k); c.Length *= k; c.Radius *= k; break;
            case C3dSphere sp: sp.Centre = Mul(sp.Centre, k); sp.Radius *= k; break;
            case C3dEllipsoid el: el.Centre = Mul(el.Centre, k); el.SemiAxes = Mul(el.SemiAxes, k); break;
            case C3dPhantom ph:
                ph.Centre = Mul(ph.Centre, k); ph.SemiAxes = Mul(ph.SemiAxes, k); ph.Size = Mul(ph.Size, k);
                ph.Radius *= k; ph.Length *= k; ph.Shell1 *= k; ph.Shell2 *= k; ph.Voxel *= k;
                break;
            case C3dSheet s:
                s.Offset *= k;
                if (s.Rect is { } r) s.Rect = new C3dRect { Min = Mul(r.Min, k), Size = Mul(r.Size, k) };
                s.Outline = [.. s.Outline.Select(q => Mul(q, k))];
                s.Holes = [.. s.Holes.Select(h => h.Select(q => Mul(q, k)).ToList())];
                break;
            case C3dCylinderSheet cs: cs.Base = Mul(cs.Base, k); cs.Length *= k; cs.Radius *= k; break;
            case C3dPolyline l:
                l.Offset *= k;
                l.Points = [.. l.Points.Select(q => Mul(q, k))];
                if (l.Points3 is { } p3) l.Points3 = [.. p3.Select(q => Mul(q, k))];
                break;
            case C3dPolyhedron ph: ph.Vertices = [.. ph.Vertices.Select(q => Mul(q, k))]; break;
            case C3dWire w:
                w.Points = [.. w.Points.Select(q => Mul(q, k))];
                if (w.Array is { } wa) wa.Pitch = Mul(wa.Pitch, k);
                break;
        }
    }

    /// <summary>
    /// R-em3d48-5 / R-L3c-4 — every level of <paramref name="doc"/> flattened, on a COPY: one level at a time until no
    /// instance is left. Returns the flat copy (or why not) so the caller can state the count before anything changes.
    /// <paramref name="elaborate"/> elaborates a working copy (the editor's elaborator, whose child cache makes it cheap).
    /// </summary>
    public static (C3dDocument? Flat, string? Refusal, IReadOnlyList<string> Notes, IReadOnlyList<TechMaterial> MissingMaterials) FlattenAll(
        C3dDocument doc, string docPath, Func<C3dDocument, C3dElaboration> elaborate, Technology? tech,
        TechnologyCache? cache = null, string? workspaceCws = null, int maxLevels = 64)
    {
        var work = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
        var notes = new List<string>();
        for (int level = 0; work.Instances.Count > 0; level++)
        {
            if (level == maxLevels) return (null, $"The hierarchy is deeper than {maxLevels} levels.", notes, []);
            var e = elaborate(work);
            var next = new List<C3dInstance>();
            var added = new List<C3dObject>();
            var nextGroups = new HashSet<string>(StringComparer.Ordinal);       // the groups `next` names, not in `work` yet
            for (int i = 0; i < work.Instances.Count; i++)
            {
                var r = FlattenOne(work, docPath, i, e, tech, cache, workspaceCws, takenGroups: nextGroups);
                if (r.Refusal is { } why) return (null, why, notes, r.MissingMaterials);
                added.AddRange(r.Objects);
                next.AddRange(r.Instances);
                foreach (var ni in r.Instances) nextGroups.UnionWith(C3dGroups.Segments(ni.Group));
                foreach (string n in r.Notes) if (!notes.Contains(n)) notes.Add(n);
                // Later instances' new names must not collide with what this one just added.
                work.Objects.AddRange(r.Objects);
            }
            work.Instances = next;
        }
        return (work, null, notes, []);
    }

    // ── group into cell (R-em3d48-5) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the objects at <paramref name="objectIndices"/> and the instances at <paramref name="instanceIndices"/> of
    /// <paramref name="doc"/> into a new cell <paramref name="cellName"/> under <paramref name="parentDir"/> — its
    /// <c>3d/</c> holding them, every placement moved by −<paramref name="anchor"/> — and returns the instance that
    /// replaces them (at the anchor, so nothing moves) with the new files, or why not. <paramref name="doc"/> itself is
    /// not changed: the caller makes that one undo entry.
    /// </summary>
    public static (C3dInstance? Instance, string? CellDir, string? C3dPath, string? Refusal) GroupIntoCell(
        C3dDocument doc, string docPath, IReadOnlyList<int> objectIndices, IReadOnlyList<int> instanceIndices,
        string cellName, string parentDir, C3dPoint3 anchor)
    {
        if (objectIndices.Count + instanceIndices.Count == 0) return (null, null, null, "Select what to group first.");
        if (NameValidator.Validate(cellName) is { } bad) return (null, null, null, $"'{cellName}' cannot name a cell: {bad}");
        string cellDir = Path.Combine(parentDir, cellName);
        if (Directory.Exists(cellDir) || File.Exists(cellDir)) return (null, null, null, $"'{cellDir}' already exists.");

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(docPath))!;
        string newPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.ThreeD), cellName + C3dPersistence.Extension);
        string newDir = Path.GetDirectoryName(newPath)!;
        var minus = new C3dPoint3(-anchor.X, -anchor.Y, -anchor.Z);
        // 3D editor groups — the new instance takes the group everything grouped shared, and the child keeps only the
        // groups below it: moving a group's contents into a cell leaves the cell where the group was.
        var paths = objectIndices.Select(i => doc.Objects[i].Group).Concat(instanceIndices.Select(i => doc.Instances[i].Group)).ToList();
        var shared = C3dGroups.Segments(paths[0]).ToList();
        foreach (var p in paths.Skip(1))
        {
            var segs = C3dGroups.Segments(p);
            int k = 0;
            while (k < shared.Count && k < segs.Length && shared[k] == segs[k]) k++;
            shared.RemoveRange(k, shared.Count - k);
        }
        string? Below(string? path) => C3dGroups.Join(C3dGroups.Segments(path).Skip(shared.Count));
        var child = new C3dDocument
        {
            DbuPerMicron = doc.DbuPerMicron,
            DisplayUnit = doc.DisplayUnit,
            SnapDbu = doc.SnapDbu,
            TechRef = doc.TechRef is { Length: > 0 } tr && !Path.IsPathRooted(tr)
                ? Path.GetRelativePath(newDir, Path.GetFullPath(Path.Combine(baseDir, tr))).Replace('\\', '/')
                : doc.TechRef,
            Look = C3dLook.ForNewDocument(),
        };
        foreach (int i in objectIndices.Order())
        {
            var o = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(doc.Objects[i]));
            o.Placement = o.Placement.Translated(minus);
            o.Group = Below(o.Group);
            if (C3dWires.BakePlacement(o, doc.DisplayUnit, doc.DbuPerMicron, out _) is { } why) return (null, null, null, why);
            C3dImages.Rebase([o], docPath, newPath);         // brief-em3d-101 — an image resolves against its new document
            child.Objects.Add(o);
        }
        foreach (int i in instanceIndices.Order())
        {
            var inst = C3dPersistence.DeserializeInstance(C3dPersistence.SerializeInstance(doc.Instances[i]));
            if (!ExternalCellRef.IsExternalRef(inst.CellRef) && ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is { } abs)
                inst.CellRef = Path.GetRelativePath(newDir, abs).Replace('\\', '/');
            inst.Placement = inst.Placement.Translated(minus);
            inst.Group = Below(inst.Group);
            child.Instances.Add(inst);
        }
        try
        {
            CellFolder.CreateCellFolder(parentDir, cellName);
            CellCreate.WriteThreeDView(cellDir, cellName, child);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null, null, $"The cell could not be created: {e.Message}");
        }
        var placed = new C3dInstance
        {
            Name = NextInstanceName(doc),
            CellRef = ExternalCellRef.MakeCellRef(baseDir, cellDir),
            Group = C3dGroups.Join(shared),
            Placement = new C3dPlacement { Origin = anchor },
        };
        return (placed, cellDir, newPath, null);
    }

    // ── New 3D View from Layout (R-em3d48-7) ─────────────────────────────────────────────────

    /// <summary>
    /// A cell's first <c>.c3d</c> from its own layout: one instance <c>U1</c> of it at the origin, the layout's display
    /// unit, DBU and snap, the layout's technology as <c>TechRef</c> (relative to <paramref name="c3dPath"/>), and a
    /// copy of every 3D <c>.cem</c> naming the layout as an embedded setup — its ports translated to parent-level ports
    /// where each is one axis-aligned rectangle, else left out and listed. Writes nothing.
    /// </summary>
    public static C3dFromLayout NewFromLayout(string cellDir, string c3dPath, string? workspaceCws, TechnologyCache? cache = null)
    {
        cache ??= new TechnologyCache();
        if (ViewFile(cellDir, C3dInstanceView.Layout) is not { } clay)
            return new(null, MissingView(cellDir, C3dInstanceView.Layout), [], []);
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception e) { return new(null, $"'{clay}' cannot be read: {e.Message}", [], []); }

        string c3dDir = Path.GetDirectoryName(Path.GetFullPath(c3dPath))!;
        var (tech, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, workspaceCws, cache);
        var doc = new C3dDocument
        {
            DbuPerMicron = view.DbuPerMicron,
            DisplayUnit = view.DisplayUnit,
            SnapDbu = view.SnapDbu,
            TechRef = tech.ResolvedPath is { } tp ? Path.GetRelativePath(c3dDir, tp).Replace('\\', '/') : null,
            Instances = [new C3dInstance { Name = InstancePrefix + "1", CellRef = ExternalCellRef.MakeCellRef(c3dDir, cellDir), View = C3dInstanceView.Layout }],
            Look = C3dLook.ForNewDocument(),
        };

        // Every .cem naming this layout: the workspace's own finder when there is a workspace, else the cell's em/ folder.
        IEnumerable<string> cems = workspaceCws is { } cws && File.Exists(cws)
            ? EmSetupResolver.FindSetupsForLayout(cws, clay)
            : Directory.Exists(Path.Combine(cellDir, "em"))
                ? Directory.EnumerateFiles(Path.Combine(cellDir, "em"), "*" + EmSetupPersistence.Extension)
                    .Where(c => { try { var s = EmSetupPersistence.LoadFromFile(c); return EmSetupResolver.ResolveLayoutPath(c, s.LayoutRef, null) is { } p && Same(Norm(p), Norm(clay)); } catch { return false; } })
                : [];
        var copied = new List<string>();
        var leftOut = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ports = new Dictionary<int, C3dPort>();
        var moved = new List<string>();
        foreach (string cem in cems.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            EmSetup setup;
            try { setup = EmSetupPersistence.LoadFromFile(cem); }
            catch { continue; }
            if (!setup.Is3D) continue;
            if (setup.Name.Length == 0) setup.Name = Path.GetFileNameWithoutExtension(cem);
            string baseName = setup.Name;
            for (int n = 2; !names.Add(setup.Name); n++) setup.Name = $"{baseName} {n}";
            doc.Setups.Add(EmSetupPersistence.ToEmbedded(setup));
            copied.Add(setup.Name);
            TranslatePorts(setup, cem, workspaceCws, cache, view, ports, leftOut, moved);
        }
        // R-pw1-6 — a layout no 3D setup names still brings in its ports, translated with a default driven setup.
        string? portsNote = null;
        if (copied.Count == 0 && tech.Tech is { } t &&
            view.Shapes.OfType<LabelShape>().Any(l => l.IsPort))
        {
            TranslateWith(DefaultPortSetup(clay), new EmLayoutSource(clay, view, t, view.DbuPerMicron), t, view, null,
                          ports, leftOut, moved);
            portsNote = DefaultSetupNote(ports.Count);
        }
        foreach (var (n, p) in ports) p.FromLayout = new C3dPortSource { Instance = doc.Instances[0].Name, Port = n };
        doc.Ports = [.. ports.OrderBy(p => p.Key).Select(p => p.Value)];
        // brief-sdl-8 D4 — a layout made from the cell's schematic brings one linked VAR per name that drives it.
        var linked = view.GeneratedPlacements.Count > 0 ? AddLinkedVariables(doc, cellDir) : [];
        return new C3dFromLayout(doc, null, copied, leftOut) { PortsNote = portsNote, PortsMoved = moved, LinkedVariables = linked };
    }

    /// <summary>
    /// brief-sdl-8 D3/D4 — one VAR per cell parameter (its default as the expression, linked by name as every VAR named like
    /// a parameter is) and one per VAR of the cell's schematic, linked by the same rule. A schematic VAR's expression may read
    /// names this view cannot see, so it is written as its VALUE in its row's unit: the view resolves on its own, and an EM
    /// evaluation of the cell replaces it with the schematic's value at the point. A row whose unit this view cannot state
    /// is left out. Returns the names added.
    /// </summary>
    internal static List<string> AddLinkedVariables(C3dDocument doc, string cellDir)
    {
        var added = new List<string>();
        var taken = doc.Variables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var p in C3dCell.OfFolder(cellDir).Parameters)
        {
            if (C3dResolver.ValidateName(p.Name) is not null || !taken.Add(p.Name)) continue;
            doc.Variables.Add(new C3dVariable { Name = p.Name, Expression = p.DefaultExpression, Unit = p.Unit is { Length: > 0 } u ? u : null });
            added.Add(p.Name);
        }
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || primary.ResolvedName is null) return added;
        CircuitRF.Design.Schematic.SchematicEditModel model;
        try
        {
            string csch = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), primary.ResolvedName);
            model = CircuitRF.Design.Schematic.SchematicPersistence.LoadFromFile(csch).model;
            model.SchematicDirectory = Path.GetDirectoryName(csch);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                                       or ArgumentException or InvalidOperationException) { return added; }
        var values = CircuitRF.Design.Schematic.CellVariables.Values(model);
        foreach (var row in CircuitRF.Design.Schematic.CellVariables.Of(cellDir))
        {
            if (C3dResolver.ValidateName(row.Name) is not null || taken.Contains(row.Name)) continue;
            if (!values.TryGetValue(row.Name, out var v)) continue;
            string? unit = row.Unit is { Length: > 0 } ru ? ru.Trim() : null;
            double scale = 1;
            if (unit is not null)
            {
                if (C3dUnits.Engine(unit, out string? bad) is not { } engine || bad is not null) continue;
                scale = CircuitRF.Core.Expressions.Units.Scale(engine) is { } sc && sc != 0 ? sc : 1;
            }
            taken.Add(row.Name);
            doc.Variables.Add(new C3dVariable
            {
                Name = row.Name, Unit = unit,
                Expression = (v.Value / scale).ToString("R", CultureInfo.InvariantCulture),
            });
            added.Add(row.Name);
        }
        return added;
    }

    /// <summary>R-pw1-6 — the setup a layout's port labels are translated with when no 3D <c>.cem</c> names it: the
    /// default 3D solver, driven, at a new setup's frequencies, every other choice the setup's own default.</summary>
    public static EmSetup DefaultPortSetup(string layoutPath) => new()
    {
        Name = "ports", LayoutRef = layoutPath, Solver3D = Em3dSolvers.Default, Frequency = EmSetup.NewSetupFrequency(),
    };

    private static string DefaultSetupNote(int count) =>
        $"No 3D setup names this layout, so its port labels were brought in with a default driven setup ({count} " +
        "port(s)). Each is the sheet a 3D run of the layout would build; delete any you do not want.";

    /// <summary>
    /// R-pw1-4/5 — every <c>Layout</c> instance's port labels, translated now into <paramref name="doc"/>'s frame (the
    /// instance's placement applied) with the first 3D <c>.cem</c> naming that layout, else the default driven setup
    /// (R-pw1-6). Every instance's labels, numbered as the labels number them: two instances may both state a port 1, and
    /// <see cref="UpdatePorts"/> is what gives the later one a free number. Writes nothing.
    /// </summary>
    /// <param name="layoutsInMemory">brief-sdl-8 D4 — layouts to translate from memory rather than their file, by full path:
    /// an EM evaluation's regenerated layout, whose port labels moved with its copper.</param>
    public static C3dLayoutPorts PortsFromLayouts(C3dDocument doc, string c3dPath, string? workspaceCws,
                                                  TechnologyCache? cache = null,
                                                  IReadOnlyDictionary<string, LayoutView>? layoutsInMemory = null)
    {
        cache ??= new TechnologyCache();
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(c3dPath))!;
        var ports = new List<C3dLayoutPort>();
        var leftOut = new List<string>();
        var notes = new List<string>();
        foreach (var inst in doc.Instances.Where(i => i.View == C3dInstanceView.Layout))
        {
            if (inst.Array is not null) { leftOut.Add($"{inst.Name}: an arrayed instance's ports are not translated"); continue; }
            if (ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is not { } cellDir ||
                ViewFile(cellDir, C3dInstanceView.Layout) is not { } clay) continue;
            LayoutView view;
            var memory = layoutsInMemory?.GetValueOrDefault(Path.GetFullPath(clay));
            try { view = memory ?? LayoutPersistence.LoadFromFile(clay); }
            catch (Exception e) { leftOut.Add($"{inst.Name}: '{clay}' cannot be read ({e.Message})"); continue; }
            if (!view.Shapes.OfType<LabelShape>().Any(l => l.IsPort)) continue;

            var mine = new Dictionary<int, C3dPort>();
            var placement = inst.Placement.IsDefault ? (C3dTransform?)null : inst.Placement.ToTransform();
            string? cem = (workspaceCws is { } cws && File.Exists(cws)
                    ? EmSetupResolver.FindSetupsForLayout(cws, clay)
                    : Directory.Exists(Path.Combine(cellDir, "em"))
                        ? Directory.EnumerateFiles(Path.Combine(cellDir, "em"), "*" + EmSetupPersistence.Extension)
                        : [])
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(c => { try { var st = EmSetupPersistence.LoadFromFile(c); return st.Is3D && EmSetupResolver.ResolveLayoutPath(c, st.LayoutRef, null) is { } lp && Same(Norm(lp), Norm(clay)); } catch { return false; } });
            if (cem is not null)
            {
                var setup = EmSetupPersistence.LoadFromFile(cem);
                if (setup.Name.Length == 0) setup.Name = Path.GetFileNameWithoutExtension(cem);
                if (memory is null) TranslatePorts(setup, cem, workspaceCws, cache, view, mine, leftOut, notes, placement);
                else
                {
                    var (mt, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, workspaceCws, cache);
                    if (mt.Tech is not { } t1) { leftOut.Add($"{inst.Name}: its layout's technology does not resolve"); continue; }
                    TranslateWith(setup, new EmLayoutSource(clay, view, t1, view.DbuPerMicron), t1, view, placement, mine, leftOut, notes);
                }
            }
            else
            {
                var (tech, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, workspaceCws, cache);
                if (tech.Tech is not { } t) { leftOut.Add($"{inst.Name}: its layout's technology does not resolve"); continue; }
                TranslateWith(DefaultPortSetup(clay), new EmLayoutSource(clay, view, t, view.DbuPerMicron), t, view, placement,
                              mine, leftOut, notes);
                notes.Add($"{inst.Name}: " + DefaultSetupNote(mine.Count));
            }
            foreach (var (n, p) in mine.OrderBy(m => m.Key))
            {
                p.FromLayout = new C3dPortSource { Instance = inst.Name, Port = n };
                ports.Add(new C3dLayoutPort(inst.Name, n, p));
            }
        }
        return new C3dLayoutPorts(ports, leftOut, notes);
    }

    /// <summary>
    /// The layout label a record stands for, among those not yet <paramref name="claimed"/>: by its
    /// <see cref="C3dPort.FromLayout"/> link when it has one (so a renumbered record, or one from a second instance that
    /// also numbers a port 1, still finds its label); otherwise the label of the same number on the same plane at the same
    /// offset — the same edge — which is what a record made before the link existed can be matched by.
    ///
    /// <para><b>The plane and offset are part of the unlinked match on purpose.</b> A parent's own port may share a number
    /// with a placed layout's label and be a different port altogether — the shipped 3D Package example's P1/P2 are the
    /// package's pins, millimetres from the die's labels 1 and 2. A Width or Use edge width edit keeps a sheet on its edge,
    /// so such an edit is still caught.</para>
    /// </summary>
    private static C3dLayoutPort? MatchOf(C3dPort record, C3dLayoutPorts translated, ISet<C3dLayoutPort>? claimed = null)
    {
        if (record.Cylinder is not null || record.Terminals is not null) return null;
        bool Free(C3dLayoutPort x) => claimed is null || !claimed.Contains(x);
        if (record.FromLayout is { } src)
            return translated.Ports.FirstOrDefault(x => Free(x) && x.Instance == src.Instance && x.LayoutNumber == src.Port);
        return translated.Ports.FirstOrDefault(x => Free(x) && x.LayoutNumber == record.Number &&
                                                    x.Port.Plane == record.Plane && x.Port.Offset == record.Offset);
    }

    private static bool SamePlace(C3dPort a, C3dPort b) =>
        a.Plane == b.Plane && a.Offset == b.Offset && a.Rect.Min == b.Rect.Min && a.Rect.Size == b.Rect.Size;

    /// <summary>
    /// R-pw1-4 — the document's ports that stand for a placed layout's port label (<see cref="MatchOf"/>) and whose sheet
    /// differs from what that label translates to now, with both widths (the rectangle's longer side). Nothing is
    /// rewritten.
    /// </summary>
    public static IReadOnlyList<C3dStalePort> StalePorts(C3dDocument doc, C3dLayoutPorts translated)
    {
        static long Width(C3dPort p) => Math.Max(Math.Abs(p.Rect.Size.U), Math.Abs(p.Rect.Size.V));
        var claimed = new HashSet<C3dLayoutPort>();
        var stale = new List<C3dStalePort>();
        foreach (var port in doc.Ports)
        {
            if (MatchOf(port, translated, claimed) is not { } now) continue;
            claimed.Add(now);
            // A turned port was turned here, on purpose: it stands for its label and is left as it is.
            if (port.Angle != 0 || SamePlace(port, now.Port)) continue;
            stale.Add(new C3dStalePort(port.Number, Width(port), Width(now.Port), now));
        }
        return stale;
    }

    /// <summary>
    /// R-pw1-5 — Update Ports from Layout, on <paramref name="doc"/>. Every record that stands for a label
    /// (<see cref="MatchOf"/>) takes the label's sheet and kind — its number, name, Z0, stated conductors and Model flag
    /// are kept — and is linked to it. Every label no record stands for is added: at its own number when that is free,
    /// else at the smallest free one (a terminal's number counts as taken), which is how a second placed layout's port 1
    /// comes in. Records no label stands for are left alone.
    /// </summary>
    public static C3dPortUpdate UpdatePorts(C3dDocument doc, C3dLayoutPorts translated)
    {
        var claimed = new HashSet<C3dLayoutPort>();
        var updated = new List<int>();
        foreach (var record in doc.Ports)
        {
            if (MatchOf(record, translated, claimed) is not { } now) continue;
            claimed.Add(now);
            record.FromLayout ??= new C3dPortSource { Instance = now.Instance, Port = now.LayoutNumber };
            if (record.Angle != 0 || (SamePlace(record, now.Port) && record.Kind == now.Port.Kind)) continue;
            record.Plane = now.Port.Plane; record.Offset = now.Port.Offset; record.Kind = now.Port.Kind;
            record.Rect = new C3dRect { Min = now.Port.Rect.Min, Size = now.Port.Rect.Size };
            updated.Add(record.Number);
        }

        var used = C3dPorts.UsedNumbers(doc).ToHashSet();
        var added = new List<(C3dLayoutPort, int)>();
        foreach (var label in translated.Ports.Where(x => !claimed.Contains(x)))
        {
            int n = label.LayoutNumber;
            if (used.Contains(n)) { n = 1; while (used.Contains(n)) n++; }
            used.Add(n);
            var p = label.Port;
            doc.Ports.Add(new C3dPort
            {
                Number = n, Name = $"P{n}", Kind = p.Kind, Plane = p.Plane, Offset = p.Offset,
                Rect = new C3dRect { Min = p.Rect.Min, Size = p.Rect.Size }, Z0 = p.Z0,
                FromLayout = new C3dPortSource { Instance = label.Instance, Port = label.LayoutNumber },
            });
            added.Add((label, n));
        }
        if (updated.Count + added.Count > 0) doc.Ports = [.. doc.Ports.OrderBy(p => C3dPorts.Numbers(p).DefaultIfEmpty(0).Min())];
        return new C3dPortUpdate(updated, added);
    }

    /// <summary>The setup's generated ports as brief 49's parent-level records (one per number, the first setup's wins),
    /// in the new view's frame: the layout sits at the origin with its stack's bottom at z = 0, then
    /// <paramref name="placement"/> when the instance states one.</summary>
    private static void TranslatePorts(EmSetup setup, string cem, string? workspaceCws, TechnologyCache cache, LayoutView view,
                                       Dictionary<int, C3dPort> ports, List<string> leftOut, List<string> moved,
                                       C3dTransform? placement = null)
    {
        // brief-em3d-49 — a layout's ports are its port labels, whatever kind the setup states for them: Ports3D only
        // names a wave port, so gating on it left every all-lumped setup's ports behind.
        var resolved = EmSetupResolver.Resolve(cem, setup.LayoutRef, workspaceCws, cache);
        if (resolved.Source is not { Technology: { } tech } source) return;
        TranslateWith(setup, source, tech, view, placement, ports, leftOut, moved);
    }

    private static void TranslateWith(EmSetup setup, EmLayoutSource source, Technology tech, LayoutView view,
                                      C3dTransform? placement, Dictionary<int, C3dPort> ports, List<string> leftOut,
                                      List<string> moved)
    {
        // One port that cannot be built leaves out that port alone, and a plane pulled back from a line's end puts the
        // port where the plane begins (Em3dGenerator's portsEach): the .c3d's ports are rectangles the user can move.
        // Designer feedback round 34 — a .c3d instance always takes the whole layout, so the setup's solve region is not
        // this view's: applied here it refused the generation for a port outside it, and every port was left out.
        if (setup.SolveRegion is not null)
        {
            setup = setup.Clone();
            setup.SolveRegion = null;
        }
        var g = Em3dGenerator.Generate(setup, source, tech, portsEach: true);
        if (g.Problem is not { } problem)
        {
            leftOut.Add($"{setup.Name}: its ports ({g.Refusal})");
            return;
        }
        foreach (var lo in g.PortsLeftOut)
            leftOut.Add($"{setup.Name}: port {lo.Port} ({lo.Why})");
        moved.AddRange(g.PortsMoved);
        var solids = Em3dLayoutSolids.From(source, tech, null, new Em3dLayoutSolidsOptions(null, setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC, Instance: true));
        double dz = -solids.StackBottomM;
        double per = C3dLowering.Metres(1, view.DbuPerMicron);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        foreach (var p in problem.Ports)
        {
            if (ports.ContainsKey(p.Number)) continue;
            // The corners in DBU, in the layout's own frame, then through the instance's placement (R-pw1-4).
            var ca = new C3dPoint3(D(p.Min.X), D(p.Min.Y), D(p.Min.Z + dz));
            var cb = new C3dPoint3(D(p.Max.X), D(p.Max.Y), D(p.Max.Z + dz));
            if (placement is { } tr)
            {
                static long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
                var (ax, ay, az) = tr.Apply(ca);
                var (bx, by, bz) = tr.Apply(cb);
                (ca, cb) = (new C3dPoint3(R(Math.Min(ax, bx)), R(Math.Min(ay, by)), R(Math.Min(az, bz))),
                            new C3dPoint3(R(Math.Max(ax, bx)), R(Math.Max(ay, by)), R(Math.Max(az, bz))));
            }
            int flat = ca.X == cb.X ? 0 : ca.Y == cb.Y ? 1 : ca.Z == cb.Z ? 2 : -1;
            if (p.Annulus is not null || flat < 0)
            {
                leftOut.Add($"{setup.Name}: port {p.Number} ({p.Name}) — " + (p.Annulus is not null ? "a coaxial annulus is not one rectangle" : "it is not flat"));
                continue;
            }
            var (plane, offset, u0, v0, u1, v1) = flat switch
            {
                0 => (C3dPlane.YZ, ca.X, ca.Y, ca.Z, cb.Y, cb.Z),
                1 => (C3dPlane.XZ, ca.Y, ca.X, ca.Z, cb.X, cb.Z),
                _ => (C3dPlane.XY, ca.Z, ca.X, ca.Y, cb.X, cb.Y),
            };
            ports[p.Number] = new C3dPort
            {
                Number = p.Number,
                Name = $"P{p.Number}",
                Kind = p.Kind,
                Plane = plane,
                Offset = offset,
                Rect = new C3dRect { Min = new C3dPoint2(u0, v0), Size = new C3dPoint2(u1 - u0, v1 - v0) },
                Z0 = C3dPorts.FormatZ0(p.Z0),
            };
        }
    }
}
