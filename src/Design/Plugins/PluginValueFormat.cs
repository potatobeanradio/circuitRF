using System.Globalization;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.Plugins;

/// <summary>A readout as it is shown: one line of text, or a table whose every cell is already formatted.</summary>
public sealed record PluginReadoutValue(string? Text, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<string>>? Rows)
{
    public static readonly PluginReadoutValue Empty = new("—", null, null);
    public bool IsTable => Columns is not null;
}

/// <summary>
/// Turns what a plugin returns in base SI into what a panel shows (<c>plugins.md</c> §5.2): a number with an SI prefix
/// and the unit, so a plugin never formats a number for display. A string is shown as the plugin wrote it.
/// </summary>
public static class PluginValueFormat
{
    private static readonly (string Prefix, double Scale)[] Prefixes =
        [("T", 1e12), ("G", 1e9), ("M", 1e6), ("k", 1e3), ("", 1), ("m", 1e-3), ("µ", 1e-6), ("n", 1e-9), ("p", 1e-12), ("f", 1e-15)];

    /// <summary><paramref name="v"/> in <paramref name="unit"/> with an SI prefix: <c>2.4 GHz</c>, <c>18.7 mm</c>,
    /// <c>3.3 nH</c>. A logarithmic or unitless value takes no prefix.</summary>
    public static string Number(double v, string? unit)
    {
        if (!double.IsFinite(v)) return "—";
        string u = Glyph(unit ?? "");
        bool prefixed = u.Length > 0 && unit is not ("dB" or "dBm" or "dBc" or "dBW" or "deg" or "%");
        if (!prefixed || v == 0) return Join(v.ToString("G4", CultureInfo.InvariantCulture), u);
        double a = Math.Abs(v);
        foreach (var (p, s) in Prefixes)
            if (a >= s * 0.9995) return Join((v / s).ToString("G4", CultureInfo.InvariantCulture), p + u);
        var (lp, ls) = Prefixes[^1];
        return Join((v / ls).ToString("G4", CultureInfo.InvariantCulture), lp + u);

        static string Join(string n, string u) => u.Length == 0 ? n : $"{n} {u}";
    }

    /// <summary>A unit as the editors show it.</summary>
    public static string Glyph(string unit) => unit switch { "Ohm" => "Ω", "metre" => "m", "deg" => "°", _ => unit };

    /// <summary>
    /// A value from a reply as text: a number in <paramref name="unit"/>; <c>{value, unit}</c> in its own unit; a
    /// quantity's <c>{text}</c> as written; a string as written; true/false; anything else as its JSON.
    /// </summary>
    public static string Text(JsonNode? value, string? unit)
    {
        switch (value)
        {
            case null: return "—";
            case JsonValue v when v.TryGetValue<double>(out double d): return Number(d, unit);
            case JsonValue v when v.TryGetValue<string>(out var s): return s;
            case JsonValue v when v.TryGetValue<bool>(out bool b): return b ? "true" : "false";
            case JsonObject o when o["text"] is JsonValue t && t.TryGetValue<string>(out var ts): return ts;
            case JsonObject o when o["value"] is JsonValue n && n.TryGetValue<double>(out double nv):
                return Number(nv, o["unit"] is JsonValue un && un.TryGetValue<string>(out var us) ? us : unit);
            default: return value.ToJsonString();
        }
    }

    /// <summary>
    /// A readout from a reply: a table when it carries <c>columns</c> and <c>rows</c> (<c>plugins.md</c> §5.2), each
    /// number formatted in its column's unit; otherwise one line.
    /// </summary>
    public static PluginReadoutValue Readout(JsonNode? value, string? unit)
    {
        if (value is JsonObject o && o["columns"] is JsonArray cols)
        {
            var units = o["units"] as JsonArray;
            var headers = cols.Select(c => Text(c, null)).ToList();
            var rows = new List<IReadOnlyList<string>>();
            if (o["rows"] is JsonArray ra)
                foreach (var r in ra)
                {
                    var cells = new List<string>();
                    if (r is JsonArray ca)
                        for (int i = 0; i < ca.Count; i++)
                        {
                            string? u = units is not null && i < units.Count && units[i] is JsonValue uv && uv.TryGetValue<string>(out var us) && us.Length > 0 ? us : null;
                            cells.Add(Text(ca[i], u));
                        }
                    rows.Add(cells);
                }
            return new PluginReadoutValue(null, headers, rows);
        }
        return new PluginReadoutValue(value is null ? "—" : Text(value, unit), null, null);
    }

    /// <summary>
    /// What a control shows for a value a reply <c>set</c>s: a quantity's number with its prefix and unit, so it reads
    /// back through the expression engine to the same value; anything else as <see cref="Text"/>.
    /// </summary>
    public static string ControlText(PluginParam p, JsonNode? value)
    {
        if (p.Kind == PluginParamKind.Quantity)
        {
            double? v = value switch
            {
                JsonValue jv when jv.TryGetValue<double>(out double d) => d,
                JsonObject o when o["value"] is JsonValue n && n.TryGetValue<double>(out double nv) => nv,
                _ => null,
            };
            if (value is JsonObject to && to["text"] is JsonValue t && t.TryGetValue<string>(out var ts)) return ts;
            if (v is { } x)
            {
                // Expression-engine spelling: 'u' for micro and the unit name, not the glyph.
                string shown = Number(x, p.Unit);
                return shown.Replace("µ", "u").Replace("Ω", "Ohm");
            }
        }
        if (p.Kind is PluginParamKind.Int or PluginParamKind.Real && value is JsonValue nvj && nvj.TryGetValue<double>(out double num))
            return num.ToString(p.Kind == PluginParamKind.Int ? "0" : "R", CultureInfo.InvariantCulture);
        return Text(value, null);
    }
}
