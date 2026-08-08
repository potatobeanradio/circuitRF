using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using RfCore;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The Touchstone export dialog (brief-wbond-wbe M3): reference impedance, frequency grid, number
/// format, and a <b>read-only port list</b> showing port <i>k</i> → array name.
///
/// <para>The port list is not decoration. The mapping decides how the file gets wired up by whoever
/// receives it, so it is shown before the file is written rather than left to be recovered from the
/// comments afterwards.</para>
/// </summary>
public partial class WBondTouchstoneExportDialog : Window
{
    private WBondDesign? _design;

    public WBondTouchstoneExportDialog() : this(null) { }

    public WBondTouchstoneExportDialog(WBondDesign? design)
    {
        InitializeComponent();
        _design = design;

        if (design is not null)
        {
            var names = WBondTouchstoneExport.PortNames(design);
            PortList.ItemsSource = names.Select((n, i) => $"Port {i + 1}  →  {n}").ToArray();
        }

        StartBox.ValueChanged  += (_, _) => RefreshCost();
        StopBox.ValueChanged   += (_, _) => RefreshCost();
        PointsBox.ValueChanged += (_, _) => RefreshCost();
        RefreshCost();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Shows the dialog and returns the chosen options, or null when cancelled.
    /// </summary>
    public static Task<WBondTouchstoneExport.Options?> ShowAsync(Window owner, WBondDesign design) =>
        new WBondTouchstoneExportDialog(design)
            .ShowDialog<WBondTouchstoneExport.Options?>(owner);

    /// <summary>
    /// R-wbe-5's own instruction: state the cost rather than let a 600-wire export look like a hang.
    /// One complex M×M factorisation per frequency, measured at 55.8 ms for N = 600 wires in WB-B.
    /// </summary>
    private void RefreshCost()
    {
        if (_design is null || PointsBox.Value is not { } points) { CostNote.Text = ""; return; }

        int n = (int)points;
        CostNote.Text =
            $"{n} point(s) × one {_design.Arrays.Count}×{_design.Arrays.Count} complex factorisation " +
            $"over {_design.WireCount} wire(s). A large design can take several seconds.";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Z0Box.Value is not { } z0 || z0 <= 0)      { Fail("State a positive reference impedance."); return; }
        if (StartBox.Value is not { } startGhz)        { Fail("State a start frequency."); return; }
        if (StopBox.Value is not { } stopGhz)          { Fail("State a stop frequency."); return; }
        if (PointsBox.Value is not { } points)         { Fail("State a point count."); return; }

        int n = (int)points;
        if (n < 1) { Fail("A sweep needs at least one point."); return; }
        if (n > 1 && stopGhz <= startGhz)
        {
            Fail("The stop frequency must be above the start frequency.");
            return;
        }

        Close(new WBondTouchstoneExport.Options(
            Z0Ohms:       (double)z0,
            StartHz:      (double)startGhz * 1e9,
            StopHz:       (double)stopGhz * 1e9,
            Points:       n,
            Logarithmic:  LogRadio.IsChecked == true,
            Digits:       9,
            DigitFormat:  'g',
            MatrixFormat: SelectedFormat()));
    }

    private MatrixFormat SelectedFormat() =>
        MaRadio.IsChecked == true ? MatrixFormat.MA
        : DbRadio.IsChecked == true ? MatrixFormat.DB
        : MatrixFormat.RI;

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
