// Owner: PCD-10 (brief-pcd-10 R-pcd10-2). Framework-free.
//
// Which length each cell parameter is: read off what the plan and the schematic already hold (stretch bindings, offsets,
// the primitives' own PCell handles), never stored.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>One dimension: what it measures (a parameter, or the expression that uses one), its two ends in the plan's
/// DBU, its label (<c>Lpad = 300 µm</c>) and the unit the label is in (<c>um</c>), so its length can be read in the same.</summary>
public sealed record PCellDimensionLine(string Subject, string Label, long FromX, long FromY, long ToX, long ToY, string Unit = "");

/// <summary>brief-pcd-10 R-pcd10-2: the cell parameters as dimensions on a designed cell's planned layout.</summary>
public static class PCellDimensions
{
    /// <summary>
    /// One dimension per use of a parameter in <paramref name="parameters"/> (name → its unit): a stretch binding, an
    /// offset that names one, and a primitive parameter that names one (once per expression). <paramref name="scope"/>
    /// holds the cell's values. What cannot be found (an unplaced part, an expression that does not evaluate) is left out.
    /// </summary>
    public static IReadOnlyList<PCellDimensionLine> Of(LayoutView layout, string layoutDir, Technology? technology,
                                                   SchematicEditModel schematic, Scope scope,
                                                   IReadOnlyDictionary<string, string> parameters)
    {
        var list = new List<PCellDimensionLine>();
        var evaluator = new Evaluator();
        var pins = Pins(layout, layoutDir, technology).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        LayoutInstance? Placed(string id) => layout.Instances.FirstOrDefault(i => i.SchematicId == id);

        void Add(string expression, string siteUnit, (long X, long Y) from, (long X, long Y) to)
        {
            if (Label(expression, siteUnit, scope, evaluator, parameters) is { } label)
                list.Add(new PCellDimensionLine(expression.Trim(), label.Text, from.X, from.Y, to.X, to.Y, label.Unit));
        }

        foreach (var comp in schematic.Components)
        {
            // A stretch binding: anchor vertex to the first moving one, along its axis (in the part's frame).
            if (comp.Stretch is { Count: > 0 } bindings && DrawnMetal.Is(comp) && Placed(comp.InstanceName) is { } shape)
            {
                var vertices = Vertices(comp, shape, layout.DbuPerMicron, scope).ToDictionary(v => v.Vertex, v => (v.X, v.Y));
                foreach (var b in bindings)
                {
                    if (!vertices.TryGetValue(b.Anchor, out var a) || b.Moving.Length == 0
                        || !vertices.TryGetValue(b.Moving[0], out var m)) continue;
                    double rad = b.AxisDeg * Math.PI / 180;
                    var (ox, oy) = LayoutInstanceTransform.TransformPoint(0, 0, shape, 0, 0);
                    var (ax, ay) = LayoutInstanceTransform.TransformPoint((long)Math.Round(1e6 * Math.Cos(rad)),
                                                                          (long)Math.Round(1e6 * Math.Sin(rad)), shape, 0, 0);
                    double len = Math.Sqrt((double)(ax - ox) * (ax - ox) + (double)(ay - oy) * (ay - oy));
                    double ux = (ax - ox) / len, uy = (ay - oy) / len;
                    double along = (m.X - a.X) * ux + (m.Y - a.Y) * uy;
                    Add(b.Parameter, parameters.GetValueOrDefault(b.Parameter, ""), a,
                        (a.X + (long)Math.Round(along * ux), a.Y + (long)Math.Round(along * uy)));
                }
            }

            // An offset that names a parameter: from the anchor pin to the part's own pin.
            if (comp.Offset is { } offset)
            {
                int dot = offset.Anchor.LastIndexOf('.');
                var named = new[] { (offset.Dx, offset.Unit), (offset.Dy, offset.Unit), (offset.Angle, "deg") }
                    .FirstOrDefault(e => Names(e.Item1, parameters));
                if (named.Item1 is not null && dot > 0 && int.TryParse(offset.Anchor[(dot + 1)..], out int anchorPin)
                    && pins.FirstOrDefault(p => p.Instance == offset.Anchor[..dot] && p.Pin == anchorPin) is { Instance: not null } from
                    && pins.FirstOrDefault(p => p.Instance == comp.InstanceName && p.Pin == offset.Pin) is { Instance: not null } to)
                    Add(named.Item1, named.Item2, (from.Pose.X, from.Pose.Y), (to.Pose.X, to.Pose.Y));
            }

            // A primitive parameter that names one: the primitive's own handle for it gives the span.
            if (DrawnMetal.Is(comp) || Placed(comp.InstanceName) is not { } placed) continue;
            var uses = comp.Parameters.Where(p => Names(p.Expression, parameters) && seen.Add(p.Expression.Trim())).ToList();
            if (uses.Count == 0) continue;
            var handles = HandlesOf(placed, layoutDir, technology);
            foreach (var p in uses)
                if (handles.FirstOrDefault(h => h.Parameter == p.Name) is { } h)
                    Add(p.Expression, p.Unit, LayoutInstanceTransform.TransformPoint(h.AnchorX, h.AnchorY, placed, 0, 0),
                        LayoutInstanceTransform.TransformPoint(h.X, h.Y, placed, 0, 0));
        }
        // brief-pcd-13 D9: a Grow amount or a Repeat pitch that names a parameter.
        foreach (var span in Operations.PCellOperations.TraceOf(layout)?.Spans ?? [])
            if (Names(span.Expression, parameters)) Add(span.Expression, span.Unit, (span.FromX, span.FromY), (span.ToX, span.ToY));
        return list;
    }

    private static bool Names(string expression, IReadOnlyDictionary<string, string> parameters) =>
        Refs(expression).Any(parameters.ContainsKey);

    private static IReadOnlyCollection<string> Refs(string expression)
    {
        try { return AstWalker.CollectRefs(Parser.Parse(expression)); }
        catch (Exception) { return []; }
    }

    /// <summary><c>name = value unit</c>, or <c>expression = value unit</c>; the unit is the bare parameter's, else that of
    /// the first parameter the expression names, else the site's (brief-pcd-10 D2). Null when it does not evaluate.</summary>
    private static (string Text, string Unit)? Label(string expression, string siteUnit, Scope scope, Evaluator evaluator,
                                 IReadOnlyDictionary<string, string> parameters)
    {
        string text = expression.Trim();
        string unit = siteUnit == "deg" ? "deg"
            : parameters.TryGetValue(text, out var own) && own.Length > 0 ? own
            : Refs(text).Select(r => parameters.GetValueOrDefault(r, "")).FirstOrDefault(u => u.Length > 0) ?? siteUnit;
        double si;
        try { si = evaluator.Eval(text, scope, Site(siteUnit)).AsReal(); }
        catch (Exception) { return null; }
        double value = unit == "deg" ? si * 180 / Math.PI : si / (Units.Scale(UnitNormalizer.ToEngineUnit(unit)) ?? 1.0);
        string shown = Math.Round(value, 6).ToString("G6", CultureInfo.InvariantCulture);
        string glyph = unit switch { "um" => "µm", "deg" => "°", _ => unit };
        return ($"{text} = {shown}{(glyph is "°" or "" ? "" : " ")}{glyph}", unit);
    }

    /// <summary>The site unit as the evaluator takes it: none for an empty one (it would read "" as an unknown unit).</summary>
    private static string? Site(string? unit) => UnitNormalizer.ToEngineUnit(unit) is { Length: > 0 } u ? u : null;

    private static IReadOnlyList<PCellHandle> HandlesOf(LayoutInstance placed, string layoutDir, Technology? technology)
    {
        if (CellLayoutResolver.Resolve(placed, layoutDir, technology).View?.PCellOrigin is not { } origin
            || !PCellRegistry.TryGet(origin.GeneratorId, out var generator)) return [];
        try { return generator(origin.Parameters, technology, PCellLayerSelection.Default).Handles ?? []; }
        catch (Exception) { return []; }   // a generator that cannot draw here has no dimensions, never a failed plan
    }

    /// <summary>Every placed pin: its part, its number and its pose, from the generated cells' pins through the instance
    /// transform (brief-pcd-6 D6).</summary>
    public static IEnumerable<(string Instance, int Pin, PinPose Pose)> Pins(LayoutView layout, string layoutDir, Technology? technology)
    {
        foreach (var inst in layout.Instances)
        {
            if (inst.SchematicId is not { Length: > 0 } id) continue;
            var pins = CellLayoutResolver.Resolve(inst, layoutDir, technology).View?.Pins ?? [];
            for (int k = 0; k < pins.Count; k++)
                yield return (id, int.TryParse(pins[k].Name, out int n) ? n : k + 1, Pose(pins[k], inst));
        }
    }

    private static PinPose Pose(LayoutPin pin, LayoutInstance inst)
    {
        var (x, y) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, 0, 0);
        double rad = pin.OutwardDeg * Math.PI / 180;
        var (tx, ty) = LayoutInstanceTransform.TransformPoint(pin.X + (long)Math.Round(1e6 * Math.Cos(rad)),
                                                              pin.Y + (long)Math.Round(1e6 * Math.Sin(rad)), inst, 0, 0);
        double deg = Math.Atan2(ty - y, tx - x) * 180 / Math.PI;
        return new PinPose(x, y, Math.Round(deg < 0 ? deg + 360 : deg, 9));
    }

    /// <summary>An <c>MSHAPE</c>'s vertices where the plan put it (DBU), from its <c>X[i]</c>/<c>Y[i]</c> in
    /// <paramref name="scope"/>; a vertex that does not evaluate is left out.</summary>
    public static IReadOnlyList<(int Vertex, long X, long Y)> Vertices(EditableComponent shape, LayoutInstance placed,
                                                                        int dbuPerMicron, Scope scope)
    {
        var evaluator = new Evaluator();
        double dbuPerMetre = dbuPerMicron * 1e6;
        var list = new List<(int, long, long)>();
        for (int i = 1; shape.Parameters.FirstOrDefault(p => p.Name == DrawnMetal.X(i)) is { } xr
                        && shape.Parameters.FirstOrDefault(p => p.Name == DrawnMetal.Y(i)) is { } yr; i++)
        {
            try
            {
                double x = evaluator.Eval(xr.Expression, scope, Site(xr.Unit)).AsReal();
                double y = evaluator.Eval(yr.Expression, scope, Site(yr.Unit)).AsReal();
                var (wx, wy) = LayoutInstanceTransform.TransformPoint((long)Math.Round(x * dbuPerMetre),
                                                                      (long)Math.Round(y * dbuPerMetre), placed, 0, 0);
                list.Add((i, wx, wy));
            }
            catch (Exception) { /* a vertex that does not evaluate is not drawn either */ }
        }
        return list;
    }
}
