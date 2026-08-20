using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CircuitRF.Core.Matching;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// One row of the network pane's grid view (match.md §9.3): instance, type, value, unit — and the same
/// red/dimmed treatment the schematic view gives the same element.
/// </summary>
/// <remarks>
/// <b>There is no nearest-standard-value column</b>, on the owner's 2026-08-19 decision. What counts
/// as realizable depends on the flow: in an MMIC flow a capacitor is designed to its value and an
/// E-series is meaningless there.
/// </remarks>
public sealed record MatchElementRowViewModel(
    string Instance, string Name, ElementType Type, bool IsShunt, double Value,
    MatchElementRole Role, string ValueText, string Unit)
{
    /// <summary>"L" or "C", plus how it sits.</summary>
    public string TypeText => (Type == ElementType.L ? "L" : "C") + (IsShunt ? " shunt" : " series");

    /// <summary>The theme role, so the grid and the preview cannot disagree about an element.</summary>
    public string ColorRoleKey =>
        new MatchLadderElement(Name, Type, IsShunt, Value, Role, 0, 0, ValueText).ColorRoleKey;

    /// <summary>"absorbed" / "surplus" / "detune" / "" — the grid's own one-word annotation.</summary>
    public string Note => Role switch
    {
        MatchElementRole.Absorbed   => "absorbed",
        MatchElementRole.Excess     => "surplus",
        MatchElementRole.Detune     => "detune",
        MatchElementRole.OutOfRange => "out of range",
        _                           => "",
    };

    /// <summary>The clipboard form of a whole grid, as CSV.</summary>
    public static string ToCsv(IEnumerable<MatchElementRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.AppendLine("instance,type,orientation,value,unit,note");
        foreach (var r in rows)
            sb.Append(Csv(r.Instance)).Append(',')
              .Append(r.Type == ElementType.L ? 'L' : 'C').Append(',')
              .Append(r.IsShunt ? "shunt" : "series").Append(',')
              .Append(Csv(r.ValueText)).Append(',')
              .Append(Csv(r.Unit)).Append(',')
              .AppendLine(Csv(r.Note));
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.Contains(',', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal)
            ? '"' + s.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : s;
}
