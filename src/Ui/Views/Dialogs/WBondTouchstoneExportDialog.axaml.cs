using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using CircuitRF.WBond.Mom;
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

        TerminalBasisRadio.IsCheckedChanged += (_, _) => RefreshPorts();
        ArrayBasisRadio.IsCheckedChanged    += (_, _) => RefreshPorts();

        ModelBox.SelectionChanged += (_, _) => RefreshPorts();
        SegmentsBox.ValueChanged  += (_, _) => RefreshCost();

        StartBox.ValueChanged  += (_, _) => RefreshCost();
        StopBox.ValueChanged   += (_, _) => RefreshCost();
        PointsBox.ValueChanged += (_, _) => RefreshCost();

        RefreshPorts();
        RefreshCost();
    }

    /// <summary>
    /// Shows the dialog and returns the chosen options, or null when cancelled.
    /// </summary>
    public static Task<WBondTouchstoneExport.Options?> ShowAsync(Window owner, WBondDesign design) =>
        new WBondTouchstoneExportDialog(design)
            .ShowDialog<WBondTouchstoneExport.Options?>(owner);

    /// <summary>The basis the radio buttons currently select.</summary>
    private WBondPortBasis SelectedBasis =>
        ArrayBasisRadio.IsChecked == true ? WBondPortBasis.ArrayPairs : WBondPortBasis.Terminals;

    /// <summary>The model the combo currently selects.</summary>
    private WBondNetworkModel SelectedModel =>
        ModelBox.SelectedIndex == 1 ? WBondNetworkModel.Distributed : WBondNetworkModel.Lumped;

    /// <summary>
    /// Rebuilds the port map, and says what the chosen basis will LEAVE OUT when that is not nothing.
    ///
    /// <para>Shown before the file is written rather than left to be recovered from its comments — an
    /// exported file outlives the session, so a user must be able to see that an array-pair export of
    /// a design WITH capacitance is not the network their schematic simulates.</para>
    /// </summary>
    private void RefreshPorts()
    {
        if (_design is null) { BasisNote.Text = ""; return; }

        var names = WBondTouchstoneExport.PortNames(_design, SelectedBasis);
        PortList.ItemsSource = names.Select((n, i) => $"Port {i + 1}  →  {n}").ToArray();

        bool capacitance = _design.IncludeCapacitance && _design.GroundPlane.Enabled;
        bool loses = capacitance && SelectedBasis == WBondPortBasis.ArrayPairs;

        BasisNote.Text = loses
            ? "This design includes capacitance. A differential pair has no terminal for a shunt to "
              + "the ground plane to leave by, so this basis will export the series arm only — the "
              + "file will not be the network the schematic simulates."
            : "";
        BasisNote.IsVisible = loses;

        // Distributed + array-pairs is refused, not silently corrected. The user chose two things that
        // cannot both be honoured, and which one they meant is theirs to say.
        bool distributed = SelectedModel == WBondNetworkModel.Distributed;
        bool refused = distributed && SelectedBasis == WBondPortBasis.ArrayPairs;

        SegmentsBox.IsEnabled = distributed;
        SegmentsLabel.IsEnabled = distributed;

        ModelNote.Text = refused
            ? "The distributed (MoM) model publishes on the terminal basis only — an array-pair port is "
              + "a floating pair, and this model's shunt capacitance has no terminal to return through. "
              + "Use the terminal basis, or the lumped model if you want an array-pair file."
            : "";
        ModelNote.IsVisible = refused;
        ExportButton.IsEnabled = !refused;

        RefreshCost();
    }

    /// <summary>
    /// R-wbe-5's own instruction: state the cost rather than let a 600-wire export look like a hang.
    /// One complex M×M factorisation per frequency, measured at 55.8 ms for N = 600 wires in WB-B.
    /// </summary>
    private void RefreshCost()
    {
        if (_design is null || PointsBox.Value is not { } points) { CostNote.Text = ""; return; }

        int n = (int)points;
        int ports = WBondTouchstoneExport.PortNames(_design, SelectedBasis).Count;

        if (SelectedModel == WBondNetworkModel.Distributed)
        {
            int segments = SegmentsBox.Value is { } s ? (int)s : 24;
            var settings = WireMomSettings.Default with { TargetSegmentsPerWire = segments };

            try
            {
                // THE PREDICTION, NOT AN ADJECTIVE. This note used to say a distributed export "takes
                // seconds, not milliseconds", which is true of a 40-wire array and off by two orders for
                // a 200-wire one. WireMomCost's constants are measured, so the number can be quoted —
                // and the slow-run warning names a cheaper segmentation when the answer is minutes.
                var report = WireMomMesh.Predict(_design, settings);
                string text =
                    $"{n} point(s) × one dense complex factorisation over " +
                    $"{report.Segments.ToString("N0", CultureInfo.InvariantCulture)} current unknowns " +
                    $"({_design.WireCount} wire(s) × {segments} segments), written as a {ports}-port. " +
                    report.CostSummary(n);

                if (WireMomMesh.SlowRunWarning(_design, n, settings) is { } slow) text += "\n⚠ " + slow;

                CostNote.Text = text;
            }
            catch (Exception)
            {
                // A refusal (no ground plane, above the segment ceiling) is already shown by ModelNote,
                // which is where it belongs — this line stays silent rather than repeating it.
                CostNote.Text =
                    $"{n} point(s) × one dense complex factorisation per point, written as a {ports}-port.";
            }

            return;
        }

        CostNote.Text =
            $"{n} point(s) × one complex factorisation over {_design.WireCount} wire(s), written as a " +
            $"{ports}-port. A large design can take several seconds.";
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
            MatrixFormat: SelectedFormat(),
            PortBasis:    SelectedBasis,
            Model:        SelectedModel,
            SegmentsPerWire: SegmentsBox.Value is { } segments ? (int)segments : 24));
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
