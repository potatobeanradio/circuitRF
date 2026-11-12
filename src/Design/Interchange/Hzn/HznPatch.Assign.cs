// HZN-5 R-hzn5-2 — variables, ports, face boundaries and setups: each edited through the same before-and-after writing the
// objects are, and traced to the item of the file it came from by the provenance the translation recorded (HZN-2).

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznPatch
{
    private Delta _objectDelta = new([], [], []);

    // ── variables ─────────────────────────────────────────────────────────────────────────────────

    private Delta _variableDelta = new([], [], []);

    private HznStringMap DesignVariables => new(_o, "variables");

    private void Variables()
    {
        _variableDelta = Compare(Read(_b, "Variables", x => Str(x, "Name")), Read(_e, "Variables", x => Str(x, "Name")));
        var names = _variableDelta.Changed.Concat(_variableDelta.Added).ToList();
        if (names.Count == 0) return;
        var wb = Write(Mini(_b), "Variables", opened: true);
        var we = Write(Mini(_e), "Variables", opened: false);
        if (wb is null || we is null) return;
        var before = wb.Json!.GetObject("variables");
        var after = we.Json!.GetObject("variables");
        foreach (string name in names)
        {
            string? project = HznNames.HznProjectName(name);
            string hzn = project ?? name;
            if (after?[hzn] is not HznJsonScalar { StringValue: { } text })
            {
                Refuse($"Variable '{name}': it cannot be written to the Hertzon design.");
                continue;
            }
            if (_variableDelta.Changed.Contains(name) && before?[hzn] is HznJsonScalar { StringValue: { } was } && was == text) continue;
            var map = project is not null ? _project.Variables : DesignVariables;
            if (project is null && _variableDelta.Added.Contains(name) && map.Contains(hzn))
            {
                Refuse($"Variable '{name}': the Hertzon design already has a variable of that name that this document does not hold.");
                continue;
            }
            map[hzn] = text;
            _changed = true;
        }
    }

    private void RemovedVariables()
    {
        foreach (string name in _variableDelta.Removed)
        {
            if (HznNames.HznProjectName(name) is { } project)
            {
                Note($"Project variable '{project}' was left in the project: other designs of it may use it.");
                continue;
            }
            if (DesignVariables.Remove(name)) _changed = true;
            HertzonOnlyNaming([name], "variable");
        }
    }

    // ── ports ─────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> ContactNames(C3dPort p) =>
        new[] { p.Positive, p.Negative }.Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("airbox/", StringComparison.Ordinal))
                                        .Select(n => n!).Distinct(StringComparer.Ordinal);

    private C3dDocument PortMini(C3dDocument src, int number)
    {
        var port = src.Ports.First(p => p.Number == number);
        var objects = ContactNames(port).Where(n => src.Objects.Any(o => o.Name == n));
        return Mini(src, objects, [number]);
    }

    private void Ports()
    {
        var delta = Compare(Read(_b, "Ports", x => Str(x, "Number")), Read(_e, "Ports", x => Str(x, "Number")));
        foreach (string n in delta.Removed) DeletePort(int.Parse(n));
        foreach (string n in delta.Changed) ChangePort(int.Parse(n));
        foreach (string n in delta.Added) AddPort(int.Parse(n));
    }

    private HznPortOrigin? OriginOf(int number) => _prov.Ports.FirstOrDefault(p => p.Number == number);

    private void ChangePort(int number)
    {
        string who = $"Port {number}";
        if (OriginOf(number) is not { } origin)
        {
            Refuse($"{who}: the edit cannot be traced to an excitation of the Hertzon design.");
            return;
        }
        var wb = Write(PortMini(_b, number), who, opened: true);
        var we = Write(PortMini(_e, number), who, opened: false);
        if (wb is null || we is null) return;
        var opened = HznJsonOps.Objects(wb.Json!.GetArray("excitations")).FirstOrDefault();
        var edited = HznJsonOps.Objects(we.Json!.GetArray("excitations")).FirstOrDefault();
        if (opened is null || edited is null) { Refuse($"{who}: it is not written as an excitation."); return; }
        if (HznJsonOps.ByName(_o.GetArray("excitations"), origin.Excitation) is not { } target)
        {
            Refuse($"{who}: the excitation '{origin.Excitation}' it came from is no longer in the Hertzon design.");
            return;
        }
        PatchAssignment(target, opened, edited);

        var sheetBefore = ByName(wb.Json.GetArray("objects")).GetValueOrDefault(HznJsonOps.Strings(opened.GetArray("objects")).FirstOrDefault() ?? "");
        var sheetAfter = ByName(we.Json.GetArray("objects")).GetValueOrDefault(HznJsonOps.Strings(edited.GetArray("objects")).FirstOrDefault() ?? "");
        if (sheetBefore is null || sheetAfter is null || HznJsonOps.Same(sheetBefore["history"], sheetAfter["history"])) return;
        if (origin.SheetObject is not { } sheet)
        {
            Refuse($"{who} was made from a face of a solid, which is where its rectangle comes from in Hertzon; moving or resizing it is not saved. " +
                   "Edit the face's object, or Save As a .c3d.");
            return;
        }
        if (HznJsonOps.ByName(_o.GetArray("objects"), sheet) is not { } sheetTarget)
        {
            Refuse($"{who}: its sheet '{sheet}' is no longer an object of the Hertzon design.");
            return;
        }
        PatchObject(sheetTarget, sheetBefore, sheetAfter, sheet);
        if (target.GetObject("props")?.ContainsKey("IntegrationLine") == true && !(edited.GetObject("props")?.ContainsKey("IntegrationLine") ?? false))
            Note($"{who} moved or was resized; its integration line was not changed. Check it in Hertzon.");
    }

    /// <summary>A boundary or excitation, edited: its kind and enabled switch, then the properties whose written text moved.</summary>
    private void PatchAssignment(HznJsonObject target, HznJsonObject opened, HznJsonObject edited)
    {
        foreach (string key in edited.Keys.ToList())
        {
            if (key is "name" or "objects" or "faces" or "edges" or "props") continue;
            if (HznJsonOps.Same(opened[key], edited[key])) continue;
            target.Set(key, HznJsonOps.Clone(edited[key]!));
            _changed = true;
        }
        var before = opened.GetObject("props");
        var after = edited.GetObject("props");
        var props = target.GetObject("props");
        if (after is not null)
            foreach (string key in after.Keys.ToList())
            {
                if (before?.ContainsKey(key) == true && HznJsonOps.Same(before[key], after[key])) continue;
                if (props is null) target.Set("props", props = new HznJsonObject());
                props.Set(key, HznJsonOps.Clone(after[key]!));
                _changed = true;
            }
        if (before is not null && props is not null)
            foreach (string key in before.Keys.ToList())
                if (after?.ContainsKey(key) != true && props.Remove(key)) _changed = true;
    }

    private void AddPort(int number)
    {
        string who = $"Port {number}";
        var we = Write(PortMini(_e, number), who, opened: false);
        if (we is null) return;
        var excitation = HznJsonOps.Objects(we.Json!.GetArray("excitations")).FirstOrDefault();
        if (excitation is null) { Refuse($"{who}: it is not written as an excitation."); return; }
        var sheets = ByName(we.Json.GetArray("objects"));
        var sheetNames = HznJsonOps.Strings(excitation.GetArray("objects"));
        var copy = (HznJsonObject)HznJsonOps.Clone(excitation);
        var names = new HznJsonArray();
        foreach (string sheetName in sheetNames)
        {
            if (!sheets.TryGetValue(sheetName, out var sheet)) continue;
            string unique = Unique(sheetName, ObjectNameTaken);
            var sheetCopy = (HznJsonObject)HznJsonOps.Clone(sheet);
            sheetCopy.SetString("name", unique);
            ObjectsJson.Add(sheetCopy);
            names.Add(HznJsonScalar.String(unique));
        }
        copy.Set("objects", names);
        var excitations = Array("excitations");
        copy.SetString("name", Unique(excitation.GetString("name") ?? $"P{number}", n => HznJsonOps.ByName(excitations, n) is not null));
        excitations.Add(copy);
        _changed = true;
    }

    private void DeletePort(int number)
    {
        string who = $"Port {number}";
        if (OriginOf(number) is not { } origin)
        {
            Refuse($"{who} was deleted, but it cannot be traced to an excitation of the Hertzon design.");
            return;
        }
        var excitations = _o.GetArray("excitations");
        int i = excitations is null ? -1 : HznJsonOps.IndexOfName(excitations, origin.Excitation);
        if (i < 0)
        {
            Refuse($"{who} was deleted, but the excitation '{origin.Excitation}' it came from is no longer in the Hertzon design.");
            return;
        }
        excitations!.RemoveAt(i);
        _changed = true;
        Note($"Excitation '{origin.Excitation}' was removed with {who}" + (origin.SheetObject is { } s ? $" and its sheet '{s}'." : "."));
        if (origin.SheetObject is { } sheet) DropObjects([sheet]);
        if (origin.Excitation.Any(char.IsLetter)) HertzonOnlyNaming([origin.Excitation], "excitation");
    }

    // ── face boundaries ───────────────────────────────────────────────────────────────────────────

    private string HznObjectName(string c3dName) =>
        _prov.Objects.TryGetValue(c3dName, out var o) ? o.HznObject
        : HznNames.HznObjectName(c3dName);

    private void FaceBoundaries()
    {
        var delta = Compare(Read(_b, "FaceBoundaries", x => Str(x, "Object") + "|" + Str(x, "Face")),
                            Read(_e, "FaceBoundaries", x => Str(x, "Object") + "|" + Str(x, "Face")));
        var deleted = _objectDelta.Removed.ToHashSet(StringComparer.Ordinal);

        var gone = delta.Removed.Concat(delta.Changed).Select(k => k.Split('|')).Where(p => !deleted.Contains(p[0])).ToList();
        var fresh = delta.Added.Concat(delta.Changed).Select(k => k.Split('|')).Where(p => !deleted.Contains(p[0])).ToList();
        RemoveFaceRows(gone);
        AddFaceRows(fresh);

        // A row that did not change on an object that did: the object may have moved, so the face is found again.
        var touched = delta.Removed.Concat(delta.Added).Concat(delta.Changed).ToHashSet(StringComparer.Ordinal);
        var moved = _objectDelta.Changed.Concat(_objectDelta.Added).ToHashSet(StringComparer.Ordinal);
        foreach (var origin in _prov.FaceBoundaries.Where(o => o.FaceIndex is not null && moved.Contains(o.Object)).ToList())
        {
            string key = origin.Object + "|" + origin.Face;
            if (touched.Contains(key) || !_e.FaceBoundaries.Any(r => r.Object == origin.Object && r.Face == origin.Face)) continue;
            RefindFace(origin);
        }
    }

    private void RemoveFaceRows(List<string[]> rows)
    {
        foreach (var group in rows.GroupBy(r => r[0]))
        {
            foreach (var row in group)
            {
                string who = $"Face boundary on {row[0]}.{row[1]}";
                if (_prov.FaceBoundaries.FirstOrDefault(o => o.Object == row[0] && o.Face == row[1]) is not { } origin)
                {
                    Refuse($"{who} was removed, but it cannot be traced to a boundary of the Hertzon design.");
                    continue;
                }
                if (HznJsonOps.ByName(_o.GetArray("boundaries"), origin.Boundary) is not { } boundary)
                {
                    Refuse($"{who} was removed, but the boundary '{origin.Boundary}' it came from is no longer in the Hertzon design.");
                    continue;
                }
                string hzn = HznObjectName(row[0]);
                if (origin.FaceIndex is not { } index)
                {
                    // A boundary stated on the whole object covers every face: dropping some of them cannot be said by dropping the object.
                    var siblings = _prov.FaceBoundaries.Where(o => o.Boundary == origin.Boundary && o.Object == origin.Object).Select(o => o.Face).ToList();
                    if (!siblings.All(f => rows.Any(r => r[0] == row[0] && r[1] == f)))
                    {
                        Refuse($"{who}: the boundary '{origin.Boundary}' covers the whole of '{hzn}'; removing some of its faces is not saved. " +
                               "Remove them all, or Save As a .c3d.");
                        continue;
                    }
                    DropFromBoundary(boundary, hzn, null);
                    continue;
                }
                DropFromBoundary(boundary, hzn, index);
            }
        }
    }

    private void DropFromBoundary(HznJsonObject boundary, string hznObject, long? face)
    {
        bool touched = false;
        if (face is null && boundary.GetArray("objects") is { } objects)
            for (int k = objects.Count - 1; k >= 0; k--)
                if (objects[k] is HznJsonScalar { StringValue: { } s } && s == hznObject) { objects.RemoveAt(k); touched = true; }
        if (face is { } idx && boundary.GetArray("faces") is { } faces)
            for (int k = faces.Count - 1; k >= 0; k--)
                if (faces[k] is HznJsonObject f && f.GetString("obj") == hznObject && f["face"] is HznJsonScalar n && n.TryGetInt64(out long v) && v == idx)
                { faces.RemoveAt(k); touched = true; }
        if (!touched) return;
        _changed = true;
        bool left = (boundary.GetArray("objects")?.Count ?? 0) + (boundary.GetArray("faces")?.Count ?? 0) + (boundary.GetArray("edges")?.Count ?? 0) > 0;
        if (left) return;
        var boundaries = _o.GetArray("boundaries")!;
        boundaries.RemoveAt(HznJsonOps.IndexOfName(boundaries, boundary.GetString("name") ?? ""));
        Note($"Boundary '{boundary.GetString("name")}' ({boundary.GetString("kind")}) was removed: it named only '{hznObject}'.");
    }

    private void AddFaceRows(List<string[]> rows)
    {
        foreach (var group in rows.GroupBy(r => r[0]))
        {
            var chosen = _e.FaceBoundaries.Where(r => r.Object == group.Key && group.Any(g => g[1] == r.Face)).ToList();
            if (chosen.Count == 0) continue;
            var we = Write(Mini(_e, [group.Key], faceRows: chosen), $"Face boundary on '{group.Key}'", opened: false);
            if (we is null) continue;
            foreach (var written in HznJsonOps.Objects(we.Json!.GetArray("boundaries")))
            {
                if (written.GetArray("faces") is { Count: > 0 })
                {
                    var boundaries = Array("boundaries");
                    var copy = (HznJsonObject)HznJsonOps.Clone(written);
                    copy.SetString("name", Unique(written.GetString("name") ?? "Boundary", n => HznJsonOps.ByName(boundaries, n) is not null));
                    boundaries.Add(copy);
                    _changed = true;
                }
                else MergeBoundary(written);
            }
        }
    }

    /// <summary>A face the file names by index and signature, found again on the edited object: the signature is rewritten from the
    /// geometry as it now is, and the index only if it no longer names that face.</summary>
    private void RefindFace(HznBoundaryOrigin origin)
    {
        string who = $"Face {origin.Object}.{origin.Face}";
        var row = new C3dFaceBoundary { Object = origin.Object, Face = origin.Face, Kind = Em3dFaceBoundaryKind.Pec };
        var we = Write(Mini(_e, [origin.Object], faceRows: [row]), who, opened: false);
        if (we is null) return;
        string hzn = HznObjectName(origin.Object);
        var frame = HznJsonOps.Objects(we.Json!.GetArray("boundaries"))
                              .SelectMany(b => HznJsonOps.Objects(b.GetArray("faces")))
                              .FirstOrDefault(f => f.GetString("obj") == hzn);
        if (frame is null || frame["face"] is not HznJsonScalar { } newIndex || frame.GetArray("sig") is not { } newSig)
        {
            Refuse($"{who} is named by index in the Hertzon design, and the edited object's face could not be found again to rewrite it.");
            return;
        }
        if (HznJsonOps.ByName(_o.GetArray("boundaries"), origin.Boundary) is not { } boundary)
        {
            Refuse($"{who}: the boundary '{origin.Boundary}' it came from is no longer in the Hertzon design.");
            return;
        }
        var target = HznJsonOps.Objects(boundary.GetArray("faces"))
                               .FirstOrDefault(f => f.GetString("obj") == hzn && f["face"] is HznJsonScalar n && n.TryGetInt64(out long v) && v == origin.FaceIndex);
        if (target is null)
        {
            Refuse($"{who}: the Hertzon design no longer has that face reference on '{origin.Boundary}'.");
            return;
        }
        var oldSig = target.GetArray("sig");
        bool sameSig = oldSig is not null && oldSig.Count >= 7 && newSig.Count >= 7
            && Enumerable.Range(0, 7).All(i => oldSig[i] is HznJsonScalar a && newSig[i] is HznJsonScalar b
                && a.TryGetDouble(out double x) && b.TryGetDouble(out double y)
                && Math.Abs(x - y) <= (i < 3 ? 1e-9 : 1e-6 * Math.Max(1e-12, Math.Max(Math.Abs(x), Math.Abs(y)))));
        bool sameIndex = newIndex.TryGetInt64(out long ni) && ni == origin.FaceIndex;
        if (sameSig && sameIndex) return;
        if (!sameIndex) target.Set("face", HznJsonOps.Clone(newIndex));
        if (!sameSig)
        {
            var sig = new HznJsonArray();
            for (int i = 0; i < newSig.Count; i++) sig.Add(HznJsonOps.Clone(newSig[i]));
            if (oldSig is not null)
                for (int i = newSig.Count; i < oldSig.Count; i++) sig.Add(HznJsonOps.Clone(oldSig[i]));
            target.Set("sig", sig);
        }
        _changed = true;
    }

    // ── setups ────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] SetupKeysWritten = ["MaximumPasses", "BasisOrder"];

    private void Setups()
    {
        var before = Read(_b, "Setups", x => Str(x, "Name"));
        var after = Read(_e, "Setups", x => Str(x, "Name"));
        var delta = Compare(before, after);
        foreach (string n in delta.Removed) Refuse($"Setup '{n}' was removed; setups of a Hertzon design are edited in place by this save, not added or removed.");
        foreach (string n in delta.Added) Refuse($"Setup '{n}' was added; setups of a Hertzon design are edited in place by this save, not added or removed.");
        foreach (string n in delta.Changed)
        {
            if (AirBoxOf(before.Raw[n]) != AirBoxOf(after.Raw[n]))
            {
                Refuse($"Setup '{n}': its air box changed, and a Hertzon design states its air box as a region object; that edit is not saved. " +
                       "Undo it, or Save As a .c3d.");
                continue;
            }
            ChangeSetup(n);
        }
    }

    private static string AirBoxOf(string setupJson)
    {
        using var d = System.Text.Json.JsonDocument.Parse(setupJson);
        foreach (var p in d.RootElement.EnumerateObject())
            if (string.Equals(p.Name, "AirBox", StringComparison.OrdinalIgnoreCase)) return p.Value.GetRawText();
        return "";
    }

    private void ChangeSetup(string name)
    {
        string who = $"Setup '{name}'";
        if (!_prov.Setups.TryGetValue(name, out string? origin))
        {
            Refuse($"{who}: the edit cannot be traced to a setup of the Hertzon design.");
            return;
        }
        var wb = Write(Mini(_b, setups: [name]), who, opened: true);
        var we = Write(Mini(_e, setups: [name]), who, opened: false);
        if (wb is null || we is null) return;
        var opened = HznJsonOps.Objects(wb.Json!.GetArray("setups")).FirstOrDefault();
        var edited = HznJsonOps.Objects(we.Json!.GetArray("setups")).FirstOrDefault();
        if (opened is null || edited is null) { Refuse($"{who} is not written as a setup."); return; }

        string setupName = origin.Contains('/') ? origin[..origin.IndexOf('/')] : origin;
        string? sweepName = origin.Contains('/') ? origin[(origin.IndexOf('/') + 1)..] : null;
        if (HznJsonOps.ByName(_o.GetArray("setups"), setupName) is not { } target)
        {
            Refuse($"{who}: the setup '{setupName}' it came from is no longer in the Hertzon design.");
            return;
        }
        var targetProps = target.GetObject("props");
        var beforeProps = opened.GetObject("props");
        var afterProps = edited.GetObject("props");
        foreach (string key in SetupKeysWritten)
        {
            if (afterProps?[key] is null || HznJsonOps.Same(beforeProps?[key], afterProps[key])) continue;
            if (targetProps is null) target.Set("props", targetProps = new HznJsonObject());
            targetProps.Set(key, HznJsonOps.Clone(afterProps[key]!));
            _changed = true;
        }

        var sweepBefore = HznJsonOps.Objects(opened.GetArray("sweeps")).FirstOrDefault();
        var sweepAfter = HznJsonOps.Objects(edited.GetArray("sweeps")).FirstOrDefault();
        if (sweepBefore is null || sweepAfter is null)
        {
            if (!HznJsonOps.Same(opened["sweeps"], edited["sweeps"]))
                Refuse($"{who}: a frequency sweep was added or removed; this save edits a sweep in place only.");
            return;
        }
        if (HznJsonOps.Same(sweepBefore, sweepAfter)) return;
        if (sweepName is null || HznJsonOps.ByName(target.GetArray("sweeps"), sweepName) is not { } sweep)
        {
            Refuse($"{who}: its frequencies came from the setup alone, with no sweep of the Hertzon design to edit.");
            return;
        }
        PatchAssignment(sweep, sweepBefore, sweepAfter);
    }

    // ── readback ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The design as saved is read once more, and compared with the document that was edited: every object it should hold
    /// is there, the ones that changed read back the same, and no port was lost to a boundary that went with a deleted object.</summary>
    private void ReadbackGuard(C3dTranslation readback)
    {
        var p = readback.Document!;
        var expected = _e.Objects.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var got = p.Objects.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(got).Where(n => !HasDisabledBoolean(_e.Objects.First(o => o.Name == n))).ToList();
        var extra = got.Except(expected).ToList();
        if (missing.Count > 0) Refuse($"The saved design would open without {string.Join(", ", missing.Select(HznJsonOps.Quoted))}, so nothing was written.");
        if (extra.Count > 0 && _e.Objects.All(o => !HasDisabledBoolean(o)))
            Refuse($"The saved design would open with {string.Join(", ", extra.Select(HznJsonOps.Quoted))}, which the edited document does not hold, so nothing was written.");
        var lost = _e.Ports.Select(x => x.Number).Except(p.Ports.Select(x => x.Number)).ToList();
        if (lost.Count > 0) Refuse($"The saved design would open without port {string.Join(", ", lost)}, so nothing was written.");

        foreach (string n in _objectDelta.Changed.Concat(_objectDelta.Added))
        {
            var mine = _editedAsGiven.Objects.FirstOrDefault(o => o.Name == n);
            var read = p.Objects.FirstOrDefault(o => o.Name == n);
            if (mine is null || read is null || HasDisabledBoolean(mine)) continue;
            if (Normalised(mine) != Normalised(read))
                Refuse($"Object '{n}' would not read back as it was edited, so nothing was written.");
        }
    }

    private static bool HasDisabledBoolean(C3dObject o) => C3dOperands.SelfAndDescendants(o).OfType<C3dBoolean>().Any(b => !b.Enabled);

    private static string Normalised(C3dObject o)
    {
        var copy = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(o));
        foreach (var part in C3dOperands.SelfAndDescendants(copy))
        {
            part.Transparency = null;
            part.Appearance = null;
            part.DrawingLayer = null;
            part.FaceImages = null;
        }
        return C3dPersistence.SerializeResolved(copy);
    }
}
