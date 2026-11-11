// The plugin's own window: an Avalonia window built in code (no XAML, so there is no XAML-compilation step to explain).
// circuitRF draws none of it. The rule that matters here: the UI thread never waits on the pipe. Every message to
// circuitRF is awaited, so the window stays live while circuitRF works — or while its user answers a Replace / New name
// question about the placement.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace LineSectionPlugin;

public sealed class App : Application
{
    internal static Protocol? Pipe;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Pipe is { } pipe)
        {
            desktop.MainWindow = new MainWindow(pipe);
            // circuitRF said bye, or went away: close within its 2 s, or it kills the process.
            pipe.Ended += () => Dispatcher.UIThread.Post(() => desktop.Shutdown());
            pipe.Listen();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class MainWindow : Window
{
    private readonly Protocol _pipe;
    private readonly ComboBox _conductor = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _frequency = new() { Text = "2.4" };
    private readonly TextBox _z0 = new() { Text = "50" };
    private readonly TextBox _degrees = new() { Text = "90" };
    // Two lines reserved, so the buttons below never move when a result appears or clears.
    private readonly TextBlock _result = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 40 };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _place = new() { Content = "Place in workspace", IsEnabled = false };
    private readonly Button _open = new() { Content = "Open in circuitRF", IsEnabled = false };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private Technology? _tech;
    private (LineSpec Spec, LineDesign Design)? _design;
    private string? _placedAs;
    private int _generation;

    public static int Run(Protocol pipe, string[] args)
    {
        App.Pipe = pipe;
        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    public MainWindow(Protocol pipe)
    {
        _pipe = pipe;
        Title = "Line Section";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        void Row(int row, string label, Control input, string unit)
        {
            form.Children.Add(Cell(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) }, row, 0));
            form.Children.Add(Cell(input, row, 1));
            form.Children.Add(Cell(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 4, 0, 4), MinWidth = 32 }, row, 2));
        }
        Row(0, "Conductor", _conductor, "");
        Row(1, "Frequency", _frequency, "GHz");
        Row(2, "Impedance", _z0, "Ohm");
        Row(3, "Electrical length", _degrees, "deg");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _place, _open } };
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { form, _result, buttons, _status } };

        foreach (var box in new[] { _frequency, _z0, _degrees }) box.TextChanged += (_, _) => Changed();
        _conductor.SelectionChanged += (_, _) => Changed();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Recalculate(); };
        _place.Click += (_, _) => Place();
        _open.Click += (_, _) => Open();
        Opened += (_, _) => LoadTechnology();
    }

    private static Control Cell(Control c, int row, int column)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
        return c;
    }

    /// <summary>The workspace's default technology, and the conductors a line can be drawn on.</summary>
    private async void LoadTechnology()
    {
        _status.Text = "Asking circuitRF for the workspace's technology…";
        try
        {
            _tech = Technology.From(await _pipe.ResolveAsync(_pipe.NextId(), "technology"));
            _conductor.ItemsSource = _tech.Signals.Select(c => c.Name).ToList();
            _conductor.SelectedIndex = 0;
            _status.Text = "";
        }
        catch (Exception e) when (e is HostException or IOException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            _status.Text = e.Message;
        }
    }

    private void Changed()
    {
        _place.IsEnabled = false;
        _debounce.Stop();
        _debounce.Start();
    }

    private LineSpec? Spec(out string? problem)
    {
        problem = null;
        if (!Number(_frequency, out double ghz) || !Number(_z0, out double z0) || !Number(_degrees, out double degrees))
        {
            problem = "Each value is a number.";
            return null;
        }
        var spec = new LineSpec(ghz * 1e9, z0, degrees, _conductor.SelectedItem as string ?? "");
        problem = LineSection.Problem(spec);
        return problem is null ? spec : null;

        static bool Number(TextBox box, out double v)
            => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>Designs the line with circuitRF's line model. A newer change supersedes an older answer still on its way.</summary>
    private async void Recalculate()
    {
        if (_tech is not { } tech) return;
        int generation = ++_generation;
        _design = null;
        if (Spec(out var problem) is not { } spec)
        {
            _result.Text = "";
            _status.Text = problem;
            return;
        }
        _status.Text = "circuitRF is modelling the line…";
        try
        {
            string substrate = $"{spec.Conductor} : {tech.Path}";
            var design = await LineSection.DesignAsync(spec, tech, w => _pipe.LineModelAsync(_pipe.NextId(), substrate, w, spec.FrequencyHz));
            if (generation != _generation) return;
            _design = (spec, design);
            _result.Text = string.Create(CultureInfo.InvariantCulture,
                $"Width {design.Width * 1e3:G4} mm, length {design.Length * 1e3:G4} mm, effective permittivity {design.Eeff:G4}; circuitRF's model gives {design.ModelZ0:F2} Ohm at that width.");
            _status.Text = "";
            _place.IsEnabled = true;
        }
        catch (Exception e) when (e is HostException or IOException or InvalidOperationException)
        {
            if (generation == _generation) _status.Text = e.Message;
        }
    }

    /// <summary>Stages the three documents and sends <c>placed</c>; circuitRF validates, checkpoints and places them.</summary>
    private async void Place()
    {
        if (_design is not var (spec, design) || _tech is not { } tech) return;
        _place.IsEnabled = false;
        _status.Text = "Placing…";
        try
        {
            var cell = LineSection.Stage(LineSection.Documents(spec, tech, design, techRef: null), _pipe.Hello.Staging);
            long id = _pipe.NextId();
            var answer = await _pipe.AskAsync(id, new PlacedMessage { Id = id, Cells = [cell] }, PluginJson.Wire.PlacedMessage);
            if (answer.Error is { } error) _status.Text = error;
            else
            {
                _placedAs = answer.Cells?.FirstOrDefault() ?? cell.Name;
                _status.Text = $"Placed as {_placedAs}.";
                _open.IsEnabled = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = e.Message;
        }
        finally
        {
            _place.IsEnabled = _design is not null;
        }
    }

    /// <summary>Asks circuitRF to open the placed layout.</summary>
    private async void Open()
    {
        if (_placedAs is null || _pipe.Hello.Workspace is not { } ws) return;
        long id = _pipe.NextId();
        string path = Path.Combine(ws, _placedAs, "layout", LineSection.CellName + ".clay");
        try
        {
            var answer = await _pipe.AskAsync(id, new OpenMessage { Id = id, Path = path }, PluginJson.Wire.OpenMessage);
            _status.Text = answer.Error ?? $"Opened {_placedAs} in circuitRF.";
        }
        catch (IOException e)
        {
            _status.Text = e.Message;
        }
    }
}
