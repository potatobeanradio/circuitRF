using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins.Views;

/// <summary>A view's gesture, for the controller: the event's kind and fields, and the parameter it changed (with the
/// text its control now shows), or none.</summary>
public delegate void PluginViewEvent(string view, string kind, JsonObject fields, string? param, string? text);

/// <summary>What every plugin view control does: draw a view it has been given, or say why it cannot.</summary>
internal interface IPluginViewControl
{
    /// <summary>Draws <paramref name="view"/>; returns why it cannot be drawn, or null.</summary>
    string? Show(PluginView view);
}

/// <summary>
/// One view in a panel (brief-plg-3): the frame a <c>view</c> leaf of the layout tree is drawn in. Given a view's body
/// from a reply it reads it (<see cref="PluginViews.Read"/>) and hands it to the control for its kind — one control per
/// frame, kept across replies, so a plot keeps the user's markers and zoom and a layout its pan. Given null it clears;
/// given what it cannot read it shows an empty frame with the reason, and <see cref="Problems"/> says it for the
/// panel's messages. A reply that does not carry the view leaves it as it was (<c>plugins.md</c> §5).
/// </summary>
public sealed class PluginViewFrame : Border
{
    public const string NothingYetText = "Nothing to show yet.";

    private readonly string _name;
    private readonly string _kind;
    private readonly PluginViewContext _context;
    private readonly PluginViewEvent _event;
    private readonly Func<string, bool>? _openDocument;
    private readonly TextBlock _empty;
    private readonly Panel _host = new();
    private Control? _control;
    private long _serial = -1;
    private string? _staging;

    public PluginViewFrame(string name, string kind, PluginViewContext context, PluginViewEvent onEvent, Func<string, bool>? openDocument)
    {
        _name = name;
        _kind = kind;
        _context = context;
        _event = onEvent;
        _openDocument = openDocument;
        MinHeight = 120;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        ClipToBounds = true;
        this.Bind(BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
        _empty = new TextBlock
        {
            Text = NothingYetText, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12),
        };
        _host.Children.Add(_empty);
        Child = _host;
    }

    public string ViewName => _name;

    /// <summary>Why the view is not drawn, for the panel's messages; null when it is.</summary>
    public string? Error { get; private set; }

    /// <summary>What the messages should also say about a view that is drawn — a layer its technology lacks, a handle
    /// that cannot be dragged.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>The control drawing the view, or null while the frame is empty — what a snapshot or a test reaches.</summary>
    public Control? ViewControl => _control is { IsVisible: true } ? _control : null;

    /// <summary>The view as last read — what a test inspects.</summary>
    public PluginView? Current { get; private set; }

    /// <summary>Draws <paramref name="body"/> when it is new; clears for null. True when anything changed.</summary>
    public bool Update(PluginViewBody? body)
    {
        if (body is null)
        {
            if (_serial == -1) return false;
            _serial = -1;
            Current = null;
            (Error, Notes) = (null, []);
            Empty(NothingYetText);
            return true;
        }
        if (body.Serial == _serial && body.StagingDir == _staging) return false;
        _serial = body.Serial;
        _staging = body.StagingDir;

        var view = PluginViews.Read(body, _kind, _context);
        Current = view;
        (Error, Notes) = (null, []);
        if (view.Problem is { } problem)
        {
            Error = problem;
            Empty(problem);
            return true;
        }

        _control ??= Make(view);
        if (_control is null) { Error = $"The {_name} view cannot be drawn here."; Empty(Error); return true; }
        string? why;
        try { why = ((IPluginViewControl)_control).Show(view); }
        catch (Exception e) when (e is not OutOfMemoryException) { why = $"The {_name} view cannot be drawn: {e.Message}"; }
        if (why is not null) { Error = why; Empty(why); return true; }

        Notes = _control is PluginLayoutViewControl lv ? [.. view.Notes, .. lv.Overlay.Refused] : view.Notes;
        _empty.IsVisible = false;
        _control.IsVisible = true;
        return true;
    }

    private void Empty(string text)
    {
        _empty.Text = text;
        _empty.IsVisible = true;
        if (_control is not null) _control.IsVisible = false;
    }

    private Control? Make(PluginView view)
    {
        Control? c = view switch
        {
            PluginPlotView      => new PluginPlotViewControl(_name, _context.Manifest, Raise, _openDocument),
            PluginTableView     => new PluginTableViewControl(),
            PluginLayoutView    => new PluginLayoutViewControl(_name, _context, Raise),
            PluginSchematicView => new PluginSchematicViewControl(),
            PluginImageView     => new PluginImageViewControl(_name, Raise),
            _                   => null,
        };
        if (c is not null) _host.Children.Add(c);
        return c;
    }

    private void Raise(string kind, JsonObject fields, string? param, string? text) => _event(_name, kind, fields, param, text);
}
