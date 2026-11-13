// HZN-2 — the translation itself: variables, dimensions, and each object's history folded into a .c3d tree.
// Boundaries, ports, setups, materials and face references are the partials beside this file.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>A length: a number of DBU (a literal), or an expression in the design's units.</summary>
/// <param name="Text">The expression, or null for a literal.</param>
/// <param name="Dbu">The literal's value.</param>
/// <param name="Bearing">The expression's value carries a unit; always true for a literal.</param>
internal sealed record Dim(string? Text, long Dbu, bool Bearing)
{
    public bool IsLiteral => Text is null;
    public bool IsZeroLiteral => Text is null && Dbu == 0;
    public static Dim Zero { get; } = new(null, 0, true);
}

internal sealed partial class HznTranslator
{
    private readonly HznProject _project;
    private readonly HznDesign _design;
    private readonly HznProvenance _prov;
    private readonly GeometryKernel? _kernel;
    private readonly HznRefusalBuilder _r = new();
    private readonly List<string> _notes = [];
    private readonly C3dDocument _doc = new() { DbuPerMicron = HznToC3d.DbuPerMicron };

    private HznNames _names = new();
    private HznExpressionTranslator _expr = null!;
    private HznExpressionTranslator _projectExpr = null!;
    private C3dResolution? _res;
    private string _siteUnit = "mm";
    private double _siteScale = 1e-3;

    public HznTranslator(HznProject project, HznDesign design, HznProvenance prov, GeometryKernel? kernel)
    {
        _project = project;
        _design = design;
        _prov = prov;
        _kernel = kernel;
    }

    private string DesignName => _design.Name ?? "";

    // ── the run ───────────────────────────────────────────────────────────────────────────────────

    public C3dTranslation Run()
    {
        if (!SetUnits()) return Finish(null);

        var projectVars = VarMap(_project.Variables);
        var designVars = VarMap(_design.Variables);
        _names = new HznNames { Design = designVars, Project = projectVars };
        _expr = new HznExpressionTranslator(_names, _r);
        _projectExpr = new HznExpressionTranslator(_names, _r, projectScope: true);

        Variables(designVars, projectVars);
        if (_r.Any) return Finish(null);
        if (!Scope()) return Finish(null);

        // Every stage runs even when an earlier one found something, so the refusal lists every item at once
        // (brief-hzn-0 D3). An assignment on an object that failed to fold is skipped: its own refusal already says why.
        Objects();
        Resolve();
        Ports();
        Holes();
        Boundaries();
        Setups();
        MaterialsToTechnology();
        Terminals();                // brief-hzn-14 — after the boundaries, which make sheets metal
        if (_r.Any) return Finish(null);

        Check();
        return Finish(_doc);
    }

    private C3dTranslation Finish(C3dDocument? doc) =>
        new(DesignName, _r.Any ? null : doc, _r.Any ? null : _technology, _prov, _r.Items(), _notes, null);

    // ── units ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, (string Engine, double Scale, Layout.LayoutUnit Display)> DesignUnits = new(StringComparer.Ordinal)
    {
        ["nm"] = ("nm", 1e-9, Layout.LayoutUnit.Nm), ["um"] = ("um", 1e-6, Layout.LayoutUnit.Um),
        ["mm"] = ("mm", 1e-3, Layout.LayoutUnit.Mm), ["cm"] = ("cm", 1e-2, Layout.LayoutUnit.Mm),
        ["m"] = ("metre", 1.0, Layout.LayoutUnit.Mm), ["mil"] = ("mil", 2.54e-5, Layout.LayoutUnit.Mil),
        ["in"] = ("in", 2.54e-2, Layout.LayoutUnit.Inch),
    };

    /// <summary>The design's units: what a bare number in a length is, and the document's display unit.</summary>
    private bool SetUnits()
    {
        string units = _design.Units ?? "mm";
        if (!DesignUnits.TryGetValue(units, out var u))
        {
            _r.Add(HznRefusalKind.Setting, $"design units '{units}'", "circuitRF translates designs in nm, um, mm, cm, m, mil or in", DesignName);
            return false;
        }
        (_siteUnit, _siteScale, _doc.DisplayUnit) = (u.Engine, u.Scale, u.Display);
        if (units == "cm" || units == "m")
            _notes.Add($"The design's units are {units}; the document's display unit is mm, and bare numbers in its dimensions are still read in {units}.");
        return true;
    }

    private static Dictionary<string, string> VarMap(HznStringMap map) =>
        map.Names.ToDictionary(n => n, n => map[n] ?? "", StringComparer.Ordinal);

    // ── variables ─────────────────────────────────────────────────────────────────────────────────

    private void Variables(Dictionary<string, string> designVars, Dictionary<string, string> projectVars)
    {
        foreach (var (name, source) in designVars)
        {
            if (C3dResolver.ValidateName(name) is { } bad)
            {
                _r.Add(HznRefusalKind.Expression, $"variable name '{name}'", $"circuitRF cannot use it as a name: {bad}", DesignName);
                continue;
            }
            if (name.StartsWith(HznNames.ProjectPrefix, StringComparison.Ordinal))
            {
                _r.Add(HznRefusalKind.Expression, $"variable name '{name}'", $"names starting '{HznNames.ProjectPrefix}' are reserved for project variables", DesignName);
                continue;
            }
            if (_expr is null) continue;
            var e = _expr.Translate(source, $"variable {name}");
            if (e is null) continue;
            UseProjectRefs(e);
            _doc.Variables.Add(new C3dVariable { Name = name, Expression = e.Text });
            _prov.Variables.Add(new HznVariableOrigin(name, name, Project: false));
        }
    }

    /// <summary>Project variables that something reaches are added (transitively); the rest are not the design's business.</summary>
    private readonly HashSet<string> _projectUsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _projectDone = new(StringComparer.Ordinal);

    private void UseProjectRefs(HznExpr e)
    {
        foreach (string name in e.Refs)
            if (!_names.Design.ContainsKey(name) && _names.Project.ContainsKey(name)) _projectUsed.Add(name);
    }

    private void AddProjectVariables()
    {
        var done = _projectDone;
        var queue = new Queue<string>(_projectUsed);
        while (queue.Count > 0)
        {
            string name = queue.Dequeue();
            if (!done.Add(name)) continue;
            if (!_names.Project.ContainsKey(name)) continue;
            string c3d = HznNames.C3dProjectName(name);
            var e = _projectExpr.Translate(_names.Project[name], $"project variable {name}");
            if (e is null) continue;
            _doc.Variables.Add(new C3dVariable { Name = c3d, Expression = e.Text });
            _prov.Variables.Add(new HznVariableOrigin(c3d, name, Project: true));
            foreach (string r in e.Refs) if (_names.Project.ContainsKey(r)) queue.Enqueue(r);
        }
    }

    /// <summary>The scope every dimension is evaluated in, before any object exists: a scratch document of the variables.</summary>
    private bool Scope()
    {
        // Project variables are added as references appear; for evaluation now, add them all that translate quietly.
        var quiet = new HznRefusalBuilder();
        var q = new HznExpressionTranslator(_names, quiet, projectScope: true);
        var all = new List<C3dVariable>(_doc.Variables);
        foreach (string name in _names.Project.Keys)
            if (q.Translate(_names.Project[name], "") is { } e) all.Add(new C3dVariable { Name = HznNames.C3dProjectName(name), Expression = e.Text });
        _scratch = new C3dDocument { DbuPerMicron = HznToC3d.DbuPerMicron, Variables = all };
        _res = C3dResolver.Resolve(_scratch, C3dCell.None);
        if (!_res.Ok)
        {
            foreach (string err in _res.Errors)
                _r.Add(HznRefusalKind.Expression, "variables", err, DesignName);
            return false;
        }
        return true;
    }

    private C3dDocument _scratch = null!;

    // ── dimensions ────────────────────────────────────────────────────────────────────────────────

    private string LiteralText(long dbu) =>
        (dbu / (double)HznToC3d.DbuPerMicron).ToString("R", CultureInfo.InvariantCulture) + "um";

    private long ToDbu(double si, string source, string user)
    {
        double raw = si * 1e6 * HznToC3d.DbuPerMicron;
        double rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded - raw) > 1e-9 * Math.Max(1, Math.Abs(raw)))
            _notes.Add($"{user}: '{source}' is {raw:0.###} nm and was rounded to {rounded:0} nm (the document's unit).");
        return (long)rounded;
    }

    /// <summary>A length. A literal opens as a number; anything else stays an expression (brief-hzn-0 D5).</summary>
    private Dim? Len(string? source, string user)
    {
        if (source is null) { _r.Add(HznRefusalKind.Setting, "missing property", $"{user} is not stated", DesignName); return null; }
        var e = _expr.Translate(source, user);
        if (e is null) return null;
        UseProjectRefs(e);
        if (e.LiteralSi is { } si)
            return new Dim(null, ToDbu(e.Bearing ? si : si * _siteScale, source, user), true);
        return new Dim(e.Text, 0, e.Bearing);
    }

    private Dim[]? Triple(string? source, string user)
    {
        if (source is null) { _r.Add(HznRefusalKind.Setting, "missing property", $"{user} is not stated", DesignName); return null; }
        var parts = HznExpressionTranslator.SplitTopLevel(source, ',');
        if (parts.Count != 3)
        {
            _r.Add(HznRefusalKind.Expression, "position", $"'{source}' is not three comma-separated values", user);
            return null;
        }
        var dims = new Dim?[3];
        for (int i = 0; i < 3; i++) dims[i] = Len(parts[i], user);
        return dims.All(d => d is not null) ? [dims[0]!, dims[1]!, dims[2]!] : null;
    }

    private double? EvalMetres(HznExpr e, string user)
    {
        if (e.LiteralSi is { } si) return e.Bearing ? si : si * _siteScale;
        try { return _res!.Evaluate(e.Text, _siteUnit).AsReal(); }
        catch (Exception ex) when (ex is ExpressionException or InvalidCastException)
        {
            _r.Add(HznRefusalKind.Expression, "expression", $"'{e.Text}' does not evaluate: {ex.Message}", user);
            return null;
        }
    }

    private Dim AsBearing(Dim d) => d.Bearing || d.IsLiteral ? d : new Dim($"({d.Text})*1{_siteUnit}", 0, true);

    private string TextOf(Dim d) => d.Text ?? LiteralText(d.Dbu);

    private Dim Add(Dim a, Dim b)
    {
        if (a.IsZeroLiteral) return b;
        if (b.IsZeroLiteral) return a;
        if (a.IsLiteral && b.IsLiteral) return new Dim(null, a.Dbu + b.Dbu, true);
        a = AsBearing(a);
        b = AsBearing(b);
        return new Dim($"({TextOf(a)})+({TextOf(b)})", 0, true);
    }

    private static readonly System.Text.RegularExpressions.Regex SimpleNegative = new(@"^-[A-Za-z_][A-Za-z0-9_]*$|^-[0-9.]+[A-Za-z]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private Dim Neg(Dim a) =>
        a.IsLiteral ? new Dim(null, -a.Dbu, true)
        : SimpleNegative.IsMatch(a.Text!) ? new Dim(a.Text![1..], 0, a.Bearing)
        : new Dim($"-({a.Text})", 0, a.Bearing);

    private double Metres(Dim d)
    {
        if (d.IsLiteral) return d.Dbu / (1e6 * HznToC3d.DbuPerMicron);
        try { return _res!.Evaluate(d.Text!, _siteUnit).AsReal(); }
        catch (Exception e) when (e is ExpressionException or InvalidCastException)
        {
            _r.Add(HznRefusalKind.Expression, "expression", $"'{d.Text}' does not evaluate: {e.Message}", DesignName);
            return double.NaN;
        }
    }

    /// <summary>A corner and a size, the size made positive: a negative size moves the corner by it (a box is Min + Size).</summary>
    private (Dim Min, Dim Size) Normalised(Dim min, Dim size)
    {
        double m = Metres(size);
        return m < 0 ? (Add(min, size), Neg(size)) : (min, size);
    }

    private void Put(IC3dBindable owner, string property, int k, Dim d)
    {
        var spec = C3dBindings.SpecOf(owner.GetType(), property)!;   // every call names a field of C3dBindings.Fields
        if (d.IsLiteral) C3dBindings.SetNumber(owner, spec, k, d.Dbu);
        else C3dBindings.SetExpr(owner, spec, k, new C3dExpr(d.Text!, _siteUnit));
    }

    private void PutAll(IC3dBindable owner, string property, Dim[] dims)
    {
        for (int k = 0; k < dims.Length; k++) Put(owner, property, k, dims[k]);
    }

    /// <summary>brief-hzn-13 — a polyline's offset and outline into a sheet or prism: the outline sized first (every point a number,
    /// the literal's or 0 until the document resolves), then each component that is an expression bound in place.</summary>
    private void PutOutline(C3dObject owner, string offsetProperty, string outlineProperty, PolyInfo q)
    {
        Put(owner, offsetProperty, 0, q.Offset);
        var spec = C3dBindings.SpecOf(owner.GetType(), outlineProperty)!;
        var list = (List<C3dPoint2>)spec.Info.GetValue(owner)!;
        list.Clear();
        list.AddRange(q.Outline.Select(d => new C3dPoint2(d[0].Dbu, d[1].Dbu)));
        for (int e = 0; e < q.Outline.Count; e++)
            for (int k = 0; k < 2; k++)
                if (!q.Outline[e][k].IsLiteral) C3dBindings.SetExpr(owner, spec.ElementAt(e), k, new C3dExpr(q.Outline[e][k].Text!, _siteUnit));
    }

    /// <summary>A value that must be a plain number (a segment count, a flag): evaluated, never an expression.</summary>
    private double? Plain(string? source, string user)
    {
        if (source is null) return null;
        var e = _expr.Translate(source, user);
        if (e is null) return null;
        UseProjectRefs(e);
        try { return _res!.Evaluate(e.Text, null).AsReal(); }
        catch (Exception ex) when (ex is ExpressionException or InvalidCastException)
        {
            _r.Add(HznRefusalKind.Expression, "expression", $"'{source}' does not evaluate to a number: {ex.Message}", user);
            return null;
        }
    }

    private static bool IsTrue(string? s) => string.Equals(s?.Trim(), "True", StringComparison.OrdinalIgnoreCase);

    // ── angles ────────────────────────────────────────────────────────────────────────────────────

    private sealed record Ang(string? Text, double Deg);

    private Ang? Angle(string? source, string user)
    {
        var e = source is null ? null : _expr.Translate(source, user);
        if (e is null) return null;
        UseProjectRefs(e);
        if (!e.Bearing && !e.IsZero)
        {
            _r.Add(HznRefusalKind.Expression, "angle without a unit",
                   $"'{source}' states an angle with no unit; whether Hertzon reads it as degrees or radians is not established", user);
            return null;
        }
        if (e.LiteralSi is { } rad) return new Ang(null, e.IsZero ? 0 : rad * 180.0 / Math.PI);
        return new Ang(e.Text, 0);
    }

    // ── objects ───────────────────────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, HznObject> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Folded?> _folded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folding = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keptTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HznObject> _regions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _c3dName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SheetGeom> _sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, C3dObject> _top = new(StringComparer.Ordinal);
    private readonly List<string> _topOrder = [];

    /// <summary>A rectangle sheet that is an exact rectangle in an axis plane: what a port needs.</summary>
    /// <summary>A sheet a port or element can take: its rectangle on a plane, and (brief-hzn-14) the turn about the vertical through its
    /// centre when it stands in a vertical plane that is no axis plane, in degrees as a port's <c>Angle</c>.</summary>
    internal sealed record SheetGeom(C3dPlane Plane, Dim Offset, Dim[] Min, Dim[] Size, bool Placed, double Angle = 0);

    private sealed record Folded(C3dObject Tree, SheetGeom? Sheet, IReadOnlyList<string> Consumed, IReadOnlyList<HznStepOrigin> Steps);

    private void Objects()
    {
        // The view makes fresh wrappers on every read; one list is read once so the same objects are compared.
        var objects = _design.Objects;
        foreach (var o in objects)
        {
            string name = o.Name ?? "";
            if (name.Length == 0) { _r.Add(HznRefusalKind.Setting, "object without a name", "every object needs a name", DesignName); continue; }
            if (!_byName.TryAdd(name, o))
                _r.Add(HznRefusalKind.Setting, $"object name '{name}'", "two objects have the same name", DesignName);
        }
        // brief-hzn-11 — a design with no drawn region and no Auto Open is closed: what no object fills is outside Hertzon's solved
        // space, and that space's walls are perfect conductors.
        string autoOpen = _design.Json.GetObject("settings")?.GetString("AutoOpen") ?? "Off";
        _closedDomain = !objects.Any(IsRegion) && autoOpen.Equals("Off", StringComparison.OrdinalIgnoreCase);
        // Tools a Subtract keeps are solids of their own AND the Subtract's operands: they are emitted once, inside it.
        foreach (var o in objects)
            foreach (var s in o.History)
                if (s.Suppressed != true && s.Kind is "Subtract" && IsTrue(s.Props["KeepOriginals"]))
                    foreach (string t in HznExpressionTranslator.SplitTopLevel(s.Props["Tools"] ?? "", ','))
                        if (t.Length > 0) _keptTools.Add(t);

        foreach (var o in objects)
        {
            string name = o.Name ?? "";
            if (name.Length == 0 || !ReferenceEquals(_byName.GetValueOrDefault(name), o)) continue;
            if (IsRegion(o)) { Region(o); continue; }
            if (!string.IsNullOrEmpty(o.ConsumedBy) || _keptTools.Contains(name)) continue;
            if (FoldObject(name) is { } f) EmitTop(o, f);
            else
            {
                _failed.Add(name);
                if (o.Material is { Length: > 0 } m) Material(m, name);
            }
        }
    }

    /// <summary>brief-hzn-11 — true when the design draws no region and has no Auto Open (<see cref="Objects"/>).</summary>
    private bool _closedDomain;

    /// <summary>brief-hzn-11 — the tools a Subtract consumed from a non-conductor in a closed design, kept as perfect-conductor
    /// solids, by Hertzon name: a boundary on one dresses its walls (<see cref="Conductor"/>).</summary>
    private readonly Dictionary<string, C3dObject> _voidTools = new(StringComparer.Ordinal);

    /// <summary>brief-hzn-11 — each Subtract of solids in a closed design, with its Hertzon object name and its tools' names: a hole
    /// in the object that bounds the whole model (<see cref="Holes"/>).</summary>
    private readonly List<(C3dBoolean Made, string Name, IReadOnlyList<string> Tools)> _holeCandidates = [];

    /// <summary>
    /// brief-hzn-11 — in a closed design, a hole a Subtract cuts from a non-conductor that bounds the whole model is outside Hertzon's
    /// solved space, and its walls are perfect conductors (Hertzon's WR90 post designs: the post is a hole in the vacuum guide, and
    /// Hertzon's own solve reflects 98 % from it at 8 GHz). circuitRF's air box fills whatever no object fills, so the hole is kept as
    /// a perfect-conductor solid in its place. Only a blank whose bounds are the model's: a hole in a smaller object may lie inside
    /// another, which then fills it.
    /// </summary>
    private void Holes()
    {
        if (_holeCandidates.Count == 0 || Content() is not { } all) return;
        foreach (var (made, name, toolNames) in _holeCandidates)
        {
            if (made.Blank is not { } blank || IsConductorMaterial(C3dValidation.EffectiveMaterial(blank))) continue;
            if (Bounds(blank) is not { } own || !own.Min.SequenceEqual(all.Min) || !own.Max.SequenceEqual(all.Max)) continue;
            made.KeepTools = true;
            var (pec, pecRole) = UseMaterial("pec", name, sheet: false);
            foreach (var (tool, t) in made.Tools.Zip(toolNames))
            {
                var carrier = tool;
                while (carrier is C3dOperation && C3dOperands.Inner(carrier) is { } inner) carrier = inner;
                carrier.Material = pec;
                carrier.Role = pecRole;
                _voidTools[t] = carrier;
                _notes.Add($"'{t}' is subtracted from '{name}', which bounds this closed design, so its volume is outside Hertzon's solved " +
                           "space, whose walls are perfect conductors: it is kept as a perfect-conductor solid in its place.");
            }
        }
    }

    /// <summary>Objects whose history could not be folded: assignments on them are skipped, not refused a second time.</summary>
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    private static bool IsRegion(HznObject o) =>
        o.History.Count > 0 && o.History.All(s => s.Kind == "CreateRegion" || s.Suppressed == true) && o.History.Any(s => s.Kind == "CreateRegion");

    private void EmitTop(HznObject o, Folded f)
    {
        string hzn = o.Name!;
        string name = HznNames.C3dObjectName(hzn);
        string? renamed = null;
        if (name != hzn)
        {
            renamed = hzn;
            _notes.Add($"Object '{hzn}' is '{name}' in the document: 'airbox' is reserved there.");
        }
        _c3dName[hzn] = name;
        Dress(f.Tree, o, name, topLevel: true);
        _top[name] = f.Tree;
        _topOrder.Add(name);
        _doc.Objects.Add(f.Tree);
        _prov.Objects[name] = new HznObjectOrigin(hzn, f.Steps, f.Consumed, renamed);
        if (f.Sheet is { } sh) _sheets[hzn] = sh;
    }

    /// <summary>The object's name, material, role and flags onto the tree: a wrapper takes the name, group and flags, and the
    /// innermost Blank takes the material and role (the result's); a plain shape takes them all.</summary>
    private void Dress(C3dObject tree, HznObject o, string name, bool topLevel)
    {
        tree.Name = name;
        if (topLevel)
        {
            tree.Group = string.IsNullOrEmpty(o.Group) ? null : o.Group;
            tree.Hidden = o.Visible == false;
            tree.Model = o.Model != false;
        }
        var carrier = tree;
        while (carrier is C3dOperation && C3dOperands.Inner(carrier) is { } inner) carrier = inner;
        if (o.Material is { Length: > 0 } mat)
        {
            var (matName, role) = UseMaterial(mat, o.Name!, carrier is C3dSheet);
            carrier.Material = matName;
            carrier.Role = role;
        }
        else _r.Add(HznRefusalKind.Material, "no material", "the object states no material", o.Name!);
        if (o.SolveInside == false && o.Model != false && !IsConductorMaterial(o.Material))
            _r.Add(HznRefusalKind.Setting, "solve_inside false on a non-conductor",
                   "circuitRF has no solid whose interior is left out of the solve other than a conductor", o.Name!);
        if (!string.IsNullOrEmpty(o.Json.GetString("orientation")) && o.Json.GetString("orientation") != "Global")
            _r.Add(HznRefusalKind.Setting, "orientation other than Global", "only the global coordinate system is translated", o.Name!);
    }

    // ── folding one object's history ──────────────────────────────────────────────────────────────

    private enum Pend { None, Rect, Circle, Poly, VPoly }

    private sealed class State
    {
        public C3dObject? Root;
        public SheetGeom? Sheet;
        public Pend Pending;
        public Dictionary<string, string> Props = [];
        public Dim[]? Origin;
        public readonly List<(C3dAxis Axis, Ang Angle)> Rot = [];
        public bool Mirror;
        /// <summary>brief-hzn-14 — an upright polyline's own turn about z and its origin: the sheet's frame, not a move the history
        /// made, so they do not make it Placed (a port can still take it).</summary>
        public double? OwnTurn;
        public C3dPoint3 OwnOrigin;
        public bool Placed => Mirror || Rot.Count > 0 || (Origin is not null && Origin.Any(d => !d.IsZeroLiteral));
        public readonly List<string> Consumed = [];
        public readonly List<HznStepOrigin> Steps = [];
    }

    private Folded? FoldObject(string name)
    {
        if (_folded.TryGetValue(name, out var done)) return done;
        if (!_byName.TryGetValue(name, out var o)) return null;
        if (!_folding.Add(name))
        {
            _r.Add(HznRefusalKind.Operation, "boolean cycle", "objects consume each other", name);
            return null;
        }
        Folded? result = null;
        try { result = Fold(o); if (result is null) _failed.Add(name); }
        finally { _folding.Remove(name); }
        return _folded[name] = result;
    }

    private Folded? Fold(HznObject o)
    {
        string name = o.Name!;
        var st = new State();
        bool failed = false;
        int index = -1;
        foreach (var step in o.History)
        {
            index++;
            string kind = step.Kind ?? "";
            bool suppressed = step.Suppressed == true;
            if (suppressed)
            {
                st.Steps.Add(new HznStepOrigin(index, kind, true, false));
                continue;
            }
            st.Steps.Add(new HznStepOrigin(index, kind, false, true));
            if (!Apply(st, o, step, kind)) failed = true;
            // Once the shape itself failed, later steps have nothing to change; their refusals would only repeat it.
            if (failed && st.Root is null && st.Pending == Pend.None) break;
        }
        if (failed) return null;
        if (!FinalisePending(st, name)) return null;
        if (st.Root is null)
        {
            _r.Add(HznRefusalKind.Setting, "object without a shape", "every step that creates its shape is suppressed or absent", name);
            return null;
        }
        FlushPlacement(st, name);
        return new Folded(st.Root, st.Sheet, st.Consumed, st.Steps);
    }

    private bool Apply(State st, HznObject o, HznStep step, string kind)
    {
        string name = o.Name!;
        string user = $"{name} ({kind})";
        var p = step.Props.Names.ToDictionary(n => n, n => step.Props[n] ?? "", StringComparer.Ordinal);
        switch (kind)
        {
            case "CreateBox": return CreateBox(st, p, user);
            case "CreateCylinder": return CreateCylinder(st, p, user);
            case "CreateSphere": return CreateSphere(st, p, user);
            case "CreateRectangle": return CreateRectangle(st, p, user);
            case "CreateCircle": return CreateCircle(st, p, user);
            case "CreateEllipse": return CreateEllipse(st, p, user);
            case "CreatePolyline": return CreatePolyline(st, p, user);
            case "SweepAlongVector": return Sweep(st, p, user);
            case "Subtract" or "Unite" or "Intersect": return Boolean(st, o, p, kind, user);
            case "Move": return Move(st, p, user);
            case "Rotate": return Rotate(st, p, user);
            case "Mirror": return Mirror(st, p, user);
            case "Fillet" or "Chamfer":
                _r.Add(HznRefusalKind.Operation, kind,
                       "its edges and vertices are named by index alone and the step records no signature to find them by; the index is never trusted", name);
                return false;
            case "Import":
                _r.Add(HznRefusalKind.CreationStep, "Import", "a file import is not translated yet (it needs the geometry worker and a scratch folder)", name);
                return false;
            case "CreateBondwire":
                _r.Add(HznRefusalKind.CreationStep, "CreateBondwire", "no defined correspondence between its profile families and a .c3d wire's points", name);
                return false;
            case "CreateRegion":
                _r.Add(HznRefusalKind.CreationStep, "CreateRegion", "a region that is not the object's only step is not translated", name);
                return false;
            case "CreateCone" or "CreateTorus" or "CreateHelix" or "CreateSpiral" or "CreateRegularPolygon"
                or "CreateRegularPolyhedron":
                _r.Add(HznRefusalKind.CreationStep, kind, "circuitRF has no primitive that states it exactly", name);
                return false;
            default:
                _r.Add(kind.StartsWith("Create", StringComparison.Ordinal) ? HznRefusalKind.CreationStep : HznRefusalKind.Operation,
                       kind, "not translated yet", name);
                return false;
        }
    }

    private bool NeedShape(State st, string user)
    {
        if (st.Root is not null || st.Pending != Pend.None)
        {
            _r.Add(HznRefusalKind.Setting, "second shape", "an object's history has one step that creates its shape", user);
            return false;
        }
        return true;
    }

    private bool NeedRoot(State st, string kind, string name)
    {
        if (!FinalisePending(st, name)) return false;
        if (st.Root is null)
        {
            _r.Add(HznRefusalKind.Operation, kind, "there is no shape for it to change", name);
            return false;
        }
        return true;
    }

    private static bool CoordGlobal(Dictionary<string, string> props) =>
        !props.TryGetValue("CoordinateSystem", out string? cs) || string.IsNullOrEmpty(cs) || cs == "Global";

    private bool Segments(Dictionary<string, string> p, string user, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (!p.TryGetValue(k, out string? v)) continue;
            if (Plain(v, user) is not { } n) return false;
            if (n != 0)
            {
                _r.Add(HznRefusalKind.CreationStep, user.Split(' ')[^1].Trim('(', ')'), $"{k} {v} makes a faceted shape; only a true circle or sphere (0) is translated", user.Split(' ')[0]);
                return false;
            }
        }
        return true;
    }

    private static C3dAxis? AxisOf(string? s) => s?.Trim() switch { "X" => C3dAxis.X, "Y" => C3dAxis.Y, "Z" => C3dAxis.Z, _ => null };

    private bool CreateBox(State st, Dictionary<string, string> p, string user)
    {
        if (!NeedShape(st, user)) return false;
        var pos = Triple(p.GetValueOrDefault("Position"), user);
        var x = Len(p.GetValueOrDefault("XSize"), user);
        var y = Len(p.GetValueOrDefault("YSize"), user);
        var z = Len(p.GetValueOrDefault("ZSize"), user);
        if (pos is null || x is null || y is null || z is null) return false;
        var sizes = new[] { x, y, z };
        var min = new Dim[3];
        var size = new Dim[3];
        for (int i = 0; i < 3; i++) (min[i], size[i]) = Normalised(pos[i], sizes[i]);
        var box = new C3dBox();
        PutAll(box, nameof(C3dBox.Min), min);
        PutAll(box, nameof(C3dBox.Size), size);
        st.Root = box;
        return true;
    }

    private bool CreateCylinder(State st, Dictionary<string, string> p, string user)
    {
        if (!NeedShape(st, user)) return false;
        if (!Segments(p, user, "NumberOfSegments")) return false;
        var centre = Triple(p.GetValueOrDefault("CenterPosition"), user);
        var radius = Len(p.GetValueOrDefault("Radius"), user);
        var height = Len(p.GetValueOrDefault("Height"), user);
        if (AxisOf(p.GetValueOrDefault("Axis")) is not { } axis)
        {
            _r.Add(HznRefusalKind.CreationStep, "CreateCylinder", $"its axis '{p.GetValueOrDefault("Axis")}' is not X, Y or Z", user);
            return false;
        }
        if (centre is null || radius is null || height is null) return false;
        st.Root = Cylinder(centre, axis, radius, height);
        return true;
    }

    private C3dCylinder Cylinder(Dim[] centre, C3dAxis axis, Dim radius, Dim height)
    {
        int a = (int)axis;
        var baseAt = (Dim[])centre.Clone();
        if (Metres(height) < 0)
        {
            baseAt[a] = Add(centre[a], height);
            height = Neg(height);
        }
        var c = new C3dCylinder { Axis = axis };
        PutAll(c, nameof(C3dCylinder.Base), baseAt);
        Put(c, nameof(C3dCylinder.Length), 0, height);
        Put(c, nameof(C3dCylinder.Radius), 0, radius);
        return c;
    }

    private bool CreateSphere(State st, Dictionary<string, string> p, string user)
    {
        if (!NeedShape(st, user)) return false;
        // brief-hzn-12 Part 2 — NumberOfSegments above 0 facets the sphere, on NumberOfVertices points (black-box: the segment count
        // itself changes nothing of the solid); 0 is a true sphere whatever NumberOfVertices says.
        int facets = 0;
        if (p.TryGetValue("NumberOfSegments", out string? segText))
        {
            if (Plain(segText, user) is not { } seg) return false;
            if (seg != 0)
            {
                if (Plain(p.GetValueOrDefault("NumberOfVertices") ?? "", user) is not { } nv || nv != Math.Floor(nv) ||
                    nv < C3dFacetedSphere.MinVertices || nv > C3dFacetedSphere.MaxVertices)
                {
                    _r.Add(HznRefusalKind.CreationStep, "CreateSphere faceted",
                           $"NumberOfVertices '{p.GetValueOrDefault("NumberOfVertices")}' is not a whole number from {C3dFacetedSphere.MinVertices} to {C3dFacetedSphere.MaxVertices}",
                           user.Split(' ')[0]);
                    return false;
                }
                facets = (int)nv;
            }
        }
        var centre = Triple(p.GetValueOrDefault("CenterPosition"), user);
        var radius = Len(p.GetValueOrDefault("Radius"), user);
        if (centre is null || radius is null) return false;
        var s = new C3dSphere { FacetVertices = facets };
        PutAll(s, nameof(C3dSphere.Centre), centre);
        Put(s, nameof(C3dSphere.Radius), 0, radius);
        st.Root = s;
        return true;
    }

    // A rectangle's plane: the axis is its normal; the other two sizes are its extents, in (u, v) order of the .c3d plane.
    private static (C3dPlane Plane, int U, int V) PlaneOf(C3dAxis normal) => normal switch
    {
        C3dAxis.X => (C3dPlane.YZ, 1, 2),
        C3dAxis.Y => (C3dPlane.XZ, 0, 2),
        _ => (C3dPlane.XY, 0, 1),
    };

    private sealed record RectInfo(C3dAxis Normal, Dim[] Pos, Dim[] Min2, Dim[] Size2);
    /// <summary>A circle, or (brief-hzn-13) an ellipse when <paramref name="RadiusV"/> is set: its second radius, along the plane's v.</summary>
    private sealed record CircleInfo(C3dAxis Normal, Dim[] Centre, Dim Radius, Dim? RadiusV = null);
    /// <summary>A polyline in an axis plane: its offset and each point's (u, v), each component a number or an expression (brief-hzn-13).
    /// <paramref name="Rect"/> is its rectangle when it is one (brief-hzn-14: a port may take it).</summary>
    private sealed record PolyInfo(C3dPlane Plane, C3dAxis Normal, Dim Offset, List<Dim[]> Outline, SheetGeom? Rect = null);

    /// <summary>brief-hzn-14 R-hzn14-2 — a polyline standing in a vertical plane that is no axis plane: a sheet on <paramref name="Plane"/>
    /// (XZ or YZ) at offset 0, its outline (distance along the plane from <paramref name="Origin"/>, z), turned about z by
    /// <paramref name="Degrees"/> and moved to <paramref name="Origin"/> by its placement. <paramref name="Rect"/> is the port sheet it
    /// is when it is a rectangle.</summary>
    private sealed record VPolyInfo(C3dPlane Plane, double Degrees, C3dPoint3 Origin, List<Dim[]> Outline, SheetGeom? Rect);

    private object? _pendingInfo;

    private bool CreateRectangle(State st, Dictionary<string, string> p, string user)
    {
        if (!NeedShape(st, user)) return false;
        if (AxisOf(p.GetValueOrDefault("Axis")) is not { } normal)
        {
            _r.Add(HznRefusalKind.CreationStep, "CreateRectangle", $"its axis '{p.GetValueOrDefault("Axis")}' is not X, Y or Z", user);
            return false;
        }
        var pos = Triple(p.GetValueOrDefault("Position"), user);
        var (_, u, v) = PlaneOf(normal);
        string[] sizeKeys = ["XSize", "YSize", "ZSize"];
        var su = Len(p.GetValueOrDefault(sizeKeys[u]), user);
        var sv = Len(p.GetValueOrDefault(sizeKeys[v]), user);
        if (pos is null || su is null || sv is null) return false;
        var (mu, nu) = Normalised(pos[u], su);
        var (mv, nv) = Normalised(pos[v], sv);
        st.Pending = Pend.Rect;
        _pendingInfo = new RectInfo(normal, pos, [mu, mv], [nu, nv]);
        return true;
    }

    private bool CreateCircle(State st, Dictionary<string, string> p, string user)
    {
        if (!NeedShape(st, user)) return false;
        if (!Segments(p, user, "NumberOfSegments")) return false;
        if (AxisOf(p.GetValueOrDefault("Axis")) is not { } normal)
        {
            _r.Add(HznRefusalKind.CreationStep, "CreateCircle", $"its axis '{p.GetValueOrDefault("Axis")}' is not X, Y or Z", user);
            return false;
        }
        var centre = Triple(p.GetValueOrDefault("CenterPosition"), user);
        var radius = Len(p.GetValueOrDefault("Radius"), user);
        if (centre is null || radius is null) return false;
        st.Pending = Pend.Circle;
        _pendingInfo = new CircleInfo(normal, centre, radius);
        return true;
    }

    /// <summary>brief-hzn-13 R-hzn13-3 — an ellipse: its major radius along x (established black-box: a 2 × 1 bounding box from
    /// MajorRadius 2 and Ratio 0.5 on Axis Z), its minor radius MajorRadius × Ratio. On Axis X or Y the major axis's direction is not
    /// established, so it is refused rather than guessed.</summary>
    private bool CreateEllipse(State st, Dictionary<string, string> p, string user)
    {
        if (!NeedShape(st, user)) return false;
        if (AxisOf(p.GetValueOrDefault("Axis")) is not C3dAxis.Z)
        {
            _r.Add(HznRefusalKind.CreationStep, "CreateEllipse off Axis Z",
                   $"its axis is '{p.GetValueOrDefault("Axis")}'; which way the major radius lies is known only for Axis Z (along x)", user.Split(' ')[0]);
            return false;
        }
        var centre = Triple(p.GetValueOrDefault("CenterPosition"), user);
        var major = Len(p.GetValueOrDefault("MajorRadius"), user);
        if (centre is null || major is null) return false;
        if (p.GetValueOrDefault("Ratio") is not { } ratioText) { _r.Add(HznRefusalKind.Setting, "missing property", $"{user} Ratio is not stated", DesignName); return false; }
        var ratio = _expr.Translate(ratioText, user);
        if (ratio is null) return false;
        UseProjectRefs(ratio);
        Dim minor;
        if (major.IsLiteral && ratio.LiteralSi is { } rs && !ratio.Bearing)
            minor = new Dim(null, (long)Math.Round(major.Dbu * rs, MidpointRounding.AwayFromZero), true);
        else minor = new Dim($"({TextOf(major)})*({ratio.Text})", 0, major.Bearing);
        st.Pending = Pend.Circle;
        _pendingInfo = new CircleInfo(C3dAxis.Z, centre, major, minor);
        return true;
    }

    private bool CreatePolyline(State st, Dictionary<string, string> p, string user)
    {
        if (!NeedShape(st, user)) return false;
        string name = user.Split(' ')[0];
        bool ok = true;
        if (!IsTrue(p.GetValueOrDefault("Closed")) || !IsTrue(p.GetValueOrDefault("Covered")))
        {
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline", "only a closed, covered polyline (a sheet) is translated", name);
            ok = false;
        }
        string segments = p.GetValueOrDefault("SegmentTypes") ?? "";
        if (segments.Length > 0 && HznExpressionTranslator.SplitTopLevel(segments, ',').Any(s => s != "Line"))
        {
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline with arcs or splines", $"segment types '{segments}' are not all straight lines", name);
            ok = false;
        }
        if (!string.IsNullOrEmpty(p.GetValueOrDefault("Holes")))
        {
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline with holes", "holes in a polyline are not translated", name);
            ok = false;
        }
        if (!ok) return false;

        // brief-hzn-13 R-hzn13-3 — each component keeps its expression (Len): the in-plane ones go to the outline's points, the
        // normal one to the offset. The plane test below runs on the EVALUATED points.
        var pts = new List<double[]>();
        var dims = new List<Dim[]>();
        foreach (string text in (p.GetValueOrDefault("Points") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var comps = HznExpressionTranslator.SplitTopLevel(text, ',');
            if (comps.Count != 3) { _r.Add(HznRefusalKind.Expression, "polyline point", $"'{text}' is not three values", user); return false; }
            var m = new double[3];
            var d = new Dim[3];
            for (int i = 0; i < 3; i++)
            {
                if (Len(comps[i], user) is not { } dim) return false;
                double metres = Metres(dim);
                if (double.IsNaN(metres)) return false;
                (d[i], m[i]) = (dim, metres);
            }
            pts.Add(m);
            dims.Add(d);
        }
        if (pts.Count < 3) { _r.Add(HznRefusalKind.CreationStep, "CreatePolyline", "it has fewer than three points", name); return false; }
        long Dbu(double metres) => (long)Math.Round(metres * 1e6 * HznToC3d.DbuPerMicron, MidpointRounding.AwayFromZero);
        var axes = new List<C3dAxis>();
        for (int a = 0; a < 3; a++)
            if (pts.All(q => Dbu(q[a]) == Dbu(pts[0][a]))) axes.Add((C3dAxis)a);
        if (axes.Count != 1)
        {
            if (VerticalPolyline(pts, dims, name, Dbu) is { } vertical)
            {
                st.Pending = Pend.VPoly;
                _pendingInfo = vertical;
                return true;
            }
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline outside an axis plane",
                   "its points lie neither in one plane perpendicular to X, Y or Z nor in one upright plane", name);
            return false;
        }
        var normal = axes[0];
        var (plane, ui, vi) = PlaneOf(normal);
        int ni = (int)normal;
        var offset = dims[0][ni];
        if (dims.Any(q => TextOf(q[ni]) != TextOf(offset)))
            _notes.Add($"{name}: its points spell their {normal} coordinate differently ({string.Join(", ", dims.Select(q => TextOf(q[ni])).Distinct().Take(4).Select(t => $"'{t}'"))}), " +
                       $"which all evaluate to the plane it lies in; the sheet's offset is the first point's, '{TextOf(offset)}'.");
        st.Pending = Pend.Poly;
        var outline2 = dims.Select(q => new[] { q[ui], q[vi] }).ToList();
        _pendingInfo = new PolyInfo(plane, normal, offset, outline2, AxisRect(plane, offset, outline2, pts.Select(q => (Dbu(q[ui]), Dbu(q[vi]))).ToList()));
        return true;
    }

    /// <summary>brief-hzn-14 — an axis-plane polyline that is a rectangle (four points on two u and two v values) as the sheet a port
    /// takes, its corner and size keeping the points' expressions; null otherwise.</summary>
    private SheetGeom? AxisRect(C3dPlane plane, Dim offset, List<Dim[]> outline, List<(long U, long V)> at)
    {
        if (outline.Count != 4) return null;
        var us = at.Select(p => p.U).Distinct().ToList();
        var vs = at.Select(p => p.V).Distinct().ToList();
        if ((us.Count != 2 || vs.Count != 2) && plane == C3dPlane.XY) return TurnedXyRect(offset, at);
        if (us.Count != 2 || vs.Count != 2) return null;
        foreach (long u in us) foreach (long v in vs) if (!at.Contains((u, v))) return null;
        int iu0 = at.FindIndex(p => p.U == us.Min()), iu1 = at.FindIndex(p => p.U == us.Max());
        int iv0 = at.FindIndex(p => p.V == vs.Min()), iv1 = at.FindIndex(p => p.V == vs.Max());
        var min = new[] { outline[iu0][0], outline[iv0][1] };
        var size = new[] { Add(outline[iu1][0], Neg(outline[iu0][0])), Add(outline[iv1][1], Neg(outline[iv0][1])) };
        return new SheetGeom(plane, offset, min, size, Placed: false);
    }

    /// <summary>brief-hzn-14 — four XY points that are a rectangle turned in the plane (each corner square and opposite sides equal, to
    /// two DBU: a turned port written as its corners rounds each of them): the turned port sheet it is, numbers only; null otherwise.</summary>
    private static SheetGeom? TurnedXyRect(Dim offset, List<(long U, long V)> at)
    {
        double Len(int i) { var (a, b) = (at[i], at[(i + 1) % 4]); return Math.Sqrt((double)(b.U - a.U) * (b.U - a.U) + (double)(b.V - a.V) * (b.V - a.V)); }
        double w = Len(0), l = Len(1);
        if (w < 1 || l < 1 || Math.Abs(w - Len(2)) > 2 || Math.Abs(l - Len(3)) > 2) return null;
        double cu = at.Average(p => p.U), cv = at.Average(p => p.V);
        for (int i = 0; i < 4; i++)
        {
            double r = Math.Sqrt((at[i].U - cu) * (at[i].U - cu) + (at[i].V - cv) * (at[i].V - cv));
            if (Math.Abs(r - Math.Sqrt(w * w + l * l) / 2) > 2) return null;     // corners on one circle: every corner square
        }
        double degrees = Math.Atan2(at[1].V - at[0].V, at[1].U - at[0].U) * 180 / Math.PI;
        long R(double d) => (long)Math.Round(d, MidpointRounding.AwayFromZero);
        return new SheetGeom(C3dPlane.XY, offset, [new Dim(null, R(cu - w / 2), true), new Dim(null, R(cv - l / 2), true)],
                             [new Dim(null, R(w), true), new Dim(null, R(l), true)], Placed: false, Angle: degrees);
    }

    /// <summary>
    /// brief-hzn-14 R-hzn14-2 — a polyline whose points stand in one upright plane (their plan positions on one line, their heights
    /// spanning more than a DBU): the sheet it is, on XZ or YZ — whichever needs the smaller turn about z — with its outline as
    /// (distance along the line, z). The heights keep their expressions; the distances are numbers (a turn is not linear in the
    /// points' coordinates), noted when the points named variables. Null when the points are not so.
    /// </summary>
    private VPolyInfo? VerticalPolyline(List<double[]> pts, List<Dim[]> dims, string name, Func<double, long> dbu)
    {
        var plan = pts.Select(q => (X: dbu(q[0]), Y: dbu(q[1]))).ToList();
        var far = plan.OrderByDescending(p => (double)(p.X - plan[0].X) * (p.X - plan[0].X) + (double)(p.Y - plan[0].Y) * (p.Y - plan[0].Y)).First();
        double dx = far.X - plan[0].X, dy = far.Y - plan[0].Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1 || pts.Max(q => dbu(q[2])) - pts.Min(q => dbu(q[2])) < 1) return null;
        (dx, dy) = (dx / len, dy / len);
        if (plan.Any(p => Math.Abs((p.X - plan[0].X) * dy - (p.Y - plan[0].Y) * dx) > 1)) return null;   // off the line, past a DBU
        // The turn: XZ's u axis is +x turned by θ, YZ's is +y turned by θ; the line may run either way, so θ is taken in (−90°, 90°].
        static double Fold(double deg) { deg %= 180; if (deg > 90) deg -= 180; else if (deg <= -90) deg += 180; return deg; }
        double phi = Math.Atan2(dy, dx) * 180 / Math.PI;
        double xz = Fold(phi), yz = Fold(phi - 90);
        var (plane, degrees) = Math.Abs(xz) <= Math.Abs(yz) ? (C3dPlane.XZ, xz) : (C3dPlane.YZ, yz);
        double t = degrees * Math.PI / 180;
        var (ax, ay) = plane == C3dPlane.XZ ? (Math.Cos(t), Math.Sin(t)) : (-Math.Sin(t), Math.Cos(t));   // the plane's turned u axis
        var origin = new C3dPoint3(plan[0].X, plan[0].Y, 0);
        var s = plan.Select(p => (long)Math.Round((p.X - origin.X) * ax + (p.Y - origin.Y) * ay, MidpointRounding.AwayFromZero)).ToList();
        if (dims.Any(d => !d[0].IsLiteral || !d[1].IsLiteral))
            _notes.Add($"{name}: it stands in an upright plane turned {HznExpressionWriter.Digits(degrees)}° about z, so its points' x and y are numbers " +
                       "in the sheet; their heights keep their expressions.");
        var outline = dims.Select((d, k) => new[] { new Dim(null, s[k], true), d[2] }).ToList();

        // A rectangle: two heights, the same two plan points at each — the port sheet it is, turned about its centre.
        SheetGeom? rect = null;
        var z = pts.Select(q => dbu(q[2])).ToList();
        var levels = z.Distinct().OrderBy(v => v).ToList();
        if (pts.Count == 4 && levels.Count == 2)
        {
            var low = Enumerable.Range(0, 4).Where(k => z[k] == levels[0]).ToList();
            var high = Enumerable.Range(0, 4).Where(k => z[k] == levels[1]).ToList();
            var lowS = low.Select(k => s[k]).Order().ToList();
            var highS = high.Select(k => s[k]).Order().ToList();
            if (low.Count == 2 && lowS.SequenceEqual(highS) && lowS[1] > lowS[0])
            {
                double mid = (lowS[0] + lowS[1]) / 2.0;
                double cx = origin.X + mid * ax, cy = origin.Y + mid * ay;
                long width = lowS[1] - lowS[0];
                double cu = plane == C3dPlane.XZ ? cx : cy;
                long offset = (long)Math.Round(plane == C3dPlane.XZ ? cy : cx, MidpointRounding.AwayFromZero);
                long u0 = (long)Math.Round(cu - width / 2.0, MidpointRounding.AwayFromZero);
                var zLow = dims[low[0]][2];
                var zHigh = dims[high[0]][2];
                rect = new SheetGeom(plane, new Dim(null, offset, true), [new Dim(null, u0, true), zLow], [new Dim(null, width, true), Add(zHigh, Neg(zLow))],
                                     Placed: false, Angle: degrees);
            }
        }
        return new VPolyInfo(plane, degrees, origin, outline, rect);
    }

    /// <summary>A pending 2D shape that nothing swept becomes what it is: a rectangle, polyline, circle or ellipse a sheet.</summary>
    private bool FinalisePending(State st, string name)
    {
        switch (st.Pending)
        {
            case Pend.None: return true;
            case Pend.Rect:
            {
                var r = (RectInfo)_pendingInfo!;
                var (plane, _, _) = PlaneOf(r.Normal);
                var sheet = new C3dSheet { Plane = plane, Rect = new C3dRect() };
                Put(sheet, nameof(C3dSheet.Offset), 0, r.Pos[(int)r.Normal]);
                PutAll(sheet.Rect, nameof(C3dRect.Min), r.Min2);
                PutAll(sheet.Rect, nameof(C3dRect.Size), r.Size2);
                st.Root = sheet;
                st.Sheet = new SheetGeom(plane, r.Pos[(int)r.Normal], r.Min2, r.Size2, Placed: false);
                break;
            }
            case Pend.Poly:
            {
                var q = (PolyInfo)_pendingInfo!;
                var sheet = new C3dSheet { Plane = q.Plane };
                PutOutline(sheet, nameof(C3dSheet.Offset), nameof(C3dSheet.Outline), q);
                st.Root = sheet;
                st.Sheet = q.Rect;          // brief-hzn-14 — a rectangle drawn as a polyline is a port sheet too
                break;
            }
            case Pend.VPoly:
            {
                // brief-hzn-14 R-hzn14-2 — a sheet in an upright plane: on XZ or YZ, turned about z and moved by its placement
                var q = (VPolyInfo)_pendingInfo!;
                var sheet = new C3dSheet { Plane = q.Plane };
                PutOutline(sheet, nameof(C3dSheet.Offset), nameof(C3dSheet.Outline), new PolyInfo(q.Plane, C3dAxis.Z, Dim.Zero, q.Outline));
                st.OwnTurn = q.Degrees;           // FlushPlacement writes the frame, with any move the history adds
                st.OwnOrigin = q.Origin;
                st.Root = sheet;
                st.Sheet = q.Rect;
                break;
            }
            default:
            {
                // brief-hzn-13 R-hzn13-2 — an unswept circle or ellipse is an Ellipse sheet, its centre and radii kept as expressions.
                var c = (CircleInfo)_pendingInfo!;
                var (plane, u, v) = PlaneOf(c.Normal);
                var sheet = new C3dSheet { Plane = plane, Ellipse = new C3dEllipse() };
                Put(sheet, nameof(C3dSheet.Offset), 0, c.Centre[(int)c.Normal]);
                PutAll(sheet.Ellipse, nameof(C3dEllipse.Centre), [c.Centre[u], c.Centre[v]]);
                PutAll(sheet.Ellipse, nameof(C3dEllipse.Radii), [c.Radius, c.RadiusV ?? c.Radius]);
                st.Root = sheet;
                break;
            }
        }
        st.Pending = Pend.None;
        _pendingInfo = null;
        return true;
    }

    private bool Sweep(State st, Dictionary<string, string> p, string user)
    {
        string name = user.Split(' ')[0];
        if (st.Pending == Pend.None)
        {
            _r.Add(HznRefusalKind.Operation, "SweepAlongVector", $"on {(st.Root is null ? "nothing" : "a " + C3dObject.KindOf(st.Root).ToLowerInvariant())}; only a rectangle, circle or polyline in an axis plane swept along its normal is translated", name);
            return false;
        }
        if (st.Pending == Pend.VPoly)
        {
            _r.Add(HznRefusalKind.Operation, "SweepAlongVector of an upright polyline", "a polygon in a turned upright plane swept along a vector has no .c3d primitive", name);
            st.Pending = Pend.None;
            _pendingInfo = null;
            return false;
        }
        var v = Triple(p.GetValueOrDefault("Vector"), user);
        if (v is null) return false;
        C3dAxis normal = st.Pending switch
        {
            Pend.Rect => ((RectInfo)_pendingInfo!).Normal,
            Pend.Circle => ((CircleInfo)_pendingInfo!).Normal,
            _ => ((PolyInfo)_pendingInfo!).Normal,
        };
        for (int i = 0; i < 3; i++)
            if (i != (int)normal && !v[i].IsZeroLiteral)
            {
                _r.Add(HznRefusalKind.Operation, "SweepAlongVector off the normal", $"the vector '{p.GetValueOrDefault("Vector")}' is not along the shape's normal", name);
                return false;
            }
        var h = v[(int)normal];
        switch (st.Pending)
        {
            case Pend.Rect:
            {
                var r = (RectInfo)_pendingInfo!;
                var (_, u, w) = PlaneOf(r.Normal);
                var min = new Dim[3];
                var size = new Dim[3];
                int n = (int)r.Normal;
                (min[n], size[n]) = Normalised(r.Pos[n], h);
                (min[u], size[u]) = (r.Min2[0], r.Size2[0]);
                (min[w], size[w]) = (r.Min2[1], r.Size2[1]);
                var box = new C3dBox();
                PutAll(box, nameof(C3dBox.Min), min);
                PutAll(box, nameof(C3dBox.Size), size);
                st.Root = box;
                break;
            }
            case Pend.Circle:
            {
                var c = (CircleInfo)_pendingInfo!;
                if (c.RadiusV is not null)
                {
                    _r.Add(HznRefusalKind.Operation, "SweepAlongVector of an ellipse", "an elliptic cylinder has no .c3d primitive", name);
                    st.Pending = Pend.None;
                    _pendingInfo = null;
                    return false;
                }
                st.Root = Cylinder(c.Centre, c.Normal, c.Radius, h);
                break;
            }
            default:
            {
                var q = (PolyInfo)_pendingInfo!;
                var prism = new C3dPrism { Plane = q.Plane };
                PutOutline(prism, nameof(C3dPrism.Offset), nameof(C3dPrism.Outline), q);
                Put(prism, nameof(C3dPrism.Height), 0, h);
                st.Root = prism;
                break;
            }
        }
        st.Pending = Pend.None;
        _pendingInfo = null;
        return true;
    }

    // ── booleans ──────────────────────────────────────────────────────────────────────────────────

    private bool Boolean(State st, HznObject o, Dictionary<string, string> p, string kind, string user)
    {
        string name = o.Name!;
        if (!NeedRoot(st, kind, name)) return false;
        if (st.Root is C3dPolyline)
        {
            _r.Add(HznRefusalKind.Operation, $"{kind} on a polyline", "an open polyline is construction geometry, not a shape", name);
            return false;
        }
        // brief-hzn-10 R-hzn10-4 — on a sheet, the operands are coplanar sheets and the result is a .c3d boolean of sheets.
        bool onSheet = C3dSheetBooleans.IsSheet(st.Root!);
        bool keep = IsTrue(p.GetValueOrDefault("KeepOriginals"));
        if (keep && kind != "Subtract")
        {
            _r.Add(HznRefusalKind.Operation, $"{kind} keeping the originals", "only a Subtract has a keep-tools equivalent", name);
            return false;
        }
        var op = kind switch { "Subtract" => C3dBooleanOp.Subtract, "Unite" => C3dBooleanOp.Unite, _ => C3dBooleanOp.Intersect };
        var tools = new List<C3dObject>();
        bool ok = true;
        foreach (string t in HznExpressionTranslator.SplitTopLevel(p.GetValueOrDefault("Tools") ?? "", ','))
        {
            if (t.Length == 0) continue;
            if (!_byName.TryGetValue(t, out var toolObj))
            {
                _r.Add(HznRefusalKind.Operation, kind, $"it names the tool '{t}', which is not an object of the design", name);
                ok = false;
                continue;
            }
            // On a sheet, a solid tool is the item that stops it, whatever else is wrong with the tool.
            if (onSheet && FoldObject(t) is { } solidTool && !C3dSheetBooleans.IsSheet(solidTool.Tree))
            {
                _r.Add(HznRefusalKind.Operation, $"{kind} on a sheet",
                       $"the tool '{t}' is a {C3dObject.KindOf(solidTool.Tree).ToLowerInvariant()}; a .c3d boolean has sheets with sheets or solids with solids", name);
                ok = false;
                continue;
            }
            if (!keep && toolObj.ConsumedBy != name)
            {
                _r.Add(HznRefusalKind.Operation, kind, $"the tool '{t}' is not recorded as consumed by '{name}'", name);
                ok = false;
                continue;
            }
            var f = FoldObject(t);
            if (f is null) { ok = false; continue; }
            if (C3dSheetBooleans.IsSheet(f.Tree) != onSheet)
            {
                if (onSheet)
                    _r.Add(HznRefusalKind.Operation, $"{kind} on a sheet",
                           $"the tool '{t}' is a {C3dObject.KindOf(f.Tree).ToLowerInvariant()}; a .c3d boolean has sheets with sheets or solids with solids", name);
                else
                    _r.Add(HznRefusalKind.Operation, $"{kind} with a sheet tool", $"the tool '{t}' is a sheet; a .c3d boolean has sheets with sheets or solids with solids", name);
                ok = false;
                continue;
            }
            var tree = f.Tree;
            Dress(tree, toolObj, t, topLevel: false);
            tree.Hidden = false;
            tools.Add(tree);
            st.Consumed.Add(t);
            _prov.Objects[t] = new HznObjectOrigin(t, f.Steps, f.Consumed, null);
            _c3dName[t] = t;
        }
        if (!ok) return false;
        if (tools.Count == 0)
        {
            _r.Add(HznRefusalKind.Operation, kind, "it names no tool", name);
            return false;
        }
        FlushPlacement(st, name);
        var blank = st.Root!;
        blank.Name = "";
        var made = new C3dBoolean { Op = op, Blank = blank, Tools = tools, KeepTools = keep };
        if (op == C3dBooleanOp.Subtract && !keep && !onSheet && _closedDomain)
            _holeCandidates.Add((made, name, [.. HznExpressionTranslator.SplitTopLevel(p.GetValueOrDefault("Tools") ?? "", ',').Where(x => x.Length > 0)]));
        // brief-hzn-10 — a boolean of sheets needs them in one plane, each placed within it; otherwise the item stays, with the reason.
        // The offsets are compared resolved (a copy, in the variables' scope), since an operand's offset is often an expression.
        var probe = new C3dDocument { DbuPerMicron = HznToC3d.DbuPerMicron, Variables = _scratch.Variables, Objects = [C3dBooleans.Copy(made)] };
        bool resolved = C3dResolver.Resolve(probe, C3dCell.None).Ok;
        if (onSheet && resolved && C3dSheetBooleans.PlaneFaults((C3dBoolean)probe.Objects[0], name) is [var (operand, why), ..])
        {
            _r.Add(HznRefusalKind.Operation, $"{kind} on a sheet", $"'{(operand.StartsWith(name + ".", StringComparison.Ordinal) ? name : operand)}' {why}", name);
            return false;
        }
        st.Root = made;
        st.Sheet = null;
        return true;
    }

    // ── placement ─────────────────────────────────────────────────────────────────────────────────

    private void FlushPlacement(State st, string name)
    {
        if (st.Root is null) return;
        var pl = st.Root.Placement;
        bool placed = st.Placed;            // what the history moved, before the upright sheet's own frame is added
        pl.Rotate.Clear();
        pl.MirrorX = st.Mirror;
        if (st.OwnTurn is { } own)
        {
            // brief-hzn-14 — the upright sheet's frame: its turn first, its origin added to any move. A rotation or mirror the history
            // adds would act on the moved frame, which one placement cannot state.
            if (st.Mirror || st.Rot.Count > 0)
                _r.Add(HznRefusalKind.Operation, "Rotate or Mirror of an upright polyline", "its own turn and a later rotation about the origin are not one placement", name);
            if (own != 0) pl.Rotate.Add(new C3dRotation { Axis = C3dAxis.Z, Deg = own });
            var o = st.Origin ?? [Dim.Zero, Dim.Zero, Dim.Zero];
            st.Origin = [Add(o[0], new Dim(null, st.OwnOrigin.X, true)), Add(o[1], new Dim(null, st.OwnOrigin.Y, true)), o[2]];
        }
        if (st.Origin is { } origin) PutAll(pl, nameof(C3dPlacement.Origin), origin);
        foreach (var (axis, ang) in st.Rot)
        {
            var rot = new C3dRotation { Axis = axis };
            if (ang.Text is null) rot.Deg = ang.Deg;
            else C3dBindings.SetExpr(rot, C3dBindings.SpecOf(typeof(C3dRotation), nameof(C3dRotation.Deg))!, 0, new C3dExpr(ang.Text, C3dUnits.Degrees));
            pl.Rotate.Add(rot);
        }
        if (st.Sheet is { } sh && placed) st.Sheet = sh with { Placed = true };
        st.Origin = null;
        st.Rot.Clear();
        st.Mirror = false;
    }

    private bool Move(State st, Dictionary<string, string> p, string user)
    {
        string name = user.Split(' ')[0];
        if (!NeedRoot(st, "Move", name)) return false;
        var v = Triple(p.GetValueOrDefault("Vector"), user);
        if (v is null) return false;
        var origin = st.Origin ?? [Dim.Zero, Dim.Zero, Dim.Zero];
        st.Origin = [Add(origin[0], v[0]), Add(origin[1], v[1]), Add(origin[2], v[2])];
        return true;
    }

    private bool Rotate(State st, Dictionary<string, string> p, string user)
    {
        string name = user.Split(' ')[0];
        if (!NeedRoot(st, "Rotate", name)) return false;
        if (!CoordGlobal(p))
        {
            _r.Add(HznRefusalKind.Operation, "Rotate in a coordinate system", "only the global coordinate system is translated", name);
            return false;
        }
        if (AxisOf(p.GetValueOrDefault("Axis")) is not { } axis)
        {
            _r.Add(HznRefusalKind.Operation, "Rotate", $"its axis '{p.GetValueOrDefault("Axis")}' is not X, Y or Z", name);
            return false;
        }
        var ang = Angle(p.GetValueOrDefault("Angle"), user);
        var centre = Triple(p.GetValueOrDefault("Center") ?? "0,0,0", user);
        if (ang is null || centre is null) return false;
        if (st.Origin is not null && st.Origin.Any(d => !d.IsZeroLiteral))
        {
            _r.Add(HznRefusalKind.Operation, "Rotate after Move", "a placement is rotated first and moved last, so a rotation after a move needs the move rotated too; that is not stated exactly", name);
            return false;
        }
        st.Rot.Add((axis, ang));
        if (centre.Any(d => !d.IsZeroLiteral))
        {
            // p' = R(p - c) + c = R p + (c - R c): the move that follows the rotation.
            if (st.Rot.Count > 1)
            {
                _r.Add(HznRefusalKind.Operation, "Rotate about a centre after another rotation", "the centre's own move would have to be rotated by the earlier rotation", name);
                return false;
            }
            string a = ang.Text is null ? $"{ang.Deg.ToString("R", CultureInfo.InvariantCulture)}deg" : $"({ang.Text})";
            string C(int i) => $"({TextOf(AsBearing(centre[i]))})";
            string cos = $"cos({a})", sin = $"sin({a})";
            int i1 = ((int)axis + 1) % 3, i2 = ((int)axis + 2) % 3;
            // Right-handed rotation about `axis`: (i1, i2) turn as (x, y) do about z.
            string r1 = $"({C(i1)}*{cos}-{C(i2)}*{sin})";
            string r2 = $"({C(i1)}*{sin}+{C(i2)}*{cos})";
            var origin = new Dim[3];
            origin[(int)axis] = Dim.Zero;
            origin[i1] = new Dim($"{C(i1)}-{r1}", 0, true);
            origin[i2] = new Dim($"{C(i2)}-{r2}", 0, true);
            st.Origin = origin;
        }
        return true;
    }

    private bool Mirror(State st, Dictionary<string, string> p, string user)
    {
        string name = user.Split(' ')[0];
        if (!NeedRoot(st, "Mirror", name)) return false;
        if (st.Placed)
        {
            _r.Add(HznRefusalKind.Operation, "Mirror after a move or rotation", "a placement mirrors first, so a mirror after another placement is not stated exactly", name);
            return false;
        }
        var b = Triple(p.GetValueOrDefault("Base"), user);
        var n = Triple(p.GetValueOrDefault("Normal"), user);
        if (b is null || n is null) return false;
        int axis = -1;
        // The normal is a direction: three bare numbers, one of them ±1 and the others 0.
        var comps = HznExpressionTranslator.SplitTopLevel(p.GetValueOrDefault("Normal") ?? "", ',');
        var nums = comps.Select(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToArray();
        if (nums.Length == 3 && nums.Count(x => x == 0) == 2 && nums.Count(x => Math.Abs(x) == 1) == 1)
            axis = Array.FindIndex(nums, x => x != 0);
        if (axis < 0)
        {
            _r.Add(HznRefusalKind.Operation, "Mirror", $"its normal '{p.GetValueOrDefault("Normal")}' is not along X, Y or Z", name);
            return false;
        }
        st.Mirror = true;
        var twice = Add(b[axis], b[axis]);
        var origin = new[] { Dim.Zero, Dim.Zero, Dim.Zero };
        origin[axis] = twice;
        st.Origin = origin;
        // MirrorX negates x; a half turn about Y or Z then turns the mirror into one across y or z.
        if (axis == 1) st.Rot.Add((C3dAxis.Z, new Ang(null, 180)));
        if (axis == 2) st.Rot.Add((C3dAxis.Y, new Ang(null, 180)));
        return true;
    }

    // ── checks ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The finished document through the validators the 3D editor uses, so a translation that opens is a document that checks.</summary>
    private void Check()
    {
        AddProjectVariables();
        if (_r.Any) return;
        var resolution = C3dResolver.Resolve(_doc, C3dCell.None);
        foreach (string err in resolution.Errors)
            _r.Add(HznRefusalKind.Expression, "dimension", err, DesignName);
        foreach (var n in resolution.Notes) _notes.Add(n);
        if (_r.Any) return;
        foreach (var d in C3dValidation.Validate(_doc, isKnownMaterial: m => _technology?.FindMaterial(m) is not null))
            if (d.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error)
                _r.Add(HznRefusalKind.Setting, d.Id, d.Render(), DesignName);
    }
}
