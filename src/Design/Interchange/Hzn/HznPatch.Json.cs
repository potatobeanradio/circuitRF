// HZN-5 R-hzn5-2 — the small JSON operations a save needs: a copy of a value that belongs to no tree yet, a comparison that
// ignores how a scalar was spelled, and a lookup of an array's element by its name.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Interchange.Hzn;

internal static class HznJsonOps
{
    /// <summary>A copy of <paramref name="value"/> with no parent, ready to be placed in another tree. Whitespace is not copied: a
    /// placed value is laid out in the style of the file it lands in.</summary>
    public static HznJsonValue Clone(HznJsonValue value)
    {
        switch (value)
        {
            case HznJsonScalar s:
                return s.Kind switch
                {
                    HznJsonKind.String => HznJsonScalar.String(s.StringValue ?? ""),
                    HznJsonKind.True => HznJsonScalar.Bool(true),
                    HznJsonKind.False => HznJsonScalar.Bool(false),
                    HznJsonKind.Null => HznJsonScalar.Null(),
                    _ => HznJsonScalar.FromToken(HznJsonKind.Number, s.RawText ?? "0", null),
                };
            case HznJsonObject o:
            {
                var copy = new HznJsonObject();
                foreach (var m in o.Members) copy.Set(m.Key, Clone(m.Value));
                return copy;
            }
            case HznJsonArray a:
            {
                var copy = new HznJsonArray();
                foreach (var item in a.Items) copy.Add(Clone(item));
                return copy;
            }
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    /// <summary>True when the two say the same thing: the same kind, and for a scalar the same value however it was spelled
    /// (<c>0.50</c> and <c>0.5</c> are the same number); an object compares by key, an array by position.</summary>
    public static bool Same(HznJsonValue? a, HznJsonValue? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Kind != b.Kind) return false;
        switch (a)
        {
            case HznJsonScalar x when b is HznJsonScalar y:
                if (x.Kind == HznJsonKind.String) return string.Equals(x.StringValue, y.StringValue, StringComparison.Ordinal);
                if (x.Kind == HznJsonKind.Number)
                    return x.TryGetDouble(out double dx) && y.TryGetDouble(out double dy) ? dx == dy : x.RawText == y.RawText;
                return true;
            case HznJsonObject x when b is HznJsonObject y:
                return x.Count == y.Count && x.Keys.All(k => y.ContainsKey(k) && Same(x[k], y[k]));
            case HznJsonArray x when b is HznJsonArray y:
                return x.Count == y.Count && x.Items.Zip(y.Items).All(p => Same(p.First, p.Second));
            default:
                return false;
        }
    }

    /// <summary>The element of <paramref name="array"/> whose <c>name</c> is <paramref name="name"/>.</summary>
    public static HznJsonObject? ByName(HznJsonArray? array, string name) =>
        array?.Items.OfType<HznJsonObject>().FirstOrDefault(o => string.Equals(o.GetString("name"), name, StringComparison.Ordinal));

    public static int IndexOfName(HznJsonArray array, string name)
    {
        for (int i = 0; i < array.Count; i++)
            if (array[i] is HznJsonObject o && string.Equals(o.GetString("name"), name, StringComparison.Ordinal)) return i;
        return -1;
    }

    public static IReadOnlyList<HznJsonObject> Objects(HznJsonArray? array) => array is null ? [] : [.. array.Items.OfType<HznJsonObject>()];

    /// <summary>The strings of an array of strings.</summary>
    public static IReadOnlyList<string> Strings(HznJsonArray? array) =>
        array is null ? [] : [.. array.Items.OfType<HznJsonScalar>().Select(s => s.StringValue).OfType<string>()];

    /// <summary>A value as a short piece of text, for a sentence.</summary>
    public static string Show(HznJsonValue? v) => v switch
    {
        null => "(absent)",
        HznJsonScalar { Kind: HznJsonKind.String } s => s.StringValue ?? "",
        HznJsonScalar s => s.RawText ?? s.Kind.ToString().ToLowerInvariant(),
        _ => v.Kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Every string scalar in <paramref name="v"/>, at any depth.</summary>
    public static IEnumerable<string> AllStrings(HznJsonValue v)
    {
        switch (v)
        {
            case HznJsonScalar { Kind: HznJsonKind.String } s: yield return s.StringValue ?? ""; break;
            case HznJsonObject o:
                foreach (var m in o.Members)
                    foreach (string t in AllStrings(m.Value)) yield return t;
                break;
            case HznJsonArray a:
                foreach (var item in a.Items)
                    foreach (string t in AllStrings(item)) yield return t;
                break;
        }
    }

    /// <summary>True when <paramref name="text"/> holds <paramref name="name"/> as a whole identifier (not as part of a longer one).</summary>
    public static bool NamesIdentifier(string text, string name)
    {
        if (name.Length == 0 || !text.Contains(name, StringComparison.Ordinal)) return false;
        return Regex.IsMatch(text, "(?<![A-Za-z0-9_])" + Regex.Escape(name) + "(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    }

    public static string Join(IEnumerable<string> items, string separator = ", ") => string.Join(separator, items);

    public static string Number(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static string Quoted(string name) => new StringBuilder("'").Append(name).Append('\'').ToString();
}
