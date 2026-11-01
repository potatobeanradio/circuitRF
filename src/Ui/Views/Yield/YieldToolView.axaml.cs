using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Tuning;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.Views.Yield;

/// <summary>
/// The Yield panel (brief-yield-10). Everything it DOES is on <see cref="YieldPanelViewModel"/>; this file opens the
/// correlation grid and the corner generator the view model asks for, commits a settings box or a spread on Enter and
/// when it loses focus, and routes Enter in the Add… list.
/// </summary>
public partial class YieldToolView : UserControl
{
    /// <summary>An active DOE effect in bold (brief-yield-14).</summary>
    public static readonly IValueConverter ActiveWeight = new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.Bold : FontWeight.Normal);

    /// <summary>A disabled goal is listed dimmed.</summary>
    public static readonly IValueConverter EnabledOpacity = new FuncValueConverter<bool, double>(on => on ? 1.0 : 0.45);

    /// <summary>A failing trial's pass/fail in red.</summary>
    public static readonly IValueConverter FailBrush =
        new FuncValueConverter<bool, IBrush?>(fail => fail ? new SolidColorBrush(Color.FromRgb(0xD9, 0x4F, 0x4F)) : null);

    /// <summary>A matrix-tile cell's share on the tile's one sequential scale, 0 (clear) to its maximum (accent).</summary>
    public static readonly IValueConverter ShareBrush = new FuncValueConverter<double, IBrush?>(t =>
        t <= 0 ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb((byte)(36 + 219 * Math.Clamp(t, 0, 1)), 0x2F, 0x7F, 0xD9)));

    /// <summary>A cell's label reads white once its fill is dark.</summary>
    public static readonly IValueConverter ShareText = new FuncValueConverter<double, object?>(t => t > 0.55 ? Brushes.White : AvaloniaProperty.UnsetValue);

    /// <summary>The tile's legend: the same scale, 0 to the maximum.</summary>
    public static readonly IBrush ShareLegend = new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
        EndPoint   = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(36, 0x2F, 0x7F, 0xD9), 0), new GradientStop(Color.FromArgb(255, 0x2F, 0x7F, 0xD9), 1) },
    };

    private YieldPanelViewModel? _panel;

    private FlyoutBase AddFlyout => AddButton.Flyout!;

    public YieldToolView()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
        EscapeCancelsEdit.Attach(this);

        AddFlyout.Opened += (_, _) => { if (_panel is { } p) p.Add.IsOpen = true; AddSearch.Focus(); };
        AddFlyout.Closed += (_, _) => { if (_panel is { } p) p.Add.IsOpen = false; };
        TuningAddPopup.Attach(AddSearch, AddList, () => _panel?.Add);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_panel is not null)
        {
            _panel.CorrelationsRequested -= OnCorrelationsRequested;
            _panel.GeneratorRequested    -= OnGeneratorRequested;
            _panel.Add.PropertyChanged   -= OnAddChanged;
        }
        _panel = (DataContext as YieldPanelTool)?.Panel;
        if (_panel is not null)
        {
            _panel.CorrelationsRequested += OnCorrelationsRequested;
            _panel.GeneratorRequested    += OnGeneratorRequested;
            _panel.Add.PropertyChanged   += OnAddChanged;
            if (this.IsAttachedToVisualTree()) _panel.SetShown(true);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _panel?.SetShown(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _panel?.SetShown(false);
    }

    private void OnAddChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TuningAddViewModel.IsOpen) && _panel is { Add.IsOpen: false }) AddFlyout.Hide();
    }

    private async void OnCorrelationsRequested(object? sender, CorrelationsEditorViewModel editor)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new CorrelationsDialog(editor).ShowDialog(owner);
    }

    private async void OnGeneratorRequested(object? sender, CornerGeneratorViewModel editor)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new CornerGeneratorDialog(editor).ShowDialog(owner);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var source = e.Source as Control;
        if (source is TextBox { DataContext: YieldVariableRowViewModel row } spread && spread.Classes.Contains("spread"))
        {
            if (e.Key == Key.Enter)  { row.CommitSpread(spread.Text ?? ""); spread.Text = row.SpreadText; e.Handled = true; }
            if (e.Key == Key.Escape) { spread.Text = row.SpreadText; e.Handled = true; }
            return;
        }
        // A settings box commits on LostFocus, and on Enter.
        if (source is TextBox box && box.Classes.Contains("commit") && e.Key == Key.Enter)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: YieldVariableRowViewModel row } box && box.Classes.Contains("spread")
            && box.Text != row.SpreadText)
        {
            row.CommitSpread(box.Text ?? "");
            box.Text = row.SpreadText;
        }
    }

    private void OnHelp(object? sender, RoutedEventArgs e)
        => DocLauncher.Open(_panel?.Mode == YieldMode.Sensitivity ? "reference/sensitivity.html" : "reference/yield.html");
}
