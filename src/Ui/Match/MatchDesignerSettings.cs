using System.Collections.Generic;
using CircuitRF.Core.Matching;
using CircuitRF.Engine.Matching;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// What the Designer's <c>Settings</c> flyout holds: display units per dimension, significant digits
/// and <c>Qmin</c>. match.md §9.9 listed a fourth — whether to offer Q-adjusted solutions at all —
/// and it is gone (owner, 2026-08-28). The solutions search now always computes them and the
/// solutions panel's own filter decides whether they are listed, which is the same choice made in
/// front of the list it changes, and free rather than a re-search.
/// </summary>
/// <remarks>
/// <b>There is deliberately no standard-value series here</b> (§9.3, owner 2026-08-19). What counts as
/// realizable depends on the flow — an MMIC capacitor is designed to its value, so an E-series is not
/// merely unhelpful there but wrong — so the decision stays with the person who knows the process.
///
/// <para><b>These are display and search settings, not design inputs</b>, which is why they live on
/// the view-model and not in <c>MatchDesign</c>. Changing the capacitance unit must not make a stored
/// design different from the one that was saved.</para>
/// </remarks>
public sealed partial class MatchDesignerSettings : ObservableObject
{
    /// <summary>
    /// Display unit for inductances, or <see cref="MatchValueFormat.AutoUnit"/>.
    /// </summary>
    /// <remarks>
    /// <b>pH, not Auto</b> (owner, 2026-08-20: "change the default inductor units to pH and the
    /// default capacitance units to pF"). Auto picks a unit per VALUE, so one ladder can read
    /// "1.53 nH" beside "680 pH" beside "12 µH" and the eye has to convert before it can compare —
    /// which is the whole thing a designer does while looking at this pane. A fixed unit makes the
    /// column of numbers directly comparable; Auto is still one click away in Settings.
    /// </remarks>
    [ObservableProperty] private string _inductanceUnit = "pH";

    /// <summary>Display unit for capacitances, or <see cref="MatchValueFormat.AutoUnit"/>.</summary>
    /// <remarks>pF, for the reason <see cref="InductanceUnit"/> gives.</remarks>
    [ObservableProperty] private string _capacitanceUnit = "pF";

    /// <summary>Display unit for resistances.</summary>
    [ObservableProperty] private string _resistanceUnit = "Ω";

    /// <summary>Display unit for the band edges.</summary>
    [ObservableProperty] private string _frequencyUnit = "GHz";

    /// <summary>
    /// Significant digits in the NETWORK READOUT — the ladder preview's labels and the value grid
    /// (owner, 2026-08-19: "set the default significant digits for the network component readout to
    /// 3"). The Settings flyout offers 3..9, so anyone who wants the old six gets them back in one
    /// click.
    /// </summary>
    /// <remarks>
    /// <b>The specification pane's own entry fields do NOT read this</b> — they use
    /// <see cref="EntryDigits"/>. A field the user types into has to round-trip what was typed: at
    /// three digits a 12.345 GHz band edge redisplays as 12.3 and the next commit would silently
    /// write that back. Rounding a READOUT is a display choice; rounding an INPUT is data loss.
    /// </remarks>
    [ObservableProperty] private int _significantDigits = DefaultSignificantDigits;

    /// <summary>
    /// The readout digit count a Designer starts at — and the count a flatten falls back to when it
    /// is run from the schematic's own context menu, where no Designer (and so no chosen number) is
    /// open. See <c>MatchFlatten.Value</c> for why the flattened cell rounds at all.
    /// </summary>
    public const int DefaultSignificantDigits = 3;

    /// <summary>
    /// Digits the specification pane's editable fields render with — enough that a typed value
    /// survives a redisplay. See <see cref="SignificantDigits"/>'s own remark for why this is not
    /// the same number.
    /// </summary>
    public const int EntryDigits = 9;

    /// <summary>match.md §4.6's floor on a deliberately-inflated analysis-end Q.</summary>
    [ObservableProperty] private double _qMin = MatchSolutionSearch.DefaultQMin;

    /// <summary>
    /// The largest capacitance the <c>Block</c> toggle's default will seed (match.md §22.2).
    /// </summary>
    /// <remarks>
    /// <b>A cap on the SEED, never on the value</b> (owner, 2026-08-28: too big a capacitor can be
    /// impossible to build). The f₀/10 rule alone reaches tens of nanofarads at a low band with a
    /// small end inductor — fine on a board, absurd on an MMIC — and there is no way for the Designer
    /// to know which flow it is in, which is exactly the shape of every other entry in this class. Any
    /// positive value the user types afterwards is accepted, compensated exactly at ω₀, and reported
    /// with the spread it costs; nothing here refuses one.
    /// </remarks>
    [ObservableProperty] private double _dcBlockMaxFarads = DefaultDcBlockMaxFarads;

    /// <summary>10 nF — the seed cap a Designer starts at.</summary>
    public const double DefaultDcBlockMaxFarads = 10e-9;

    /// <summary>
    /// match.md §14.5 — the mean |ΔΓ| above which a probed termination is applied but flagged.
    /// </summary>
    /// <remarks>
    /// <b>A setting because it is a calibration task, not a design constant</b>, and the owner said so
    /// explicitly (§14.5). It is only ever a WARNING threshold: the residual is displayed for all four
    /// fits regardless, and the best physical fit is applied at any setting — so a wrong default costs
    /// a misplaced warning and never a withheld result. 0.05 in Γ is roughly the difference between a
    /// -20 dB and a -16.5 dB match.
    /// </remarks>
    [ObservableProperty] private double _probeResidualWarning = TerminationProbe.DefaultResidualWarning;

    /// <summary>The unit choices for one dimension, with Auto in front of the shared table's own.</summary>
    public static IReadOnlyList<string> UnitOptions(UnitDimension dim)
    {
        var opts = ComponentTypeRegistry.UnitOptions(dim);
        var list = new List<string>(opts.Length) { MatchValueFormat.AutoUnit };
        foreach (string u in opts)
            if (u != "None") list.Add(u);
        return list;
    }

    /// <summary>Inductance choices.</summary>
    public static IReadOnlyList<string> InductanceUnitOptions { get; } = UnitOptions(UnitDimension.Inductance);

    /// <summary>Capacitance choices.</summary>
    public static IReadOnlyList<string> CapacitanceUnitOptions { get; } = UnitOptions(UnitDimension.Capacitance);

    /// <summary>Resistance choices.</summary>
    public static IReadOnlyList<string> ResistanceUnitOptions { get; } = UnitOptions(UnitDimension.Resistance);

    /// <summary>Frequency choices.</summary>
    public static IReadOnlyList<string> FrequencyUnitOptions { get; } = UnitOptions(UnitDimension.Frequency);

    /// <summary>The display unit for one quantity.</summary>
    public string UnitFor(MatchQuantity quantity) => quantity switch
    {
        MatchQuantity.Inductance  => InductanceUnit,
        MatchQuantity.Capacitance => CapacitanceUnit,
        MatchQuantity.Resistance  => ResistanceUnit,
        _                         => FrequencyUnit,
    };

    /// <summary>Formats a value in this settings object's own unit and digits.</summary>
    public string Format(double value, MatchQuantity quantity) =>
        MatchValueFormat.FormatWithUnit(value, quantity, UnitFor(quantity), SignificantDigits);
}
