using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CircuitRF.Design.Layout;

/// <summary>What reading a <c>.clay</c> found wrong with it without refusing it.</summary>
public enum LayoutLoadFindingKind
{
    /// <summary>A key the reader does not know, which it IGNORED — see <see cref="LayoutLoadAudit"/>.</summary>
    UnknownField,

    /// <summary>A vertex-list shape with too few vertices to be a shape at all.</summary>
    DegenerateShape,
}

/// <summary>
/// One thing <see cref="LayoutPersistence"/> noticed while loading a layout. Worded to be shown
/// as-is — the GUI posts it to the Messages panel on a fresh load and <c>circuitrf check</c> reports
/// it — so both say the same sentence about the same file.
/// </summary>
public sealed record LayoutLoadFinding(LayoutLoadFindingKind Kind, bool IsError, string Message);

/// <summary>
/// The two silent losses a <c>.clay</c> read can suffer, reported rather than refused.
///
/// <para><b>Unknown fields.</b> The reader ignores a key it does not know, deliberately: a file written
/// by a newer circuitRF must still open here (the same rule <c>SmithDesignIo</c> states). But a
/// misspelt key is ignored the same way, and a <c>Poly</c> written with <c>"Points"</c> instead of
/// <c>"Xy"</c> loaded as a polygon with NO vertices, which <c>check</c> passed with 0 errors and an
/// EM run would have solved without — the F0 spike's case B lost its ground plane exactly so
/// (<c>docs/design/em-3d-f0-findings.md</c> §7). So the reader still ignores the key, and now says
/// it did. A WARNING, never an error: a newer file is not a broken one.</para>
///
/// <para><b>The serializer itself reports the keys</b> (<see cref="CaptureUnknownFields"/>), during
/// the read that already happens — so what counts as unknown is exactly what the reader did not
/// bind, and a clean file pays nothing measurable for the check.</para>
///
/// <para><b>Degenerate shapes.</b> A polygon needs three vertices to enclose anything; a path and a
/// curve need two (a curve's edges may be arcs, so two vertices can still bound area). A vertex list
/// of odd length has a dangling coordinate. Such a shape draws nothing, meshes nothing and is checked
/// by no rule — so it is an ERROR: nothing circuitRF writes produces one, and an author who wrote one
/// meant something else.</para>
///
/// <para>Both kinds are AGGREGATED — one finding per unknown key per kind of object, one per kind of
/// degenerate shape — with a count and the first few places, so a file carrying one bad key on
/// 50,000 shapes is one line in the Messages panel, not 50,000.</para>
/// </summary>
public static class LayoutLoadAudit
{
    private const int MaxPlacesNamed = 3;

    // ── unknown fields ───────────────────────────────────────────────────────

    /// <summary>
    /// A contract modifier for <see cref="LayoutPersistence"/>'s serializer options: gives every
    /// reference-type object contract an <b>extension-data</b> property, so the deserializer itself
    /// hands over each key it did not map, during the one read that already happens.
    ///
    /// <para><b>Why this and not a second pass over the text.</b> The first version walked the JSON again
    /// with a <see cref="Utf8JsonReader"/> against the same contract. On a 61 MB board it cost
    /// 135–200 ms of a 405–510 ms load, and no amount of tuning could shrink it much: the vertex lists
    /// are most of the bytes, and even skipping them means tokenizing them. Here a clean file costs one
    /// property per contract that is never hit. And the serializer is the one deciding what is unknown,
    /// so this cannot drift from what the reader binds.</para>
    ///
    /// <para>The setter is called with an EMPTY dictionary, before the key is added to it, and the
    /// serializer then reads it BACK through the getter to add the key — so the getter must return what
    /// was set (a getter that always answers null makes the read throw
    /// <see cref="InvalidOperationException"/>). Both go through a table that exists only for the
    /// duration of a <see cref="Capturing{T}"/> read, keyed by object identity, and the keys are read
    /// after the parse. Outside a read the getter answers null, so a SAVE writes nothing: the keys a load
    /// ignored are reported, never carried back out. <see cref="LayerKey"/> and the other value types get no capture: the serializer's
    /// extension-data path needs a settable reference, and their few fields are not where a misspelt key
    /// silently empties a shape. Null on write, so a save never emits the property.</para>
    /// </summary>
    internal static void CaptureUnknownFields(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.Type.IsValueType) return;
        foreach (var existing in info.Properties)
            if (existing.IsExtensionData) return;

        var property = info.CreateJsonPropertyInfo(typeof(Dictionary<string, JsonElement>), "\u0001unknown");
        property.IsExtensionData = true;
        property.Get = static owner =>
            t_byOwner is { } table && table.TryGetValue(owner, out var keys) ? keys : null;
        property.Set = static (owner, value) =>
        {
            if (value is not Dictionary<string, JsonElement> keys || t_byOwner is not { } table) return;
            table[owner] = keys;
            t_captured!.Add((owner, keys));
        };
        info.Properties.Add(property);
    }

    [ThreadStatic] private static List<(object Owner, Dictionary<string, JsonElement> Keys)>? t_captured;
    [ThreadStatic] private static Dictionary<object, Dictionary<string, JsonElement>>? t_byOwner;

    /// <summary>Runs <paramref name="read"/> with capture on for this thread, and returns what it caught.</summary>
    internal static (T Result, List<(object Owner, Dictionary<string, JsonElement> Keys)> Captured) Capturing<T>(Func<T> read)
    {
        var (outerList, outerTable) = (t_captured, t_byOwner);
        var captured = t_captured = [];
        t_byOwner = new Dictionary<object, Dictionary<string, JsonElement>>(ReferenceEqualityComparer.Instance);
        try { return (read(), captured); }
        finally { (t_captured, t_byOwner) = (outerList, outerTable); }
    }

    /// <summary>
    /// The unknown keys a read captured, aggregated per key per kind of object, with where they were.
    /// A shape is named by its position in the file's <c>Shapes</c> list, the root by name, anything
    /// else by its kind — the capture holds the object, not its path, and only shapes come in numbers
    /// large enough for a position to matter.
    /// </summary>
    internal static List<LayoutLoadFinding> UnknownFields(
        List<(object Owner, Dictionary<string, JsonElement> Keys)> captured, ClayFile file)
    {
        if (captured.Count == 0) return [];

        var shapeIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < file.Shapes.Count; i++) shapeIndex[file.Shapes[i]] = i;

        var tally = new Dictionary<(string Owner, string Key), (int Count, List<string> Places)>();
        foreach (var (owner, keys) in captured)
        {
            string ownerName, place;
            if (owner is LayoutShape shape)
            {
                ownerName = $"a {Discriminator(shape.GetType())} shape";
                place = shapeIndex.TryGetValue(owner, out int i) ? $"Shapes[{i}]" : "a shape";
            }
            else if (owner is ClayFile)
            {
                ownerName = "the layout file";
                place = "the top level";
            }
            else
            {
                ownerName = $"a {owner.GetType().Name}";
                place = $"a {owner.GetType().Name}";
            }

            foreach (var key in keys.Keys)
            {
                tally.TryGetValue((ownerName, key), out var seen);
                seen.Places ??= [];
                if (seen.Places.Count < MaxPlacesNamed && !seen.Places.Contains(place)) seen.Places.Add(place);
                tally[(ownerName, key)] = (seen.Count + 1, seen.Places);
            }
        }

        var findings = new List<LayoutLoadFinding>(tally.Count);
        foreach (var ((owner, key), (count, places)) in tally)
        {
            string where = count == 1
                ? $"at {places[0]}"
                : $"on {count} of them (first at {string.Join(", ", places)})";
            findings.Add(new LayoutLoadFinding(
                LayoutLoadFindingKind.UnknownField, IsError: false,
                $"'{key}' is not a field of {owner} and was ignored when the layout was read, {where}. " +
                "A misspelt field is lost this way; so is one written by a newer circuitRF."));
        }
        return findings;
    }

    /// <summary>The <c>$type</c> a shape is written with — "Poly", not "PolygonShape".</summary>
    private static string Discriminator(Type shapeType)
    {
        foreach (var a in typeof(LayoutShape).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), false))
            if (a is JsonDerivedTypeAttribute d && d.DerivedType == shapeType && d.TypeDiscriminator is string s)
                return s;
        return shapeType.Name;
    }

    // ── degenerate shapes ────────────────────────────────────────────────────

    /// <summary>
    /// Tallies one shape as it is read. <paramref name="index"/> is its position in the file's
    /// <c>Shapes</c> list, so the place named is the one an author finds in the file.
    /// </summary>
    internal sealed class DegenerateTally
    {
        private readonly Dictionary<string, (int Count, List<string> Places, string Need)> _byKind = [];

        public void Add(LayoutShape shape, int index)
        {
            var (kind, xy, minVertices) = shape switch
            {
                PolygonShape p => ("Poly", p.Xy, 3),
                PathShape p    => ("Path", p.Xy, 2),
                CurveShape c   => ("Curve", c.Xy, 2),
                _              => ("", null, 0),
            };
            if (xy is null) return;

            int vertices = xy.Length / 2;
            bool odd = xy.Length % 2 != 0;
            if (vertices >= minVertices && !odd) return;

            string what = odd
                ? $"Shapes[{index}] on layer {shape.Layer.Layer}/{shape.Layer.Datatype} ({xy.Length} coordinates, an odd count)"
                : $"Shapes[{index}] on layer {shape.Layer.Layer}/{shape.Layer.Datatype} ({vertices} vertices)";
            _byKind.TryGetValue(kind, out var seen);
            seen.Places ??= [];
            if (seen.Places.Count < MaxPlacesNamed) seen.Places.Add(what);
            _byKind[kind] = (seen.Count + 1, seen.Places, $"at least {minVertices} vertices as x, y pairs");
        }

        public IEnumerable<LayoutLoadFinding> Findings()
        {
            foreach (var (kind, (count, places, need)) in _byKind)
            {
                string subject = count == 1
                    ? $"A {kind} shape has too few coordinates to be a shape: {places[0]}."
                    : $"{count} {kind} shapes have too few coordinates to be shapes (first: {string.Join("; ", places)}).";
                yield return new LayoutLoadFinding(
                    LayoutLoadFindingKind.DegenerateShape, IsError: true,
                    $"{subject} A {kind} needs {need}; with fewer it encloses nothing, so it draws " +
                    "nothing, meshes nothing and no design rule sees it.");
            }
        }
    }
}
