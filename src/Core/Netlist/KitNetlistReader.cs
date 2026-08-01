using System.Text;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// One thing the reader could not use, named so it can be reported rather than guessed at.
/// </summary>
/// <param name="Line">1-based line number in the source file.</param>
public sealed record KitNetlistNote(int Line, string Message);

/// <summary>What a kit's netlist was found to contain.</summary>
/// <param name="Library">Every subcircuit that could be read, as an ordinary circuitRF cell.</param>
/// <param name="Notes">Everything skipped or unrecognised, by line. Never silently dropped.</param>
/// <param name="Variables">
/// Declarations outside any subcircuit — a kit's process constants. The cells reference them by bare
/// name, so a definition read without them does not resolve.
/// </param>
/// <param name="Functions">Expression functions declared outside any subcircuit, likewise referenced by name.</param>
/// <param name="IncompleteCells">
/// Cells holding something the reader could not read. This is the honest signal that circuitRF cannot
/// build them: not that a type is unfamiliar — an unfamiliar type may well be a device a provider
/// supplies — but that a line of the definition itself was skipped, so what is left is not the
/// circuit the kit wrote.
/// </param>
public sealed record KitNetlistResult(
    Library                       Library,
    IReadOnlyList<KitNetlistNote> Notes,
    IReadOnlyList<Variable>       Variables,
    IReadOnlyList<UserFunction>   Functions,
    IReadOnlySet<string>          IncompleteCells);

/// <summary>Raised when the file's structure is broken in a way that cannot be read past.</summary>
public sealed class KitNetlistException(int line, string message)
    : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// Reads the netlist a kit ships, into the same <see cref="Library"/> of <see cref="Cell"/>s that
/// circuitRF's own <c>.cnl</c> produces — so everything downstream (elaboration, nets, sweeps,
/// results) treats a kit's part exactly like a cell the user drew.
///
/// <para><b>Why this exists.</b> A vendor kit is read-only and self-contained, and importing one must
/// produce a working part with no file placed anywhere afterwards. Three facts a part needs — that it
/// offers a choice of formulation, which choice is buildable, and what circuit it actually is — are
/// all sitting in this file. Every alternative is a declaration someone has to write and put
/// somewhere; reading the file removes the need for any of them.</para>
///
/// <para><b>This reads a FORMAT, not a kit.</b> Nothing here names a supplier, a library, a part or a
/// model family. It is deliberately not a general-purpose importer either: it reads what a kit needs
/// to define a part, and everything else is reported by name and skipped.</para>
/// </summary>
public static class KitNetlistReader
{
    /// <summary>The one <c>Type:Name</c> line that is not a device — the simulator's own options.</summary>
    private const string OptionsType = "Options";

    /// <summary>
    /// Reads a kit's netlist from disk. Knowing WHERE it was read from is what lets a data file the
    /// kit names by a relative path be anchored to where that file actually is — see
    /// <see cref="KitDataFileResolver"/>. Reading from text cannot do that, so it does not try.
    /// </summary>
    public static KitNetlistResult ReadFile(string path)
        => Read(File.ReadAllLines(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    public static KitNetlistResult Read(string text)
        => Read(text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray());

    public static KitNetlistResult Read(IReadOnlyList<string> lines, string? sourceDirectory = null)
    {
        var dataFiles = new KitDataFileResolver(sourceDirectory);

        var library   = new Library("kit");
        var notes     = new List<KitNetlistNote>();
        var globals    = new List<Variable>();
        var functions  = new List<UserFunction>();
        var incomplete = new HashSet<string>(StringComparer.Ordinal);

        Cell?  cell        = null;
        string cellName    = "";
        var    cellParams  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (line, number) in JoinContinuations(lines))
        {
            string s = StripComment(line).Trim();
            if (s.Length == 0) continue;

            if (s.StartsWith("define ", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("define\t", StringComparison.Ordinal))
            {
                if (cell is not null)
                    throw new KitNetlistException(number, $"'define' inside '{cellName}', which has no 'end'.");

                (cellName, var ports) = ParseDefine(s, number);
                cell = new Cell(cellName);
                cell.Ports.AddRange(ports);
                cellParams.Clear();
                continue;
            }

            if (s.StartsWith("end", StringComparison.OrdinalIgnoreCase) &&
                (s.Length == 3 || char.IsWhiteSpace(s[3])))
            {
                if (cell is null) throw new KitNetlistException(number, "'end' without 'define'.");

                string named = s.Length > 3 ? s[3..].Trim() : "";
                // A mismatched name means the file is not nested the way it appears to be, and every
                // cell after this one would be attributed wrongly. That is worth stopping for.
                if (named.Length > 0 && !named.Equals(cellName, StringComparison.OrdinalIgnoreCase))
                    throw new KitNetlistException(number, $"'end {named}' closes '{cellName}'.");

                library.Cells.Add(cell);
                cell = null;
                continue;
            }

            if (cell is null)
            {
                // A kit's process constants and helper functions live out here, and the cells
                // reference them by bare name — a definition read without them does not resolve.
                if (TryParseTopLevel(s, notes, number, globals, functions)) continue;
                continue;
            }

            if (s.StartsWith("parameters", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("parameter",  StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (name, value, unit, isText) in ParseAssignments(s[(s.IndexOf(' ') + 1)..]))
                {
                    // cellParams keeps the RAW text: strcat concatenates it, and a quoted piece
                    // would put quotes in the middle of the result.
                    cellParams[name] = Unquoted(value);
                    cell.Parameters.Add(
                        new ParameterDeclaration(name, AsTextLiteral(value, isText), unit, hidden: isText));
                }
                continue;
            }

            int colon = IndexOfTypeSeparator(s);
            if (colon > 0)
            {
                string type = s[..colon].Trim();
                if (type.Equals(OptionsType, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(new(number, $"'{OptionsType}' is a simulator setting, not a device; skipped."));
                    continue;
                }

                if (TryParseInstance(s, colon, type, cellParams, number, notes, dataFiles) is { } instance)
                    cell.Instances.Add(instance);
                continue;
            }

            int eq = IndexOfAssignment(s);
            if (eq > 0)
            {
                string name = s[..eq].Trim();
                string expr = RewriteExpression(s[(eq + 1)..].Trim());
                if (IsIdentifier(name))
                {
                    cell.Variables.Add(new Variable(name, expr));
                    continue;
                }
            }

            // Only an UNREADABLE line marks the cell incomplete. A deliberately skipped simulator
            // setting does not: the circuit is still the one the kit wrote.
            notes.Add(new(number, $"not understood, skipped: {Shorten(s)}"));
            incomplete.Add(cellName);
        }

        if (cell is not null)
            throw new KitNetlistException(lines.Count, $"'{cellName}' has no 'end'.");

        return new KitNetlistResult(library, notes, globals, functions, incomplete);
    }

    /// <summary>
    /// Reads a declaration outside any subcircuit: <c>name(a, b) = expr</c> as an expression function,
    /// <c>name = expr</c> as a constant. Anything else out here belongs to the simulator, not to a
    /// part's definition, and is passed over in silence — reporting every such line would bury the
    /// notes that matter.
    /// </summary>
    private static bool TryParseTopLevel(
        string s, List<KitNetlistNote> notes, int number,
        List<Variable> globals, List<UserFunction> functions)
    {
        int eq = IndexOfAssignment(s);
        if (eq <= 0) return false;

        string lhs  = s[..eq].Trim();
        string expr = RewriteExpression(s[(eq + 1)..].Trim());

        if (IsIdentifier(lhs)) { globals.Add(new Variable(lhs, expr)); return true; }

        int open = lhs.IndexOf('(');
        if (open <= 0 || !lhs.EndsWith(')')) return false;

        string name = lhs[..open].Trim();
        if (!IsIdentifier(name)) return false;

        var args = lhs[(open + 1)..^1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .ToArray();
        if (args.Length == 0 || !args.All(IsIdentifier)) return false;

        try { functions.Add(new UserFunction(name, args, expr)); }
        catch (Exception ex)
        {
            // The body is the kit's, not ours — an unreadable one is reported, never approximated.
            notes.Add(new(number, $"'{name}' could not be read as a function ({ex.Message}); skipped."));
        }
        return true;
    }

    // ── line assembly ─────────────────────────────────────────────────────────

    /// <summary>
    /// Joins backslash-continued lines, reporting each joined line at the number it STARTED on —
    /// which is where a reader of the file would look for it.
    /// </summary>
    private static IEnumerable<(string Line, int Number)> JoinContinuations(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        int start = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i];
            string trimmed = raw.TrimEnd();

            // A trailing backslash inside a quoted string is part of the string, not a continuation.
            bool continues = trimmed.EndsWith('\\') && !EndsInsideQuotes(trimmed);

            if (sb.Length == 0) start = i + 1;
            sb.Append(continues ? trimmed[..^1] : trimmed).Append(' ');

            if (continues) continue;

            yield return (sb.ToString(), start);
            sb.Clear();
        }

        if (sb.Length > 0) yield return (sb.ToString(), start);
    }

    private static bool EndsInsideQuotes(string s)
    {
        bool inQuotes = false;
        foreach (char c in s) if (c == '"') inQuotes = !inQuotes;
        return inQuotes;
    }

    /// <summary>Removes a trailing comment, respecting quotes. Both <c>;</c> and <c>#</c> start one.</summary>
    private static string StripComment(string s)
    {
        bool inQuotes = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') inQuotes = !inQuotes;
            else if (!inQuotes && (s[i] == ';' || s[i] == '#')) return s[..i];
        }
        return s;
    }

    // ── define ────────────────────────────────────────────────────────────────

    private static (string Name, List<string> Ports) ParseDefine(string s, int number)
    {
        string rest = s[("define".Length)..].Trim();

        int open = rest.IndexOf('(');
        string name = (open < 0 ? rest : rest[..open]).Trim();
        if (name.Length == 0) throw new KitNetlistException(number, "'define' names no cell.");

        var ports = new List<string>();
        if (open >= 0)
        {
            int close = rest.IndexOf(')', open);
            if (close < 0) throw new KitNetlistException(number, $"'define {name}' has no closing ')'.");
            ports.AddRange(rest[(open + 1)..close]
                .Split([' ', '\t', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        }

        return (name, ports);
    }

    // ── instances ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>:</c> that separates a device type from its instance name — the FIRST one, and only
    /// when it is inside the leading word. A colon later in the line belongs to a value.
    /// </summary>
    private static int IndexOfTypeSeparator(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]) || s[i] == '=' || s[i] == '"') return -1;
            if (s[i] == ':') return i;
        }
        return -1;
    }

    private static Instance? TryParseInstance(
        string s, int colon, string type, Dictionary<string, string> cellParams,
        int number, List<KitNetlistNote> notes, KitDataFileResolver dataFiles)
    {
        var words = SplitRespectingQuotes(s[(colon + 1)..]);
        if (words.Count == 0)
        {
            notes.Add(new(number, $"'{type}' names no instance; skipped."));
            return null;
        }

        string instanceName = words[0];

        // Nets are the bare words before the first assignment. Everything from there on is
        // parameters, so a net can never be mistaken for one or the other by position alone.
        int firstAssignment = words.FindIndex(1, w => IndexOfAssignment(w) > 0);
        int netsEnd = firstAssignment < 0 ? words.Count : firstAssignment;

        var nets = words.Skip(1).Take(netsEnd - 1).ToList();

        var overrides = new List<ParameterAssignment>();
        if (firstAssignment >= 0)
            foreach (var (name, value, unit, isText) in
                     ParseAssignments(string.Join(' ', words.Skip(firstAssignment))))
            {
                string resolved = ResolveStrcat(value, cellParams);

                // A path the kit wrote relative to its own data folder is anchored HERE, where the
                // file it came from is still known. Left relative, it survives into the generated
                // .cnl and is finally resolved against the workspace — a directory the kit has
                // nothing to do with — so the run fails naming a file that is sitting in the kit.
                string anchored = dataFiles.Resolve(Unquoted(resolved)) ?? resolved;

                // A strcat that actually resolved produced a PATH — text, even though the source
                // expression was not quoted. So did an anchored file.
                overrides.Add(new ParameterAssignment(
                    name, AsTextLiteral(anchored, isText || anchored != value), unit));
            }

        // The dialect spells an N-port Touchstone block by writing the port count INTO the type
        // name — `S15P`. circuitRF's own device is `SnP` and carries the count as a parameter, so the
        // count is moved out of the name here. Without this the type resolves as neither a primitive
        // nor a cell and elaboration fails with "Cell 'S15P' not found in libraries".
        //
        // The count is taken from the NAME rather than from how many nets were listed: the last net
        // of such a block is the reference node, so counting nets would give one port too many.
        if (TouchstonePortCount(type) is { } ports)
        {
            type = "SnP";
            if (!overrides.Any(o => o.Name.Equals("NumPorts", StringComparison.OrdinalIgnoreCase)))
                overrides.Add(new ParameterAssignment("NumPorts", ports.ToString(CultureInfo.InvariantCulture), null));
        }

        return new Instance(instanceName, type, nets, overrides);
    }

    /// <summary>
    /// The port count of an <c>S&lt;N&gt;P</c> type name, or null for anything else. Matched whole,
    /// so an ordinary cell whose name merely starts with S and ends with P is not mistaken for one.
    /// </summary>
    internal static int? TouchstonePortCount(string type)
    {
        if (type.Length < 3) return null;
        if (type[0] is not ('S' or 's') || type[^1] is not ('P' or 'p')) return null;

        string digits = type[1..^1];
        return digits.All(char.IsAsciiDigit)
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n >= 1
            ? n : null;
    }

    // ── assignments ───────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a run of <c>k=v [unit]</c> pairs.
    ///
    /// <para><b>The unit rule, which silently corrupts if it is wrong.</b> A bare word after a value
    /// is that value's UNIT (<c>R=0.001 Ohm</c>); a word containing <c>=</c> starts the next
    /// parameter. Reading <c>R=1 TOhm</c> as <c>R=1</c> gives a resistor a thousand billion times too
    /// small and everything downstream still runs.</para>
    /// </summary>
    private static IEnumerable<(string Name, string Value, string? Unit, bool IsText)> ParseAssignments(string s)
    {
        var words = SplitRespectingQuotes(s);

        for (int i = 0; i < words.Count; i++)
        {
            int eq = IndexOfAssignment(words[i]);
            if (eq <= 0) continue;                       // a stray word with no '=' — nothing to bind

            string name  = words[i][..eq].Trim();
            string value = words[i][(eq + 1)..].Trim();

            // A value may be split from its name by spaces: "k = v".
            if (value.Length == 0 && i + 1 < words.Count && IndexOfAssignment(words[i + 1]) <= 0)
                value = words[++i];

            // The dialect's own boolean words. They are RESERVED tokens here (`Noise=no`,
            // `TopologyCheck=yes`), never variable names — so they are text, exactly as if the kit
            // had quoted them. Without this they reach the expression engine as bare words and
            // elaboration fails with "Unresolved name 'no'", reported from a kit.
            //
            // This is deliberately a CLOSED list and not "quote any bare word": `R=SECOND` and
            // `Size=Gate_Periphery*1.0e3` are genuine references, and quoting those would turn every
            // parameter that refers to a variable into a meaningless string.
            if (IsDialectBoolean(value))
            {
                // Yielded ALREADY QUOTED, not merely flagged as text. Not every consumer looks at
                // the text flag — the instance-override path builds a ParameterAssignment from the
                // value alone — so the literal has to be in the value itself to be safe everywhere.
                yield return (name, $"\"{value}\"", null, true);
                continue;
            }

            bool isText = value.Length >= 2 && value[0] == '"' && value[^1] == '"';
            if (isText)
            {
                // A backslash in a quoted value is a directory separator, not an escape — kits write
                // paths the way the platform they were authored on writes them, and a trailing one is
                // how a folder is spelled (`Path="Data\"`). Normalised so the path works everywhere,
                // and so joining it to a filename produces a path rather than a run-on word.
                value = value[1..^1].Replace('\\', '/');
            }

            string? unit = null;
            if (!isText && i + 1 < words.Count && IsBareUnitWord(words[i + 1]))
                unit = words[++i];

            // A kit writes the unit glued as readily as spaced — `CLINE=1 pF` and `LLINE=1pH` sit on
            // the same line of a kit. Unsplit, the glued one reaches the expression engine as
            // `1pH` and fails to parse, so the two spellings must mean the same thing here.
            // Only when no separate unit word was taken, so `1pH pH` can never arise.
            if (!isText && unit is null && CnlReader.TrySplitGluedUnit(value, out var gv, out var gu))
            {
                value = gv;
                unit  = gu;
            }

            yield return (name, RewriteExpression(value), unit, isText);
        }
    }

    /// <summary>A word that can only be a unit: no <c>=</c>, no quotes, no brackets, not a number.</summary>
    private static bool IsBareUnitWord(string w)
    {
        if (w.Length == 0 || IndexOfAssignment(w) > 0) return false;
        if (w.Contains('"') || w.Contains('(') || w.Contains(')') || w.Contains(',')) return false;
        return !double.TryParse(w, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out _)
               && w.All(c => char.IsLetter(c) || c == '%' || c == '/');
    }

    /// <summary>Index of a binding <c>=</c> — never one belonging to <c>==</c>, <c>&lt;=</c>, <c>!=</c>.</summary>
    private static int IndexOfAssignment(string s)
    {
        bool inQuotes = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') { inQuotes = !inQuotes; continue; }
            if (inQuotes || s[i] != '=') continue;
            if (i + 1 < s.Length && s[i + 1] == '=') { i++; continue; }
            if (i > 0 && s[i - 1] is '=' or '<' or '>' or '!') continue;
            return i;
        }
        return -1;
    }

    private static List<string> SplitRespectingQuotes(string s)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        int depth = 0;

        foreach (char c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; sb.Append(c); continue; }
            if (!inQuotes && c == '(') depth++;
            if (!inQuotes && c == ')') depth--;

            if (!inQuotes && depth == 0 && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    // ── expressions ───────────────────────────────────────────────────────────

    /// <summary>
    /// The one place a kit's expression text is translated into circuitRF's own grammar. Every
    /// rewrite here is a SPELLING change — the kit's meaning is carried through untouched — so a
    /// caller never has to know which dialect a value came from.
    /// </summary>
    public static string RewriteExpression(string expr)
        => RewritePowerOperator(RewriteConditionals(expr));

    /// <summary>
    /// Rewrites the kit's <c>**</c> exponentiation into circuitRF's own <c>^</c>. Both are
    /// right-associative and bind tighter than the arithmetic operators, so this is a spelling
    /// change and not a change of meaning.
    ///
    /// <para>Quoted text is skipped. The same values carry file paths and enum names, and a
    /// <c>**</c> inside one of those is data — rewriting it would corrupt a path rather than
    /// translate an operator.</para>
    /// </summary>
    public static string RewritePowerOperator(string expr)
    {
        if (!expr.Contains('*')) return expr;

        var sb = new System.Text.StringBuilder(expr.Length);
        bool inQuotes = false;

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '"') { inQuotes = !inQuotes; sb.Append(c); continue; }

            if (!inQuotes && c == '*' && i + 1 < expr.Length && expr[i + 1] == '*')
            {
                sb.Append('^');
                i++;                       // consume the second '*' of the pair
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rewrites <c>if(c) then (a) else (b) endif</c> into circuitRF's own <c>if(c, a, b)</c>. Purely
    /// syntactic — the condition and both branches are carried through untouched, so nothing about
    /// what the kit meant is reinterpreted here.
    /// </summary>
    public static string RewriteConditionals(string expr)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            int at = FindKeyword(expr, "if", 0);
            if (at < 0) return expr;

            if (TryRewriteOneConditional(expr, at, out string rewritten)) expr = rewritten;
            else return expr;   // malformed — left exactly as the kit wrote it
        }
        return expr;
    }

    /// <summary>
    /// Rewrites the conditional starting at <paramref name="at"/> into circuitRF's own
    /// <c>if(c, a, b)</c>, following any <c>elseif</c> chain into nested calls.
    ///
    /// <para>A branch may be bracketed (<c>then (1.0e-6)</c>) or bare (<c>then "m1"</c>) — kits write
    /// both — so a bare branch runs to the next keyword at the same bracket depth. The condition and
    /// every branch are carried through untouched: this is a syntax change, and nothing about what
    /// the kit meant is reinterpreted.</para>
    /// </summary>
    private static bool TryRewriteOneConditional(string expr, int at, out string result)
    {
        result = expr;

        if (!TryReadBracketed(expr, at + 2, out string cond, out int p)) return false;
        if (!TryTakeKeyword(expr, p, "then", out p)) return false;
        if (!TryReadBranch(expr, p, out string then, out p)) return false;

        var conds   = new List<string> { cond };
        var branches = new List<string> { then };

        while (TryTakeKeyword(expr, p, "elseif", out int afterElseIf))
        {
            if (!TryReadBracketed(expr, afterElseIf, out string c2, out p)) return false;
            if (!TryTakeKeyword(expr, p, "then", out p)) return false;
            if (!TryReadBranch(expr, p, out string b2, out p)) return false;
            conds.Add(c2);
            branches.Add(b2);
        }

        if (!TryTakeKeyword(expr, p, "else", out p)) return false;
        if (!TryReadBranch(expr, p, out string otherwise, out p)) return false;
        if (!TryTakeKeyword(expr, p, "endif", out int end)) return false;

        string built = otherwise;
        for (int i = conds.Count - 1; i >= 0; i--)
            built = $"if({conds[i]}, {branches[i]}, {built})";

        result = string.Concat(expr.AsSpan(0, at), built, expr.AsSpan(end));
        return true;
    }

    /// <summary>A branch: a bracketed expression, or everything up to the next keyword at depth 0.</summary>
    private static bool TryReadBranch(string s, int from, out string branch, out int after)
    {
        while (from < s.Length && char.IsWhiteSpace(s[from])) from++;

        if (from < s.Length && s[from] == '(' && TryReadBracketed(s, from, out branch, out after))
            return true;

        int depth = 0;
        bool inQuotes = false;
        for (int i = from; i < s.Length; i++)
        {
            if (s[i] == '"') { inQuotes = !inQuotes; continue; }
            if (inQuotes) continue;
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (depth == 0 && (StartsKeyword(s, i, "elseif") || StartsKeyword(s, i, "else") ||
                                    StartsKeyword(s, i, "endif")))
            {
                branch = s[from..i].Trim();
                after  = i;
                return branch.Length > 0;
            }
        }

        branch = ""; after = from;
        return false;
    }

    /// <summary>Index of a whole-word keyword at or after <paramref name="from"/>, or -1.</summary>
    private static int FindKeyword(string s, string keyword, int from)
    {
        for (int i = from; i + keyword.Length <= s.Length; i++)
            if (StartsKeyword(s, i, keyword)) return i;
        return -1;
    }

    private static bool StartsKeyword(string s, int i, string keyword)
    {
        if (i + keyword.Length > s.Length) return false;
        if (!s.AsSpan(i, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')) return false;
        int nxt = i + keyword.Length;
        return nxt == s.Length || !(char.IsLetterOrDigit(s[nxt]) || s[nxt] == '_');
    }

    private static bool TryReadBracketed(string s, int from, out string inner, out int after)
    {
        inner = ""; after = from;
        while (from < s.Length && char.IsWhiteSpace(s[from])) from++;
        if (from >= s.Length || s[from] != '(') return false;

        int depth = 0;
        for (int i = from; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0)
            {
                inner = s[(from + 1)..i].Trim();
                after = i + 1;
                return true;
            }
        }
        return false;
    }

    private static bool TryTakeKeyword(string s, int from, string keyword, out int after)
    {
        while (from < s.Length && char.IsWhiteSpace(s[from])) from++;
        after = from + keyword.Length;
        return from + keyword.Length <= s.Length
            && s.AsSpan(from, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves <c>strcat(a, "b", …)</c> to the literal it means, using the enclosing cell's own
    /// parameter defaults for any name it joins. Left exactly as written when a piece cannot be
    /// resolved — a half-built path is worse than the expression that produced it.
    /// </summary>
    public static string ResolveStrcat(string value, IReadOnlyDictionary<string, string> cellParams)
    {
        int at = value.IndexOf("strcat", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return value;
        if (!TryReadBracketed(value, at + "strcat".Length, out string args, out int after)) return value;

        var sb = new StringBuilder();
        foreach (var piece in SplitTopLevelCommas(args))
        {
            string p = piece.Trim();
            if (p.Length >= 2 && p[0] == '"' && p[^1] == '"') { sb.Append(p[1..^1]); continue; }
            if (cellParams.TryGetValue(p, out string? bound)) { sb.Append(bound); continue; }
            return value;   // not resolvable — hand back what the kit wrote
        }

        return string.Concat(value.AsSpan(0, at), sb.ToString(), value.AsSpan(after));
    }

    private static IEnumerable<string> SplitTopLevelCommas(string s)
    {
        var sb = new StringBuilder();
        bool inQuotes = false;
        int depth = 0;

        foreach (char c in s)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && c == '(') depth++;
            else if (!inQuotes && c == ')') depth--;
            else if (!inQuotes && depth == 0 && c == ',') { yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static bool IsIdentifier(string s)
        => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..57] + "…";

    /// <summary>
    /// The netlist dialect's boolean literals. Reserved words in this format, so treating them as
    /// text is a translation rather than a guess — see <c>ParseAssignments</c> for why the list is
    /// closed.
    /// </summary>
    internal static bool IsDialectBoolean(string value) =>
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("no",  StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes a value that is TEXT survive the expression engine.
    ///
    /// <para>The reader strips the quotes a kit wrote, because the text is what a worker wants. But
    /// everything it produces is later EVALUATED, and a bare word is a variable name — so
    /// <c>FS="PROC1"</c> became "Unresolved name 'PROC1'". Re-quoting at this boundary restores the
    /// author's meaning. Reported three times from a kit, on three different paths, which is
    /// why the rule lives in one place instead of at each of them.</para>
    ///
    /// <para>Already-quoted values are left alone, so applying this twice is harmless.</para>
    /// </summary>
    internal static string AsTextLiteral(string value, bool isText)
    {
        if (!isText || value.Length == 0) return value;
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value;
        if (value.Contains('"')) return value;   // no escape syntax here; report it rather than mangle it
        return $"\"{value}\"";
    }

    /// <summary>Strips one layer of quotes, for the raw text <c>strcat</c> concatenates.</summary>
    private static string Unquoted(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
