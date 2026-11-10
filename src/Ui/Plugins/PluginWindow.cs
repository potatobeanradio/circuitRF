using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Converters;
using CircuitRF.Ui.Messages;
using Material.Icons;
using Material.Icons.Avalonia;

namespace CircuitRF.Ui.Plugins;

/// <summary>A panel window as last left, per plugin <c>id</c>: its size, each split's proportions and each tab strip's
/// selection, keyed by the node's place in the layout tree.</summary>
public sealed class PluginWindowMemory
{
    public double Width { get; set; }
    public double Height { get; set; }
    public Dictionary<string, double[]>? Splits { get; set; }
    public Dictionary<string, int>? Tabs { get; set; }
}

/// <summary>
/// A <c>panel</c> plugin's window (brief-plg-2 §2): a top-level tool window, like Match Designer's and railRF's, drawn
/// from the manifest's layout tree with circuitRF's own controls. Everything it shows comes from
/// <see cref="PluginPanelController.State"/>, applied whole each time it changes; everything the user does goes to the
/// controller, which never makes this window wait on the plugin.
///
/// <para><b>Nothing moves.</b> The busy bar along the top is always laid out, the messages area has a fixed height, a
/// control a reply disables is greyed in place, and a field whose text cannot be sent is marked by a border colour and
/// a tooltip — never by text that takes room.</para>
/// </summary>
public sealed class PluginWindow : Window
{
    public const double MinimumWidth = 480;
    public const double MinimumHeight = 360;
    public const double MessagesHeight = 96;
    public const double TextHeight = 68;
    public const double TableHeight = 140;
    public const string StopText = "Stop";
    public const string ViewPendingText = "view not available yet";
    public const string NoViewsYetText = "No views yet.";
    public const string NoViewsText = "This plugin shows no views.";

    private readonly PluginPanelController _controller;
    private readonly PluginPanelLayout _layout;
    private readonly string? _workspaceDir;
    private readonly Dictionary<string, PluginParamControl> _params = new(StringComparer.Ordinal);
    private readonly List<(PluginReadoutLeaf Leaf, ContentControl Host)> _readouts = [];
    private readonly List<(PluginButtonLeaf Leaf, Button Button)> _buttons = [];
    private readonly List<StackPanel> _messageLists = [];
    private readonly List<(PluginSplitNode Node, Grid Grid)> _splits = [];
    private readonly List<(PluginTabsNode Node, TabControl Tabs, TextBlock Empty)> _tabs = [];
    private readonly ProgressBar _busy;
    private readonly TextBlock _status;
    private readonly Button _stop;
    private long _setSerial;
    private bool _replyTabsFilled;

    /// <summary>Hears what to remember when the window closes; null remembers nothing (a snapshot).</summary>
    public Action<PluginWindowMemory, IReadOnlyDictionary<string, string>>? Remember { get; set; }

    /// <summary>Every parameter's control, by name — what a test measures.</summary>
    public IReadOnlyDictionary<string, PluginParamControl> ParamControls => _params;
    /// <summary>Every button, in tree order.</summary>
    public IReadOnlyList<Button> Buttons => _buttons.Select(b => b.Button).ToList();
    public Button StopButton => _stop;

    public PluginWindow(PluginPanelLayout layout, PluginPanelController controller, string? workspaceDir, PluginWindowMemory? memory)
    {
        _layout = layout;
        _controller = controller;
        _workspaceDir = workspaceDir;

        Title = layout.Title;
        var (w, h) = memory is { Width: > 0, Height: > 0 } ? (memory.Width, memory.Height)
                   : layout.Size ?? (PluginPanelLayout.DefaultWidth, PluginPanelLayout.DefaultHeight);
        Width = Math.Max(w, MinimumWidth);
        Height = Math.Max(h, MinimumHeight);
        MinWidth = MinimumWidth;
        MinHeight = MinimumHeight;
        CanResize = true;
        this.Bind(BackgroundProperty, this.GetResourceObservable("SystemControlBackgroundAltHighBrush"));

        // A field whose text cannot be sent: the box's own border turns the error colour. A window style beats the
        // theme's focus and hover styles, so the mark stays while the user types.
        var invalid = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        Styles.Add(new Style(x => x.OfType<TextBox>().Class(PluginParamControls.InvalidClass).Template().OfType<Border>().Name("PART_BorderElement"))
        {
            Setters = { new Setter(Border.BorderBrushProperty, invalid), new Setter(Border.BorderThicknessProperty, new Thickness(1.5)) },
        });
        Styles.Add(new Style(x => x.OfType<ComboBox>().Class(PluginParamControls.InvalidClass).Template().OfType<Border>().Name("Background"))
        {
            Setters = { new Setter(Border.BorderBrushProperty, invalid), new Setter(Border.BorderThicknessProperty, new Thickness(1.5)) },
        });

        // The busy bar: always laid out, along the top edge.
        _busy = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
        _status = new TextBlock { FontSize = 11, Opacity = 0.7, MinHeight = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        _stop = new Button { Content = StopText, FontSize = 12, Width = 72, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center };
        _stop.Click += (_, _) => _controller.Stop();
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(12, 6, 12, 4) };
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _busy, _status } };
        Grid.SetColumn(_stop, 1);
        bar.Children.Add(left);
        bar.Children.Add(_stop);
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);

        var body = Build(layout.Root, memory);
        Grid.SetIsSharedSizeScope(body, true);
        var root = new DockPanel { Children = { bar, new Border { Margin = new Thickness(12, 4, 12, 12), Child = body } } };
        Content = root;

        _controller.StateChanged += Apply;
        Closing += (_, _) =>
        {
            _controller.StateChanged -= Apply;
            Remember?.Invoke(Memory(), _controller.Values);
            _ = _controller.CloseAsync();
        };
        Apply(_controller.State);
    }

    // ── Building the tree ───────────────────────────────────────────────────────

    private Control Build(PluginLayoutNode node, PluginWindowMemory? memory)
    {
        switch (node)
        {
            case PluginStackNode s:
            {
                var panel = new StackPanel { Orientation = s.Horizontal ? Orientation.Horizontal : Orientation.Vertical, Spacing = s.Horizontal ? 12 : 8 };
                foreach (var c in s.Children) panel.Children.Add(Build(c, memory));
                return panel;
            }
            case PluginGroupNode g:
            {
                // As the analysis editor draws its groups: a rounded, bordered box with a quiet title.
                var inner = new StackPanel { Spacing = 6 };
                inner.Children.Add(new TextBlock { Text = g.Title, FontSize = 12, Opacity = 0.75 });
                foreach (var c in g.Children) inner.Children.Add(Build(c, memory));
                var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = inner };
                box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
                return box;
            }
            case PluginSplitNode sp:
                return Split(sp, memory);
            case PluginTabsNode t:
                return Tabs(t, memory);
            case PluginParamLeaf p:
                return ParamRow(p);
            case PluginReadoutLeaf r:
                return Readout(r);
            case PluginButtonLeaf b:
            {
                var button = new Button { Content = b.Label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4) };
                button.Click += (_, _) => _controller.RunAction(b.Action);
                _buttons.Add((b, button));
                return button;
            }
            case PluginMessagesLeaf:
            {
                var list = new StackPanel { Spacing = 3 };
                _messageLists.Add(list);
                var frame = new Border
                {
                    Height = MessagesHeight, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 4),
                    Child = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                };
                frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
                return frame;
            }
            case PluginViewLeaf v:
                return EmptyFrame(v.IsKnownKind ? ViewPendingText : $"A {v.Kind} view {PluginUnknownNode.Reason}.");
            case PluginNoteLeaf n:
                return new TextBlock { Text = n.Text, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
            case PluginUnknownNode u:
                return EmptyFrame($"'{u.What}' {PluginUnknownNode.Reason}.", 48);
            default:
                return new Panel();
        }
    }

    private Control ParamRow(PluginParamLeaf leaf)
    {
        var grid = Row();
        grid.Children.Add(new TextBlock { Text = leaf.DisplayLabel, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var control = PluginParamControls.Build(leaf.Param, _workspaceDir, _controller.Value(leaf.Param.Name), leaf.ClampedChars, () => this);
        control.Changed += c => _controller.SetValue(c.Param.Name, c.Read());
        control.PlaceIn(grid, 0);
        _params[leaf.Param.Name] = control;
        return grid;
    }

    private Control Readout(PluginReadoutLeaf leaf)
    {
        var host = new ContentControl();
        _readouts.Add((leaf, host));
        if (leaf.As == PluginReadoutStyle.Value)
        {
            var grid = Row();
            grid.Children.Add(new TextBlock { Text = leaf.DisplayLabel, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(host, 1); Grid.SetColumnSpan(host, 2);
            grid.Children.Add(host);
            return grid;
        }
        // A text or a table is as long as the plugin makes it, so it has a fixed height and scrolls: a reply that says
        // less must not move what is below it.
        host.VerticalAlignment = VerticalAlignment.Top;
        host.VerticalContentAlignment = VerticalAlignment.Top;
        var frame = new ScrollViewer
        {
            Content = host, Height = leaf.As == PluginReadoutStyle.Table ? TableHeight : TextHeight,
            VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = leaf.As == PluginReadoutStyle.Table
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        return new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = leaf.DisplayLabel, FontSize = 12, Opacity = 0.75 }, frame },
        };
    }

    /// <summary>One labelled row: the label column is shared across the panel so labels line up.</summary>
    private static Grid Row()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "PluginLabel" });
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        return grid;
    }

    private Control Split(PluginSplitNode sp, PluginWindowMemory? memory)
    {
        var sizes = memory?.Splits?.GetValueOrDefault(sp.Path) is { } kept && kept.Length == sp.Children.Count && kept.All(x => x > 0)
            ? kept : sp.Sizes.ToArray();
        var grid = new Grid();
        for (int i = 0; i < sp.Children.Count; i++)
        {
            if (i > 0)
            {
                var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = sp.Horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows };
                if (sp.Horizontal) { grid.ColumnDefinitions.Add(new ColumnDefinition(8, GridUnitType.Pixel)); Grid.SetColumn(splitter, 2 * i - 1); }
                else { grid.RowDefinitions.Add(new RowDefinition(8, GridUnitType.Pixel)); Grid.SetRow(splitter, 2 * i - 1); }
                grid.Children.Add(splitter);
            }
            if (sp.Horizontal) grid.ColumnDefinitions.Add(new ColumnDefinition(sizes[i], GridUnitType.Star) { MinWidth = 120 });
            else grid.RowDefinitions.Add(new RowDefinition(sizes[i], GridUnitType.Star) { MinHeight = 80 });

            var child = Build(sp.Children[i], memory);
            // A pane scrolls rather than squeezing its controls, so a short window never moves one.
            Control pane = sp.Children[i] is PluginTabsNode or PluginSplitNode or PluginViewLeaf
                ? child
                : new ScrollViewer { Content = child, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 6, 0) };
            Grid.SetIsSharedSizeScope(pane, true);
            if (sp.Horizontal) Grid.SetColumn(pane, 2 * i); else Grid.SetRow(pane, 2 * i);
            grid.Children.Add(pane);
        }
        _splits.Add((sp, grid));
        return grid;
    }

    private Control Tabs(PluginTabsNode t, PluginWindowMemory? memory)
    {
        var tabs = new TabControl { Padding = new Thickness(0, 4, 0, 0) };
        foreach (var tab in t.Tabs) tabs.Items.Add(TabItemFor(tab, memory));
        if (memory?.Tabs?.GetValueOrDefault(t.Path) is { } sel && sel >= 0 && sel < t.Tabs.Count) tabs.SelectedIndex = sel;
        var empty = new TextBlock
        {
            Text = t.FromFirstReply ? NoViewsYetText : "", FontSize = 12, Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsVisible = t.Tabs.Count == 0,
        };
        _tabs.Add((t, tabs, empty));
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = new Panel { Children = { tabs, empty } } };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
        return frame;
    }

    private TabItem TabItemFor(PluginTab tab, PluginWindowMemory? memory)
    {
        var content = Build(tab.Content, memory);
        Grid.SetIsSharedSizeScope(content, true);
        return new TabItem { Header = new TextBlock { Text = tab.Title, FontSize = 13 }, Content = content };
    }

    private static Control EmptyFrame(string text, double minHeight = 120)
    {
        var frame = new Border
        {
            MinHeight = minHeight, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = text, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8),
            },
        };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
        return frame;
    }

    // ── Applying a state ────────────────────────────────────────────────────────

    /// <summary>Draws <paramref name="s"/> whole: values a reply set, readouts, enabled, field marks, messages and the
    /// busy bar — always together, from one state.</summary>
    private void Apply(PluginPanelState s)
    {
        if (s.SetSerial != _setSerial)
        {
            _setSerial = s.SetSerial;
            foreach (var (name, text) in s.Set)
                if (_params.TryGetValue(name, out var c)) c.Write(text);
        }

        foreach (var (leaf, host) in _readouts)
        {
            var value = s.Readouts.GetValueOrDefault(leaf.Param.Name) ?? PluginReadoutValue.Empty;
            host.Content = leaf.As switch
            {
                PluginReadoutStyle.Table => PluginUi.Table(value),
                PluginReadoutStyle.Text  => new SelectableTextBlock { Text = value.Text ?? "", FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top },
                _ => new SelectableTextBlock
                {
                    Text = value.IsTable ? $"{value.Rows!.Count} rows" : value.Text, FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            // A value is one line: one longer than its row ends in … and reads whole as a tooltip.
            if (leaf.As == PluginReadoutStyle.Value) ToolTip.SetTip(host, value.Text);
        }

        foreach (var (name, control) in _params)
        {
            control.SetEnabled(s.IsEnabled(name));
            control.SetError(s.FieldErrors.GetValueOrDefault(name));
        }
        foreach (var (leaf, button) in _buttons)
            button.IsEnabled = !s.Committing && s.IsEnabled(leaf.Action) && s.IsEnabled(leaf.Label);

        foreach (var list in _messageLists) FillMessages(list, s);

        if (s.Reply is not null && !_replyTabsFilled)
        {
            _replyTabsFilled = true;
            foreach (var (node, tabs, empty) in _tabs.Where(t => t.Node.FromFirstReply))
            {
                foreach (var tab in PluginLayoutReader.TabsFor(s.Views, node.Path)) tabs.Items.Add(TabItemFor(tab, null));
                empty.Text = NoViewsText;
                empty.IsVisible = tabs.Items.Count == 0;
                if (tabs.Items.Count > 0) tabs.SelectedIndex = 0;
            }
        }

        _busy.Opacity = s.Busy ? 1 : 0;
        _busy.IsIndeterminate = s.Busy && s.Progress?.Fraction is null;
        if (s.Progress?.Fraction is { } f) _busy.Value = Math.Clamp(f, 0, 1);
        _status.Text = s.Busy ? s.Progress?.Text ?? "Working…" : s.Committing ? "Placing…" : "";
        _stop.IsEnabled = s.InFlight || s.Committing;
    }

    private void FillMessages(StackPanel list, PluginPanelState s)
    {
        list.Children.Clear();
        foreach (var (name, error) in s.FieldErrors)
        {
            string label = LabelOf(name);
            list.Children.Add(MessageRow(MessageLevel.Error, $"{label}: {error}"));
        }
        foreach (var m in s.Messages)
            list.Children.Add(MessageRow(m.Severity switch { "error" => MessageLevel.Error, "warning" => MessageLevel.Warning, _ => MessageLevel.Info }, m.Text));
        if (s.Host is { } host)
        {
            var level = host.Kind switch
            {
                PluginHostLineKind.Stopped or PluginHostLineKind.Failed => MessageLevel.Error,
                PluginHostLineKind.NoAnswer => MessageLevel.Warning,
                _ => MessageLevel.Info,
            };
            Button? stop = null;
            if (host.OffersStop)
            {
                stop = new Button { Content = StopText, FontSize = 11, Padding = new Thickness(8, 1), VerticalAlignment = VerticalAlignment.Top };
                stop.Click += (_, _) => _controller.Stop();
            }
            list.Children.Add(MessageRow(level, host.Text, stop));
            foreach (string detail in host.Details)
                list.Children.Add(new SelectableTextBlock { Text = detail, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 0) });
        }
    }

    private string LabelOf(string name)
        => _controller.Manifest.Params.FirstOrDefault(p => p.Name == name)?.DisplayLabel ?? name;

    private static Control MessageRow(MessageLevel level, string text, Control? trailing = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
        var icon = new MaterialIcon
        {
            Kind = (MaterialIconKind)MessageLevelToIconConverter.Instance.Convert(level, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture),
            Foreground = (IBrush)MessageLevelToColorConverter.Instance.Convert(level, typeof(IBrush), null, CultureInfo.InvariantCulture),
            Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
        };
        var body = new SelectableTextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(body, 1);
        row.Children.Add(icon);
        row.Children.Add(body);
        if (trailing is not null) { Grid.SetColumn(trailing, 2); row.Children.Add(trailing); }
        return row;
    }

    /// <summary>What to remember: the size, each split's proportions, each tab strip's selection.</summary>
    public PluginWindowMemory Memory()
    {
        var m = new PluginWindowMemory { Width = Math.Round(Bounds.Width > 0 ? Bounds.Width : Width), Height = Math.Round(Bounds.Height > 0 ? Bounds.Height : Height) };
        foreach (var (node, grid) in _splits)
        {
            var sizes = node.Horizontal
                ? grid.ColumnDefinitions.Where((_, i) => i % 2 == 0).Select(c => c.ActualWidth).ToArray()
                : grid.RowDefinitions.Where((_, i) => i % 2 == 0).Select(r => r.ActualHeight).ToArray();
            if (sizes.All(x => x > 0)) (m.Splits ??= [])[node.Path] = [.. sizes.Select(x => Math.Round(x))];
        }
        foreach (var (node, tabs, _) in _tabs)
            if (tabs.SelectedIndex >= 0) (m.Tabs ??= [])[node.Path] = tabs.SelectedIndex;
        return m;
    }
}
