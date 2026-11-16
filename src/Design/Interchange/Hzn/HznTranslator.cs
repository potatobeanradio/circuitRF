// HZN-2 — the translation itself: variables, dimensions, and each object's history folded into a .c3d tree.
// Boundaries, ports, setups, materials and face references are the partials beside this file.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
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
        Outside();                  // brief-hzn-19 — after the holes, which fill their own part of the box
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
            string c3dName = HznNames.C3dVariableName(name);
            if (C3dResolver.ValidateName(c3dName) is { } bad)
            {
                _r.Add(HznRefusalKind.Expression, $"variable name '{name}'", $"circuitRF cannot use it as a name: {bad}", DesignName);
                continue;
            }
            if (c3dName != name)
            {
                if (designVars.ContainsKey(c3dName))
                {
                    _r.Add(HznRefusalKind.Expression, $"variable name '{name}'",
                           $"circuitRF has a built-in of that name, and '{c3dName}', the name it would take here, is another variable of the design", DesignName);
                    continue;
                }
                _notes.Add($"Variable '{name}' is '{c3dName}' here: circuitRF has a built-in of that name. Saving writes it back as '{name}'.");
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
            _doc.Variables.Add(new C3dVariable { Name = c3dName, Expression = e.Text });
            _prov.Variables.Add(new HznVariableOrigin(c3dName, name, Project: false));
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
        // brief-hzn-17 R-hzn17-1 — a polyline's holes, numbers
        if (q.Holes is { Count: > 0 } holes)
        {
            var target = owner switch { C3dPrism pr => pr.Holes, C3dSheet sh => sh.Holes, _ => null };
            target?.AddRange(holes.Select(h => h.ToList()));
        }
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
    /// <summary>brief-hzn-15 — tools a boolean names that the file does not record as consumed, by the object whose history names them.</summary>
    private readonly Dictionary<string, string> _impliedConsumer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HznObject> _regions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _c3dName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SheetGeom> _sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, C3dObject> _top = new(StringComparer.Ordinal);
    private readonly List<string> _topOrder = [];

    /// <summary>A rectangle sheet that is an exact rectangle in an axis plane: what a port needs.</summary>
    /// <summary>A sheet a port or element can take: its rectangle on a plane, and (brief-hzn-14) the turn about the vertical through its
    /// centre when it stands in a vertical plane that is no axis plane, in degrees as a port's <c>Angle</c>.</summary>
    internal sealed record SheetGeom(C3dPlane Plane, Dim Offset, Dim[] Min, Dim[] Size, bool Placed, double Angle = 0)
    {
        /// <summary>brief-hzn-15 — a four-cornered sheet in a tilted plane that is a rectangle to a part in a hundred: its corners,
        /// world DBU (a port's <c>Corners</c>); null for every other sheet.</summary>
        public List<C3dPoint3>? Corners { get; init; }

        /// <summary>brief-hzn-17 — the points <see cref="Corners"/> came from named variables, which the corners (numbers) do not keep.</summary>
        public bool CornersNamedVariables { get; init; }

        /// <summary>brief-hzn-19 — the sheet is a disc (a circle), and <see cref="Min"/>/<see cref="Size"/> are its bounding square: a wave
        /// port on a coaxial line's end cap takes it, nothing else does.</summary>
        public bool Disc { get; init; }
    }

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
        // brief-hzn-15 R-hzn15-5 — a boolean that does not keep its tools consumes them, whether or not the file recorded it: a tool
        // whose consumed_by is empty is taken as consumed by the one object whose history names it (not drawn a second time on its own)
        foreach (var o in objects)
            foreach (var s in o.History)
                if (s.Suppressed != true && s.Kind is "Subtract" or "Unite" or "Intersect" && !IsTrue(s.Props["KeepOriginals"]) && o.Name is { } owner)
                    foreach (string t in HznExpressionTranslator.SplitTopLevel(s.Props["Tools"] ?? "", ','))
                        if (t.Length > 0 && _byName.TryGetValue(t, out var tool) && string.IsNullOrEmpty(tool.ConsumedBy) && !_keptTools.Contains(t))
                            _impliedConsumer.TryAdd(t, owner);

        foreach (var o in objects)
        {
            string name = o.Name ?? "";
            if (name.Length == 0 || !ReferenceEquals(_byName.GetValueOrDefault(name), o)) continue;
            if (IsRegion(o)) { Region(o); continue; }
            if (!string.IsNullOrEmpty(o.ConsumedBy) || _keptTools.Contains(name) || _impliedConsumer.ContainsKey(name)) continue;
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

    /// <summary>The name of the perfect-conductor solid <see cref="Outside"/> adds.</summary>
    internal const string OutsideName = "hzn_outside";

    /// <summary>
    /// brief-hzn-19 — in a closed design, what no object fills inside the box round the model is outside Hertzon's solved space, whose
    /// walls are perfect conductors; circuitRF's air box fills it with air instead. Where the solids do not fill their box (a coaxial
    /// line's cylinder, whose box has four corners outside it), that space is kept as one perfect-conductor solid: the box less every
    /// modelled solid. It is hidden, so the model stays in view, and its box is numbers at the variables' values. Solids that tile
    /// their box (every closed design that opened before this: a waveguide, stacked substrate and air boxes) add nothing.
    /// </summary>
    private void Outside()
    {
        if (!_closedDomain || _regionName is not null || _autoOpen is not null || _r.Any) return;
        var solids = _doc.Objects.Where(o => o.Model && o is not C3dPolyline && !C3dSheetBooleans.IsSheet(o)).ToList();
        // a non-model object (a field-plot plane) never moves the outer extent (brief-hzn-15)
        if (solids.Count == 0 || Content(modelOnly: true) is not { } all) return;
        if (Tiles(solids, all)) return;
        if (_doc.Objects.Any(o => o.Name == OutsideName)) { _r.Add(HznRefusalKind.Setting, $"object name '{OutsideName}'", "circuitRF names the conductor outside a closed design so", DesignName); return; }
        var (pec, pecRole) = UseMaterial("pec", OutsideName, sheet: false);
        var box = new C3dBox
        {
            Min = new C3dPoint3(all.Min[0], all.Min[1], all.Min[2]),
            Size = new C3dPoint3(all.Max[0] - all.Min[0], all.Max[1] - all.Min[1], all.Max[2] - all.Min[2]),
            Material = pec, Role = pecRole,
        };
        var tools = solids.Select(o =>
        {
            var copy = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(o));
            // an operation's operands are unnamed (the result takes the name), so only a named one is renamed
            foreach (var d in C3dOperands.SelfAndDescendants(copy).Where(d => !string.IsNullOrEmpty(d.Name))) d.Name = $"{OutsideName}_{d.Name}";
            // a Subtract that keeps its tools draws its blank AND its tools (a coaxial line's dielectric and its pin): as a cutting tool
            // it is their union, or the kept tools would be drawn a second time as conductors of their own
            foreach (var b in C3dOperands.SelfAndDescendants(copy).OfType<C3dBoolean>().Where(b => b.Op == C3dBooleanOp.Subtract && b.KeepTools))
                (b.Op, b.KeepTools) = (C3dBooleanOp.Unite, false);
            return copy;
        }).ToList();
        _doc.Objects.Add(new C3dBoolean { Name = OutsideName, Op = C3dBooleanOp.Subtract, Blank = box, Tools = tools, Hidden = true });
        _notes.Add($"This closed design's solids do not fill the box round them, and what they leave is outside Hertzon's solved space, whose walls " +
                   $"are perfect conductors: it is '{OutsideName}', a hidden perfect-conductor solid (the box less {string.Join(", ", solids.Select(o => $"'{o.Name}'"))}), " +
                   "its box numbers at the variables' values.");
    }

    /// <summary>brief-hzn-19 — true when the axis-aligned boxes among <paramref name="solids"/> (a box, or a Subtract from one, whose outer
    /// shape is that box) cover <paramref name="all"/> exactly, measured on the grid their faces make.</summary>
    private bool Tiles(IReadOnlyList<C3dObject> solids, Box3 all)
    {
        var boxes = new List<Box3>();
        foreach (var o in solids)
        {
            var shape = o;
            while (shape is C3dBoolean { Op: C3dBooleanOp.Subtract, Blank: { } b }) shape = b;
            if (shape is C3dBox && Bounds(o) is { } bb) boxes.Add(bb);
        }
        if (boxes.Count == 0) return false;
        var cuts = Enumerable.Range(0, 3).Select(a => boxes.SelectMany(b => new[] { Math.Clamp(b.Min[a], all.Min[a], all.Max[a]), Math.Clamp(b.Max[a], all.Min[a], all.Max[a]) })
                                                       .Append(all.Min[a]).Append(all.Max[a]).Distinct().Order().ToArray()).ToArray();
        for (int i = 0; i + 1 < cuts[0].Length; i++)
            for (int j = 0; j + 1 < cuts[1].Length; j++)
                for (int k = 0; k + 1 < cuts[2].Length; k++)
                {
                    long[] c = [(cuts[0][i] + cuts[0][i + 1]) / 2, (cuts[1][j] + cuts[1][j + 1]) / 2, (cuts[2][k] + cuts[2][k + 1]) / 2];
                    if (!boxes.Any(b => Enumerable.Range(0, 3).All(a => b.Min[a] <= c[a] && c[a] <= b.Max[a]))) return false;
                }
        return true;
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
            tree.Model = o.Model != false || tree is C3dPolyline;   // a polyline is never solved; 'Model': false on one is noise
        }
        var carrier = tree;
        while (carrier is C3dOperation && C3dOperands.Inner(carrier) is { } inner) carrier = inner;
        if (carrier is C3dPolyline) { }   // construction geometry has no material
        else if (o.Material is { Length: > 0 } mat)
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

    private enum Pend { None, Rect, Circle, Poly, VPoly, TPoly }

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
        /// <summary>brief-hzn-15 — a tilted polyline's own rotations (its frame), with <see cref="OwnOrigin"/>; null otherwise.</summary>
        public List<C3dRotation>? OwnRot;
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
            case "ThickenSheet": return Thicken(st, p, user);
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
        // brief-hzn-15 R-hzn15-4 — NumberOfSegments above 0 facets the cylinder: a prism of that many sides
        int facets = 0;
        if (p.TryGetValue("NumberOfSegments", out string? segText))
        {
            if (Plain(segText, user) is not { } seg) return false;
            if (seg != 0 && (seg < 3 || seg != Math.Floor(seg) || seg > 1024))
            {
                _r.Add(HznRefusalKind.CreationStep, "CreateCylinder", $"NumberOfSegments {segText} is neither 0 (a true circle) nor a whole number of sides from 3 to 1024", user.Split(' ')[0]);
                return false;
            }
            facets = (int)seg;
        }
        var centre = Triple(p.GetValueOrDefault("CenterPosition"), user);
        var radius = Len(p.GetValueOrDefault("Radius"), user);
        var height = Len(p.GetValueOrDefault("Height"), user);
        if (AxisOf(p.GetValueOrDefault("Axis")) is not { } axis)
        {
            _r.Add(HznRefusalKind.CreationStep, "CreateCylinder", $"its axis '{p.GetValueOrDefault("Axis")}' is not X, Y or Z", user);
            return false;
        }
        if (centre is null || radius is null || height is null) return false;
        st.Root = facets > 0 ? FacetedCylinder(centre, axis, radius, height, facets) : Cylinder(centre, axis, radius, height);
        return true;
    }

    /// <summary>
    /// brief-hzn-15 R-hzn15-4 — a faceted cylinder as the prism it is. Established black-box from Hertzon's own mesh (12, 10 and 7 sides,
    /// axes X, Y and Z): the <paramref name="sides"/> vertices lie ON the circle, the first along the next axis round from the cylinder's
    /// (Z → +x, X → +y, Y → +z), the rest every 360°/N. Each vertex keeps the centre's and radius's expressions.
    /// </summary>
    private C3dPrism FacetedCylinder(Dim[] centre, C3dAxis axis, Dim radius, Dim height, int sides)
    {
        var (plane, ui, vi) = PlaneOf(axis);
        int first = ((int)axis + 1) % 3;            // the world axis the first vertex lies along
        double start = first == ui ? 0 : Math.PI / 2;
        var outline = new List<Dim[]>(sides);
        for (int k = 0; k < sides; k++)
        {
            double t = start + 2 * Math.PI * k / sides;
            outline.Add([Add(centre[ui], Scaled(radius, Math.Cos(t))), Add(centre[vi], Scaled(radius, Math.Sin(t)))]);
        }
        var prism = new C3dPrism { Plane = plane };
        PutOutline(prism, nameof(C3dPrism.Offset), nameof(C3dPrism.Outline), new PolyInfo(plane, axis, centre[(int)axis], outline));
        Put(prism, nameof(C3dPrism.Height), 0, height);
        return prism;
    }

    /// <summary>A length times a plain factor: a literal stays one (rounded to the DBU), an expression gains the factor.</summary>
    private Dim Scaled(Dim d, double f)
    {
        if (Math.Abs(f) < 1e-15) return Dim.Zero;
        if (d.IsLiteral) return new Dim(null, (long)Math.Round(d.Dbu * f, MidpointRounding.AwayFromZero), true);
        if (Math.Abs(f - 1) < 1e-15) return d;
        f = Math.Round(f, 14);
        return new Dim($"({TextOf(AsBearing(d))})*{f.ToString("R", CultureInfo.InvariantCulture)}", 0, true);
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
    /// <paramref name="Rect"/> is its rectangle when it is one (brief-hzn-14: a port may take it). <paramref name="Holes"/> (brief-hzn-17)
    /// are its holes' (u, v) rings, DBU.</summary>
    private sealed record PolyInfo(C3dPlane Plane, C3dAxis Normal, Dim Offset, List<Dim[]> Outline, SheetGeom? Rect = null, List<List<C3dPoint2>>? Holes = null);

    /// <summary>brief-hzn-14 R-hzn14-2 — a polyline standing in a vertical plane that is no axis plane: a sheet on <paramref name="Plane"/>
    /// (XZ or YZ) at offset 0, its outline (distance along the plane from <paramref name="Origin"/>, z), turned about z by
    /// <paramref name="Degrees"/> and moved to <paramref name="Origin"/> by its placement. <paramref name="Rect"/> is the port sheet it
    /// is when it is a rectangle.</summary>
    private sealed record VPolyInfo(C3dPlane Plane, double Degrees, C3dPoint3 Origin, List<Dim[]> Outline, SheetGeom? Rect);

    /// <summary>brief-hzn-15 — a polyline in a tilted plane: a sheet on the axis plane nearest it (<paramref name="Plane"/>, offset 0), its
    /// outline in that plane's (u, v) in DBU, carried onto the tilted plane by <paramref name="Rot"/> (the matrix <paramref name="R"/>) and
    /// moved to <paramref name="Origin"/>. <paramref name="Rect"/> is the port sheet it is when it is a rectangle.</summary>
    private sealed record TPolyInfo(C3dPlane Plane, int U, int V, int N, double[,] R, List<C3dRotation> Rot, C3dPoint3 Origin,
                                    List<C3dPoint2> Outline, SheetGeom? Rect);

    private object? _pendingInfo;

    /// <summary>brief-hzn-15 — a rectangle as the polygon of its four corners, counter-clockwise in its plane, each corner keeping its expressions.</summary>
    private PolyInfo RectAsPoly(RectInfo r)
    {
        var (plane, _, _) = PlaneOf(r.Normal);
        var (u0, v0) = (r.Min2[0], r.Min2[1]);
        var (u1, v1) = (Add(r.Min2[0], r.Size2[0]), Add(r.Min2[1], r.Size2[1]));
        return new PolyInfo(plane, r.Normal, r.Pos[(int)r.Normal], [[u0, v0], [u1, v0], [u1, v1], [u0, v1]]);
    }

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
        // An open or uncovered polyline is a line, not a sheet: construction geometry (a field-plot path, a sweep's path), which a
        // .c3d polyline is too.
        bool line = !IsTrue(p.GetValueOrDefault("Closed")) || !IsTrue(p.GetValueOrDefault("Covered"));
        string segments = p.GetValueOrDefault("SegmentTypes") ?? "";
        if (segments.Length > 0 && HznExpressionTranslator.SplitTopLevel(segments, ',').Any(s => s != "Line"))
        {
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline with arcs or splines", $"segment types '{segments}' are not all straight lines", name);
            ok = false;
        }
        string holesText = p.GetValueOrDefault("Holes") ?? "";
        if (holesText.Length > 0 && line)
        {
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline with holes", "an open or uncovered polyline has no face for a hole to be cut from", name);
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
        if (line)
        {
            if (pts.Count < 2) { _r.Add(HznRefusalKind.CreationStep, "CreatePolyline", "it has fewer than two points", name); return false; }
            st.Root = ConstructionLine(dims, IsTrue(p.GetValueOrDefault("Closed")));
            return true;
        }
        if (pts.Count < 3) { _r.Add(HznRefusalKind.CreationStep, "CreatePolyline", "it has fewer than three points", name); return false; }
        long Dbu(double metres) => (long)Math.Round(metres * 1e6 * HznToC3d.DbuPerMicron, MidpointRounding.AwayFromZero);
        var axes = new List<C3dAxis>();
        for (int a = 0; a < 3; a++)
            if (pts.All(q => Dbu(q[a]) == Dbu(pts[0][a]))) axes.Add((C3dAxis)a);
        if (axes.Count != 1 && holesText.Length > 0)
        {
            // brief-hzn-17 R-hzn17-1 — no example has one; an upright or tilted sheet's outline is in its own frame, and its holes would be too
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline with holes outside an axis plane", "holes are translated in an axis plane only", name);
            return false;
        }
        if (axes.Count != 1)
        {
            if (VerticalPolyline(pts, dims, name, Dbu) is { } vertical)
            {
                st.Pending = Pend.VPoly;
                _pendingInfo = vertical;
                return true;
            }
            if (TiltedPolyline(pts, dims, name) is { } tilted)
            {
                st.Pending = Pend.TPoly;
                _pendingInfo = tilted;
                return true;
            }
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline outside a plane",
                   "its points do not lie in one plane", name);
            return false;
        }
        var normal = axes[0];
        var (plane, ui, vi) = PlaneOf(normal);
        int ni = (int)normal;
        var offset = dims[0][ni];
        if (dims.Any(q => TextOf(q[ni]) != TextOf(offset)))
            _notes.Add($"{name}: its points spell their {normal} coordinate differently ({string.Join(", ", dims.Select(q => TextOf(q[ni])).Distinct().Take(4).Select(t => $"'{t}'"))}), " +
                       $"which all evaluate to the plane it lies in; the sheet's offset is the first point's, '{TextOf(offset)}'.");
        var outline2 = dims.Select(q => new[] { q[ui], q[vi] }).ToList();
        List<List<C3dPoint2>>? holes = null;
        if (holesText.Length > 0)
        {
            holes = PolylineHoles(holesText, pts, ui, vi, ni, Dbu, name, user);
            if (holes is null) return false;
        }
        st.Pending = Pend.Poly;
        // a sheet with holes is no port sheet
        var rect = holes is null ? AxisRect(plane, offset, outline2, pts.Select(q => (Dbu(q[ui]), Dbu(q[vi]))).ToList()) : null;
        if (rect?.Corners is not null && dims.Any(d => d.Any(c => !c.IsLiteral))) rect = rect with { CornersNamedVariables = true };
        _pendingInfo = new PolyInfo(plane, normal, offset, outline2, rect, holes);
        return true;
    }

    /// <summary>
    /// brief-hzn-17 R-hzn17-1 — a polyline's <c>Holes</c> (rings of <c>x,y,z</c> points, <c>;</c> between points and <c>|</c> between
    /// rings) as the (u, v) rings of the outline's plane, DBU. Each ring has three distinct points in the outline's plane, and the
    /// rings are checked by the 2D layout's own hole rule (<see cref="LayoutClipper.HolesAreValid"/>): inside the outline, crossing
    /// neither it nor each other. Hertzon repairs touching rings with a bridge of its own; a different shape here would be silent, so an
    /// invalid ring is refused. Null, with the refusal recorded, when any of that fails.
    /// </summary>
    private List<List<C3dPoint2>>? PolylineHoles(string text, List<double[]> outline, int ui, int vi, int ni, Func<double, long> dbu, string name, string user)
    {
        var rings = new List<List<C3dPoint2>>();
        long plane = dbu(outline[0][ni]);
        bool named = false;
        foreach (string ringText in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ring = new List<C3dPoint2>();
            foreach (string pointText in ringText.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var comps = HznExpressionTranslator.SplitTopLevel(pointText, ',');
                if (comps.Count != 3) { _r.Add(HznRefusalKind.Expression, "polyline hole point", $"'{pointText}' is not three values", user); return null; }
                var m = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (Len(comps[i], user) is not { } dim) return null;
                    m[i] = Metres(dim);
                    if (double.IsNaN(m[i])) return null;
                    named |= !dim.IsLiteral;
                }
                if (dbu(m[ni]) != plane)
                {
                    _r.Add(HznRefusalKind.CreationStep, "CreatePolyline with a hole off its plane", $"the hole point '{pointText}' does not lie in the outline's plane", name);
                    return null;
                }
                ring.Add(new C3dPoint2(dbu(m[ui]), dbu(m[vi])));
            }
            if (ring.Distinct().Count() < 3)
            {
                _r.Add(HznRefusalKind.CreationStep, "CreatePolyline with a degenerate hole", $"hole {rings.Count} has fewer than three distinct points", name);
                return null;
            }
            rings.Add(ring);
        }
        static long[] Flat(IEnumerable<C3dPoint2> ring) => [.. ring.SelectMany(q => new[] { q.U, q.V })];
        var all = new List<long[]> { Flat(outline.Select(q => new C3dPoint2(dbu(q[ui]), dbu(q[vi])))) };
        all.AddRange(rings.Select(Flat));
        if (!LayoutClipper.HolesAreValid(all))
        {
            _r.Add(HznRefusalKind.CreationStep, "CreatePolyline with an invalid hole", "a hole lies outside its outline, crosses it, or crosses another hole", name);
            return null;
        }
        if (named) _notes.Add($"{name}: its holes named variables; a .c3d hole holds numbers, so they are the values at translation.");
        return rings;
    }

    /// <summary>An open or uncovered polyline as a .c3d polyline (construction geometry, never solved), its vertices in 3D so each
    /// component keeps its expression.</summary>
    private C3dPolyline ConstructionLine(List<Dim[]> dims, bool closed)
    {
        var line = new C3dPolyline { Plane = C3dPlane.XY, Closed = closed, Points3 = [.. dims.Select(d => new C3dPoint3(d[0].Dbu, d[1].Dbu, d[2].Dbu))] };
        var spec = C3dBindings.SpecOf(typeof(C3dPolyline), nameof(C3dPolyline.Points3))!;
        for (int e = 0; e < dims.Count; e++)
            for (int k = 0; k < 3; k++)
                if (!dims[e][k].IsLiteral) C3dBindings.SetExpr(line, spec.ElementAt(e), k, new C3dExpr(dims[e][k].Text!, _siteUnit));
        return line;
    }

    /// <summary>brief-hzn-14 — an axis-plane polyline that is a rectangle (four points on two u and two v values) as the sheet a port
    /// takes, its corner and size keeping the points' expressions; null otherwise.</summary>
    private SheetGeom? AxisRect(C3dPlane plane, Dim offset, List<Dim[]> outline, List<(long U, long V)> at)
    {
        if (outline.Count != 4) return null;
        var us = at.Select(p => p.U).Distinct().ToList();
        var vs = at.Select(p => p.V).Distinct().ToList();
        if ((us.Count != 2 || vs.Count != 2) && plane == C3dPlane.XY && TurnedXyRect(offset, at) is { } turned) return turned;
        if (us.Count != 2 || vs.Count != 2) return DrawnAxisQuad(plane, offset, at);
        foreach (long u in us) foreach (long v in vs) if (!at.Contains((u, v))) return DrawnAxisQuad(plane, offset, at);
        int iu0 = at.FindIndex(p => p.U == us.Min()), iu1 = at.FindIndex(p => p.U == us.Max());
        int iv0 = at.FindIndex(p => p.V == vs.Min()), iv1 = at.FindIndex(p => p.V == vs.Max());
        var min = new[] { outline[iu0][0], outline[iv0][1] };
        var size = new[] { Add(outline[iu1][0], Neg(outline[iu0][0])), Add(outline[iv1][1], Neg(outline[iv0][1])) };
        return new SheetGeom(plane, offset, min, size, Placed: false);
    }

    /// <summary>
    /// brief-hzn-17 R-hzn17-2 — four axis-plane points that are neither an exact rectangle nor an exact turned one, but a rectangle to a
    /// part in a hundred (a port sheet joining a slanted board edge to a pad): the port sheet it is, as its corners in world DBU and drawn
    /// order, exactly as <see cref="TiltedRect"/> does in a tilted plane. Numbers only. Null when the points are no such quadrilateral.
    /// </summary>
    private SheetGeom? DrawnAxisQuad(C3dPlane plane, Dim offset, List<(long U, long V)> at)
    {
        long n = Dbu(offset);
        var corners = at.Select(p => plane switch
        {
            C3dPlane.YZ => new C3dPoint3(n, p.U, p.V),
            C3dPlane.XZ => new C3dPoint3(p.U, n, p.V),
            _ => new C3dPoint3(p.U, p.V, n),
        }).ToList();
        static CircuitRF.Engine.Em3d.Point3 W(C3dPoint3 p) => new(p.X, p.Y, p.Z);
        var quad = new CircuitRF.Engine.Em3d.Em3dPortQuad(W(corners[0]), W(corners[1]), W(corners[2]), W(corners[3]));
        if (quad.Problem(CircuitRF.Engine.Em3d.Em3dPortQuad.DrawnTolerance) is not null) return null;
        return new SheetGeom(plane, Dim.Zero, [Dim.Zero, Dim.Zero], [Dim.Zero, Dim.Zero], Placed: false) { Corners = corners };
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

    /// <summary>
    /// brief-hzn-15 R-hzn15-1 — a polyline whose points lie in one plane that is neither an axis plane nor upright: the sheet it is on the
    /// nearest axis plane, carried onto its own plane by the smallest rotation that does it (<see cref="C3dTilt"/>) and moved to its first
    /// point. The points may stand off the fitted plane by two DBU or by a part in 10⁴ of their extent (an exported model's points carry
    /// nine digits, not exact coplanarity), which is noted; the outline is the points projected onto it, as numbers (a rotation is not
    /// linear in the points' coordinates), noted when they named variables. Null when the points do not span one plane.
    /// </summary>
    private TPolyInfo? TiltedPolyline(List<double[]> pts, List<Dim[]> dims, string name)
    {
        double scale = 1e6 * HznToC3d.DbuPerMicron;
        var at = pts.Select(q => new[] { q[0] * scale, q[1] * scale, q[2] * scale }).ToList();
        if (C3dTilt.FitPlane(at) is not var (normal, _, deviation)) return null;
        double extent = Enumerable.Range(0, 3).Max(a => at.Max(q => q[a]) - at.Min(q => q[a]));
        double allowed = Math.Max(2, 1e-4 * extent);
        if (deviation > allowed) return null;
        var (plane, ni, axis) = C3dTilt.NearestPlane(normal);
        var r = C3dTilt.Between(axis, normal);
        var rot = C3dTilt.Euler(r);
        long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
        var origin = new C3dPoint3(Round(at[0][0]), Round(at[0][1]), Round(at[0][2]));
        var (_, ui, vi) = PlaneOf((C3dAxis)ni);
        // Local = inverse of (the rotations, then the move to the origin), applied as the lowering will apply it forward.
        var inv = C3dTilt.Rotation(rot).Then(C3dTransform.Translation(origin)).Inverse();
        double[] Local(double[] q) =>
        [
            inv.M00 * q[0] + inv.M01 * q[1] + inv.M02 * q[2] + inv.Tx,
            inv.M10 * q[0] + inv.M11 * q[1] + inv.M12 * q[2] + inv.Ty,
            inv.M20 * q[0] + inv.M21 * q[1] + inv.M22 * q[2] + inv.Tz,
        ];
        var local = at.Select(Local).ToList();
        var outline = local.Select(l => new C3dPoint2(Round(l[ui]), Round(l[vi]))).ToList();
        double tilt = Math.Acos(Math.Clamp(Math.Abs(normal[ni]), 0, 1)) * 180 / Math.PI;
        _notes.Add($"{name}: it lies in a plane tilted {tilt.ToString("0.###", CultureInfo.InvariantCulture)}° from {plane}, so it is drawn on {plane} and turned onto that plane; " +
                   "its points are numbers" + (dims.Any(d => d.Any(c => !c.IsLiteral)) ? " (they named variables)." : "."));
        if (deviation > 2)
            _notes.Add($"{name}: its points stand up to {deviation / HznToC3d.DbuPerMicron:0.###} µm off one plane; they are projected onto the plane that fits them best.");
        return new TPolyInfo(plane, ui, vi, ni, r, rot, origin, outline, TiltedRect(at, normal, plane, ui, vi, ni));
    }

    /// <summary>
    /// brief-hzn-15 R-hzn15-3 — four points in a tilted plane that make a rectangle to a part in a hundred (an exported model joins two
    /// edges that are not exactly parallel): the port sheet it is, as its corners, so both of its ends stay exactly where they were
    /// drawn; null otherwise.
    /// </summary>
    private static SheetGeom? TiltedRect(List<double[]> c, double[] normal, C3dPlane plane, int ui, int vi, int ni)
    {
        if (c.Count != 4) return null;
        long R(double x) => (long)Math.Round(x, MidpointRounding.AwayFromZero);
        var corners = c.Select(q => new C3dPoint3(R(q[0]), R(q[1]), R(q[2]))).ToList();
        static CircuitRF.Engine.Em3d.Point3 W(C3dPoint3 p) => new(p.X, p.Y, p.Z);
        var quad = new CircuitRF.Engine.Em3d.Em3dPortQuad(W(corners[0]), W(corners[1]), W(corners[2]), W(corners[3]));
        if (quad.Problem(CircuitRF.Engine.Em3d.Em3dPortQuad.DrawnTolerance) is not null) return null;
        return new SheetGeom(plane, Dim.Zero, [Dim.Zero, Dim.Zero], [Dim.Zero, Dim.Zero], Placed: false) { Corners = corners };
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
            case Pend.TPoly:
            {
                // brief-hzn-15 R-hzn15-1 — a sheet on the nearest axis plane, its frame the tilt and the first point
                var q = (TPolyInfo)_pendingInfo!;
                st.Root = new C3dSheet { Plane = q.Plane, Outline = q.Outline };
                st.OwnRot = q.Rot;
                st.OwnOrigin = q.Origin;
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
                // brief-hzn-19 — a circle's bounding square, for a wave port on a coaxial end cap
                if (c.RadiusV is null)
                    st.Sheet = new SheetGeom(plane, c.Centre[(int)c.Normal], [Add(c.Centre[u], Neg(c.Radius)), Add(c.Centre[v], Neg(c.Radius))],
                                             [Scaled(c.Radius, 2), Scaled(c.Radius, 2)], Placed: false) { Disc = true };
                break;
            }
        }
        st.Pending = Pend.None;
        _pendingInfo = null;
        return true;
    }

    /// <summary>
    /// brief-hzn-18 R-hzn18-3 — Thicken Sheet: by Hertzon's tutorial a positive thickness grows to the positive side of the sheet's main
    /// axis (+z for a sheet in the XY plane), a negative one to the other side, and Reverse direction flips it. So it is the sweep along
    /// the sheet's normal by that thickness, written as one and translated by <see cref="Sweep"/>. Both sides at once is refused: whether
    /// its thickness is per side or in total is not established.
    /// </summary>
    private bool Thicken(State st, Dictionary<string, string> p, string user)
    {
        string name = user.Split(' ')[0];
        if (IsTrue(p.GetValueOrDefault("BothSides")))
        {
            _r.Add(HznRefusalKind.Operation, "ThickenSheet on both sides", "whether its thickness is per side or in total is not established", name);
            return false;
        }
        C3dAxis? normal = st.Pending switch
        {
            Pend.Rect => ((RectInfo)_pendingInfo!).Normal,
            Pend.Circle => ((CircleInfo)_pendingInfo!).Normal,
            Pend.Poly => ((PolyInfo)_pendingInfo!).Normal,
            _ => null,
        };
        if (normal is null)
        {
            _r.Add(HznRefusalKind.Operation, "ThickenSheet", "only a rectangle, circle or polyline in an axis plane is thickened", name);
            return false;
        }
        if (p.GetValueOrDefault("Thickness") is not { Length: > 0 } t)
        {
            _r.Add(HznRefusalKind.Setting, "missing property", $"{user} Thickness is not stated", DesignName);
            return false;
        }
        string along = IsTrue(p.GetValueOrDefault("ReverseDirection")) ? $"-({t})" : t;
        string[] v = ["0", "0", "0"];
        v[(int)normal.Value] = along;
        return Sweep(st, new Dictionary<string, string>(StringComparer.Ordinal) { ["Vector"] = string.Join(",", v) }, user);
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
        if (st.Pending == Pend.TPoly)
        {
            // brief-hzn-15 R-hzn15-2 — a tilted polygon swept along any vector: a prism in the sheet's frame, its height the vector's
            // part along the frame's normal and its shear the rest, as numbers (the frame turns the vector)
            var q = (TPolyInfo)_pendingInfo!;
            double[] w = [Metres(v[0]) * 1e6 * HznToC3d.DbuPerMicron, Metres(v[1]) * 1e6 * HznToC3d.DbuPerMicron, Metres(v[2]) * 1e6 * HznToC3d.DbuPerMicron];
            if (w.Any(double.IsNaN)) return false;
            var r = q.R;
            double Along(int axis) => r[0, axis] * w[0] + r[1, axis] * w[1] + r[2, axis] * w[2];   // Rᵀ·w
            long Round(double x) => (long)Math.Round(x, MidpointRounding.AwayFromZero);
            if (v.Any(d => !d.IsLiteral))
                _notes.Add($"{name}: the sweep vector '{p.GetValueOrDefault("Vector")}' is numbers in the prism (its frame is tilted).");
            st.Root = new C3dPrism { Plane = q.Plane, Outline = q.Outline, Height = Round(Along(q.N)), Shear = new C3dPoint2(Round(Along(q.U)), Round(Along(q.V))) };
            st.OwnRot = q.Rot;
            st.OwnOrigin = q.Origin;
            st.Pending = Pend.None;
            _pendingInfo = null;
            return true;
        }
        C3dAxis normal = st.Pending switch
        {
            Pend.Rect => ((RectInfo)_pendingInfo!).Normal,
            Pend.Circle => ((CircleInfo)_pendingInfo!).Normal,
            _ => ((PolyInfo)_pendingInfo!).Normal,
        };
        var h = v[(int)normal];
        bool offNormal = Enumerable.Range(0, 3).Any(i => i != (int)normal && !v[i].IsZeroLiteral);
        if (offNormal)
        {
            // brief-hzn-15 R-hzn15-2 — off the normal: a rectangle or polygon is a prism whose top is sheared against its bottom by the
            // vector's in-plane part; a circle would be an oblique cylinder, which .c3d has no primitive for
            if (st.Pending == Pend.Circle)
            {
                _r.Add(HznRefusalKind.Operation, "SweepAlongVector of a circle off its normal", "an oblique cylinder has no .c3d primitive", name);
                st.Pending = Pend.None;
                _pendingInfo = null;
                return false;
            }
            var q = st.Pending == Pend.Rect ? RectAsPoly((RectInfo)_pendingInfo!) : (PolyInfo)_pendingInfo!;
            var (_, ui, vi) = PlaneOf(normal);
            var prism = new C3dPrism { Plane = q.Plane };
            PutOutline(prism, nameof(C3dPrism.Offset), nameof(C3dPrism.Outline), q);
            Put(prism, nameof(C3dPrism.Height), 0, h);
            Put(prism, nameof(C3dPrism.Shear), 0, v[ui]);
            Put(prism, nameof(C3dPrism.Shear), 1, v[vi]);
            st.Root = prism;
            st.Pending = Pend.None;
            _pendingInfo = null;
            return true;
        }
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
            if (!keep && toolObj.ConsumedBy != name && _impliedConsumer.GetValueOrDefault(t) == name)
                _notes.Add($"'{t}' is a tool of the {kind} in '{name}', which consumes it, though the file does not record it as consumed: it is drawn as part of '{name}' only.");
            else if (!keep && toolObj.ConsumedBy != name)
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
        if (st.OwnRot is { } ownRot)
        {
            // brief-hzn-15 — the tilted shape's frame: its rotations first, its origin added to any move; a rotation or mirror the
            // history adds acts on the moved frame, which one placement cannot state
            if (st.Mirror || st.Rot.Count > 0)
                _r.Add(HznRefusalKind.Operation, "Rotate or Mirror of a tilted polyline", "its own tilt and a later rotation about the origin are not one placement", name);
            pl.Rotate.AddRange(ownRot);
            var o = st.Origin ?? [Dim.Zero, Dim.Zero, Dim.Zero];
            st.Origin = [Add(o[0], new Dim(null, st.OwnOrigin.X, true)), Add(o[1], new Dim(null, st.OwnOrigin.Y, true)), Add(o[2], new Dim(null, st.OwnOrigin.Z, true))];
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
