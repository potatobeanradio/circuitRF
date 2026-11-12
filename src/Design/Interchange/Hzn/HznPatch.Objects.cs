// HZN-5 R-hzn5-2 — objects: a changed one is edited step by step, an added one appended, a deleted one removed with what names
// only it.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznPatch
{
    private void Objects()
    {
        var delta = _objectDelta = Compare(Read(_b, "Objects", x => Str(x, "Name")), Read(_e, "Objects", x => Str(x, "Name")));
        foreach (string n in delta.Removed) DeleteObject(n);
        foreach (string n in delta.Changed) ChangeObject(n);
        foreach (string n in delta.Added) AddObject(n);
    }

    /// <summary>The names of the objects a changed top-level object is written as: the object and the tools its booleans consume.</summary>
    private static Dictionary<string, HznJsonObject> ByName(HznJsonArray? array) =>
        HznJsonOps.Objects(array).Where(o => o.GetString("name") is { Length: > 0 })
                  .GroupBy(o => o.GetString("name")!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    // ── changed ───────────────────────────────────────────────────────────────────────────────────

    private void ChangeObject(string name)
    {
        var wb = Write(Mini(_b, [name]), $"Object '{name}'", opened: true);
        var we = Write(Mini(_e, [name]), $"Object '{name}'", opened: false);
        if (wb is null || we is null) return;
        // A sheet is metal because a boundary says so; a change to which metal, or how thick, is a change to that boundary.
        if (!HznJsonOps.Same(wb.Json!["boundaries"], we.Json!["boundaries"]))
        {
            Refuse($"Object '{name}': its metal (the material or thickness of a sheet) is a boundary in a Hertzon design, and a change to it is not saved. " +
                   "Undo that change, or Save As a .c3d.");
            return;
        }
        PatchGroup(wb.Json!, we.Json!, name);
    }

    /// <summary>One changed top-level object, written before and after: the objects both writings hold are edited in place, an object
    /// only the edited one holds (a new tool) is appended, one only the baseline holds (a removed tool) is removed.</summary>
    private void PatchGroup(HznJsonObject wb, HznJsonObject we, string who)
    {
        var before = ByName(wb.GetArray("objects"));
        var after = ByName(we.GetArray("objects"));
        foreach (var (name, edited) in after)
        {
            if (before.TryGetValue(name, out var opened))
            {
                if (HznJsonOps.ByName(_o.GetArray("objects"), name) is not { } target)
                {
                    Refuse($"Object '{who}': its origin '{name}' is no longer an object of the Hertzon design, so the edit cannot be traced to it.");
                    continue;
                }
                PatchObject(target, opened, edited, name);
            }
            else AppendObject(edited, who);
        }
        foreach (string gone in before.Keys.Where(k => !after.ContainsKey(k)))
        {
            Note($"Object '{gone}' was removed with the operation that used it.");
            DropObjects([gone]);
        }
    }

    private void AppendObject(HznJsonObject written, string who)
    {
        string name = written.GetString("name") ?? "";
        if (ObjectNameTaken(name))
        {
            Refuse($"'{who}': the name '{name}' is already an object of the Hertzon design (a port's sheet, or an object this save cannot see); rename it.");
            return;
        }
        ObjectsJson.Add(HznJsonOps.Clone(written));
        _changed = true;
    }

    // ── added ─────────────────────────────────────────────────────────────────────────────────────

    private void AddObject(string name)
    {
        var we = Write(Mini(_e, [name]), $"Object '{name}'", opened: false);
        if (we is null) return;
        foreach (string note in we.Notes.Where(n => n.Contains("construction geometry", StringComparison.Ordinal))) Note(note);
        foreach (var written in HznJsonOps.Objects(we.Json!.GetArray("objects"))) AppendObject(written, name);
        foreach (var boundary in HznJsonOps.Objects(we.Json.GetArray("boundaries"))) MergeBoundary(boundary);
    }

    /// <summary>A boundary a new object brings (the metal of a sheet): joined to an existing boundary of the same kind and
    /// properties when there is one, else appended under a name not yet taken.</summary>
    private void MergeBoundary(HznJsonObject written)
    {
        var boundaries = Array("boundaries");
        var names = HznJsonOps.Strings(written.GetArray("objects"));
        foreach (var existing in HznJsonOps.Objects(boundaries))
        {
            if (existing.GetString("kind") != written.GetString("kind") || existing.GetBool("enabled") == false) continue;
            if (existing.GetArray("objects") is not { Count: > 0 } objects || existing.GetArray("faces") is { Count: > 0 }) continue;
            if (!HznJsonOps.Same(existing["props"], written["props"])) continue;
            foreach (string n in names) objects.Add(HznJsonScalar.String(n));
            _changed = true;
            return;
        }
        string unique = Unique(written.GetString("name") ?? "Boundary", n => HznJsonOps.ByName(boundaries, n) is not null);
        var copy = (HznJsonObject)HznJsonOps.Clone(written);
        copy.SetString("name", unique);
        boundaries.Add(copy);
        _changed = true;
    }

    // ── deleted ───────────────────────────────────────────────────────────────────────────────────

    private void DeleteObject(string name)
    {
        string origin = _prov.Objects.TryGetValue(name, out var o) ? o.HznObject : name;
        var names = new List<string> { origin };
        void Tools(string hzn)
        {
            if (!_prov.Objects.TryGetValue(hzn, out var found)) return;
            foreach (string tool in found.Consumed)
                if (!names.Contains(tool)) { names.Add(tool); Tools(tool); }
        }
        Tools(name);
        Tools(origin);
        if (!names.Any(n => HznJsonOps.ByName(_o.GetArray("objects"), n) is not null))
        {
            Refuse($"Object '{name}' was deleted, but the Hertzon design no longer has the object '{origin}' it came from.");
            return;
        }
        DropObjects(names);
    }

    /// <summary>Removes objects, and with them what names only them. Hertzon-only content that names one is NOT edited: it is listed.</summary>
    private void DropObjects(IReadOnlyList<string> names)
    {
        var objects = ObjectsJson;
        foreach (string n in names)
            if (HznJsonOps.IndexOfName(objects, n) is int i and >= 0) { objects.RemoveAt(i); _changed = true; }
        Scrub("boundaries", "Boundary", names);
        Scrub("excitations", "Excitation", names);
        HertzonOnlyNaming(names, "object");
    }

    private void Scrub(string key, string word, IReadOnlyList<string> names)
    {
        if (_o.GetArray(key) is not { } array) return;
        for (int i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is not HznJsonObject item) continue;
            var touched = new List<string>();
            if (item.GetArray("objects") is { } objects)
                for (int k = objects.Count - 1; k >= 0; k--)
                    if (objects[k] is HznJsonScalar { StringValue: { } s } && names.Contains(s)) { objects.RemoveAt(k); touched.Add(s); }
            if (item.GetArray("faces") is { } faces)
                for (int k = faces.Count - 1; k >= 0; k--)
                    if (faces[k] is HznJsonObject f && f.GetString("obj") is { } s && names.Contains(s)) { faces.RemoveAt(k); touched.Add(s); }
            if (touched.Count == 0) continue;
            _changed = true;
            string label = $"{word} '{item.GetString("name")}'" + (item.GetString("kind") is { } kind ? $" ({kind})" : "");
            bool left = (item.GetArray("objects")?.Count ?? 0) + (item.GetArray("faces")?.Count ?? 0) + (item.GetArray("edges")?.Count ?? 0) > 0;
            if (left)
            {
                Note($"{label} no longer names {string.Join(", ", touched.Distinct().Select(HznJsonOps.Quoted))}.");
                continue;
            }
            array.RemoveAt(i);
            Note($"{label} was removed: it named only {string.Join(", ", touched.Distinct().Select(HznJsonOps.Quoted))}.");
        }
    }

    private static readonly HashSet<string> EditedKeys = new(StringComparer.Ordinal)
    { "name", "units", "kind", "settings", "variables", "objects", "boundaries", "excitations", "setups", "working_cs" };

    private static string Singular(string key) => key switch
    {
        "reports" => "report", "field_plots" => "field plot", "optimetrics" => "optimization", "parametrics" => "parametric sweep",
        "mesh_ops" => "mesh operation", "spheres" => "far-field sphere", "field_exprs" => "field expression", "field_funcs" => "field function",
        "coord_systems" => "coordinate system", "notes" => "note", _ => key,
    };

    /// <summary>The Hertzon-only content of the design (a report, a field plot, an optimization goal, ...) that names one of
    /// <paramref name="names"/>. A save does not edit it; each is listed so the user knows what Hertzon will flag.</summary>
    private void HertzonOnlyNaming(IReadOnlyList<string> names, string what)
    {
        foreach (string key in _o.Keys.Where(k => !EditedKeys.Contains(k)).ToList())
        {
            var value = _o[key];
            if (value is null) continue;
            if (value is HznJsonArray array)
            {
                foreach (var item in array.Items)
                {
                    string? hit = names.FirstOrDefault(n => HznJsonOps.AllStrings(item).Any(t => HznJsonOps.NamesIdentifier(t, n)));
                    if (hit is null) continue;
                    string? itemName = (item as HznJsonObject)?.GetString("name");
                    Note($"{Singular(key)}{(itemName is null ? "" : $" '{itemName}'")} names the deleted {what} '{hit}'; it was not edited, and Hertzon will flag it.");
                }
            }
            else
            {
                string? hit = names.FirstOrDefault(n => HznJsonOps.AllStrings(value).Any(t => HznJsonOps.NamesIdentifier(t, n)));
                if (hit is not null) Note($"{Singular(key)} names the deleted {what} '{hit}'; it was not edited, and Hertzon will flag it.");
            }
        }
    }
}
