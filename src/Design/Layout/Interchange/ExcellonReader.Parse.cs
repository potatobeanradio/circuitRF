// The state machine half of the drill reader (brief-L4f §3). Split from ExcellonReader.cs only for
// size; it is one class.
//
// The format is line-oriented and modal in the same way Gerber is: a tool selection stands until the
// next one, and an omitted coordinate word inherits. What it adds is a SECOND mode that changes what
// a coordinate MEANS — in drill mode a coordinate is a hole, in rout mode with the tool down the same
// coordinate is a cut. R-L4f-7's failure (a slot read as two holes: two openings where the board has
// one) is exactly what happens when that second mode is ignored, and it looks entirely plausible in
// the viewer.

using System.Globalization;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class ExcellonReader
{
    private ExcellonReader(DrillFormatInference inference, int dbuPerMicron)
    {
        _inference = inference;
        _format = new GerberCoordinateFormat(
            inference.Unit, inference.IntegerDigits, inference.DecimalDigits,
            inference.ZeroOmission, GerberNotation.Absolute, dbuPerMicron);
        _coordinatesExact = inference.DecimalCoordinates || _format.IsExact;
    }

    private readonly DrillFormatInference _inference;
    private readonly GerberCoordinateFormat _format;

    // ── Modal state ───────────────────────────────────────────────────────────
    private long _x, _y;
    private bool _absolute = true;
    private bool _routeMode;      // G00 entered rout mode; G05/G81 returns to drill mode
    private bool _toolDown;       // M15 .. M16/M17 — the tool is cutting
    private int _currentTool;

    /// <summary>Whether an <c>M48</c> header is open. It decides whether a tool DEFINITION also
    /// SELECTS that tool — see <see cref="DefineTool"/>, which is where a whole file's holes were
    /// being lost.</summary>
    private bool _inHeader;

    // ── Accumulators ──────────────────────────────────────────────────────────
    private readonly Dictionary<int, DrillTool> _tools = [];
    private readonly List<int> _toolOrder = [];
    private readonly List<DrillHit> _hits = [];
    private readonly List<DrillSlot> _slots = [];
    private readonly List<long> _routeXy = [];
    private readonly Dictionary<string, int> _unknown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = [];

    private bool? _filePlated;          // ; #@! TF.FileFunction, or a ;TYPE= section outside any tool
    private bool? _sectionPlated;       // the ;TYPE=PLATED / ;TYPE=NON_PLATED section in force
    private bool _mixedTypeSections;    // the file carries both, so it has no single file-level plating
    private bool? _pendingToolPlated;   // ; #@! TA.AperFunction, applying to the NEXT tool
    private string? _pendingToolFunction;
    private string? _fileFunction;
    private DrillSpan? _span;
    private bool _coordinatesExact;
    private bool _toolDiametersExact = true;
    private string? _refusal;
    private int _routPositioningMoves;
    private DrillExtents _extents = DrillExtents.Empty;
    private DrillHit? _lastHit;

    // ── Line loop ─────────────────────────────────────────────────────────────

    private void Parse(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            if (_refusal is not null) return;
            ProcessLine(raw.Trim().TrimEnd('\r').Trim());
        }

        // A file that ends mid-cut still has an opening in it; emit what was routed rather than
        // dropping it silently.
        if (_toolDown) EndCut();
    }

    private void ProcessLine(string line)
    {
        if (line.Length == 0) return;

        if (line[0] == ';') { Comment(line); return; }
        if (line[0] == '%') { _inHeader = false; return; }   // end of the M48 header

        if (StartsWithWord(line, "INCH") || StartsWithWord(line, "METRIC")) return;  // read by the pre-scan
        if (StartsWithWord(line, "FMAT") || StartsWithWord(line, "VER") || StartsWithWord(line, "DETECT")) return;
        if (StartsWithWord(line, "ICI"))
        {
            // Incremental input, declared in the header rather than with G91. Same meaning, and
            // reading it as absolute is silently catastrophic in exactly the way R-L4e-3 describes.
            _absolute = !line.Contains("ON", StringComparison.OrdinalIgnoreCase);
            return;
        }
        if (line.Equals("LZ", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("TZ", StringComparison.OrdinalIgnoreCase)) return;           // read by the pre-scan

        var tokens = Tokenize(line);
        if (tokens.Count == 0) { Count(_unknown, Truncate(line)); return; }

        // A tool DEFINITION is the one line shape that must be recognized whole: T<n>C<diameter>,
        // in an M48 header or inline in the body — both forms are in use (R-L4f-4).
        if (tokens.Any(t => t.Letter == 'C') && tokens.Any(t => t.Letter == 'T'))
        {
            DefineTool(tokens);
            return;
        }

        InterpretMotion(tokens);
    }

    private static bool StartsWithWord(string line, string word) =>
        line.StartsWith(word, StringComparison.OrdinalIgnoreCase) &&
        (line.Length == word.Length || !char.IsAsciiLetter(line[word.Length]));

    // ── Comments, and the attributes hiding inside them (R-L4f-5, R-L4f-6, R-L4f-10) ──

    private void Comment(string line)
    {
        int marker = line.IndexOf("#@!", StringComparison.Ordinal);
        if (marker >= 0) { Attribute(line[(marker + 3)..].Trim()); return; }

        string body = line.TrimStart(';', ' ', '\t');
        if (body.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase))
        {
            string type = body[5..].Trim();
            bool? plated =
                type.Contains("NON", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("UNPLATED", StringComparison.OrdinalIgnoreCase) ? false :
                type.Contains("PLATED", StringComparison.OrdinalIgnoreCase) ? true : null;
            if (plated is not null)
            {
                // A file that carries BOTH sections has no single file-level plating, and saying it
                // has one (the first section's) would be a quiet lie to whatever reads it. The tools
                // still carry their own, which is where the distinction actually lives.
                if (_sectionPlated is { } previous && previous != plated) _mixedTypeSections = true;
                _sectionPlated = plated;
                if (_mixedTypeSections) _filePlated = null;
                else _filePlated ??= plated;
            }
        }
    }

    /// <summary>R-L4f-5's third spelling: X2 attributes smuggled through Excellon comments behind a
    /// <c>; #@!</c> marker. Parsed as ATTRIBUTES, not as comments, because they state three things
    /// that otherwise have to be inferred or cannot be recovered at all — the plating, the layer span
    /// (R-L4f-6), and what each tool is FOR (R-L4f-10).</summary>
    private void Attribute(string text)
    {
        var fields = text.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length == 0) return;
        string name = fields[0];

        if (name.EndsWith("FileFunction", StringComparison.OrdinalIgnoreCase))
        {
            _fileFunction = string.Join(',', fields.Skip(1));
            var (plated, from, to, kind) = ParseFunctionFields(fields);
            if (plated is not null) _filePlated ??= plated;
            if (from is not null && to is not null)
                _span = new DrillSpan(from.Value, to.Value, kind ?? "PTH", plated ?? _filePlated);
            else if (kind is not null)
                _span = new DrillSpan(0, 0, kind, plated ?? _filePlated);
        }
        else if (name.EndsWith("AperFunction", StringComparison.OrdinalIgnoreCase))
        {
            var (plated, _, _, _) = ParseFunctionFields(fields);
            _pendingToolPlated = plated;
            _pendingToolFunction = DrillFunctionField(fields);
        }
        else if (name.StartsWith("TD", StringComparison.OrdinalIgnoreCase))
        {
            _pendingToolPlated = null;
            _pendingToolFunction = null;
        }
    }

    private static (bool? Plated, int? From, int? To, string? Kind) ParseFunctionFields(string[] fields)
    {
        bool? plated = null;
        int? from = null, to = null;
        string? kind = null;

        foreach (string f in fields.Skip(1))
        {
            if (f.Equals("Plated", StringComparison.OrdinalIgnoreCase)) plated = true;
            else if (f.Equals("NonPlated", StringComparison.OrdinalIgnoreCase) ||
                     f.Equals("NPTH", StringComparison.OrdinalIgnoreCase)) plated ??= false;

            if (f.Equals("PTH", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("NPTH", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Blind", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Buried", StringComparison.OrdinalIgnoreCase)) kind = f;
            else if (int.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out int layer))
            {
                if (from is null) from = layer;
                else to ??= layer;
            }
        }
        return (plated, from, to, kind);
    }

    /// <summary>The one field of an <c>AperFunction</c> that says what the tool is FOR. Recognized by
    /// its own spelling rather than by position, because the standard set is short and the position
    /// varies with how much plating detail the writer chose to include.</summary>
    private static string? DrillFunctionField(string[] fields)
    {
        foreach (string f in fields.Skip(1))
            foreach (string known in KnownDrillFunctions)
                if (f.Equals(known, StringComparison.OrdinalIgnoreCase)) return known;
        return null;
    }

    internal static readonly string[] KnownDrillFunctions =
        ["ViaDrill", "ComponentDrill", "MechanicalDrill", "CastellatedDrill", "OtherDrill"];

    // ── Tools ─────────────────────────────────────────────────────────────────

    private void DefineTool(List<(char Letter, string Value)> tokens)
    {
        int number = 0;
        string diameterText = "";
        foreach (var (letter, value) in tokens)
        {
            if (letter == 'T') int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
            else if (letter == 'C') diameterText = value;
        }
        if (diameterText.Length == 0) return;

        // R-L4f-4: an inch diameter at <= 5 decimals and a millimetre one at <= 6 are exact; beyond
        // that this rounds AND SAYS SO, the same inversion of export's refuse-rather-than-round that
        // R-L4e-2 makes for coordinates.
        long diameter = _format.DecimalToDbu(diameterText, out bool exact);
        if (!exact)
        {
            _toolDiametersExact = false;
            _diagnostics.Add($"Tool T{number}'s diameter of {diameterText} is not a whole number of " +
                             "database units; it was rounded to the nearest one.");
        }

        var tool = new DrillTool(number, diameter, diameterText, exact,
            _pendingToolPlated ?? _sectionPlated ?? _filePlated, _pendingToolFunction);

        if (!_tools.ContainsKey(number)) _toolOrder.Add(number);
        _tools[number] = tool;
        _pendingToolPlated = null;
        _pendingToolFunction = null;

        // A TOOL DEFINITION IN THE BODY ALSO SELECTS THAT TOOL, and getting this wrong loses every
        // hole in the file. One dialect in circulation writes no M48 header at all and no separate
        // T<n> select line — the file opens with `%`, then `T1C.01378F095S3` and its coordinates
        // straight after, then `T2C.016…` and its own. Read as a definition ONLY, no tool is ever
        // current, and every hit is dropped as "hole with no tool selected": measured at 751 of 751
        // on one real board, counted but not imported.
        //
        // Gated on the header for the opposite case: a file whose M48 header defines T1..T4 up front
        // has not selected T4 by declaring it, and a body that then drills with no select at all is a
        // file we cannot read rather than one to guess the last tool for.
        if (!_inHeader) _currentTool = number;
    }

    private void SelectTool(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return;
        _currentTool = number;

        // An AperFunction attribute may sit against the tool's USE rather than its definition; take it
        // either way rather than losing a declaration to a placement difference (R-L4f-10: where it is
        // declared, the pairing must be a lookup).
        if ((_pendingToolFunction is not null || _pendingToolPlated is not null) &&
            _tools.TryGetValue(number, out var existing))
        {
            _tools[number] = existing with
            {
                Function = _pendingToolFunction ?? existing.Function,
                Plated = _pendingToolPlated ?? existing.Plated,
            };
            _pendingToolFunction = null;
            _pendingToolPlated = null;
        }
    }

    // ── Motion ────────────────────────────────────────────────────────────────

    private void InterpretMotion(List<(char Letter, string Value)> tokens)
    {
        long? px = null, py = null;
        bool sawCoordinate = false, canned = false, sawRepeat = false;
        long cannedStartX = 0, cannedStartY = 0;
        int repeatCount = 0;

        foreach (var (letter, value) in tokens)
        {
            switch (letter)
            {
                case 'T':
                    SelectTool(value);
                    break;

                case 'X':
                    px = CoordinateToDbu(value); sawCoordinate = true;
                    break;

                case 'Y':
                    py = CoordinateToDbu(value); sawCoordinate = true;
                    break;

                case 'R':
                    sawRepeat = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out repeatCount);
                    if (!sawRepeat) Count(_unknown, "R" + value);
                    break;

                case 'G':
                    // G85 is the canned slot: the coordinate BEFORE it is the start and the one after
                    // is the end, on one line. Read as two ordinary coordinate blocks it becomes two
                    // holes — R-L4f-7's specific, plausible-looking wrong answer.
                    if (value is "85")
                    {
                        (cannedStartX, cannedStartY) = Move(px, py);
                        canned = true;
                        px = py = null;
                        sawCoordinate = false;
                    }
                    else GCode(value);
                    break;

                case 'M':
                    MCode(value);
                    break;

                // Feeds, speeds, retract rates and step counts: real syntax, no geometry.
                case 'F' or 'S' or 'B' or 'H' or 'Z' or 'N':
                    break;

                default:
                    Count(_unknown, letter + value);
                    break;
            }
        }

        if (canned)
        {
            var (ex, ey) = Move(px, py);
            EmitSlot([cannedStartX, cannedStartY, ex, ey]);
            return;
        }

        if (sawRepeat)
        {
            Repeat(repeatCount, px ?? 0, py ?? 0);
            return;
        }

        if (sawCoordinate) MoveTo(px, py);
    }

    private long CoordinateToDbu(string word)
    {
        if (word.Contains('.', StringComparison.Ordinal))
        {
            long v = _format.DecimalToDbu(word, out bool exact);
            if (!exact) _coordinatesExact = false;
            return v;
        }
        return _format.ToDbu(_format.ParseCoordinateWord(word));
    }

    /// <summary>Resolves one coordinate block against the modal position and the absolute/incremental
    /// mode, and moves there. Returns the new position.</summary>
    private (long X, long Y) Move(long? px, long? py)
    {
        _x = _absolute ? px ?? _x : _x + (px ?? 0);
        _y = _absolute ? py ?? _y : _y + (py ?? 0);
        return (_x, _y);
    }

    private void MoveTo(long? px, long? py)
    {
        var (x, y) = Move(px, py);

        if (_toolDown) { _routeXy.Add(x); _routeXy.Add(y); return; }
        if (_routeMode) { _routPositioningMoves++; return; }
        EmitHit(x, y);
    }

    private void GCode(string value)
    {
        switch (value)
        {
            case "00" or "0":
                _routeMode = true;
                break;
            case "01" or "1":
                break;                       // linear; the coordinates on the same line do the work
            case "02" or "2" or "03" or "3":
                // A circular routed segment. Its endpoint is kept so the opening keeps its shape and
                // its length; the curvature is not, and that is reported rather than assumed harmless.
                Count(_skipped, "circular routed segment (read as a straight segment)");
                break;
            case "05" or "5" or "81":
                _routeMode = false;
                break;
            case "90":
                _absolute = true;
                break;
            case "91":
                _absolute = false;
                break;
            case "04" or "4":
                break;                       // dwell
            case "93":
                Count(_skipped, "G93 zero set");
                break;
            default:
                Count(_unknown, "G" + value);
                break;
        }
    }

    private void MCode(string value)
    {
        switch (value)
        {
            case "48": _inHeader = true; break;
            case "95": _inHeader = false; break;
            case "15": StartCut(); break;
            case "16" or "17": EndCut(); break;
            case "71" or "72": break;        // units, read by the pre-scan
            case "30" or "00" or "0": break; // end of file / end of program
            case "47" or "97" or "98" or "99" or "45": break; // operator messages and canned text
            default: Count(_unknown, "M" + value); break;
        }
    }

    private void StartCut()
    {
        _toolDown = true;
        _routeXy.Clear();
        _routeXy.Add(_x);
        _routeXy.Add(_y);
    }

    private void EndCut()
    {
        _toolDown = false;
        _routeMode = false;
        if (_routeXy.Count >= 4) EmitSlot([.. _routeXy]);
        else Count(_skipped, "routed pass that cut nothing");
        _routeXy.Clear();
    }

    /// <summary>The repeat form: <c>R&lt;n&gt;</c> with a step, repeating the LAST hit n more times.
    /// A repeat with no preceding hit is meaningless and is counted rather than guessed at.</summary>
    private void Repeat(int count, long stepX, long stepY)
    {
        if (_lastHit is null || count <= 0) { Count(_skipped, "R repeat with no preceding hit"); return; }

        long x = _lastHit.X, y = _lastHit.Y;
        for (int i = 0; i < count; i++)
        {
            x += stepX;
            y += stepY;
            EmitHit(x, y);
        }
        _x = x;
        _y = y;
    }

    private void EmitHit(long x, long y)
    {
        if (_tools.TryGetValue(_currentTool, out var tool))
        {
            if (_hits.Count >= HitHardCeiling)
            {
                _refusal = $"This drill file contains more than {HitHardCeiling:N0} hits, over circuitRF's " +
                           "import ceiling. Nothing was imported.";
                return;
            }
            var hit = new DrillHit(x, y, tool.Number, tool.DiameterDbu, tool.Plated, tool.Function);
            _hits.Add(hit);
            _lastHit = hit;
            _extents = _extents.Include(x, y);
        }
        else Count(_skipped, "hole with no tool selected");
    }

    private void EmitSlot(long[] xy)
    {
        if (!_tools.TryGetValue(_currentTool, out var tool))
        {
            Count(_skipped, "routed slot with no tool selected");
            return;
        }

        _slots.Add(new DrillSlot(xy, tool.Number, tool.DiameterDbu, tool.Plated, tool.Function));
        for (int i = 0; i + 1 < xy.Length; i += 2) _extents = _extents.Include(xy[i], xy[i + 1]);
    }

    private static void Count(Dictionary<string, int> counts, string name) =>
        counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;

    private static string Truncate(string line) => line.Length <= 24 ? line : line[..24] + "...";

    // ── Result ────────────────────────────────────────────────────────────────

    private ExcellonReadResult Build()
    {
        if (_refusal is not null)
            return new ExcellonReadResult { Refusal = _refusal, Format = _inference };

        if (_tools.Count == 0 && _hits.Count == 0 && _slots.Count == 0)
            return new ExcellonReadResult
            {
                Refusal = "This file has an Excellon shape but defines no tools and drills no holes — " +
                          "nothing was imported. If it is a drill listing or report, the drill file " +
                          "itself is a separate file, usually with the same name.",
                Format = _inference,
            };

        var diagnostics = new List<string>(_diagnostics);
        diagnostics.AddRange(_inference.Evidence);

        if (_inference.RequiredAGuess)
            diagnostics.Add("This drill file does not fully declare what its numbers mean, so part of " +
                            "the format above was INFERRED. Check the hole positions against the artwork.");

        if (_span is null)
            diagnostics.Add("No layer span was declared, so these holes are assumed to go through the " +
                            "whole board. A set with blind or buried vias declares a span per drill file.");
        else if (!_span.IsThroughHole)
            diagnostics.Add($"These holes span copper layers {_span.FromLayer} to {_span.ToLayer} " +
                            $"({_span.Kind}) — they do not go through the whole board.");

        if (_filePlated is not null || _tools.Values.Any(t => t.Plated is not null))
            diagnostics.Add("A plated / non-plated distinction was read from this file. circuitRF's " +
                            "Gerber export writes a single plated drill file, so the distinction " +
                            "survives into the design and is flattened again on export.");

        if (_routPositioningMoves > 0)
            diagnostics.Add($"{_routPositioningMoves} rout positioning move(s) drilled nothing, as intended.");

        return new ExcellonReadResult
        {
            Tools = [.. _toolOrder.Select(n => _tools[n])],
            Hits = _hits,
            Slots = _slots,
            Format = _inference,
            Plated = _filePlated,
            Span = _span,
            FileFunction = _fileFunction,
            CoordinatesExact = _coordinatesExact,
            WorstCaseRoundingErrorDbu = _coordinatesExact ? 0.0 : 0.5,
            ToolDiametersExact = _toolDiametersExact,
            Extents = _extents,
            UnknownCommandCounts = _unknown,
            SkippedConstructCounts = _skipped,
            Diagnostics = diagnostics,
        };
    }
}
