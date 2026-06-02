using System.Text.RegularExpressions;
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
    private string?            _sourceDirectory;

    public (Library Library, TestBench TestBench) Read(string source,
        string testBenchName = "tb",
        string? sourceDirectory = null)
    {
        _testBench       = new TestBench(testBenchName);
        _sourceDirectory = sourceDirectory;
        _lineNumber      = 0;

        // Pre-process: join backslash-continued lines (VendorA convention).
        var joinedLines = JoinContinuationLines(source.Split('\n'));

        foreach (var rawLine in joinedLines)
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
    {
        var fullPath  = Path.GetFullPath(path);
        var sourceDir = Path.GetDirectoryName(fullPath);
        return new CnlReader().Read(File.ReadAllText(fullPath), testBenchName, sourceDir);
    }

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

    // ── SDD-specific line parser ──────────────────────────────────────────────

    // Matches SDD-style equation assignments: I[p,w]=, Q[p,w]=, F[p,w]=, C[n]=,
    // Cport[n]=, In[p,w]=, Nc[p,q]= — used for boundary detection.
    // Optional whitespace around '=' is allowed (spaced form: I[1,0] = expr).
    // Capture group 3 captures the '=' (to find where the RHS expression starts).
    private static readonly Regex SddAssignmentHeader = new(
        @"(I|Q|F|In|Nc)\[\d+,\d+\]\s*(=)|(C(?:port)?)\[\d+\]\s*(=)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse a raw SDD instance line.
    /// Equation expressions may contain whitespace; multiple assignments per line are allowed.
    /// Boundary between assignments: the next I[p,w]= or Q[p,w]= (etc.) at parenthesis-depth zero.
    /// </summary>
    private void ParseSddLine(string rawLine)
    {
        // First token: SDD:Name
        int firstSpace = rawLine.IndexOfAny([' ', '\t']);
        if (firstSpace < 0) return;
        var typeAndName  = rawLine[..firstSpace];
        int colon        = typeAndName.IndexOf(':');
        if (colon < 0) return;
        var instanceName = typeAndName[(colon + 1)..];
        var rest         = rawLine[(firstSpace + 1)..].TrimStart();

        // Find first equation assignment in `rest`.
        int eqStart = FindFirstSddEquation(rest);

        // Everything before the first equation is net names.
        var netSection = eqStart >= 0 ? rest[..eqStart].Trim() : rest;
        var nets       = netSection.Length > 0
            ? netSection.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();

        // Parse equation assignments from the remainder.
        var overrides = new List<ParameterAssignment>();
        if (eqStart >= 0)
            overrides = ParseSddEquations(rest[eqStart..]);

        var inst = new Instance(instanceName, "SDD", nets, overrides);
        if (_currentCell is not null)
            _currentCell.Instances.Add(inst);
        else
            _testBench!.Instances.Add(inst);
    }

    /// <summary>
    /// Returns the index of the first SDD equation header (I[p,w]=, Q[p,w]=, …)
    /// at parenthesis-depth zero in <paramref name="text"/>, or -1 if none.
    /// </summary>
    private static int FindFirstSddEquation(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth > 0) continue;
            var m = SddAssignmentHeader.Match(text, i);
            if (m.Success && m.Index == i) return i;
        }
        return -1;
    }

    /// <summary>
    /// Split <paramref name="eqSection"/> into SDD ParameterAssignments.
    /// Boundaries are I[p,w]=, Q[p,w]=, etc. at parenthesis-depth zero.
    /// Whitespace around '=' is accepted: "I[1,0] = expr" is valid.
    /// </summary>
    private static List<ParameterAssignment> ParseSddEquations(string eqSection)
    {
        var result     = new List<ParameterAssignment>();
        var boundaries = new List<(int HeaderStart, int ExprStart, string Name)>();

        int depth = 0;
        int i = 0;
        while (i < eqSection.Length)
        {
            char c = eqSection[i];
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { depth--; i++; continue; }
            if (depth == 0)
            {
                var m = SddAssignmentHeader.Match(eqSection, i);
                if (m.Success && m.Index == i)
                {
                    // Assignment name: everything before the optional-whitespace + '='
                    // Find the '=' position in the match to determine where the name ends.
                    int eqPos = m.Value.LastIndexOf('=');
                    string assignName = m.Value[..eqPos].TrimEnd(); // e.g. "I[1,0]"
                    int exprStart = m.Index + m.Length;             // expression starts after '='
                    boundaries.Add((i, exprStart, assignName));
                    i = exprStart;  // skip past the header
                    continue;
                }
            }
            i++;
        }

        // Extract expression text between consecutive boundaries.
        for (int k = 0; k < boundaries.Count; k++)
        {
            var (hStart, exprStart, name) = boundaries[k];
            int exprEnd = k + 1 < boundaries.Count ? boundaries[k + 1].HeaderStart : eqSection.Length;
            var expr = eqSection[exprStart..exprEnd].Trim();
            result.Add(new ParameterAssignment(name, expr));
        }

        return result;
    }

    // ── Line-continuation pre-processor ──────────────────────────────────────

    private static IEnumerable<string> JoinContinuationLines(IEnumerable<string> rawLines)
    {
        var buf = new System.Text.StringBuilder();
        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimEnd().EndsWith('\\'))
            {
                buf.Append(line.TrimEnd()[..^1]);  // append without the trailing '\'
            }
            else
            {
                buf.Append(line);
                yield return buf.ToString();
                buf.Clear();
            }
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    // ── General instance line parser ──────────────────────────────────────────

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

        // SDD lines: delegate to the whitespace-aware SDD parser.
        if (typeName.Equals("SDD", StringComparison.OrdinalIgnoreCase))
        {
            ParseSddLine(line);
            return;
        }

        bool isSnP = typeName.Equals("SnP", StringComparison.OrdinalIgnoreCase);

        // Remaining tokens: nets (no '=') then param=val pairs
        var nets      = new List<string>();
        var overrides = new List<ParameterAssignment>();

        int i = 1;
        while (i < tokens.Count)
        {
            var tok = tokens[i];
            if (tok.Contains('='))
            {
                int eq = tok.IndexOf('=');
                var pname = tok[..eq];
                var pexpr = tok[(eq + 1)..];

                // SnP: silently ignore Temp, CheckPassivity, Noise, SaveCurrent, Mode
                if (isSnP && IsIgnoredSnpParam(pname))
                    { i++; continue; }

                // SnP File: resolve relative path to absolute using source directory
                if (isSnP && pname.Equals("File", StringComparison.OrdinalIgnoreCase) &&
                    pexpr.Length >= 2 && pexpr[0] == '"' && pexpr[^1] == '"')
                {
                    var rawPath      = pexpr[1..^1];
                    var resolvedPath = _sourceDirectory is not null
                        ? Path.GetFullPath(Path.Combine(_sourceDirectory, rawPath))
                        : rawPath;
                    pexpr = "\"" + resolvedPath.Replace('\\', '/') + "\"";
                }

                string? unit = null;
                // Only check for unit suffix when pexpr is NOT a string literal
                if (!(pexpr.Length >= 2 && pexpr[0] == '"') &&
                    i + 1 < tokens.Count && Units.IsKnown(tokens[i + 1]))
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

        // SnP: validate NumPorts vs net count, extract floating reference if N+1 nets
        string? refNetBinding = null;
        if (isSnP)
            ValidateSnpNets(nets, overrides, out refNetBinding);

        var inst = new Instance(instanceName, typeName, nets, overrides)
                   { RefNetBinding = refNetBinding };
        if (_currentCell is not null)
            _currentCell.Instances.Add(inst);
        else
            _testBench!.Instances.Add(inst);
    }

    private void ValidateSnpNets(List<string> nets, List<ParameterAssignment> overrides,
        out string? refNetBinding)
    {
        refNetBinding = null;

        var numPortsOv = overrides.FirstOrDefault(
            ov => ov.Name.Equals("NumPorts", StringComparison.OrdinalIgnoreCase));
        if (numPortsOv is null)
            throw new CnlReadException(_lineNumber, "", "SnP: NumPorts parameter is required");
        if (!int.TryParse(numPortsOv.Expression, out int numPorts) || numPorts < 1)
            throw new CnlReadException(_lineNumber, "",
                $"SnP: NumPorts must be a positive integer, got '{numPortsOv.Expression}'");

        // Validate Type (hard error on anything other than "touchstone")
        var typeOv = overrides.FirstOrDefault(
            ov => ov.Name.Equals("Type", StringComparison.OrdinalIgnoreCase));
        if (typeOv is not null)
        {
            var typeStr = typeOv.Expression.Trim('"');
            if (!typeStr.Equals("touchstone", StringComparison.OrdinalIgnoreCase))
                throw new CnlReadException(_lineNumber, "",
                    $"SnP: Type must be \"touchstone\" in v1, got \"{typeStr}\"");
        }

        if (nets.Count == numPorts)
        {
            // Ground-referenced: reference is ground (node 0), refNetBinding stays null
        }
        else if (nets.Count == numPorts + 1)
        {
            // Floating reference: last net is the shared reference for all ports
            refNetBinding = nets[numPorts];
            nets.RemoveAt(numPorts);
        }
        else
        {
            throw new CnlReadException(_lineNumber, "",
                $"SnP: NumPorts={numPorts} but {nets.Count} nets (expected {numPorts} or {numPorts + 1})");
        }
    }

    private static bool IsIgnoredSnpParam(string name) =>
        name.Equals("Temp",           StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CheckPassivity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Noise",          StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SaveCurrent",    StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Mode",           StringComparison.OrdinalIgnoreCase);

    // ── Tokenisation helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Split a line into whitespace-separated tokens.
    /// Quoted regions ("...") are kept as a single unit, so
    /// key="value with spaces" stays one token.
    /// </summary>
    private static List<string> TokeniseLine(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            var sb = new System.Text.StringBuilder();
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                if (line[i] == '"')
                {
                    sb.Append(line[i++]); // opening "
                    while (i < line.Length && line[i] != '"')
                        sb.Append(line[i++]);
                    if (i < line.Length)
                        sb.Append(line[i++]); // closing "
                }
                else
                {
                    sb.Append(line[i++]);
                }
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
        }
        return tokens;
    }

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
