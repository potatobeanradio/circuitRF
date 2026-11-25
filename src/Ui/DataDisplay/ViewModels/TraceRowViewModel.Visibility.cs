// ================================================================
//  TraceRowViewModel.Visibility.cs  —  a trace shown by a control
//  (brief-ddl-6 R-ddl6-5)
//
//  The card's eye button binds the trace's visibility to a display
//  control: shown while its value is non-zero, or while it is zero when
//  inverted. A button then switches a group of traces with no
//  expression. The menu is built per click, so it lists the controls the
//  document holds now. The binding is saved (TraceConfig.ShownBy,
//  ShownByInverted); whether the trace is hidden at this moment is
//  worked out by LinkResolve, which `render` runs too.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using Material.Icons;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class TraceRowViewModel
{
    /// <summary>The eye: open while the trace always shows, a closed eye while a control hides it now, and a dashed
    /// one while a control shows it.</summary>
    public MaterialIconKind VisibilityIcon
        => _trace.ShownBy is null ? MaterialIconKind.EyeOutline
         : _trace.HiddenByControl ? MaterialIconKind.EyeOffOutline
         : MaterialIconKind.EyeCheckOutline;

    public string VisibilityTip
        => _trace.ShownBy is null ? "Shown always. Click to show it only while a control is on"
         : $"Shown while {_trace.ShownByName ?? "its control"} is {(_trace.ShownByInverted ? "off (zero)" : "on (non-zero)")}"
           + (_trace.HiddenByControl ? "; hidden now" : "");

    /// <summary>The menu: Always, then one entry per control of the document, then Inverted.</summary>
    public IReadOnlyList<StatisticsMenuEntry> VisibilityMenu()
    {
        var list = new List<StatisticsMenuEntry>
        {
            new("Always shown", () => SetShownBy(null, false), Checked: _trace.ShownBy is null),
        };
        var doc = _parent.LinkDocumentProvider?.Invoke();
        var controls = doc?.Controls().Select(x => x.Control).Where(ControlLinkSource.IsSource)
                          .OrderBy(c => c is ButtonControl ? 0 : 1).ThenBy(c => c.Name, System.StringComparer.Ordinal)
                          .ToList() ?? [];
        if (controls.Count == 0)
            list.Add(new("No controls to show it by", Tooltip: "Add a button, knob or slider from Controls"));
        foreach (var c in controls)
        {
            bool on = string.Equals(_trace.ShownBy, c.Id, System.StringComparison.OrdinalIgnoreCase)
                   || string.Equals(_trace.ShownBy, c.Name, System.StringComparison.Ordinal);
            list.Add(new($"Shown by {c.Name}", () => SetShownBy(c.Id, _trace.ShownByInverted), Checked: on));
        }
        if (_trace.ShownBy is not null)
            list.Add(new("Inverted: shown while it is off", () => SetShownBy(_trace.ShownBy, !_trace.ShownByInverted),
                         Checked: _trace.ShownByInverted));
        return list;
    }

    /// <summary>Binds the trace's visibility to <paramref name="source"/> (null: always shown), and resolves it at once.</summary>
    internal void SetShownBy(string? source, bool inverted)
    {
        if (_trace.ShownBy == source && _trace.ShownByInverted == inverted) return;
        _trace.ShownBy = source;
        _trace.ShownByInverted = source is not null && inverted;
        if (_parent.LinkDocumentProvider?.Invoke() is { } doc)
            LinkResolve.Resolve(doc, new LibraryDataSources(_parent.Library));
        else
            _trace.HiddenByControl = false;
        RefreshVisibility();
        _parent.RebuildAndNotify();
    }

    /// <summary>The eye follows the trace's shown state — after a link follow as well as after an edit.</summary>
    internal void RefreshVisibility()
    {
        OnPropertyChanged(nameof(VisibilityIcon));
        OnPropertyChanged(nameof(VisibilityTip));
    }
}
