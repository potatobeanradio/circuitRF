using System.Globalization;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>What kind of value a PCell parameter carries.</summary>
public enum PCellValueKind
{
    Real,
    Int,
    Bool,
    String,
}

/// <summary>
/// One resolved PCell parameter value.
///
/// <para><b>Why parameters stopped being bare doubles (contract version 2).</b> Real cells need more
/// than numbers: a device cell names a model, a pad cell picks a shape, a spiral counts turns, and a
/// display mode is a word. Every one of those arrives as a string, an integer or a flag, and a
/// <c>double</c>-only contract forces each to be smuggled through as a number the generator then
/// decodes — which is a convention, not an interface, and every cell author invents a different
/// one.</para>
///
/// <para><b>Why this is its own type rather than the expression engine's <c>Value</c>.</b> The
/// vocabulary is deliberately the same — Real, Bool, String read exactly as <c>expressions.md</c>
/// spells them — but that type also carries Complex, a data cube, and an all-axis sentinel. None of
/// those is a thing a PCell parameter can be, and none of them can cross a process boundary to a
/// script host. This is the THIRD-PARTY-FACING surface, so every member of it has to be something a
/// cell can receive and something the wire can carry; anything else would be a member that exists
/// only to be rejected.</para>
///
/// <para><b>Int is separate from Real on purpose.</b> A finger count, a segment count and a turn
/// count are integers, and a generator that receives 3.0000000000000004 for one of them either
/// rounds — inventing a rule the caller cannot see — or produces geometry nobody asked for. Kept
/// apart, the caller's intent survives.</para>
/// </summary>
public readonly struct PCellValue : IEquatable<PCellValue>
{
    private readonly double  _number;
    private readonly string? _text;

    public PCellValueKind Kind { get; }

    private PCellValue(PCellValueKind kind, double number, string? text)
    {
        Kind    = kind;
        _number = number;
        _text   = text;
    }

    public static PCellValue Real(double v)   => new(PCellValueKind.Real, v, null);
    public static PCellValue Int(long v)      => new(PCellValueKind.Int, v, null);
    public static PCellValue Bool(bool v)     => new(PCellValueKind.Bool, v ? 1 : 0, null);
    public static PCellValue Text(string v)   => new(PCellValueKind.String, 0, v ?? "");

    public static implicit operator PCellValue(double v) => Real(v);
    public static implicit operator PCellValue(long v)   => Int(v);
    public static implicit operator PCellValue(int v)    => Int(v);
    public static implicit operator PCellValue(bool v)   => Bool(v);
    public static implicit operator PCellValue(string v) => Text(v);

    /// <summary>
    /// The value as a number. Int and Bool convert — a generator asking "how wide" does not care
    /// whether the caller wrote 3 or 3.0, and a flag read as 0/1 is the conventional reading. A
    /// String does NOT convert: parsing one here would silently accept a model name where a
    /// dimension was meant, which is the mistake this type exists to make impossible.
    /// </summary>
    public double AsReal(double fallback = 0.0) => Kind switch
    {
        PCellValueKind.Real => _number,
        PCellValueKind.Int  => _number,
        PCellValueKind.Bool => _number,
        _                   => fallback,
    };

    /// <summary>The value as an integer. A Real is TRUNCATED, never rounded — see the type's note on
    /// why Int is separate: a caller who meant an integer sent one.</summary>
    public long AsInt(long fallback = 0) => Kind switch
    {
        PCellValueKind.Int  => (long)_number,
        PCellValueKind.Real => (long)_number,
        PCellValueKind.Bool => (long)_number,
        _                   => fallback,
    };

    public bool AsBool(bool fallback = false) => Kind switch
    {
        PCellValueKind.Bool => _number != 0,
        PCellValueKind.Int  => _number != 0,
        PCellValueKind.Real => _number != 0,
        _                   => fallback,
    };

    /// <summary>
    /// The value as text. A number renders round-trippably, so a cache key built from it is stable
    /// and a value written out reads back as the same value.
    /// </summary>
    public string AsText(string fallback = "") => Kind switch
    {
        PCellValueKind.String => _text ?? fallback,
        PCellValueKind.Bool   => _number != 0 ? "true" : "false",
        PCellValueKind.Int    => ((long)_number).ToString(CultureInfo.InvariantCulture),
        _                     => _number.ToString("R", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Stable across runs and across processes, which is what the geometry cache and the generated-cell
    /// store key on. The KIND is part of it: <c>1</c> the integer and <c>1.0</c> the real are different
    /// inputs, and a cell that reads one of them as a count and the other as a length would otherwise
    /// share a cache entry.
    ///
    /// <para><b>A Real is written BARE, and that is load-bearing rather than cosmetic.</b> The
    /// generated-cell folder name is a hash over exactly these strings, and a placed instance's
    /// <c>CellRef</c> names that folder — so a change to this encoding renames every generated cell in
    /// every existing workspace while every instance still points at the old name. Writing a Real
    /// exactly as the pre-kinded code wrote a <c>double</c> keeps every existing hash byte-identical;
    /// only a value of some OTHER kind — which no existing workspace can contain, since no way to
    /// author one existed — takes the tagged form.</para>
    ///
    /// <para>The tagged forms cannot collide with each other or with a Real: a Real is digits, and a
    /// String carrying the text <c>Int:4</c> encodes as <c>String:Int:4</c>, not <c>Int:4</c>.</para>
    /// </summary>
    public override string ToString() => Kind switch
    {
        PCellValueKind.Real => _number.ToString("R", CultureInfo.InvariantCulture),
        _                   => $"{Kind}:{AsText()}",
    };

    public bool Equals(PCellValue other)
        => Kind == other.Kind
        && (Kind == PCellValueKind.String
                ? string.Equals(_text, other._text, StringComparison.Ordinal)
                : _number.Equals(other._number));

    public override bool Equals(object? obj) => obj is PCellValue v && Equals(v);

    public override int GetHashCode()
        => Kind == PCellValueKind.String
            ? HashCode.Combine(Kind, _text)
            : HashCode.Combine(Kind, _number);

    public static bool operator ==(PCellValue a, PCellValue b) => a.Equals(b);
    public static bool operator !=(PCellValue a, PCellValue b) => !a.Equals(b);
}

/// <summary>
/// Reading a PCell's parameters. These exist so a generator states its own defaults inline, which is
/// where a reader of that generator looks for them — the alternative is a declaration somewhere else
/// that drifts out of step with the code that uses it.
/// </summary>
public static class PCellParameters
{
    /// <summary>A length, an angle, an impedance — anything continuous.</summary>
    public static double Real(this IReadOnlyDictionary<string, PCellValue> p, string name, double fallback = 0.0)
        => p.TryGetValue(name, out var v) ? v.AsReal(fallback) : fallback;

    /// <summary>A count, an index, a mode selector.</summary>
    public static long Int(this IReadOnlyDictionary<string, PCellValue> p, string name, long fallback = 0)
        => p.TryGetValue(name, out var v) ? v.AsInt(fallback) : fallback;

    public static bool Bool(this IReadOnlyDictionary<string, PCellValue> p, string name, bool fallback = false)
        => p.TryGetValue(name, out var v) ? v.AsBool(fallback) : fallback;

    /// <summary>A model name, a mode word, a path.</summary>
    public static string Text(this IReadOnlyDictionary<string, PCellValue> p, string name, string fallback = "")
        => p.TryGetValue(name, out var v) ? v.AsText(fallback) : fallback;

    /// <summary>Builds a parameter set from plain numbers — the common case for a caller that has them.</summary>
    public static IReadOnlyDictionary<string, PCellValue> FromReals(IReadOnlyDictionary<string, double> reals)
    {
        var result = new Dictionary<string, PCellValue>(reals.Count, StringComparer.Ordinal);
        foreach (var (k, v) in reals) result[k] = PCellValue.Real(v);
        return result;
    }
}
