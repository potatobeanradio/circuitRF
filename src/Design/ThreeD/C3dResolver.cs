// brief-em3d-51 R-em3d51-2 / -5 — a 3D view's names, and every expression in it, resolved BEFORE any geometry is built.
//
// ONE SCOPE PER DOCUMENT (expressions.md §9): the cell's parameters and the document's VARs, in one namespace, through
// the one expression engine — never string substitution. There are no globals: a .c3d is not in a TestBench. The order
// is the brief's: the parameters (an instance's overrides, evaluated in the PARENT's scope, or else the .ccell defaults),
// then the VARs, then each field at its own site unit.
//
// A VAR NAMED LIKE A PARAMETER IS LINKED unless it says Linked: false (owner decision D12). Linked takes the parameter's
// value, default included — which is NOT the circuit side's order (src/Core/RESOLVED.md, R-em3d51-0: there a same-name
// VAR replaces the default and only an override gets through). The difference is deliberate: the circuit side's order
// gives a cell two defaults for one name, and an instance that overrides nothing would then be one size in the schematic
// and another in 3D with nothing saying so. An UNLINKED same-name VAR is the circuit side's shape without even the
// override: it hides the parameter from every dimension here, and check says so.
//
// CYCLES are the engine's own resolving stack (expressions.md §10): a → b → a is reported with its chain, and the
// geometry is never built. The numbers a field holds are written HERE, in place — see C3dBindings for why the number
// is a cache of its expression.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.ThreeD;

/// <summary>Where a name's value came from (R-em3d51-5c: <c>explain</c> reports it per name).</summary>
public enum C3dNameSource
{
    /// <summary>A cell parameter, from the instance's override.</summary>
    Override,
    /// <summary>A cell parameter, from its <c>.ccell</c> default.</summary>
    CellDefault,
    /// <summary>A VAR linked to a cell parameter: the parameter's value (an override, else the default).</summary>
    LinkedVar,
    /// <summary>A VAR's own expression — no parameter of the name, or one it is unlinked from.</summary>
    Var,
    /// <summary><c>explain --set</c>.</summary>
    Set,
}

/// <summary>One name of the document's scope, resolved. <see cref="Value"/> is in base SI (metres for a length).</summary>
public sealed record C3dName(string Name, C3dNameSource Source, string Expression, string? Unit, double? Value, string? Error)
{
    /// <summary>For a linked VAR: where the PARAMETER's value came from (override or default).</summary>
    public C3dNameSource? ParameterSource { get; init; }
}

/// <summary>A field whose expression did not resolve: the item, the field, the text and the engine's message.</summary>
public sealed record C3dFieldProblem(string Item, string Path, string Expr, string Message);

/// <summary>The cell a 3D view belongs to: its <c>.ccell</c> and parameters. <see cref="None"/> for a loose document.</summary>
public sealed record C3dCell(string? CellDir, string? CcellPath, IReadOnlyList<CcellParameter> Parameters)
{
    public static C3dCell None { get; } = new(null, null, []);

    public bool InCell => CcellPath is not null;

    public CcellParameter? Parameter(string name) => Parameters.FirstOrDefault(p => p.Name == name);

    /// <summary>The cell of the <c>.c3d</c> at <paramref name="path"/>: its folder is <c>&lt;cell&gt;/3d/</c> and the cell
    /// folder holds a <c>.ccell</c>. Anything else is loose, and sees its VARs only.</summary>
    public static C3dCell Of(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is null || !string.Equals(Path.GetFileName(dir), CellFolder.ThreeDSubFolder, StringComparison.OrdinalIgnoreCase))
            return None;
        string? cellDir = Path.GetDirectoryName(dir);
        if (cellDir is null) return None;
        string ccell = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccell)) return None;
        try { return new C3dCell(cellDir, ccell, CellPersistence.LoadFromFile(ccell).Parameters); }
        catch { return new C3dCell(cellDir, ccell, []); }
    }

    /// <summary>The cell of a CELL FOLDER (an instance's target).</summary>
    public static C3dCell OfFolder(string cellDir)
    {
        string ccell = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccell)) return new C3dCell(cellDir, null, []);
        try { return new C3dCell(cellDir, ccell, CellPersistence.LoadFromFile(ccell).Parameters); }
        catch { return new C3dCell(cellDir, ccell, []); }
    }
}

/// <summary>An instance override, evaluated in the parent's scope: the value and the unit that makes it unit-bearing.</summary>
public sealed record C3dOverride(Value Value, string? Unit);

/// <summary>Units as a <c>.c3d</c> writes them and as the engine reads them.</summary>
public static class C3dUnits
{
    /// <summary>The spelling a field or a VAR stores for a layout unit: the enum's name (<c>Mil</c>, <c>Um</c>).</summary>
    public static string Stored(LayoutUnit unit) => unit.ToString();

    /// <summary>The angle unit a rotation field stores.</summary>
    public const string Degrees = "Deg";

    /// <summary>
    /// The engine's spelling of a stored unit — <c>Mil</c> → <c>mil</c>, <c>Um</c> → <c>um</c>, <c>Deg</c> → <c>deg</c>
    /// — or the text itself when the engine already knows it. Null for none. <b>A unit the engine does not know is an
    /// error, never a multiplier of 1</b> (expressions.md §8: an unknown unit is silently identity there).
    /// </summary>
    public static string? Engine(string? unit, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(unit)) return null;
        string u = unit.Trim();
        if (Enum.TryParse<LayoutUnit>(u, ignoreCase: true, out var lu) && !int.TryParse(u, out _)) return LayoutUnits.AsciiSuffix(lu);
        if (string.Equals(u, "deg", StringComparison.OrdinalIgnoreCase)) return "deg";
        if (string.Equals(u, "rad", StringComparison.OrdinalIgnoreCase)) return "rad";
        if (Units.IsKnown(u)) return u;
        error = $"'{unit}' is not a unit this build knows. The length units are {string.Join(", ", Enum.GetNames<LayoutUnit>())}; an angle is Deg.";
        return null;
    }

    /// <summary>A length in metres, spelled in <paramref name="unit"/> for a preview: <c>0.254 mm</c>.</summary>
    public static string Spell(double metres, LayoutUnit unit)
    {
        double scale = Units.Scale(LayoutUnits.AsciiSuffix(unit)) ?? 1;
        return (metres / scale).ToString("0.######", CultureInfo.InvariantCulture) + " " + LayoutUnits.Suffix(unit);
    }
}

/// <summary>A 3D view's scope and its fields, resolved (<see cref="C3dResolver.Resolve"/>).</summary>
public sealed class C3dResolution
{
    private readonly Evaluator _evaluator;
    private readonly Scope _scope;

    internal C3dResolution(Evaluator evaluator, Scope scope)
    {
        _evaluator = evaluator;
        _scope = scope;
    }

    /// <summary>Every name the document sees — the cell's parameters and its VARs — in declaration order.</summary>
    public Dictionary<string, C3dName> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>Each field whose expression did not resolve, with the engine's message.</summary>
    public List<C3dFieldProblem> FieldErrors { get; } = [];

    /// <summary>What refuses the document: field errors, cycles, bad VARs, bad overrides. Geometry is not built.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>A VAR hiding a parameter; a dimension above 1 m (the mark a unit slip leaves).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>A rounding to the DBU that moved a value by more than 1e-9 relative.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>A VAR nothing uses.</summary>
    public List<string> Infos { get; } = [];

    /// <summary>Names referenced by some expression and defined nowhere — what the Define strip offers.</summary>
    public SortedSet<string> Unknown { get; } = new(StringComparer.Ordinal);

    /// <summary>For each name, the fields that reference it directly (item, path).</summary>
    public Dictionary<string, List<(string Item, string Path)>> Uses { get; } = new(StringComparer.Ordinal);

    /// <summary>For each field, its resolved value in base SI (metres, degrees, a count or metres for µm fields).</summary>
    public Dictionary<(string Item, string Path), double> FieldValues { get; } = [];

    public bool Ok => Errors.Count == 0;

    /// <summary>Evaluates <paramref name="expression"/> in this document's scope at the site unit <paramref name="unit"/>
    /// (a stored spelling: <c>Mil</c>) — <c>explain --expr</c>, the typed field's preview, an override for a child.</summary>
    /// <exception cref="ExpressionException">It does not resolve; the message is the engine's.</exception>
    public Value Evaluate(string expression, string? unit)
    {
        string? engine = C3dUnits.Engine(unit, out string? bad);
        if (bad is not null) throw new ExpressionException(bad);
        return _evaluator.Eval(expression, _scope, engine);
    }

    /// <summary>
    /// HZN-4 — <paramref name="expression"/> typed at the site unit <paramref name="unit"/> (a stored spelling), as a tree whose
    /// every unit is spelled out: the value the field or VAR would have, with no site unit left to apply. A unit-bearing
    /// expression keeps its form and its bare additive operands take the unit (<c>w + 5</c> in mil is <c>w + 5mil</c>); any other
    /// is multiplied by one of the unit. This is the evaluator's own rule (Evaluator.SiteUnit.cs), applied to the tree and not
    /// carried out, so a writer for a language that has no site unit reads the same quantity. <c>Bearing</c> says the result is a
    /// quantity and not a plain number.
    /// </summary>
    /// <exception cref="ExpressionException">The text does not parse, or the unit is not one this build knows.</exception>
    public (Expr Ast, bool Bearing) AtSite(string expression, string? unit)
    {
        var ast = Parser.Parse(expression);
        string? engine = C3dUnits.Engine(unit, out string? bad);
        if (bad is not null) throw new ExpressionException(bad);
        bool bearing = Evaluator.IsUnitBearing(ast, _scope);
        if (string.IsNullOrEmpty(engine)) return (ast, bearing);
        if (bearing) return (Evaluator.ScaleBareOperands(ast, _scope, engine), true);
        double scale = Units.Scale(engine) ?? 1.0;
        return (ast is NumberExpr { Unit: null } n ? new NumberExpr(n.Value * scale, engine)
                                                   : new BinaryExpr("*", ast, new NumberExpr(scale, engine)), true);
    }

    /// <summary>brief-em3d-74 R-em3d74-5d — evaluates an already-parsed expression in this document's scope: a thermal
    /// measure whose probe calls have been replaced by the probe's values (the AST is rewritten, never the text).</summary>
    /// <exception cref="ExpressionException">It does not resolve; the message is the engine's.</exception>
    public Value EvaluateParsed(Expr expression) => _evaluator.EvalExpr(expression, _scope);

    /// <summary>True when <paramref name="name"/> is bound in this scope.</summary>
    public bool IsDefined(string name) => _scope.Lookup(name) is not null;

    /// <summary>True when <paramref name="expression"/> is unit-bearing — it holds a unit literal (<c>10mil</c>) or references
    /// a name that carries its own unit — so its site unit is skipped (var-unit-wins) — the engine's own test.</summary>
    public bool SkipsSiteUnit(string expression) => Evaluator.IsUnitBearing(expression, _scope);

    /// <summary>
    /// R-em3d51-2e — <paramref name="inst"/>'s overrides of the child's parameters, evaluated in THIS scope. A name that
    /// is not a parameter of the child is refused (naming the VAR when it is one), and a layout instance takes none.
    /// </summary>
    public Dictionary<string, C3dOverride> OverridesFor(C3dInstance inst, string instPath, C3dCell child,
                                                        IReadOnlyList<C3dVariable> childVariables, List<string> errors)
    {
        var result = new Dictionary<string, C3dOverride>(StringComparer.Ordinal);
        if (inst.Params is not { Count: > 0 } ps) return result;
        string cellName = child.CellDir is { } d ? Path.GetFileName(d) : inst.CellRef;
        if (inst.View == C3dInstanceView.Layout)
        {
            errors.Add($"Instance '{instPath}' places the layout of '{cellName}' and overrides {string.Join(", ", ps.Keys.Select(k => $"'{k}'"))}: " +
                       "a .clay has no parameters (a PCell's belong to its generator), so a layout instance takes no overrides.");
            return result;
        }
        foreach (var (name, expr) in ps)
        {
            if (child.Parameter(name) is not { } p)
            {
                errors.Add(childVariables.Any(v => v.Name == name)
                    ? $"Instance '{instPath}' overrides '{name}', which is a VAR of '{cellName}''s 3D view, not a parameter; promote it there to override it here."
                    : $"Instance '{instPath}' overrides '{name}', which is not a parameter of '{cellName}' " +
                      $"({(child.Parameters.Count == 0 ? "it has none" : "its parameters: " + string.Join(", ", child.Parameters.Select(x => x.Name)))}).");
                continue;
            }
            try
            {
                var v = Evaluate(expr.Expr, expr.Unit);
                string? unit = C3dUnits.Engine(p.Unit, out _) ?? C3dUnits.Engine(expr.Unit, out _);
                result[name] = new C3dOverride(v, unit);
            }
            catch (ExpressionException ex)
            {
                errors.Add($"Instance '{instPath}' overrides '{name}' = {expr.Expr}, which does not resolve in this 3D view: {ex.Message}");
            }
        }
        return result;
    }
}

/// <summary>Resolves a 3D view's names and fields (R-em3d51-5a).</summary>
public static class C3dResolver
{
    /// <summary>A dimension longer than this is reported: the mark a unit slip leaves.</summary>
    public const double LargeMetres = 1.0;

    /// <summary>
    /// Resolves <paramref name="doc"/>'s scope and writes every bound field's value into its number, in place.
    /// <paramref name="overrides"/> are an instance's, already evaluated in the parent's scope; <paramref name="sets"/>
    /// are <c>explain --set</c>'s, bound last.
    /// </summary>
    public static C3dResolution Resolve(C3dDocument doc, C3dCell cell, IReadOnlyDictionary<string, C3dOverride>? overrides = null,
                                        IReadOnlyList<(string Name, string Expr)>? sets = null)
    {
        const string scopeName = "3d";
        var ev = new Evaluator();
        var scope = new Scope(scopeName);
        var r = new C3dResolution(ev, scope);

        // 1. The cell's parameters: an override, else the .ccell default (evaluated lazily, in this scope).
        foreach (var p in cell.Parameters)
        {
            string? unit = C3dUnits.Engine(p.Unit, out _);
            // An UNLINKED VAR of the name hides the parameter here, override and all — and an injected value would outlive
            // the VAR's own binding in the evaluator's memo, so the override is not injected at all.
            bool hidden = doc.Variables.Any(v => v.Name == p.Name && v.Linked == false);
            if (!hidden && overrides is not null && overrides.TryGetValue(p.Name, out var ov))
            {
                scope.Bind(p.Name, "__resolved__", ov.Unit ?? unit);
                ev.InjectResolved(scopeName, p.Name, ov.Value);
                r.Names[p.Name] = new C3dName(p.Name, C3dNameSource.Override, "(override)", ov.Unit ?? unit, Real(ov.Value), null);
            }
            else
            {
                scope.Bind(p.Name, p.DefaultExpression, unit);
                r.Names[p.Name] = new C3dName(p.Name, C3dNameSource.CellDefault, p.DefaultExpression, unit, null, null);
            }
        }

        // 2. The VARs. A linked one leaves the parameter's binding as it is.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in doc.Variables)
        {
            if (ValidateName(v.Name) is { } bad) { r.Errors.Add($"VAR '{v.Name}': {bad}"); continue; }
            if (!seen.Add(v.Name)) { r.Errors.Add($"VAR '{v.Name}' is declared more than once; a VAR's name is unique in its 3D view."); continue; }
            string? unit = C3dUnits.Engine(v.Unit, out string? unitError);
            if (unitError is not null) { r.Errors.Add($"VAR '{v.Name}': {unitError}"); continue; }
            var param = cell.Parameter(v.Name);
            if (param is not null && v.Linked != false)
            {
                var p = r.Names[v.Name];
                r.Names[v.Name] = new C3dName(v.Name, C3dNameSource.LinkedVar, p.Expression, p.Unit, p.Value, null) { ParameterSource = p.Source };
                continue;
            }
            if (param is not null)
                r.Warnings.Add($"VAR '{v.Name}' hides cell parameter '{v.Name}'; an instance's override of '{v.Name}' does not change this " +
                               "3D view. Link it, or rename one.");
            scope.Bind(v.Name, v.Expression, unit);
            r.Names[v.Name] = new C3dName(v.Name, C3dNameSource.Var, v.Expression, unit, null, null);
        }

        // A unit-less VAR (or parameter default) that references a unit-bearing name TAKES that name's unit (R-em3d51-1c:
        // `gap = w / 4` is in w's units). Its value is already in base SI, so it is marked with the scale-1 base unit —
        // otherwise a field `gap` typed in mil would apply its site unit to a value that is already metres.
        MarkDerivedUnits(r, scope);

        // 2b. explain --set: bound over whatever else has the name.
        foreach (var (name, expr) in sets ?? [])
        {
            scope.Bind(name, expr);
            r.Names[name] = new C3dName(name, C3dNameSource.Set, expr, null, null, null);
        }

        // Each name's value, for the panel and explain. A parameter the 3D view never reads may reference something it
        // cannot see (a schematic global); that is only an error once a dimension depends on it.
        foreach (var n in r.Names.Values.ToList())
        {
            if (n.Value is not null) continue;
            try { r.Names[n.Name] = n with { Value = Real(ev.Resolve(n.Name, scope)) }; }
            catch (Exception ex) when (ex is ExpressionException or InvalidCastException)
            {
                r.Names[n.Name] = n with { Error = ex.Message };
                if (n.Source is C3dNameSource.Var or C3dNameSource.Set || ex is CycleException)
                    AddOnce(r.Errors, ex is CycleException c ? $"The names cycle: {c.Chain}." : $"VAR '{n.Name}' = {n.Expression} does not resolve: {ex.Message}");
            }
        }

        // 3. Every field, at its own site unit.
        foreach (var f in C3dBindings.Bound(doc)) Field(doc, f, ev, scope, r);
        foreach (var v in doc.Variables) MilliLiteralWarning(r, $"VAR '{v.Name}'", v.Expression);

        // Overrides reference the parent's names too — they are uses.
        foreach (var inst in doc.Instances)
            foreach (var (name, e) in inst.Params ?? [])
                Use(r, e.Expr, inst.Name, $"Params.{name}", scope);

        // Unused VARs: referenced by no field and by nothing a field references.
        var used = Closure(r, doc, cell);
        foreach (var v in doc.Variables)
            if (!used.Contains(v.Name))
                r.Infos.Add($"VAR '{v.Name}' is used by no dimension in this 3D view.");
        return r;
    }

    /// <summary>The base length unit: scale 1, so marking a binding with it changes no value, only var-unit-wins.</summary>
    public const string BaseUnit = "metre";

    private static void MarkDerivedUnits(C3dResolution r, Scope scope)
    {
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var n in r.Names.Values.ToList())
            {
                bool fromText = n.Source is C3dNameSource.Var or C3dNameSource.CellDefault
                                || n is { Source: C3dNameSource.LinkedVar, ParameterSource: C3dNameSource.CellDefault };
                if (n.Unit is not null || !fromText) continue;
                if (!Evaluator.IsUnitBearing(n.Expression, scope)) continue;
                scope.Bind(n.Name, n.Expression, BaseUnit);
                r.Names[n.Name] = n with { Unit = BaseUnit };
                changed = true;
            }
        }
    }

    private static void Field(C3dDocument doc, C3dBoundField f, Evaluator ev, Scope scope, C3dResolution r)
    {
        var e = f.Expr;
        Use(r, e.Expr, f.Item, f.Path, scope);
        string? error = null;
        string? unit = null;
        switch (f.Spec.Kind)
        {
            case C3dFieldKind.Length or C3dFieldKind.Microns:
                MilliLiteralWarning(r, $"'{f.Item}' {Where(f)}", e.Expr);
                unit = C3dUnits.Engine(e.Unit, out error);
                if (error is null && unit is null && !Evaluator.IsUnitBearing(e.Expr, scope))
                    error = "it states no unit. An expression in a dimension carries the unit it was typed in.";
                break;
            case C3dFieldKind.Angle:
                unit = C3dUnits.Engine(e.Unit ?? C3dUnits.Degrees, out error);
                break;
        }
        if (error is null)
        {
            try
            {
                var v = ev.Eval(e.Expr, scope, unit);
                if (v.Kind != ValueKind.Real) error = $"it is {v.Kind.ToString().ToLowerInvariant()}; a dimension is a real number.";
                else error = Store(doc, f, v.AsReal(), r);
            }
            catch (CycleException c) { error = $"the names cycle: {c.Chain}."; }
            catch (UnresolvedNameException u) { error = $"unknown: {u.Name}"; }
            catch (ExpressionException x) { error = x.Message; }
        }
        if (error is null) return;
        r.FieldErrors.Add(new C3dFieldProblem(f.Item, f.Path, e.Expr, error));
        r.Errors.Add($"'{f.Item}' {Where(f)} = {e.Expr}: {error}");
    }

    /// <summary>
    /// A unit literal glued with a bare <c>m</c> — <c>2m</c> — is two MILLI (the SI prefix), which in a length is 2 mm, not
    /// the two metres a reader may have meant. It is legal and deliberate (brief-core-length-units: <c>m</c> stays milli
    /// everywhere), so it is warned about in a length, never refused.
    /// </summary>
    internal static void MilliLiteralWarning(C3dResolution r, string where, string expression)
    {
        if (!HasMilliLiteral(expression)) return;
        AddOnce(r.Warnings, $"{where} = {expression}: a number followed by a bare 'm' is MILLI, so it is millimetres in a length. " +
                            "Write 'mm' to say so, or 'metre' for metres.");
    }

    /// <summary>True when <paramref name="expression"/> holds a unit literal glued with a bare <c>m</c> (<c>2m</c>).</summary>
    public static bool HasMilliLiteral(string expression)
    {
        Expr ast;
        try { ast = Parser.Parse(expression); }
        catch (ExpressionException) { return false; }
        return ContainsMilli(ast);

        static bool ContainsMilli(Expr e) => e switch
        {
            NumberExpr n      => n.Unit == "m",
            UnaryExpr u       => ContainsMilli(u.Operand),
            BinaryExpr b      => ContainsMilli(b.Left) || ContainsMilli(b.Right),
            CompareExpr c     => ContainsMilli(c.Left) || ContainsMilli(c.Right),
            LogicExpr l       => ContainsMilli(l.Left) || ContainsMilli(l.Right),
            ConditionalExpr d => ContainsMilli(d.Condition) || ContainsMilli(d.Then) || ContainsMilli(d.Else),
            CallExpr cl       => cl.Args.Any(ContainsMilli),
            _                 => false,
        };
    }

    /// <summary>The value into the field's number, by kind — or the reason it cannot go there.</summary>
    private static string? Store(C3dDocument doc, C3dBoundField f, double si, C3dResolution r)
    {
        double value;
        switch (f.Spec.Kind)
        {
            case C3dFieldKind.Length:
                double raw = si * 1e6 * doc.DbuPerMicron;
                double rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
                if (Math.Abs(rounded) > long.MaxValue / 2.0) return "it is too large to be a coordinate.";
                if (Math.Abs(rounded - raw) > 1e-9 * Math.Abs(raw))
                    r.Notes.Add(string.Create(CultureInfo.InvariantCulture,
                        $"'{f.Item}' {Where(f)} = {f.Expr.Expr} is {raw:0.###} DBU, rounded to {rounded:0}: two expressions meant to meet exactly may not."));
                if (Math.Abs(si) > LargeMetres)
                    r.Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                        $"'{f.Item}' {Where(f)} = {f.Expr.Expr} is {si:0.######} m, above 1 m. A MULTIPLIER is not scaled by the field's unit (`2*w` is twice w), and a name with no unit is in metres; the metre itself is spelled `metre` — a bare `m` is milli."));
                if (rounded < 0 && IsSize(f)) return string.Create(CultureInfo.InvariantCulture, $"it is {rounded:0} DBU; a size is positive.");
                // brief-em3d-64 R-em3d64-1c — a fillet's radius and a chamfer's distances round or cut something: zero is as
                // meaningless as negative, and the kernel would refuse it far from the field that caused it.
                if (rounded <= 0 && (f.Spec.Owner == typeof(C3dFillet) || f.Spec.Owner == typeof(C3dChamfer)))
                    return string.Create(CultureInfo.InvariantCulture, $"it is {rounded:0} DBU; a {(f.Spec.Owner == typeof(C3dFillet) ? "fillet's radius" : "chamfer's distance")} is positive.");
                value = rounded;
                break;
            case C3dFieldKind.Angle:
                value = si * 180.0 / Math.PI;
                break;
            case C3dFieldKind.Count:
                double n = Math.Round(si);
                if (Math.Abs(si - n) > 1e-9 || n < 1)
                    return string.Create(CultureInfo.InvariantCulture, $"it is {si:G6}; an array count is an integer of at least 1, and is never rounded.");
                value = n;
                break;
            default:   // microns
                value = si * 1e6;
                if (Math.Abs(si) > LargeMetres)
                    r.Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"'{f.Item}' {Where(f)} = {f.Expr.Expr} is {si:0.######} m, above 1 m."));
                break;
        }
        r.FieldValues[(f.Item, f.Path)] = si;
        C3dBindings.SetNumber(f.Owner, f.Spec, f.Component, value);
        return null;
    }

    /// <summary>The field as a sentence names it: its path, or a point's <c>point 2 z</c> (brief-em3d-132).</summary>
    private static string Where(C3dBoundField f) => C3dBindings.Label(f.Spec, f.Component, f.Path);

    private static bool IsSize(C3dBoundField f)
        => f.Spec.Property is nameof(C3dBox.Size) or nameof(C3dCylinder.Radius) or nameof(C3dWire.LoopHeight) or nameof(C3dWire.Span);

    private static void Use(C3dResolution r, string expr, string item, string path, Scope scope)
    {
        Expr ast;
        try { ast = Parser.Parse(expr); }
        catch (ExpressionException) { return; }
        foreach (string name in AstWalker.CollectRefs(ast))
        {
            (r.Uses.TryGetValue(name, out var list) ? list : r.Uses[name] = []).Add((item, path));
            if (scope.Lookup(name) is null) r.Unknown.Add(name);
        }
    }

    /// <summary>Every name a field or an override reaches, directly or through another name's expression.</summary>
    private static HashSet<string> Closure(C3dResolution r, C3dDocument doc, C3dCell cell)
    {
        var used = new HashSet<string>(r.Uses.Keys, StringComparer.Ordinal);
        // brief-em3d-73 — a VAR a heat source's power or a thermal setup reads is used, though no dimension reads it; brief-sar-8 —
        // and so is one a lumped element's R, L or C reads.
        foreach (string text in ThermalExpressions(doc).Concat(doc.LumpedElements.SelectMany(le => new[] { le.R, le.L, le.C }).OfType<string>()))
            try { used.UnionWith(AstWalker.CollectRefs(Parser.Parse(Units.LiftInlineUnit(text).Expression))); }
            catch (ExpressionException) { }
        var stack = new Stack<string>(used);
        while (stack.Count > 0)
        {
            string name = stack.Pop();
            string? expr = doc.Variables.FirstOrDefault(v => v.Name == name)?.Expression;
            string? pexpr = cell.Parameter(name)?.DefaultExpression;
            foreach (string? e in new[] { expr, pexpr })
            {
                if (e is null) continue;
                try
                {
                    foreach (string n in AstWalker.CollectRefs(Parser.Parse(e)))
                        if (used.Add(n)) stack.Push(n);
                }
                catch (ExpressionException) { }
            }
        }
        return used;
    }

    /// <summary>brief-em3d-73 — every expression the document's thermal content reads: heat sources' default powers, and each
    /// thermal setup's expressions (<see cref="C3dThermal.ExpressionFields"/>) and sweep variables.</summary>
    private static IEnumerable<string> ThermalExpressions(C3dDocument doc)
    {
        foreach (var h in doc.HeatSources)
            if (h.Power is { Length: > 0 } p) yield return p;
        foreach (var (_, _, t) in C3dThermal.ThermalSetups(doc))
        {
            foreach (var f in C3dThermal.ExpressionFields(t)) yield return f.Text;
            foreach (var w in t.Sweep ?? []) yield return w.Var;
        }
    }

    private static double? Real(Value v) => v.Kind == ValueKind.Real ? v.AsReal() : null;

    private static void AddOnce(List<string> list, string s) { if (!list.Contains(s)) list.Add(s); }

    // ── names (R-em3d51-1c) ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Null when <paramref name="name"/> can name a VAR: an identifier the engine parses as a bare reference, not a
    /// constant (<c>j</c>, <c>pi</c>, <c>e</c>), not <c>freq</c>, and not the name of a built-in function — asked of the
    /// engine itself, so a function added there is refused here without a second list.
    /// </summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a name cannot be empty.";
        Expr ast;
        try { ast = Parser.Parse(name); }
        catch (ExpressionException) { return "it is not a name the expression engine can read (letters, digits and _, starting with a letter)."; }
        if (ast is ConstExpr) return $"'{name}' is a built-in constant.";
        if (ast is not RefExpr re || re.Name != name) return "it is not a name the expression engine can read (letters, digits and _, starting with a letter).";
        if (name == FreqDeferral.FreqName) return "'freq' is reserved: it is the frequency a model is evaluated at.";
        if (IsFunctionName(name)) return $"'{name}' is the name of a built-in function.";
        return null;
    }

    /// <summary>True when the engine has a function of this name: calling it fails with anything but "unknown function".</summary>
    public static bool IsFunctionName(string name)
    {
        try { new Evaluator().Eval($"{name}(1)", new Scope("probe")); }
        catch (UnknownFunctionException) { return false; }
        catch { return true; }
        return true;
    }
}
