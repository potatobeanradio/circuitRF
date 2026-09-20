using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One row of the Load panel — what the cascade lands on at one of the generator table's
/// frequencies, as three read-only cells mirroring the generator's own f / R / X columns
/// (owner instruction, 2026-09-19).
/// </summary>
/// <remarks>
/// <b>The generator table says where the design STARTS; this says where it ENDS</b>, at the same
/// frequencies, in the same three columns, so the two tables are read against each other row for
/// row. The status strip already reports the load — but at the design frequency only, which is one
/// row of the table, and the question a matching network poses is what the other rows are doing.
///
/// <para><b>Read-only, and not an <c>InlineEditText</c>.</b> Every editable value in this window is
/// one (<c>R-smith4-5</c>) and this is the counter-case: there is no load element and nothing
/// terminates the cascade (§3.2), so a load impedance is not a quantity that can be set — it is
/// where the walk arrived. The cells are still SELECTABLE text, because the whole point of putting a
/// number on screen is that it can be copied out.</para>
///
/// <para><b>It computes nothing.</b> The impedance is <c>SmithReadings</c>', handed in by the view
/// model from the one evaluation that also produced the chart and the strip — the rule
/// <c>SmithReading</c>'s own header states, and the reason a VSWR or a conjugate cannot be wrong in
/// two places here. A frequency the cascade refuses shows <see cref="Unavailable"/> rather than a
/// number, in that row alone: one unreadable S2P should not empty the table.</para>
/// </remarks>
public sealed partial class SmithLoadRowViewModel : ObservableObject
{
    /// <summary>What a cell shows when the cascade could not be evaluated at this row's frequency.
    /// An em dash, not a zero: a zero-ohm load is a real answer and this is the absence of one.</summary>
    public const string Unavailable = "—";

    public SmithLoadRowViewModel(double frequencyHz) => FrequencyHz = frequencyHz;

    /// <summary>The generator row's frequency this reading was taken at, hertz.</summary>
    public double FrequencyHz { get; private set; }

    [ObservableProperty] private string _frequencyDisplay  = "";
    [ObservableProperty] private string _resistanceDisplay = Unavailable;
    [ObservableProperty] private string _reactanceDisplay  = Unavailable;

    /// <summary>
    /// Puts one evaluated load impedance on this row. <paramref name="loadZ"/> is null when the
    /// cascade refused at this frequency.
    /// </summary>
    public void Set(double frequencyHz, System.Numerics.Complex? loadZ)
    {
        FrequencyHz       = frequencyHz;
        FrequencyDisplay  = MatchValueFormat.FormatWithUnit(
                                frequencyHz, MatchQuantity.Frequency, MatchValueFormat.AutoUnit, 6);

        if (loadZ is not { } z || !double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary))
        {
            ResistanceDisplay = Unavailable;
            ReactanceDisplay  = Unavailable;
            return;
        }

        // THE GENERATOR TABLE'S OWN FORMATTER, so a load of 12.4 Ω is spelled exactly as a generator
        // resistance of 12.4 Ω is and the two columns line up as numbers rather than as strings that
        // happen to be similar. A negative reactance carries its sign here (it is not an entry field
        // with a separate sign convention) — capacitive is the ordinary case and is what the whole
        // tool is usually walking away from.
        //
        // FOUR SIGNIFICANT DIGITS, WHICH IS THE STATUS STRIP'S AND NOT THE GENERATOR TABLE'S. The
        // strip reports this same quantity — the load impedance — at the design frequency, which is
        // one of these rows; at the table's six the two would print different numbers for the one
        // row they share, and a reader comparing them would be looking at a disagreement that is not
        // one. Six is right for the generator table because those values are TYPED and a cell must
        // give back what was entered; nothing here was typed.
        ResistanceDisplay = MatchValueFormat.FormatWithUnit(z.Real, MatchQuantity.Resistance, "Ω", 4);
        ReactanceDisplay  = MatchValueFormat.FormatWithUnit(z.Imaginary, MatchQuantity.Resistance, "Ω", 4);
    }
}
