using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using static CircuitRF.Design.Modeling.VerilogA.VaText;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>
/// A <c>.cmodel</c>'s extrinsic network as Verilog-A (brief-modelcomposer-26 R-mc26-4), mirroring
/// <see cref="ComposedNetworkTemplate.Synthesize"/>: pins 1–3 are the module's <c>g d s</c>, pins 4–6 its internal
/// <c>gi di si</c>, and every other net an internal node of the same name. Each variable is a module parameter in base SI
/// at the base periphery; one with a periphery rule is used through <c>&lt;name&gt;_N</c>, the parameter times the
/// rule's factor at <c>N</c>.
///
/// <para><c>R</c>, <c>C</c>, <c>L</c>, <c>Mutual</c> and <c>Short</c> translate; anything else is refused by name. A
/// resistor named by a variable gives that variable the range <c>(0:inf)</c>, so a zero resistance is refused where it is
/// set rather than divided by.</para>
/// </summary>
internal sealed class VaNetwork
{
    public const string Ground = "gnd";

    /// <summary>The module's three terminals and three intrinsic nodes, in pin order.</summary>
    public static readonly IReadOnlyList<string> PinNodes = ["g", "d", "s", "gi", "di", "si"];

    public sealed record Variable(string Name, double Value, string Rule, string? Unit, bool Positive)
    {
        /// <summary>What the network's elements read: the parameter itself, or its scaled copy.</summary>
        public string Scaled => Rule == PeripheryScaling.Fixed ? Name : Name + "_N";
    }

    public sealed record Element(string Name, string Type, string A, string B, string? Value, string? Inductor1 = null,
                                 string? Inductor2 = null, string? Branch = null)
    {
        /// <summary>An inductor's branch: <see cref="Branch"/> when given, else <c>br_</c> and its name.</summary>
        public string BranchName => Branch ?? "br_" + Name;
    }

    public IReadOnlyList<Variable> Variables { get; }
    public IReadOnlyList<Element> Elements { get; }

    /// <summary>The internal nets, in the order the elements first name them.</summary>
    public IReadOnlyList<string> Nets { get; }

    public bool UsesGround { get; }

    private VaNetwork(IReadOnlyList<Variable> variables, IReadOnlyList<Element> elements, IReadOnlyList<string> nets, bool ground)
    {
        Variables = variables; Elements = elements; Nets = nets; UsesGround = ground;
    }

    /// <summary>The network, or null with every refusal added to <paramref name="refusals"/>.</summary>
    public static VaNetwork? Build(ExtrinsicNetwork network, List<(string Field, string Sentence)> refusals)
    {
        const string field = "Network.Netlist";
        var template = ComposedNetworkTemplate.Build(network);
        var (lib, tb) = new CnlReader().Read(network.Netlist, "network");
        var cell = lib.Cells[0];

        // The template's scope, so an element's value is checked against the number circuitRF folds it to.
        var global = new Scope("network");
        foreach (var v in tb.GlobalVariables) global.Bind(v.Name, v.Expression, v.Unit);
        var local = new Scope("network-cell", global);
        foreach (var p in cell.Parameters) local.Bind(p.Name, p.DefaultExpression, p.Unit);
        foreach (var v in cell.Variables) local.Bind(v.Name, v.Expression, v.Unit);
        var units = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var v in tb.GlobalVariables) units[v.Name] = v.Unit;
        foreach (var p in cell.Parameters) units[p.Name] = p.Unit;
        foreach (var v in cell.Variables) units[v.Name] = v.Unit;

        var known = template.Variables.ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);
        var positive = new HashSet<string>(StringComparer.Ordinal);
        var evaluator = new Evaluator();
        var elements = new List<Element>();
        var nets = new List<string>();
        bool ground = false;
        int before = refusals.Count;

        string Node(string net)
        {
            int k = cell.Ports.IndexOf(net);
            if (k >= 0) return PinNodes[k];
            if (net == "0") { ground = true; return Ground; }
            if (!nets.Contains(net)) nets.Add(net);
            return net;
        }

        string Scaled(string name) => known.TryGetValue(name, out var v) && v.Rule != PeripheryScaling.Fixed ? name + "_N" : name;

        // An element's value: its expression over the scaled variables, the site unit applied as circuitRF applies it —
        // and checked against the number circuitRF folds it to, so a value whose units this cannot follow is refused
        // rather than written wrong.
        string? Value(Instance inst, string parameter)
        {
            var ov = inst.Overrides.FirstOrDefault(o => o.Name.Equals(parameter, StringComparison.OrdinalIgnoreCase));
            if (ov is null)
            {
                refusals.Add((field, $"element '{inst.InstanceName}' ({inst.Reference}) states no {parameter}."));
                return null;
            }
            try
            {
                var ast = Parser.Parse(ov.Expression);
                double want = evaluator.Eval(ov.Expression, local, ov.Unit).AsReal();
                double bare = evaluator.Eval(ov.Expression, local).AsReal();
                string text = VaEquation.Text(ast, Scaled);
                if (Same(want, bare)) return text;
                double scale = ov.Unit is { Length: > 0 } u ? Units.Scale(u) ?? 1.0 : 1.0;
                if (Same(want, bare * scale)) return $"({text})*{Num(scale)}";
            }
            catch (VaUntranslatableException ex)
            {
                refusals.Add((field, $"element '{inst.InstanceName}' ({inst.Reference}): {parameter}={ov.Expression} uses {ex.Message}, which cannot be written as Verilog-A."));
                return null;
            }
            catch (Exception) { /* refused below */ }
            refusals.Add((field, $"element '{inst.InstanceName}' ({inst.Reference}): the value {parameter}={ov.Expression}" +
                                 (ov.Unit is { } su ? $" {su}" : "") + " cannot be written as Verilog-A."));
            return null;
        }

        void OnlyParameters(Instance inst, params string[] allowed)
        {
            foreach (var ov in inst.Overrides)
                if (!allowed.Contains(ov.Name, StringComparer.OrdinalIgnoreCase))
                    refusals.Add((field, $"element '{inst.InstanceName}' ({inst.Reference}): its parameter '{ov.Name}' cannot be written as Verilog-A."));
        }

        var inductors = cell.Instances.Where(i => i.Reference.Equals("L", StringComparison.OrdinalIgnoreCase))
                                      .Select(i => i.InstanceName).ToHashSet(StringComparer.Ordinal);

        foreach (var inst in cell.Instances)
        {
            string type = inst.Reference;
            if (type.Equals("Mutual", StringComparison.OrdinalIgnoreCase))
            {
                OnlyParameters(inst, "Inductor1", "Inductor2", "M");
                string L1 = Unquote(inst.Overrides.FirstOrDefault(o => o.Name == "Inductor1")?.Expression);
                string L2 = Unquote(inst.Overrides.FirstOrDefault(o => o.Name == "Inductor2")?.Expression);
                foreach (string l in (string[])[L1, L2])
                    if (!inductors.Contains(l))
                        refusals.Add((field, $"element '{inst.InstanceName}' (Mutual) couples '{l}', which is not an inductor (L) of the network; only two L elements can be coupled in Verilog-A."));
                elements.Add(new Element(inst.InstanceName, "Mutual", "", "", Value(inst, "M"), L1, L2));
                continue;
            }

            string kind = type.ToUpperInvariant() switch { "R" => "R", "C" => "C", "L" => "L", "SHORT" => "Short", _ => "" };
            if (kind.Length == 0 || inst.NetBindings.Count != 2)
            {
                refusals.Add((field, $"element '{inst.InstanceName}' ({type}) cannot be written as Verilog-A; the export writes R, C, L, Mutual and Short."));
                continue;
            }
            string a = Node(inst.NetBindings[0]), b = Node(inst.NetBindings[1]);
            if (kind == "Short")
            {
                OnlyParameters(inst);
                elements.Add(new Element(inst.InstanceName, kind, a, b, null));
                continue;
            }
            OnlyParameters(inst, kind);
            string? value = Value(inst, kind);
            if (kind == "R" && inst.Overrides.FirstOrDefault(o => o.Name.Equals("R", StringComparison.OrdinalIgnoreCase)) is { } r
                && Parser.Parse(r.Expression) is RefExpr re && known.ContainsKey(re.Name))
                positive.Add(re.Name);
            elements.Add(new Element(inst.InstanceName, kind, a, b, value));
        }

        if (refusals.Count > before) return null;
        var variables = template.Variables
            .Select(v => new Variable(v.Name, v.Value, v.Rule,
                                      units.GetValueOrDefault(v.Name) is { Length: > 0 } u ? Units.BaseUnit(u) : null,
                                      positive.Contains(v.Name)))
            .ToList();
        return new VaNetwork(variables, elements, nets, ground);
    }

    private static bool Same(double a, double b) => a == b || Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));

    private static string Unquote(string? s) => (s ?? "").Trim().Trim('"');

    /// <summary>The scaled copies, one statement each: <c>rg_N = rg*(N0/N)</c>.</summary>
    public IEnumerable<string> ScalingStatements(int n0)
    {
        string N0 = n0.ToString(CultureInfo.InvariantCulture) + ".0";
        foreach (var v in Variables)
            switch (v.Rule)
            {
                case PeripheryScaling.PerFinger:        yield return $"{v.Scaled} = {v.Name}*(N/{N0});"; break;
                case PeripheryScaling.PerFingerInverse: yield return $"{v.Scaled} = {v.Name}*({N0}/N);"; break;
                case PeripheryScaling.Fixed: break;
                default: throw new InvalidOperationException($"no Verilog-A form for the scaling rule '{v.Rule}'.");
            }
    }

    /// <summary>The contributions, one or more per element, each with the element's name in a comment.</summary>
    public IEnumerable<string> Contributions() => Contributions(Elements);

    /// <summary>
    /// The contributions of <paramref name="elements"/> — the one writer for an R, C, L, Mutual or Short, used for a
    /// composed FET's network and for a compiled netlist (brief vendorlib-16) alike. A Mutual's <c>Inductor1</c> and
    /// <c>Inductor2</c> name elements of the same list, whose branch it couples.
    /// </summary>
    public static IEnumerable<string> Contributions(IReadOnlyList<Element> elements)
    {
        var coupled = elements.Where(e => e.Type == "Mutual").ToList();
        var branchOf = elements.Where(e => e.Type == "L").ToDictionary(e => e.Name, e => e.BranchName, StringComparer.Ordinal);
        foreach (var e in elements)
            switch (e.Type)
            {
                case "R":
                    yield return $"I({e.A}, {e.B}) <+ V({e.A}, {e.B})/{Paren(e.Value!)};   // {e.Name}";
                    break;
                case "C":
                    yield return $"I({e.A}, {e.B}) <+ ddt({Paren(e.Value!)}*V({e.A}, {e.B}));   // {e.Name}";
                    break;
                case "L":
                    yield return $"V({e.BranchName}) <+ {Paren(e.Value!)}*ddt(I({e.BranchName}));   // {e.Name}";
                    foreach (var m in coupled)
                    {
                        string? other = m.Inductor1 == e.Name ? m.Inductor2 : m.Inductor2 == e.Name ? m.Inductor1 : null;
                        if (other is not null)
                            yield return $"V({e.BranchName}) <+ {Paren(m.Value!)}*ddt(I({branchOf.GetValueOrDefault(other, "br_" + other)}));   // {m.Name}, coupling {e.Name} to {other}";
                    }
                    break;
                case "Short":
                    yield return $"V({e.A}, {e.B}) <+ 0.0;   // {e.Name}";
                    break;
            }
    }

    private static string Paren(string text) => IsAtom(text) ? text : $"({text})";

    private static bool IsAtom(string text) => text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')
                                               || (text.StartsWith('(') && text.EndsWith(')') && Balanced(text[1..^1]));

    private static bool Balanced(string s)
    {
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }
}
