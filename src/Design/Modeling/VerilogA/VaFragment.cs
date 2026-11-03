using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>
/// A module written by one of circuitRF's own Verilog-A writers, taken apart so it can be inlined into a larger module
/// (brief vendorlib-16 R-vl16-1b): every name it declares — node, branch, variable, function, function argument and
/// parameter — prefixed, its terminals and its ground bound to the host's nodes, and its parameters turned into variables
/// the host assigns. The writer is not copied: <see cref="CmodelVerilogA.Fragment"/> renders the standalone module and
/// this renames it, so there is one writer and two outputs.
///
/// <para>The renaming is by token, never by text substitution: identifiers inside strings and comments, system names
/// (<c>$temperature</c>), macros (<c>`M_PI</c>), numbers and the access functions (<c>V(…)</c>, <c>I(…)</c>,
/// <c>Temp(…)</c>, <c>Pwr(…)</c>) are left alone. A name is renamed everywhere it occurs, so a function's own
/// argument that shares a name with a module variable stays consistent.</para>
/// </summary>
internal sealed partial class VaFragment
{
    /// <summary>A parameter of the standalone module, now a variable of the host: its type, name (prefixed) and its
    /// default as the module wrote it (prefixed).</summary>
    public sealed record Parameter(string Type, string OwnName, string Name, string Default);

    /// <summary>The module's terminals, in order, as the module named them.</summary>
    public IReadOnlyList<string> Terminals { get; }
    /// <summary>Which of <see cref="Terminals"/> are thermal.</summary>
    public IReadOnlySet<string> ThermalTerminals { get; }
    /// <summary>Node, branch and variable declarations, renamed, terminals and ground left out.</summary>
    public IReadOnlyList<string> Declarations { get; }
    public IReadOnlyList<Parameter> Parameters { get; }
    /// <summary>The analog functions, renamed.</summary>
    public string Functions { get; }
    /// <summary>The analog block's statements, renamed.</summary>
    public string Body { get; }

    private VaFragment(IReadOnlyList<string> terminals, IReadOnlySet<string> thermal, IReadOnlyList<string> declarations,
                       IReadOnlyList<Parameter> parameters, string functions, string body)
    {
        Terminals = terminals; ThermalTerminals = thermal; Declarations = declarations; Parameters = parameters;
        Functions = functions; Body = body;
    }

    private static readonly HashSet<string> AccessFunctions = new(StringComparer.Ordinal) { "V", "I", "Temp", "Pwr", "Flux", "Pot" };

    /// <summary>
    /// <paramref name="module"/> renamed: each declared name prefixed by <paramref name="prefix"/>, except a name
    /// <paramref name="bind"/> maps (a terminal or the ground node), which becomes the host's node.
    /// </summary>
    public static VaFragment From(string module, string prefix, IReadOnlyDictionary<string, string> bind)
    {
        var lines = module.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => ModuleLine().IsMatch(l));
        int end = Array.FindLastIndex(lines, l => l.Trim() == "endmodule");
        if (start < 0 || end < start) throw new InvalidOperationException("the module text has no module … endmodule.");
        var head = ModuleLine().Match(lines[start]);
        var terminals = head.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        // Every declared name, in the whole module (functions' own names and arguments included).
        var declared = new HashSet<string>(StringComparer.Ordinal);
        string stripped = StripComments(string.Join('\n', lines[(start + 1)..end]));
        foreach (Match m in Declaration().Matches(stripped))
            foreach (string n in m.Groups[2].Value.Split(','))
                if (Ident().Match(n.Trim()) is { Success: true } id) declared.Add(id.Value);
        foreach (Match m in ParameterDecl().Matches(stripped)) declared.Add(m.Groups[2].Value);
        foreach (Match m in BranchDecl().Matches(stripped)) declared.Add(m.Groups[1].Value);
        foreach (Match m in FunctionDecl().Matches(stripped)) declared.Add(m.Groups[1].Value);
        foreach (string t in terminals) declared.Add(t);

        string Map(string name) => bind.TryGetValue(name, out string? to) ? to : prefix + name;
        string R(string text) => Rename(text, declared, Map);

        var thermal = new HashSet<string>(StringComparer.Ordinal);
        var declarations = new List<string>();
        var parameters = new List<Parameter>();
        var functions = new StringBuilder();
        var body = new StringBuilder();
        int analog = -1;
        for (int i = start + 1; i < end; i++)
        {
            string line = lines[i];
            string t = line.Trim();
            if (t.StartsWith("analog function", StringComparison.Ordinal))
            {
                // A function's own names — its arguments and locals — are its scope's, never a terminal: a continuation
                // function's local `s` is not the source pin, so binding it to the host's node would rewrite the
                // function. Each is prefixed; anything else it names (a parameter) is mapped as the module maps it.
                int first = i;
                while (i < end && lines[i].Trim() != "endfunction") i++;
                var block = lines[first..i];
                var locals = new HashSet<string>(StringComparer.Ordinal);
                string blockText = StripComments(string.Join('\n', block));
                foreach (Match m in Declaration().Matches(blockText))
                    foreach (string n in m.Groups[2].Value.Split(','))
                        if (Ident().Match(n.Trim()) is { Success: true } id) locals.Add(id.Value);
                // The function's own name is the module's (its callers use it); everything else declared in it is local.
                // A local gets a prefix of its own, never the module's: the standalone module declares its variables
                // after its functions, and a function local sharing a module variable's name declared BEFORE it was read
                // as that variable by the compiler (VL-16, src/Design/RESOLVED.md).
                if (FunctionDecl().Match(blockText) is { Success: true } fn) locals.Remove(fn.Groups[1].Value);
                string Local(string name) => locals.Contains(name) ? prefix + "l_" + name : Map(name);
                foreach (string l in block) functions.Append(Rename(l, declared, Local)).Append('\n');
                functions.Append(lines[i]).Append('\n');
                continue;
            }
            if (t == "analog begin") { analog = i; break; }
            if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("inout", StringComparison.Ordinal)) continue;
            if (ParameterDecl().Match(t) is { Success: true } p)
            {
                string def = p.Groups[3].Value.Trim();
                int from = FindFrom(def);
                if (from >= 0) def = def[..from].Trim();
                parameters.Add(new Parameter(p.Groups[1].Value, p.Groups[2].Value, Map(p.Groups[2].Value), R(def)));
                continue;
            }
            // A node declaration keeps only the names that stay the fragment's own.
            if (NodeDecl().Match(t) is { Success: true } nd)
            {
                var names = nd.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (nd.Groups[1].Value == "thermal")
                    foreach (string n in names.Where(terminals.Contains)) thermal.Add(n);
                var own = names.Where(n => !bind.ContainsKey(n)).Select(Map).ToList();
                if (own.Count > 0 && nd.Groups[1].Value != "ground") declarations.Add($"{nd.Groups[1].Value} {string.Join(", ", own)};");
                continue;
            }
            declarations.Add(R(t));
        }
        if (analog < 0) throw new InvalidOperationException("the module text has no analog block.");
        int close = Array.FindLastIndex(lines, end - 1, end - analog, l => l.Trim() == "end");
        for (int i = analog + 1; i < close; i++) body.Append(R(lines[i])).Append('\n');

        return new VaFragment(terminals, thermal, declarations, parameters, functions.ToString(), body.ToString());
    }

    /// <summary>The index of a range's <c>from</c>/<c>exclude</c> keyword in a parameter's default, or -1.</summary>
    private static int FindFrom(string def)
    {
        var m = Regex.Match(def, @"\b(from|exclude)\b");
        return m.Success ? m.Index : -1;
    }

    /// <summary><paramref name="text"/> with every identifier in <paramref name="declared"/> mapped, outside strings,
    /// comments, system names, macros and access-function calls.</summary>
    internal static string Rename(string text, IReadOnlySet<string> declared, Func<string, string> map)
    {
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { sb.Append(text, i, text.Length - i); break; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? text.Length : e + 2;
                sb.Append(text, i, e - i); i = e; continue;
            }
            if (c == '"')
            {
                int j = i + 1;
                while (j < text.Length && text[j] != '"') j += text[j] == '\\' ? 2 : 1;
                j = Math.Min(j + 1, text.Length);
                sb.Append(text, i, j - i); i = j; continue;
            }
            if (c is '$' or '`')
            {
                int j = i + 1;
                while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '_')) j++;
                sb.Append(text, i, j - i); i = j; continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                int j = i;
                while (j < text.Length && (char.IsAsciiDigit(text[j]) || text[j] == '.')) j++;
                if (j < text.Length && text[j] is 'e' or 'E')
                {
                    int k = j + 1;
                    if (k < text.Length && text[k] is '+' or '-') k++;
                    if (k < text.Length && char.IsAsciiDigit(text[k]))
                    {
                        j = k;
                        while (j < text.Length && char.IsAsciiDigit(text[j])) j++;
                    }
                }
                sb.Append(text, i, j - i); i = j; continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int j = i + 1;
                while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '_')) j++;
                string word = text[i..j];
                int k = j;
                while (k < text.Length && text[k] is ' ' or '\t') k++;
                bool call = k < text.Length && text[k] == '(';
                sb.Append(declared.Contains(word) && !(call && AccessFunctions.Contains(word)) ? map(word) : word);
                i = j; continue;
            }
            sb.Append(c); i++;
        }
        return sb.ToString();
    }

    private static string StripComments(string text)
        => Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"//[^\n]*", "");

    [GeneratedRegex(@"^\s*module\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)\s*;")]
    private static partial Regex ModuleLine();

    [GeneratedRegex(@"\b(electrical|thermal|ground|real|integer|input|output|inout)\s+([^;()]+);")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^\s*(electrical|thermal|ground)\s+([^;]+);\s*$")]
    private static partial Regex NodeDecl();

    [GeneratedRegex(@"\bparameter\s+(real|integer)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([^;]+);")]
    private static partial Regex ParameterDecl();

    [GeneratedRegex(@"\bbranch\s*\([^)]*\)\s*([A-Za-z_][A-Za-z0-9_]*)\s*;")]
    private static partial Regex BranchDecl();

    [GeneratedRegex(@"\banalog\s+function\s+(?:real|integer)\s+([A-Za-z_][A-Za-z0-9_]*)\s*;")]
    private static partial Regex FunctionDecl();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Ident();
}
