// brief-em3d-51 R-em3d51-1 — a dimension that holds an expression.
//
// THE NUMBER STAYS WHERE IT WAS. Every named dimension keeps its integer (or real) field, and that field is what every
// reader downstream reads — the kernel, the lowering, the tools, the scene. An expression is held BESIDE it, in the
// owning record's Exprs, one slot per component, and resolution (C3dResolver) writes its value into the number. So the
// number is the last resolved value of the expression, a cache, and nothing below the resolver has to know expressions
// exist. The file never writes that cache: a component with an expression is spelled as the expression, always with
// its unit (R-em3d51-1a), and the number reads back as 0 until the document is resolved.
//
// WHY THE UNIT IS STORED. Taking the site unit from the document's DisplayUnit would make changing the display unit move
// geometry, which layout-view.md §1.3 forbids. The unit in force when the user typed is written beside the expression.
//
// THE SPELLING is the owner's JSON, a component at a time: `"Size": [ { "Expr": "w", "Unit": "Mil" }, 1270000, 0 ]`,
// on one line like every point. It is produced by replacing each bindable property's JSON contract (Modify) with one of
// type C3dFieldJson, so the object model keeps its plain numeric properties and a document with no expressions is
// written byte for byte as before.

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CircuitRF.Design.ThreeD;

/// <summary>
/// An expression in a dimension field: its text, and the unit in force when it was typed (<c>Mil</c>, <c>Um</c> …, or
/// <c>Deg</c> for an angle). The unit is the SITE unit: it scales a bare expression, and is skipped when the expression is
/// unit-bearing — a unit literal (<c>10mil</c>) or a unit-bearing name (var-unit-wins, expressions.md §8) — where it goes
/// to the bare ADDITIVE operands instead: in <c>2*w + 5</c>, with <c>w</c> in mil, the 5 is five mil.
/// </summary>
[JsonConverter(typeof(C3dExprJsonConverter))]
[System.ComponentModel.Description("{ \"Expr\": expression, \"Unit\": unit }")]
public sealed record C3dExpr(string Expr, string? Unit = null);

/// <summary><c>{ "Expr": "2*a", "Unit": "Mil" }</c> on one line — an instance override's spelling, the same as a bound
/// component's.</summary>
internal sealed class C3dExprJsonConverter : JsonConverter<C3dExpr>
{
    public override C3dExpr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            throw new JsonException(C3dDiagnostics.ExpressionAsString(reader.GetString() ?? "").Render());
        var values = new List<object?>();
        var exprs = new List<C3dExpr?>();
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException(C3dDiagnostics.ExpressionShape(reader.TokenType.ToString()).Render());
        C3dFieldJsonConverter.ReadComponentPublic(ref reader, values, exprs);
        return exprs[0]!;
    }

    public override void Write(Utf8JsonWriter writer, C3dExpr value, JsonSerializerOptions options)
        => writer.WriteRawValue(C3dFieldJsonConverter.Spell(value), skipInputValidation: true);
}

/// <summary>A record that can hold expressions in some of its dimension fields.</summary>
public interface IC3dBindable
{
    /// <summary>By property name, one slot per component (null where the component is a number); null when nothing is
    /// bound. Never written by the serializer directly — see <see cref="C3dBindings"/>.</summary>
    Dictionary<string, C3dExpr?[]>? Exprs { get; set; }
}

/// <summary>What a bindable field holds.</summary>
public enum C3dFieldKind
{
    /// <summary>An integer DBU length.</summary>
    Length,
    /// <summary>Degrees (a rotation).</summary>
    Angle,
    /// <summary>An integer ≥ 1 (an array's count). A non-integer is refused, never rounded.</summary>
    Count,
    /// <summary>A real number of micrometres (a sheet's thickness, a wire's diameter).</summary>
    Microns,
}

/// <summary>One bindable property: its owner type, name, how many components it has, and what it holds.</summary>
public sealed record C3dFieldSpec(Type Owner, string Property, int Arity, C3dFieldKind Kind)
{
    internal PropertyInfo Info { get; } = Owner.GetProperty(Property)!;

    /// <summary>brief-em3d-132 R-em3d132-1 — the property is a LIST of <see cref="Arity"/>-component points (a wire's
    /// <c>Points</c>, a polyline's <c>Points</c>/<c>Points3</c>), each element bound on its own.</summary>
    public bool IsList { get; init; }

    /// <summary>On an element of a list spec (<see cref="ElementAt"/>), which point; null on the list spec itself and on
    /// every fixed-arity field.</summary>
    public int? Element { get; init; }

    /// <summary>The expression map's key: the property name, or <c>Points[k]</c> for point <c>k</c> of a list (overview
    /// R-em3d131-1), one slot per component either way.</summary>
    public string Key => Element is { } k ? $"{Property}[{k}]" : Property;

    /// <summary>Point <paramref name="k"/> of a list spec, as a field of <see cref="Arity"/> components.</summary>
    public C3dFieldSpec ElementAt(int k) => this with { Element = k };
}

/// <summary>A path to one component of one bindable field of an item (an object, instance or port):
/// <c>Size[0]</c>, <c>Rect.Min[1]</c>, <c>Placement.Rotate[0].Deg</c>, <c>Array.Counts[2]</c>, <c>Radius</c>.</summary>
public readonly record struct C3dFieldPath(string Path)
{
    public override string ToString() => Path;
}

/// <summary>A bound component found in a document: which item, which field, and the owner that holds it.</summary>
public sealed record C3dBoundField(string Item, string Path, IC3dBindable Owner, C3dFieldSpec Spec, int Component, C3dExpr Expr);

/// <summary>The table of bindable fields, the accessors, and the JSON contract.</summary>
public static class C3dBindings
{
    /// <summary>R-em3d51-1b — every named dimension that may hold an expression. Of the point lists, a wire's and a
    /// polyline's take expressions per point (brief-em3d-132), and a prism's and a sheet's outline (brief-hzn-13); holes and a
    /// polyhedron's vertices stay numbers.</summary>
    public static IReadOnlyList<C3dFieldSpec> Fields { get; } =
    [
        new(typeof(C3dBox), nameof(C3dBox.Min), 3, C3dFieldKind.Length),
        new(typeof(C3dBox), nameof(C3dBox.Size), 3, C3dFieldKind.Length),
        new(typeof(C3dPrism), nameof(C3dPrism.Offset), 1, C3dFieldKind.Length),
        new(typeof(C3dPrism), nameof(C3dPrism.Height), 1, C3dFieldKind.Length),
        new(typeof(C3dPrism), nameof(C3dPrism.Shear), 2, C3dFieldKind.Length),
        new(typeof(C3dSheet), nameof(C3dSheet.Offset), 1, C3dFieldKind.Length),
        new(typeof(C3dSheet), nameof(C3dSheet.ThicknessUm), 1, C3dFieldKind.Microns),
        new(typeof(C3dPolyline), nameof(C3dPolyline.Offset), 1, C3dFieldKind.Length),
        new(typeof(C3dCylinder), nameof(C3dCylinder.Base), 3, C3dFieldKind.Length),
        new(typeof(C3dCylinder), nameof(C3dCylinder.Length), 1, C3dFieldKind.Length),
        new(typeof(C3dCylinder), nameof(C3dCylinder.Radius), 1, C3dFieldKind.Length),
        new(typeof(C3dSphere), nameof(C3dSphere.Centre), 3, C3dFieldKind.Length),
        new(typeof(C3dSphere), nameof(C3dSphere.Radius), 1, C3dFieldKind.Length),
        // brief-sar-3 — an ellipsoid's and a phantom's dimensions, exactly as every other.
        new(typeof(C3dEllipsoid), nameof(C3dEllipsoid.Centre), 3, C3dFieldKind.Length),
        new(typeof(C3dEllipsoid), nameof(C3dEllipsoid.SemiAxes), 3, C3dFieldKind.Length),
        new(typeof(C3dPhantom), nameof(C3dPhantom.Centre), 3, C3dFieldKind.Length),
        new(typeof(C3dPhantom), nameof(C3dPhantom.SemiAxes), 3, C3dFieldKind.Length),
        new(typeof(C3dPhantom), nameof(C3dPhantom.Radius), 1, C3dFieldKind.Length),
        new(typeof(C3dPhantom), nameof(C3dPhantom.Length), 1, C3dFieldKind.Length),
        new(typeof(C3dPhantom), nameof(C3dPhantom.Size), 3, C3dFieldKind.Length),
        new(typeof(C3dPhantom), nameof(C3dPhantom.Shell1), 1, C3dFieldKind.Length),
        new(typeof(C3dPhantom), nameof(C3dPhantom.Shell2), 1, C3dFieldKind.Length),
        new(typeof(C3dPhantom), nameof(C3dPhantom.Voxel), 1, C3dFieldKind.Length),
        // brief-sar-11 — a voxel body's FDTD cell inside it (0: a line on every voxel boundary).
        new(typeof(C3dVoxelBody), nameof(C3dVoxelBody.GridStep), 1, C3dFieldKind.Length),
        new(typeof(C3dWire), nameof(C3dWire.DiameterUm), 1, C3dFieldKind.Microns),
        new(typeof(C3dRect), nameof(C3dRect.Min), 2, C3dFieldKind.Length),
        new(typeof(C3dRect), nameof(C3dRect.Size), 2, C3dFieldKind.Length),
        // brief-cxp-1 R-cxp1-1 — a coaxial port's ring
        new(typeof(C3dPortAnnulus), nameof(C3dPortAnnulus.Centre), 2, C3dFieldKind.Length),
        new(typeof(C3dPortAnnulus), nameof(C3dPortAnnulus.Inner), 1, C3dFieldKind.Length),
        new(typeof(C3dPortAnnulus), nameof(C3dPortAnnulus.Outer), 1, C3dFieldKind.Length),
        new(typeof(C3dPlacement), nameof(C3dPlacement.Origin), 3, C3dFieldKind.Length),
        new(typeof(C3dRotation), nameof(C3dRotation.Deg), 1, C3dFieldKind.Angle),
        new(typeof(C3dArray), nameof(C3dArray.Counts), 3, C3dFieldKind.Count),
        new(typeof(C3dArray), nameof(C3dArray.Pitch), 3, C3dFieldKind.Length),
        new(typeof(C3dWireArray), nameof(C3dWireArray.Count), 1, C3dFieldKind.Count),
        new(typeof(C3dWireArray), nameof(C3dWireArray.Pitch), 3, C3dFieldKind.Length),
        // brief-em3d-64 R-em3d64-1c — an operation's dimensions, exactly as every other.
        new(typeof(C3dFillet), nameof(C3dFillet.Radius), 1, C3dFieldKind.Length),
        new(typeof(C3dChamfer), nameof(C3dChamfer.Distance), 1, C3dFieldKind.Length),
        new(typeof(C3dChamfer), nameof(C3dChamfer.Distance2), 1, C3dFieldKind.Length),
        // brief-em3d-73 R-em3d73-4 — the thermal places' dimensions. A heat sheet's Rect is a C3dRect, already above.
        new(typeof(C3dHeatSheet), nameof(C3dHeatSheet.Offset), 1, C3dFieldKind.Length),
        new(typeof(C3dProbe), nameof(C3dProbe.Point), 3, C3dFieldKind.Length),
        new(typeof(C3dProbeSpot), nameof(C3dProbeSpot.Center), 3, C3dFieldKind.Length),
        new(typeof(C3dProbeSpot), nameof(C3dProbeSpot.Diameter), 1, C3dFieldKind.Length),
        new(typeof(C3dProbeLine), nameof(C3dProbeLine.From), 3, C3dFieldKind.Length),
        new(typeof(C3dProbeLine), nameof(C3dProbeLine.To), 3, C3dFieldKind.Length),
        new(typeof(C3dMeshRegion), nameof(C3dMeshRegion.Min), 3, C3dFieldKind.Length),
        new(typeof(C3dMeshRegion), nameof(C3dMeshRegion.Size), 3, C3dFieldKind.Length),
        new(typeof(C3dMeshRegion), nameof(C3dMeshRegion.SizeUm), 1, C3dFieldKind.Microns),
        // brief-em3d-76 — an effective block's box and a symmetry plane's coordinate, which their own doc comments and the
        // schema always said may be expressions: without these rows the reader refused { "Expr": … } there.
        new(typeof(C3dEffectiveBlock), nameof(C3dEffectiveBlock.Min), 3, C3dFieldKind.Length),
        new(typeof(C3dEffectiveBlock), nameof(C3dEffectiveBlock.Size), 3, C3dFieldKind.Length),
        new(typeof(C3dSymmetryPlane), nameof(C3dSymmetryPlane.At), 1, C3dFieldKind.Length),
        // brief-em3d-86 R-em3d86-2 — the drawn wires' ground plane
        new(typeof(C3dWireGroundPlane), nameof(C3dWireGroundPlane.Z), 1, C3dFieldKind.Length),
        // brief-em3d-132 R-em3d132-1 — point lists: a polyline's Points are in its plane (u, v), at its Offset.
        new(typeof(C3dWire), nameof(C3dWire.Points), 3, C3dFieldKind.Length) { IsList = true },
        new(typeof(C3dPolyline), nameof(C3dPolyline.Points), 2, C3dFieldKind.Length) { IsList = true },
        new(typeof(C3dPolyline), nameof(C3dPolyline.Points3), 3, C3dFieldKind.Length) { IsList = true },
        // brief-hzn-13 R-hzn13-1 — a prism's and a sheet's outline, in the plane's (u, v), point by point as a polyline's.
        new(typeof(C3dPrism), nameof(C3dPrism.Outline), 2, C3dFieldKind.Length) { IsList = true },
        new(typeof(C3dSheet), nameof(C3dSheet.Outline), 2, C3dFieldKind.Length) { IsList = true },
        // brief-hzn-13 R-hzn13-2 — an ellipse sheet's centre and radii
        new(typeof(C3dEllipse), nameof(C3dEllipse.Centre), 2, C3dFieldKind.Length),
        new(typeof(C3dEllipse), nameof(C3dEllipse.Radii), 2, C3dFieldKind.Length),
        // brief-em3d-135 — a wire's held loop height and span: the arch is shaped to them when the document resolves.
        new(typeof(C3dWire), nameof(C3dWire.LoopHeight), 1, C3dFieldKind.Length),
        new(typeof(C3dWire), nameof(C3dWire.Span), 1, C3dFieldKind.Length),
    ];

    public static C3dFieldSpec? SpecOf(Type owner, string property)
        => Fields.FirstOrDefault(f => f.Owner == owner && f.Property == property);

    // ── numbers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Component <paramref name="k"/> of the field, as a double (DBU, degrees, a count or µm); null for an
    /// unset optional field.</summary>
    public static double? GetNumber(object owner, C3dFieldSpec spec, int k) => spec.Info.GetValue(owner) switch
    {
        List<C3dPoint3> l when spec.Element is { } e => e < l.Count ? k switch { 0 => l[e].X, 1 => l[e].Y, _ => l[e].Z } : null,
        List<C3dPoint2> l when spec.Element is { } e => e < l.Count ? (k == 0 ? l[e].U : l[e].V) : null,
        C3dPoint3 p => k switch { 0 => p.X, 1 => p.Y, _ => p.Z },
        C3dPoint2 p => k == 0 ? p.U : p.V,
        long l => l,
        double d => d,
        List<int> l => k < l.Count ? l[k] : null,
        _ => null,
    };

    /// <summary>Sets component <paramref name="k"/> of the field. A length or a count is rounded to an integer here —
    /// the resolver has already decided whether that rounding is allowed.</summary>
    public static void SetNumber(object owner, C3dFieldSpec spec, int k, double value)
    {
        long n = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        object? now = spec.Info.GetValue(owner);
        // brief-em3d-132 — a point of a list is written in place: the list is the property's own.
        if (spec.Element is { } e)
        {
            if (now is List<C3dPoint3> l3 && e < l3.Count) l3[e] = k switch { 0 => l3[e] with { X = n }, 1 => l3[e] with { Y = n }, _ => l3[e] with { Z = n } };
            else if (now is List<C3dPoint2> l2 && e < l2.Count) l2[e] = k == 0 ? l2[e] with { U = n } : l2[e] with { V = n };
            return;
        }
        object next = now switch
        {
            C3dPoint3 p => k switch { 0 => p with { X = n }, 1 => p with { Y = n }, _ => p with { Z = n } },
            C3dPoint2 p => k == 0 ? p with { U = n } : p with { V = n },
            long => n,
            List<int> l => SetAt(l, k, (int)n),
            null when spec.Info.PropertyType == typeof(long?) => n,   // brief-em3d-135 — an unset held length
            _ => value,   // double, double?, or an unset double?
        };
        spec.Info.SetValue(owner, next);

        static List<int> SetAt(List<int> l, int k, int v)
        {
            while (l.Count <= k) l.Add(1);
            l[k] = v;
            return l;
        }
    }

    public static C3dExpr? GetExpr(IC3dBindable owner, string property, int k)
        => owner.Exprs is { } e && e.TryGetValue(property, out var slots) && k < slots.Length ? slots[k] : null;

    /// <summary>Component <paramref name="k"/>'s expression, by the spec's map key (a list element's is <c>Points[k]</c>).</summary>
    public static C3dExpr? GetExpr(IC3dBindable owner, C3dFieldSpec spec, int k) => GetExpr(owner, spec.Key, k);

    /// <summary>Binds (or, with null, unbinds) one component. An owner with nothing bound keeps a null map.</summary>
    public static void SetExpr(IC3dBindable owner, C3dFieldSpec spec, int k, C3dExpr? expr)
    {
        var map = owner.Exprs;
        if (expr is null && map is null) return;
        map ??= new Dictionary<string, C3dExpr?[]>(StringComparer.Ordinal);
        if (!map.TryGetValue(spec.Key, out var slots)) slots = map[spec.Key] = new C3dExpr?[spec.Arity];
        slots[k] = expr;
        if (slots.All(s => s is null)) map.Remove(spec.Key);
        owner.Exprs = map.Count == 0 ? null : map;
    }

    /// <summary>A deep copy of an owner's expression map (a record is immutable, so the slots are what is copied).</summary>
    public static Dictionary<string, C3dExpr?[]>? Copy(Dictionary<string, C3dExpr?[]>? map)
        => map?.ToDictionary(kv => kv.Key, kv => (C3dExpr?[])kv.Value.Clone(), StringComparer.Ordinal);

    // ── walking a document ─────────────────────────────────────────────────────────────────────────

    /// <summary>Every bindable owner of an item, with the path prefix its fields are named under.</summary>
    public static IEnumerable<(string Prefix, IC3dBindable Owner)> OwnersOf(object item)
    {
        switch (item)
        {
            case C3dObject o:
                yield return ("", o);
                if (o is C3dSheet { Rect: { } r }) yield return ("Rect.", r);
                if (o is C3dSheet { Ellipse: { } el }) yield return ("Ellipse.", el);
                if (o is C3dWire { Array: { } wa }) yield return ("Array.", wa);
                foreach (var p in Placement(o.Placement)) yield return p;
                // brief-em3d-64 — an operation's operands are owned inline, so their fields are the operation's own,
                // named under the operand's path (Blank.Size[0], Tools[1].Radius, Target.Blank.Min[2]).
                foreach (var (prefix, child) in C3dOperands.Of(o))
                    foreach (var (p, owner) in OwnersOf(child)) yield return (prefix + p, owner);
                break;
            case C3dInstance i:
                foreach (var p in Placement(i.Placement)) yield return p;
                if (i.Array is { } a) yield return ("Array.", a);
                break;
            case C3dPort port:
                yield return ("Rect.", port.Rect);
                if (port.Annulus is { } ring) yield return ("Annulus.", ring);
                break;
            // brief-em3d-73 — the thermal places, named as check and the panels name them.
            case C3dHeatSource h when h.Sheet is { } sheet:
                yield return ("Sheet.", sheet);
                if (sheet.Rect is { } hr) yield return ("Sheet.Rect.", hr);
                break;
            case C3dProbe probe:
                yield return ("", probe);
                if (probe.Spot is { } spot) yield return ("Spot.", spot);
                if (probe.Line is { } line) yield return ("Line.", line);
                break;
            case C3dMeshRegion region:
                yield return ("", region);
                break;
            case C3dEffectiveBlock block:
                yield return ("", block);
                break;
            case C3dSymmetryPlane plane:
                yield return ("", plane);
                break;
            case C3dWireGroundPlane ground:
                yield return ("", ground);
                break;
        }

        static IEnumerable<(string, IC3dBindable)> Placement(C3dPlacement p)
        {
            yield return ("Placement.", p);
            for (int k = 0; k < p.Rotate.Count; k++) yield return ($"Placement.Rotate[{k}].", p.Rotate[k]);
        }
    }

    /// <summary>The fields of an owner, as (spec, component, path). A list spec is enumerated from the owner's current
    /// count, one element spec per point: <c>Points[k][c]</c>.</summary>
    public static IEnumerable<(C3dFieldSpec Spec, int Component, string Path)> FieldsOf(IC3dBindable owner, string prefix)
    {
        foreach (var spec in Fields)
        {
            if (spec.Owner != owner.GetType()) continue;
            if (spec.IsList)
            {
                int count = spec.Info.GetValue(owner) is ICollection list ? list.Count : 0;
                for (int e = 0; e < count; e++)
                {
                    var element = spec.ElementAt(e);
                    for (int k = 0; k < spec.Arity; k++) yield return (element, k, $"{prefix}{spec.Property}[{e}][{k}]");
                }
                continue;
            }
            if (spec.Arity == 1) { yield return (spec, 0, prefix + spec.Property); continue; }
            for (int k = 0; k < spec.Arity; k++) yield return (spec, k, $"{prefix}{spec.Property}[{k}]");
        }
    }

    /// <summary>The document's items, each named as <c>check</c> and the panels name it: an object by its name, an
    /// instance by its name, a port as <c>port &lt;number&gt;</c>.</summary>
    public static IEnumerable<(string Name, object Item)> ItemsOf(C3dDocument doc)
    {
        foreach (var o in doc.Objects) yield return (o.Name, o);
        foreach (var i in doc.Instances) yield return (i.Name, i);
        foreach (var p in doc.Ports) yield return ($"port {p.Number}", p);
        foreach (var h in doc.HeatSources) yield return (h.Name, h);
        foreach (var p in doc.Probes) yield return (p.Name, p);
        foreach (var m in doc.MeshRegions) yield return (m.Name, m);
        foreach (var b in doc.EffectiveBlocks) yield return (b.Name, b);
        foreach (var s in doc.SymmetryPlanes) yield return ($"symmetry {s.Axis}", s);
        if (doc.WireGroundPlane is { } g) yield return ("wire ground plane", g);
    }

    /// <summary>Every bound component in the document.</summary>
    public static IEnumerable<C3dBoundField> Bound(C3dDocument doc)
    {
        foreach (var (name, item) in ItemsOf(doc))
            foreach (var f in BoundOf(name, item)) yield return f;
    }

    /// <summary>Every bound component of one item.</summary>
    public static IEnumerable<C3dBoundField> BoundOf(string name, object item)
    {
        foreach (var (prefix, owner) in OwnersOf(item))
        {
            if (owner.Exprs is null) continue;
            foreach (var (spec, k, path) in FieldsOf(owner, prefix))
                if (GetExpr(owner, spec, k) is { } e) yield return new C3dBoundField(name, path, owner, spec, k, e);
        }
    }

    /// <summary>The owner, spec and component a path names in <paramref name="item"/>; null when the item has no such
    /// field (a sheet with an outline has no Rect).</summary>
    public static (IC3dBindable Owner, C3dFieldSpec Spec, int Component)? Find(object item, string path)
    {
        foreach (var (prefix, owner) in OwnersOf(item))
            foreach (var (spec, k, p) in FieldsOf(owner, prefix))
                if (p == path) return (owner, spec, k);
        return null;
    }

    /// <summary>
    /// How a field is named in a sentence: its path, except a point of a list, which reads as the Inspector labels its
    /// rows — <c>point 2 z</c>, 1-based, with a polyline's in-plane point's components <c>u</c> and <c>v</c>
    /// (overview R-em3d131-1).
    /// </summary>
    public static string Label(C3dFieldSpec spec, int component, string path)
        => spec.Element is { } e
            ? $"point {e + 1} {(spec.Arity == 2 ? (component == 0 ? "u" : "v") : component switch { 0 => "x", 1 => "y", _ => "z" })}"
            : path;

    /// <summary>True when the item holds an expression anywhere.</summary>
    public static bool HasAny(object item) => OwnersOf(item).Any(o => o.Owner.Exprs is { Count: > 0 });

    // ── the JSON contract ──────────────────────────────────────────────────────────────────────────

    /// <summary>The encoder the <c>.c3d</c> writes with: an expression's <c>+</c> is written as a plus, not
    /// <c>+</c>, so the file reads as it was typed.</summary>
    public static JavaScriptEncoder Encoder => JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// The resolver modifier (<see cref="C3dSpelling"/> says which spelling): each bindable property's contract is replaced by one of type <see cref="C3dFieldJson"/>
    /// that writes a number where the component is a number and <c>{ "Expr": …, "Unit": … }</c> where it is bound. With
    /// <paramref name="numbersOnly"/>, expressions are left out and the resolved numbers written — the elaborator's
    /// cache key (R-em3d51-5b: an object is keyed on its RESOLVED fields).
    /// </summary>
    internal static void Modify(JsonTypeInfo info, C3dSpelling spelling)
    {
        bool numbersOnly = spelling == C3dSpelling.NumbersOnly;
        if (info.Kind != JsonTypeInfoKind.Object || !typeof(IC3dBindable).IsAssignableFrom(info.Type)) return;
        // brief-em3d-132 R-em3d132-2 — Points3 IS the polyline when it is present, and Points is then ignored: an expression
        // under the ignored list would be dropped without a word, so the read refuses it by name.
        if (info.Type == typeof(C3dPolyline))
        {
            var then = info.OnDeserialized;
            info.OnDeserialized = o =>
            {
                then?.Invoke(o);
                var l = (C3dPolyline)o;
                if (l.Points3 is not null && l.Exprs?.Keys.FirstOrDefault(k => k.StartsWith(nameof(C3dPolyline.Points) + "[", StringComparison.Ordinal)) is { } key)
                    throw new JsonException(C3dDiagnostics.IgnoredPointExpression(l.Name, key).Render());
            };
        }
        for (int i = 0; i < info.Properties.Count; i++)
        {
            var p = info.Properties[i];
            if (p.AttributeProvider is not PropertyInfo pi || SpecOf(info.Type, pi.Name) is not { } spec) continue;
            var get = p.Get!;
            var set = p.Set!;
            bool omitDefault = pi.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.WhenWritingDefault };
            var np = info.CreateJsonPropertyInfo(typeof(C3dFieldJson), p.Name);
            np.Order = p.Order;
            np.Get = owner =>
            {
                var map = numbersOnly ? null : ((IC3dBindable)owner).Exprs;
                var value = get(owner);
                var slots = map?.GetValueOrDefault(spec.Property);
                C3dExpr?[]?[]? points = null;
                if (spec.IsList && map is not null && value is ICollection list)
                {
                    points = new C3dExpr?[]?[list.Count];
                    for (int k = 0; k < list.Count; k++) points[k] = map.GetValueOrDefault(spec.ElementAt(k).Key);
                }
                return new C3dFieldJson(spec, value, slots) { WithValues = spelling == C3dSpelling.WithValues, PointExprs = points };
            };
            np.Set = (owner, v) =>
            {
                var f = (C3dFieldJson)v!;
                set(owner, ToProperty(spec, pi.PropertyType, f.Value));
                if (f.Exprs is { } slots)
                    for (int k = 0; k < slots.Length; k++)
                        if (slots[k] is { } e) SetExpr((IC3dBindable)owner, spec, k, e);
                if (f.PointExprs is { } points)
                    for (int p = 0; p < points.Length; p++)
                        for (int k = 0; k < (points[p]?.Length ?? 0); k++)
                            if (points[p]![k] is { } e) SetExpr((IC3dBindable)owner, spec.ElementAt(p), k, e);
            };
            np.ShouldSerialize = (owner, v) =>
            {
                var f = (C3dFieldJson)v!;
                if (f.Exprs is { } s && s.Any(e => e is not null)) return true;
                if (f.Value is null) return false;
                if (f.Value is ICollection { Count: 0 }) return false;
                return !omitDefault || !Equals(f.Value, Activator.CreateInstance(pi.PropertyType));
            };
            info.Properties[i] = np;
        }
    }

    /// <summary>A value read in its generic shape, as the property's own type — with the refusals the number readers
    /// give (a point's arity, an integer where one belongs).</summary>
    private static object? ToProperty(C3dFieldSpec spec, Type type, object? raw)
    {
        object?[] parts = raw is RawComponents rc ? rc.Values : [raw];
        long Int(object? o) => o switch
        {
            long l => l,
            double d => throw new JsonException(C3dDiagnostics.IntegerExpected(d.ToString("R", CultureInfo.InvariantCulture)).Render()),
            _ => throw new JsonException(C3dDiagnostics.NumberExpected("null").Render()),
        };
        void Arity(int n, string shape)
        {
            if (raw is not RawComponents) throw new JsonException(C3dDiagnostics.ArrayExpected(shape).Render());
            if (parts.Length != n) throw new JsonException(C3dDiagnostics.WrongArity(shape, n, parts.Length).Render());
        }
        if (type == typeof(C3dPoint3) || type == typeof(C3dPoint3?))
        {
            if (raw is null && type == typeof(C3dPoint3?)) return null;
            Arity(3, "[x, y, z]");
            return new C3dPoint3(Int(parts[0]), Int(parts[1]), Int(parts[2]));
        }
        if (type == typeof(C3dPoint2)) { Arity(2, "[u, v]"); return new C3dPoint2(Int(parts[0]), Int(parts[1])); }
        // brief-em3d-132 — a point list, with the refusals the number-only list readers give.
        if (type == typeof(List<C3dPoint3>) || type == typeof(List<C3dPoint2>))
        {
            bool three = type == typeof(List<C3dPoint3>);
            if (raw is null) return null;
            if (raw is not RawComponents) throw new JsonException(C3dDiagnostics.ArrayExpected(three ? "[[x, y, z], …]" : "[[u, v], …]").Render());
            string shape = three ? "[x, y, z]" : "[u, v]";
            int n = three ? 3 : 2;
            var points3 = new List<C3dPoint3>();
            var points2 = new List<C3dPoint2>();
            foreach (var part in parts)
            {
                if (part is not RawComponents pc) throw new JsonException(C3dDiagnostics.ArrayExpected(shape).Render());
                if (pc.Values.Length != n) throw new JsonException(C3dDiagnostics.WrongArity(shape, n, pc.Values.Length).Render());
                if (three) points3.Add(new C3dPoint3(Int(pc.Values[0]), Int(pc.Values[1]), Int(pc.Values[2])));
                else points2.Add(new C3dPoint2(Int(pc.Values[0]), Int(pc.Values[1])));
            }
            return three ? points3 : points2;
        }
        if (type == typeof(List<int>))
        {
            if (raw is not RawComponents) throw new JsonException(C3dDiagnostics.ArrayExpected("[integer]").Render());
            return parts.Select(o => checked((int)Int(o))).ToList();
        }
        if (raw is RawComponents) throw new JsonException(C3dDiagnostics.NumberExpected("list").Render());
        if (type == typeof(long)) return Int(raw);
        if (type == typeof(long?)) return raw is null ? null : Int(raw);
        if (type == typeof(double)) return raw is null ? throw new JsonException(C3dDiagnostics.NumberExpected("null").Render()) : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        if (type == typeof(double?)) return raw is null ? null : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        _ = spec;
        return raw;
    }
}

/// <summary>Which spelling a serializer writes a bound component in.</summary>
public enum C3dSpelling
{
    /// <summary>The file: the expression and its unit. The resolved number is a cache and is not written.</summary>
    File,
    /// <summary>An object's in-memory copy (an undo entry, a clone): the expression AND its last resolved number
    /// (<c>"Value"</c>), so a copy is at the same place as the original before anything re-resolves it.</summary>
    WithValues,
    /// <summary>Numbers only: the elaborator's cache key.</summary>
    NumbersOnly,
}

/// <summary>A bindable field on its way to or from the file: the numeric value (as the property holds it) and the
/// expression slots.</summary>
[JsonConverter(typeof(C3dFieldJsonConverter))]
public sealed class C3dFieldJson(C3dFieldSpec? spec, object? value, C3dExpr?[]? exprs)
{
    public C3dFieldSpec? Spec { get; } = spec;
    public object? Value { get; } = value;
    public C3dExpr?[]? Exprs { get; } = exprs;

    /// <summary>Write each bound component's resolved number beside its expression (<see cref="C3dSpelling.WithValues"/>).</summary>
    public bool WithValues { get; init; }

    /// <summary>brief-em3d-132 — a point list's slots, one array (or null) per point.</summary>
    public C3dExpr?[]?[]? PointExprs { get; init; }
}

/// <summary>Writes and reads <see cref="C3dFieldJson"/> — a component is a number or <c>{ "Expr", "Unit" }</c>.</summary>
internal sealed class C3dFieldJsonConverter : JsonConverter<C3dFieldJson>
{
    public override C3dFieldJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Which property this is comes from the type info that called us, and the reader cannot see it — so the value is
        // read in its generic shape (a scalar or a list of components) and C3dFieldJson.Value is converted on Set.
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<object?>();
            var exprs = new List<C3dExpr?>();
            List<C3dExpr?[]?>? points = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    var shaped = Shape(values, exprs, array: true);
                    return points is { } p && p.Any(e => e is not null) ? new C3dFieldJson(null, shaped.Value, shaped.Exprs) { PointExprs = [.. p] } : shaped;
                }
                // brief-em3d-132 — a point list: an array of points, each read as a fixed field's components are.
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var inner = Read(ref reader, typeToConvert, options);
                    values.Add(inner.Value);
                    exprs.Add(null);
                    (points ??= []).Add(inner.Exprs);
                    continue;
                }
                ReadComponent(ref reader, values, exprs);
                points?.Add(null);
            }
            throw new JsonException(C3dDiagnostics.ArrayExpected("[…]").Render());
        }
        var one = new List<object?>();
        var e1 = new List<C3dExpr?>();
        ReadComponent(ref reader, one, e1);
        return Shape(one, e1, array: false);
    }

    internal static void ReadComponentPublic(ref Utf8JsonReader reader, List<object?> values, List<C3dExpr?> exprs)
        => ReadComponent(ref reader, values, exprs);

    private static void ReadComponent(ref Utf8JsonReader reader, List<object?> values, List<C3dExpr?> exprs)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                values.Add(reader.TryGetInt64(out long l) ? (object)l : reader.GetDouble());
                exprs.Add(null);
                return;
            case JsonTokenType.Null:
                values.Add(null);
                exprs.Add(null);
                return;
            case JsonTokenType.String:
                throw new JsonException(C3dDiagnostics.ExpressionAsString(reader.GetString() ?? "").Render());
            case JsonTokenType.StartObject:
                string? text = null, unit = null;
                object cached = 0L;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string key = reader.GetString() ?? "";
                    reader.Read();
                    if (string.Equals(key, "Expr", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String) text = reader.GetString();
                    else if (string.Equals(key, "Unit", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String) unit = reader.GetString();
                    else if (string.Equals(key, "Value", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.Number)
                        cached = reader.TryGetInt64(out long cl) ? (object)cl : reader.GetDouble();
                    else throw new JsonException(C3dDiagnostics.ExpressionShape(key).Render());
                }
                if (string.IsNullOrWhiteSpace(text)) throw new JsonException(C3dDiagnostics.ExpressionShape("Expr").Render());
                values.Add(cached);
                exprs.Add(new C3dExpr(text, unit));
                return;
            default:
                throw new JsonException(C3dDiagnostics.NumberExpected(reader.TokenType.ToString()).Render());
        }
    }

    private static C3dFieldJson Shape(List<object?> values, List<C3dExpr?> exprs, bool array)
        => new(null, array ? new RawComponents([.. values]) : values[0], exprs.Any(e => e is not null) ? [.. exprs] : null);

    public override void Write(Utf8JsonWriter writer, C3dFieldJson value, JsonSerializerOptions options)
    {
        var spec = value.Spec!;
        var slots = value.Exprs;
        if (spec.IsList)
        {
            // brief-em3d-132 R-em3d132-2 — a point per line, as the number-only list converters write it; a point with an
            // expression is spelled as a fixed field's components are, on its one line.
            var lines = new List<string>();
            var items = value.Value switch
            {
                List<C3dPoint3> l3 => l3.Select(p => new long[] { p.X, p.Y, p.Z }).ToList(),
                List<C3dPoint2> l2 => l2.Select(p => new long[] { p.U, p.V }).ToList(),
                _ => [],
            };
            for (int i = 0; i < items.Count; i++)
            {
                var point = value.PointExprs is { } pe && i < pe.Length ? pe[i] : null;
                if (point is null || point.All(e => e is null)) { lines.Add(C3dListLayout.Inline(items[i])); continue; }
                lines.Add("[" + string.Join(", ", items[i].Select((v, k) => k < point.Length && point[k] is { } e
                    ? Spell(e, value.WithValues ? v.ToString(CultureInfo.InvariantCulture) : null)
                    : v.ToString(CultureInfo.InvariantCulture))) + "]");
            }
            var sb = new StringBuilder();
            C3dListLayout.AppendLines(sb, writer, 0, lines);
            writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
            return;
        }
        if (spec.Arity == 1 && value.Value is not List<int>)
        {
            if (slots?[0] is { } e) { writer.WriteRawValue(Spell(e, value.WithValues ? Number(value.Value) : null), skipInputValidation: true); return; }
            switch (value.Value)
            {
                case long l: writer.WriteNumberValue(l); break;
                case double d: writer.WriteNumberValue(d); break;
                case null: writer.WriteNullValue(); break;
                default: writer.WriteNumberValue(Convert.ToDouble(value.Value, CultureInfo.InvariantCulture)); break;
            }
            return;
        }
        var parts = new List<string>(spec.Arity);
        int n = value.Value is List<int> list ? list.Count : spec.Arity;
        for (int k = 0; k < n; k++)
        {
            double? v = value.Value switch
            {
                C3dPoint3 p => k switch { 0 => p.X, 1 => p.Y, _ => p.Z },
                C3dPoint2 p => k == 0 ? p.U : p.V,
                List<int> l => l[k],
                _ => null,
            };
            if (slots is not null && k < slots.Length && slots[k] is { } e)
            {
                parts.Add(Spell(e, value.WithValues ? ((long)(v ?? 0)).ToString(CultureInfo.InvariantCulture) : null));
                continue;
            }
            parts.Add(((long)(v ?? 0)).ToString(CultureInfo.InvariantCulture));
        }
        writer.WriteRawValue("[" + string.Join(", ", parts) + "]", skipInputValidation: true);
    }

    /// <summary><c>{ "Expr": "w", "Unit": "Mil" }</c>, on one line.</summary>
    internal static string Spell(C3dExpr e, string? value = null)
    {
        var sb = new StringBuilder("{ \"Expr\": ").Append(Quote(e.Expr));
        if (e.Unit is { Length: > 0 } u) sb.Append(", \"Unit\": ").Append(Quote(u));
        if (value is not null) sb.Append(", \"Value\": ").Append(value);
        return sb.Append(" }").ToString();
    }

    private static string? Number(object? v) => v switch
    {
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Quote(string s) => JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = C3dBindings.Encoder });
}

/// <summary>A list of components read before the property's own type is known.</summary>
internal sealed class RawComponents(object?[] values)
{
    public object?[] Values { get; } = values;
}
