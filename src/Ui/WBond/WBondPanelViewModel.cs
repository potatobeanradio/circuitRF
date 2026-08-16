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

    /// <summary>
    /// Whether this card's detail rows are showing. <b>Collapsed by default</b> — the card's job is
    /// the array name and its inductance, side by side, so a column of arrays can be compared at a
    /// glance; the counts and lengths are reference, wanted one array at a time.
    ///
    /// <para>Per ROW rather than per panel, and deliberately not persisted: it is a momentary "let me
    /// look at this one", not a document setting.</para>
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// Mutual to every OTHER array, already formatted in pH — the self term is deliberately absent.
    ///
    /// <para>It used to be included, which put a second copy of <see cref="Self"/> at the bottom of
    /// every card and made a single-array design's card carry the same number twice. A mutual to a
    /// different array is real information and is kept, behind <see cref="IsExpanded"/> with the rest
    /// of the detail.</para>
    /// </summary>
    public ObservableCollection<string> Mutuals { get; } = [];

    /// <summary>True when there is any cross-array mutual to show at all.</summary>
    [ObservableProperty] private bool _hasMutuals;

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
///
/// <h3>Lengths follow the display unit; inductance does not</h3>
/// <para>The two rules look inconsistent and are not. An inductance is fixed at pH because the panel
/// exists to compare inductances against each other, and a unit that changed under a drag would
/// destroy that comparison. A LENGTH is compared against the geometry the user is drawing and against
/// the numbers they type into the Properties panel and the transform dialogs — all of which are in
/// <see cref="Unit"/>. Reporting those in mm while the toolbar says <c>mil</c> is simply a wrong
/// readout, which is exactly how the owner found it.</para>
/// </summary>
public sealed partial class WBondPanelViewModel : ObservableObject
{
    /// <summary>
    /// The unit the length rows are shown in — the editor's own display unit (§6.5), pushed in by the
    /// document. Changing it re-formats the rows already on screen.
    /// </summary>
    [ObservableProperty] private WBondUnit _unit = WBondUnit.Mil;

    /// <summary>What the return path currently is — stated at all times (WB20 / RW13).</summary>
    [ObservableProperty] private string _returnPath = "";

    /// <summary>True when no return path is declared, so the view can style it as a problem.</summary>
    [ObservableProperty] private bool _returnPathUndeclared;

    /// <summary>
    /// Whether the return-path sentence is worth a row at all.
    ///
    /// <para>False for the ordinary image plane at z = 0, which is every document with a ground plane
    /// — a line that says the same expected thing always costs a row and tells nobody anything. True
    /// the moment the return path is NOT that, which is the case WB20/RW13 actually exists for.</para>
    /// </summary>
    [ObservableProperty] private bool _showReturnPath;

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
        _last = readout;

        ReturnPath = readout.ReturnPath;
        ReturnPathUndeclared = readout.ReturnPath.Contains("UNDECLARED", StringComparison.Ordinal);
        ShowReturnPath = !readout.ReturnPathIsDefault;

        while (Rows.Count > readout.Rows.Count) Rows.RemoveAt(Rows.Count - 1);
        while (Rows.Count < readout.Rows.Count) Rows.Add(new WBondArrayRowViewModel());

        for (int i = 0; i < readout.Rows.Count; i++)
            Apply(Rows[i], readout.Rows[i], i, Unit);
    }

    /// <summary>
    /// The readout last shown, kept so a unit change can re-format it without waiting for the next
    /// edit — switching the toolbar unit must change the panel immediately, not eventually.
    /// </summary>
    private PanelReadout? _last;

    partial void OnUnitChanged(WBondUnit value)
    {
        if (_last is not null) Update(_last);
    }

    private static void Apply(WBondArrayRowViewModel row, PanelReadout.ArrayRow source, int selfIndex,
                              WBondUnit unit)
    {
        row.Name = source.Name;
        row.Self = FormatPicoHenries(source.SelfPicoHenries);
        row.Wires = source.WireCount.ToString(CultureInfo.InvariantCulture);
        row.TotalLength = FormatLength(source.TotalLengthMm, unit);
        row.LandingSpan = FormatLength(source.MaxLandingSpanMm, unit);

        // The self term is skipped: it is already the card's headline number, and repeating it under
        // the fold was the "redundant pH readout" on every single-array document.
        Fill(row.Mutuals, source.MutualPicoHenries
                                .Select((pH, i) => (pH, i))
                                .Where(t => t.i != selfIndex)
                                .Select(t => FormatPicoHenries(t.pH)));
        Fill(row.Coupling, source.CouplingCoefficients
                                 .Select((k, i) => (k, i))
                                 .Where(t => t.i != selfIndex)
                                 .Select(t => FormatCoupling(t.k)));
        row.HasMutuals = row.Mutuals.Count > 0;

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
        picoHenries.ToString("F1", CultureInfo.InvariantCulture) + " pH";

    /// <summary>
    /// How many decimals a length gets in THIS panel, per unit.
    ///
    /// <para><b>Chosen so one digit is worth roughly the same physical amount in every unit</b>,
    /// rather than by giving every unit the same decimal count — which would make a nanometre readout
    /// four digits of noise and a millimetre one quantise to the mil the user is drawing in. The last
    /// digit is ~2.5 µm for mil and inch, 1 µm for mm, 0.1 µm for µm, and 1 nm for nm.</para>
    ///
    /// <para>Mil is the owner's stated case and is pinned at ONE decimal.</para>
    /// </summary>
    public static int Decimals(WBondUnit unit) => unit switch
    {
        WBondUnit.Nm => 0,
        WBondUnit.Um => 1,
        WBondUnit.Mm => 3,
        WBondUnit.Mil => 1,
        WBondUnit.Inch => 4,
        _ => 2,
    };

    /// <summary>
    /// Formats a length the readout carries in millimetres into the panel's display unit, at that
    /// unit's own precision (<see cref="Decimals"/>).
    ///
    /// <para>The conversion goes through nanometres, the same integer DBU everything else in wBond
    /// stores, so it agrees exactly with the Properties panel's own reading of the same quantity.</para>
    /// </summary>
    public static string FormatLength(double millimetres, WBondUnit unit)
    {
        double value = WBondUnits.FromNm((long)Math.Round(millimetres * 1e6), unit);

        return value.ToString("F" + Decimals(unit).ToString(CultureInfo.InvariantCulture),
                              CultureInfo.InvariantCulture)
             + " " + WBondUnits.Suffix(unit);
    }

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
