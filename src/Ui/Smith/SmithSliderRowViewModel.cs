using System;
using System.Globalization;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One settable parameter of the selected element: a label, a compact slider and an
/// <c>InlineEditText</c> (<c>brief-smith-6-network-strip.md</c> <c>R-smith6-4</c>,
/// <c>docs/design/smith-chart.md</c> §5.6).
/// </summary>
/// <remarks>
/// <b>An EDITOR over the element, not a copy of its value</b> — the generator rows' own shape. It holds
/// the element's INDEX rather than the element, because every committed edit and every undo REPLACES
/// the whole design (<c>SmithChartViewModel.ApplySnapshot</c>), so a reference captured at construction
/// would write into an object the document no longer owns and the strip would quietly stop responding.
///
/// <para><b>The RANGE a row spans is a constant, not a function of the value</b> — see
/// <see cref="DefaultRange"/>, which is where the owner's per-component defaults live and where the
/// runaway that made this necessary is written down. The slider over it is logarithmic for R, L, C and
/// a line's Z₀ whenever that range starts above zero, and linear otherwise: those four span decades,
/// and a linear slider over 1 pF … 100 pF spends nine tenths of its travel above 10 pF. An electrical
/// length and a Z1P's two parts are naturally linear and one of them is routinely negative, which a log
/// axis cannot express at all.</para>
///
/// <para><b>Typing a value outside the range WIDENS the range rather than clamping it</b>
/// (<c>R-smith6-4</c>). A typed number is an instruction; a dragged one is a gesture. Clamping a typed
/// value silently substitutes a different design for the one that was asked for, and the only evidence
/// is a number that did not change.</para>
///
/// <para><b>Every write goes out through the owner</b>, which is what makes one drag one undo entry —
/// this type pushes nothing and knows nothing about the undo stack. The Match Designer's slider
/// write-back defect (every undo ADDING an entry, so eight edits took fourteen undos) is the regression
/// that rule exists to prevent.</para>
/// </remarks>
public sealed partial class SmithSliderRowViewModel : ObservableObject
{
    private readonly SmithChartViewModel _owner;

    internal SmithSliderRowViewModel(SmithChartViewModel owner, int elementIndex, SmithParameter parameter)
    {
        _owner       = owner;
        ElementIndex = elementIndex;
        Parameter    = parameter;
    }

    /// <summary>Which element in <see cref="SmithDesign.Elements"/> this row edits.</summary>
    public int ElementIndex { get; }

    /// <summary>Which of its parameters.</summary>
    public SmithParameter Parameter { get; }

    // ── What the row says ────────────────────────────────────────────────────

    /// <summary>The row's label — the parameter's own name, in the spelling the chart and the
    /// schematic label both use.</summary>
    public string Label => LabelFor(Parameter);

    /// <summary>The parameter's name as this window writes it.</summary>
    public static string LabelFor(SmithParameter p) => p switch
    {
        SmithParameter.R                => "R",
        SmithParameter.L                => "L",
        SmithParameter.C                => "C",
        SmithParameter.Z0               => "Z₀",
        SmithParameter.ElectricalLength => "E",
        SmithParameter.ImpedanceReal    => "Re Z",
        SmithParameter.ImpedanceImag    => "Im Z",
        _                               => p.ToString(),
    };

    /// <summary>
    /// True when this is the element's <see cref="SmithElement.ActiveParameter"/> — the one a gripper
    /// drag moves (<c>R-smith6-5</c>).
    /// </summary>
    /// <remarks>
    /// <b>The active row is MARKED</b>, so the connection between "the slider I just used" and "the
    /// handle on the chart" is visible rather than remembered. Without it the handle silently changes
    /// what it does, and the only way to find out which parameter it is on is to drag it.
    /// </remarks>
    public bool IsActive => _owner.ElementAt(ElementIndex) is { } e
                         && SmithComponentMap.ActiveParameterOf(e) == Parameter;

    /// <summary>
    /// True when this row's slider is logarithmic — the four decade-spanning parameters,
    /// <b>and only when the range in force starts above zero</b>.
    /// </summary>
    /// <remarks>
    /// <b>A log axis cannot express zero, so a range that includes it is drawn linearly.</b> That is
    /// not a compromise, it is what makes the owner's own defaults expressible: an inductor's range
    /// is 0 … 10 nH and a shunt capacitor's 0 … 10 pF, while a series capacitor's 0.1 pF … 1000 pF
    /// spans four decades and would be unusable on anything but a log slider. The rule keys on the
    /// RANGE rather than the parameter so a user who re-ranges a row gets the axis that range needs.
    /// </remarks>
    public bool IsLogarithmic => IsLogKind(Parameter) && Range.Min > 0;

    private static bool IsLogKind(SmithParameter p)
        => p is SmithParameter.R or SmithParameter.L or SmithParameter.C or SmithParameter.Z0;

    // ── The value ────────────────────────────────────────────────────────────

    /// <summary>The stored value, base SI (or degrees for an electrical length).</summary>
    public double Value => _owner.ElementAt(ElementIndex) is { } e ? Read(e, Parameter) : 0.0;

    /// <summary>The value with its unit — the <c>InlineEditText</c>'s text.</summary>
    public string ValueEntry
    {
        get => Format(Value);
        set
        {
            if (!TryParse(value, out double v)) { NotifyAll(); return; }

            // A TYPED value out of range moves the RANGE, not the value. The order matters: the range
            // is published first so the slider's own coercion — a RangeBase clamps Value into
            // [Minimum, Maximum] the instant either changes — cannot reach back with the old bounds
            // still on it. That coercion writing through an unguarded setter mid-edit is precisely the
            // Match Designer's defect (src/Ui/Match/RESOLVED.md).
            EnsureRangeCovers(v);
            _owner.SetElementValue(ElementIndex, Parameter, v, $"Edit {Label}");
        }
    }

    /// <summary>
    /// Where the thumb sits — <b>log₁₀ of the value on a logarithmic row</b>, and the value itself on a
    /// linear one. Two-way bound, and written on every step of a drag.
    /// </summary>
    /// <remarks>
    /// <b>This setter pushes NO undo entry</b>, which is the whole of <c>R-smith6-4</c>'s "one drag is
    /// one undo entry". The entry is pushed on release by <c>SmithChartViewModel.EndSliderDrag</c>,
    /// carrying the value captured on press — and a keyboard step, which has no press and no release,
    /// gets its own single entry from the owner instead.
    /// </remarks>
    public double Position
    {
        get
        {
            double v = Value;
            if (!IsLogarithmic) return v;
            return v > 0 ? Math.Log10(v) : Minimum;
        }
        set
        {
            double v = IsLogarithmic ? Math.Pow(10.0, value) : value;
            _owner.SetElementValue(ElementIndex, Parameter, v, $"Drag {Label}");
        }
    }

    // ── The range ────────────────────────────────────────────────────────────

    /// <summary>The slider's lower bound, in <see cref="Position"/>'s own domain.</summary>
    public double Minimum => IsLogarithmic ? Math.Log10(Range.Min) : Range.Min;

    /// <summary>The slider's upper bound, in <see cref="Position"/>'s own domain.</summary>
    public double Maximum => IsLogarithmic ? Math.Log10(Range.Max) : Range.Max;

    /// <summary>The range's lower end, with its unit — shown at the slider's left and editable.</summary>
    public string MinEntry
    {
        get => Format(Range.Min);
        set { if (TryParse(value, out double v)) SetRange(v, Range.Max); else NotifyAll(); }
    }

    /// <summary>The range's upper end, with its unit — shown at the slider's right and editable.</summary>
    public string MaxEntry
    {
        get => Format(Range.Max);
        set { if (TryParse(value, out double v)) SetRange(Range.Min, v); else NotifyAll(); }
    }

    /// <summary>
    /// What this row's slider spans — the element's own stored range when it has one, and otherwise
    /// one derived from the current value.
    /// </summary>
    /// <remarks>
    /// <b>A derived range is not persisted.</b> Only an explicit re-range writes into the document, so
    /// a design that has never had its ranges touched carries none and reopens with sliders centred on
    /// whatever its values have become — which is what a user who never asked about ranges expects.
    ///
    /// <para><b>A stored range that does not CONTAIN the value is stood down for as long as that is
    /// true, and this is a safety rule rather than a convenience.</b> A <c>RangeBase</c> coerces its
    /// <c>Value</c> into <c>[Minimum, Maximum]</c> the instant either is published, and the coerced
    /// number writes straight back through the two-way binding — so a range that excluded the value
    /// would silently CHANGE the design, from a notification, with no gesture behind it. That is
    /// exactly the Match Designer's defect (<c>src/Ui/Match/RESOLVED.md</c>), and the cheapest way to
    /// be immune to it is to make the coercion a no-op by construction. A gripper drag can carry a
    /// value out of its slider's stored range at any time, so this is a live case and not a
    /// theoretical one; the stored range is not erased and comes back the moment the value returns to
    /// it.</para>
    /// </remarks>
    public SmithSliderRange Range
    {
        get
        {
            if (_owner.ElementAt(ElementIndex) is not { } e) return DefaultRange(null, 0.0);

            double v = Read(e, Parameter);
            return e.SliderRange.TryGetValue(Parameter, out var r) && IsUsable(r) && Covers(r, v)
                ? r
                : DefaultRange(e, v);
        }
    }

    private static bool IsUsable(SmithSliderRange r)
        => double.IsFinite(r.Min) && double.IsFinite(r.Max) && r.Max > r.Min;

    private static bool Covers(SmithSliderRange r, double value)
        => double.IsFinite(value) && value >= r.Min && value <= r.Max;

    /// <summary>
    /// The range a row opens on: a <b>FIXED</b> span per parameter and placement, widened only when
    /// the element's own value falls outside it.
    /// </summary>
    /// <remarks>
    /// <b>A range derived from the current value is a runaway, and that is what this replaces</b>
    /// (owner report, 2026-09-19: a series L dragged to the right end of its slider reached
    /// <i>9999999 H</i>). The old rule was one decade either side of the value, recomputed on every
    /// read: drag the thumb to the top and the value becomes the old maximum, so the next read
    /// centred the range on THAT and the maximum moved up again. Each pointer move multiplied the
    /// ceiling by ten and the slider never ran out of travel. <see cref="BaseRange"/> does not move,
    /// so a drag to the end is a drag to the end.
    ///
    /// <para><b>The spans are the ones that mean something at the design frequency this tool opens
    /// on</b> (2 GHz, owner instruction): 10 nH is about 125 Ω of series reactance there and 10 pF
    /// about 8 Ω of shunt, which is the useful width of a narrowband match. A series capacitor is
    /// the one that is not symmetric with its shunt twin — it is usually a DC block rather than a
    /// matching element — so it runs 0.1 pF … 1000 pF, and its bottom end is above zero because a
    /// series C of zero farads is an open circuit rather than a small capacitor.</para>
    ///
    /// <para><b>Widening is decade-snapped rather than value-centred</b>, which is what keeps it
    /// stable: a value carried past the top by a gripper drag (which has no slider to bound it)
    /// raises the ceiling to the next 1/2/5 × 10ⁿ at or above it, and dragging the thumb to that new
    /// ceiling leaves it exactly where it is. The element's stored range, if it has one, is
    /// untouched and comes back the moment the value returns to it.</para>
    /// </remarks>
    private SmithSliderRange DefaultRange(SmithElement? element, double value)
    {
        var (min, max) = BaseRange(element);

        if (!double.IsFinite(value) || (value >= min && value <= max))
            return new SmithSliderRange(min, max);

        if (value > max) max = NiceCeiling(value);
        else             min = NiceFloor(value);

        return new SmithSliderRange(min, max);
    }

    /// <summary>
    /// What a row spans before anything has been dragged past it — <b>a constant, per parameter and
    /// placement</b>, quoted for a 2 GHz design frequency.
    /// </summary>
    private (double Min, double Max) BaseRange(SmithElement? element)
    {
        bool shunt = element?.Placement == SmithPlacement.Shunt;

        return Parameter switch
        {
            // A series R of zero ohms is a wire and perfectly meaningful; a shunt R of zero is a dead
            // short, so that one starts at an ohm and spans decades instead.
            SmithParameter.R  => shunt ? (1.0, 1e4) : (0.0, 1e3),

            // 10 nH ≈ 125 Ω at 2 GHz, in either placement.
            SmithParameter.L  => (0.0, 10e-9),

            // 10 pF ≈ 8 Ω of shunt at 2 GHz; a series C is a blocking capacitor and wants the decades.
            SmithParameter.C  => shunt ? (0.0, 10e-12) : (0.1e-12, 1000e-12),

            // A line's characteristic impedance: what is buildable, rather than what is expressible.
            SmithParameter.Z0 => (5.0, 500.0),

            // A full turn. Longer lines exist and are reachable by typing, which widens the range.
            SmithParameter.ElectricalLength => (0.0, 360.0),

            // A Z1P's two parts. The imaginary one is negative as often as not, so it is symmetric;
            // a passive real part is not.
            SmithParameter.ImpedanceReal => (0.0, 500.0),
            SmithParameter.ImpedanceImag => (-500.0, 500.0),

            _ => (0.0, 1.0),
        };
    }

    /// <summary>The smallest 1/2/5 × 10ⁿ at or above <paramref name="v"/>.</summary>
    private static double NiceCeiling(double v)
    {
        if (!(v > 0)) return 0.0;
        double mag = Math.Pow(10.0, Math.Floor(Math.Log10(v)));
        foreach (double step in Steps)
            if (v <= step * mag * (1.0 + 1e-12)) return step * mag;
        return 10.0 * mag;
    }

    /// <summary>The largest 1/2/5 × 10ⁿ at or below <paramref name="v"/>, and zero for anything that
    /// is not positive — a range whose bottom is zero is simply drawn linearly.</summary>
    private static double NiceFloor(double v)
    {
        if (v <= 0) return v < 0 ? -NiceCeiling(-v) : 0.0;
        double mag = Math.Pow(10.0, Math.Floor(Math.Log10(v)));
        double best = mag;
        foreach (double step in Steps)
            if (step * mag <= v * (1.0 + 1e-12)) best = step * mag;
        return best;
    }

    private static readonly double[] Steps = [1.0, 2.0, 5.0];

    /// <summary>
    /// Re-ranges this row. <b>One undo entry</b>, because a stored range is part of the document.
    /// </summary>
    public void SetRange(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || !(max > min)) { NotifyAll(); return; }

        // A minimum of zero is NOT refused any more — IsLogarithmic reads the range, so a row whose
        // span reaches zero simply becomes a linear one. That is what lets an inductor's default be
        // the 0 … 10 nH the owner asked for.
        _owner.SetSliderRange(ElementIndex, Parameter, min, max);
    }

    /// <summary>Widens the range so it covers <paramref name="v"/>, keeping the value centred — the
    /// "re-centre rather than clamp" half of <c>R-smith6-4</c>.</summary>
    private void EnsureRangeCovers(double v)
    {
        var r = Range;
        if (v >= r.Min && v <= r.Max) return;

        var next = DefaultRange(_owner.ElementAt(ElementIndex), v);
        _owner.SetSliderRange(ElementIndex, Parameter, next.Min, next.Max);
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary>
    /// The quantity this row's number IS — what picks its unit ladder.
    /// </summary>
    /// <remarks>
    /// An electrical length has none: it is stored in DEGREES, which is the document's one named
    /// exception to base SI, and it therefore must not be run through a prefix ladder that would offer
    /// to render it in millidegrees.
    /// </remarks>
    private MatchQuantity? Quantity => Parameter switch
    {
        SmithParameter.L                => MatchQuantity.Inductance,
        SmithParameter.C                => MatchQuantity.Capacitance,
        SmithParameter.ElectricalLength => null,
        _                               => MatchQuantity.Resistance,
    };

    private string Format(double v)
    {
        if (!double.IsFinite(v)) return MatchValueFormat.Significant(v, 4);
        if (Quantity is not { } q) return MatchValueFormat.Significant(v, 5) + " deg";
        return MatchValueFormat.FormatWithUnit(v, q, MatchValueFormat.AutoUnit, 5);
    }

    private bool TryParse(string? text, out double value)
    {
        if (Quantity is { } q)
            return MatchValueFormat.TryParseWithUnit(text, q, DefaultUnit, out value, out _);

        // Degrees: the number, with an optional "deg"/"°" the user may have typed back.
        value = 0.0;
        string s = (text ?? "").Trim().TrimEnd('°').Replace("deg", "", StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The unit a bare number is read as — the registry's own display unit for the
    /// parameter.</summary>
    private string DefaultUnit => Parameter switch
    {
        SmithParameter.L => "nH",
        SmithParameter.C => "pF",
        _                => "Ω",
    };

    // ── Reading one parameter off an element ─────────────────────────────────

    private static double Read(SmithElement e, SmithParameter p) => p switch
    {
        SmithParameter.R                => e.Values.ROhm,
        SmithParameter.L                => e.Values.LHenry,
        SmithParameter.C                => e.Values.CFarad,
        SmithParameter.Z0               => e.Values.Z0Ohm,
        SmithParameter.ElectricalLength => e.Values.ElectricalLengthDeg,
        SmithParameter.ImpedanceReal    => e.Values.ImpedanceOhm.Real,
        SmithParameter.ImpedanceImag    => e.Values.ImpedanceOhm.Imaginary,
        _                               => 0.0,
    };

    /// <summary>
    /// Re-reads everything this row shows.
    /// </summary>
    /// <remarks>
    /// <b>A REJECTED edit still notifies</b> — the generator rows' own rule, for the same reason. An
    /// <c>InlineEditText</c> at rest is a <c>TextBlock</c> bound to one of these properties, so a value
    /// the parser refused leaves the control showing text the model does not hold, and with nothing
    /// raised it stays on screen looking accepted.
    /// </remarks>
    public void NotifyAll()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueEntry));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Range));

        // BOUNDS BEFORE VALUE, always. A Slider coerces its Value into [Minimum, Maximum] the moment
        // either bound changes, so publishing Position first — with the OLD bounds still applied —
        // clamps the thumb to a range that is about to be replaced, and the coerced value writes back.
        // That ordering is the fix recorded in src/Ui/Match/RESOLVED.md, restated here because it is
        // invisible and because getting it wrong costs the user their redo stack.
        OnPropertyChanged(nameof(Minimum));
        OnPropertyChanged(nameof(Maximum));
        OnPropertyChanged(nameof(MinEntry));
        OnPropertyChanged(nameof(MaxEntry));
        OnPropertyChanged(nameof(Position));
    }
}
