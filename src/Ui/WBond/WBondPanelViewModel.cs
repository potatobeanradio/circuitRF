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
    /// <summary>
    /// Which array this card is, as a flat index into the design's arrays — the same index
    /// <c>WireMesh.ArrayOfWire</c> reports, so double-clicking the name can select exactly this
    /// array's wires (owner, 2026-08-16) without the view deriving membership a second way.
    /// </summary>
    [ObservableProperty] private int _arrayIndex;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _self = "";
    [ObservableProperty] private string _wires = "";
    [ObservableProperty] private string _totalLength = "";

    /// <summary>
    /// The four settable geometry rows (owner, 2026-08-16), in the panel's own order: loop height,
    /// span, diameter, material.
    ///
    /// <para>Each is the MEDIAN across the array's wires, suffixed with
    /// <see cref="WBondPanelViewModel.NonUniformMarker"/> when they do not all agree — see
    /// <see cref="PanelReadout.Aggregate{T}"/> for why an array cannot honestly report one wire's
    /// value as its own. Double-clicking any of them sets every wire in the array to a new value,
    /// which is also why the marker is on the value and not on the label: the value is the thing the
    /// gesture is about.</para>
    /// </summary>
    [ObservableProperty] private string _loopHeight = "";

    /// <inheritdoc cref="LoopHeight"/>
    [ObservableProperty] private string _span = "";

    /// <inheritdoc cref="LoopHeight"/>
    [ObservableProperty] private string _diameter = "";

    /// <inheritdoc cref="LoopHeight"/>
    [ObservableProperty] private string _material = "";

    /// <summary>
    /// Whether this card's detail rows are showing. <b>Collapsed by default</b> — the card's job is
    /// the array name and its inductance, side by side, so a column of arrays can be compared at a
    /// glance; the counts and lengths are reference, wanted one array at a time.
    ///
    /// <para>Per ROW rather than per panel, and deliberately not persisted: it is a momentary "let me
    /// look at this one", not a document setting.</para>
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

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
/// One PAIR of arrays and the mutual inductance between them — <c>G1-G2</c> and its pH (owner,
/// 2026-08-16).
///
/// <para>Named by the pair rather than living on either array's card, because M(G1,G2) belongs to
/// neither one of them: it is a property of the two together, and putting it on both cards printed
/// it twice.</para>
/// </summary>
public sealed partial class WBondMutualPairViewModel : ObservableObject
{
    /// <summary>The pair, in array order — <c>G1-G2</c>, never also <c>G2-G1</c>.</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>The mutual, formatted in pH exactly as a self inductance is.</summary>
    [ObservableProperty] private string _mutual = "";

    /// <summary>k = M/√(L₁L₂) as a percentage — the scale-free half of the same answer.</summary>
    [ObservableProperty] private string _coupling = "";
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
    /// Every cross-array mutual, ONCE, in its own box below the cards (owner, 2026-08-16).
    ///
    /// <para><b>M is symmetric, so a per-card list said everything twice</b> — G1's card carried
    /// G1-G3 and G3's carried G3-G1, the same number under two names, and neither card could show
    /// the pair a reader was actually comparing without expanding both. Collected here the list is
    /// n(n−1)/2 rows, each named by the pair it belongs to, and it is shown at the same size as a
    /// self inductance because a mutual matters just as much.</para>
    ///
    /// <para>Empty on a single-array design, where there is no pair to report.</para>
    /// </summary>
    public ObservableCollection<WBondMutualPairViewModel> MutualPairs { get; } = [];

    /// <summary>True when there is any cross-array mutual at all — the box's own visibility.</summary>
    [ObservableProperty] private bool _hasMutualPairs;

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

        UpdateMutualPairs(readout);
    }

    /// <summary>
    /// Rebuilds the shared mutual list — the upper triangle only, so G1-G3 appears and G3-G1 does
    /// not.
    ///
    /// <para>Updated in place like <see cref="Rows"/>, and for the same reason: this list is live
    /// during a drag, and replacing the collection sixty times a second would make every row flicker
    /// exactly where the user is trying to watch a number move.</para>
    /// </summary>
    private void UpdateMutualPairs(PanelReadout readout)
    {
        var pairs = new List<(string Label, double PicoHenries, double Coupling)>();

        for (int a = 0; a < readout.Rows.Count; a++)
        {
            var row = readout.Rows[a];
            for (int b = a + 1; b < readout.Rows.Count; b++)
            {
                if (b >= row.MutualPicoHenries.Count) continue;

                pairs.Add((row.Name + "-" + readout.Rows[b].Name,
                           row.MutualPicoHenries[b],
                           b < row.CouplingCoefficients.Count ? row.CouplingCoefficients[b] : 0.0));
            }
        }

        while (MutualPairs.Count > pairs.Count) MutualPairs.RemoveAt(MutualPairs.Count - 1);
        while (MutualPairs.Count < pairs.Count) MutualPairs.Add(new WBondMutualPairViewModel());

        for (int i = 0; i < pairs.Count; i++)
        {
            MutualPairs[i].Name = pairs[i].Label;
            MutualPairs[i].Mutual = FormatPicoHenries(pairs[i].PicoHenries);
            MutualPairs[i].Coupling = FormatCoupling(pairs[i].Coupling);
        }

        HasMutualPairs = pairs.Count > 0;
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
        row.ArrayIndex = selfIndex;
        row.Name = source.Name;
        row.Self = FormatPicoHenries(source.SelfPicoHenries);
        row.Wires = source.WireCount.ToString(CultureInfo.InvariantCulture);
        row.TotalLength = FormatLength(source.TotalLengthMm, unit);

        row.LoopHeight = Mark(FormatLength(source.LoopHeightMm.Value, unit), source.LoopHeightMm.Varies);
        row.Span       = Mark(FormatLength(source.SpanMm.Value, unit),       source.SpanMm.Varies);
        row.Diameter   = Mark(FormatLength(source.DiameterMm.Value, unit),   source.DiameterMm.Varies);
        row.Material   = Mark(source.Material.Value,                          source.Material.Varies);

        // The mutuals are NOT here any more: M is symmetric, so a per-card list printed every pair
        // twice under two names. They live once, in the panel's own MutualPairs box.
        Fill(row.Coupling, source.CouplingCoefficients
                                 .Select((k, i) => (k, i))
                                 .Where(t => t.i != selfIndex)
                                 .Select(t => FormatCoupling(t.k)));

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
    /// What a value is suffixed with when the array's wires do not all share it (owner, 2026-08-16).
    ///
    /// <para>One character, on the value rather than on the label, because the value is what it
    /// qualifies — "18.5 mil *" reads as "about 18.5, and they differ", which is exactly the claim
    /// being made. A second row of prose saying so would cost more space than the whole card has.</para>
    /// </summary>
    public const string NonUniformMarker = " *";

    /// <summary>Appends <see cref="NonUniformMarker"/> when the array's wires disagree.</summary>
    public static string Mark(string text, bool varies) => varies ? text + NonUniformMarker : text;

    /// <summary>
    /// The tooltip on the four settable rows. Stated once here rather than four times in the markup,
    /// and it has to state both halves: the gesture (a double-click is not discoverable) and what the
    /// <c>*</c> means (a bare asterisk beside a number is not either).
    /// </summary>
    public static string SettableRowTip =>
        "Double-click to set this for every wire in the array." +
        "\n\"" + NonUniformMarker.Trim() + "\" marks a value the array's wires do not share — the median is shown.";

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
