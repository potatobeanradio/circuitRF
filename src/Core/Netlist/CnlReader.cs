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
            var tb = _currentCell is null ? _testBench! : ThrowDirectiveInCell(line);

            // If this is a type=hb directive, parse it into a typed HarmonicBalanceAnalysis.
            if (TryParseHbDirective(rawLine, out var hbAnalysis))
                tb.Analyses.Add(hbAnalysis!);
            else if (TryParseDcDirective(rawLine, out var dcAnalysis))
                tb.Analyses.Add(dcAnalysis!);
            else if (TryParseLoadpullPursuitDirective(rawLine, _sourceDirectory, out var lppAnalysis))
                tb.Analyses.Add(lppAnalysis!);
            else if (TryParseLoadpullDirective(rawLine, _sourceDirectory, out var lpAnalysis))
                tb.Analyses.Add(lpAnalysis!);
            else if (TryParseParametricSweepDirective(rawLine, out var psAnalysis))
                tb.Analyses.Add(psAnalysis!);
            else if (TryParseSParamDirective(rawLine, out var spAnalysis))
            {
                // Multi-segment: merge consecutive segments with the same analysis name.
                var existing = tb.Analyses
                    .OfType<SParameterAnalysis>()
                    .LastOrDefault(a => a.Name == spAnalysis!.Name);
                if (existing is not null)
                {
                    int idx = tb.Analyses.IndexOf(existing);
                    var merged = new SParameterAnalysis(
                        existing.Name,
                        [.. existing.Sweeps, .. spAnalysis!.Sweeps]);
                    merged.Enabled = existing.Enabled;
                    tb.Analyses[idx] = merged;
                }
                else
                    tb.Analyses.Add(spAnalysis!);
            }
            else
                tb.RawDirectives.Add(new RawDirective("analysis", rawLine));
            return true;
        }

        if (line.StartsWith("measure ", StringComparison.Ordinal) ||
            line.Equals("measure", StringComparison.Ordinal))
        {
            var rawLine = line.Length > "measure".Length
                ? line["measure".Length..].TrimStart()
                : "";
            var tb = _currentCell is null ? _testBench! : ThrowDirectiveInCell(line);
            if (!TryParseMeasurementLine(rawLine, tb))
                tb.RawDirectives.Add(new RawDirective("measure", rawLine));
            return true;
        }

        // Net-label provenance: "labelednets name1 name2 ..."
        if (line.StartsWith("labelednets ", StringComparison.Ordinal) ||
            line.Equals("labelednets", StringComparison.Ordinal))
        {
            if (_currentCell is not null)
                throw new CnlReadException(_lineNumber, line,
                    "'labelednets' is only valid at top level, not inside a define block.");
            var rest = line.Length > "labelednets".Length
                ? line["labelednets".Length..].Trim()
                : "";
            foreach (var net in rest.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                _testBench!.LabeledNets.Add(net);
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

        // User-defined expression function: "name(a, b, ...) = expr". Checked before the plain
        // assignment case, whose LHS test requires a bare identifier and so never matches this.
        if (TryParseFunctionDeclaration(line))
            return true;

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

    private static bool TryParseMeasurementLine(string rawLine, TestBench tb)
    {
        // Format: "Name = expression [unit] [; comment]"
        int eq = rawLine.IndexOf('=');
        if (eq <= 0) return false;
        var name = rawLine[..eq].Trim();
        if (!IsIdentifier(name)) return false;
        var rest = rawLine[(eq + 1)..].Trim();
        // Strip trailing semicolon comment
        int semi = rest.IndexOf(';');
        if (semi >= 0) rest = rest[..semi].Trim();

        // Extract optional trailing unit token — a bare word that follows the expression.
        // Valid units are single tokens like "dB", "V", "dBm", "%", "GHz", etc.
        string expr = rest;
        string? unit = null;
        int lastSpace = rest.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            var candidate = rest[(lastSpace + 1)..];
            if (IsMeasurementUnit(candidate))
            {
                unit = candidate;
                expr = rest[..lastSpace].Trim();
            }
        }

        tb.Measurements.Add(new Measurement(name, expr, unit));
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="s"/> looks like a measurement unit token
    /// (a simple word containing only letters, digits, and '%', starting with a letter or '%').
    /// </summary>
    private static bool IsMeasurementUnit(string s) =>
        s.Length > 0 &&
        (char.IsLetter(s[0]) || s[0] == '%') &&
        s.All(c => char.IsLetterOrDigit(c) || c == '%');

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

    /// <summary>
    /// `name(a, b, ...) = expr` — a user-defined expression function. The expression engine has
    /// supported these since v1 (Evaluator.RegisterFunction); this is the netlist syntax for
    /// declaring one. Distinguished from an ordinary assignment by a parenthesised parameter list
    /// immediately after the name and before the '='.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex RxFunctionDecl =
        new(@"^([A-Za-z_]\w*)\s*\(([^)]*)\)\s*=\s*(.+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private bool TryParseFunctionDeclaration(string line)
    {
        var m = RxFunctionDecl.Match(line);
        if (!m.Success) return false;

        var parameters = m.Groups[2].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .ToArray();
        if (parameters.Length == 0 || parameters.Any(a => !IsIdentifier(a)))
            return false;                    // `f() = ...` or `g(2*x) = ...` is not a declaration

        if (_currentCell is not null)
            throw new CnlReadException(_lineNumber, line,
                "user-defined expression functions are only valid at top level, not inside a define block.");

        _testBench!.Functions.Add(new CircuitRF.Core.Expressions.UserFunction(
            m.Groups[1].Value, parameters, m.Groups[3].Value.Trim()));
        return true;

        static bool IsIdentifier(string t)
            => t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_') && t.All(c => char.IsLetterOrDigit(c) || c == '_');
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
    // Single-index forms I[p]= and Q[p]= are also accepted (sugar for I[p,0] and Q[p,1]).
    // Optional whitespace around '=' is allowed (spaced form: I[1,0] = expr).
    // Capture group 3 captures the '=' (to find where the RHS expression starts).
    private static readonly Regex SddAssignmentHeader = new(
        @"(I|Q|F|In|Nc)\[\d+(,\d+)?\]\s*(=)|(C(?:port)?)\[\d+\]\s*(=)|H\[\d+\]\s*(=)",
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

        // Everything before the first equation is net names OR scalar param=value tokens.
        // Tokens containing '=' are treated as parameter overrides (e.g. Ports=2), not nets.
        var netSection = eqStart >= 0 ? rest[..eqStart].Trim() : rest;
        var nets       = new List<string>();
        var overrides  = new List<ParameterAssignment>();
        if (netSection.Length > 0)
        {
            foreach (var tok in netSection.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = tok.IndexOf('=');
                if (eq > 0 && eq < tok.Length - 1)
                    overrides.Add(new ParameterAssignment(tok[..eq], tok[(eq + 1)..], null));
                else
                    nets.Add(tok);
            }
        }

        // Parse equation assignments from the remainder.
        if (eqStart >= 0)
            overrides.AddRange(ParseSddEquations(rest[eqStart..]));

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

    // ── Z_Port line parser ────────────────────────────────────────────────────

    // Z[i,j]= boundary detection (allows spaces inside the expression).
    private static readonly Regex ZPortAssignmentHeader = new(
        @"Z\[\d+,\d+\]\s*=", RegexOptions.Compiled);

    private void ParseZPortLine(string rawLine)
    {
        int firstSpace = rawLine.IndexOfAny([' ', '\t']);
        if (firstSpace < 0) return;
        var typeAndName  = rawLine[..firstSpace];
        int colon        = typeAndName.IndexOf(':');
        if (colon < 0) return;
        var instanceName = typeAndName[(colon + 1)..];
        var rest         = rawLine[(firstSpace + 1)..].TrimStart();

        // Find first Z[i,j]= at paren-depth 0.
        int eqStart = FindFirstZPortEquation(rest);
        var netSection = eqStart >= 0 ? rest[..eqStart].Trim() : rest;
        var nets = netSection.Length > 0
            ? netSection.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();

        var overrides = new List<ParameterAssignment>();
        if (eqStart >= 0)
            overrides = ParseZPortEquations(rest[eqStart..]);

        // 2N nets: port1+, port1−, port2+, port2−, … Arity validated in the Elaborator.
        var inst = new Instance(instanceName, "Z_Port", nets, overrides);
        if (_currentCell is not null)
            _currentCell.Instances.Add(inst);
        else
            _testBench!.Instances.Add(inst);
    }

    private static int FindFirstZPortEquation(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth > 0) continue;
            var m = ZPortAssignmentHeader.Match(text, i);
            if (m.Success && m.Index == i) return i;
        }
        return -1;
    }

    private static List<ParameterAssignment> ParseZPortEquations(string eqSection)
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
                var m = ZPortAssignmentHeader.Match(eqSection, i);
                if (m.Success && m.Index == i)
                {
                    int eqPos = m.Value.LastIndexOf('=');
                    string assignName = m.Value[..eqPos].TrimEnd(); // e.g. "Z[1,1]"
                    int exprStart = m.Index + m.Length;
                    boundaries.Add((i, exprStart, assignName));
                    i = exprStart;
                    continue;
                }
            }
            i++;
        }

        for (int k = 0; k < boundaries.Count; k++)
        {
            var (hStart, exprStart, name) = boundaries[k];
            int exprEnd = k + 1 < boundaries.Count ? boundaries[k + 1].HeaderStart : eqSection.Length;
            var expr = eqSection[exprStart..exprEnd].Trim();
            result.Add(new ParameterAssignment(name, expr));
        }

        return result;
    }

    // ── Tuner line parser ─────────────────────────────────────────────────────

    // Z[k]= or G[k]= at paren-depth zero (no spaces in the header itself).
    private static readonly Regex TunerHarmonicHeader = new(
        @"[ZG]\[\d+\]\s*=", RegexOptions.Compiled);

    /// <summary>
    /// Parses "Tuner:Name net0 net1  Z[1]=expr  Z[2]=expr  Zdefault=expr  BiasTee=on  Vbias=expr"
    /// The Z[k]= / G[k]= values may be complex expressions (e.g. 80+j*10).
    /// Uses bracket-depth-zero scanning like ParseZPortLine / ParseSddLine.
    /// </summary>
    private void ParseTunerLine(string rawLine)
    {
        int firstSpace = rawLine.IndexOfAny([' ', '\t']);
        if (firstSpace < 0) return;
        var typeAndName  = rawLine[..firstSpace];
        int colon        = typeAndName.IndexOf(':');
        if (colon < 0) return;
        var instanceName = typeAndName[(colon + 1)..];
        var rest         = rawLine[(firstSpace + 1)..].TrimStart();

        // Find first Z[k]= or G[k]= at paren-depth 0.
        int eqStart = FindFirstTunerHarmonic(rest);
        var netSection = eqStart >= 0 ? rest[..eqStart].Trim() : rest;
        // Net tokens never contain '='. The simple params (Zdefault=, Z0=, BiasTee=, Vbias=) may
        // appear in the net section when Z[1]= is written last (the GUI emits this order); they are
        // captured separately by ParseTunerSimpleParams. Drop them here so they never become bogus
        // nets — two tuners sharing the same param strings would otherwise cross-wire their internals.
        var nets = netSection.Length > 0
            ? netSection.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                        .Where(t => !t.Contains('='))
                        .ToList()
            : [];

        // Parse simple key=value pairs before the harmonic entries.
        // Everything after the first harmonic header uses the bracket-depth scanner.
        var overrides = new List<ParameterAssignment>
        {
            new("TunerName", $"\"{instanceName}\""),
        };

        // First, collect plain params (Zdefault, BiasTee, Vbias, Z0) from the net section
        // (they appear before the harmonic expressions in the canonical form, but may appear anywhere).
        // Re-scan rest for non-bracket key=value pairs.
        overrides.AddRange(ParseTunerSimpleParams(rest, eqStart));

        // Then parse Z[k]= / G[k]= with the bracket-depth scanner.
        if (eqStart >= 0)
            overrides.AddRange(ParseTunerHarmonicEquations(rest[eqStart..]));

        var inst = new Instance(instanceName, "Tuner", nets, overrides);
        if (_currentCell is not null)
            _currentCell.Instances.Add(inst);
        else
            _testBench!.Instances.Add(inst);
    }

    private static int FindFirstTunerHarmonic(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth > 0) continue;
            var m = TunerHarmonicHeader.Match(text, i);
            if (m.Success && m.Index == i) return i;
        }
        return -1;
    }

    private static List<ParameterAssignment> ParseTunerSimpleParams(string rest, int harmonicStart)
    {
        // Extract simple key=value params that appear BEFORE the first harmonic entry.
        // These are Zdefault=, BiasTee=, Vbias=, Z0=.
        var result = new List<ParameterAssignment>();
        var prefix = harmonicStart >= 0 ? rest[..harmonicStart] : rest;
        var tokens = TokeniseLine(prefix);
        foreach (var tok in tokens)
        {
            if (!tok.Contains('=')) continue;
            int eq = tok.IndexOf('=');
            var key = tok[..eq].Trim();
            var val = tok[(eq + 1)..].Trim();
            // Only accept known non-harmonic params.
            if (key.Equals("Zdefault", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("BiasTee",  StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Vbias",    StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Z0",       StringComparison.OrdinalIgnoreCase))
            {
                // BiasTee is a string value ("on"/"off"); wrap in quotes if not already.
                if (key.Equals("BiasTee", StringComparison.OrdinalIgnoreCase) &&
                    !(val.StartsWith('"')))
                    val = $"\"{val}\"";
                result.Add(new ParameterAssignment(key, val));
            }
        }
        return result;
    }

    private static List<ParameterAssignment> ParseTunerHarmonicEquations(string eqSection)
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
                var m = TunerHarmonicHeader.Match(eqSection, i);
                if (m.Success && m.Index == i)
                {
                    int eqPos = m.Value.LastIndexOf('=');
                    string assignName = m.Value[..eqPos].TrimEnd(); // e.g. "Z[1]"
                    int exprStart = m.Index + m.Length;
                    boundaries.Add((i, exprStart, assignName));
                    i = exprStart;
                    continue;
                }
            }
            i++;
        }

        for (int k = 0; k < boundaries.Count; k++)
        {
            var (_, exprStart, name) = boundaries[k];
            int exprEnd = k + 1 < boundaries.Count ? boundaries[k + 1].HeaderStart : eqSection.Length;

            // The region contains the harmonic expression value plus any trailing simple params.
            // Harmonic values (80+j*10, 1, 1e-6) have no whitespace; everything after the
            // first whitespace is trailing simple params (Zdefault=, BiasTee=, Vbias=, Z0=).
            var region = eqSection[exprStart..exprEnd].TrimStart();

            // Extract the harmonic expression value (first whitespace-delimited token).
            int ws = region.IndexOfAny([' ', '\t']);
            string harmonicExpr  = ws >= 0 ? region[..ws] : region.TrimEnd();
            string trailingText  = ws >= 0 ? region[(ws + 1)..].TrimStart() : "";

            if (!string.IsNullOrEmpty(harmonicExpr))
                result.Add(new ParameterAssignment(name, harmonicExpr));

            // Extract trailing simple params from the region (Zdefault=, BiasTee=, Vbias=, Z0=).
            // These appear between harmonic values in any order.
            if (!string.IsNullOrEmpty(trailingText))
                result.AddRange(ParseTunerSimpleParamsDirect(trailingText));
        }

        return result;
    }

    /// <summary>
    /// Parses space-delimited key=value tokens for the known simple Tuner params
    /// (Zdefault, BiasTee, Vbias, Z0). Used for tokens that appear anywhere in the
    /// Tuner line (before or after harmonic Z[k]/G[k] entries).
    /// </summary>
    private static List<ParameterAssignment> ParseTunerSimpleParamsDirect(string text)
    {
        var result = new List<ParameterAssignment>();
        var tokens = TokeniseLine(text);
        foreach (var tok in tokens)
        {
            if (!tok.Contains('=')) continue;
            int eq = tok.IndexOf('=');
            var key = tok[..eq].Trim();
            var val = tok[(eq + 1)..].Trim();
            if (!key.Equals("Zdefault", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("BiasTee",  StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("Vbias",    StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("Z0",       StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("BiasTee", StringComparison.OrdinalIgnoreCase) &&
                !val.StartsWith('"'))
                val = $"\"{val}\"";
            result.Add(new ParameterAssignment(key, val));
        }
        return result;
    }

    // ── Shared enabled-flag parser ────────────────────────────────────────────

    /// <summary>
    /// Reads <c>enabled=false</c> from <paramref name="tokens"/>.
    /// Returns <c>false</c> only when the key is present and its value is exactly "false"
    /// (case-insensitive). Absent or any other value → <c>true</c> (default-enabled).
    /// </summary>
    private static bool ParseEnabledToken(IReadOnlyList<string> tokens)
    {
        foreach (var t in tokens)
        {
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (t[..eq].Equals("enabled", StringComparison.OrdinalIgnoreCase))
                return !t[(eq + 1)..].Trim('"').Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    // ── Loadpull analysis directive parser ────────────────────────────────────

    /// <summary>
    /// Attempts to parse a "Name type=dc ..." directive into a <see cref="DcAnalysis"/>.
    /// Returns false if it is not a type=dc directive.
    /// </summary>
    private static bool TryParseDcDirective(string rawLine, out DcAnalysis? result)
    {
        result = null;
        var tokens = TokeniseLine(rawLine);
        if (tokens.Count < 2) return false;

        string analysisName = tokens[0];
        bool isDc = false;
        for (int i = 1; i < tokens.Count; i++)
        {
            int eq = tokens[i].IndexOf('=');
            if (eq > 0)
            {
                string key = tokens[i][..eq];
                string val = tokens[i][(eq + 1)..];
                if (key.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                    val.Equals("dc", StringComparison.OrdinalIgnoreCase))
                    isDc = true;
            }
        }

        if (!isDc) return false;
        result = new DcAnalysis(analysisName) { Enabled = ParseEnabledToken(tokens) };
        return true;
    }

    /// <summary>
    /// Attempts to parse a "Name type=loadpull_pursuit key=value ..." directive.
    /// Returns false if it is not a type=loadpull_pursuit directive.
    /// </summary>
    private static bool TryParseLoadpullPursuitDirective(string rawLine, string? sourceDirectory,
        out CircuitRF.Core.Design.LoadpullPursuitAnalysis? result)
    {
        result = null;
        var tokens = TokeniseLine(rawLine);
        if (tokens.Count < 2) return false;

        string analysisName = tokens[0];
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < tokens.Count; i++)
        {
            int eq = tokens[i].IndexOf('=');
            if (eq <= 0) continue;
            string key = tokens[i][..eq];
            string val = tokens[i][(eq + 1)..];
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                val = val[1..^1];
            kv[key] = val;
        }

        if (!kv.TryGetValue("type", out var typeVal) ||
            !typeVal.Equals("loadpull_pursuit", StringComparison.OrdinalIgnoreCase))
            return false;

        // Resolve OutputGrid path if provided.
        string? outputGrid = kv.GetValueOrDefault("OutputGrid");
        if (!string.IsNullOrEmpty(outputGrid) && sourceDirectory is not null &&
            !Path.IsPathRooted(outputGrid))
            outputGrid = Path.GetFullPath(Path.Combine(sourceDirectory, outputGrid));

        result = new CircuitRF.Core.Design.LoadpullPursuitAnalysis(analysisName)
        {
            ToneExpr              = kv.GetValueOrDefault("Tone",                "0"),
            ToneUnit              = kv.GetValueOrDefault("ToneUnit",            "Hz"),
            MaxHarmonicExpr       = kv.GetValueOrDefault("MaxHarm",             "5"),
            LoadTunerName         = kv.GetValueOrDefault("LoadTuner",           ""),
            SourceTunerName       = kv.GetValueOrDefault("SourceTuner",         ""),
            SweepExpr             = kv.GetValueOrDefault("Sweep",               "Load"),
            TuneHarmExpr          = kv.GetValueOrDefault("TuneHarm",            "1"),
            CompressionExpr       = kv.GetValueOrDefault("Compression",         "3"),
            GainTypeExpr          = kv.GetValueOrDefault("GainType",            "Gt"),
            PinStartExpr          = kv.GetValueOrDefault("PinStart",            "-20"),
            PinStepExpr           = kv.GetValueOrDefault("PinStep",             "1"),
            PinMaxExpr            = kv.GetValueOrDefault("PinMax",              "10"),
            TickleExpr            = kv.GetValueOrDefault("Tickle",              "-50"),
            MaxIterExpr           = kv.GetValueOrDefault("MaxIter",             "100"),
            FFTOverSampleExpr     = kv.GetValueOrDefault("FFTOverSample",       "1"),
            TolExpr               = kv.GetValueOrDefault("Tol",                 "1e-6"),
            DriveSteppingExpr     = kv.GetValueOrDefault("DriveStepping",       "IfNecessary"),
            GuardHarmonicExpr     = kv.GetValueOrDefault("GuardHarmonic",       "0"),
            EffTypeExpr           = kv.GetValueOrDefault("EffType",             "DE"),
            ZsourceOBOExpr        = kv.GetValueOrDefault("ZsourceOBO",          "5"),
            SearchMethodExpr      = kv.GetValueOrDefault("SearchMethod",         "SteepestAscent"),
            OutputGridPath        = outputGrid,
            Vswr1Expr             = kv.GetValueOrDefault("VSWR1",               "1.5"),
            Vswr1ResolutionExpr   = kv.GetValueOrDefault("VSWR1_resolution",    "4"),
            Vswr2Expr             = kv.GetValueOrDefault("VSWR2",               "3"),
            Vswr2ResolutionExpr   = kv.GetValueOrDefault("VSWR2_resolution",    "4"),
            KeepNonconvergingExpr        = kv.GetValueOrDefault("keepNonconvergingPoints", "false"),
            NonconvergentVswrExpr        = kv.GetValueOrDefault("nonconvergentVSWR",        "1.05"),
            CreateLoadpullResultExpr     = kv.GetValueOrDefault("CreateLoadpullResult",     "true"),
            LoadpullResultZsourceExpr    = kv.GetValueOrDefault("LoadpullResultZsource",    "MXE"),
            SourceDirectory              = sourceDirectory,
            Enabled                      = ParseEnabledToken(tokens),
        };
        return true;
    }

    /// Attempts to parse a "Name type=loadpull key=value ..." directive.
    /// Returns false if it is not a type=loadpull directive.
    /// </summary>
    private static bool TryParseLoadpullDirective(string rawLine, string? sourceDirectory,
        out CircuitRF.Core.Design.LoadpullAnalysis? result)
    {
        result = null;
        var tokens = TokeniseLine(rawLine);
        if (tokens.Count < 2) return false;

        string analysisName = tokens[0];

        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < tokens.Count; i++)
        {
            int eq = tokens[i].IndexOf('=');
            if (eq <= 0) continue;
            string key = tokens[i][..eq];
            string val = tokens[i][(eq + 1)..];
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                val = val[1..^1];
            kv[key] = val;
        }

        if (!kv.TryGetValue("type", out var typeVal) ||
            !typeVal.Equals("loadpull", StringComparison.OrdinalIgnoreCase))
            return false;

        // Resolve Grid path relative to the source directory (like SnP File= resolution).
        string gridPath = kv.GetValueOrDefault("Grid", "");
        if (!string.IsNullOrEmpty(gridPath) && sourceDirectory is not null &&
            !Path.IsPathRooted(gridPath))
            gridPath = Path.GetFullPath(Path.Combine(sourceDirectory, gridPath));

        result = new CircuitRF.Core.Design.LoadpullAnalysis(analysisName)
        {
            ToneExpr          = kv.GetValueOrDefault("Tone",           "0"),
            ToneUnit          = kv.GetValueOrDefault("ToneUnit",       "Hz"),
            MaxHarmonicExpr   = kv.GetValueOrDefault("MaxHarm",        "5"),
            LoadTunerName     = kv.GetValueOrDefault("LoadTuner",      ""),
            SourceTunerName   = kv.GetValueOrDefault("SourceTuner",    ""),
            SweepExpr         = kv.GetValueOrDefault("Sweep",          "Load"),
            TuneHarmExpr      = kv.GetValueOrDefault("TuneHarm",       "1"),
            GridPath          = gridPath,
            CompressionExpr   = kv.GetValueOrDefault("Compression",    "3"),
            GainTypeExpr      = kv.GetValueOrDefault("GainType",       "Gt"),
            PinStartExpr      = kv.GetValueOrDefault("PinStart",       "-20"),
            PinStepExpr       = kv.GetValueOrDefault("PinStep",        "1"),
            PinMaxExpr        = kv.GetValueOrDefault("PinMax",         "10"),
            TickleExpr        = kv.GetValueOrDefault("Tickle",         "-50"),
            MaxIterExpr       = kv.GetValueOrDefault("MaxIter",        "100"),
            FFTOverSampleExpr = kv.GetValueOrDefault("FFTOverSample",  "1"),
            TolExpr           = kv.GetValueOrDefault("Tol",            "1e-6"),
            DriveSteppingExpr = kv.GetValueOrDefault("DriveStepping", "IfNecessary"),
            GuardHarmonicExpr = kv.GetValueOrDefault("GuardHarmonic", "0"),
            SourceDirectory   = sourceDirectory,
            Enabled           = ParseEnabledToken(tokens),
        };
        return true;
    }

    // ── S-parameter analysis directive parser ─────────────────────────────────

    /// <summary>
    /// Attempts to parse a "Name type=sparam [log] start=expr stop=expr (step=expr | npts=N)" directive
    /// into a typed <see cref="SParameterAnalysis"/> with one sweep segment.
    /// Returns false if it is not a type=sparam directive.
    ///
    /// Supported key=value pairs:
    ///   start, stop          — expression strings (Hz) or value + optional unit token (GHz/MHz/kHz/Hz)
    ///   step                 — step-size expression (Hz) or value + optional unit token → StepSize mode
    ///   npts                 — integer point count → PointCount mode
    ///   log                  — bare keyword or log=true → SweepKind.Log
    ///
    /// When both step and npts are absent, defaults to step=1e8 (100 MHz) to match legacy behaviour.
    /// </summary>
    private static bool TryParseSParamDirective(string rawLine,
        out CircuitRF.Core.Design.SParameterAnalysis? result)
    {
        result = null;
        var tokens = TokeniseLine(rawLine);
        if (tokens.Count < 2) return false;

        string analysisName = tokens[0];

        // Collect key=value pairs and bare keywords.
        var kv   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bare = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < tokens.Count; i++)
        {
            int eq = tokens[i].IndexOf('=');
            if (eq > 0)
            {
                string key = tokens[i][..eq];
                string val = tokens[i][(eq + 1)..];
                // Strip surrounding quotes
                if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                    val = val[1..^1];
                kv[key] = val;
            }
            else
            {
                bare.Add(tokens[i]);
            }
        }

        if (!kv.TryGetValue("type", out var typeVal) ||
            !typeVal.Equals("sparam", StringComparison.OrdinalIgnoreCase))
            return false;

        // Resolve optional frequency-unit suffix: "1 GHz" → "1e9"
        // When a key's value is a plain number and the NEXT token is a unit keyword, consume it.
        // (Not applicable here because TokeniseLine splits on whitespace — the unit is a separate
        //  token. We handle this by re-scanning tokens after the key=value pairs.)
        // NormalizeFreqExpr handles old-style unquoted `start=1 GHz` for backward compat.
        // New-style `start="1" startUnit=GHz` gives startExpr="1" and startUnit from kv below.
        string startExpr = NormalizeFreqExpr(kv.GetValueOrDefault("start", "1e9"),
                                              tokens, kv.Keys, "start");
        string stopExpr  = NormalizeFreqExpr(kv.GetValueOrDefault("stop",  "10e9"),
                                              tokens, kv.Keys, "stop");

        // Read explicit unit keys (new format). Absent → "Hz" (back-compat with baked values).
        string startUnit = kv.GetValueOrDefault("startUnit", "Hz");
        string stopUnit  = kv.GetValueOrDefault("stopUnit",  "Hz");

        // Detect kind: bare "log" keyword OR log=true key.
        bool isLog = bare.Contains("log") ||
                     (kv.TryGetValue("log", out var logVal) &&
                      logVal.Equals("true", StringComparison.OrdinalIgnoreCase));
        var kind = isLog ? SweepKind.Log : SweepKind.Linear;

        // Detect mode: npts → PointCount; step → StepSize; default → StepSize 1e8.
        FrequencySpec freqSpec;
        if (kv.TryGetValue("npts", out var nptsStr) &&
            int.TryParse(nptsStr, out int npts) && npts >= 1)
        {
            freqSpec = new FrequencySpec(startExpr, stopExpr, npts, kind, startUnit, stopUnit);
        }
        else
        {
            string stepExpr = NormalizeFreqExpr(kv.GetValueOrDefault("step", "1e8"),
                                                 tokens, kv.Keys, "step");
            string stepUnit = kv.GetValueOrDefault("stepUnit", "Hz");
            freqSpec = new FrequencySpec(startExpr, stopExpr, stepExpr, kind, startUnit, stopUnit, stepUnit);
        }

        result = new CircuitRF.Core.Design.SParameterAnalysis(analysisName, freqSpec)
            { Enabled = ParseEnabledToken(tokens) };
        return true;
    }

    /// <summary>
    /// Normalizes a frequency expression string, applying a unit scale when the expression
    /// is a plain numeric literal followed by a unit token in the token list.
    ///
    /// E.g., tokens "start=1" followed by "GHz" → returns "1e9".
    /// If the expression already looks like "1e9" (scientific or no unit), returns it as-is.
    /// For non-literal expressions (e.g., "2*f0"), returns them unchanged.
    /// </summary>
    private static string NormalizeFreqExpr(string valStr,
        IReadOnlyList<string> tokens,
        IEnumerable<string>   usedKeys,
        string                keyName)
    {
        // Try direct numeric → check if the next non-key token after "keyName=valStr" is a unit
        double rawVal = 0;
        bool   isNum  = double.TryParse(valStr, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out rawVal);

        if (isNum)
        {
            // Look for a bare unit token immediately after "keyName=..." in the token stream.
            string? unitToken = FindUnitAfterKey(tokens, keyName, valStr);
            if (unitToken is null) return valStr;  // no unit → bare literal

            double scale = unitToken.ToUpperInvariant() switch
            {
                "GHZ" => 1e9,
                "MHZ" => 1e6,
                "KHZ" => 1e3,
                "HZ"  => 1.0,
                _     => 0.0,
            };
            if (scale == 0.0) return valStr;

            double hz = rawVal * scale;
            return hz.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Handle fused form: "1GHz", "500MHz", "1kHz", "500Hz".
        // Check longest suffix first so "kHz" wins over "Hz".
        foreach (var unit in new[] { "GHz", "MHz", "kHz", "Hz" })
        {
            if (!valStr.EndsWith(unit, System.StringComparison.OrdinalIgnoreCase)) continue;
            string numPart = valStr[..^unit.Length];
            if (numPart.Length == 0) continue;
            if (!double.TryParse(numPart, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out double fusedVal)) continue;
            double fusedScale = unit.ToUpperInvariant() switch
            {
                "GHZ" => 1e9,
                "MHZ" => 1e6,
                "KHZ" => 1e3,
                "HZ"  => 1.0,
                _     => 0.0,
            };
            if (fusedScale > 0)
                return (fusedVal * fusedScale).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        return valStr;  // non-literal expression — return as-is
    }

    private static readonly HashSet<string> _freqUnits =
        new(["GHz", "MHz", "kHz", "Hz"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scans <paramref name="tokens"/> for the token matching "key=val" and returns the
    /// immediately-following token if it is a frequency-unit keyword; otherwise null.
    /// Also handles split form: "key=", then val as a separate token, then unit.
    /// </summary>
    private static string? FindUnitAfterKey(IReadOnlyList<string> tokens,
                                             string key, string val)
    {
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            var tok = tokens[i];
            // Match "key=val" in one token
            if (tok.Equals($"{key}={val}", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count &&
                _freqUnits.Contains(tokens[i + 1]))
                return tokens[i + 1];
            // Match split: tok == "key=" and next == val and next+1 == unit
            if (tok.Equals($"{key}=", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count && tokens[i + 1] == val &&
                i + 2 < tokens.Count && _freqUnits.Contains(tokens[i + 2]))
                return tokens[i + 2];
        }
        return null;
    }

    // ── HB analysis directive parser ─────────────────────────────────────────

    /// <summary>
    /// Parses: "Name type=parametric_sweep Var=VarName (Values=v1,v2,… | Start=… Stop=… (Step=… | Npts=…) [log]) Inner=InnerName"
    /// </summary>
    private static bool TryParseParametricSweepDirective(string rawLine,
        out CircuitRF.Core.Design.ParametricSweepAnalysis? result)
    {
        result = null;
        var tokens = TokeniseLine(rawLine);
        if (tokens.Count < 2) return false;

        string analysisName = tokens[0];

        // Collect key=value pairs and bare keywords (mirrors TryParseSParamDirective).
        var kv   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bare = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < tokens.Count; i++)
        {
            int eq = tokens[i].IndexOf('=');
            if (eq > 0)
            {
                string key = tokens[i][..eq];
                string val = tokens[i][(eq + 1)..];
                if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                    val = val[1..^1];
                kv[key] = val;
            }
            else
            {
                bare.Add(tokens[i]);
            }
        }

        if (!kv.TryGetValue("type", out var typeVal) ||
            !typeVal.Equals("parametric_sweep", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!kv.TryGetValue("Var", out var varName) || string.IsNullOrEmpty(varName))
            throw new InvalidOperationException(
                $"Parametric sweep '{analysisName}': missing or empty Var= key.");

        if (!kv.TryGetValue("Inner", out var innerName) || string.IsNullOrEmpty(innerName))
            throw new InvalidOperationException(
                $"Parametric sweep '{analysisName}': missing or empty Inner= key.");

        // Determine which form is present: Values= (explicit list) or Start=+Stop= (spec form).
        bool hasValues    = kv.TryGetValue("Values", out var valuesStr) && !string.IsNullOrEmpty(valuesStr);
        bool hasStart     = kv.TryGetValue("Start",  out var startStr)  && !string.IsNullOrEmpty(startStr);
        bool hasStop      = kv.TryGetValue("Stop",   out var stopStr)   && !string.IsNullOrEmpty(stopStr);

        if (!hasValues && !(hasStart && hasStop))
            throw new InvalidOperationException(
                $"Parametric sweep '{analysisName}': needs Values= or Start=/Stop=.");

        if (hasValues)
        {
            // ── Explicit list form (backward-compat) ──────────────────────────
            double[] values;
            try
            {
                values = valuesStr!.Split(',')
                    .Select(s => double.Parse(s.Trim(),
                        System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Parametric sweep '{analysisName}': cannot parse Values='{valuesStr}': {ex.Message}");
            }

            if (values.Length == 0)
                throw new InvalidOperationException(
                    $"Parametric sweep '{analysisName}': Values= must contain at least one value.");

            result = new CircuitRF.Core.Design.ParametricSweepAnalysis(
                analysisName, varName, values, innerName);
        }
        else
        {
            // ── Start/Stop/Step|Npts spec form ────────────────────────────────
            if (!double.TryParse(startStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double start))
                throw new InvalidOperationException(
                    $"Parametric sweep '{analysisName}': cannot parse Start='{startStr}'.");
            if (!double.TryParse(stopStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double stop))
                throw new InvalidOperationException(
                    $"Parametric sweep '{analysisName}': cannot parse Stop='{stopStr}'.");

            bool isLog = bare.Contains("log") ||
                         (kv.TryGetValue("log", out var logVal) &&
                          logVal.Equals("true", StringComparison.OrdinalIgnoreCase));
            var kind = isLog ? CircuitRF.Core.Design.SweepKind.Log : CircuitRF.Core.Design.SweepKind.Linear;

            string unit = kv.TryGetValue("Unit", out var unitStr) ? (unitStr ?? "") : "";

            CircuitRF.Core.Design.SweepSpec spec;
            if (kv.TryGetValue("Npts", out var nptsStr) && !string.IsNullOrEmpty(nptsStr))
            {
                if (!int.TryParse(nptsStr, out int npts) || npts < 1)
                    throw new InvalidOperationException(
                        $"Parametric sweep '{analysisName}': Npts={nptsStr} must be a positive integer.");
                spec = new CircuitRF.Core.Design.SweepSpec(start, stop, npts,
                    CircuitRF.Core.Design.SweepAxisMode.PointCount, kind, unit);
            }
            else if (kv.TryGetValue("Step", out var stepStr) && !string.IsNullOrEmpty(stepStr))
            {
                if (!double.TryParse(stepStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double step))
                    throw new InvalidOperationException(
                        $"Parametric sweep '{analysisName}': cannot parse Step='{stepStr}'.");
                if (step <= 0)
                    throw new InvalidOperationException(
                        $"Parametric sweep '{analysisName}': Step= must be > 0 (got {step}).");
                spec = new CircuitRF.Core.Design.SweepSpec(start, stop, step,
                    CircuitRF.Core.Design.SweepAxisMode.StepSize, kind, unit);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Parametric sweep '{analysisName}': Start/Stop form requires Step= or Npts=.");
            }

            result = new CircuitRF.Core.Design.ParametricSweepAnalysis(
                analysisName, varName, spec, innerName);
        }

        result.Enabled = ParseEnabledToken(tokens);
        return true;
    }

    private static bool TryParseHbDirective(string rawLine,
        out CircuitRF.Core.Design.HarmonicBalanceAnalysis? result)
    {
        result = null;
        // Use TokeniseLine (quote-aware) so that Sweep="var: a .. b step s" stays as one token.
        var tokens = TokeniseLine(rawLine);
        if (tokens.Count < 2) return false;

        string analysisName = tokens[0];

        // Collect key=value pairs.
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < tokens.Count; i++)
        {
            int eq = tokens[i].IndexOf('=');
            if (eq <= 0) continue;
            string key = tokens[i][..eq];
            string val = tokens[i][(eq + 1)..];
            // Strip surrounding quotes from string values (e.g. Sweep="...").
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                val = val[1..^1];
            kv[key] = val;
        }

        if (!kv.TryGetValue("type", out var typeVal) ||
            !typeVal.Equals("hb", StringComparison.OrdinalIgnoreCase))
            return false;

        // Parse the Sweep string if present: "varName: start .. stop step s"
        // Sweep= on HB is deprecated — use ParametricSweepAnalysis instead.
        string?  sweepVar = null, sweepStart = null, sweepStop = null, sweepStep = null;
        if (kv.TryGetValue("Sweep", out var sweepStr))
        {
            Console.Error.WriteLine(
                $"[CnlReader] HB '{analysisName}': Sweep= is deprecated. " +
                $"Wrap this analysis in a parametric_sweep to drive sweeps. Sweep= is ignored by the engine.");
            ParseSweepString(sweepStr, out sweepVar, out sweepStart, out sweepStop, out sweepStep);
        }

        // Parse multi-tone fields: NumFreqs and Tone[1..N].
        string numFreqsExpr   = kv.GetValueOrDefault("NumFreqs",      "1");
        string maxMixOrderExpr = kv.GetValueOrDefault("MaxMixOrder",  "5");

        // Collect Tone[i] expressions in order (1-based in the directive, 0-based in the array).
        // Scan the kv dictionary for keys matching "Tone[N]".
        var toneExprs = new List<string>();
        for (int i = 1; ; i++)
        {
            if (kv.TryGetValue($"Tone[{i}]", out var tExpr))
                toneExprs.Add(tExpr);
            else
                break;  // stop at first gap
        }

        // Collect ToneUnit[i] parallel to toneExprs. Default "Hz" for any missing entry.
        var toneUnits = new List<string>();
        for (int i = 1; i <= toneExprs.Count; i++)
            toneUnits.Add(kv.GetValueOrDefault($"ToneUnit[{i}]", "Hz"));

        result = new CircuitRF.Core.Design.HarmonicBalanceAnalysis(analysisName)
        {
            ToneExpr          = kv.GetValueOrDefault("Tone",            "0"),
            ToneUnit          = kv.GetValueOrDefault("ToneUnit",        "Hz"),
            NumFreqsExpr      = numFreqsExpr,
            ToneExprs         = toneExprs.ToArray(),
            ToneUnits         = toneUnits.ToArray(),
            MaxMixOrderExpr   = maxMixOrderExpr,
            MaxHarmonicExpr   = kv.GetValueOrDefault("MaxHarm",         "7"),
            FFTOverSampleExpr = kv.GetValueOrDefault("FFTOverSample",    "1"),
            TolExpr           = kv.GetValueOrDefault("Tol",             "1e-6"),
            DriveSteppingExpr = kv.GetValueOrDefault("DriveStepping",   "IfNecessary"),
            GuardHarmonicExpr = kv.GetValueOrDefault("GuardHarmonic",   "0"),
            LambdaExpr        = kv.GetValueOrDefault("Lambda",          "1"),
            MaxIterExpr       = kv.GetValueOrDefault("MaxIter",         "100"),
            Enabled           = ParseEnabledToken(tokens),
#pragma warning disable CS0618
            SweepVarName      = sweepVar,
            SweepStartExpr    = sweepStart,
            SweepStopExpr     = sweepStop,
            SweepStepExpr     = sweepStep,
#pragma warning restore CS0618
        };
        return true;
    }

    // Parse "varName: start .. stop step s" → parts
    private static void ParseSweepString(string s,
        out string? varName, out string? start, out string? stop, out string? step)
    {
        varName = start = stop = step = null;
        // Split on ':' first to get variable name.
        int colon = s.IndexOf(':');
        if (colon < 0) return;
        varName = s[..colon].Trim();
        var rest = s[(colon + 1)..].Trim();
        // Split on ".." for start/stop.
        int dotDot = rest.IndexOf("..", StringComparison.Ordinal);
        if (dotDot < 0) return;
        start = rest[..dotDot].Trim();
        var afterDotDot = rest[(dotDot + 2)..].Trim();
        // Split on "step" (case-insensitive).
        int stepIdx = afterDotDot.IndexOf("step", StringComparison.OrdinalIgnoreCase);
        if (stepIdx < 0) { stop = afterDotDot; return; }
        stop = afterDotDot[..stepIdx].Trim();
        step = afterDotDot[(stepIdx + 4)..].Trim();
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

        // Legacy V:→Vdc remap for backward compatibility.
        if (typeName.Equals("V", StringComparison.OrdinalIgnoreCase))
            typeName = "Vdc";

        // SDD lines: delegate to the whitespace-aware SDD parser.
        if (typeName.Equals("SDD", StringComparison.OrdinalIgnoreCase))
        {
            ParseSddLine(line);
            return;
        }

        // Z_Port lines: expressions in Z[i,j] may contain spaces (if/then/endif) — dedicated parser.
        if (typeName.Equals("Z_Port", StringComparison.OrdinalIgnoreCase))
        {
            ParseZPortLine(line);
            return;
        }

        // Tuner lines: Z[k]=expr or G[k]=expr values may be complex; dedicated parser.
        if (typeName.Equals("Tuner", StringComparison.OrdinalIgnoreCase))
        {
            ParseTunerLine(line);
            return;
        }

        // V_1Tone / V_nTone: V= may reference complex expressions (e.g. Vs_mag) — standard whitespace
        // tokenizer is fine since the values are simple identifiers, but we handle them explicitly
        // so the Elaborator recognises them as parameterized types.

        bool isSnP = typeName.Equals("SnP", StringComparison.OrdinalIgnoreCase);

        // Tolerate whitespace around '=' in param assignments: "C = 1 uF", "C =1", "C= 1" all become
        // the canonical "C=1" token (the trailing unit stays its own token). Safe here because the
        // expression-bearing lines (Z_Port/SDD/Tuner) were already dispatched above, and quoted
        // values (SnP File="…") are atomic tokens from TokeniseLine.
        tokens = MergeSpacedAssignments(tokens);

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
                bool isStringLit = pexpr.Length >= 2 && pexpr[0] == '"';
                // Unit as a separate token: "C=1 uF" or "Pavl=Pin dBm" or "Vdc=-3.05 V".
                // IsRecognizedUnit covers both linear-scale units and identity/measurement
                // units (V, A, W, dBm, dB, …) that must not leak into the net list.
                if (!isStringLit && i + 1 < tokens.Count && Units.IsRecognizedUnit(tokens[i + 1]))
                {
                    unit = tokens[i + 1];
                    i++;
                }
                // Unit glued to the value: "C=1uF" → value "1", unit "uF". Guarded by IsKnown so a
                // bare number ("50"), scientific literal ("2e9"/"2e-9") or identifier ("Vs_mag")
                // is never split.
                else if (!isStringLit && TrySplitGluedUnit(pexpr, out var gv, out var gu))
                {
                    pexpr = gv;
                    unit  = gu;
                }
                overrides.Add(new ParameterAssignment(pname, pexpr, unit));
            }
            else
            {
                nets.Add(tok);
            }
            i++;
        }

        // Legacy V→Vdc param name normalization: if loaded as V: and has V=/Vac= but no Vdc=, rename.
        if (typeName.Equals("Vdc", StringComparison.OrdinalIgnoreCase) &&
            !overrides.Any(o => o.Name.Equals("Vdc", StringComparison.OrdinalIgnoreCase)))
        {
            for (int j = 0; j < overrides.Count; j++)
            {
                var o = overrides[j];
                if (o.Name.Equals("V", StringComparison.OrdinalIgnoreCase) ||
                    o.Name.Equals("Vac", StringComparison.OrdinalIgnoreCase))
                {
                    overrides[j] = new ParameterAssignment("Vdc", o.Expression, o.Unit);
                    break;
                }
            }
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
    /// Rejoins whitespace-separated assignment tokens so spaces around '=' are tolerated:
    /// ["C","=","1"] / ["C","=1"] / ["C=","1"] all collapse to ["C=1"]. A trailing unit ("uF")
    /// stays a separate token, and quoted values (already atomic from TokeniseLine) are never split.
    /// Applied only on the generic component path (expression lines use dedicated parsers).
    /// </summary>
    // Matches a numeric literal (incl. scientific) glued directly to a trailing alpha suffix:
    // "1uF" → ("1","uF"), "300pH" → ("300","pH"). "2e9"/"2e-9" leave no alpha tail → no match.
    private static readonly Regex GluedNumericUnit = new(
        @"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)([A-Za-z]+)$", RegexOptions.Compiled);

    /// <summary>
    /// Splits a value with a glued unit suffix ("1uF") into ("1","uF") — but only when the suffix
    /// is a known unit. Bare numbers, scientific literals, and identifiers return false (unchanged).
    /// </summary>
    private static bool TrySplitGluedUnit(string s, out string value, out string unit)
    {
        value = s; unit = "";
        var m = GluedNumericUnit.Match(s);
        if (!m.Success) return false;
        string u = m.Groups[2].Value;
        // IsRecognizedUnit covers identity/measurement units (V, A, W, dBm, …) in addition
        // to linear-scale units, so "48V" and "0dBm" split correctly. The regex already
        // requires a numeric head, so identifiers like "Pin" and "Vs_mag" never reach here.
        if (!Units.IsRecognizedUnit(u)) return false;
        value = m.Groups[1].Value;
        unit  = u;
        return true;
    }

    private static List<string> MergeSpacedAssignments(List<string> tokens)
    {
        var result = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];
            if (t == "=" && result.Count > 0 && i + 1 < tokens.Count)
                result[^1] += "=" + tokens[++i];            // "key = value"
            else if (t.Length > 1 && t[0] == '=' && result.Count > 0)
                result[^1] += t;                            // "key =value"
            else if (t.Length > 1 && t[^1] == '=' && i + 1 < tokens.Count)
                result.Add(t + tokens[++i]);                // "key= value"
            else
                result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// Given the right-hand side of "name = rhs", split into (expression, unit?).
    /// Strips inline comments (from the first bare ';' not inside quotes).
    /// The unit, if present, is the last token if it matches a known unit.
    /// </summary>
    private static (string Expression, string? Unit) SplitExprUnit(string rhs)
    {
        // Strip inline comment: first ';' not inside a quoted string.
        var stripped = StripInlineComment(rhs);
        var tokens = stripped.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return ("", null);
        if (tokens.Length == 1) return (tokens[0], null);
        var last = tokens[^1];
        if (Units.IsKnown(last))
            return (string.Join(" ", tokens[..^1]), last);
        return (stripped.Trim(), null);
    }

    /// <summary>
    /// Strips the remainder of the string from the first bare ';' (not inside quotes).
    /// </summary>
    private static string StripInlineComment(string s)
    {
        bool inString = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') { inString = !inString; continue; }
            if (!inString && s[i] == ';') return s[..i].TrimEnd();
        }
        return s;
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
