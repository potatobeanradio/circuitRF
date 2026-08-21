using System;
using System.Collections.Generic;
using System.Text;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// One row of the network pane's grid view (match.md §9.3): instance, type, value, unit — and the same
/// red/dimmed treatment the schematic view gives the same element.
/// </summary>
/// <remarks>
/// <b>There is no nearest-standard-value column</b>, on the owner's 2026-08-19 decision. What counts
/// as realizable depends on the flow: in an MMIC flow a capacitor is designed to its value and an
/// E-series is meaningless there.
///
/// <h3>An observable row that is REUSED, not a record that is replaced</h3>
/// <para>This was a <c>record</c> and the grid rebuilt the whole collection on every refresh, which
/// was fine while the value column was a <c>TextBlock</c>. It is an editor now (owner, 2026-08-20:
/// <i>"give the component values in the grid view the same inline text editor option as what we use
/// for the schematic"</i>), and an editor cannot live in a container the model destroys underneath it:
/// committing a value rebuilds the ladder, which would have thrown away the very
/// <c>InlineEditText</c> that was mid-commit. So the rows persist and their contents are updated in
/// place — the same shape <see cref="MatchTransformRowViewModel"/> already has, for the same reason.
/// </para>
/// </remarks>
public sealed partial class MatchElementRowViewModel : ObservableObject
{
    private readonly MatchDesignerViewModel _owner;

    internal MatchElementRowViewModel(MatchDesignerViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <summary>Overwrites this row from one ladder element.</summary>
    internal void Update(string instance, string name, ElementType type, bool isShunt, double value,
                         MatchElementRole role, string valueText, string unit)
    {
        Instance  = instance;
        Name      = name;
        Type      = type;
        IsShunt   = isShunt;
        Value     = value;
        Role      = role;
        ValueText = valueText;
        Unit      = unit;

        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(ValueWithUnit));
        OnPropertyChanged(nameof(ValueEntry));
        OnPropertyChanged(nameof(ColorRoleKey));
        OnPropertyChanged(nameof(Note));
    }

    /// <summary>The instance name as the schematic labels it — "L1", never "MN1.L1".</summary>
    [ObservableProperty] private string _instance = "";

    /// <summary>The element's ladder name — the key every stored transform resolves through.</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>L or C.</summary>
    [ObservableProperty] private ElementType _type;

    /// <summary>True for a shunt arm.</summary>
    [ObservableProperty] private bool _isShunt;

    /// <summary>The value in SI base units — what the grid SORTS on.</summary>
    [ObservableProperty] private double _value;

    /// <summary>Absorbed, surplus, detune, out-of-range, or ordinary.</summary>
    [ObservableProperty] private MatchElementRole _role;

    /// <summary>The value as rendered, without its unit.</summary>
    [ObservableProperty] private string _valueText = "";

    /// <summary>The rendered unit.</summary>
    [ObservableProperty] private string _unit = "";

    /// <summary>"L" or "C", plus how it sits.</summary>
    public string TypeText => (Type == ElementType.L ? "L" : "C") + (IsShunt ? " shunt" : " series");

    /// <summary>
    /// The value and its unit as one string — "112 pH" — which is how the grid shows it.
    /// </summary>
    /// <remarks>
    /// <b>One column, not two</b> (owner, 2026-08-20: "merge the Value and Unit columns in the grid
    /// view into one column called Value so that the value and units are on the same column"). Split
    /// across two columns a number and its unit are read as two facts, and the gap between them
    /// widened with the column; together they are the quantity, which is the one thing the row is
    /// for. <see cref="ValueText"/> and <see cref="Unit"/> stay separate underneath because the CSV
    /// and the sort both want the halves.
    /// </remarks>
    public string ValueWithUnit => Unit.Length == 0 ? ValueText : ValueText + " " + Unit;

    /// <summary>
    /// The grid's editable value — <b>the schematic's inline editor, on the same target and through
    /// the same validator</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"give the component values in the grid view (below the schematic)
    /// the same inline text editor option as what we use for the schematic (ie. the ability for user
    /// to change them, with the exact same value validator as what is used in the schematic)."</i>
    ///
    /// <para><b>"The exact same validator" is taken literally: there is no second one.</b> The setter
    /// resolves this row through <see cref="MatchDesignerViewModel.ResolveInlineEdit"/> — the same
    /// call the canvas's double-click makes, keyed on the same element name — and hands the typed
    /// string to <see cref="MatchDesignerViewModel.CommitInlineEdit"/>. So the grid inherits every
    /// rule the drawing has, including the ones that are easy to forget a second time: a complex
    /// value is refused with the reason, a non-positive one is refused, re-committing the displayed
    /// text is not an edit, and an element value is aimed at through the TRANSFORM RACK rather than
    /// written to the element. It also inherits the note under the pane when the rack cannot reach
    /// what was asked for.</para>
    ///
    /// <para>The setter never stores anything. Either the commit rebuilt the grid — in which case
    /// this row has already been updated in place and the raise below repaints it — or it refused,
    /// and the raise puts the old value back in the box, which is what a refusal has to look
    /// like.</para>
    /// </remarks>
    public string ValueEntry
    {
        get => ValueWithUnit;
        set
        {
            if (_owner.ResolveInlineEdit(Name) is { } target)
                _owner.CommitInlineEdit(target, value);
            OnPropertyChanged();
        }
    }

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
