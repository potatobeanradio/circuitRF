using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// Reads a .cnl file and populates a Library + TestBench.
///
/// Line shapes (data-model §10):
///   ; comment                — skipped
///   name = expr [unit]        — global variable
///   define CellName (ports)   — start of cell definition block
///   parameters name=expr ...  — parameter declarations inside a define block
///   Type:Inst nets param=val [unit] ... — primitive component line
///   Cell:Inst nets param=val ...        — cell-instance line
///   end [CellName]            — end of cell definition block
///   analysis Name ...         — raw directive → RawDirective("analysis", ...)
///   measure Name ...          — raw directive → RawDirective("measure", ...)
///
/// Unknown lines are skipped (real-world exports may have header lines).
/// Analysis/measure directives are stored verbatim — grammar is Phase 2.
/// </summary>
public sealed class CnlReader
{
    private readonly Library   _library   = new("netlist");
    private TestBench?         _testBench;
    private Cell?              _currentCell;
    private int                _lineNumber;

    public (Library Library, TestBench TestBench) Read(string source, string testBenchName = "tb")
    {
        _testBench = new TestBench(testBenchName);
        _lineNumber = 0;

        foreach (var rawLine in source.Split('\n'))
        {
            _lineNumber++;
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
                continue;

            try
            {
                if (!TryParseLine(trimmed))
                {
                    // Skip unknown/unrecognised lines (header lines, unknown directives).
                    // Committed fixtures are clean, so this path is for real-world imports.
                }
            }
            catch (Exception ex) when (ex is not CnlReadException)
            {
                throw new CnlReadException(_lineNumber, trimmed, ex.Message, ex);
            }
        }

        return (_library, _testBench);
    }

    public static (Library Library, TestBench TestBench) ReadFile(string path, string testBenchName = "tb")
        => new CnlReader().Read(File.ReadAllText(path), testBenchName);

    // ── Line dispatch ─────────────────────────────────────────────────────────

    private bool TryParseLine(string line)
    {
        // Raw directive keywords: "analysis" and "measure"
        if (line.StartsWith("analysis ", StringComparison.Ordinal) ||
            line.Equals("analysis",  StringComparison.Ordinal))
        {
            var rawLine = line.Length > "analysis".Length
                ? line["analysis".Length..].TrimStart()
                : "";
            (_currentCell is null ? _testBench! : ThrowDirectiveInCell(line))
                .RawDirectives.Add(new RawDirective("analysis", rawLine));
            return true;
        }

        if (line.StartsWith("measure ", StringComparison.Ordinal) ||
            line.Equals("measure", StringComparison.Ordinal))
        {
            var rawLine = line.Length > "measure".Length
                ? line["measure".Length..].TrimStart()
                : "";
            (_currentCell is null ? _testBench! : ThrowDirectiveInCell(line))
                .RawDirectives.Add(new RawDirective("measure", rawLine));
            return true;
        }

        // Cell definition start: "define CellName ( P1 P2 ... )"
        if (line.StartsWith("define ", StringComparison.Ordinal))
        {
            ParseDefine(line);
            return true;
        }

        // End of cell definition: "end" or "end CellName"
        if (line.Equals("end", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("end ", StringComparison.OrdinalIgnoreCase))
        {
            if (_currentCell is null)
                throw new CnlReadException(_lineNumber, line, "'end' without matching 'define'");
            _library.Cells.Add(_currentCell);
            _currentCell = null;
            return true;
        }

        // Parameter declarations inside a define block: "parameters name=expr [unit] ..."
        if (line.StartsWith("parameters ", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("parameters", StringComparison.OrdinalIgnoreCase))
        {
            if (_currentCell is null)
                throw new CnlReadException(_lineNumber, line, "'parameters' outside 'define' block");
            ParseParameterDeclarations(line["parameters".Length..].TrimStart());
            return true;
        }

        // Assignment: "name = expr [unit]"  (no colon in line, has "=", not param=val style)
        if (IsVariableAssignment(line))
        {
            ParseVariableAssignment(line);
            return true;
        }

        // Instance line: "Type:InstName net... [param=val [unit]] ..."
        // Detected by the presence of exactly one ':' before any whitespace
        if (IsInstanceLine(line))
        {
            ParseInstanceLine(line);
            return true;
        }

        return false; // unrecognised — caller skips it
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsVariableAssignment(string line)
    {
        // A variable assignment looks like: "name = expr [unit]"
        // It must have a single '=' not preceded by another '=' or operator char,
        // and no ':' before the '='.
        int eq = line.IndexOf('=');
        if (eq <= 0) return false;
        int colon = line.IndexOf(':');
        if (colon >= 0 && colon < eq) return false; // instance line
        // The left side must be a simple identifier
        var lhs = line[..eq].Trim();
        return IsIdentifier(lhs);
    }

    private static bool IsInstanceLine(string line)
    {
        // An instance line has a ':' before the first whitespace
        int space = line.IndexOfAny([' ', '\t']);
        int colon = line.IndexOf(':');
        return colon >= 0 && (space < 0 || colon < space);
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        return s.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    // ── Parsers ───────────────────────────────────────────────────────────────

    private void ParseDefine(string line)
    {
        // "define CellName ( P1 P2 ... )"  or  "define CellName (P1 P2)"
        var rest = line["define ".Length..].Trim();

        string cellName;
        List<string> ports = [];

        int paren = rest.IndexOf('(');
        if (paren >= 0)
        {
            cellName = rest[..paren].Trim();
            var portSection = rest[(paren + 1)..];
            int close = portSection.IndexOf(')');
            if (close >= 0) portSection = portSection[..close];
            ports.AddRange(portSection.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            // No port list
            cellName = rest.Trim();
        }

        _currentCell = new Cell(cellName);
        _currentCell.Ports.AddRange(ports);
    }

    private void ParseParameterDeclarations(string rest)
    {
        // "name=expr [unit] name2=expr2 [unit2] ..."
        // Parse left-to-right: each "name=expr" token, then optionally a unit identifier.
        var tokens = TokeniseLine(rest);
        int i = 0;
        while (i < tokens.Count)
        {
            var token = tokens[i];
            int eq = token.IndexOf('=');
            if (eq <= 0) { i++; continue; }
            var name = token[..eq].Trim();
            var expr = token[(eq + 1)..].Trim();
            string? unit = null;
            // peek next token: if it looks like a unit suffix, consume it
            if (i + 1 < tokens.Count && Units.IsKnown(tokens[i + 1]))
            {
                unit = tokens[i + 1];
                i++;
            }
            _currentCell!.Parameters.Add(new ParameterDeclaration(name, expr, unit));
            i++;
        }
    }

    private void ParseVariableAssignment(string line)
    {
        int eq = line.IndexOf('=');
        var name = line[..eq].Trim();
        var rest = line[(eq + 1)..].Trim();
        var (expr, unit) = SplitExprUnit(rest);
        var v = new Variable(name, expr, unit);
        if (_currentCell is not null)
            _currentCell.Variables.Add(v);
        else
            _testBench!.GlobalVariables.Add(v);
    }

    private void ParseInstanceLine(string line)
    {
        // "Type:InstName net1 net2 ... [param=val [unit]] ..."
        var tokens = TokeniseLine(line);
        if (tokens.Count == 0) return;

        var typeAndName = tokens[0];
        int colon = typeAndName.IndexOf(':');
        if (colon < 0) return;

        var typeName     = typeAndName[..colon];
        var instanceName = typeAndName[(colon + 1)..];

        // Remaining tokens: nets (no '=') then param=val pairs
        var nets     = new List<string>();
        var overrides = new List<ParameterAssignment>();

        int i = 1;
        while (i < tokens.Count)
        {
            var tok = tokens[i];
            if (tok.Contains('='))
            {
                // parameter assignment: "name=value"
                int eq = tok.IndexOf('=');
                var pname = tok[..eq];
                var pexpr = tok[(eq + 1)..];
                string? unit = null;
                if (i + 1 < tokens.Count && Units.IsKnown(tokens[i + 1]))
                {
                    unit = tokens[i + 1];
                    i++;
                }
                overrides.Add(new ParameterAssignment(pname, pexpr, unit));
            }
            else
            {
                nets.Add(tok);
            }
            i++;
        }

        var inst = new Instance(instanceName, typeName, nets, overrides);
        if (_currentCell is not null)
            _currentCell.Instances.Add(inst);
        else
            _testBench!.Instances.Add(inst); // top-level — goes directly on the TestBench
    }

    // ── Tokenisation helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Split a line into whitespace-separated tokens.
    /// Preserves "name=value" as a single token.
    /// </summary>
    private static List<string> TokeniseLine(string line)
        => [.. line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Given the right-hand side of "name = rhs", split into (expression, unit?).
    /// The unit, if present, is the last token if it matches a known unit.
    /// </summary>
    private static (string Expression, string? Unit) SplitExprUnit(string rhs)
    {
        var tokens = rhs.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return ("", null);
        if (tokens.Length == 1) return (tokens[0], null);
        var last = tokens[^1];
        if (Units.IsKnown(last))
            return (string.Join(" ", tokens[..^1]), last);
        return (rhs, null);
    }

    private static TestBench ThrowDirectiveInCell(string line)
        => throw new CnlReadException(0, line, "Analysis/measure directives cannot appear inside a 'define' block");
}

public sealed class CnlReadException : Exception
{
    public int    LineNumber { get; }
    public string RawLine   { get; }

    public CnlReadException(int lineNumber, string rawLine, string message, Exception? inner = null)
        : base($"Line {lineNumber}: {message} → \"{rawLine}\"", inner)
    {
        LineNumber = lineNumber;
        RawLine    = rawLine;
    }
}
