using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Modeling.VerilogA;
using static CircuitRF.Design.Modeling.VerilogA.VaText;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;

/// <summary>The compiled module's source and how the wrapper places it.</summary>
/// <param name="Text">The module, deterministic: the same cell always the same bytes (D6).</param>
/// <param name="Nets">The nets the wrapper binds the module's terminals to, in terminal order.</param>
/// <param name="Overrides">What the wrapper's instance passes: each cell parameter by its own name, or — when the cell is
/// only a composed FET — that FET's own instance values.</param>
public sealed record ProtectModule(string ModuleName, string Text, IReadOnlyList<string> Nets, IReadOnlyList<ParameterAssignment> Overrides);

/// <summary>A cell that cannot be compiled; every reason is collected.</summary>
public sealed class ProtectRefusedException(IReadOnlyList<string> refusals) : Exception(string.Join(" ", refusals))
{
    public IReadOnlyList<string> Refusals { get; } = refusals;
}

/// <summary>
/// One flat Verilog-A module from a <see cref="ProtectPlan"/> (brief vendorlib-16 D1, D3, R-vl16-1). The cell's own
/// parameters are the module's parameters, with their defaults in base SI; every value below the top — a sub-cell's
/// parameters and variables, an element's value, a composed FET's instance values — is a <c>localparam</c> expression of
/// them, so a placed part takes overrides, sweeps, corners and tuning exactly as the unprotected part does.
///
/// <para><b>Names inside are opaque</b> (<c>crf_n3</c>, <c>crf_k7</c>, <c>crf_u1_…</c>): the compiled file carries
/// identifiers, and the hierarchy's names are part of the topology being protected. The source written for the maker says
/// in comments what each one is.</para>
///
/// <para><b>A cell that is only a composed FET</b> is the FET's own standalone module, unchanged (R-vl16-1c), with the
/// wrapper passing its instance values — the path that is already tested.</para>
/// </summary>
public static class NetlistVerilogA
{
    private const string Ground = "crf_gnd";

    public static ProtectModule Write(ProtectPlan plan, string moduleName)
    {
        var refusals = new List<string>(plan.Refusals);
        if (!IsIdentifier(moduleName)) refusals.Add($"'{moduleName}' is not a legal Verilog-A module name.");
        // Every refusal is collected, the partition's and the writer's, so the dialog lists them all at once.

        var fets = plan.Inside.Where(e => ProtectPartition.IsComposedFet(e.Type)).ToList();
        if (fets.Count == 1 && plan.Inside.Count == 1 && fets[0].Level.IsTop)
            return Standalone(plan, fets[0], moduleName, refusals);
        return new Writer(plan, moduleName, refusals).Write();
    }

    /// <summary>R-vl16-1c: the composed FET's own module, its values passed by the wrapper as written.</summary>
    private static ProtectModule Standalone(ProtectPlan plan, ProtectElement fet, string moduleName, List<string> refusals)
    {
        var model = LoadFet(plan, fet, refusals);
        CheckFetOverrides(fet, refusals);
        if (refusals.Count > 0 || model is null) throw new ProtectRefusedException(refusals);
        string text;
        try { text = CmodelVerilogA.Write(model.Value.Model, Options(moduleName, model.Value.Path)); }
        catch (VerilogAExportException ex) { throw new ProtectRefusedException(FetRefusals(fet, ex)); }
        var overrides = fet.Instance.Overrides.Where(o => !o.Name.Equals("File", StringComparison.Ordinal)).ToList();
        return new ProtectModule(moduleName, text, fet.Nets, overrides);
    }

    internal static VerilogAOptions Options(string moduleName, string cmodel)
        => new() { ModuleName = moduleName, SourceName = Path.GetFileName(cmodel) };

    private static IReadOnlyList<string> FetRefusals(ProtectElement fet, VerilogAExportException ex)
        => [.. ex.Refusals.Select(r => $"{fet.Path} (ComposedFET) cannot be compiled: {r.Field}: {r.Sentence} " +
                                       "Refit it, or keep the FET outside as a separate protected device.")];

    private static (ComposedModel Model, string Path)? LoadFet(ProtectPlan plan, ProtectElement fet, List<string> refusals)
    {
        string file = (fet.Instance.Overrides.FirstOrDefault(o => o.Name == "File")?.Expression ?? "").Trim().Trim('"');
        if (file.Length == 0) { refusals.Add($"{fet.Path} (ComposedFET) names no .cmodel file."); return null; }
        string path = Path.IsPathRooted(file) || plan.BaseDirectory is null ? file : Path.GetFullPath(Path.Combine(plan.BaseDirectory, file));
        try { return (CmodelFile.Read(path), path); }
        catch (Exception ex) { refusals.Add($"{fet.Path} (ComposedFET): '{file}' could not be read ({ex.Message})."); return null; }
    }

    private static void CheckFetOverrides(ProtectElement fet, List<string> refusals)
    {
        foreach (var ov in fet.Instance.Overrides)
            if (ov.Name is "m" or "M" && ov.Expression.Trim() is not ("1" or "1.0"))
                refusals.Add($"{fet.Path} (ComposedFET) states {ov.Name}={ov.Expression}; a compiled module carries no device multiplier. " +
                             "Set the finger count N instead, or set m on the placed part.");
    }

    private sealed class Writer(ProtectPlan plan, string moduleName, List<string> refusals)
    {
        private readonly Dictionary<string, string> _node = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Level, string Name), string> _value = [];
        private readonly List<(string Name, string Type, string Text, HashSet<string> Uses, string What)> _localparams = [];
        private readonly HashSet<string> _thermalNodes = new(StringComparer.Ordinal);
        private bool _ground;
        private readonly Evaluator _evaluator = new();

        public ProtectModule Write()
        {
            var top = plan.Top;
            var insideNets = plan.Inside.SelectMany(e => e.Nets).ToHashSet(StringComparer.Ordinal);
            var terminals = plan.Pins.Where(insideNets.Contains).Concat(plan.Joins).ToList();
            foreach (string t in terminals) _node[t] = $"crf_n{_node.Count + 1}";
            foreach (string n in plan.Inside.SelectMany(e => e.Nets))
                if (n != "0" && !_node.ContainsKey(n)) _node[n] = $"crf_n{_node.Count + 1}";

            // The cell's parameters: the module's, in base SI.
            var parameters = new List<string>();
            foreach (var p in top.Cell.Parameters.Where(p => !p.Hidden))
            {
                if (!IsIdentifier(p.Name) || p.Name.StartsWith("crf_", StringComparison.Ordinal))
                {
                    refusals.Add($"The cell parameter '{p.Name}' cannot be a Verilog-A parameter name; rename it.");
                    continue;
                }
                try
                {
                    var v = _evaluator.Eval(p.DefaultExpression, top.Scope, p.Unit);
                    if (v.Kind != ValueKind.Real) { refusals.Add($"The cell parameter {p.Name} = {p.DefaultExpression} is not a real number; a compiled module's parameters are."); continue; }
                    parameters.Add($"  parameter real {p.Name} = {Num(v.AsReal())};   // {Ascii(p.DefaultExpression)}{(p.Unit is { Length: > 0 } u ? " " + Ascii(u) : "")}");
                }
                catch (Exception ex) { refusals.Add($"The cell parameter {p.Name} = {p.DefaultExpression} could not be evaluated ({ex.Message})."); }
            }

            // Every value below the top, as a localparam.
            foreach (var level in plan.Levels.Where(l => !l.IsTop))
            {
                foreach (var p in level.Cell.Parameters)
                    if (!level.PlacedBy!.Overrides.Any(o => o.Name == p.Name) && !level.Cell.Variables.Any(v => v.Name == p.Name))
                        Define(level, p.Name, () => Site(p.DefaultExpression, p.Unit, level, $"{level.Path}.{p.Name}"));
                foreach (var v in level.Cell.Variables)
                    if (!level.PlacedBy!.Overrides.Any(o => o.Name == v.Name))
                        Define(level, v.Name, () => Site(v.Expression, v.Unit, level, $"{level.Path}.{v.Name}"));
                foreach (var ov in level.PlacedBy!.Overrides)
                    Define(level, ov.Name, () => Site(ov.Expression, ov.Unit, level.Parent!, $"{level.Path}.{ov.Name}"));
            }
            foreach (var v in top.Cell.Variables) Define(top, v.Name, () => Site(v.Expression, v.Unit, top, v.Name));

            // The elements.
            var elements = new List<VaNetwork.Element>();
            var branch = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in plan.Inside.Where(e => e.Type.Equals("L", StringComparison.OrdinalIgnoreCase)))
                branch[e.Path] = $"crf_b{branch.Count + 1}";
            var fragments = new List<(ProtectElement Fet, VaFragment Fragment)>();
            foreach (var e in plan.Inside)
            {
                string type = e.Type.ToUpperInvariant();
                if (type == "COMPOSEDFET") { if (Fet(e, fragments.Count + 1) is { } f) fragments.Add((e, f)); continue; }
                if (type == "MUTUAL")
                {
                    Only(e, "Inductor1", "Inductor2", "M");
                    string L1 = Coupled(e, "Inductor1", branch), L2 = Coupled(e, "Inductor2", branch);
                    elements.Add(new VaNetwork.Element(e.Path, "Mutual", "", "", Value(e, "M"), L1, L2));
                    continue;
                }
                string kind = type switch { "R" => "R", "C" => "C", "L" => "L", _ => "Short" };
                if (e.Nets.Count != 2) { refusals.Add($"{e.Path} ({e.Type}) binds {e.Nets.Count} nets; it has two."); continue; }
                string a = Node(e.Nets[0]), b = Node(e.Nets[1]);
                if (kind == "Short") { Only(e); elements.Add(new VaNetwork.Element(e.Path, kind, a, b, null)); continue; }
                Only(e, kind);
                elements.Add(new VaNetwork.Element(e.Path, kind, a, b, Value(e, kind), Branch: branch.GetValueOrDefault(e.Path)));
            }

            // Ground is a TERMINAL the wrapper ties to node 0, never a `ground` node of the module's own: a compiled
            // module's internal ground node is not the circuit's ground when it runs here (src/Design/RESOLVED.md, VL-16),
            // while one reached through a terminal is — which is how the composed FET's own module gets it, through s.
            if (_ground) { terminals.Add("0"); _node["0"] = Ground; }

            var ordered = Ordered();
            if (refusals.Count > 0) throw new ProtectRefusedException(refusals.Distinct(StringComparer.Ordinal).ToList());

            var sb = new StringBuilder();
            sb.Append($"// {moduleName}: a cell's netlist compiled as one module by circuitRF (Compile netlist).\n");
            sb.Append("// This source is the maker's: it is not shipped in the library, and every platform's build is made from it.\n");
            sb.Append("//\n// Terminals, in order:\n");
            foreach (string t in terminals)
                sb.Append(t == "0" ? $"//   {Ground}  ground: the wrapper ties it to node 0\n"
                                   : $"//   {_node[t]}  {Ascii(t)}{(plan.Joins.Contains(t) ? "  (joins an element left outside)" : "")}\n");
            sb.Append("\n`include \"disciplines.vams\"\n`include \"constants.vams\"\n\n");
            string ports = string.Join(", ", terminals.Select(t => _node[t]));
            sb.Append($"module {moduleName}({ports});\n");
            if (terminals.Count > 0) sb.Append($"  inout {ports};\n");
            var electrical = terminals.Where(t => !_thermalNodes.Contains(_node[t])).Select(t => _node[t]).ToList();
            var thermal = terminals.Where(t => _thermalNodes.Contains(_node[t])).Select(t => _node[t]).ToList();
            if (electrical.Count > 0) sb.Append($"  electrical {string.Join(", ", electrical)};\n");
            if (thermal.Count > 0) sb.Append($"  thermal {string.Join(", ", thermal)};\n");
            var internalNets = _node.Where(kv => !terminals.Contains(kv.Key) && kv.Key != "0").ToList();
            foreach (var chunk in internalNets.Where(kv => !_thermalNodes.Contains(kv.Value)).Select(kv => kv.Value).Chunk(12))
                sb.Append($"  electrical {string.Join(", ", chunk)};\n");
            foreach (var e in elements.Where(e => e.Type == "L"))
                sb.Append($"  branch ({e.A}, {e.B}) {e.BranchName};   // {Ascii(e.Name)}\n");

            sb.Append("\n  // The cell's parameters (base SI)\n");
            foreach (string p in parameters) sb.Append(p).Append('\n');
            if (ordered.Count > 0) sb.Append("\n  // Every value below the cell's own, as an expression of its parameters\n");
            foreach (var lp in ordered) sb.Append($"  localparam {lp.Type} {lp.Name} = {lp.Text};   // {Ascii(lp.What)}\n");

            foreach (var (fet, f) in fragments)
            {
                sb.Append($"\n  // {Ascii(fet.Path)} (ComposedFET)\n");
                foreach (string d in f.Declarations) sb.Append("  ").Append(d).Append('\n');
                sb.Append(f.Functions);
            }

            sb.Append("\n  analog begin\n");
            foreach (string c in VaNetwork.Contributions(elements)) sb.Append($"    {c}\n");
            foreach (var (fet, f) in fragments)
            {
                sb.Append($"\n    // {Ascii(fet.Path)} (ComposedFET)\n");
                sb.Append(f.Body);
            }
            sb.Append("  end\nendmodule\n");

            var overrides = top.Cell.Parameters.Where(p => !p.Hidden).Select(p => new ParameterAssignment(p.Name, p.Name)).ToList();
            return new ProtectModule(moduleName, sb.ToString(), terminals, overrides);
        }

        private string Node(string net)
        {
            if (net == "0") { _ground = true; return Ground; }
            return _node[net];
        }

        private void Only(ProtectElement e, params string[] allowed)
        {
            foreach (var ov in e.Instance.Overrides)
                if (!allowed.Contains(ov.Name, StringComparer.OrdinalIgnoreCase))
                    refusals.Add($"{e.Path} ({e.Type}): its parameter '{ov.Name}' cannot be written as Verilog-A.");
        }

        private string Coupled(ProtectElement e, string parameter, Dictionary<string, string> branch)
        {
            string name = (e.Instance.Overrides.FirstOrDefault(o => o.Name == parameter)?.Expression ?? "").Trim().Trim('"');
            string path = e.Level.IsTop ? name : e.Level.Path + "." + name;
            if (!branch.ContainsKey(path))
                refusals.Add($"{e.Path} (Mutual) couples '{name}', which is not an inductor compiled beside it.");
            return path;
        }

        private string? Value(ProtectElement e, string parameter)
        {
            var ov = e.Instance.Overrides.FirstOrDefault(o => o.Name.Equals(parameter, StringComparison.OrdinalIgnoreCase));
            if (ov is null) { refusals.Add($"{e.Path} ({e.Type}) states no {parameter}."); return null; }
            return Site(ov.Expression, ov.Unit, e.Level, $"{e.Path}'s {parameter}");
        }

        /// <summary>A composed FET as a fragment: terminals and ground bound, every name prefixed, its instance values as
        /// localparams of the cell's parameters.</summary>
        private VaFragment? Fet(ProtectElement e, int index)
        {
            var model = LoadFet(plan, e, refusals);
            CheckFetOverrides(e, refusals);
            if (model is null) return null;
            string prefix = $"crf_u{index}_";
            var bind = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["g"] = Node(e.Nets[0]), ["d"] = Node(e.Nets[1]), ["s"] = Node(e.Nets[2]), [VaNetwork.Ground] = Ground,
            };
            if (e.Nets.Count == 4 && _node.ContainsKey(e.Nets[3]) && plan.Pins.Contains(e.Nets[3]))
            {
                bind["t"] = _node[e.Nets[3]];
                _thermalNodes.Add(_node[e.Nets[3]]);
            }
            else if (e.Nets.Count == 4 && _node.Remove(e.Nets[3], out string? internalT))
                _ = internalT;   // left internal: the fragment declares its own thermal node
            VaFragment f;
            try { f = CmodelVerilogA.Fragment(model.Value.Model, Options("crf_fet", model.Value.Path), prefix, bind); }
            catch (VerilogAExportException ex) { refusals.AddRange(FetRefusals(e, ex)); return null; }
            if (f.Body.Contains(Ground, StringComparison.Ordinal) || f.Declarations.Any(d => d.Contains(Ground, StringComparison.Ordinal))) _ground = true;

            var given = e.Instance.Overrides.Where(o => o.Name is not ("File" or "m" or "M")).ToList();
            foreach (var ov in given)
                if (!f.Parameters.Any(p => p.OwnName == ov.Name))
                    refusals.Add($"{e.Path} (ComposedFET): '{ov.Name}' is not a parameter its module has.");
            foreach (var p in f.Parameters)
            {
                var ov = given.FirstOrDefault(o => o.Name == p.OwnName);
                string text = ov is null ? p.Default
                    : ov.Expression.Trim().ToLowerInvariant() is "true" ? "1" : ov.Expression.Trim().ToLowerInvariant() is "false" ? "0"
                    : Site(ov.Expression, ov.Unit, e.Level, $"{e.Path}.{ov.Name}", linearOnly: false) ?? p.Default;
                var uses = Uses(text);
                _localparams.Add((p.Name, p.Type, text, uses, $"{e.Path}.{p.OwnName}"));
            }
            return f;
        }

        /// <summary>A name at <paramref name="level"/> as the module writes it: a cell parameter by its name, a value below
        /// it by its localparam. Walks up as a run's scope does.</summary>
        private string Resolve(ProtectLevel level, string name)
        {
            for (var l = level; l is not null; l = l.Parent)
            {
                if (l.IsTop)
                {
                    if (l.Cell.Variables.Any(v => v.Name == name)) return Key(l, name);
                    if (l.Cell.Parameters.Any(p => p.Name == name)) return name;
                    break;
                }
                if (l.PlacedBy!.Overrides.Any(o => o.Name == name) || l.Cell.Variables.Any(v => v.Name == name)
                    || l.Cell.Parameters.Any(p => p.Name == name))
                    return Key(l, name);
            }
            throw new VaUntranslatableException($"the name '{name}', which nothing in the cell defines");
        }

        private string Key(ProtectLevel level, string name)
        {
            if (!_value.TryGetValue((level.Path, name), out string? k)) _value[(level.Path, name)] = k = $"crf_k{_value.Count + 1}";
            return k;
        }

        private void Define(ProtectLevel level, string name, Func<string?> text)
        {
            string k = Key(level, name);
            if (_localparams.Any(l => l.Name == k)) return;
            if (text() is { } t) _localparams.Add((k, "real", t, Uses(t), level.IsTop ? name : $"{level.Path}.{name}"));
        }

        private static HashSet<string> Uses(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\bcrf_[ku][A-Za-z0-9_]*"))
                set.Add(m.Value);
            return set;
        }

        /// <summary>
        /// A value at its site, as a run evaluates it: var-unit-wins decided by the level's own scope, and otherwise the
        /// site unit's scale on the result (expressions.md §8). A distribution here is a refusal — it is compiled, so it
        /// could never draw (D4).
        /// </summary>
        private string? Site(string expression, string? unit, ProtectLevel level, string what, bool linearOnly = true)
        {
            Expr ast;
            try { ast = Parser.Parse(expression); }
            catch (Exception ex) { refusals.Add($"{what} = {expression} does not parse ({ex.Message})."); return null; }
            if (HasDistribution(ast))
            {
                refusals.Add($"{what} = {expression} holds a distribution, and it would be compiled, where it can never draw. Make {expression} a cell parameter to keep its spread.");
                return null;
            }
            if (!string.IsNullOrEmpty(unit))
            {
                if (Evaluator.IsUnitBearing(ast, level.Scope)) ast = Evaluator.ScaleBareOperands(ast, level.Scope, unit);
                else if (Units.Scale(unit) is { } s) { if (s != 1.0) ast = new BinaryExpr("*", ast, new NumberExpr(s)); }
                else if (linearOnly) { refusals.Add($"{what} is in {unit}, which is not a scale Verilog-A can apply."); return null; }
            }
            try { return VaEquation.Text(ast, n => Resolve(level, n)); }
            catch (VaUntranslatableException ex)
            {
                refusals.Add($"{what} = {expression} uses {ex.Message}, which cannot be written as Verilog-A.");
                return null;
            }
        }

        private static bool HasDistribution(Expr e) => e switch
        {
            CallExpr c => Evaluator.IsStatisticalCall(c) || c.Args.Any(HasDistribution),
            UnaryExpr u => HasDistribution(u.Operand),
            BinaryExpr b => HasDistribution(b.Left) || HasDistribution(b.Right),
            CompareExpr c => HasDistribution(c.Left) || HasDistribution(c.Right),
            LogicExpr l => HasDistribution(l.Left) || HasDistribution(l.Right),
            ConditionalExpr c => HasDistribution(c.Condition) || HasDistribution(c.Then) || HasDistribution(c.Else),
            _ => false,
        };

        /// <summary>The localparams in an order where each follows what it uses; a cycle is a refusal.</summary>
        private List<(string Name, string Type, string Text, HashSet<string> Uses, string What)> Ordered()
        {
            var byName = _localparams.ToDictionary(l => l.Name, StringComparer.Ordinal);
            var done = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<(string, string, string, HashSet<string>, string)>();
            void Visit(string name)
            {
                if (done.Contains(name) || !byName.TryGetValue(name, out var lp)) return;
                if (!visiting.Add(name)) { refusals.Add($"{lp.What} depends on itself."); return; }
                foreach (string u in lp.Uses.Order(StringComparer.Ordinal)) Visit(u);
                visiting.Remove(name);
                done.Add(name);
                order.Add(lp);
            }
            foreach (var lp in _localparams) Visit(lp.Name);
            return order;
        }
    }
}
