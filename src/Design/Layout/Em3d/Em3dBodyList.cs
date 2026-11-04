// brief-dfr31-1 R-dfr31-1-4 — the bodies of a static problem as a terminal picker lists them: each by its net (or its
// generated name), the layers it is on and how many objects it holds, and the name a terminal row writes for it.
//
// Built from the conductors the run itself builds (a 3D view's elaboration, a .cem's layout solids), so a row the picker
// fills resolves to exactly that body when the run resolves it (Em3dConductorNames).

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>
/// brief-dfr31-1 — one row of the terminal picker. <paramref name="Token"/> is what a row writes: the body's net when it
/// carries one, else its generated name (<c>body:</c> and its largest member), which resolves to that member and so to the
/// whole body.
/// </summary>
public sealed record Em3dBodyChoice(string Name, string Token, IReadOnlyList<string> Layers, IReadOnlyList<string> Members,
                                    IReadOnlyList<string> Nets)
{
    /// <summary>brief-dfr34-4 R-dfr34-4-2 — the layers it spans, top to bottom ("Top Copper–Bottom Copper"), "drawn" for metal
    /// on no layer of a stackup.</summary>
    public string LayerRange { get; init; } = "";

    /// <summary>How many of its members are via barrels.</summary>
    public int Vias { get; init; }

    /// <summary>Its plan extent, as "12.4 × 3.1 mm".</summary>
    public string SizeText { get; init; } = "";

    /// <summary>Its plan area, m² (the sort's second key).</summary>
    public double Area { get; init; }

    /// <summary>brief-dfr34-4 — part of the default ground: a member is on a ground-reference layer, or shares a net with one.</summary>
    public bool IsDefaultGround { get; init; }

    /// <summary>What the picker shows under the name: the layers it spans and its vias, its size and its object count.</summary>
    public string Detail
    {
        get
        {
            string layers = LayerRange.Length > 0 ? LayerRange : Layers.Count == 0 ? "drawn" : string.Join(", ", Layers);
            string vias = Vias switch { 0 => "", 1 => ", 1 via", _ => $", {Vias} vias" };
            string size = SizeText.Length > 0 ? $" · {SizeText}" : "";
            return $"{layers}{vias}{size} · {(Members.Count == 1 ? "1 object" : $"{Members.Count} objects")}";
        }
    }

    /// <summary>What a one-line list shows: the name, then <see cref="Detail"/>.</summary>
    public string Label => $"{Name} — {Detail}";

    /// <summary>True when <paramref name="name"/>, as a terminal row would state it, names this body: its token, a member,
    /// a net, or a member's path suffix.</summary>
    public bool IsNamedBy(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        if (name == Token || name == Name || Nets.Contains(name, StringComparer.Ordinal)) return true;
        if (name.StartsWith(Em3dBodies.GeneratedPrefix, StringComparison.Ordinal)) name = name[Em3dBodies.GeneratedPrefix.Length..];
        return Members.Any(m => m == name || m.EndsWith("/" + name, StringComparison.Ordinal));
    }

    public override string ToString() => Token;
}

public static class Em3dBodyList
{
    /// <summary>The bodies of a 3D view's elaboration, as a terminal of one of its setups sees them (what is not modelled
    /// left out).</summary>
    public static IReadOnlyList<Em3dBodyChoice> Of(CircuitRF.Design.ThreeD.C3dDocument document, CircuitRF.Design.ThreeD.C3dElaboration drawn)
    {
        var e = CircuitRF.Design.ThreeD.C3dModelled.Filter(document, drawn);
        return Of(e.Solids, e.Sheets, e.CylinderSheets, n => e.ObjectNets.TryGetValue(n, out var s) ? s : [], e.Origins,
                  e.GroundBandObjects);
    }

    /// <summary>The bodies of a layout's solids (a <c>.cem</c>'s).</summary>
    public static IReadOnlyList<Em3dBodyChoice> Of(Em3dLayoutSolidsResult r)
        => r.Ok ? Of(r.Solids, r.Sheets, null, n => r.ObjectNets.TryGetValue(n, out var s) ? s : [], r.Origins, r.GroundBandObjects) : [];

    /// <summary>
    /// Every body, sorted as the picker lists them (brief-dfr34-4 R-dfr34-4-2): bodies carrying a net first, by net, then
    /// the bodies with none; within each, the largest first. <paramref name="groundBand"/> is the problem's ground-reference
    /// objects, which mark the default ground.
    /// </summary>
    public static IReadOnlyList<Em3dBodyChoice> Of(IReadOnlyList<Em3dSolid> solids, IReadOnlyList<Em3dSheet> sheets,
                                                   IReadOnlyList<Em3dCylinderSheet>? cylinderSheets,
                                                   Func<string, IEnumerable<string>> netsOf,
                                                   IReadOnlyDictionary<string, Em3dObjectOrigin> origins,
                                                   IEnumerable<string>? groundBand = null)
    {
        var grouping = Em3dBodies.Group(solids, sheets, cylinderSheets, netsOf);
        var bounds = new Dictionary<string, (double X0, double Y0, double Z0, double X1, double Y1, double Z1)>(StringComparer.Ordinal);
        foreach (var s in solids) if (s.Role == Em3dRole.Conductor) bounds[s.Name] = Em3dProblem.Bounds(s.Primitive);
        foreach (var sh in sheets) bounds[sh.Name] = sh.WorldBounds();
        foreach (var cs in cylinderSheets ?? []) bounds[cs.Name] = cs.WorldBounds();
        var band = (groundBand ?? []).ToHashSet(StringComparer.Ordinal);
        var bandNets = band.SelectMany(netsOf).ToHashSet(StringComparer.Ordinal);

        Em3dBodyChoice Choice(Em3dBody b)
        {
            var layers = b.Members.Select(m => origins.GetValueOrDefault(m)?.StackupEntry).OfType<string>()
                                  .Distinct(StringComparer.Ordinal).ToList();
            // The layers of its copper (not its barrels), top first by the height of their copper.
            var copper = b.Members
                .Where(m => origins.GetValueOrDefault(m) is { Kind: not Em3dObjectKind.Via, StackupEntry: { } } && bounds.ContainsKey(m))
                .GroupBy(m => origins[m].StackupEntry!, StringComparer.Ordinal)
                .Select(g => (Layer: g.Key, Z: g.Max(m => bounds[m].Z1)))
                .OrderByDescending(x => x.Z).Select(x => x.Layer).ToList();
            string range = copper.Count switch
            {
                0 => "",
                1 => copper[0],
                2 => $"{copper[0]}–{copper[1]}",
                _ => $"{copper[0]}–{copper[^1]} ({copper.Count} layers)",
            };
            int vias = b.Members.Count(m => origins.GetValueOrDefault(m)?.Kind == Em3dObjectKind.Via);
            var boxes = b.Members.Where(bounds.ContainsKey).Select(m => bounds[m]).ToList();
            string size = "";
            double area = 0;
            if (boxes.Count > 0)
            {
                double w = boxes.Max(x => x.X1) - boxes.Min(x => x.X0), h = boxes.Max(x => x.Y1) - boxes.Min(x => x.Y0);
                area = w * h;
                size = Size(w, h);
            }
            return new Em3dBodyChoice(b.Name, b.Generated ? b.Name : b.Nets[0], layers, b.Members, b.Nets)
            {
                LayerRange = range, Vias = vias, SizeText = size, Area = area,
                IsDefaultGround = b.Members.Any(band.Contains) || b.Nets.Any(bandNets.Contains),
            };
        }

        return [.. grouping.Bodies.Select(Choice)
                           .OrderBy(c => c.Nets.Count == 0)
                           .ThenBy(c => c.Nets.Count == 0 ? "" : c.Name, CircuitRF.Design.ThreeD.NaturalOrder.Instance)
                           .ThenByDescending(c => c.Area)];
    }

    /// <summary>"12.4 × 3.1 mm", or in µm when both sides are under a millimetre.</summary>
    private static string Size(double w, double h)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        bool um = Math.Max(w, h) < 1e-3;
        double k = um ? 1e6 : 1e3;
        string F(double v) => (v * k).ToString(v * k >= 100 ? "0" : "0.#", ci);
        return $"{F(w)} × {F(h)} {(um ? "µm" : "mm")}";
    }
}
