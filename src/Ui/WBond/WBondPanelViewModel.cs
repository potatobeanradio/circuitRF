using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.WBond;

/// <summary>One array's row in the inductance panel (wbond.md §6.8).</summary>
public sealed partial class WBondArrayRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _self = "";
    [ObservableProperty] private string _wires = "";
    [ObservableProperty] private string _totalLength = "";
    [ObservableProperty] private string _landingSpan = "";

    /// <summary>Mutual to each array, already formatted in pH.</summary>
    public ObservableCollection<string> Mutuals { get; } = [];

    /// <summary>Coupling coefficients to each array, as percentages.</summary>
    public ObservableCollection<string> Coupling { get; } = [];

    /// <summary>
    /// Per-wire current share for 1 A into this array, normalised to [0,1] against the array's own
    /// maximum — the canvas paints this as a colour ramp.
    ///
    /// <para>Edge wires carry measurably more than centre ones. That is real array current-crowding
    /// the reduction captures for free, and showing it is most of why the panel is worth having.</para>
    /// </summary>
    public ObservableCollection<double> CurrentRamp { get; } = [];
}

/// <summary>
/// The inductance panel (wbond.md §6.8, R-wbc-7).
///
/// <h3>Picohenries, fixed, never auto-ranged (WB27a / D9)</h3>
/// <para>The panel exists for <b>comparison during a drag</b> — across arrays, and against the same
/// array a second ago. A readout that silently switches nH to pH mid-drag makes a number appear to
/// jump by 1000x when the geometry moved by a mil, which is precisely the illusion a live readout
/// must not create. Wirebond inductances live in the tens-to-thousands of pH, so one unit covers the
/// whole useful range and <see cref="FormatPicoHenries"/> has no ranging logic to get wrong.</para>
/// </summary>
public sealed partial class WBondPanelViewModel : ObservableObject
{
    /// <summary>What the return path currently is — stated at all times (WB20 / RW13).</summary>
    [ObservableProperty] private string _returnPath = "";

    /// <summary>True when no return path is declared, so the view can style it as a problem.</summary>
    [ObservableProperty] private bool _returnPathUndeclared;

    /// <summary>Unmodelled coupling to other wBond components, or empty (WB30).</summary>
    [ObservableProperty] private string _couplingWarning = "";

    public ObservableCollection<WBondArrayRowViewModel> Rows { get; } = [];

    /// <summary>
    /// Rebuilds the panel from a readout.
    ///
    /// <para>Rows are updated in place where the shape is unchanged, so a drag does not churn the
    /// bound collection sixty times a second — that would make every row flicker and would defeat the
    /// comparison the fixed unit exists to enable.</para>
    /// </summary>
    public void Update(PanelReadout readout)
    {
        ArgumentNullException.ThrowIfNull(readout);

        ReturnPath = readout.ReturnPath;
        ReturnPathUndeclared = readout.ReturnPath.Contains("UNDECLARED", StringComparison.Ordinal);

        while (Rows.Count > readout.Rows.Count) Rows.RemoveAt(Rows.Count - 1);
        while (Rows.Count < readout.Rows.Count) Rows.Add(new WBondArrayRowViewModel());

        for (int i = 0; i < readout.Rows.Count; i++)
            Apply(Rows[i], readout.Rows[i]);
    }

    private static void Apply(WBondArrayRowViewModel row, PanelReadout.ArrayRow source)
    {
        row.Name = source.Name;
        row.Self = FormatPicoHenries(source.SelfPicoHenries);
        row.Wires = source.WireCount.ToString(CultureInfo.InvariantCulture);
        row.TotalLength = source.TotalLengthMm.ToString("F2", CultureInfo.InvariantCulture) + " mm";
        row.LandingSpan = source.MaxLandingSpanMm.ToString("F2", CultureInfo.InvariantCulture) + " mm";

        Fill(row.Mutuals, source.MutualPicoHenries.Select(FormatPicoHenries));
        Fill(row.Coupling, source.CouplingCoefficients.Select(FormatCoupling));

        // Normalised against this array's own maximum share, so the ramp uses its full range whatever
        // the absolute currents are.
        double peak = source.CurrentShares.Count == 0 ? 0.0 : source.CurrentShares.Max(Math.Abs);
        Fill(row.CurrentRamp, source.CurrentShares.Select(s => peak == 0.0 ? 0.0 : Math.Abs(s) / peak));
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var list = values.ToList();
        while (target.Count > list.Count) target.RemoveAt(target.Count - 1);
        for (int i = 0; i < list.Count; i++)
        {
            if (i < target.Count) target[i] = list[i];
            else target.Add(list[i]);
        }
    }

    /// <summary>
    /// Formats an inductance in <b>picohenries, always</b> (WB27a / D9). No ranging, no unit suffix
    /// switching — that is the whole point.
    /// </summary>
    public static string FormatPicoHenries(double picoHenries) =>
        picoHenries.ToString("F2", CultureInfo.InvariantCulture) + " pH";

    /// <summary>
    /// Formats a coupling coefficient as a percentage.
    ///
    /// <para>Offered alongside the pH mutuals because it is scale-free, and it is the number that
    /// says whether two arrays are meaningfully coupled — a bare pH mutual does not, without mentally
    /// dividing by the selfs.</para>
    /// </summary>
    public static string FormatCoupling(double k) =>
        (k * 100.0).ToString("F2", CultureInfo.InvariantCulture) + " %";
}
