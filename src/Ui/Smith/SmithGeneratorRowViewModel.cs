using System;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One row of the generator table — a frequency and a complex impedance (§3.1), as three
/// <c>InlineEditText</c> cells.
/// </summary>
/// <remarks>
/// <b>An EDITOR over the row, not a copy of it</b> — <c>RailSourceRowViewModel</c>'s shape, and for its
/// reason. <see cref="SmithGeneratorRow"/> is the document's own object; this holds a reference to it
/// and writes each committed edit straight through, so the row and the document cannot come to
/// disagree. Every write goes out through <see cref="SmithChartViewModel.EditGeneratorRow"/>, which is
/// what makes it one undo entry and what re-sorts the table.
///
/// <para><b>A REJECTED edit still notifies</b>, which is not belt-and-braces. An <c>InlineEditText</c>
/// at rest is a <c>TextBlock</c> bound to one of these properties; a value the parser refused leaves
/// the control showing text the model does not hold, and with nothing raised it stays on screen looking
/// accepted. The owner re-reads all three after every attempt for that reason.</para>
///
/// <para><b>Three columns, fixed width by construction</b> — a frequency and two ohm values. That is
/// what lets this table take <c>InlineEditText</c>'s in-place hosting rather than the floating overlay
/// (<c>R-smith4-6</c>); if a column here ever became width-shared, the column widths are the thing to
/// fix and not the hosting.</para>
/// </remarks>
public sealed partial class SmithGeneratorRowViewModel : ObservableObject
{
    private readonly SmithChartViewModel _owner;

    public SmithGeneratorRowViewModel(SmithChartViewModel owner, SmithGeneratorRow row)
    {
        _owner = owner;
        Row    = row;
    }

    /// <summary>The document's own row. Mutated in place by <see cref="SmithChartViewModel"/>.</summary>
    public SmithGeneratorRow Row { get; }

    /// <summary>The frequency cell. Base SI in the document, a scaled unit on the face of it.</summary>
    public string FrequencyEntry
    {
        get => MatchValueFormat.FormatWithUnit(Row.FrequencyHz, MatchQuantity.Frequency, MatchValueFormat.AutoUnit, 6);
        set => _owner.EditGeneratorRow(this, "Edit generator frequency", v =>
        {
            if (MatchValueFormat.TryParseWithUnit(value, MatchQuantity.Frequency, "GHz", out double f, out _)
                && f > 0)
            {
                v.FrequencyHz = f;
                return true;
            }
            return false;
        });
    }

    /// <summary>The resistance cell, ohms.</summary>
    public string ResistanceEntry
    {
        get => MatchValueFormat.FormatWithUnit(Row.ResistanceOhm, MatchQuantity.Resistance, "Ω", 6);
        set => _owner.EditGeneratorRow(this, "Edit generator resistance", v =>
        {
            if (MatchValueFormat.TryParseWithUnit(value, MatchQuantity.Resistance, "Ω", out double r, out _))
            {
                v.ResistanceOhm = r;
                return true;
            }
            return false;
        });
    }

    /// <summary>
    /// The reactance cell, ohms. <b>Negative is ordinary here</b> — a capacitive generator is the
    /// common case and the whole point of Conjugate.
    /// </summary>
    public string ReactanceEntry
    {
        get => MatchValueFormat.FormatWithUnit(Row.ReactanceOhm, MatchQuantity.Resistance, "Ω", 6);
        set => _owner.EditGeneratorRow(this, "Edit generator reactance", v =>
        {
            if (MatchValueFormat.TryParseWithUnit(value, MatchQuantity.Resistance, "Ω", out double x, out _))
            {
                v.ReactanceOhm = x;
                return true;
            }
            return false;
        });
    }

    /// <summary>Re-reads all three cells from the document — see the type's own remarks.</summary>
    public void NotifyAll()
    {
        OnPropertyChanged(nameof(FrequencyEntry));
        OnPropertyChanged(nameof(ResistanceEntry));
        OnPropertyChanged(nameof(ReactanceEntry));
    }
}
