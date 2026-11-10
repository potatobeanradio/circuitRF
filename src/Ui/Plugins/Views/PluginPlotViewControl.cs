using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using CircuitRF.Cli;
using CircuitRF.Design.Plugins;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Plugins.Views;

/// <summary>A view gesture as a view raises it: the kind, its fields, and the parameter it changed with its new text.</summary>
public delegate void PluginViewGesture(string kind, JsonObject fields, string? param, string? text);

/// <summary>
/// A <c>plot</c> or <c>smith</c> view (brief-plg-3 §1): the Data Display's own <see cref="PlotControl"/>, embedded as the
/// RF Chain Budget embeds it, over a plot composed by <c>circuitRF plot</c>'s code (<see cref="PluginPlots"/>). Zoom,
/// marker placement, readouts, copy and export are the control's. A marker the plugin bound to a parameter is an
/// ordinary Data Display marker: dropping it somewhere else sends <c>event {kind: "marker", marker, x}</c> and sets the
/// parameter. Markers the user adds are the user's, kept across replies and never sent.
/// </summary>
public sealed class PluginPlotViewControl : DockPanel, IPluginViewControl
{
    public const string OpenText = "Open in Data Display";

    private readonly string _name;
    private readonly PluginManifest _manifest;
    private readonly PluginViewGesture _gesture;
    private readonly Func<string, bool>? _openDocument;
    private readonly DataDisplayViewModel _host = new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);
    private readonly PlotControl _plotControl;
    private readonly Button _open;
    private PlotContainerViewModel? _container;
    private PluginPlotComposition? _composition;
    private PluginPlotView? _view;
    private readonly Dictionary<Marker, double> _boundAt = new();
    private readonly HashSet<Marker> _pluginMarkers = new();
    private bool _userMovedAxes;
    private bool _filling;

    public PluginPlotViewControl(string name, PluginManifest manifest, PluginViewGesture gesture, Func<string, bool>? openDocument)
    {
        _name = name;
        _manifest = manifest;
        _gesture = gesture;
        _openDocument = openDocument;

        _open = new Button { Content = OpenText, FontSize = 11, Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = false };
        _open.Click += (_, _) => OpenInDataDisplay();
        var bar = new Border { Padding = new Thickness(6, 4, 6, 0), Child = _open };
        SetDock(bar, Avalonia.Controls.Dock.Top);
        Children.Add(bar);

        _plotControl = new PlotControl { ClipToBounds = true, CanDeletePlot = false, CanEditPlotProperties = false };
        _plotControl.ContainerProvider = () => _container;
        _plotControl.PlotChanged += (_, _) => OnPlotChanged();
        Children.Add(_plotControl);

        ActualThemeVariantChanged += (_, _) => SyncTheme();
        AttachedToVisualTree += (_, _) => SyncTheme();
    }

    /// <summary>The plot drawn, or null — what a test reads.</summary>
    public Plot? Plot => _container?.PlotVM.Plot;
    public PlotControl PlotControl => _plotControl;

    public string? Show(PluginView view)
    {
        var v = (PluginPlotView)view;
        var c = PluginPlots.Compose(v, _manifest);
        if (c.Problem is not null) return c.Problem;

        // The user's markers, by trace and sample, before the traces are replaced.
        var kept = new List<(int Trace, Marker Marker, double X)>();
        if (Plot is { } old)
            for (int t = 0; t < old.Traces.Count; t++)
                foreach (var m in old.Traces[t].Markers.Where(m => !_pluginMarkers.Contains(m)))
                    if (PluginPlots.XOf(old.Traces[t], m) is { } x) kept.Add((t, m, x));

        var pc = c.Container!;
        if (_container is null || _container.PlotVM.Plot.PlotType != pc.PlotType)
        {
            if (_container is not null) _host.Plots.Remove(_container);
            _container = _host.AddPlot(pc.PlotType, pc.FreqUnit, 0, 0, pc.Width, pc.Height);
            _host.SelectOnly((PlotContainerViewModel?)null);
            _plotControl.Library = _host.Library;
            _userMovedAxes = false;
        }

        _filling = true;
        try
        {
            var plot = _container.PlotVM.Plot;
            PlotConfigLoader.ApplySettings(plot, pc);
            plot.Traces.Clear();
            _pluginMarkers.Clear();
            foreach (var t in c.Plot!.Traces)
            {
                plot.Traces.Add(t);
                foreach (var m in t.Markers) _pluginMarkers.Add(m);
            }
            // The plugin's window each time, unless the user has zoomed or panned since: then theirs stands.
            if (!_userMovedAxes) PlotConfigLoader.RestoreAxes(plot, pc.Axes);
            foreach (var (t, m, x) in kept)
                if (t < plot.Traces.Count && PluginPlots.Place(plot.Traces[t], m, x)) plot.Traces[t].Markers.Add(m);

            _boundAt.Clear();
            foreach (var (_, marker, trace) in c.Bound)
                if (PluginPlots.XOf(trace, marker) is { } at) _boundAt[marker] = at;
            _plotControl.Plot = plot;
            _container.OnPlotChanged(_container, EventArgs.Empty);
            _container.RequestPlotRedraw();
        }
        finally { _filling = false; }

        _composition = c;
        _view = v;
        _open.IsEnabled = _openDocument is not null;
        _plotControl.InvalidateVisual();
        return null;
    }

    /// <summary>
    /// After a gesture: a bound marker that now sits on another sample was dragged, so the plugin hears it and the
    /// parameter becomes that sample's value — exactly as the axis holds it. Anything else (a pan, a zoom) is the
    /// user's own view, kept until a reply's axes are asked for again.
    /// </summary>
    private void OnPlotChanged()
    {
        if (_filling || _composition is null) return;
        foreach (var (spec, marker, trace) in _composition.Bound)
        {
            if (!_boundAt.TryGetValue(marker, out double was) || PluginPlots.XOf(trace, marker) is not { } now || now == was) continue;
            _boundAt[marker] = now;
            DropBoundMarker(spec, now);
            return;
        }
        _userMovedAxes = true;
    }

    /// <summary>A bound marker dropped at <paramref name="x"/> (base SI): the <c>event</c>, and the parameter's new text.
    /// Public so a harness can drop one exactly where a drag would.</summary>
    public void DropBoundMarker(PluginPlotMarker spec, double x)
    {
        var param = _manifest.Params.First(p => p.Name == spec.Bind);
        string text = PluginViews.QuantityText(param, x, new PluginParamContext(null));
        _gesture("marker", new JsonObject { ["marker"] = spec.Index, ["x"] = x }, param.Name, text);
    }

    /// <summary>Moves bound marker <paramref name="index"/> onto the sample nearest <paramref name="x"/> and drops it there,
    /// as a drag that ends there does. False when there is no such marker.</summary>
    public bool DragBoundMarker(int index, double x)
    {
        if (_composition is null) return false;
        foreach (var (spec, marker, trace) in _composition.Bound)
        {
            if (spec.Index != index) continue;
            if (!PluginPlots.Place(trace, marker, x)) return false;
            _plotControl.InvalidateVisual();
            OnPlotChanged();
            return true;
        }
        return false;
    }

    private void OpenInDataDisplay()
    {
        if (_composition is null || _view is null || _openDocument is null || _view.StagingDir.Length == 0) return;
        try
        {
            _openDocument(PluginPlots.WriteDataDisplay(_composition, _view.StagingDir, _name));
            ToolTip.SetTip(_open, null);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ToolTip.SetTip(_open, $"The {_name} view could not be written for a Data Display: {e.Message}");
        }
    }

    private void SyncTheme()
    {
        _host.Theme = ActualThemeVariant == ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        _plotControl.SetValue(PlotControl.PlotThemeProperty, _host.Theme);
        _plotControl.InvalidateVisual();
    }
}
