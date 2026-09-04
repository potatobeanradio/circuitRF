// The state machine half of the Gerber reader (brief-L4e §3-§8). Split from GerberReader.cs only for
// size; it is one class.
//
// R-L4e-4: modal state is not an optimization in this format, it IS the syntax. An omitted coordinate
// word inherits ("Y1506D01*" keeps the current X) and an omitted operation code repeats the last one
// ("X1092501*" is a whole legal block), and files exist in which the great majority of blocks are of
// that second form — so neither is an edge case to bolt on later.

using Clipper2Lib;

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class GerberReader
{
    private GerberReader(int dbuPerMicron) => _dbuPerMicron = dbuPerMicron;

    private readonly int _dbuPerMicron;

    // ── Declared format ───────────────────────────────────────────────────────
    private GerberUnit? _unit;
    private int _integerDigits = 3, _decimalDigits = 6;
    private GerberZeroOmission _zeroOmission = GerberZeroOmission.Leading;
    private GerberNotation _notation = GerberNotation.Absolute;
    private bool _formatDeclared;
    private GerberCoordinateFormat? _format;

    // ── Modal graphics state (R-L4e-4) ────────────────────────────────────────
    private long _x, _y;
    private long _offsetX, _offsetY;                  // %OF, applied at coordinate resolution
    private ApertureDef? _aperture;
    private Interpolation _mode = Interpolation.Linear;
    private bool _multiQuadrant = true;               // G75; G74 is the deprecated single-quadrant form
    private bool _clearPolarity;                      // %LPC
    private bool _regionMode;                         // G36 .. G37
    private int _lastOpCode;                          // R-L4e-4: a bare-coordinate block repeats this

    private enum Interpolation { Linear, ClockwiseArc, CounterClockwiseArc }

    // ── Accumulators ──────────────────────────────────────────────────────────
    private readonly Dictionary<int, ApertureDef> _apertures = [];
    private readonly Dictionary<string, GerberMacroDefinition> _macros = new(StringComparer.Ordinal);
    private readonly List<PaintedObject> _objects = [];
    private readonly Dictionary<string, string> _fileAttributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _apertureAttributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _objectAttributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _unknown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = [];

    private string? _imageName, _layerName, _refusal;
    private int _strokes, _flashes, _regions, _arcs;
    private int _stepRepeatFactor = 1;
    private bool _sawClearCommand;

    // Stroke buffer — consecutive D01s with the same aperture and no intervening D02 are ONE path
    // (R-L4e-10), which is precisely what L4c's writer emits for a round-capped PathShape.
    private readonly List<long> _strokeXy = [];
    private readonly List<LayoutEdge> _strokeEdges = [];
    private ApertureDef? _strokeAperture;
    private bool _strokeClear;
    private string? _strokeNet, _strokeFunction, _strokeComponent, _strokePin;

    // Region contours — the D01 sequence between G36 and G37; a D02 inside starts a NEW contour.
    private readonly List<Contour> _contours = [];
    private Contour? _contour;

    // %SR state (R-L4e-15): flattened on close, never mapped onto a LayoutInstance.
    private int _srStart = -1, _srNx = 1, _srNy = 1;
    private long _srDx, _srDy;

    private sealed class Contour
    {
        internal readonly List<long> Xy = [];
        internal readonly List<LayoutEdge> Edges = [];
    }

    private sealed class PaintedObject(LayoutShape shape, bool clear, string? function, string? component, string? pin)
    {
        internal LayoutShape Shape { get; } = shape;
        internal bool Clear { get; } = clear;
        internal string? Function { get; } = function;
        internal string? Component { get; } = component;
        internal string? Pin { get; } = pin;
    }

    private sealed class ApertureDef
    {
        internal int Code;
        internal bool IsPlainCircle;
        internal long CircleDiameterDbu;
        internal bool IsPlainRect;
        internal long RectWidthDbu, RectHeightDbu;
        /// <summary>Everything else — obround, regular polygon, macro, or any holed aperture — as
        /// pre-resolved outer/hole rings in DBU relative to the flash point (R-L4e-9's third row).</summary>
        internal List<(long[] Outer, List<long[]>? Holes)> Rings = [];
        /// <summary>The same geometry as Clipper paths, for a non-circular stroke's Minkowski sweep.</summary>
        internal Paths64 Paths = [];
        internal string? Function;
    }

    // ── Tokenizing ────────────────────────────────────────────────────────────

    private void Parse(string text)
    {
        int i = 0;
        while (i < text.Length && _refusal is null)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '%')
            {
                int end = text.IndexOf('%', i + 1);
                if (end < 0) { Count(_unknown, "unterminated % command"); break; }
                ExtendedCommand(text[(i + 1)..end]);
                i = end + 1;
                continue;
            }

            int star = text.IndexOf('*', i);
            if (star < 0) star = text.Length;
            WordBlock(text[i..star]);
            i = star + 1;
        }

        FlushStroke();
        CloseStepAndRepeat();
    }

    // ── Extended (%…%) commands ───────────────────────────────────────────────

    /// <summary>ONE <c>%…%</c> BLOCK MAY HOLD SEVERAL COMMANDS, each ended by its own <c>*</c> — the
    /// original RS-274X spelling, and still what several exporters emit. <c>%FSLAX45Y45*MOMM*%</c> is
    /// the whole of a real board's format declaration, and reading only the first segment loses the
    /// unit, which then reads as "this file declares no %MO*%" and refuses the entire file. The one
    /// command that legitimately consumes the remaining segments is <c>%AM</c>, whose primitives ARE
    /// <c>*</c>-separated blocks; it therefore ends the loop.</summary>
    private void ExtendedCommand(string body)
    {
        var segments = body.Split('*');
        for (int s = 0; s < segments.Length && _refusal is null; s++)
        {
            string head = Strip(segments[s]);
            if (head.Length == 0) continue;              // the empty tail after the block's final '*'
            if (head.Length < 2) { Count(_unknown, "empty % command"); continue; }
            if (head[..2] == "AM") { ParseMacro(head[2..], segments[s..]); return; }
            OneExtendedCommand(head[..2], head[2..]);
        }
    }

    private void OneExtendedCommand(string code, string rest)
    {
        switch (code)
        {
            case "FS": ParseFormatSpec(rest); return;
            case "MO": ParseMode(rest); return;
            case "AD": ParseApertureDefine(rest); return;

            case "LP":
                FlushStroke();
                _clearPolarity = rest.StartsWith('C');
                if (_clearPolarity) _sawClearCommand = true;
                return;

            case "LN": _layerName = Unescape(rest); return;
            case "IN": _imageName = Unescape(rest); return;

            case "IP":
                // R-L4e-14: %IPNEG inverts the whole image, which needs a bounding frame the file does
                // not supply. An inside-out layer that looks plausible is worse than an import that did
                // not happen — so this refuses BY NAME rather than being ignored.
                if (rest.Equals("NEG", StringComparison.OrdinalIgnoreCase))
                    _refusal = "This Gerber file declares %IPNEG*% (negative image), which inverts the " +
                               "whole image against a bounding frame the file does not supply. circuitRF " +
                               "cannot read it correctly, so nothing was imported.";
                return;

            // %IR<degrees> — image rotation, the deprecated whole-image transform. %IR0*% is the
            // identity and is what a file that emits the command at all almost always carries, so
            // counting it as unrecognized put one noise line on every file of a real board's set while
            // saying nothing. A non-zero rotation gets the same treatment as its siblings below.
            case "IR": RefuseUnlessIdentity(rest, "IR", "%IR*% (image rotation)", static r => IsNumber(r, 0)); return;

            case "MI": RefuseUnlessIdentity(rest, "MI", "%MI*% (mirror image)", static r => AbNumbersAllZero(r)); return;
            case "SF": RefuseUnlessIdentity(rest, "SF", "%SF*% (scale factor)", static r => AbNumbersAllOne(r)); return;
            case "AS": RefuseUnlessIdentity(rest, "AS", "%AS*% (axis select)",
                           static r => r.Length == 0 || r.Equals("AXBY", StringComparison.OrdinalIgnoreCase)); return;
            case "LM": RefuseUnlessIdentity(rest, "LM", "%LM*% (load mirroring)",
                           static r => r.Length == 0 || r.Equals("N", StringComparison.OrdinalIgnoreCase)); return;
            case "LR": RefuseUnlessIdentity(rest, "LR", "%LR*% (load rotation)", static r => IsNumber(r, 0)); return;
            case "LS": RefuseUnlessIdentity(rest, "LS", "%LS*% (load scaling)", static r => IsNumber(r, 1)); return;

            case "OF": ParseOffset(rest); return;
            case "SR": ParseStepAndRepeat(rest); return;

            case "AB":
                // R-L4e-15: a block aperture is the same decision as %SR — flatten, or refuse by name.
                // Not implemented, so it refuses by name rather than dropping the block's geometry.
                _refusal = "This Gerber file uses %AB*% block apertures, which circuitRF does not yet " +
                           "read. Nothing was imported.";
                return;

            case "TF": StoreAttribute(_fileAttributes, rest); return;
            case "TA": StoreAttribute(_apertureAttributes, rest); return;
            case "TO": StoreAttribute(_objectAttributes, rest); return;

            case "TD":
                // R-L4e-18: a BARE %TD*% deletes them ALL, not one. Treating it as a no-op leaves stale
                // nets and aperture functions attached to every subsequent object.
                if (rest.Length == 0) { _objectAttributes.Clear(); _apertureAttributes.Clear(); }
                else { _objectAttributes.Remove(rest); _apertureAttributes.Remove(rest); }
                return;

            default:
                Count(_unknown, "%" + code);
                return;
        }
    }

    private void RefuseUnlessIdentity(string rest, string code, string display, Func<string, bool> isIdentity)
    {
        if (isIdentity(rest)) return;
        // R-L4e-14: silently ignoring %MI yields a mirrored board that looks entirely plausible. Every
        // one of these deprecated transforms is refused by NAME rather than dropped.
        _refusal = $"This Gerber file uses {display} with a non-identity value ({code}{rest}), which " +
                   "circuitRF does not apply. Importing it would produce artwork that looks plausible " +
                   "and is wrong, so nothing was imported.";
    }

    private void ParseFormatSpec(string rest)
    {
        // %FS<zeroOmission><notation>X<i><d>Y<i><d>*% — parse it, never assume it (R-L4e-1).
        int i = 0;
        while (i < rest.Length && rest[i] is 'L' or 'T' or 'A' or 'I' or 'D')
        {
            if (rest[i] == 'L') _zeroOmission = GerberZeroOmission.Leading;
            else if (rest[i] == 'T') _zeroOmission = GerberZeroOmission.Trailing;
            else if (rest[i] == 'A') _notation = GerberNotation.Absolute;
            else if (rest[i] == 'I') _notation = GerberNotation.Incremental;
            i++;
        }
        for (; i < rest.Length; i++)
        {
            if (rest[i] is not ('X' or 'Y')) continue;
            if (i + 2 >= rest.Length) break;
            int intDigits = rest[i + 1] - '0', decDigits = rest[i + 2] - '0';
            if (intDigits is < 0 or > 9 || decDigits is < 0 or > 9) continue;
            _integerDigits = intDigits;
            _decimalDigits = decDigits;
            i += 2;
        }
        _formatDeclared = true;
        _format = null;
    }

    private void ParseMode(string rest)
    {
        if (rest.StartsWith("MM", StringComparison.OrdinalIgnoreCase)) _unit = GerberUnit.Millimetres;
        else if (rest.StartsWith("IN", StringComparison.OrdinalIgnoreCase)) _unit = GerberUnit.Inches;
        else { Count(_unknown, "%MO" + rest); return; }
        _format = null;
    }

    /// <summary>The resolved format, built lazily so <c>%FS</c> and <c>%MO</c> may arrive in either
    /// order (and so the deprecated <c>G70</c>/<c>G71</c> can set the unit after <c>%FS</c>).</summary>
    private GerberCoordinateFormat Format =>
        _format ??= new GerberCoordinateFormat(
            _unit ?? GerberUnit.Millimetres, _integerDigits, _decimalDigits, _zeroOmission, _notation, _dbuPerMicron);

    private void ParseOffset(string rest)
    {
        // %OFA<x>B<y>*% — a plain translation, so it is applied rather than refused (R-L4e-14's "apply
        // them if the implementation is trivial").
        (string? a, string? b) = SplitAb(rest);
        if (a is not null) _offsetX = Format.DecimalToDbu(a);
        if (b is not null) _offsetY = Format.DecimalToDbu(b);
    }

    private void ParseStepAndRepeat(string rest)
    {
        CloseStepAndRepeat();
        if (rest.Length == 0) return;   // %SR*% alone closes the open block

        int nx = 1, ny = 1;
        long dx = 0, dy = 0;
        foreach (var (letter, value) in SplitLetterValues(rest))
        {
            switch (letter)
            {
                case 'X': nx = (int)ParseIntOrDefault(value, 1); break;
                case 'Y': ny = (int)ParseIntOrDefault(value, 1); break;
                case 'I': dx = Format.DecimalToDbu(value); break;
                case 'J': dy = Format.DecimalToDbu(value); break;
            }
        }
        if (nx <= 1 && ny <= 1) return;

        FlushStroke();
        _srStart = _objects.Count;
        _srNx = Math.Max(1, nx);
        _srNy = Math.Max(1, ny);
        _srDx = dx;
        _srDy = dy;
    }

    /// <summary>R-L4e-15: flatten the repetitions and report the multiplication. Deliberately NOT a
    /// <see cref="LayoutInstance"/> — step-and-repeat is panelization ("this board appears six times on
    /// the manufacturing panel"), not hierarchy, and mapping it to hierarchy would oblige the writer to
    /// reproduce it, which L4c cannot do, breaking the round trip on the first cycle.</summary>
    private void CloseStepAndRepeat()
    {
        if (_srStart < 0) return;
        FlushStroke();

        int start = _srStart;
        _srStart = -1;
        int count = _objects.Count - start;
        if (count <= 0) return;

        var source = _objects.GetRange(start, count);
        for (int iy = 0; iy < _srNy; iy++)
        for (int ix = 0; ix < _srNx; ix++)
        {
            if (ix == 0 && iy == 0) continue;
            foreach (var obj in source)
            {
                var clone = LayoutGeometry.Clone(obj.Shape);
                LayoutGeometry.TranslateBy(clone, _srDx * ix, _srDy * iy);
                _objects.Add(new PaintedObject(clone, obj.Clear, obj.Function, obj.Component, obj.Pin));
            }
        }
        _stepRepeatFactor *= _srNx * _srNy;
        _diagnostics.Add($"%SR step-and-repeat multiplied {count} object(s) by {_srNx}x{_srNy}; the " +
                         "repetitions were flattened, not turned into instances.");
    }

    // ── Attributes (R-L4e-16/17/18) ───────────────────────────────────────────

    private static void StoreAttribute(Dictionary<string, string> into, string rest)
    {
        int comma = rest.IndexOf(',');
        string name = comma < 0 ? rest : rest[..comma];
        string value = comma < 0 ? "" : Unescape(rest[(comma + 1)..]);
        if (name.Length == 0) return;
        into[name] = value;
    }

    /// <summary>R-L4e-18: attribute values carry <c>\uXXXX</c> escapes for characters that would
    /// otherwise terminate the block — <c>*</c> is <c>*</c>. A component reference containing one
    /// arrives mangled if they are not undone.</summary>
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 5 < s.Length && (s[i + 1] == 'u' || s[i + 1] == 'U')
                && int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
            {
                sb.Append((char)cp);
                i += 5;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // ── Small text helpers ────────────────────────────────────────────────────

    private static string Strip(string s)
    {
        if (!s.Any(char.IsWhiteSpace)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    private static void Count(Dictionary<string, int> into, string key) =>
        into[key] = into.TryGetValue(key, out int n) ? n + 1 : 1;

    private static long ParseIntOrDefault(string s, long fallback) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;

    private static bool IsNumber(string s, double expected) =>
        s.Length == 0 ||
        (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v == expected);

    private static (string? A, string? B) SplitAb(string rest)
    {
        int a = rest.IndexOf('A'), b = rest.IndexOf('B');
        if (a < 0 && b < 0) return (null, null);
        if (a >= 0 && b > a) return (rest[(a + 1)..b], rest[(b + 1)..]);
        if (a >= 0 && b < 0) return (rest[(a + 1)..], null);
        if (b >= 0 && a < 0) return (null, rest[(b + 1)..]);
        return (rest[(a + 1)..], rest[(b + 1)..a]);
    }

    private static bool AbNumbersAllZero(string rest)
    {
        var (a, b) = SplitAb(rest);
        return IsNumber(a ?? "", 0) && IsNumber(b ?? "", 0);
    }

    private static bool AbNumbersAllOne(string rest)
    {
        var (a, b) = SplitAb(rest);
        return IsNumber(a ?? "", 1) && IsNumber(b ?? "", 1);
    }

    /// <summary>Splits a run like <c>X2Y3I5.0J4.0</c> into its letter/value pairs.</summary>
    private static List<(char Letter, string Value)> SplitLetterValues(string s)
    {
        var result = new List<(char, string)>();
        int i = 0;
        while (i < s.Length)
        {
            if (!char.IsAsciiLetter(s[i])) { i++; continue; }
            char letter = s[i++];
            int start = i;
            while (i < s.Length && !char.IsAsciiLetter(s[i])) i++;
            result.Add((letter, s[start..i]));
        }
        return result;
    }
}
