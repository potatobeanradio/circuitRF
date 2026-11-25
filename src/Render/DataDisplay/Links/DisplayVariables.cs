// ================================================================
//  DisplayVariables.cs  —  a display's variables: list, get, set,
//  subscribe (brief-ddl-7, the seam)
//
//  A display variable is a link source: a marker, a slider, a knob, a
//  button, or the trial selection. This is the ONE place they are
//  listed, read and set, below the firewall, so nothing about a variable
//  is reachable only through a view model: `circuitrf render --marker /
//  --control` sets through it, and the window holds one over its live
//  tabs. A later display-bound plugin session is handed this, never the
//  window.
//
//  SETTING IS THE SAME ACT WHOEVER DOES IT. The value is applied by the
//  code the canvas applies it with (Trace.PlaceMarkerAtSample,
//  ControlLinkSource.SetFromText), wrapped by the host's writer: the
//  window's records the change with the commands its own gestures use
//  (an undo step, a dirty mark, a live follow); headless, there is
//  nothing to record. There is no second write path to drift.
//
//  Subscribe is driven by the host: it calls Publish after anything may
//  have moved (the window after every link follow), and each variable
//  whose value differs from the last publication is reported once.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircuitRF.Render.DataDisplay;

public enum DisplayVariableKind { Marker, Slider, Knob, Button, Selection }

/// <summary>One display variable as it now stands.</summary>
/// <param name="Id">What a link names it by: a marker's or a control's id, or <c>selected</c>.</param>
/// <param name="Value">Its value in base SI (a marker's X, a control's value, a button's 0 or 1, the lowest trial
/// selected), or null when it has none to give (a free point on the Smith chart, nothing selected).</param>
/// <param name="Unit">The base unit of <paramref name="Value"/>: <c>Hz</c>, <c>dBm</c>, or empty.</param>
/// <param name="Index">The sample or stop it is on, when it is on one.</param>
/// <param name="Text">The value as the display writes it: <c>2.4 GHz</c>, <c>On</c>.</param>
public sealed record DisplayVariable(string Id, string Name, DisplayVariableKind Kind, int TabIndex, string TabName,
                                     double? Value, string Unit, int? Index, string Text);

public enum VariableSetStatus { Done, Unknown, Refused }

/// <summary>What a set came to. <see cref="VariableSetStatus.Unknown"/> is a name that resolves to no settable variable
/// (or to several); <see cref="VariableSetStatus.Refused"/> is a value it cannot take.</summary>
public readonly record struct VariableSetResult(VariableSetStatus Status, string? Reason = null)
{
    public bool Done => Status == VariableSetStatus.Done;
    public static VariableSetResult Ok => new(VariableSetStatus.Done);
}

/// <summary>
/// How a set reaches the document: the host applies <c>change</c> — the value, set by the code the canvas sets it with —
/// inside whatever its own gestures do around a change. <c>change</c> returns false when the value was refused, and the
/// host then records nothing.
/// </summary>
public interface IDisplayVariableWriter
{
    bool WriteControl(DisplayControl control, Func<bool> change);
    bool WriteMarker(LinkDocument.MarkerSite site, Func<bool> change);
}

public sealed partial class DisplayVariables
{
    private readonly Func<LinkDocument> _document;
    private readonly Func<IPlotDataSources?> _sources;
    private readonly IDisplayVariableWriter? _writer;
    private readonly List<Action<DisplayVariable>> _subscribers = [];
    private Dictionary<string, DisplayVariable>? _published;

    /// <param name="document">The display as the links see it, built afresh on each call — so a variable added or
    /// removed since is seen.</param>
    /// <param name="sources">The display's data, which a bound control's stops and a marker's coordinates are read
    /// from.</param>
    /// <param name="writer">The host's own way of making a change; null applies it with nothing around it (a render).</param>
    public DisplayVariables(Func<LinkDocument> document, Func<IPlotDataSources?>? sources = null, IDisplayVariableWriter? writer = null)
    {
        _document = document;
        _sources  = sources ?? (() => null);
        _writer   = writer;
    }

    // ---- list and get -----------------------------------------------------------------------------

    /// <summary>Every variable of the display: its markers, its input controls (a readout shows a variable and is not
    /// one) and the trial selection.</summary>
    public IReadOnlyList<DisplayVariable> List()
    {
        var doc = _document();
        var sources = _sources();
        var list = new List<DisplayVariable>();
        foreach (var site in doc.Markers()) list.Add(OfMarker(doc, site, sources));
        foreach (var (tab, c) in doc.Controls())
            if (ControlLinkSource.IsSource(c)) list.Add(OfControl(doc, tab, c, sources));
        list.Add(OfSelection(doc));
        return list;
    }

    /// <summary>The variable with this id, else this name (a name used twice is no answer), or null.</summary>
    public DisplayVariable? Get(string nameOrId)
    {
        var all = List();
        var byId = all.FirstOrDefault(v => string.Equals(v.Id, nameOrId, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var named = all.Where(v => string.Equals(v.Name, nameOrId, StringComparison.Ordinal)).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static DisplayVariable OfMarker(LinkDocument doc, LinkDocument.MarkerSite site, IPlotDataSources? sources)
    {
        var value = MarkerLinkSource.Resolve(site, sources);
        var c = value.Coordinates.FirstOrDefault();
        var (scale, unit) = SiUnit.Split(c?.Unit);
        double? v = c is null ? null : c.Value * scale;
        string text = v is { } x ? LinkMatch.Format(x, unit)
                    : value.Position is { } p ? $"Γ {p.Gamma.Real:0.###}{(p.Gamma.Imaginary < 0 ? "−" : "+")}j{Math.Abs(p.Gamma.Imaginary):0.###}"
                    : "";
        return new DisplayVariable(site.Marker.Id.ToString("D"), site.Marker.Name, DisplayVariableKind.Marker, site.TabIndex,
                                   doc.Tabs[site.TabIndex].Name, v, unit, c?.Index, text);
    }

    private static DisplayVariable OfControl(LinkDocument doc, int tab, DisplayControl c, IPlotDataSources? sources)
    {
        if (c is ButtonControl b)
            return new DisplayVariable(c.Id, c.Name, DisplayVariableKind.Button, tab, doc.Tabs[tab].Name, b.NumericValue, "",
                                       b.On ? 1 : 0, b.On ? "On" : "Off");
        var s = (SteppedControl)c;
        if (s.Domain == SliderDomain.Bound) ControlLinkSource.Refresh(s, sources);
        var (scale, unit) = SiUnit.Split(s.DisplayUnit);
        return new DisplayVariable(c.Id, c.Name, c is KnobControl ? DisplayVariableKind.Knob : DisplayVariableKind.Slider,
                                   tab, doc.Tabs[tab].Name, s.CurrentValue * scale, unit, s.StopIndex,
                                   ControlRenderer.Format(s, s.CurrentValue, s.DisplayUnit));
    }

    private static DisplayVariable OfSelection(LinkDocument doc)
    {
        var selected = doc.Traces().Select(x => x.Trace.SourcePath).Distinct()
                          .Select(p => doc.SelectedTrials?.Invoke(p)).FirstOrDefault(s => s is { Count: > 0 });
        int? lowest = selected?.Min();
        return new DisplayVariable(LinkDocument.SelectionId, LinkDocument.SelectionId, DisplayVariableKind.Selection, -1, "",
                                   lowest, "", lowest, lowest is { } t ? $"trial {t}" : "none");
    }

    // ---- set --------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the variable named (or with this id) from <paramref name="valueText"/>: <c>2.4GHz</c> (the nearest sample or
    /// stop; a unit is compared in base SI), a bare number (in the variable's own unit), or <c>#5</c> (that sample or
    /// stop). A button takes 0 or 1.
    /// </summary>
    public VariableSetResult Set(string nameOrId, string valueText)
    {
        if (ParseValue(valueText) is not { } v)
            return new(VariableSetStatus.Refused, $"'{valueText}' is not a value: write a number with or without its unit, or #index.");
        var doc = _document();
        if (doc.FindControl(nameOrId) is { } byId) return SetControl(byId, v.Index, v.Value, v.Unit);
        if (doc.FindMarker(nameOrId) is { } site) return SetMarker(site, v.Index, v.Value, v.Unit);
        if (LinkDocument.IsSelection(nameOrId)) return new(VariableSetStatus.Refused, "The trial selection is set by selecting trials.");
        bool isControl = doc.Controls().Any(x => string.Equals(x.Control.Name, nameOrId, StringComparison.Ordinal));
        return isControl ? SetControl(nameOrId, v.Index, v.Value, v.Unit) : SetMarker(nameOrId, v.Index, v.Value, v.Unit);
    }

    /// <summary>Sets the control named <paramref name="name"/> — unique across the document — to stop
    /// <paramref name="index"/>, or to the stop nearest <paramref name="value"/> in <paramref name="unit"/> (base SI; empty
    /// is the control's own unit).</summary>
    public VariableSetResult SetControl(string name, int? index, double value, string unit)
    {
        var doc = _document();
        var named = doc.Controls().Where(x => x.Control.Name == name).Select(x => x.Control).ToList();
        if (named.Count == 1) return SetControl(named[0], index, value, unit);
        var all = doc.Controls().Select(x => x.Control.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return new(VariableSetStatus.Unknown, named.Count > 1
            ? $"'{name}' names {named.Count} controls; rename one."
            : all.Count == 0 ? "this display has no controls." : $"no control '{name}'. Controls: {string.Join(", ", all)}.");
    }

    private VariableSetResult SetControl(DisplayControl control, int? index, double value, string unit)
    {
        string? why = null;
        bool Change() => (why = ControlLinkSource.SetFromText(control, index, value, unit)) is null;
        if (_writer is { } w) w.WriteControl(control, Change);
        else Change();
        return why is null ? VariableSetResult.Ok : new(VariableSetStatus.Refused, why);
    }

    /// <summary>Puts the marker named <paramref name="name"/> on sample <paramref name="index"/> of its trace, or on the
    /// sample nearest <paramref name="value"/> in <paramref name="unit"/> (the trace's X unit; empty takes it as written).</summary>
    public VariableSetResult SetMarker(string name, int? index, double value, string unit)
    {
        var doc = _document();
        if (!doc.TryFindByName(name, consumerTab: -1, out var site, out var why))
            return new(VariableSetStatus.Unknown, why);
        return SetMarker(site, index, value, unit);
    }

    private VariableSetResult SetMarker(LinkDocument.MarkerSite site, int? index, double value, string unit)
    {
        var t = site.Trace;
        string name = site.Marker.Name;
        if (site.Marker.FreePosition || t.IsContourTrace)
            return new(VariableSetStatus.Refused, $"{name} is placed freely on the chart, not on a sample.");
        // A trace that draws nothing has no axis to name in a refusal, and no sample to stand on.
        int count = t.IsCubeBound ? t.CubeXValues?.Count ?? 0 : t.Data?.Frequencies?.Length ?? 0;
        if (count == 0) return new(VariableSetStatus.Refused, $"{name}'s trace draws nothing to put it on.");
        int sample;
        if (index is { } i) sample = i;
        else
        {
            string axisUnit = t.IsCubeBound ? t.CubeXUnit ?? "" : "Hz";
            if (unit.Length > 0 && !string.Equals(unit, axisUnit, StringComparison.Ordinal))
                return new(VariableSetStatus.Refused,
                           $"{name} is on {(t.IsCubeBound ? t.CubeXAxisName : "freq")}"
                         + (axisUnit.Length > 0 ? $" ({axisUnit})" : ", which has no unit") + $", not in {unit}.");
            sample = t.NearestSampleTo(value);
        }
        bool Change() => t.PlaceMarkerAtSample(site.Marker, sample);
        bool placed = _writer is { } w ? w.WriteMarker(site, Change) : Change();
        if (placed) return VariableSetResult.Ok;
        return new(VariableSetStatus.Refused, $"{name}'s trace has samples 0..{count - 1}.");
    }

    // ---- the value grammar (R-ddl1-10) -------------------------------------------------------------

    [GeneratedRegex(@"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(\S*)\s*$")]
    private static partial Regex NumberWithUnit();

    /// <summary>
    /// <c>#17</c> → index 17; <c>2.4GHz</c> → 2.4e9 Hz; <c>-3 dBm</c> → −3 dBm; <c>5</c> → 5 with no unit. Null for
    /// anything else. Invariant culture, as every number on a command line is.
    /// </summary>
    public static (int? Index, double Value, string Unit)? ParseValue(string text)
    {
        string rhs = (text ?? "").Trim();
        if (rhs.StartsWith('#'))
            return int.TryParse(rhs[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int idx) ? (idx, 0, "") : null;
        var m = NumberWithUnit().Match(rhs);
        if (!m.Success) return null;
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var (scale, unit) = SplitPrefix(m.Groups[2].Value);
        return (null, v * scale, unit);
    }

    /// <summary>"GHz" → (1e9, "Hz"); "dBm" → (1, "dBm"); "k" → (1e3, ""); "" → (1, "").</summary>
    private static (double Scale, string Unit) SplitPrefix(string s)
    {
        if (s.Length == 0) return (1, "");
        double? p = s[0] switch
        {
            'f' => 1e-15, 'p' => 1e-12, 'n' => 1e-9, 'u' or 'µ' => 1e-6, 'm' => 1e-3,
            'k' => 1e3, 'M' => 1e6, 'G' => 1e9, 'T' => 1e12, _ => null,
        };
        // A prefix needs a unit after it, or to stand alone; "dB", "Hz", "K" are units of their own.
        return p is { } scale && (s.Length == 1 || char.IsUpper(s[1]) || s[1..] is "s" or "m" or "g")
            ? (scale, s[1..]) : (1, s);
    }

    // ---- subscribe --------------------------------------------------------------------------------

    /// <summary><paramref name="changed"/> is called with each variable whose value moved, added or not, once per
    /// <see cref="Publish"/>. Dispose the result to stop.</summary>
    public IDisposable Subscribe(Action<DisplayVariable> changed)
    {
        _subscribers.Add(changed);
        _published ??= List().ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        return new Unsubscribe(this, changed);
    }

    /// <summary>
    /// The host's word that variables may have moved: each one whose value, sample or text differs from the last
    /// publication — or that is new — is reported to every subscriber. A variable that went away is not reported; a
    /// list says what is there.
    /// </summary>
    public void Publish()
    {
        if (_subscribers.Count == 0) { _published = null; return; }
        var now = List().ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        var was = _published ?? now;
        _published = now;
        foreach (var v in now.Values)
            if (!was.TryGetValue(v.Id, out var old) || old != v)
                foreach (var s in _subscribers.ToList()) s(v);
    }

    private sealed class Unsubscribe(DisplayVariables owner, Action<DisplayVariable> action) : IDisposable
    {
        public void Dispose() => owner._subscribers.Remove(action);
    }
}
