// HZN-4 R-hzn4-1 — a `.c3d` document as one Hertzon 3D design.
//
// The inverse of HznToC3d, written from the same tables read the other way (src/Design/RESOLVED.md §HZN-2): each object
// becomes a modelling history, a Boolean's tools become objects of their own with `consumed_by` set, a placement becomes
// Mirror / Rotate / Move steps, ports and lumped elements become a rectangle sheet plus the assignment on it, a face
// boundary is written by object when it covers the whole object and by `{obj, face, sig}` otherwise.
//
// WHAT CIRCUITRF CANNOT SAY IN HERTZON IS REFUSED, NEVER APPROXIMATED (brief-hzn-0 D3), and everything the translation back
// would refuse is never written, so every export reads back through HznToC3d. The refusal lists every item at once.
//
// Clean room (brief-hzn-0 D1): what each property means is from Hertzon's documentation, its example files and
// `hertzon-cli describe`; src/Design/RESOLVED.md §HZN records each meaning relied on.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>What an export needs besides the document.</summary>
public sealed record HznExportOptions
{
    /// <summary>The design's name in the project; the cell's, or the file's, when null.</summary>
    public string? DesignName { get; init; }

    /// <summary>The technology the document's materials come from (its objects, face boundaries and air box fill name them).</summary>
    public Technology? Technology { get; init; }

    /// <summary>The geometry worker, asked only for a face signature past a plain box or an extent past a plain shape; the
    /// shared one when null.</summary>
    public GeometryKernel? Kernel { get; init; }

    /// <summary>The cell the document is the view of: its parameters are names in the document's scope.</summary>
    public C3dCell? Cell { get; init; }

    /// <summary>What was done before the writer ran (instances and layouts flattened), already in a sentence each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What <see cref="C3dToHzn.Write"/> produced: the design as a JSON tree ready to append to a project's
/// <c>designs</c>, the project materials it needs, or the refusal.</summary>
/// <param name="Design">The design's name.</param>
/// <param name="Json">The design; null when refused.</param>
/// <param name="Materials">The project materials the design names that Hertzon does not have built in, each a JSON object.</param>
/// <param name="Refusals">Every unsupported item, grouped.</param>
/// <param name="Notes">What was flattened or written as built, and what was dropped because it is display state.</param>
/// <param name="Unused">Settings of the document that the design does not carry, with why.</param>
public sealed record C3dToHznResult(string Design, HznJsonObject? Json, IReadOnlyList<HznJsonObject> Materials,
                                    IReadOnlyList<HznRefusalItem> Refusals, IReadOnlyList<string> Notes)
{
    public bool Ok => Json is not null;
}

public static class C3dToHzn
{
    /// <summary>The Hertzon name of a project variable's reserved <c>.c3d</c> spelling.</summary>
    public const string ProjectPrefix = HznNames.ProjectPrefix;

    /// <summary>
    /// <paramref name="doc"/> (not changed) as a Hertzon 3D design. Reads no file. A document with instances must be flattened
    /// first (<see cref="HznExport"/> does); one that still has them is refused.
    /// </summary>
    public static C3dToHznResult Write(C3dDocument doc, HznExportOptions options) =>
        new C3dToHznWriter(doc, options).Run();
}

internal sealed partial class C3dToHznWriter
{
    private readonly C3dDocument _src;
    private readonly C3dDocument _doc;            // a resolved clone: every number is current, every expression kept
    private readonly HznExportOptions _o;
    private readonly HznRefusalBuilder _r = new();
    private readonly List<string> _notes = [];
    private C3dResolution _res = null!;
    private HznExpressionWriter _x = null!;
    private LayoutUnit _unit;
    private int _dbu;
    private string _user = "";

    public C3dToHznWriter(C3dDocument doc, HznExportOptions options)
    {
        _src = doc;
        _o = options;
        _doc = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
    }

    private string DesignName => _o.DesignName is { Length: > 0 } n ? n : "Design";

    public C3dToHznResult Run()
    {
        _notes.AddRange(_o.Notes);
        _unit = _doc.DisplayUnit;
        _dbu = _doc.DbuPerMicron;

        _res = C3dResolver.Resolve(_doc, _o.Cell ?? C3dCell.None);
        if (!_res.Ok)
        {
            foreach (string err in _res.Errors) _r.Add(HznRefusalKind.Expression, "expression", err, DesignName);
            return Finish(null, []);
        }
        _x = new HznExpressionWriter(_res, _r, HznName);

        RefuseWhatHertzonHasNoWordFor();
        var variables = Variables();
        Objects();
        Sheets();
        Ports();
        LumpedElements();
        FaceBoundaries();
        var setups = Setups();
        AirBox();
        MaterialsUsed();
        if (_r.Any) return Finish(null, []);
        if (_x.WroteConditional)
            _notes.Add("An if() was written: Hertzon's tutorial names it, but no evaluation of it could be checked.");

        return Finish(AssembleDesign(variables, setups), _projectMaterials);
    }

    private C3dToHznResult Finish(HznJsonObject? design, IReadOnlyList<HznJsonObject> materials) =>
        new(DesignName, _r.Any ? null : design, _r.Any ? [] : materials, _r.Items(), _notes);

    // ── names ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A variable's name in Hertzon: a project variable's reserved prefix comes off (our variables are the design's, D9).</summary>
    private string HznName(string c3dName) => HznNames.HznProjectName(c3dName) ?? c3dName;

    /// <summary>An object's name in Hertzon: <c>AirBox_hzn</c> is the <c>AirBox</c> a translation renamed ('airbox' is reserved here).</summary>
    private static string ObjectName(string c3dName) => HznNames.HznObjectName(c3dName);

    // ── what has no Hertzon word ──────────────────────────────────────────────────────────────────

    private void RefuseWhatHertzonHasNoWordFor()
    {
        if (_doc.Instances.Count > 0)
            _r.Add(HznRefusalKind.Setting, "instances", "placed cells are flattened before an export; this document still has " +
                   string.Join(", ", _doc.Instances.Select(i => $"'{i.Name}'")), DesignName);
        foreach (var h in _doc.HeatSources) _r.Add(HznRefusalKind.Setting, "heat source", "Hertzon has no thermal solution", h.Name);
        foreach (var p in _doc.Probes) _r.Add(HznRefusalKind.Setting, "probe", "Hertzon has no thermal solution", p.Name);
        foreach (var _ in _doc.ContactResistances) _r.Add(HznRefusalKind.Setting, "contact resistance", "Hertzon has no thermal solution", DesignName);
        foreach (var b in _doc.EffectiveBlocks) _r.Add(HznRefusalKind.Setting, "effective block", "Hertzon has no thermal solution", b.Name);
        if (_doc.MeshRegions.Count > 0)
            _notes.Add($"{_doc.MeshRegions.Count} mesh region(s) were not written: Hertzon states its mesh in its own operations.");
        if (_doc.SymmetryPlanes.Count > 0)
            _notes.Add("The symmetry planes the model was cut on were not written; the cut model is what is written.");
    }

    // ── variables ─────────────────────────────────────────────────────────────────────────────────

    private List<(string Name, string Text)> Variables()
    {
        var list = new List<(string, string)>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in _res.Names.Values)
        {
            if (n.Source == C3dNameSource.Override) continue;
            string name = HznName(n.Name);
            if (seen.TryGetValue(name, out string? other))
            {
                _r.Add(HznRefusalKind.Expression, $"variable name '{name}'", $"'{n.Name}' and '{other}' are the same name in Hertzon", DesignName);
                continue;
            }
            seen[name] = n.Name;
            if (n.Name != name) _notes.Add($"Variable '{n.Name}' is '{name}' in the design: a project variable's reserved prefix comes off, and our variables are the design's.");
            if (_x.Write(n.Expression, n.Unit, $"variable {n.Name}") is { } t) list.Add((name, t.Text));
        }
        return list;
    }

    // ── dimensions ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A literal length: the DBU spelled in the document's display unit, to the digit (reads back to the same DBU).</summary>
    private string Lit(long dbu) => LayoutUnits.Spell(dbu, _unit, _dbu);

    /// <summary>One component of a bound length field: its expression in Hertzon's spelling, or the number as a literal.</summary>
    private string Dim(IC3dBindable owner, string property, int k, long number)
    {
        if (C3dBindings.GetExpr(owner, property, k) is { } e)
            return _x.Write(e.Expr, e.Unit, _user)?.Text ?? "0";
        return Lit(number);
    }

    private string Dim3(IC3dBindable owner, string property, C3dPoint3 p) =>
        $"{Dim(owner, property, 0, p.X)},{Dim(owner, property, 1, p.Y)},{Dim(owner, property, 2, p.Z)}";

    // ── extents, for the air box and for integration lines ────────────────────────────────────────

    internal readonly record struct Box3(long[] Min, long[] Max);

    private static Box3? Union(Box3? a, Box3? b) =>
        a is not { } x ? b : b is not { } y ? a
        : new Box3([Math.Min(x.Min[0], y.Min[0]), Math.Min(x.Min[1], y.Min[1]), Math.Min(x.Min[2], y.Min[2])],
                   [Math.Max(x.Max[0], y.Max[0]), Math.Max(x.Max[1], y.Max[1]), Math.Max(x.Max[2], y.Max[2])]);

    private static (int U, int V, int N) Axes(C3dPlane p) => p switch { C3dPlane.XY => (0, 1, 2), C3dPlane.YZ => (1, 2, 0), _ => (0, 2, 1) };

    /// <summary>An object's box in DBU: stated for an unrotated box, cylinder, sphere, sheet, prism or boolean of them, else asked of the
    /// geometry worker; null when neither can say.</summary>
    private Box3? Extent(C3dObject o)
    {
        if (AnalyticExtent(o) is { } box) return box;
        if (!GeometryKernelTree.CanHold(o)) return null;
        var kernel = _o.Kernel ?? GeometryKernel.Shared;
        if (!kernel.Capability.Available) return null;
        try
        {
            var faces = kernel.Faces(GeometryKernelTree.From(o, _dbu));
            if (faces.Count == 0) return null;
            double k = _dbu;   // the worker speaks micrometres
            long Lo(int a) => (long)Math.Floor(faces.Min(f => f.Box[a]) * k + 0.5);
            long Hi(int a) => (long)Math.Floor(faces.Max(f => f.Box[a + 3]) * k + 0.5);
            return new Box3([Lo(0), Lo(1), Lo(2)], [Hi(0), Hi(1), Hi(2)]);
        }
        catch (GeometryKernelException) { return null; }
    }

    private Box3? AnalyticExtent(C3dObject o)
    {
        var pl = o.Placement;
        // brief-hzn-14 — a turned sheet (an upright polyline's frame) reaches as far as its polygon's corners carried by its placement
        if ((pl.Rotate.Count > 0 || pl.MirrorX) && o is C3dSheet turned) return TurnedSheetBox(turned);
        if (pl.Rotate.Count > 0 || pl.MirrorX) return null;
        Box3? own = o switch
        {
            C3dBox b => new Box3([b.Min.X, b.Min.Y, b.Min.Z], [b.Min.X + b.Size.X, b.Min.Y + b.Size.Y, b.Min.Z + b.Size.Z]),
            C3dSphere s => new Box3([s.Centre.X - s.Radius, s.Centre.Y - s.Radius, s.Centre.Z - s.Radius],
                                    [s.Centre.X + s.Radius, s.Centre.Y + s.Radius, s.Centre.Z + s.Radius]),
            C3dCylinder c => CylinderBox(c),
            C3dSheet sh => SheetBox(sh),
            C3dPrism p when p.Shear == default && p.Outline.Count > 0 => PrismBox(p),
            C3dBoolean { Blank: { } blank } bo => bo.Op == C3dBooleanOp.Unite
                ? bo.Tools.Aggregate(AnalyticExtent(blank), (acc, t) => acc is null || AnalyticExtent(t) is null ? null : Union(acc, AnalyticExtent(t)))
                : AnalyticExtent(blank),
            _ => null,
        };
        if (own is not { } box) return null;
        long[] d = [pl.Origin.X, pl.Origin.Y, pl.Origin.Z];
        return new Box3([box.Min[0] + d[0], box.Min[1] + d[1], box.Min[2] + d[2]], [box.Max[0] + d[0], box.Max[1] + d[1], box.Max[2] + d[2]]);
    }

    private static Box3? TurnedSheetBox(C3dSheet s)
    {
        var (ring, _) = C3dEllipses.Polygon(s);
        if (ring.Count == 0) return null;
        var t = s.Placement.ToTransform();
        var pts = ring.Select(q => C3dLowering.OnPlane(s.Plane, q.U, q.V, s.Offset)).Select(p => t.Apply(new C3dPoint3((long)p.X, (long)p.Y, (long)p.Z))).ToList();
        static long R(double d) => (long)Math.Round(d, MidpointRounding.AwayFromZero);
        return new Box3([R(pts.Min(p => p.Item1)), R(pts.Min(p => p.Item2)), R(pts.Min(p => p.Item3))],
                        [R(pts.Max(p => p.Item1)), R(pts.Max(p => p.Item2)), R(pts.Max(p => p.Item3))]);
    }

    private static Box3 CylinderBox(C3dCylinder c)
    {
        long[] min = [c.Base.X - c.Radius, c.Base.Y - c.Radius, c.Base.Z - c.Radius];
        long[] max = [c.Base.X + c.Radius, c.Base.Y + c.Radius, c.Base.Z + c.Radius];
        int a = (int)c.Axis;
        long[] b = [c.Base.X, c.Base.Y, c.Base.Z];
        (min[a], max[a]) = (Math.Min(b[a], b[a] + c.Length), Math.Max(b[a], b[a] + c.Length));
        return new Box3(min, max);
    }

    private static Box3? SheetBox(C3dSheet s)
    {
        var (u, v, n) = Axes(s.Plane);
        long[] min = new long[3], max = new long[3];
        min[n] = max[n] = s.Offset;
        if (s.Rect is { } r)
        {
            (min[u], max[u]) = (r.Min.U, r.Min.U + r.Size.U);
            (min[v], max[v]) = (r.Min.V, r.Min.V + r.Size.V);
        }
        else if (s.Ellipse is { } el)
        {
            // brief-hzn-13 — an ellipse sheet's extent is its centre ± its radii
            (min[u], max[u]) = (el.Centre.U - el.Radii.U, el.Centre.U + el.Radii.U);
            (min[v], max[v]) = (el.Centre.V - el.Radii.V, el.Centre.V + el.Radii.V);
        }
        else if (s.Outline.Count > 0)
        {
            (min[u], max[u]) = (s.Outline.Min(q => q.U), s.Outline.Max(q => q.U));
            (min[v], max[v]) = (s.Outline.Min(q => q.V), s.Outline.Max(q => q.V));
        }
        else return null;
        return new Box3(min, max);
    }

    private static Box3 PrismBox(C3dPrism p)
    {
        var (u, v, n) = Axes(p.Plane);
        long[] min = new long[3], max = new long[3];
        (min[u], max[u]) = (p.Outline.Min(q => q.U), p.Outline.Max(q => q.U));
        (min[v], max[v]) = (p.Outline.Min(q => q.V), p.Outline.Max(q => q.V));
        (min[n], max[n]) = (Math.Min(p.Offset, p.Offset + p.Height), Math.Max(p.Offset, p.Offset + p.Height));
        return new Box3(min, max);
    }
}
