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

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(SliderControl), "Slider")]
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
/// A slider (R-ddl5-3). Its value is always one of its stops: a BOUND slider's stops are one axis's samples, so its
/// value is a sample and it supplies exactly the coordinate a marker on that axis would; a RANGE slider's stops are
/// its own, and its value is a number with a unit.
/// </summary>
public sealed class SliderControl : DisplayControl
{
    public override string KindName => "slider";

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

    /// <summary>The axis's samples as they were last read. A source that is gone keeps them (R-ddl5-7), so the slider
    /// still has its range and its value, and says so.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? BoundValues { get; set; }

    /// <summary>The axis's unit as last read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundUnit { get; set; }

    /// <summary>The stop a bound slider is on — an index into the axis.</summary>
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
    /// <summary>A range slider's value, in <see cref="Unit"/>.</summary>
    public double Value { get; set; }

    // ---- look ----

    public ControlOrientation Orientation { get; set; } = ControlOrientation.Horizontal;
    public bool ShowTicks { get; set; } = true;
    public bool ShowEnds  { get; set; } = true;
    public bool ShowValue { get; set; } = true;

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
        CopyBase(o);
        Domain = o.Domain; SourcePath = o.SourcePath; CubeName = o.CubeName; AxisName = o.AxisName;
        BoundValues = o.BoundValues is null ? null : (double[])o.BoundValues.Clone();
        BoundUnit = o.BoundUnit; Index = o.Index;
        Start = o.Start; Stop = o.Stop; Step = o.Step; Points = o.Points; Unit = o.Unit; Log = o.Log; Value = o.Value;
        Orientation = o.Orientation; ShowTicks = o.ShowTicks; ShowEnds = o.ShowEnds; ShowValue = o.ShowValue;
    }

    // ---- stops ----

    /// <summary>The most stops a continuous or very fine range is drawn and stepped with.</summary>
    public const int ContinuousStops = 1001;

    /// <summary>
    /// The values the slider can take, in order, in the unit it shows (<see cref="DisplayUnit"/>): a bound slider's
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

    /// <summary>The unit the stops and the readout are in: the axis's for a bound slider, the range's own otherwise.</summary>
    [JsonIgnore]
    public string DisplayUnit => Domain == SliderDomain.Bound ? BoundUnit ?? "" : Unit;

    /// <summary>The stop the slider is on.</summary>
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

    /// <summary>Puts the slider on stop <paramref name="index"/> (clamped). True when it moved.</summary>
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
