// HZN-1 R-hzn1-1 — a JSON tree that writes back exactly what it read.
//
// ── WHY THIS IS NOT System.Text.Json ─────────────────────────────────────────────────────────────
//
// A `.hzn` is written back after circuitRF has changed part of it, and everything circuitRF did not
// change must come back byte for byte (brief-hzn-0 D4/D5). JsonNode keeps key order but re-spells
// every number (`0.50` → `0.5`), re-escapes strings by its own rules and throws the whitespace away;
// JsonDocument keeps each value's raw text but not the whitespace between tokens, so an edit inside
// it still needs a writer that knows the file's layout. This is therefore a CONCRETE syntax tree:
// every scalar keeps its token text, every container keeps the whitespace around each of its tokens,
// and writing the tree concatenates those pieces again. An untouched file is reproduced by
// construction, not by a formatter agreeing with whoever wrote it.
//
// What changes is written in the file's own style (HznJsonStyle): a new member copies the
// whitespace of its siblings, a new container is indented by the file's own unit at its depth, and a
// new object placed in an array beside other objects takes their key order. The record of this
// choice and of the alternatives is src/Design/RESOLVED.md §HZN.

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>The kind of one JSON value.</summary>
public enum HznJsonKind { Object, Array, String, Number, True, False, Null }

/// <summary>
/// How the file was written, read once from the file itself, so anything circuitRF adds looks like
/// the rest of it.
/// </summary>
public sealed class HznJsonStyle
{
    /// <summary>"\n" or "\r\n", whichever the file uses.</summary>
    public string Newline { get; init; } = "\n";

    /// <summary>True when members sit on their own lines.</summary>
    public bool Indented { get; init; } = true;

    /// <summary>One level of indentation, as the file writes it.</summary>
    public string IndentUnit { get; init; } = " ";

    /// <summary>The whitespace after a key's colon.</summary>
    public string AfterColon { get; init; } = " ";

    /// <summary>On a file with no line breaks, the whitespace after a comma.</summary>
    public string CompactSeparator { get; init; } = " ";

    /// <summary>
    /// True when a non-ASCII character is written as <c>\uXXXX</c>. Taken from the file when it holds
    /// any non-ASCII text; otherwise escaped, which every JSON reader accepts.
    /// </summary>
    public bool EscapeNonAscii { get; init; } = true;
}

/// <summary>A JSON value in a <see cref="HznJsonDocument"/>.</summary>
public abstract class HznJsonValue
{
    /// <summary>The object or array holding this value; null at the root and while detached.</summary>
    public HznJsonValue? Parent { get; internal set; }

    public abstract HznJsonKind Kind { get; }

    internal abstract void WriteTo(StringBuilder sb, HznJsonStyle style, int depth);

    /// <summary>True when this value was built in code and has never been written.</summary>
    internal abstract bool IsFresh { get; }
}

/// <summary>A string, number, <c>true</c>, <c>false</c> or <c>null</c>, keeping its token text.</summary>
public sealed class HznJsonScalar : HznJsonValue
{
    private readonly HznJsonKind _kind;
    private string? _raw;          // the token text, exactly as read; null for a fresh string
    private readonly string? _text; // a string's decoded value

    private HznJsonScalar(HznJsonKind kind, string? raw, string? text)
    {
        _kind = kind;
        _raw = raw;
        _text = text;
    }

    public override HznJsonKind Kind => _kind;

    internal override bool IsFresh => _raw is null;

    /// <summary>The token text as it is (or will be) written; null for a fresh string until written.</summary>
    public string? RawText => _raw;

    /// <summary>A string's value, unescaped; null for any other kind.</summary>
    public string? StringValue => _kind == HznJsonKind.String ? _text : null;

    /// <summary>A number's value; false for any other kind, or for a number a double cannot hold.</summary>
    public bool TryGetDouble(out double value)
    {
        value = 0;
        if (_kind != HznJsonKind.Number || _raw is null) return false;
        switch (_raw)
        {
            case "NaN": value = double.NaN; return true;
            case "Infinity": value = double.PositiveInfinity; return true;
            case "-Infinity": value = double.NegativeInfinity; return true;
        }
        return double.TryParse(_raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A number written as an integer.</summary>
    public bool TryGetInt64(out long value)
    {
        value = 0;
        return _kind == HznJsonKind.Number && _raw is not null
            && long.TryParse(_raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A bool's value; null for any other kind.</summary>
    public bool? BoolValue => _kind switch { HznJsonKind.True => true, HznJsonKind.False => false, _ => null };

    public static HznJsonScalar String(string value) => new(HznJsonKind.String, null, value);
    public static HznJsonScalar Bool(bool value) => value ? new(HznJsonKind.True, "true", null) : new(HznJsonKind.False, "false", null);
    public static HznJsonScalar Null() => new(HznJsonKind.Null, "null", null);
    public static HznJsonScalar Number(long value) => new(HznJsonKind.Number, value.ToString(CultureInfo.InvariantCulture), null);

    /// <summary>A double, spelled the way a Python writer spells one (<c>0.5</c>, <c>1.0</c>, <c>1e-05</c>).</summary>
    public static HznJsonScalar Number(double value) => new(HznJsonKind.Number, FormatDouble(value), null);

    internal static HznJsonScalar FromToken(HznJsonKind kind, string raw, string? text) => new(kind, raw, text);

    /// <summary>
    /// True when this scalar already says what <paramref name="other"/> says, so an assignment that
    /// changes nothing keeps the original spelling (<c>0.50</c> stays <c>0.50</c>, an escaped
    /// string stays escaped).
    /// </summary>
    internal bool SameValueAs(HznJsonScalar other)
    {
        if (_kind != other._kind) return false;
        return _kind switch
        {
            HznJsonKind.String => string.Equals(_text, other._text, StringComparison.Ordinal),
            HznJsonKind.Number => TryGetDouble(out double a) && other.TryGetDouble(out double b)
                                  ? a.Equals(b) && (a != 0 || double.IsNegative(a) == double.IsNegative(b))
                                  : string.Equals(_raw, other._raw, StringComparison.Ordinal),
            _ => true,
        };
    }

    internal override void WriteTo(StringBuilder sb, HznJsonStyle style, int depth)
    {
        _raw ??= Escape(_text ?? "", style.EscapeNonAscii);
        sb.Append(_raw);
    }

    internal static string FormatDouble(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        string s = value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
        if (s.IndexOfAny(['.', 'e']) < 0) s += ".0";
        return s;
    }

    internal static string Escape(string value, bool escapeNonAscii)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (escapeNonAscii && c > 0x7F))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>One key and its value, with the whitespace around each token. Null whitespace is laid
/// out in the file's style when written.</summary>
internal sealed class HznJsonMember
{
    public string? Leading;      // before the key
    public required string Key;
    public string? KeyRaw;       // the key's token text as read
    public string? BeforeColon;
    public string? AfterColon;
    public required HznJsonValue Value;
    public string? AfterValue;   // before the comma, or before '}' on the last member
}

/// <summary>One array element, with the whitespace around it.</summary>
internal sealed class HznJsonElement
{
    public string? Leading;
    public required HznJsonValue Value;
    public string? After;
}

/// <summary>A JSON object. Keys keep their order; a repeated key is kept, and reads see the last.</summary>
public sealed class HznJsonObject : HznJsonValue
{
    internal readonly List<HznJsonMember> Members = [];
    internal string? EmptyInner;  // the whitespace inside "{ }" when there are no members
    private readonly bool _parsed;

    public HznJsonObject() { }

    internal HznJsonObject(bool parsed) => _parsed = parsed;

    public override HznJsonKind Kind => HznJsonKind.Object;

    internal override bool IsFresh => !_parsed;

    public int Count => Members.Count;

    /// <summary>The keys, in file order.</summary>
    public IEnumerable<string> Keys => Members.Select(m => m.Key);

    public bool ContainsKey(string key) => Find(key) >= 0;

    public HznJsonValue? this[string key] => Find(key) is int i and >= 0 ? Members[i].Value : null;

    public string? GetString(string key) => (this[key] as HznJsonScalar)?.StringValue;
    public bool? GetBool(string key) => (this[key] as HznJsonScalar)?.BoolValue;
    public HznJsonObject? GetObject(string key) => this[key] as HznJsonObject;
    public HznJsonArray? GetArray(string key) => this[key] as HznJsonArray;

    /// <summary>True when the key is present with a JSON <c>null</c>.</summary>
    public bool IsNull(string key) => this[key]?.Kind == HznJsonKind.Null;

    public void SetString(string key, string? value) =>
        Set(key, value is null ? HznJsonScalar.Null() : HznJsonScalar.String(value));

    public void SetBool(string key, bool value) => Set(key, HznJsonScalar.Bool(value));

    /// <summary>
    /// Sets a key. A scalar that already says the same thing is left as written. A new key is placed
    /// where the sibling objects of this one put it, and takes its siblings' whitespace.
    /// </summary>
    public void Set(string key, HznJsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Parent is not null) throw new ArgumentException(nameof(value));

        int i = Find(key);
        if (i >= 0)
        {
            var member = Members[i];
            if (member.Value is HznJsonScalar old && value is HznJsonScalar neu && old.SameValueAs(neu)) return;
            member.Value.Parent = null;
            member.Value = value;
            value.Parent = this;
            return;
        }

        var added = new HznJsonMember { Key = key, Value = value };
        value.Parent = this;
        InsertMember(PositionFor(key), added);
    }

    /// <summary>Removes a key (every occurrence). False when it was not there.</summary>
    public bool Remove(string key)
    {
        bool any = false;
        for (int i = Members.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(Members[i].Key, key, StringComparison.Ordinal)) continue;
            RemoveMemberAt(i);
            any = true;
        }
        return any;
    }

    private int Find(string key)
    {
        for (int i = Members.Count - 1; i >= 0; i--)
            if (string.Equals(Members[i].Key, key, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Where a new key goes: after the nearest key that precedes it in a sibling object of
    /// the same array, else before the nearest key that follows it there, else at the end.</summary>
    private int PositionFor(string key)
    {
        if (SiblingKeyOrder(key) is not { } order) return Members.Count;
        int at = order.IndexOf(key);
        for (int k = at - 1; k >= 0; k--)
            if (Find(order[k]) is int i and >= 0) return i + 1;
        for (int k = at + 1; k < order.Count; k++)
            if (Find(order[k]) >= 0) return FirstIndexOf(order[k]);
        return Members.Count;
    }

    private int FirstIndexOf(string key) =>
        Members.FindIndex(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    private List<string>? SiblingKeyOrder(string key)
    {
        if (Parent is not HznJsonArray array) return null;
        foreach (var sibling in array.Items)
            if (!ReferenceEquals(sibling, this) && sibling is HznJsonObject o && o.ContainsKey(key) && !o.IsFresh)
                return [.. o.Keys];
        return null;
    }

    /// <summary>Orders a fresh object's keys as <paramref name="order"/> orders them; keys it does not
    /// name keep their place after those it does.</summary>
    internal void ReorderLike(IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        var sorted = Members.Select((m, i) => (m, i))
                            .OrderBy(t => rank.TryGetValue(t.m.Key, out int r) ? r : int.MaxValue)
                            .ThenBy(t => t.i)
                            .Select(t => t.m)
                            .ToList();
        Members.Clear();
        Members.AddRange(sorted);
    }

    private void InsertMember(int index, HznJsonMember added)
    {
        // An object that was empty, or is itself fresh, is laid out at write time from the style.
        var template = Members.FirstOrDefault(m => m.Leading is not null && m.AfterValue is not null);
        if (template is null) { Members.Insert(index, added); return; }

        // Whitespace a member that is neither first nor last carries. With one member there is no
        // such sample: a leading line break is the same anywhere, and null lays it out from the style.
        string notLastAfter = Members.Count > 1 ? Members[0].AfterValue ?? "" : "";
        string? innerLeading = Members.Count > 1 ? Members[1].Leading : HznJsonLayout.LineBreakOnly(Members[0].Leading);

        added.BeforeColon = template.BeforeColon;
        added.AfterColon = template.AfterColon;
        added.AfterValue = notLastAfter;
        added.Leading = innerLeading;
        if (index == Members.Count)
        {
            // The closing whitespace stays with whichever member is last.
            added.AfterValue = Members[^1].AfterValue;
            Members[^1].AfterValue = notLastAfter;
        }
        else if (index == 0)
        {
            // The new first member takes the old first one's leading whitespace.
            added.Leading = Members[0].Leading;
            Members[0].Leading = innerLeading;
        }
        Members.Insert(index, added);
    }

    private void RemoveMemberAt(int i)
    {
        var gone = Members[i];
        gone.Value.Parent = null;
        Members.RemoveAt(i);
        if (Members.Count == 0) { EmptyInner = ""; return; }
        if (i == Members.Count) Members[^1].AfterValue = gone.AfterValue;   // it was last
        if (i == 0) Members[0].Leading = gone.Leading;
    }

    internal override void WriteTo(StringBuilder sb, HznJsonStyle style, int depth)
    {
        sb.Append('{');
        if (Members.Count == 0)
        {
            sb.Append(EmptyInner ?? "");
            sb.Append('}');
            return;
        }
        for (int i = 0; i < Members.Count; i++)
        {
            var m = Members[i];
            bool last = i == Members.Count - 1;
            if (i > 0) sb.Append(',');
            sb.Append(m.Leading ?? HznJsonLayout.Leading(style, depth, i));
            sb.Append(m.KeyRaw ??= HznJsonScalar.Escape(m.Key, style.EscapeNonAscii));
            sb.Append(m.BeforeColon ?? "");
            sb.Append(':');
            sb.Append(m.AfterColon ?? style.AfterColon);
            m.Value.WriteTo(sb, style, depth + 1);
            sb.Append(m.AfterValue ?? (last ? HznJsonLayout.Closing(style, depth) : ""));
        }
        sb.Append('}');
    }
}

/// <summary>A JSON array.</summary>
public sealed class HznJsonArray : HznJsonValue
{
    internal readonly List<HznJsonElement> Elements = [];
    internal string? EmptyInner;
    private readonly bool _parsed;

    public HznJsonArray() { }

    internal HznJsonArray(bool parsed) => _parsed = parsed;

    public override HznJsonKind Kind => HznJsonKind.Array;

    internal override bool IsFresh => !_parsed;

    public int Count => Elements.Count;

    public HznJsonValue this[int index] => Elements[index].Value;

    public IEnumerable<HznJsonValue> Items => Elements.Select(e => e.Value);

    public void Add(HznJsonValue value) => Insert(Elements.Count, value);

    /// <summary>
    /// Inserts a value. A fresh object placed beside other objects takes their key order, and every
    /// new element takes its siblings' whitespace.
    /// </summary>
    public void Insert(int index, HznJsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Parent is not null) throw new ArgumentException(nameof(value));
        if (value is HznJsonObject { IsFresh: true } fresh
            && Items.OfType<HznJsonObject>().FirstOrDefault(o => !o.IsFresh) is { } sibling)
            fresh.ReorderLike([.. sibling.Keys]);

        value.Parent = this;
        var added = new HznJsonElement { Value = value };
        // An array that was empty, or is itself fresh, is laid out at write time from the style.
        if (Elements.Any(e => e.Leading is not null && e.After is not null))
        {
            string notLastAfter = Elements.Count > 1 ? Elements[0].After ?? "" : "";
            string? innerLeading = Elements.Count > 1 ? Elements[1].Leading : HznJsonLayout.LineBreakOnly(Elements[0].Leading);
            added.After = notLastAfter;
            added.Leading = innerLeading;
            if (index == Elements.Count)
            {
                added.After = Elements[^1].After;
                Elements[^1].After = notLastAfter;
            }
            else if (index == 0)
            {
                added.Leading = Elements[0].Leading;
                Elements[0].Leading = innerLeading;
            }
        }
        Elements.Insert(index, added);
    }

    public void RemoveAt(int index)
    {
        var gone = Elements[index];
        gone.Value.Parent = null;
        Elements.RemoveAt(index);
        if (Elements.Count == 0) { EmptyInner = ""; return; }
        if (index == Elements.Count) Elements[^1].After = gone.After;
        if (index == 0) Elements[0].Leading = gone.Leading;
    }

    internal override void WriteTo(StringBuilder sb, HznJsonStyle style, int depth)
    {
        sb.Append('[');
        if (Elements.Count == 0)
        {
            sb.Append(EmptyInner ?? "");
            sb.Append(']');
            return;
        }
        for (int i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            bool last = i == Elements.Count - 1;
            if (i > 0) sb.Append(',');
            sb.Append(e.Leading ?? HznJsonLayout.Leading(style, depth, i));
            e.Value.WriteTo(sb, style, depth + 1);
            sb.Append(e.After ?? (last ? HznJsonLayout.Closing(style, depth) : ""));
        }
        sb.Append(']');
    }
}

/// <summary>Whitespace for what was added, in the file's style.</summary>
internal static class HznJsonLayout
{
    /// <summary>Before the <paramref name="index"/>th item of a container at <paramref name="depth"/>.</summary>
    public static string Leading(HznJsonStyle style, int depth, int index) =>
        style.Indented ? style.Newline + Indent(style, depth + 1)
                       : index == 0 ? "" : style.CompactSeparator;

    /// <summary>Before the closing bracket of a container at <paramref name="depth"/>.</summary>
    public static string Closing(HznJsonStyle style, int depth) =>
        style.Indented ? style.Newline + Indent(style, depth) : "";

    /// <summary>A leading run that starts a new line is the same before any item; one that does not
    /// differs between the first item and the rest, so it is not copied.</summary>
    public static string? LineBreakOnly(string? leading) =>
        leading is not null && (leading.Contains('\n') || leading.Contains('\r')) ? leading : null;

    private static string Indent(HznJsonStyle style, int levels) =>
        levels <= 0 ? "" : string.Concat(Enumerable.Repeat(style.IndentUnit, levels));
}

/// <summary>Where a file stopped being JSON.</summary>
public sealed class HznJsonSyntaxException(string reason, int line, int column) : FormatException(reason)
{
    /// <summary>What was wrong, in a few words.</summary>
    public string Reason { get; } = reason;
    public int Line { get; } = line;
    public int Column { get; } = column;
}

/// <summary>
/// A whole JSON text: a byte-order mark if there was one, the whitespace before and after the root
/// value, and the root value.
/// </summary>
public sealed class HznJsonDocument
{
    private HznJsonDocument(bool bom, string leading, HznJsonValue root, string trailing, HznJsonStyle style)
    {
        HasByteOrderMark = bom;
        Leading = leading;
        Root = root;
        Trailing = trailing;
        Style = style;
    }

    public bool HasByteOrderMark { get; }
    public string Leading { get; }
    public HznJsonValue Root { get; }
    public string Trailing { get; }
    public HznJsonStyle Style { get; }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads UTF-8 bytes. Throws <see cref="DecoderFallbackException"/> on bytes that are
    /// not UTF-8 and <see cref="HznJsonSyntaxException"/> on text that is not JSON.</summary>
    public static HznJsonDocument Parse(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        return Parse(text, bom);
    }

    /// <summary>Reads JSON text.</summary>
    public static HznJsonDocument Parse(string text, bool byteOrderMark = false)
    {
        var parser = new HznJsonParser(text);
        string leading = parser.Whitespace();
        var root = parser.Value();
        string trailing = parser.Whitespace();
        parser.ExpectEnd();
        return new HznJsonDocument(byteOrderMark, leading, root, trailing, parser.Style());
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append(Leading);
        Root.WriteTo(sb, Style, 0);
        sb.Append(Trailing);
        return sb.ToString();
    }

    public byte[] ToBytes()
    {
        byte[] body = StrictUtf8.GetBytes(ToText());
        if (!HasByteOrderMark) return body;
        var all = new byte[body.Length + 3];
        all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF;
        body.CopyTo(all, 3);
        return all;
    }
}

/// <summary>A strict JSON reader (RFC 8259, plus the NaN/Infinity spellings a Python writer emits)
/// that keeps every token's text and every run of whitespace.</summary>
internal sealed class HznJsonParser(string text)
{
    private readonly string _s = text;
    private int _i;

    // What the style is read from.
    private string? _firstLineLeading;      // a depth-1 item's leading whitespace that holds a line break
    private string? _afterColon;
    private string? _compactSeparator;
    private bool _sawEscapedNonAscii, _sawRawNonAscii;

    public HznJsonStyle Style()
    {
        string newline = _s.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string? unit = null;
        if (_firstLineLeading is { } l)
        {
            int nl = l.LastIndexOfAny(['\n', '\r']);
            unit = l[(nl + 1)..];
        }
        return new HznJsonStyle
        {
            Newline = newline,
            Indented = _firstLineLeading is not null,
            IndentUnit = string.IsNullOrEmpty(unit) ? " " : unit,
            AfterColon = _afterColon ?? " ",
            CompactSeparator = _compactSeparator ?? " ",
            EscapeNonAscii = _sawEscapedNonAscii || !_sawRawNonAscii,
        };
    }

    public string Whitespace()
    {
        int start = _i;
        while (_i < _s.Length && _s[_i] is ' ' or '\t' or '\n' or '\r') _i++;
        return _s[start.._i];
    }

    public void ExpectEnd()
    {
        if (_i < _s.Length) Fail("text after the end of the JSON value");
    }

    public HznJsonValue Value() => Value(0);

    /// <summary>A real project nests about ten levels. The parser is recursive, and a file of opening brackets would otherwise end the process
    /// with a stack overflow, which no catch can stop.</summary>
    private const int MaxDepth = 128;

    private HznJsonValue Value(int depth)
    {
        if (depth > MaxDepth) Fail($"it is nested more than {MaxDepth} levels deep");
        if (_i >= _s.Length) Fail("unexpected end of file");
        char c = _s[_i];
        switch (c)
        {
            case '{': return Object(depth);
            case '[': return Array(depth);
            case '"':
            {
                var (raw, text) = StringToken();
                return HznJsonScalar.FromToken(HznJsonKind.String, raw, text);
            }
            case 't': Literal("true"); return HznJsonScalar.FromToken(HznJsonKind.True, "true", null);
            case 'f': Literal("false"); return HznJsonScalar.FromToken(HznJsonKind.False, "false", null);
            case 'n': Literal("null"); return HznJsonScalar.FromToken(HznJsonKind.Null, "null", null);
            case 'N': Literal("NaN"); return HznJsonScalar.FromToken(HznJsonKind.Number, "NaN", null);
            case 'I': Literal("Infinity"); return HznJsonScalar.FromToken(HznJsonKind.Number, "Infinity", null);
            default:
                if (c == '-' || c is >= '0' and <= '9')
                    return HznJsonScalar.FromToken(HznJsonKind.Number, NumberToken(), null);
                Fail($"unexpected character '{c}'");
                return null!;
        }
    }

    private HznJsonObject Object(int depth)
    {
        var o = new HznJsonObject(parsed: true);
        _i++; // {
        string ws = Whitespace();
        if (Peek() == '}') { _i++; o.EmptyInner = ws; return o; }

        while (true)
        {
            if (Peek() != '"') Fail("expected a key in quotes");
            var (keyRaw, key) = StringToken();
            string beforeColon = Whitespace();
            if (Peek() != ':') Fail("expected ':' after a key");
            _i++;
            string afterColon = Whitespace();
            _afterColon ??= afterColon;
            var value = Value(depth + 1);
            string afterValue = Whitespace();
            var member = new HznJsonMember
            {
                Leading = ws, Key = key, KeyRaw = keyRaw, BeforeColon = beforeColon,
                AfterColon = afterColon, Value = value, AfterValue = afterValue,
            };
            value.Parent = o;
            o.Members.Add(member);
            NoteLeading(ws, depth, o.Members.Count - 1);

            char next = Peek();
            if (next == '}') { _i++; return o; }
            if (next != ',') Fail("expected ',' or '}'");
            _i++;
            ws = Whitespace();
        }
    }

    private HznJsonArray Array(int depth)
    {
        var a = new HznJsonArray(parsed: true);
        _i++; // [
        string ws = Whitespace();
        if (Peek() == ']') { _i++; a.EmptyInner = ws; return a; }

        while (true)
        {
            var value = Value(depth + 1);
            string after = Whitespace();
            value.Parent = a;
            a.Elements.Add(new HznJsonElement { Leading = ws, Value = value, After = after });
            NoteLeading(ws, depth, a.Elements.Count - 1);

            char next = Peek();
            if (next == ']') { _i++; return a; }
            if (next != ',') Fail("expected ',' or ']'");
            _i++;
            ws = Whitespace();
        }
    }

    private void NoteLeading(string ws, int depth, int index)
    {
        bool breaks = ws.Contains('\n') || ws.Contains('\r');
        if (breaks && depth == 0) _firstLineLeading ??= ws;
        if (!breaks && index > 0) _compactSeparator ??= ws;
    }

    private char Peek() => _i < _s.Length ? _s[_i] : '\0';

    private void Literal(string word)
    {
        if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) Fail("unexpected word");
        _i += word.Length;
    }

    private string NumberToken()
    {
        int start = _i;
        if (Peek() == '-')
        {
            _i++;
            if (string.CompareOrdinal(_s, _i, "Infinity", 0, 8) == 0) { _i += 8; return _s[start.._i]; }
        }
        if (Peek() == '0') _i++;
        else if (Peek() is >= '1' and <= '9') Digits();
        else Fail("malformed number");
        if (Peek() == '.')
        {
            _i++;
            if (Peek() is not (>= '0' and <= '9')) Fail("malformed number");
            Digits();
        }
        if (Peek() is 'e' or 'E')
        {
            _i++;
            if (Peek() is '+' or '-') _i++;
            if (Peek() is not (>= '0' and <= '9')) Fail("malformed number");
            Digits();
        }
        return _s[start.._i];
    }

    private void Digits()
    {
        while (Peek() is >= '0' and <= '9') _i++;
    }

    private (string Raw, string Text) StringToken()
    {
        int start = _i;
        _i++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) Fail("unterminated string");
            char c = _s[_i];
            if (c == '"') { _i++; break; }
            if (c < 0x20) Fail("control character in a string");
            if (c != '\\')
            {
                if (c > 0x7F) _sawRawNonAscii = true;
                sb.Append(c);
                _i++;
                continue;
            }
            _i++;
            char e = Peek();
            _i++;
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    int code = 0;
                    if (_i + 4 > _s.Length
                        || !int.TryParse(_s.AsSpan(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                        Fail("malformed \\u escape");
                    if (code > 0x7F) _sawEscapedNonAscii = true;
                    sb.Append((char)code);
                    _i += 4;
                    break;
                default:
                    _i--;
                    Fail("unknown escape in a string");
                    break;
            }
        }
        return (_s[start.._i], sb.ToString());
    }

    private void Fail(string reason)
    {
        int line = 1, col = 1;
        for (int k = 0; k < _i && k < _s.Length; k++)
        {
            if (_s[k] == '\n') { line++; col = 1; }
            else col++;
        }
        throw new HznJsonSyntaxException(reason, line, col);
    }
}
