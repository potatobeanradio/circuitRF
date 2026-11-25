// ================================================================
//  ReadoutResolve.cs  —  what each readout shows (brief-ddl-6 R-ddl6-3)
//
//  Run at the end of LinkResolve, so the window, Copy/Export and
//  `circuitrf render` show the same text: a readout reads its source as
//  the traces it sits on now stand. A marker's value and reading are its
//  info box's own lines (Trace.BuildMarkerBoxLines), so a readout says
//  what the box says, unit and prefix included; a control's value is
//  written as the control writes it.
// ================================================================

using System.Linq;

namespace CircuitRF.Render.DataDisplay;

public static class ReadoutResolve
{
    /// <summary>Sets <see cref="ReadoutControl.Text"/>, <see cref="ReadoutControl.NameText"/> and
    /// <see cref="ReadoutControl.Problem"/> of every readout in <paramref name="doc"/>.</summary>
    public static void Apply(LinkDocument doc, IPlotDataSources? sources)
    {
        foreach (var (tab, c) in doc.Controls())
            if (c is ReadoutControl r) Update(doc, tab, r, sources);
    }

    private static void Update(LinkDocument doc, int tab, ReadoutControl r, IPlotDataSources? sources)
    {
        r.Problem = null;
        if (r.Source is null && r.SourceName is null)
        {
            r.Text = null; r.NameText = null;
            r.Problem = "Choose a source.";
            return;
        }

        // The trial selection: the lowest trial selected in any source the display reads.
        if (LinkDocument.IsSelection(r.Source) || (r.Source is null && LinkDocument.IsSelection(r.SourceName)))
        {
            r.Source = LinkDocument.SelectionId;
            r.SourceName = LinkDocument.SelectionId;
            r.NameText = LinkDocument.SelectionId;
            var selected = doc.Traces().Select(x => x.Trace.SourcePath).Distinct()
                              .Select(p => doc.SelectedTrials?.Invoke(p)).FirstOrDefault(s => s is { Count: > 0 });
            r.Text = selected is null ? "none" : selected.Count == 1 ? $"trial {selected.Min()}"
                                                : $"trial {selected.Min()} (+{selected.Count - 1})";
            return;
        }

        var control = r.Source is { } id ? doc.FindControl(id) : null;
        control ??= r.Source is null && r.SourceName is { } n0 ? doc.FindControlByName(n0, tab) : null;
        if (control is not null)
        {
            r.Source = control.Id;
            r.SourceName = control.Name;
            r.NameText = Named(r, control.Name);
            r.Text = control switch
            {
                SteppedControl s => ControlRenderer.Format(s, s.CurrentValue, s.DisplayUnit),
                ButtonControl b  => b.On ? "On" : "Off",
                _                => null,
            };
            if (r.Text is null) r.Problem = $"{control.Name} has no value to show.";
            return;
        }

        LinkDocument.MarkerSite? site = r.Source is { } mid ? doc.FindMarker(mid) : null;
        if (site is null && r.Source is null && r.SourceName is { } n1 && doc.TryFindByName(n1, tab, out var byName, out _))
            site = byName;
        if (site is not { } at)
        {
            r.NameText = Named(r, r.SourceName ?? "?");
            r.Problem = $"{r.SourceName ?? "The source"} is gone.";
            return;
        }

        r.Source = at.Marker.Id.ToString("D");
        r.SourceName = at.Marker.Name;
        r.NameText = Named(r, at.Marker.Name);
        switch (r.Show)
        {
            case ReadoutShow.Coordinate:
            {
                var value = MarkerLinkSource.Resolve(at, sources);
                var c = value.Coordinates.FirstOrDefault(k => k.Axis == r.Axis) ?? value.Coordinates.FirstOrDefault();
                if (c is null) { r.Text = null; r.Problem = $"{at.Marker.Name} supplies no coordinate."; return; }
                var (scale, baseUnit) = SiUnit.Split(c.Unit);
                r.Text = LinkMatch.Format(c.Value * scale, baseUnit);
                if (r.Axis != c.Axis) r.NameText = $"{Named(r, at.Marker.Name)} · {c.Axis}";
                return;
            }
            default:
            {
                // The info box's own lines: the name, then the X reading, then the trace's reading (and any more).
                var lines = at.Trace.BuildMarkerBoxLines(at.Plot.ResolveMarker(at.Trace, at.Marker), at.Plot.FreqUnits,
                                                         showFilePrefix: false, at.Plot.Traces);
                if (lines.Count < 2) { r.Text = null; r.Problem = $"{at.Marker.Name} reads nothing."; return; }
                r.Text = r.Show == ReadoutShow.Value ? lines[1].Text : lines[^1].Text;
                return;
            }
        }
    }

    /// <summary>The name line: <c>m1</c>, or <c>m1 · Vgs</c> for a coordinate.</summary>
    private static string Named(ReadoutControl r, string name)
        => r.Show == ReadoutShow.Coordinate && r.Axis is { Length: > 0 } a ? $"{name} · {a}" : name;
}
