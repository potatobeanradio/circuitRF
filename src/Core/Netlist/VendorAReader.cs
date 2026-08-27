using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using System.Text.RegularExpressions;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// Imports a VendorA-format netlist and emits the identical design-layer model
/// (Library + TestBench) that the .cnl reader produces.
///
/// No elaboration or engine code may know which reader produced the model.
///
/// VendorA format differences from .cnl (handled here):
///   - Continuation lines ending in '\' are joined.
///   - 'Options ...', '#load ...', 'Component Module=...' lines are skipped.
///   - 'S_Param:', 'SweepPlan:', 'OutputPlan:' → RawDirective("analysis", …)
///   - 'Short:' component → ShortModel (0 V voltage source)
///   - 'Mutual:' component → MutualInductanceModel
///   - Silent-ignore params: Noise=, SaveCurrent=, Mode=, Temp=
///   - Strip opt{…}, tune{…}, notune{…} annotations from values
///   - Vocabulary mapping: ExtrapMode constant→clamp, extrapolate stays
///   - Duplicate instance names within a scope are a hard error.
/// </summary>
public sealed class VendorAReader
{
    private readonly Library   _library  = new("vendorA");
    private TestBench?         _testBench;
    private Cell?              _currentCell;
    private int                _lineNumber;
    private string?            _sourceDirectory;

    // Track instance names per scope for duplicate detection
    private readonly HashSet<string> _topLevelNames   = new(StringComparer.Ordinal);
    private          HashSet<string> _currentCellNames = new(StringComparer.Ordinal);

    public (Library Library, TestBench TestBench) Read(string source,
        string testBenchName    = "tb",
        string? sourceDirectory = null)
    {
        _testBench       = new TestBench(testBenchName);
        _sourceDirectory = sourceDirectory;
        _lineNumber      = 0;

        var lines = JoinContinuationLines(source.Split('\n'));

        foreach (var rawLine in lines)
        {
            _lineNumber++;
            var line    = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
                continue;

            try
            {
                TryParseLine(trimmed);
            }
            catch (Exception ex) when (ex is not VendorAReadException)
            {
                throw new VendorAReadException(_lineNumber, trimmed, ex.Message, ex);
            }
        }

        return (_library, _testBench);
    }

    public static (Library Library, TestBench TestBench) ReadFile(
        string path, string testBenchName = "tb")
    {
        var fullPath  = Path.GetFullPath(path);
        var sourceDir = Path.GetDirectoryName(fullPath);
        return new VendorAReader().Read(
            File.ReadAllText(fullPath), testBenchName, sourceDir);
    }

    // ── Continuation-line joining ─────────────────────────────────────────────

    private static string[] JoinContinuationLines(string[] rawLines)
    {
        var result = new List<string>();
        var pending = new System.Text.StringBuilder();
        foreach (var line in rawLines)
        {
            var trimmedRight = line.TrimEnd('\r').TrimEnd();
            if (trimmedRight.EndsWith('\\'))
            {
                pending.Append(trimmedRight[..^1]); // strip trailing backslash
                pending.Append(' ');
            }
            else
            {
                if (pending.Length > 0)
                {
                    pending.Append(trimmedRight);
                    result.Add(pending.ToString());
                    pending.Clear();
                }
                else
                {
                    result.Add(line.TrimEnd('\r'));
                }
            }
        }
        if (pending.Length > 0) result.Add(pending.ToString());
        return result.ToArray();
    }

    // ── Line dispatch ─────────────────────────────────────────────────────────

    private void TryParseLine(string line)
    {
        // Skip tooling headers
        if (line.StartsWith("Options ",    StringComparison.Ordinal) ||
            line.StartsWith("Options\t",   StringComparison.Ordinal)) return;
        if (line.StartsWith("#",           StringComparison.Ordinal)) return;
        if (line.StartsWith("Component ",  StringComparison.Ordinal)) return;

        // Analysis/sweep directives → RawDirective
        if (IsAnalysisDirective(line))
        {
            var target = _currentCell is null ? _testBench! : ThrowDirectiveInCell(line);
            target.RawDirectives.Add(new RawDirective("analysis", line));
            return;
        }

        // Cell definition start
        if (line.StartsWith("define ", StringComparison.Ordinal))
        {
            ParseDefine(line);
            return;
        }

        // End of cell definition
        if (line.Equals("end", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("end ", StringComparison.OrdinalIgnoreCase))
        {
            if (_currentCell is null)
                throw new VendorAReadException(_lineNumber, line, "'end' without matching 'define'");
            _library.Cells.Add(_currentCell);
            _currentCell = null;
            _currentCellNames = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        // Parameter declarations inside a define block
        if (line.StartsWith("parameters ", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("parameters",       StringComparison.OrdinalIgnoreCase))
        {
            if (_currentCell is null)
                throw new VendorAReadException(_lineNumber, line, "'parameters' outside 'define' block");
            ParseParameterDeclarations(line["parameters".Length..].TrimStart());
            return;
        }

        // Variable assignment
        if (IsVariableAssignment(line))
        {
            ParseVariableAssignment(line);
            return;
        }

        // Instance line
        if (IsInstanceLine(line))
        {
            ParseInstanceLine(line);
            return;
        }

        // Unknown line: silently skip (real-world VendorA exports have comment-like lines)
    }

    private static bool IsAnalysisDirective(string line)
    {
        // S_Param:, SweepPlan:, OutputPlan: — all become raw analysis directives
        return line.StartsWith("S_Param:",   StringComparison.Ordinal) ||
               line.StartsWith("SweepPlan:", StringComparison.Ordinal) ||
               line.StartsWith("OutputPlan:", StringComparison.Ordinal);
    }

    // ── Define / End ──────────────────────────────────────────────────────────

    private void ParseDefine(string line)
    {
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
            cellName = rest.Trim();
        }

        _currentCell = new Cell(cellName);
        _currentCell.Ports.AddRange(ports);
        _currentCellNames = new HashSet<string>(StringComparer.Ordinal);
    }

    // ── Parameter declarations ────────────────────────────────────────────────

    private void ParseParameterDeclarations(string rest)
    {
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
            if (i + 1 < tokens.Count && Units.IsKnown(tokens[i + 1]))
            {
                unit = tokens[i + 1];
                i++;
            }
            _currentCell!.Parameters.Add(new ParameterDeclaration(name, expr, unit));
            i++;
        }
    }

    // ── Variable assignment ───────────────────────────────────────────────────

    private void ParseVariableAssignment(string line)
    {
        int eq = line.IndexOf('=');
        var name = line[..eq].Trim();
        var rest = line[(eq + 1)..].Trim();

        // Strip opt{…}, tune{…}, notune{…} annotations
        rest = StripAnnotations(rest);

        var (expr, unit) = SplitExprUnit(rest);
        var v = new Variable(name, expr, unit);
        if (_currentCell is not null)
            _currentCell.Variables.Add(v);
        else
            _testBench!.GlobalVariables.Add(v);
    }

    // ── Instance line ─────────────────────────────────────────────────────────

    private void ParseInstanceLine(string line)
    {
        var tokens = TokeniseLine(line);
        if (tokens.Count == 0) return;

        var typeAndName = tokens[0];
        int colon = typeAndName.IndexOf(':');
        if (colon < 0) return;

        var typeName     = typeAndName[..colon];
        var instanceName = typeAndName[(colon + 1)..];

        // Skip empty instance name (e.g. "SweepPlan: xxx" — already handled above)
        if (instanceName.Length == 0) return;

        // Validate for unsupported constructs loudly
        if (!IsSupportedType(typeName))
            throw new VendorAReadException(_lineNumber, line,
                $"unsupported VendorA construct: '{typeName}'");

        bool isSnP   = typeName.Equals("SnP",    StringComparison.OrdinalIgnoreCase);
        bool isMutual = typeName.Equals("Mutual", StringComparison.OrdinalIgnoreCase);

        var nets      = new List<string>();
        var overrides = new List<ParameterAssignment>();

        int i = 1;
        while (i < tokens.Count)
        {
            var tok = tokens[i];
            if (tok.Contains('='))
            {
                int eq    = tok.IndexOf('=');
                var pname = tok[..eq];
                var pexpr = tok[(eq + 1)..];

                // Silently ignore VendorA flags
                if (IsIgnoredParam(pname))
                    { i++; continue; }

                // SnP File path resolution (same as CnlReader)
                if (isSnP && pname.Equals("File", StringComparison.OrdinalIgnoreCase) &&
                    pexpr.Length >= 2 && pexpr[0] == '"' && pexpr[^1] == '"')
                {
                    var rawPath = pexpr[1..^1];
                    // Blank stays blank — see CnlReader's copy of this for why.
                    var resolved = _sourceDirectory is not null && !string.IsNullOrWhiteSpace(rawPath)
                        ? Path.GetFullPath(Path.Combine(_sourceDirectory, rawPath))
                        : rawPath;
                    pexpr = "\"" + resolved.Replace('\\', '/') + "\"";
                }

                // Strip opt{...}/tune{...} from parameter values
                pexpr = StripAnnotations(pexpr);

                // Map VendorA vocabulary to ours
                pexpr = MapVocabulary(pname, pexpr);

                string? unit = null;
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

        // SnP: validate NumPorts and reference-node
        string? refNetBinding = null;
        if (isSnP)
            ValidateSnpNets(nets, overrides, out refNetBinding);

        // Duplicate instance name check
        var nameSet = _currentCell is null ? _topLevelNames : _currentCellNames;
        if (!nameSet.Add(instanceName))
            throw new VendorAReadException(_lineNumber, line,
                $"duplicate instance name '{instanceName}'");

        var inst = new Instance(instanceName, typeName, nets, overrides)
                   { RefNetBinding = refNetBinding };

        if (_currentCell is not null)
            _currentCell.Instances.Add(inst);
        else
            _testBench!.Instances.Add(inst);
    }

    private static bool IsSupportedType(string typeName) =>
        typeName.Equals("R",      StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("C",      StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("L",      StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("Short",  StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("Port",   StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("Term",   StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("SnP",    StringComparison.OrdinalIgnoreCase) ||
        typeName.Equals("Mutual", StringComparison.OrdinalIgnoreCase) ||
        IsCellReference(typeName);

    // A cell reference is any type name not in the primitive set — resolved at elaboration.
    private static bool IsCellReference(string typeName) =>
        !typeName.Equals("R",      StringComparison.OrdinalIgnoreCase) &&
        !typeName.Equals("C",      StringComparison.OrdinalIgnoreCase) &&
        !typeName.Equals("L",      StringComparison.OrdinalIgnoreCase) &&
        !typeName.Equals("Short",  StringComparison.OrdinalIgnoreCase) &&
        !typeName.Equals("Port",   StringComparison.OrdinalIgnoreCase) &&
        !typeName.Equals("Term",   StringComparison.OrdinalIgnoreCase) &&
        !typeName.Equals("SnP",    StringComparison.OrdinalIgnoreCase) &&
        !typeName.Equals("Mutual", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredParam(string name) =>
        name.Equals("Noise",          StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SaveCurrent",     StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Mode",            StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Temp",            StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CheckPassivity",  StringComparison.OrdinalIgnoreCase);

    private static string MapVocabulary(string pname, string pexpr)
    {
        // ExtrapMode: VendorA uses "constant" for what we call "clamp"
        if (pname.Equals("ExtrapMode", StringComparison.OrdinalIgnoreCase) &&
            pexpr.Equals("\"constant\"", StringComparison.OrdinalIgnoreCase))
            return "\"clamp\"";
        return pexpr;
    }

    // ── SnP validation (shared logic with CnlReader) ──────────────────────────

    private void ValidateSnpNets(List<string> nets, List<ParameterAssignment> overrides,
        out string? refNetBinding)
    {
        refNetBinding = null;
        var np = overrides.FirstOrDefault(ov =>
            ov.Name.Equals("NumPorts", StringComparison.OrdinalIgnoreCase));
        if (np is null)
            throw new VendorAReadException(_lineNumber, "", "SnP: NumPorts required");
        if (!int.TryParse(np.Expression, out int numPorts) || numPorts < 1)
            throw new VendorAReadException(_lineNumber, "",
                $"SnP: NumPorts must be a positive integer, got '{np.Expression}'");

        var typeOv = overrides.FirstOrDefault(ov =>
            ov.Name.Equals("Type", StringComparison.OrdinalIgnoreCase));
        if (typeOv is not null)
        {
            var ts = typeOv.Expression.Trim('"');
            if (!ts.Equals("touchstone", StringComparison.OrdinalIgnoreCase))
                throw new VendorAReadException(_lineNumber, "",
                    $"SnP: Type must be \"touchstone\" in v1, got \"{ts}\"");
        }

        if      (nets.Count == numPorts)     { /* ground-referenced */ }
        else if (nets.Count == numPorts + 1) { refNetBinding = nets[numPorts]; nets.RemoveAt(numPorts); }
        else throw new VendorAReadException(_lineNumber, "",
            $"SnP: NumPorts={numPorts} but {nets.Count} nets");
    }

    // ── Tokenisation ──────────────────────────────────────────────────────────

    private static bool IsVariableAssignment(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return false;
        int colon = line.IndexOf(':');
        if (colon >= 0 && colon < eq) return false;
        var lhs = line[..eq].Trim();
        return IsIdentifier(lhs);
    }

    private static bool IsInstanceLine(string line)
    {
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

    // ── Annotation stripping ──────────────────────────────────────────────────

    // Remove opt{…}, tune{…}, notune{…} from a value string, keeping what's before them.
    private static readonly Regex AnnotationRx =
        new(@"\s+(opt|tune|notune)\{[^}]*\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string StripAnnotations(string value) =>
        AnnotationRx.Replace(value, "").Trim();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TestBench ThrowDirectiveInCell(string line)
        => throw new VendorAReadException(0, line,
            "Analysis directives cannot appear inside a 'define' block");
}

public sealed class VendorAReadException : Exception
{
    public int    LineNumber { get; }
    public string RawLine   { get; }

    public VendorAReadException(int lineNumber, string rawLine, string message, Exception? inner = null)
        : base($"Line {lineNumber}: {message} → \"{rawLine}\"", inner)
    {
        LineNumber = lineNumber;
        RawLine    = rawLine;
    }
}
