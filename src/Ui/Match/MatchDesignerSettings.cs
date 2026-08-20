using System.Collections.Generic;
using CircuitRF.Core.Matching;
using CircuitRF.Engine.Matching;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// What the Designer's <c>Settings</c> flyout holds — and match.md §9.9 is explicit that it is this
/// and nothing more: display units per dimension, significant digits, <c>Qmin</c>, and whether to
/// offer Q-adjusted solutions at all.
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
    /// <summary>Display unit for inductances, or <see cref="MatchValueFormat.AutoUnit"/>.</summary>
    [ObservableProperty] private string _inductanceUnit = MatchValueFormat.AutoUnit;

    /// <summary>Display unit for capacitances, or <see cref="MatchValueFormat.AutoUnit"/>.</summary>
    [ObservableProperty] private string _capacitanceUnit = MatchValueFormat.AutoUnit;

    /// <summary>Display unit for resistances.</summary>
    [ObservableProperty] private string _resistanceUnit = "Ω";

    /// <summary>Display unit for the band edges.</summary>
    [ObservableProperty] private string _frequencyUnit = "GHz";

    /// <summary>Significant digits in every displayed value.</summary>
    [ObservableProperty] private int _significantDigits = 6;

    /// <summary>match.md §4.6's floor on a deliberately-inflated analysis-end Q.</summary>
    [ObservableProperty] private double _qMin = MatchSolutionSearch.DefaultQMin;

    /// <summary>Whether the solutions list offers §4.6's Q-adjusted extra solution at all.</summary>
    [ObservableProperty] private bool _offerQAdjustedSolutions = true;

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
