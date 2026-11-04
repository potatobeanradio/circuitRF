// brief-em3d-41 R-em3d41-3 — reading and writing a .c3d.
//
// The conventions are .clay's newer ones, because the reason is the same: a workspace's history is
// kept with git, so the file is written for a DIFF — a tab per level, LF on every platform, a point
// per line. Key order is the declaration order (JsonPropertyOrder where a base type's keys must frame
// a derived type's), nothing is ever sorted, and a field is omitted at its default only when it is a
// modifier: a placement that states nothing, a zero shear, an empty hole list. Geometry is always
// written, so a reader never has to know a default to know where a solid is.
//
// ROUND TRIP IS BYTE FOR BYTE (R-em3d41-3b): read then write gives the bytes that were read, for any
// file this writer produced. The one thing the writer changes is a NEGATIVE size, which it normalises
// by moving the corner (R-em3d41-2-dims) — in the model it is handed, so the document and the file
// agree afterwards.

using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.ThreeD;

/// <summary>A <c>.c3d</c> this build cannot read, with the diagnostic that says why.</summary>
public sealed class C3dReadException(Diagnostic diagnostic) : Exception(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>Reads and writes <c>.c3d</c> files. Framework-free.</summary>
public static class C3dPersistence
{
    public const string Extension            = ".c3d";
    public const int    CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = Options(C3dSpelling.File);

    /// <summary>brief-em3d-51 — an object's in-memory spelling (undo entries, clones): each bound component carries its last
    /// resolved number beside its expression, so a copy is where the original is before anything re-resolves it.</summary>
    private static readonly JsonSerializerOptions ItemOpts = Options(C3dSpelling.WithValues);

    /// <summary>brief-em3d-51 R-em3d51-5b — the same spelling with every expression replaced by its resolved number: what
    /// the elaborator keys an object on, so changing a VAR re-elaborates exactly the objects whose values changed.</summary>
    private static readonly JsonSerializerOptions ResolvedOpts = Options(C3dSpelling.NumbersOnly);

    private static JsonSerializerOptions Options(C3dSpelling spelling) => new()
    {
        WriteIndented                     = true,
        IndentCharacter                   = '\t',
        IndentSize                        = 1,
        NewLine                           = "\n",
        DefaultIgnoreCondition            = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive       = true,
        // A hand-written file may put "$type" anywhere in an object. .clay requires it first and
        // refuses the whole file otherwise; nothing about a 3D view needs that trap.
        AllowOutOfOrderMetadataProperties = true,
        Converters =
        {
            new JsonStringEnumConverter(), new C3dInt64JsonConverter(), new C3dInt32JsonConverter(),
            new C3dDoubleJsonConverter(), new C3dIndexListJsonConverter(), new C3dIndexLoopsJsonConverter(),
            new C3dPoint2ListJsonConverter(), new C3dPoint3ListJsonConverter(), new C3dRingListJsonConverter(),
        },
        // brief-em3d-51 — an expression's '+' is written as a plus (C3dBindings.Encoder).
        Encoder = C3dBindings.Encoder,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { info => C3dBindings.Modify(info, spelling), OmitEmpty, PortLevel },
        },
    };

    /// <summary>
    /// A list with nothing in it, and a placement that states nothing, are left out — except a
    /// <c>[JsonRequired]</c> one, which is <see cref="C3dDocument.Objects"/>: its presence is half of
    /// how a <c>.c3d</c> is recognised by content.
    /// </summary>
    private static void OmitEmpty(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        foreach (var p in info.Properties)
        {
            if (p.IsRequired || p.IsExtensionData) continue;
            // brief-em3d-64 R-em3d64-1b — a switch whose default is not its type's (Enabled, true) is written only when it
            // differs; an operand's empty Name is left out (R-em3d64-1d).
            if (p.AttributeProvider?.GetCustomAttributes(typeof(System.ComponentModel.DefaultValueAttribute), true) is [System.ComponentModel.DefaultValueAttribute dv, ..])
            {
                object? d = dv.Value;
                p.ShouldSerialize = (_, v) => !Equals(v, d);
                continue;
            }
            if (p.Name == nameof(C3dObject.Name) && typeof(C3dObject).IsAssignableFrom(info.Type))
            {
                p.ShouldSerialize = static (_, v) => v is string { Length: > 0 };
                continue;
            }
            if (typeof(ICollection).IsAssignableFrom(p.PropertyType))
                p.ShouldSerialize = static (_, v) => v is ICollection { Count: > 0 };
            else if (p.PropertyType == typeof(C3dPlacement))
                p.ShouldSerialize = static (_, v) => v is C3dPlacement { IsDefault: false };
        }
    }

    /// <summary>
    /// brief-em3d-114 R-em3d114-1a — a port with <see cref="C3dPort.Terminals"/> writes none of the keys a terminal carries
    /// for it (rule 3 keeps every other port's spelling exactly), unless the port states one, which is a refusal the file must
    /// keep showing. Reading marks each such key the file states, so a stated <c>"Z0": "50"</c> is told from the default.
    /// </summary>
    private static void PortLevel(JsonTypeInfo info)
    {
        if (info.Type != typeof(C3dPort)) return;
        foreach (var p in info.Properties)
        {
            if (!C3dPorts.PortLevelKeys.Contains(p.Name)) continue;
            string key = p.Name;
            // Setting ShouldSerialize replaces the ignore conditions, so theirs is restated: null is never written (the
            // options' WhenWritingNull), nor Flip's false (its WhenWritingDefault) — the only bool among these keys.
            var inner = p.ShouldSerialize;
            p.ShouldSerialize = (o, v) => (inner?.Invoke(o, v) ?? v is not (null or false)) &&
                                          (o is not C3dPort { Terminals: { Count: > 0 } } port || port.Stated?.Contains(key) == true ||
                                           C3dPorts.StatesPortLevel(port, key));
            if (p.Set is { } set)
                p.Set = (o, v) => { set(o, v); ((C3dPort)o).Stated ??= new HashSet<string>(StringComparer.Ordinal); ((C3dPort)o).Stated!.Add(key); };
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The document as the file's text. Normalises a negative size in place first.</summary>
    public static string Serialize(C3dDocument doc)
    {
        Normalize(doc);
        return JsonSerializer.Serialize(doc, JsonOpts);
    }

    /// <summary>
    /// brief-em3d-83 R-em3d83-2 — the document as a RUN sees it: the file's text without its DISPLAY state. What Simulate keeps
    /// beside a run and what every "is this result current" comparison reads (the stale banner, <c>render --field</c>, the
    /// solved glyphs), never <see cref="Serialize"/>.
    /// <para>brief-em3d-98 R-em3d98-1 — every property the file writes, classified. A property added to the document must be
    /// put in one of these two lists; a display one is reset here.</para>
    /// <list type="bullet">
    /// <item><b>Display (left out):</b> the document's <c>DisplayUnit</c> (a preference: nothing it says moves geometry, and an
    /// expression stores its own unit), <c>SnapDbu</c> (the drawing grid; it never re-snaps), <c>FieldPlots</c> (R-em3d83-2),
    /// <c>AirBoxHidden</c>, <c>AirBox</c> (the box while no setup is active — a run reads its setup's), <c>ActiveSetup</c> (R-em3d98-3), <c>Look</c> (brief 106: the realistic view's); on every object at every depth (operands, a fillet's target)
    /// <c>Hidden</c>, <c>Transparency</c> (brief 92), <c>Appearance</c> (brief 105), <c>Group</c> (organisation only), <c>DrawingLayer</c> and
    /// <c>FaceImages</c>, and on a sheet its <c>Image</c> and <c>Locked</c> (brief 101: an image is drawn, never geometry); on every
    /// instance <c>Transparency</c>, <c>Appearance</c> and <c>Group</c>.</item>
    /// <item><b>Not modelled (left out whole):</b> a top-level object (not a polyline) or an instance whose <c>Model</c> is off —
    /// no run has it (<see cref="C3dModelled.Filter"/>), so moving or editing it changes no result. Its switch still does: turning
    /// it back on puts it back in this text.</item>
    /// <item><b>Model (kept):</b> <c>FormatVersion</c>, <c>DbuPerMicron</c>, <c>TechRef</c>, <c>Objects</c> (each one's name,
    /// material, role, placement, <c>Model</c> and geometry), <c>Instances</c> (cell, view, placement, array, <c>Model</c>,
    /// parameter overrides), <c>Variables</c>, <c>Ports</c> (all of each, <c>Model</c> included), <c>FaceBoundaries</c>,
    /// <c>HeatSources</c>, <c>Probes</c>, <c>MeshRegions</c>, <c>ContactResistances</c>, <c>EffectiveBlocks</c>,
    /// <c>SymmetryPlanes</c>, <c>WireGroundPlane</c>, <c>AirBoxMaterial</c>, <c>Setups</c>, and any key this build does not
    /// read (<c>Unread</c>: it cannot know, so it keeps them).</item>
    /// </list>
    /// An instance's and a port's visibility are the view's alone (never written), so they need no entry.
    /// <para>brief-em3d-98 — with <paramref name="setup"/>, as THAT setup's run sees it (<see cref="C3dSetups.ScopeOf"/>), so
    /// nothing another run reads can make its result out of date:</para>
    /// <list type="bullet">
    /// <item>every other setup is left out (a thermal submodel keeps its From setup);</item>
    /// <item>an EM run leaves out what only the thermal solver reads: <c>HeatSources</c>, <c>Probes</c>,
    /// <c>ContactResistances</c>, <c>EffectiveBlocks</c>, <c>SymmetryPlanes</c>, <c>WireGroundPlane</c> — and an openEMS-only run
    /// the <c>MeshRegions</c>, which size Gmsh's mesh and which openEMS ignores;</item>
    /// <item>a thermal run leaves out what only EM reads: <c>FaceBoundaries</c>, <c>AirBoxMaterial</c>, and every port its
    /// currents do not name (all are kept when a current comes from a circuit).</item>
    /// </list>
    /// Null: the whole document, as a run keeps it.
    /// </summary>
    public static string SerializeForRun(C3dDocument doc, string? setup)
    {
        if (setup is null) return SerializeForRun(doc);
        var scope = C3dSetups.ScopeOf(doc, setup);
        var read = C3dSetups.Read(doc);
        var saved = (doc.Setups, doc.HeatSources, doc.Probes, doc.ContactResistances, doc.EffectiveBlocks, doc.SymmetryPlanes,
                     doc.WireGroundPlane, doc.MeshRegions, doc.FaceBoundaries, doc.AirBoxMaterial, doc.Ports);
        doc.Setups = [.. doc.Setups.Where((_, i) => read.FirstOrDefault(r => r.Index == i) is { } r && scope.Setups.Contains(r.Name))];
        if (!scope.Thermal)
        {
            (doc.HeatSources, doc.Probes, doc.ContactResistances, doc.EffectiveBlocks, doc.SymmetryPlanes, doc.WireGroundPlane) =
                ([], [], [], [], [], null);
            if (!scope.Gmsh) doc.MeshRegions = [];
        }
        else
        {
            (doc.FaceBoundaries, doc.AirBoxMaterial) = ([], null);
            if (scope.Ports is { } ports) doc.Ports = [.. doc.Ports.Where(p => ports.Contains(p.Number))];
        }
        try { return SerializeForRun(doc); }
        finally
        {
            (doc.Setups, doc.HeatSources, doc.Probes, doc.ContactResistances, doc.EffectiveBlocks, doc.SymmetryPlanes,
             doc.WireGroundPlane, doc.MeshRegions, doc.FaceBoundaries, doc.AirBoxMaterial, doc.Ports) = saved;
        }
    }

    public static string SerializeForRun(C3dDocument doc)
    {
        var (plots, unit, snap, airBoxHidden, active, look) = (doc.FieldPlots, doc.DisplayUnit, doc.SnapDbu, doc.AirBoxHidden, doc.ActiveSetup, doc.Look);
        var docBox = doc.AirBox;
        var bands = doc.BandSweeps;
        var setups = doc.Setups;
        var objects = doc.Objects.SelectMany(C3dOperands.SelfAndDescendants)
                                 .Where(o => o.Hidden || o.Transparency is not null || o.Appearance is not null || o.Group is not null || o.DrawingLayer is not null || o.FlattenedFrom is not null)
                                 .Select(o => (Object: o, o.Hidden, o.Transparency, o.Appearance, o.Group, o.DrawingLayer, o.FlattenedFrom)).ToList();
        var instances = doc.Instances.Where(i => i.Transparency is not null || i.Appearance is not null || i.Group is not null)
                                     .Select(i => (Instance: i, i.Transparency, i.Appearance, i.Group)).ToList();
        // brief-em3d-101 R-em3d101-1i — an image edit is drawing only: re-pointing, removing or locking one changes no result.
        var images = doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dSheet>()
                                .Where(s => s.Image is not null || s.Locked)
                                .Select(s => (Sheet: s, s.Image, s.Locked)).ToList();
        var faceImages = doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).Where(o => o.FaceImages is not null)
                                    .Select(o => (Object: o, o.FaceImages)).ToList();
        doc.FieldPlots = [];
        doc.DisplayUnit = LayoutUnit.Um;
        doc.SnapDbu = 0;
        doc.AirBoxHidden = false;
        doc.AirBox = null;
        doc.ActiveSetup = null;
        doc.Look = null;
        // brief-sar-12 — a band sweep's power, duty cycle and reference values are read by its table, never by a run.
        doc.BandSweeps = null;
        // brief-dfr31-4 — what a run does once it has finished (make a schematic of its result) is no part of the result.
        doc.Setups = [.. setups.Select(WithoutRunInertKeys)];
        foreach (var x in objects) { x.Object.Hidden = false; x.Object.Transparency = null; x.Object.Appearance = null; x.Object.Group = null; x.Object.DrawingLayer = null; x.Object.FlattenedFrom = null; }
        foreach (var x in instances) { x.Instance.Transparency = null; x.Instance.Appearance = null; x.Instance.Group = null; }
        foreach (var x in images) { x.Sheet.Image = null; x.Sheet.Locked = false; }
        foreach (var x in faceImages) x.Object.FaceImages = null;
        // Not modelled: left out of every run (C3dModelled.Filter), so nothing it says can make a result out of date — a reference
        // image sheet moved, above all. A port is kept whatever its Model: a run's kept document is read back for its port Z0s by
        // their document numbers (FieldDrive.RunPortZ0s).
        var (allObjects, allInstances) = (doc.Objects, doc.Instances);
        if (allObjects.Any(o => o is not C3dPolyline && !o.Model)) doc.Objects = [.. allObjects.Where(o => o is C3dPolyline || o.Model)];
        if (allInstances.Any(i => !i.Model)) doc.Instances = [.. allInstances.Where(i => i.Model)];
        try { return Serialize(doc); }
        finally
        {
            (doc.Objects, doc.Instances) = (allObjects, allInstances);
            (doc.FieldPlots, doc.DisplayUnit, doc.SnapDbu, doc.AirBoxHidden, doc.ActiveSetup, doc.Look) = (plots, unit, snap, airBoxHidden, active, look);
            doc.AirBox = docBox;
            doc.BandSweeps = bands;
            doc.Setups = setups;
            foreach (var x in objects) { x.Object.Hidden = x.Hidden; x.Object.Transparency = x.Transparency; x.Object.Appearance = x.Appearance; x.Object.Group = x.Group; x.Object.DrawingLayer = x.DrawingLayer; x.Object.FlattenedFrom = x.FlattenedFrom; }
            foreach (var x in instances) { x.Instance.Transparency = x.Transparency; x.Instance.Appearance = x.Appearance; x.Instance.Group = x.Group; }
            foreach (var x in images) { x.Sheet.Image = x.Image; x.Sheet.Locked = x.Locked; }
            foreach (var x in faceImages) x.Object.FaceImages = x.FaceImages;
        }
    }

    /// <summary>An embedded setup less the keys that change what happens after a run and never its answer.</summary>
    private static JsonElement WithoutRunInertKeys(JsonElement setup)
    {
        if (setup.ValueKind != JsonValueKind.Object || !setup.TryGetProperty("CreateSnpSchematic", out _)) return setup;
        var node = System.Text.Json.Nodes.JsonNode.Parse(setup.GetRawText())!.AsObject();
        node.Remove("CreateSnpSchematic");
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>brief-em3d-83 — the field plots as the file spells them (an undo entry's before and after).</summary>
    public static string SerializeFieldPlots(IReadOnlyList<C3dFieldPlot> list) => JsonSerializer.Serialize(list, JsonOpts);

    /// <summary>brief-em3d-106 — the Look block as the file spells it, or "" for none (an undo entry's before and after).</summary>
    public static string SerializeLook(C3dLook? look) => look is null ? "" : JsonSerializer.Serialize(look, JsonOpts);

    public static C3dLook? DeserializeLook(string json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<C3dLook>(json, JsonOpts);

    public static List<C3dFieldPlot> DeserializeFieldPlots(string json)
        => JsonSerializer.Deserialize<List<C3dFieldPlot>>(json, JsonOpts) ?? [];

    public static void SaveToFile(string path, C3dDocument doc)
        => AtomicFile.WriteAllText(path, Serialize(doc));

    /// <summary>brief-em3d-42 R-em3d42-4 — one object as the file spells it: the elaborator's per-object cache
    /// key, so an object is re-elaborated exactly when what the file would say about it changes.</summary>
    public static string SerializeObject(C3dObject obj) => JsonSerializer.Serialize(obj, ItemOpts);

    /// <summary>brief-em3d-51 R-em3d51-5b — one object with its expressions replaced by their resolved numbers: the
    /// elaborator's cache key. Never read back.</summary>
    public static string SerializeResolved(C3dObject obj) => JsonSerializer.Serialize(obj, ResolvedOpts);

    /// <summary>brief-em3d-51 — the VAR list as the file spells it (an undo entry's before and after).</summary>
    public static string SerializeVariables(IReadOnlyList<C3dVariable> list) => JsonSerializer.Serialize(list, JsonOpts);

    public static List<C3dVariable> DeserializeVariables(string json) => JsonSerializer.Deserialize<List<C3dVariable>>(json, JsonOpts) ?? [];

    /// <summary>brief-em3d-43 — one object read back from <see cref="SerializeObject"/>'s text: what an undo
    /// entry stores, so an entry holds only the objects it changed, never the document.</summary>
    public static C3dObject DeserializeObject(string json)
        => JsonSerializer.Deserialize<C3dObject>(json, ItemOpts) ?? throw new C3dReadException(C3dDiagnostics.NotAnObject());

    /// <summary>brief-em3d-43 — one instance as the file spells it, and back.</summary>
    public static string SerializeInstance(C3dInstance inst) => JsonSerializer.Serialize(inst, ItemOpts);

    /// <summary>brief-em3d-49 — the document's face boundaries as the file spells them (an undo entry's before and after).</summary>
    public static string SerializeFaceBoundaries(IReadOnlyList<C3dFaceBoundary> list) => JsonSerializer.Serialize(list, JsonOpts);

    public static List<C3dFaceBoundary> DeserializeFaceBoundaries(string json)
        => JsonSerializer.Deserialize<List<C3dFaceBoundary>>(json, JsonOpts) ?? [];

    /// <summary>brief-em3d-49 — the document's ports, as the file spells them.</summary>
    public static string SerializePorts(IReadOnlyList<C3dPort> list) => JsonSerializer.Serialize(list, ItemOpts);

    public static List<C3dPort> DeserializePorts(string json) => JsonSerializer.Deserialize<List<C3dPort>>(json, ItemOpts) ?? [];

    /// <summary>brief-em3d-75 — the thermal places (heat sources, probes, mesh regions, contact overrides) as one text, in the
    /// in-memory spelling (each bound component keeps its number): what an undo entry of a thermal edit holds.</summary>
    public static string SerializeThermalPlaces(C3dDocument doc)
        => JsonSerializer.Serialize(new ThermalPlaces(doc.HeatSources, doc.Probes, doc.MeshRegions, doc.ContactResistances,
                                                      doc.EffectiveBlocks, doc.SymmetryPlanes), ItemOpts);

    /// <summary>Writes <see cref="SerializeThermalPlaces"/>' text back into <paramref name="doc"/>.</summary>
    public static void ApplyThermalPlaces(C3dDocument doc, string json)
    {
        var t = JsonSerializer.Deserialize<ThermalPlaces>(json, ItemOpts);
        doc.HeatSources = t?.HeatSources ?? [];
        doc.Probes = t?.Probes ?? [];
        doc.MeshRegions = t?.MeshRegions ?? [];
        doc.ContactResistances = t?.ContactResistances ?? [];
        doc.EffectiveBlocks = t?.EffectiveBlocks ?? [];
        doc.SymmetryPlanes = t?.SymmetryPlanes ?? [];
    }

    private sealed record ThermalPlaces(List<C3dHeatSource> HeatSources, List<C3dProbe> Probes, List<C3dMeshRegion> MeshRegions,
                                        List<C3dContactResistance> ContactResistances, List<C3dEffectiveBlock>? EffectiveBlocks = null,
                                        List<C3dSymmetryPlane>? SymmetryPlanes = null);

    /// <summary>brief-em3d-86 R-em3d86-4 — every list that names a face by name, as one text: the EM FaceBoundaries, the embedded
    /// setups (whose thermal boundaries name faces), the probes and the field plots. What a face edit's undo entry holds before
    /// and after a fold renamed faces (C3dFoldReferences).</summary>
    public static string SerializeFaceReferences(C3dDocument doc)
        => JsonSerializer.Serialize(new FaceReferences(doc.FaceBoundaries, doc.Setups, doc.Probes, doc.FieldPlots), ItemOpts);

    /// <summary>Writes <see cref="SerializeFaceReferences"/>' text back into <paramref name="doc"/>.</summary>
    public static void ApplyFaceReferences(C3dDocument doc, string json)
    {
        var r = JsonSerializer.Deserialize<FaceReferences>(json, ItemOpts);
        doc.FaceBoundaries = r?.FaceBoundaries ?? [];
        doc.Setups = r?.Setups ?? [];
        doc.Probes = r?.Probes ?? [];
        doc.FieldPlots = r?.FieldPlots ?? [];
    }

    private sealed record FaceReferences(List<C3dFaceBoundary> FaceBoundaries, List<JsonElement> Setups, List<C3dProbe> Probes,
                                         List<C3dFieldPlot> FieldPlots);

    /// <summary>brief-em3d-49 — an embedded setup list, as the file spells it.</summary>
    public static string SerializeSetups(IReadOnlyList<JsonElement> list) => JsonSerializer.Serialize(list, JsonOpts);

    public static List<JsonElement> DeserializeSetups(string json) => JsonSerializer.Deserialize<List<JsonElement>>(json, JsonOpts) ?? [];

    public static C3dInstance DeserializeInstance(string json)
        => JsonSerializer.Deserialize<C3dInstance>(json, ItemOpts) ?? throw new C3dReadException(C3dDiagnostics.NotAnObject());

    /// <summary>R-em3d41-2-dims: a size component is positive; a negative one moves the corner. A
    /// prism's height and a cylinder's length keep their sign — each says which way it was pulled.</summary>
    /// <para>brief-em3d-51 — an object holding an expression is left alone: moving a corner under a size the expression
    /// will recompute would change what the file means. A negative size from an expression is refused at resolution.</para>
    public static void Normalize(C3dDocument doc)
    {
        foreach (var o in doc.Objects.SelectMany(C3dOperands.SelfAndDescendants))
        {
            if (C3dBindings.HasAny(o)) continue;
            switch (o)
            {
                case C3dBox b:
                    var (mx, sx) = Positive(b.Min.X, b.Size.X);
                    var (my, sy) = Positive(b.Min.Y, b.Size.Y);
                    var (mz, sz) = Positive(b.Min.Z, b.Size.Z);
                    b.Min  = new C3dPoint3(mx, my, mz);
                    b.Size = new C3dPoint3(sx, sy, sz);
                    break;
                case C3dSheet { Rect: { } r }:
                    var (mu, su) = Positive(r.Min.U, r.Size.U);
                    var (mv, sv) = Positive(r.Min.V, r.Size.V);
                    r.Min  = new C3dPoint2(mu, mv);
                    r.Size = new C3dPoint2(su, sv);
                    break;
            }
        }

        static (long Min, long Size) Positive(long min, long size) => size < 0 ? (min + size, -size) : (min, size);
    }

    // ── Read ──────────────────────────────────────────────────────────────────────────────────

    /// <exception cref="C3dReadException">The text is not a 3D view this build can read — not JSON, a
    /// newer format, an object kind it does not know, or a string where a number belongs. Each names
    /// what it found. A document that READS but is wrong (a duplicate name, an open polyhedron) is not
    /// refused here: <see cref="C3dValidation"/> lists those, all of them.</exception>
    public static C3dDocument Deserialize(string json)
    {
        PreScan(json);

        C3dDocument? doc;
        try { doc = JsonSerializer.Deserialize<C3dDocument>(json, JsonOpts); }
        catch (JsonException ex) { throw new C3dReadException(C3dDiagnostics.Unreadable(ex.Message)); }

        return doc ?? throw new C3dReadException(C3dDiagnostics.NotAnObject());
    }

    public static C3dDocument LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    /// <summary>
    /// What the serializer would report badly or not at all: a newer format (checked before binding, so
    /// a later build's new keys are not what the user is told about), and every object kind this build
    /// does not know — ALL of them, by name, not the first one the serializer tripped on.
    /// </summary>
    private static void PreScan(string json)
    {
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false }); }
        catch (JsonException ex) { throw new C3dReadException(C3dDiagnostics.Unreadable(ex.Message)); }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new C3dReadException(C3dDiagnostics.NotAnObject());

            if (TryGet(root, nameof(C3dDocument.FormatVersion), out var fv)
                && fv.ValueKind == JsonValueKind.Number && fv.TryGetInt32(out int version)
                && version > CurrentFormatVersion)
                throw new C3dReadException(C3dDiagnostics.NewerFormat(version, CurrentFormatVersion));

            if (!TryGet(root, nameof(C3dDocument.Objects), out var objects) || objects.ValueKind != JsonValueKind.Array)
                return;   // the serializer's own "required" refusal names it

            var known   = C3dObject.Kinds.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
            var unknown = new List<string>();
            int index   = 0;
            foreach (var o in objects.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.Object)
                {
                    if (!o.TryGetProperty("$type", out var kind) || kind.ValueKind != JsonValueKind.String)
                        throw new C3dReadException(C3dDiagnostics.MissingKind(index));
                    Scan(o, kind.GetString() ?? "", $"object {index}", known, unknown);
                }
                index++;
            }

            if (unknown.Count > 0)
                throw new C3dReadException(C3dDiagnostics.UnknownKinds(
                    string.Join(", ", unknown), string.Join(", ", C3dObject.Kinds.Select(k => k.Name))));
        }
    }

    /// <summary>
    /// One object's kind, and — brief-em3d-64 R-em3d64-1f — every operand inside it: an unknown kind nested in a boolean is
    /// named as clearly as one at the top. An operand is named by its own Name, or by where it sits (<c>the Blank of 'lid'</c>).
    /// </summary>
    private static void Scan(JsonElement o, string kind, string where, HashSet<string> known, List<string> unknown)
    {
        string name = TryGet(o, nameof(C3dObject.Name), out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        string label = name.Length > 0 ? $"object '{name}'" : where;
        if (!known.Contains(kind))
        {
            unknown.Add($"{label} (a \"{kind}\")");
            return;
        }
        void Operand(JsonElement e, string at)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            if (!e.TryGetProperty("$type", out var k) || k.ValueKind != JsonValueKind.String)
                throw new C3dReadException(C3dDiagnostics.MissingOperandKind(at));
            Scan(e, k.GetString() ?? "", at, known, unknown);
        }
        string owner = name.Length > 0 ? $"'{name}'" : where;
        foreach (string key in (string[])[nameof(C3dBoolean.Blank), nameof(C3dFillet.Target)])
            if (TryGet(o, key, out var e)) Operand(e, $"the {key} of {owner}");
        if (TryGet(o, nameof(C3dBoolean.Tools), out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            int k = 0;
            foreach (var t in tools.EnumerateArray()) Operand(t, $"tool {k++} of {owner}");
        }
    }

    /// <summary>A property by name, case-insensitive — the reader's own matching.</summary>
    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    // ── Recognition by content (overview §1c) ─────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="path"/> holds a circuitRF 3D view: a JSON object with a
    /// <c>FormatVersion</c> and an <c>Objects</c> list at its top level. <b>By content, not by
    /// extension</b>, because an established motion-capture format also uses <c>.c3d</c> — a file from
    /// that program is binary and answers false on its first byte, so it is named as foreign rather
    /// than reported as a broken 3D view. Reads only until both keys are seen.
    /// </summary>
    public static bool LooksLikeC3d(string path)
    {
        byte[] bytes;
        try { bytes = GzipTextFile.ReadAllBytesAutoGzip(path); }
        catch { return false; }

        var span = new ReadOnlySpan<byte>(bytes);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) span = span[3..];

        try
        {
            var reader = new Utf8JsonReader(span, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            bool version = false, objects = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) break;
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;

                string name = reader.GetString() ?? "";
                reader.Read();
                if (string.Equals(name, nameof(C3dDocument.FormatVersion), StringComparison.OrdinalIgnoreCase))
                    version = reader.TokenType == JsonTokenType.Number;
                else if (string.Equals(name, nameof(C3dDocument.Objects), StringComparison.OrdinalIgnoreCase))
                    objects = reader.TokenType == JsonTokenType.StartArray;
                if (version && objects) return true;
                reader.Skip();
            }
            return false;
        }
        catch (JsonException) { return false; }
    }
}
