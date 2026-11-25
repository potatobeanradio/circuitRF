// ================================================================
//  DisplayControl.cs  —  an input placed on the Data Display canvas
//  (brief-ddl-5 R-ddl5-1 … R-ddl5-3, R-ddl5-9; overview D6, D8)
//
//  A control is a display variable, not a marker: it is a link source of
//  its own kind, resolved through the same LinkValue. Below the firewall,
//  so the window, Copy/Export and `circuitrf render` read one model.
//
//  The model IS the file format (TabConfig.Controls), polymorphic in the
//  CanvasAnnotation pattern — a discriminator "Kind", so the knob, the
//  button and the readout (DDL-6) are additive.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace CircuitRF.Render.DataDisplay;

/// <summary>A control's size preset. <see cref="Custom"/> is what the gripper leaves (R-ddl5-2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControlSize>))]
public enum ControlSize { Small, Medium, Large, Custom }

[JsonConverter(typeof(JsonStringEnumConverter<ControlOrientation>))]
public enum ControlOrientation { Horizontal, Vertical }

/// <summary>What a slider's stops are: one data axis's samples, or a range of its own (R-ddl5-3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SliderDomain>))]
public enum SliderDomain { Range, Bound }

/// <summary>What a button does when its face is pressed (brief-ddl-6 R-ddl6-2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ButtonMode>))]
public enum ButtonMode { Toggle, Momentary }

/// <summary>Where a button's label sits.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ButtonLabelPlacement>))]
public enum ButtonLabelPlacement { Under, OnFace }

/// <summary>What a readout shows of its source (brief-ddl-6 R-ddl6-3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadoutShow>))]
public enum ReadoutShow { Value, Coordinate, Reading }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(SliderControl), "Slider")]
[JsonDerivedType(typeof(KnobControl), "Knob")]
[JsonDerivedType(typeof(ButtonControl), "Button")]
[JsonDerivedType(typeof(ReadoutControl), "Readout")]
public abstract class DisplayControl
{
    /// <summary>The id a link names — persistent, so renaming keeps links (overview D1).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary><c>slider1</c>: unique across the document, the spelling <c>@slider1</c> takes.</summary>
    public string Name { get; set; } = "";

    /// <summary>What the frame's label strip shows; the name when unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>Position and size in display logical units — the space a plot's Left/Top are in.</summary>
    public double Left   { get; set; }
    public double Top    { get; set; }
    public double Width  { get; set; }
    public double Height { get; set; }

    public ControlSize Size { get; set; } = ControlSize.Medium;

    /// <summary><c>#rrggbb</c>; the theme's accent when unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accent { get; set; }

    /// <summary>Lock Position (R-ddl5-9): neither moved nor resized, still operated. Saved, so a locked panel reopens
    /// locked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }

    /// <summary>The kind's word, which new names are numbered from: <c>slider</c>.</summary>
    [JsonIgnore] public abstract string KindName { get; }

    /// <summary>The frame's text: the label, else the name.</summary>
    [JsonIgnore] public string Caption => string.IsNullOrWhiteSpace(Label) ? Name : Label!;

    /// <summary>The fixed size a preset gives this kind, for its orientation.</summary>
    public abstract (double Width, double Height) PresetSize(ControlSize size);

    /// <summary>The smallest frame the gripper leaves: the Small preset, the least most kinds stay legible at.</summary>
    public virtual (double Width, double Height) MinimumSize() => PresetSize(ControlSize.Small);

    /// <summary>Sets <see cref="Size"/> and, for a preset, the frame to its size.</summary>
    public void ApplySize(ControlSize size)
    {
        Size = size;
        if (size == ControlSize.Custom) return;
        (Width, Height) = PresetSize(size);
    }

    /// <summary>A deep copy — what undo snapshots and a copy hold.</summary>
    public abstract DisplayControl Clone();

    /// <summary>Takes every field of <paramref name="other"/>, a snapshot of the same kind.</summary>
    public abstract void CopyFrom(DisplayControl other);

    protected void CopyBase(DisplayControl o)
    {
        Id = o.Id; Name = o.Name; Label = o.Label;
        Left = o.Left; Top = o.Top; Width = o.Width; Height = o.Height;
        Size = o.Size; Accent = o.Accent; Locked = o.Locked;
    }

    /// <summary>The lowest <c>{kind}{n}</c> no control in <paramref name="existing"/> uses.</summary>
    public static string NextName(string kind, IEnumerable<DisplayControl> existing)
    {
        var used = new HashSet<int>();
        foreach (var c in existing)
            if (c.Name.StartsWith(kind, StringComparison.Ordinal) && int.TryParse(c.Name.AsSpan(kind.Length), out int n))
                used.Add(n);
        int i = 1;
        while (used.Contains(i)) i++;
        return kind + i.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A control whose value is one of a list of stops (R-ddl5-3): the slider and the knob (brief-ddl-6 R-ddl6-1), which
/// share the two domains and every value rule. A BOUND control's stops are one axis's samples, so its value is a sample
/// and it supplies exactly the coordinate a marker on that axis would; a RANGE control's stops are its own, and its
/// value is a number with a unit.
/// </summary>
public abstract class SteppedControl : DisplayControl
{
    public SliderDomain Domain { get; set; } = SliderDomain.Range;

    // ---- bound to an axis ----

    /// <summary>The source file, as a trace names it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourcePath { get; set; }

    /// <summary>The cube, group-qualified as a trace names it: <c>Pout</c>, <c>HB1.V</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CubeName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AxisName { get; set; }

    /// <summary>The axis's samples as they were last read. A source that is gone keeps them (R-ddl5-7), so the control
    /// still has its range and its value, and says so.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? BoundValues { get; set; }

    /// <summary>The axis's unit as last read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundUnit { get; set; }

    /// <summary>The stop a bound control is on — an index into the axis.</summary>
    public int Index { get; set; }

    // ---- a range of its own ----

    public double Start { get; set; } = 0;
    public double Stop  { get; set; } = 10;
    /// <summary>The spacing of a linear range; 0 with no <see cref="Points"/> is continuous.</summary>
    public double Step  { get; set; } = 1;
    /// <summary>A point count, which wins over <see cref="Step"/>; a log range needs one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Points { get; set; }
    /// <summary>The unit the range is written in — <c>MHz</c>, <c>dBm</c>, <c>V</c>, or none.</summary>
    public string Unit { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Log { get; set; }
    /// <summary>A range control's value, in <see cref="Unit"/>.</summary>
    public double Value { get; set; }

    // ---- look ----

    /// <summary>Whether the name or label is drawn in the control. Off, the label strip is given up and the rest moves
    /// into its space.</summary>
    public bool ShowLabel { get; set; } = true;
    public bool ShowTicks { get; set; } = true;
    public bool ShowEnds  { get; set; } = true;
    public bool ShowValue { get; set; } = true;

    protected void CopyStepped(SteppedControl o)
    {
        CopyBase(o);
        Domain = o.Domain; SourcePath = o.SourcePath; CubeName = o.CubeName; AxisName = o.AxisName;
        BoundValues = o.BoundValues is null ? null : (double[])o.BoundValues.Clone();
        BoundUnit = o.BoundUnit; Index = o.Index;
        Start = o.Start; Stop = o.Stop; Step = o.Step; Points = o.Points; Unit = o.Unit; Log = o.Log; Value = o.Value;
        ShowLabel = o.ShowLabel; ShowTicks = o.ShowTicks; ShowEnds = o.ShowEnds; ShowValue = o.ShowValue;
    }

    // ---- stops ----

    /// <summary>The most stops a continuous or very fine range is drawn and stepped with.</summary>
    public const int ContinuousStops = 1001;

    /// <summary>
    /// The values the control can take, in order, in the unit it shows (<see cref="DisplayUnit"/>): a bound control's
    /// axis samples (as last read), else the range's own stops. Never empty.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<double> Stops
    {
        get
        {
            if (Domain == SliderDomain.Bound) return BoundValues is { Length: > 0 } b ? b : [0.0];
            double lo = Start, hi = Stop;
            int n;
            if (Points is > 1 and var p) n = p;
            else if (!Log && Step > 0 && hi != lo) n = (int)Math.Min(ContinuousStops, Math.Floor(Math.Abs(hi - lo) / Step + 1e-9) + 1);
            else n = ContinuousStops;
            if (n < 2 || hi == lo) return [lo];
            var s = new double[n];
            bool log = Log && lo > 0 && hi > 0;
            for (int i = 0; i < n; i++)
            {
                double f = (double)i / (n - 1);
                s[i] = log ? lo * Math.Pow(hi / lo, f)
                     : Points is > 1 || Step <= 0 ? lo + f * (hi - lo)
                     : lo + Math.Sign(hi - lo) * i * Step;
            }
            return s;
        }
    }

    /// <summary>The unit the stops and the readout are in: the axis's for a bound control, the range's own otherwise.</summary>
    [JsonIgnore]
    public string DisplayUnit => Domain == SliderDomain.Bound ? BoundUnit ?? "" : Unit;

    /// <summary>The stop the control is on.</summary>
    [JsonIgnore]
    public int StopIndex
    {
        get
        {
            var s = Stops;
            if (Domain == SliderDomain.Bound) return Math.Clamp(Index, 0, s.Count - 1);
            return NearestStop(s, Value);
        }
    }

    /// <summary>The value at <see cref="StopIndex"/>, in <see cref="DisplayUnit"/>.</summary>
    [JsonIgnore]
    public double CurrentValue => Stops[StopIndex];

    /// <summary>Where the control is along its stops, 0 at the first and 1 at the last — the knob's angle.</summary>
    [JsonIgnore]
    public double Fraction
    {
        get
        {
            int n = Stops.Count;
            return n > 1 ? (double)StopIndex / (n - 1) : 0;
        }
    }

    /// <summary>Puts the control on the stop nearest fraction <paramref name="f"/> of the way along (clamped). True when
    /// it moved.</summary>
    public bool SetFraction(double f)
    {
        int n = Stops.Count;
        return SetStop((int)Math.Round(Math.Clamp(f, 0, 1) * (n - 1)));
    }

    /// <summary>Puts the control on stop <paramref name="index"/> (clamped). True when it moved.</summary>
    public bool SetStop(int index)
    {
        var s = Stops;
        index = Math.Clamp(index, 0, s.Count - 1);
        if (Domain == SliderDomain.Bound)
        {
            if (Index == index) return false;
            Index = index;
            return true;
        }
        if (Value == s[index]) return false;
        Value = s[index];
        return true;
    }

    /// <summary>The stop nearest <paramref name="v"/>.</summary>
    public static int NearestStop(IReadOnlyList<double> stops, double v)
    {
        int best = 0; double bestD = double.PositiveInfinity;
        for (int i = 0; i < stops.Count; i++)
        {
            double d = Math.Abs(stops[i] - v);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}

/// <summary>A slider (R-ddl5-3): a stepped control drawn as a track and a thumb, horizontal or vertical.</summary>
public sealed class SliderControl : SteppedControl
{
    public override string KindName => "slider";

    public ControlOrientation Orientation { get; set; } = ControlOrientation.Horizontal;

    public override (double Width, double Height) PresetSize(ControlSize size)
    {
        var (w, h) = size switch
        {
            ControlSize.Small => (200.0, 60.0),
            ControlSize.Large => (360.0, 100.0),
            _                 => (240.0, 68.0),
        };
        return Orientation == ControlOrientation.Vertical ? (h * 1.25, w) : (w, h);
    }

    public override DisplayControl Clone()
    {
        var c = new SliderControl();
        c.CopyFrom(this);
        return c;
    }

    public override void CopyFrom(DisplayControl other)
    {
        var o = (SliderControl)other;
        CopyStepped(o);
        Orientation = o.Orientation;
    }
}

/// <summary>
/// A knob (brief-ddl-6 R-ddl6-1): the slider's two domains and value rules, drawn as a dial over a 270° sweep. A
/// drag turns it (up or right increases), the wheel steps it, and a click on a bound knob's tick sets that stop.
/// </summary>
public sealed class KnobControl : SteppedControl
{
    public override string KindName => "knob";

    /// <summary>The sweep of the dial, in degrees, from its first stop to its last.</summary>
    public const double SweepDegrees = 270;

    /// <summary>A drag of this many pixels (up plus right) turns the knob through its whole sweep; ten times as many with
    /// Ctrl.</summary>
    public const double PixelsPerSweep = 200;

    public override (double Width, double Height) PresetSize(ControlSize size) => size switch
    {
        ControlSize.Small => (96.0, 112.0),
        ControlSize.Large => (164.0, 188.0),
        _                 => (120.0, 140.0),
    };

    public override DisplayControl Clone()
    {
        var c = new KnobControl();
        c.CopyFrom(this);
        return c;
    }

    public override void CopyFrom(DisplayControl other) => CopyStepped((KnobControl)other);
}

/// <summary>
/// An illuminated button (brief-ddl-6 R-ddl6-2). <see cref="ButtonMode.Toggle"/> flips on a click;
/// <see cref="ButtonMode.Momentary"/> is on only while pressed. Its value is 0 or 1, with no unit, and the illumination
/// colour is the control's accent.
/// </summary>
public sealed class ButtonControl : DisplayControl
{
    public override string KindName => "button";

    public ButtonMode Mode { get; set; } = ButtonMode.Toggle;

    public ButtonLabelPlacement LabelPlacement { get; set; } = ButtonLabelPlacement.Under;

    /// <summary>Lit. Saved for a toggle, so a display reopens as it was left; a momentary button is never saved lit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool On { get; set; }

    /// <summary>The value it supplies: 1 lit, 0 dark.</summary>
    [JsonIgnore] public double NumericValue => On ? 1 : 0;

    /// <summary>The eight colours the inspector offers before "any colour".</summary>
    public static readonly string[] Presets =
        ["#ff3b30", "#ff9500", "#ffcc00", "#34c759", "#00c7be", "#0a84ff", "#af52de", "#f2f2f7"];

    public override (double Width, double Height) PresetSize(ControlSize size)
    {
        double k = size switch { ControlSize.Small => 0.8, ControlSize.Large => 1.35, _ => 1.0 };
        return LabelPlacement == ButtonLabelPlacement.Under ? (96 * k, 112 * k) : (96 * k, 96 * k);
    }

    /// <summary>A lamp needs no room for a track or digits, so it goes far below the Small preset: a small indicator
    /// beside a plot. Under that, its label is given up rather than drawn too small to read.</summary>
    public override (double Width, double Height) MinimumSize() => (24, 24);

    public override DisplayControl Clone()
    {
        var c = new ButtonControl();
        c.CopyFrom(this);
        return c;
    }

    public override void CopyFrom(DisplayControl other)
    {
        var o = (ButtonControl)other;
        CopyBase(o);
        Mode = o.Mode; LabelPlacement = o.LabelPlacement; On = o.On;
    }
}

/// <summary>
/// A readout (brief-ddl-6 R-ddl6-3): an indicator with no input. It shows a link source's value as text — a marker's,
/// a control's, <c>selected</c> — with its unit and SI prefix; or one coordinate the source supplies; or a marker's
/// reading on its own trace (its info box's value line). What it shows is worked out where links are resolved
/// (<see cref="ReadoutResolve"/>), so the window, Copy/Export and <c>render</c> show the same text.
/// </summary>
public sealed class ReadoutControl : DisplayControl
{
    public override string KindName => "readout";

    /// <summary>The source's id (a marker's or a control's), or <c>selected</c>. A link follows the id, so renaming the
    /// source keeps it (overview D1).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>The source's name as last read — what a hand-written file names, and what is shown when it is gone.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceName { get; set; }

    public ReadoutShow Show { get; set; } = ReadoutShow.Value;

    /// <summary>The coordinate shown with <see cref="ReadoutShow.Coordinate"/>: an axis the source supplies.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Axis { get; set; }

    /// <summary>The name line above the value: the label, else <c>m1 · Vgs</c>.</summary>
    public bool ShowName { get; set; } = true;

    /// <summary>The value as last resolved, with its unit. Not saved: worked out again on open.</summary>
    [JsonIgnore] public string? Text { get; set; }

    /// <summary>What the name line says when there is no label: <c>m1</c>, <c>m1 · Vgs</c>.</summary>
    [JsonIgnore] public string? NameText { get; set; }

    /// <summary>Why there is nothing to show — no source chosen, or the source is gone. Null when <see cref="Text"/> is
    /// current.</summary>
    [JsonIgnore] public string? Problem { get; set; }

    public override (double Width, double Height) PresetSize(ControlSize size) => size switch
    {
        ControlSize.Small => (150.0, 48.0),
        ControlSize.Large => (280.0, 88.0),
        _                 => (196.0, 62.0),
    };

    public override DisplayControl Clone()
    {
        var c = new ReadoutControl();
        c.CopyFrom(this);
        return c;
    }

    public override void CopyFrom(DisplayControl other)
    {
        var o = (ReadoutControl)other;
        CopyBase(o);
        Source = o.Source; SourceName = o.SourceName; Show = o.Show; Axis = o.Axis; ShowName = o.ShowName;
        Text = o.Text; NameText = o.NameText; Problem = o.Problem;
    }
}

/// <summary>An SI prefix split off a unit: <c>MHz</c> → (1e6, <c>Hz</c>).</summary>
public static class SiUnit
{
    /// <summary>"GHz" → (1e9, "Hz"); "dBm" → (1, "dBm"); "mV" → (1e-3, "V"); "" → (1, "").</summary>
    public static (double Scale, string Base) Split(string? unit)
    {
        string s = unit ?? "";
        if (s.Length < 2) return (1, s);
        double? p = s[0] switch
        {
            'f' => 1e-15, 'p' => 1e-12, 'n' => 1e-9, 'u' or 'µ' => 1e-6, 'm' => 1e-3,
            'k' => 1e3, 'M' => 1e6, 'G' => 1e9, 'T' => 1e12, _ => null,
        };
        // A prefix needs a unit after it; "dB", "Hz", "K" and "mm"'s second m are units of their own.
        return p is { } scale && (char.IsUpper(s[1]) || s[1..] is "s" or "m" or "g") ? (scale, s[1..]) : (1, s);
    }
}
