// HZN-5 R-hzn5-2 / R-hzn5-3 — what an edited design changes in the `.hzn` it was opened from, and nothing else.
//
// A save rewrites ONLY WHAT WAS EDITED. The document that was opened (the baseline) and the document as edited are both written
// through brief 4's writer, one item at a time, and the DIFFERENCE between the two writings is applied to the original JSON:
//
//   - an item that did not change is never looked at again, so its bytes are the file's own;
//   - a property whose written text is the same before and after keeps the original text (an expression stays what it was);
//   - a step whose kind is the same is edited in place; a history whose shape changed is extended, cut or, when no step edit
//     expresses it, rewritten, and only when nothing in it would be lost.
//
// When the way back to the original is lost (an edit that cannot be traced, a step the writer cannot write, a face that cannot be
// found again) the save is REFUSED with every reason, never done by rewriting the whole design (brief-hzn-0, risk for brief 5).
//
// Clean room (brief-hzn-0 D1): what each property means is from Hertzon's documentation, its example files and `hertzon-cli
// describe`; src/Design/RESOLVED.md §HZN records each meaning relied on.

using System.Text.Json;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>What a save needs of the document as it was opened.</summary>
/// <param name="Document">The document as opened (or as last saved): the edits are measured against it.</param>
/// <param name="Provenance">Where each of its items came from in the file (HZN-2).</param>
/// <param name="Technology">The materials the document's objects name: the project's own, held for the document.</param>
public sealed record HznSaveBaseline(C3dDocument Document, HznProvenance Provenance, Technology Technology);

/// <summary>What <see cref="HznPatcher.Apply"/> did to the project it was given.</summary>
/// <param name="Ok">False when the save must be refused; the project is then partly edited and is to be thrown away.</param>
/// <param name="Refusals">Every reason, one sentence each.</param>
/// <param name="Notes">What the save did that the user should know: a boundary removed with its object, a report that now names
/// nothing.</param>
/// <param name="Changed">True when anything in the project was edited.</param>
/// <param name="Readback">The edited project as circuitRF reads it, whose provenance is the baseline of the next save.</param>
public sealed record HznPatchOutcome(bool Ok, IReadOnlyList<string> Refusals, IReadOnlyList<string> Notes, bool Changed, C3dTranslation? Readback);

public static class HznPatcher
{
    /// <summary>
    /// Edits <paramref name="project"/> (a COPY of the file's tree) so that it holds <paramref name="edited"/>'s changes to design
    /// <paramref name="design"/>, and nothing else. Reads no file.
    /// </summary>
    public static HznPatchOutcome Apply(HznProject project, string design, HznSaveBaseline baseline, C3dDocument edited,
                                        GeometryKernel? kernel = null) =>
        new HznPatch(project, design, baseline, edited, kernel).Run();

    /// <summary>
    /// The edits <paramref name="edited"/> holds that a save would write, one line per kind, by name: what a dialog names before it
    /// asks whether to discard them. Compares documents only; reads no file and writes nothing.
    /// </summary>
    public static IReadOnlyList<string> Summarise(HznSaveBaseline baseline, C3dDocument edited) => HznPatch.Summary(baseline.Document, edited);
}

internal sealed partial class HznPatch
{
    private readonly HznProject _project;
    private readonly string _design;
    private readonly HznSaveBaseline _base;
    private readonly C3dDocument _b;          // the baseline document
    private readonly C3dDocument _e;          // the edited document, a clone
    private readonly C3dDocument _editedAsGiven;   // …and as given: the resolved numbers of its bound fields are still in it
    private readonly HznProvenance _prov;
    private readonly GeometryKernel? _kernel;
    private readonly HznJsonObject _o;        // the design's JSON, edited in place
    private readonly List<string> _refusals = [];
    private readonly List<string> _notes = [];
    private bool _changed;

    public HznPatch(HznProject project, string design, HznSaveBaseline baseline, C3dDocument edited, GeometryKernel? kernel)
    {
        _project = project;
        _design = design;
        _base = baseline;
        _b = baseline.Document;
        _prov = baseline.Provenance;
        _kernel = kernel;
        _e = Clone(edited);
        _editedAsGiven = edited;
        _o = project.Designs.First(d => string.Equals(d.Name, design, StringComparison.Ordinal)).Json;
    }

    private void Refuse(string why) { if (!_refusals.Contains(why)) _refusals.Add(why); }
    private void Note(string text) { if (!_notes.Contains(text)) _notes.Add(text); }

    public HznPatchOutcome Run()
    {
        if (_e.DbuPerMicron != _b.DbuPerMicron)
        {
            Refuse("The document's database unit changed, and a Hertzon design has no such setting to follow it.");
            return Finish(null);
        }
        _e.DisplayUnit = _b.DisplayUnit;      // a display preference: the writer spells literals in it, so it must not move the diff

        OtherDifferences();
        Variables();
        Objects();
        Ports();
        FaceBoundaries();
        Setups();
        PlaneWaves();   // brief-hzn-12
        LumpedElements();
        RemovedVariables();
        if (_refusals.Count > 0) return Finish(null);

        var readback = HznToC3d.Translate(_project, _design, _kernel);
        if (!readback.Ok)
        {
            Refuse("The design as saved would not open again, so nothing was written:\n" + HznRefusalText.Render(readback.Refusals)
                   + (readback.Error is { } err ? "\n" + err.Render() : ""));
            return Finish(null);
        }
        ReadbackGuard(readback);
        return Finish(_refusals.Count == 0 ? readback : null);
    }

    private HznPatchOutcome Finish(C3dTranslation? readback) => new(_refusals.Count == 0, [.. _refusals], [.. _notes], _changed, readback);

    // ── documents ─────────────────────────────────────────────────────────────────────────────────

    private static C3dDocument Clone(C3dDocument doc) => C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));

    /// <summary>A document with only the items asked for, of <paramref name="src"/> (a clone), and every variable: what the writer
    /// is given to write one item on its own.</summary>
    private static C3dDocument Mini(C3dDocument src, IEnumerable<string>? objects = null, IEnumerable<int>? ports = null,
                                    IEnumerable<C3dFaceBoundary>? faceRows = null, IEnumerable<string>? setups = null,
                                    IEnumerable<string>? waves = null)
    {
        var d = Clone(src);
        var keepObjects = objects?.ToHashSet(StringComparer.Ordinal) ?? [];
        var keepPorts = ports?.ToHashSet() ?? [];
        var keepSetups = setups?.ToHashSet(StringComparer.Ordinal) ?? [];
        d.Objects = [.. d.Objects.Where(o => keepObjects.Contains(o.Name))];
        d.Ports = [.. d.Ports.Where(p => keepPorts.Contains(p.Number))];
        d.FaceBoundaries = [.. faceRows ?? []];
        d.Setups = [.. d.Setups.Where(s => keepSetups.Contains(Str(s, "Name")))];
        var keepWaves = waves?.ToHashSet(StringComparer.Ordinal) ?? [];
        d.PlaneWaves = [.. d.PlaneWaves.Where(w => keepWaves.Contains(w.Name))];   // brief-hzn-12
        d.Instances = [];
        d.LumpedElements = [];
        d.HeatSources = [];
        d.Probes = [];
        d.MeshRegions = [];
        d.ContactResistances = [];
        d.EffectiveBlocks = [];
        d.SymmetryPlanes = [];
        d.FieldPlots = [];
        d.WireGroundPlane = null;
        d.AirBox = null;
        d.BandSweeps = null;
        return d;
    }

    /// <summary>One item written on its own, or null with the reason recorded.</summary>
    private C3dToHznResult? Write(C3dDocument mini, string who, bool opened)
    {
        var r = C3dToHzn.Write(mini, new HznExportOptions { DesignName = "Part", Technology = _base.Technology, Kernel = _kernel });
        if (r.Ok) return r;
        Refuse($"{who}: " + (opened
            ? "an origin this save would rewrite has a step that cannot be written to a Hertzon project, so nothing was written.\n"
            : "the edited item cannot be written to a Hertzon project, so nothing was written.\n") + HznRefusalText.Render(r.Refusals));
        return null;
    }

    // ── the document's sections, compared as the file would spell them ────────────────────────────

    private sealed record Section(IReadOnlyList<string> Order, IReadOnlyDictionary<string, string> Raw);

    private static Section Read(C3dDocument doc, string property, Func<JsonElement, string> key)
    {
        using var json = JsonDocument.Parse(C3dPersistence.Serialize(doc));
        var order = new List<string>();
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in json.RootElement.EnumerateObject())
        {
            if (!string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in p.Value.EnumerateArray())
            {
                string k = key(item);
                if (raw.TryAdd(k, item.GetRawText())) order.Add(k);
            }
        }
        return new Section(order, raw);
    }

    private static string Str(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        return "";
    }

    private sealed record Delta(IReadOnlyList<string> Removed, IReadOnlyList<string> Added, IReadOnlyList<string> Changed);

    private static Delta Compare(Section before, Section after) => new(
        [.. before.Order.Where(k => !after.Raw.ContainsKey(k))],
        [.. after.Order.Where(k => !before.Raw.ContainsKey(k))],
        [.. before.Order.Where(k => after.Raw.TryGetValue(k, out string? t) && t != before.Raw[k])]);

    // ── what is not saved ─────────────────────────────────────────────────────────────────────────

    /// <summary>Document-level state. The sections a save handles are skipped; the display state is not the design's; everything
    /// else that moved is a difference this save would drop in silence, so it is a refusal.</summary>
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    { "Objects", "Variables", "Ports", "FaceBoundaries", "Setups", "LumpedElements", "PlaneWaves" };

    private static readonly HashSet<string> Display = new(StringComparer.OrdinalIgnoreCase)
    { "DisplayUnit", "SnapDbu", "FieldPlots", "AirBoxHidden", "ActiveSetup", "Look", "FormatVersion", "TechRef" };

    private void OtherDifferences()
    {
        using var b = JsonDocument.Parse(C3dPersistence.Serialize(_b));
        using var e = JsonDocument.Parse(C3dPersistence.Serialize(_e));
        var before = b.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
        var after = e.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
        foreach (string name in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (Handled.Contains(name) || Display.Contains(name)) continue;
            before.TryGetValue(name, out string? x);
            after.TryGetValue(name, out string? y);
            if (x != y)
                Refuse($"'{name}' changed, and a Hertzon project has no place that this save writes it to. " +
                       "Undo that change, or Save As a .c3d.");
        }
    }

    private void LumpedElements()
    {
        var d = Compare(Read(_b, "LumpedElements", x => Str(x, "Name")), Read(_e, "LumpedElements", x => Str(x, "Name")));
        foreach (string n in d.Removed.Concat(d.Added).Concat(d.Changed).Distinct())
            Refuse($"Lumped element '{n}' changed; lumped elements are boundaries in a Hertzon design, and this save does not write them yet.");
    }

    // ── the project's own copy ────────────────────────────────────────────────────────────────────

    private HznJsonArray Array(string key)
    {
        if (_o.GetArray(key) is { } a) return a;
        var fresh = new HznJsonArray();
        _o.Set(key, fresh);
        return fresh;
    }

    private HznJsonArray ObjectsJson => Array("objects");

    private bool ObjectNameTaken(string name) => HznJsonOps.ByName(_o.GetArray("objects"), name) is not null;

    private static string Unique(string wanted, Func<string, bool> taken)
    {
        string name = wanted;
        for (int i = 2; taken(name); i++) name = wanted + "_" + i;
        return name;
    }
}
