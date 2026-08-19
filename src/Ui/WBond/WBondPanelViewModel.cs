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

    /// <summary>
    /// The editor these rows describe, and the one the panel's own gestures act on — double-clicking an
    /// array name to select its wires, and the four settable rows.
    ///
    /// <para><b>It lives on the FORMATTER, not on the view, and that is the whole point.</b> The view had
    /// it pushed in by each host, and the docked host pushed it exactly once — on its own
    /// <c>DataContextChanged</c>, which fires when the TOOL is bound and never again, because a dock tool
    /// instance lives for the whole session while the editor it points at changes with every document
    /// activation. So it was null for the life of the panel and the array double-click returned
    /// immediately, selecting nothing (owner, 2026-08-17, reported twice).</para>
    ///
    /// <para>Here it cannot go stale: every host that has a readout to format has the editor that
    /// produced it, and both are assigned together — there is no second moment to forget.</para>
    /// </summary>
    [ObservableProperty] private WBondViewModel? _editor;

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

    /// <summary>
    /// The frequency the self-inductance numbers are quoted at, formatted — <c>"10 GHz"</c>.
    ///
    /// <para><b>The panel needs this row only because capacitance exists</b> (wbond.md §6.8). Before
    /// it, the reported quantity was the frequency-independent partial inductance and there was
    /// nothing to state. With shunt capacitance the terminal inductance genuinely moves with
    /// frequency, so the panel has to say which one it is showing.</para>
    ///
    /// <para>GHz always, never auto-ranged — the same rule, for the same reason, as the fixed
    /// picohenries.</para>
    /// </summary>
    [ObservableProperty] private string _frequency = "";

    /// <summary>
    /// The above-self-resonance sentence, or empty. Shown in the warning brush, in place of the
    /// numbers rather than beside them.
    ///
    /// <para>Above resonance the effective inductance runs to ±∞ and comes back negative. That is not
    /// a wrong number a reader can discount — it is a number that looks like an answer, so the cards'
    /// self values are blanked while this is set (gate C9).</para>
    /// </summary>
    [ObservableProperty] private string _resonanceWarning = "";

    /// <summary>True while <see cref="ResonanceWarning"/> is worth a row.</summary>
    [ObservableProperty] private bool _aboveResonance;

    /// <summary>
    /// The capacitance toggle, <b>on the panel rather than only on the editor's toolbar</b>.
    ///
    /// <para><b>Because the panel has TWO hosts and only one of them has that toolbar</b> (owner,
    /// 2026-08-18). The wBond editor docks this control inline beside its own toolbar; the workspace
    /// offers the same control as a dock tool over the active layout, where there is no wBond editor
    /// on screen at all. The frequency row beside this one was settable in both hosts from the start,
    /// so a capacitance switch reachable in only one of them was the odd one out — and it is the
    /// setting that decides what the number above it even means.</para>
    ///
    /// <para>It writes through to <see cref="Editor"/>, which is the same
    /// <c>WBondDesign.IncludeCapacitance</c> the toolbar toggle writes, so the two cannot
    /// disagree.</para>
    /// </summary>
    [ObservableProperty] private bool _includeCapacitance = true;

    /// <summary>
    /// Whether the toggle is offered at all. False when there is no editor behind the panel — a dock
    /// tool bound before any wirebond has been opened is a pure readout, and a switch that silently
    /// does nothing is worse than an absent one.
    /// </summary>
    [ObservableProperty] private bool _canToggleCapacitance;

    /// <summary>
    /// Whether the frequency row is worth showing. <b>False when capacitance is not in the numbers</b>,
    /// because the effective inductance is then <c>L_arr</c> at every frequency and the row provably
    /// changes nothing — the same rule <see cref="ShowReturnPath"/> already follows for a line that
    /// would always say the expected thing.
    /// </summary>
    [ObservableProperty] private bool _showFrequency;

    /// <summary>
    /// Set when the design asks for capacitance and cannot have it, with the reason. Empty otherwise.
    ///
    /// <para>The one case is a disabled ground plane: the plane at z = 0 IS the reference conductor,
    /// so with it off there is nothing for the charge to return to. Saying so matters because the
    /// missing capacitance moves the reported inductance in the <b>optimistic</b> direction, which is
    /// the failure mode the return-path refusal above already exists to stop.</para>
    /// </summary>
    [ObservableProperty] private string _capacitanceUnavailable = "";

    /// <summary>Guards the write-back while <see cref="Update"/> is pushing state in.</summary>
    private bool _updating;

    partial void OnIncludeCapacitanceChanged(bool value)
    {
        if (_updating || Editor is not { } editor) return;
        editor.IncludeCapacitance = value;
    }

    partial void OnEditorChanged(WBondViewModel? value) => CanToggleCapacitance = value is not null;

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
        Frequency = FormatFrequency(readout.ReadoutFrequencyGHz);
        AboveResonance = readout.AboveSelfResonance;
        ResonanceWarning = readout.ResonanceWarning;
        ShowFrequency = readout.CapacitanceIncluded;

        // The toggle shows what was ASKED for; the note below it explains a design that asked and
        // could not have it.
        _updating = true;
        IncludeCapacitance = readout.CapacitanceRequested;
        _updating = false;

        CapacitanceUnavailable = readout.CapacitanceRequested && !readout.CapacitanceIncluded
            ? "Capacitance is off because the ground plane is disabled — the plane at z = 0 is what " +
              "the charge would return to."
            : "";

        while (Rows.Count > readout.Rows.Count) Rows.RemoveAt(Rows.Count - 1);
        while (Rows.Count < readout.Rows.Count) Rows.Add(new WBondArrayRowViewModel());

        for (int i = 0; i < readout.Rows.Count; i++)
            Apply(Rows[i], readout.Rows[i], i, Unit, readout.AboveSelfResonance);

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
                              WBondUnit unit, bool aboveResonance)
    {
        row.ArrayIndex = selfIndex;
        row.Name = source.Name;
        // Above resonance there is no number to print — see ResonanceWarning.
        row.Self = aboveResonance ? "" : FormatPicoHenries(source.SelfPicoHenries);
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

    /// <summary>Formats the readout frequency. GHz always — see <see cref="Frequency"/>.</summary>
    public static string FormatFrequency(double gigahertz) =>
        gigahertz.ToString("0.####", CultureInfo.InvariantCulture) + " GHz";

    /// <summary>
    /// The tooltip on the frequency row.
    ///
    /// <para>It used to end "this is a readout setting — it never reaches the simulation", which the
    /// owner had removed (2026-08-18). The statement is true and still lives in
    /// <see cref="WBondDesign.ReadoutFrequencyGHz"/> with a test holding it, but it describes an
    /// internal boundary rather than the thing the user is setting: <b>the frequency the inductance
    /// is extracted at</b>. Saying what a control does beats disclaiming what it does not.</para>
    /// </summary>
    public static string FrequencyRowTip =>
        "The inductance extraction frequency — the frequency the self inductances above are quoted at." +
        "\nDouble-click to change it.";

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
