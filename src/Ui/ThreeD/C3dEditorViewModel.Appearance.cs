// brief-em3d-108 — appearance, edited where it lives, seen live.
//
// R-em3d108-1d — THE DISPLAY-ONLY RELOAD. A technology (or a library it names) changes; until now every open 3D view re-elaborated.
// When the technology as it is NOW equals the one the scene was elaborated with in its canonical physics form (C3dRunDocument.
// PhysicsText — every material's Source, Color and Appearance cleared), nothing a solver reads moved: the kept elaboration is
// re-assembled into a scene with the new technology (Build's second half, Assemble), so the looks are resolved again and a changed
// Color recolours vertices — a buffer patch — with no elaboration and no tessellation (every primitive is in the cache). Any physical
// change, a build already in flight, a pushed-in frame or a boolean's preview takes today's path. A result stays current either way,
// since a run's manifest hashes the technology in the same physics form (brief 105 §5).
//
// R-em3d108-2 — THE INSPECTOR'S APPEARANCE GROUP writes ONE FIELD of each selected object's or instance's override, as one undo entry
// (C3dEditSlot, the Transparency row's). A slider drag previews with no write: the viewer restyles its scene under an override map
// (Viewer3DViewModel.PreviewObjectAppearances) — a slot re-intern and a shade-stream patch — and the release writes once.

using System.Collections.Concurrent;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Render.Scene3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    // ── R-em3d108-1d — the display-only reload ────────────────────────────────────────────────────────────────────────

    /// <summary>What one build elaborated and resolved, kept so the scene can be assembled again with another technology.
    /// <paramref name="Eligible"/> is false for a pushed-in frame or a boolean's or fillet's preview, which take today's path.</summary>
    private sealed record C3dDisplayRebuild(C3dDocument Document, C3dSceneInputs Inputs, C3dElaboration Elaboration, RecordsView Records,
                                            bool Eligible);

    private readonly ConcurrentDictionary<long, C3dDisplayRebuild> _built = new();
    private C3dDisplayRebuild? _adoptedBuild;
    private TechnologyCache? _technologies;

    /// <summary>A display-only rebuild asked for and not yet adopted, and its generation: a second cue arriving meanwhile (a library
    /// two technologies name raises one per technology) is compared against IT, not against the adopted scene's.</summary>
    private (C3dDisplayRebuild Build, long Generation)? _pendingDisplay;

    /// <summary>How many technology changes this view took the display-only path for (no elaboration, no tessellation).</summary>
    public int DisplayRebuilds { get; private set; }

    private void AdoptBuilt(long generation)
    {
        foreach (long old in _built.Keys.Where(k => k < generation).ToList()) _built.TryRemove(old, out _);
        if (_built.TryRemove(generation, out var b)) _adoptedBuild = b;
        if (_pendingDisplay is { } p && generation >= p.Generation) _pendingDisplay = null;
    }

    /// <summary>
    /// The workspace's cue that the technology at <paramref name="path"/> changed (a library edited, a technology saved, a live edit):
    /// the display-only path when nothing physical moved, else today's — re-elaborate.
    /// </summary>
    public void OnTechnologyChanged(string path)
    {
        if (TryDisplayOnly(path))
        {
            ScheduleSolveStatus();
            return;
        }
        OnChildChanged();
    }

    private bool TryDisplayOnly(string changedPath)
    {
        if (IsViewOnly || _technologies is null || _transparencyPreview is not null || _facePreview is not null) return false;
        // The scene the change is measured against: the adopted one, or a display-only rebuild already on its way (and nothing else
        // in flight — a document edit's build reads the technology afresh, and today's path is right for it).
        long requested = Viewer.Source.Requested;
        var basis = requested == AdoptedGeneration ? _adoptedBuild
                  : _pendingDisplay is { } p && p.Generation == requested ? p.Build : null;
        if (basis is not { Eligible: true } built) return false;
        var e = built.Elaboration;
        string full = Path.GetFullPath(changedPath);
        var olds = TechnologiesOf(e).Where(t => string.Equals(Path.GetFullPath(t.Path), full, StringComparison.OrdinalIgnoreCase))
                                    .Select(t => t.Technology).Distinct(ReferenceEqualityComparer.Instance).Cast<Technology>().ToList();
        // A technology this view never read, while everything it did read resolved: nothing here depends on it. (A design with a
        // refusal, or no technology, takes today's path: the file that changed may be the one it was waiting for.)
        if (olds.Count == 0)
            return e.Technology is not null && e.Refusals.Count == 0
                && !e.FilesRead.Any(f => string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase));
        Technology? now;
        try { now = _technologies.Get(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { return false; }
        if (now is null || olds.Any(o => !C3dRunDocument.SamePhysics(o, now))) return false;
        Technology Swap(Technology t) => olds.Contains(t) ? now : t;
        var swapped = e with
        {
            Technology = e.Technology is { } top ? Swap(top) : null,
            SolidMaterials = e.SolidMaterials.ToDictionary(kv => kv.Key, kv => (Swap(kv.Value.Technology), kv.Value.Material), StringComparer.Ordinal),
        };
        DisplayRebuilds++;
        var rebuild = built with { Elaboration = swapped };
        Viewer.RegenerateWith(rebuild);
        _pendingDisplay = (rebuild, Viewer.Source.Requested);
        return true;
    }

    /// <summary>Every technology an elaboration read, with the file it came from: the document's, and each placed cell's.</summary>
    private static IEnumerable<(string Path, Technology Technology)> TechnologiesOf(C3dElaboration e)
    {
        if (e.TechnologyPath is { } p && e.Technology is { } t) yield return (p, t);
        foreach (var (name, origin) in e.MaterialOrigins)
            if (origin.TechnologyPath is { } op && e.SolidMaterials.TryGetValue(name, out var m)) yield return (op, m.Technology);
    }

    /// <summary>Build's display-only branch: the kept elaboration, assembled again. Nothing is elaborated or tessellated.</summary>
    private Scene3DModel Rebuild(long generation, C3dDisplayRebuild d, CancellationToken ct)
    {
        EnterElaborating(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            _elaborations[generation] = d.Elaboration;
            _frameKeys[generation] = d.Inputs.Path + "|";
            _records[generation] = d.Records;
            _built[generation] = d;
            return Assemble(generation, d.Document, d.Elaboration, d.Inputs, d.Records);
        }
        finally { Monitor.Exit(_elaborating); }
    }

    // ── R-em3d108-2 — an object's or an instance's appearance, one field at a time ────────────────────────────────────────

    /// <summary>The members a slider drag is showing a value on, the field and the value; null when no drag is in progress.</summary>
    private (IReadOnlyList<int> Objects, IReadOnlyList<int> Instances, string Key, object? Value)? _appearancePreview;

    /// <summary>How many appearance previews have been drawn — a drag's ticks, which write nothing.</summary>
    public int AppearancePreviews { get; private set; }

    /// <summary>Shows <paramref name="key"/> = <paramref name="value"/> on the members without writing the document.</summary>
    public void PreviewAppearance(IReadOnlyList<int> objects, IReadOnlyList<int> instances, string key, object? value)
    {
        if (_appearancePreview is { } now && now.Key == key && Equals(now.Value, value) && now.Objects.SequenceEqual(objects)
            && now.Instances.SequenceEqual(instances))
            return;
        _appearancePreview = (objects, instances, key, value);
        AppearancePreviews++;
        Viewer.PreviewObjectAppearances(PreviewMap(objects, instances, key, value));
    }

    /// <summary>Whether a drag is previewing on exactly these members.</summary>
    public bool IsPreviewingAppearance(IReadOnlyList<int> objects, IReadOnlyList<int> instances)
        => _appearancePreview is { } p && p.Objects.SequenceEqual(objects) && p.Instances.SequenceEqual(instances);

    /// <summary>Ends a preview without writing anything: the document's looks are drawn again.</summary>
    public void EndAppearancePreview()
    {
        if (_appearancePreview is null) return;
        _appearancePreview = null;
        Viewer.PreviewObjectAppearances(null);
    }

    /// <summary>
    /// Writes <paramref name="key"/> = <paramref name="value"/> (null: not stated) into each object's and instance's own appearance,
    /// every other field as it was, as ONE undo entry, and ends any preview. A polyline and an operand inside an operation have none and
    /// are skipped. Returns the refusal for a value <c>check</c> would refuse, or null.
    /// </summary>
    public string? SetAppearance(IReadOnlyList<int> objects, IReadOnlyList<int> instances, string key, object? value, string description)
        => WriteAppearance(objects, instances, a => TechAppearance.With(a, key, value), description);

    /// <summary>Clear override: each member's own appearance removed, so it looks as its material does.</summary>
    public string? ClearAppearance(IReadOnlyList<int> objects, IReadOnlyList<int> instances, string description)
        => WriteAppearance(objects, instances, _ => null, description);

    private string? WriteAppearance(IReadOnlyList<int> objects, IReadOnlyList<int> instances, Func<TechAppearance?, TechAppearance?> edit,
                                    string description)
    {
        bool previewed = _appearancePreview is not null;
        var slots = new List<C3dEditSlot>();
        foreach (int i in objects.Where(i => !IsOperandIndex(i) && i >= 0 && i < Document.Objects.Count).Distinct().Order())
        {
            if (Document.Objects[i] is C3dPolyline) continue;
            var next = edit(Document.Objects[i].Appearance);
            if (MaterialValidation.AppearanceFaults(next).FirstOrDefault() is { } fault) return $"The appearance's {fault}.";
            string before = C3dPersistence.SerializeObject(Document.Objects[i]);
            var copy = C3dPersistence.DeserializeObject(before);
            copy.Appearance = next;
            string after = C3dPersistence.SerializeObject(copy);
            if (after != before) slots.Add(new C3dEditSlot(false, i, before, after));
        }
        foreach (int i in instances.Where(i => i >= 0 && i < Document.Instances.Count).Distinct().Order())
        {
            var next = edit(Document.Instances[i].Appearance);
            if (MaterialValidation.AppearanceFaults(next).FirstOrDefault() is { } fault) return $"The appearance's {fault}.";
            string before = C3dPersistence.SerializeInstance(Document.Instances[i]);
            var copy = C3dPersistence.DeserializeInstance(before);
            copy.Appearance = next;
            string after = C3dPersistence.SerializeInstance(copy);
            if (after != before) slots.Add(new C3dEditSlot(true, i, before, after));
        }
        _appearancePreview = null;
        if (slots.Count > 0)
        {
            // the preview stands until the scene built from the write arrives, which shows the same look
            Viewer.EndAppearancePreviewAtNextScene();
            Push(new C3dEdit(description, slots, ApplySlots));
        }
        else if (previewed) Viewer.PreviewObjectAppearances(null);   // the drag came back to where it started
        return null;
    }

    /// <summary>
    /// The override map a preview draws with: for a scene object made from a targeted document object, its own statement with the
    /// field set; for one inside a targeted instance, that instance's statement (its outermost) with the field set — every other
    /// statement as the document has it.
    /// </summary>
    private Func<string, AppearanceOverride?, AppearanceOverride?> PreviewMap(IReadOnlyList<int> objects, IReadOnlyList<int> instances,
                                                                             string key, object? value)
    {
        var provenance = Elaboration?.Provenance ?? new Dictionary<string, C3dProvenance>();
        var own = objects.Where(i => i >= 0 && i < Document.Objects.Count)
                         .ToDictionary(i => Document.Objects[i].Name, i => TechAppearance.With(Document.Objects[i].Appearance, key, value), StringComparer.Ordinal);
        var outer = instances.Where(i => i >= 0 && i < Document.Instances.Count)
                             .ToDictionary(i => Document.Instances[i].Name, i => TechAppearance.With(Document.Instances[i].Appearance, key, value), StringComparer.Ordinal);
        return (name, current) =>
        {
            if (!provenance.TryGetValue(name, out var p)) return current;
            if (p.InstancePath.Length == 0)
            {
                if (!own.TryGetValue(p.TopObject ?? p.ObjectName, out var mine)) return current;
                return Override(mine, current?.Instances ?? []);
            }
            string top = p.InstancePath.Split('/', '[')[0];
            if (!outer.TryGetValue(top, out var stated)) return current;
            var statements = (current?.Instances ?? []).Where(s => s.Instance != top).ToList();
            if (stated is not null) statements.Add(new AppearanceInstanceStatement(top, stated));   // the outermost comes last
            return Override(current?.Object, statements);
        };

        static AppearanceOverride? Override(TechAppearance? obj, IReadOnlyList<AppearanceInstanceStatement> instances)
            => obj is null && instances.Count == 0 ? null : new AppearanceOverride(obj, instances);
    }

    /// <summary>The materials an appearance's <c>Like</c> may name: every material the document's technology resolves.</summary>
    public IReadOnlyList<string> AppearanceLikeChoices
        => [.. (Elaboration?.Technology?.ResolvedMaterials ?? []).Select(m => m.Name).Order(StringComparer.OrdinalIgnoreCase)];
}
